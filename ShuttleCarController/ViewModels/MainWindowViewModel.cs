using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Prism.Commands;
using Prism.Mvvm;
using ShuttleCarController.Models;
using ShuttleCarController.Services;

namespace ShuttleCarController.ViewModels;

/// <summary>
/// 主窗口的唯一 ViewModel，负责连接参数、原始 Modbus 调试、业务命令、状态展示和日志绑定。
/// </summary>
/// <remarks>
/// 该类不直接创建 NModbus 对象。所有车辆 I/O 都通过可选的 IShuttleCarClient 完成；
/// 未注入通信实现时，界面仍可启动，但所有硬件命令均被禁用。
/// 后台客户端事件先合并并切换到 UI 线程，避免窗口缩放时 Dispatcher 队列持续堆积。
/// </remarks>
public sealed class MainWindowViewModel : BindableBase, IDisposable
{
    /// <summary>
    /// 界面最多保留的日志条数。
    /// 超出后删除最旧条目，防止长时间运行导致 ObservableCollection 和可视树无限增长。
    /// </summary>
    private const int MaxDisplayLogEntries = 1000;

    // 应用级服务：配置只负责持久化，日志统一落盘并通知界面，通信实现负责全部车辆 I/O。
    private readonly AppConfigStore _configStore;
    private readonly ILogService _logService;
    private readonly IShuttleCarClient? _shuttleCarClient;

    // 每秒检查一次数据年龄；只有显示值或新鲜度变化时才触发绑定更新。
    private readonly DispatcherTimer _freshnessTimer;

    // 连接与界面状态。
    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private string _connectionMessage = "尚未连接";
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    // 最近一次已应用到界面的完整车辆快照。
    private ShuttleCarSnapshot _snapshot = ShuttleCarSnapshot.Empty;

    // 原始 Modbus 调试区输入和输出。
    private string _rawResultText = "尚未执行读写操作。";
    private int _rawQuantity = 10;
    private string _rawStartAddressText = "0";
    private string _rawWriteValuesText = "0";
    private FunctionCodeOption _selectedReadFunctionCode;
    private FunctionCodeOption _selectedWriteFunctionCode;

    // 业务命令参数；任务号由 ViewModel 生成，活动任务号用于完成握手和防止重复下发。
    private int _taskType;
    private int _outboundMode;
    private int _bodyDirection;
    private int _entryBack;
    private int _taskNumberInput;
    private int? _activeTaskNumber;
    private string _commandPhase;
    private bool _disposed;

    // 后台快照合并：pending 只保留最新数据，scheduled 防止重复向 Dispatcher 投递刷新操作。
    private ShuttleCarSnapshot? _pendingSnapshot;
    private int _snapshotDispatchScheduled;

    // 定时器刷新去重缓存，避免每秒钟重复发起相同的属性通知。
    private string _lastDataAgeText = string.Empty;
    private bool _lastDataIsFresh;

    /// <summary>
    /// 初始化主窗口状态、命令、事件订阅和配置。
    /// </summary>
    /// <param name="configStore">读取和保存本地连接配置。</param>
    /// <param name="logService">接收系统、通信、命令和报警日志。</param>
    /// <param name="shuttleCarClient">
    /// 可选通信实现。DryIoc 支持可选参数：未注册实现时传入 null，
    /// 便于先运行界面；注册实现后由容器自动注入。
    /// </param>
    public MainWindowViewModel(
        AppConfigStore configStore,
        ILogService logService,
        IShuttleCarClient? shuttleCarClient = null)
    {
        _configStore = configStore;
        _logService = logService;
        _shuttleCarClient = shuttleCarClient;

        // 配置损坏时 Store 会返回默认值和警告，不让配置问题阻止窗口启动。
        var configResult = configStore.Load();
        Settings = configResult.Config.Connection;

        // 原始调试区只开放方案中约定的功能码；地址零基/一基换算由通信实现负责。
        ReadFunctionCodes =
        [
            new FunctionCodeOption(3, "03 保持寄存器"),
            new FunctionCodeOption(4, "04 输入寄存器")
        ];
        WriteFunctionCodes =
        [
            new FunctionCodeOption(6, "06 单寄存器"),
            new FunctionCodeOption(16, "16 多寄存器")
        ];
        _selectedReadFunctionCode = ReadFunctionCodes[0];
        _selectedWriteFunctionCode = WriteFunctionCodes[0];
        _taskNumberInput = GenerateTaskNumber();
        _commandPhase = _shuttleCarClient is null ? "通信未接入" : "空闲";

        // DelegateCommand 的 async lambda 在 WPF 中以事件入口方式执行；
        // 所有异步方法内部统一捕获异常，避免 async void 将异常直接抛到 Dispatcher。
        ConnectCommand = new DelegateCommand(async () => await ConnectAsync(), CanConnect);
        DisconnectCommand = new DelegateCommand(async () => await DisconnectAsync(), CanDisconnect);
        ReadRawCommand = new DelegateCommand(async () => await ReadRawAsync(), CanUseConnectedClient);
        WriteRawCommand = new DelegateCommand(async () => await WriteRawAsync(), CanUseConnectedClient);
        StartTaskCommand = new DelegateCommand(async () => await StartTaskAsync(), CanStartTask);
        InventoryResetCommand = new DelegateCommand(
            async () => await ExecuteMaintenanceAsync(ShuttleCarCommandType.InventoryReset, "盘点清零"),
            CanExecuteMaintenanceCommand);
        InitializeCommand = new DelegateCommand(
            async () => await ExecuteMaintenanceAsync(ShuttleCarCommandType.Initialize, "初始化"),
            CanExecuteMaintenanceCommand);
        ConfirmCompleteCommand = new DelegateCommand(async () => await ConfirmCompleteAsync(), CanConfirmComplete);
        ClearFaultCommand = new DelegateCommand(async () => await ClearFaultAsync(), CanClearFault);
        ClearLogViewCommand = new DelegateCommand(ClearLogView);

        // 先订阅日志再记录初始化信息，确保“通信未接入”等启动提示能出现在界面。
        _logService.EntryWritten += OnLogEntryWritten;

        if (_shuttleCarClient is not null)
        {
            _shuttleCarClient.StateChanged += OnClientStateChanged;
            _shuttleCarClient.SnapshotUpdated += OnSnapshotUpdated;
            _logService.Info(
                LogCategory.System,
                "Modbus TCP 通信已注册。",
                "轮询与业务命令按 ProtocolMap 联调映射读写；与实车对接前需按最终协议替换映射。");
        }
        else
        {
            _logService.Warning(
                LogCategory.System,
                "未注册 IShuttleCarClient，界面处于只读演示状态。",
                "实现并注册 IShuttleCarClient 后即可接入穿梭车。");
        }

        if (configResult.Warning is not null)
        {
            _logService.Warning(LogCategory.System, "配置文件加载异常。", configResult.Warning);
        }
        // 使用 Background 优先级，避免数据年龄刷新抢占用户输入和窗口缩放。
        _freshnessTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _freshnessTimer.Tick += OnFreshnessTimerTick;
        _freshnessTimer.Start();

        StatusMessage = _shuttleCarClient is null
            ? "未注册通信实现；硬件读写命令不可用。"
            : "连接后可读取车辆状态。";
    }

    /// <summary>界面正在编辑的连接配置；发送给客户端前会 Clone，避免后台读到修改中的对象。</summary>
    public ConnectionSettings Settings { get; }

    /// <summary>原始读取允许的功能码：03 保持寄存器和 04 输入寄存器。</summary>
    public IReadOnlyList<FunctionCodeOption> ReadFunctionCodes { get; }

    /// <summary>原始写入允许的功能码：06 单寄存器和 16 多寄存器。</summary>
    public IReadOnlyList<FunctionCodeOption> WriteFunctionCodes { get; }

    /// <summary>界面日志集合，最多保留 MaxDisplayLogEntries 条，按时间从旧到新排列。</summary>
    public ObservableCollection<LogEntry> Logs { get; } = [];

    /// <summary>建立 Modbus 连接并启动车辆状态轮询。</summary>
    public DelegateCommand ConnectCommand { get; }

    /// <summary>停止轮询并断开当前连接。</summary>
    public DelegateCommand DisconnectCommand { get; }

    /// <summary>按界面输入执行一次原始寄存器读取。</summary>
    public DelegateCommand ReadRawCommand { get; }

    /// <summary>按界面输入执行一次原始寄存器写入，执行前需要二次确认。</summary>
    public DelegateCommand WriteRawCommand { get; }

    /// <summary>校验任务参数并下发新任务。</summary>
    public DelegateCommand StartTaskCommand { get; }

    /// <summary>请求盘点清零，仅在数据有效且没有活动任务时开放。</summary>
    public DelegateCommand InventoryResetCommand { get; }

    /// <summary>请求车辆初始化，属于需要二次确认的高风险维护命令。</summary>
    public DelegateCommand InitializeCommand { get; }

    /// <summary>在车辆报告当前任务完成后发送完成确认。</summary>
    public DelegateCommand ConfirmCompleteCommand { get; }

    /// <summary>请求清除可恢复故障，不替代现场安全处理。</summary>
    public DelegateCommand ClearFaultCommand { get; }

    /// <summary>只清空界面日志视图，不删除已经写入磁盘的日志文件。</summary>
    public DelegateCommand ClearLogViewCommand { get; }

    /// <summary>是否已注入实际通信实现；为 false 时连接和所有硬件命令均不可用。</summary>
    public bool IsClientAvailable => _shuttleCarClient is not null;

    /// <summary>在连接区明确显示当前是否只是界面演示状态，避免用户误以为已连接实车。</summary>
    public string ClientAvailabilityText => IsClientAvailable
        ? "通信接口已接入"
        : "通信实现未接入（IShuttleCarClient）";

    /// <summary>
    /// 本机 Modbus 链路状态。
    /// 状态变化时同步刷新中文状态、连接参数可编辑性和全部命令可用性。
    /// </summary>
    public ConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (!Set(ref _connectionState, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(ConnectionStateText));
            RaisePropertyChanged(nameof(IsConnected));
            RaisePropertyChanged(nameof(IsConnectionSettingsEditable));
            RefreshCommandStates();
        }
    }

    /// <summary>连接状态对应的中文界面文案。</summary>
    public string ConnectionStateText => ConnectionState switch
    {
        ConnectionState.Connecting => "连接中",
        ConnectionState.Connected => "已连接",
        ConnectionState.Reconnecting => "重连中",
        ConnectionState.Faulted => "通信异常",
        _ => "未连接"
    };

    /// <summary>连接状态补充说明，例如正在连接、断开原因或重连错误。</summary>
    public string ConnectionMessage
    {
        get => _connectionMessage;
        private set => Set(ref _connectionMessage, value);
    }

    /// <summary>左侧状态区显示的当前操作结果；短消息，不替代详细日志。</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    /// <summary>
    /// 是否有异步操作正在执行。
    /// 为 true 时连接参数和命令按钮会被禁用，防止重复连接、重复写入或重复下发任务。
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(IsConnectionSettingsEditable));
            RefreshCommandStates();
        }
    }

    /// <summary>本机 Modbus 链路是否处于已连接状态。</summary>
    public bool IsConnected => ConnectionState == ConnectionState.Connected;

    /// <summary>
    /// 连接参数是否允许编辑。
    /// 连接中、已连接和命令执行期间锁定，避免界面配置与后台实际连接参数不一致。
    /// </summary>
    public bool IsConnectionSettingsEditable =>
        !IsBusy && ConnectionState is ConnectionState.Disconnected or ConnectionState.Faulted;

    /// <summary>
    /// 最近一次应用到界面的完整车辆快照。
    /// 所有车辆数据通过替换不可变快照更新，避免多个字段之间的状态撕裂。
    /// </summary>
    public ShuttleCarSnapshot Snapshot
    {
        get => _snapshot;
        private set
        {
            if (!Set(ref _snapshot, value))
            {
                return;
            }

            // 同步定时器去重缓存，防止下一秒重复发送相同的属性变更。
            _lastDataIsFresh = DataIsFresh;
            _lastDataAgeText = DataAgeText;
            RaisePropertyChanged(nameof(DataIsFresh));
            RaisePropertyChanged(nameof(DataAgeText));
            RefreshCommandStates();
        }
    }

    /// <summary>
    /// 车辆数据是否可用于业务控制。
    /// 必须同时满足：数据质量为 Good、已有更新时间，且未超过两个轮询周期。
    /// 该检查是软件联锁条件，不代表车辆硬件进入安全状态。
    /// </summary>
    public bool DataIsFresh
    {
        get
        {
            if (Snapshot.Quality != DataQuality.Good || Snapshot.UpdatedAt == default)
            {
                return false;
            }

            var staleAfter = TimeSpan.FromMilliseconds(Math.Max(1000, Settings.PollIntervalMs * 2));
            return DateTimeOffset.Now - Snapshot.UpdatedAt <= staleAfter;
        }
    }

    /// <summary>当前快照距离现在的时间，供操作人员判断数据是否停滞。</summary>
    public string DataAgeText
    {
        get
        {
            if (Snapshot.UpdatedAt == default)
            {
                return "--";
            }

            var seconds = Math.Max(0, (DateTimeOffset.Now - Snapshot.UpdatedAt).TotalSeconds);
            return seconds.ToString("F1", CultureInfo.InvariantCulture) + " s";
        }
    }

    /// <summary>原始读取使用的功能码选项，允许 03/04；下拉框清空选择时回退到第一项，避免空引用崩溃。</summary>
    public FunctionCodeOption SelectedReadFunctionCode
    {
        get => _selectedReadFunctionCode;
        set => Set(ref _selectedReadFunctionCode, value ?? ReadFunctionCodes[0]);
    }

    /// <summary>原始写入使用的功能码选项，允许 06/16；下拉框清空选择时回退到第一项，避免空引用崩溃。</summary>
    public FunctionCodeOption SelectedWriteFunctionCode
    {
        get => _selectedWriteFunctionCode;
        set => Set(ref _selectedWriteFunctionCode, value ?? WriteFunctionCodes[0]);
    }

    /// <summary>原始读写起始地址文本，支持十进制和 0x 前缀十六进制。</summary>
    public string RawStartAddressText
    {
        get => _rawStartAddressText;
        set => Set(ref _rawStartAddressText, value);
    }

    /// <summary>原始读取数量，界面限制为 1～125 个寄存器。</summary>
    public int RawQuantity
    {
        get => _rawQuantity;
        set => Set(ref _rawQuantity, value);
    }

    /// <summary>原始写入值文本，使用空格、逗号或分号分隔多个 ushort。</summary>
    public string RawWriteValuesText
    {
        get => _rawWriteValuesText;
        set => Set(ref _rawWriteValuesText, value);
    }

    /// <summary>最近一次原始读写结果，只读显示，因此 XAML 使用 OneWay 绑定。</summary>
    public string RawResultText
    {
        get => _rawResultText;
        private set => Set(ref _rawResultText, value);
    }

    /// <summary>任务类型原始值；最终枚举含义须由协议确认，界面不硬编码业务语义。</summary>
    public int TaskType
    {
        get => _taskType;
        set => Set(ref _taskType, value);
    }

    /// <summary>出库方式原始值，映射到上位机发送区的 Int@4。</summary>
    public int OutboundMode
    {
        get => _outboundMode;
        set => Set(ref _outboundMode, value);
    }

    /// <summary>车体方向原始值，映射到上位机发送区的 Int@6。</summary>
    public int BodyDirection
    {
        get => _bodyDirection;
        set => Set(ref _bodyDirection, value);
    }

    /// <summary>入库后退参数原始值；未确认前不按 Bool 处理。</summary>
    public int EntryBack
    {
        get => _entryBack;
        set => Set(ref _entryBack, value);
    }

    /// <summary>
    /// 待下发任务号。
    /// 用户可手工修改；小于等于 0 时发送前会自动生成新的非零任务号。
    /// </summary>
    public int TaskNumberInput
    {
        get => _taskNumberInput;
        set => Set(ref _taskNumberInput, value);
    }

    /// <summary>
    /// 已下发但尚未闭环的任务号。
    /// 用于防止重复下发，并确保完成确认对应正确的车辆任务。
    /// </summary>
    public int? ActiveTaskNumber
    {
        get => _activeTaskNumber;
        private set
        {
            if (!Set(ref _activeTaskNumber, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(ActiveTaskNumberText));
            RefreshCommandStates();
        }
    }

    /// <summary>活动任务号的显示文本；没有活动任务时显示 --。</summary>
    public string ActiveTaskNumberText => ActiveTaskNumber?.ToString(CultureInfo.InvariantCulture) ?? "--";

    /// <summary>当前业务命令阶段，例如等待车辆接收、等待完成或结果未知。</summary>
    public string CommandPhase
    {
        get => _commandPhase;
        private set => Set(ref _commandPhase, value);
    }

    /// <summary>只有在已注入通信实现、当前不忙且未连接时允许发起连接。</summary>
    private bool CanConnect()
    {
        return IsClientAvailable
               && !IsBusy
               && ConnectionState is not (ConnectionState.Connecting or ConnectionState.Connected);
    }

    /// <summary>连接中不允许点击断开；已连接、重连中或故障状态允许主动断开。</summary>
    private bool CanDisconnect()
    {
        return IsClientAvailable
               && ConnectionState is not (ConnectionState.Disconnected or ConnectionState.Connecting);
    }

    /// <summary>原始读写共用的基础条件：通信实现存在、链路已连接且没有其他异步操作。</summary>
    private bool CanUseConnectedClient()
    {
        return IsClientAvailable && IsConnected && !IsBusy;
    }

    /// <summary>
    /// 新任务联锁：连接正常、数据新鲜、无报警、无本机活动任务，且车辆自身任务号为 0/空。
    /// 这些是上位机防误操作条件，不能替代 PLC 和硬件安全联锁。
    /// </summary>
    private bool CanStartTask()
    {
        return CanUseConnectedClient()
               && DataIsFresh
               && !Snapshot.HasAlarm
               && ActiveTaskNumber is null
               && Snapshot.TaskNumber is null or 0;
    }

    /// <summary>初始化和盘点清零要求车辆空闲，避免与正在执行的任务并发。</summary>
    private bool CanExecuteMaintenanceCommand()
    {
        return CanUseConnectedClient()
               && DataIsFresh
               && ActiveTaskNumber is null
               && Snapshot.TaskNumber is null or 0;
    }

    /// <summary>
    /// 完成确认联锁：必须存在本机活动任务，车辆报告完成，且车辆任务号与本机一致。
    /// 若设备不返回任务号，则依据完成位和本机记录继续握手。
    /// </summary>
    private bool CanConfirmComplete()
    {
        return CanUseConnectedClient()
               && DataIsFresh
               && ActiveTaskNumber is > 0
               && Snapshot.TaskCompletedFlag
               && (Snapshot.TaskNumber is null or 0 || Snapshot.TaskNumber == ActiveTaskNumber);
    }

    /// <summary>清除异常只在有活动报警时开放；清除命令本身不能绕过现场安全排查。</summary>
    private bool CanClearFault()
    {
        return CanUseConnectedClient() && DataIsFresh && Snapshot.HasAlarm;
    }

    /// <summary>
    /// 校验并保存连接参数，然后调用通信实现建立连接。
    /// 连接失败会进入 Faulted 状态，便于用户修改配置后重试。
    /// </summary>
    private async Task ConnectAsync()
    {
        if (_shuttleCarClient is null)
        {
            StatusMessage = "通信实现未接入，无法连接。";
            return;
        }

        if (!TryValidateSettings(out var validationError))
        {
            StatusMessage = validationError;
            _logService.Warning(LogCategory.Connection, "连接参数校验失败。", validationError);
            return;
        }

        SaveSettings();

        await RunBusyAsync("连接穿梭车", async () =>
        {
            ConnectionState = ConnectionState.Connecting;
            ConnectionMessage = "正在建立连接…";
            _logService.Info(
                LogCategory.Connection,
                "开始连接。",
                $"IP={Settings.IpAddress}, Port={Settings.Port}, UnitId={Settings.UnitId}");

            try
            {
                await _shuttleCarClient.ConnectAsync(Settings.Clone());
                ConnectionState = _shuttleCarClient.State;
            }
            catch
            {
                ConnectionState = ConnectionState.Faulted;
                ConnectionMessage = "连接失败，请检查参数和网络。";
                throw;
            }

            if (ConnectionState == ConnectionState.Connected)
            {
                ConnectionMessage = "连接成功，等待状态数据。";
                StatusMessage = "已连接，正在等待车辆状态。";
            }
        });
    }

    /// <summary>
    /// 主动断开连接。保留最后一次快照供诊断，但将其标记为过期；
    /// 若存在活动任务，只改变提示阶段，不自动确认完成或重发。
    /// </summary>
    private async Task DisconnectAsync()
    {
        if (_shuttleCarClient is null)
        {
            return;
        }

        await RunBusyAsync("断开连接", async () =>
        {
            await _shuttleCarClient.DisconnectAsync();
            ConnectionState = ConnectionState.Disconnected;
            ConnectionMessage = "已断开";
            StatusMessage = "已断开连接。";
            Snapshot = Snapshot with
            {
                Quality = DataQuality.Stale,
                QualityMessage = "连接已断开"
            };

            if (ActiveTaskNumber is not null)
            {
                CommandPhase = "连接已断开，任务结果需人工核对";
            }

            _logService.Info(LogCategory.Connection, "已断开连接。");
        });
    }

    /// <summary>
    /// 解析原始读取参数并执行一次寄存器读取。
    /// 地址支持十进制和十六进制；数量和地址边界在进入通信层前校验。
    /// </summary>
    private async Task ReadRawAsync()
    {
        if (_shuttleCarClient is null)
        {
            return;
        }

        if (!TryParseUShort(RawStartAddressText, out var startAddress))
        {
            RawResultText = "起始地址格式无效，支持十进制或 0x 开头的十六进制。";
            return;
        }

        if (RawQuantity is < 1 or > 125)
        {
            RawResultText = "读取数量必须在 1～125 之间。";
            return;
        }

        await RunBusyAsync("原始读取", async () =>
        {
            var request = new RawReadRequest(
                Settings.UnitId,
                SelectedReadFunctionCode.Code,
                startAddress,
                (ushort)RawQuantity);
            var result = await _shuttleCarClient.ReadRawAsync(request);

            RawResultText = $"{result.Summary}{Environment.NewLine}{result.ValuesText}";
            _logService.Write(
                result.Success ? LogLevel.Info : LogLevel.Warning,
                LogCategory.Read,
                $"原始读取 FC{SelectedReadFunctionCode.Code} 地址 {startAddress} 数量 {RawQuantity}",
                result.Summary);
        });
    }

    /// <summary>
    /// 校验原始写入值并在二次确认后执行写入。
    /// FC06 只允许一个值，FC16 允许 1～123 个值；超时结果由通信层标记为“未知”。
    /// </summary>
    private async Task WriteRawAsync()
    {
        if (_shuttleCarClient is null)
        {
            return;
        }

        if (!TryParseUShort(RawStartAddressText, out var startAddress))
        {
            RawResultText = "起始地址格式无效，支持十进制或 0x 开头的十六进制。";
            return;
        }

        if (!TryParseWriteValues(RawWriteValuesText, out var values, out var parseError))
        {
            RawResultText = parseError;
            return;
        }

        if (SelectedWriteFunctionCode.Code == 6 && values.Count != 1)
        {
            RawResultText = "功能码 06 只能写入一个寄存器。";
            return;
        }

        if (SelectedWriteFunctionCode.Code == 16 && values.Count is < 1 or > 123)
        {
            RawResultText = "功能码 16 的写入数量必须在 1～123 之间。";
            return;
        }

        if (!Confirm($"确认写入 Modbus？{Environment.NewLine}FC{SelectedWriteFunctionCode.Code}，地址 {startAddress}，值 {string.Join(", ", values.Select(value => $"0x{value:X4}"))}"))
        {
            return;
        }

        await RunBusyAsync("原始写入", async () =>
        {
            var request = new RawWriteRequest(
                Settings.UnitId,
                SelectedWriteFunctionCode.Code,
                startAddress,
                values);
            var result = await _shuttleCarClient.WriteRawAsync(request);

            RawResultText = result.Summary;
            _logService.Write(
                result.ResultUnknown ? LogLevel.Warning : result.Success ? LogLevel.Info : LogLevel.Error,
                LogCategory.Write,
                $"原始写入 FC{SelectedWriteFunctionCode.Code} 地址 {startAddress}",
                result.Summary);
        });
    }

    /// <summary>
    /// 校验任务参数并下发新任务。
    /// 成功或结果未知都会记录活动任务号以阻止重复下发；只有明确失败才允许立即重试。
    /// </summary>
    private async Task StartTaskAsync()
    {
        if (_shuttleCarClient is null)
        {
            return;
        }

        // 所有 Int 参数先做 Int16 范围校验，避免界面数值溢出后在通信层被截断。
        if (!TryCreateShort(TaskType, "任务类型", out var taskType, out var error)
            || !TryCreateShort(OutboundMode, "出库方式", out var outboundMode, out error)
            || !TryCreateShort(BodyDirection, "车体方向", out var bodyDirection, out error)
            || !TryCreateShort(EntryBack, "入库后退", out var entryBack, out error))
        {
            StatusMessage = error;
            return;
        }

        var taskNumber = TaskNumberInput;
        if (taskNumber <= 0)
        {
            taskNumber = GenerateTaskNumber();
            TaskNumberInput = taskNumber;
        }

        if (!Confirm($"确认下发任务？{Environment.NewLine}任务号：{taskNumber}{Environment.NewLine}任务类型：{taskType}{Environment.NewLine}出库方式：{outboundMode}{Environment.NewLine}车体方向：{bodyDirection}"))
        {
            return;
        }

        await RunBusyAsync("下发任务", async () =>
        {
            var command = new ShuttleCarCommand
            {
                Type = ShuttleCarCommandType.StartTask,
                TaskNumber = taskNumber,
                TaskType = taskType,
                OutboundMode = outboundMode,
                BodyDirection = bodyDirection,
                EntryBack = entryBack
            };
            var result = await _shuttleCarClient.ExecuteCommandAsync(command);

            ApplyCommandResult(result, $"下发任务 {taskNumber}");

            // 已接收和结果未知都视为“存在活动任务”，防止用户连续点击生成重复任务。
            if (result.IsAccepted)
            {
                ActiveTaskNumber = taskNumber;
                CommandPhase = result.Status == CommandStatus.Completed
                    ? "车辆已完成任务，等待确认完成"
                    : "车辆已接收任务，等待完成";
            }
            else if (result.IsUnknown)
            {
                ActiveTaskNumber = taskNumber;
                CommandPhase = "结果未知，禁止重复下发，请核对车辆状态";
            }
        });
    }

    /// <summary>
    /// 向车辆发送任务完成确认。
    /// 成功后清除活动任务并生成下一任务号；结果未知时保留当前阶段等待人工核对。
    /// </summary>
    private async Task ConfirmCompleteAsync()
    {
        if (_shuttleCarClient is null || ActiveTaskNumber is null)
        {
            return;
        }

        if (!Confirm($"确认向车辆提交“任务完成确认”？{Environment.NewLine}任务号：{ActiveTaskNumber}"))
        {
            return;
        }

        await RunBusyAsync("确认完成", async () =>
        {
            var command = new ShuttleCarCommand
            {
                Type = ShuttleCarCommandType.ConfirmComplete,
                TaskNumber = ActiveTaskNumber
            };
            var result = await _shuttleCarClient.ExecuteCommandAsync(command);
            ApplyCommandResult(result, $"确认完成 {ActiveTaskNumber}");

            if (result.IsAccepted)
            {
                ActiveTaskNumber = null;
                TaskNumberInput = GenerateTaskNumber();
                CommandPhase = "完成确认已发送，下一次任务号已更新";
            }
            else if (result.IsUnknown)
            {
                CommandPhase = "完成确认结果未知，请核对车辆状态";
            }
        });
    }

    /// <summary>
    /// 在用户确认后请求清除异常。
    /// 软件只发送复位命令，不修改本地报警状态，后续状态以车辆新快照为准。
    /// </summary>
    private async Task ClearFaultAsync()
    {
        if (_shuttleCarClient is null)
        {
            return;
        }

        if (!Confirm("确认发送“清除异常”命令？该操作不能替代现场故障排查。"))
        {
            return;
        }

        await RunBusyAsync("清除异常", async () =>
        {
            var result = await _shuttleCarClient.ExecuteCommandAsync(new ShuttleCarCommand
            {
                Type = ShuttleCarCommandType.ClearFault
            });
            ApplyCommandResult(result, "清除异常");

            if (result.IsAccepted)
            {
                CommandPhase = "清除异常已发送";
            }
        });
    }

    /// <summary>
    /// 执行初始化和盘点清零等维护命令的共用流程。
    /// 两条命令都必须二次确认，并复用统一的忙碌锁、结果处理和日志记录。
    /// </summary>
    private async Task ExecuteMaintenanceAsync(ShuttleCarCommandType commandType, string displayName)
    {
        if (_shuttleCarClient is null)
        {
            return;
        }

        if (!Confirm($"确认执行“{displayName}”？{Environment.NewLine}该命令可能改变车辆状态，请确认现场安全。"))
        {
            return;
        }

        await RunBusyAsync(displayName, async () =>
        {
            var result = await _shuttleCarClient.ExecuteCommandAsync(new ShuttleCarCommand
            {
                Type = commandType
            });
            ApplyCommandResult(result, displayName);

            if (result.IsAccepted)
            {
                CommandPhase = $"{displayName}已发送";
            }
        });
    }

    /// <summary>将命令结果同时写入状态栏和命令日志；未知结果使用警告级别。</summary>
    private void ApplyCommandResult(CommandResult result, string operation)
    {
        StatusMessage = $"{operation}：{result.Message}";
        _logService.Write(
            result.IsAccepted ? LogLevel.Info : result.IsUnknown ? LogLevel.Warning : LogLevel.Error,
            LogCategory.Command,
            $"{operation}结果：{StatusText(result.Status)}",
            result.Message);
    }

    /// <summary>将命令状态转换为界面中文，特别区分“超时/未知”和普通失败。</summary>
    private static string StatusText(CommandStatus status)
    {
        return status switch
        {
            CommandStatus.Accepted => "已接收",
            CommandStatus.Completed => "已完成",
            CommandStatus.Rejected => "被拒绝",
            CommandStatus.NotSupported => "不支持",
            CommandStatus.TimedOut => "超时，结果未知",
            CommandStatus.CommunicationError => "通信错误",
            CommandStatus.ResultUnknown => "结果未知",
            _ => status.ToString()
        };
    }

    /// <summary>
    /// 统一的异步命令保护：设置忙碌状态、防止重入、捕获并记录异常，最后恢复命令可用性。
    /// 这是 DelegateCommand 的 async void 入口不把异常抛回 Dispatcher 的关键屏障。
    /// </summary>
    private async Task RunBusyAsync(string operation, Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"{operation}…";
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusMessage = $"{operation}失败：{exception.Message}";
            _logService.Error(LogCategory.System, $"{operation}失败。", exception.ToString());
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 在发起连接前校验 IPv4、端口、超时、轮询和重连参数。
    /// 范围属于软件保护值，不是车辆协议规定的通信参数。
    /// </summary>
    private bool TryValidateSettings(out string error)
    {
        if (!IPAddress.TryParse(Settings.IpAddress, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            error = "请输入有效的 IPv4 地址。";
            return false;
        }

        if (Settings.Port is < 1 or > 65535)
        {
            error = "端口必须在 1～65535 之间。";
            return false;
        }

        if (Settings.RequestTimeoutMs is < 100 or > 60000)
        {
            error = "请求超时必须在 100～60000 ms 之间。";
            return false;
        }

        if (Settings.PollIntervalMs is < 100 or > 60000)
        {
            error = "轮询周期必须在 100～60000 ms 之间。";
            return false;
        }

        if (Settings.ReconnectIntervalMs is < 500 or > 60000)
        {
            error = "重连间隔必须在 500～60000 ms 之间。";
            return false;
        }

        if (Settings.PollQuantity is < 1 or > 125)
        {
            error = "轮询数量必须在 1～125 之间。";
            return false;
        }

        if (Settings.PollStartAddress is < 0 or > 65535
            || Settings.PollStartAddress + Settings.PollQuantity > 65536)
        {
            error = "轮询起始地址必须在 0～65535 之间，且轮询范围不能越过地址 65535。";
            return false;
        }

        error = string.Empty;
        return true;
    }

    /// <summary>保存当前连接配置；配置写入失败只记警告，不阻止本次连接尝试。</summary>
    private void SaveSettings()
    {
        try
        {
            _configStore.Save(new AppConfig
            {
                Connection = Settings.Clone()
            });
        }
        catch (Exception exception)
        {
            _logService.Warning(LogCategory.System, "保存配置失败。", exception.Message);
        }
    }

    /// <summary>
    /// 处理通信客户端的连接状态事件。
    /// 事件可能来自后台线程，因此统一切换到 UI 线程；断开/故障时把数据标记为过期并保留人工核对提示。
    /// </summary>
    private void OnClientStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        OnUiThread(() =>
        {
            ConnectionState = args.CurrentState;
            if (!string.IsNullOrWhiteSpace(args.Message))
            {
                ConnectionMessage = args.Message;
            }

            if (args.CurrentState is ConnectionState.Disconnected or ConnectionState.Faulted)
            {
                Snapshot = Snapshot with
                {
                    Quality = DataQuality.Stale,
                    QualityMessage = args.Message ?? "通信中断"
                };

                if (ActiveTaskNumber is not null)
                {
                    CommandPhase = "通信中断，任务结果需人工核对";
                }
            }

            _logService.Write(
                args.CurrentState == ConnectionState.Faulted ? LogLevel.Error : LogLevel.Info,
                LogCategory.Connection,
                $"连接状态：{args.CurrentState}",
                args.Message);
        });
    }

    /// <summary>
    /// 接收后台快照并合并 UI 投递。
    /// 连续到达的多个快照只保留最后一个，避免窗口缩放或 UI 繁忙时 Dispatcher 队列不断增长。
    /// </summary>
    private void OnSnapshotUpdated(object? sender, ShuttleCarSnapshot snapshot)
    {
        // 窗口缩放或 UI 繁忙时只保留最新快照，避免 Dispatcher 队列和视觉树重建持续堆积。
        Interlocked.Exchange(ref _pendingSnapshot, snapshot);
        if (Interlocked.CompareExchange(ref _snapshotDispatchScheduled, 1, 0) != 0)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            FlushPendingSnapshot();
            return;
        }

        _ = dispatcher.InvokeAsync(FlushPendingSnapshot, DispatcherPriority.Background);
    }

    /// <summary>在 UI 线程取出最新待处理快照；清空调度标志后可接收下一批事件。</summary>
    private void FlushPendingSnapshot()
    {
        Interlocked.Exchange(ref _snapshotDispatchScheduled, 0);
        var snapshot = Interlocked.Exchange(ref _pendingSnapshot, null);
        if (snapshot is not null)
        {
            ApplySnapshot(snapshot);
        }
    }

    /// <summary>
    /// 将最新快照应用到界面，并在报警出现/解除或任务完成时记录状态变化。
    /// 报警总码非零始终按存在报警处理，不因缺少故障明细而忽略。
    /// </summary>
    private void ApplySnapshot(ShuttleCarSnapshot snapshot)
    {
        var hadAlarm = Snapshot.HasAlarm;
        var hadData = Snapshot.Quality == DataQuality.Good;
        Snapshot = snapshot;

        if (!hadData && snapshot.Quality == DataQuality.Good)
        {
            StatusMessage = "已收到车辆状态。";
        }

        if (snapshot.HasAlarm && !hadAlarm)
        {
            _logService.Warning(LogCategory.Alarm, "车辆报警状态变为存在故障。", snapshot.AlarmText);
        }
        else if (!snapshot.HasAlarm && hadAlarm)
        {
            _logService.Info(LogCategory.Alarm, "车辆报警已解除。");
        }

        if (ActiveTaskNumber is > 0
            && snapshot.TaskCompletedFlag
            && (snapshot.TaskNumber is null or 0 || snapshot.TaskNumber == ActiveTaskNumber))
        {
            CommandPhase = "车辆报告任务完成，等待确认完成";
        }
    }

    /// <summary>
    /// 将新日志追加到界面集合，并裁剪超过 1000 条的旧记录。
    /// 日志事件可能来自任意线程，因此统一通过 UI 调度器执行。
    /// </summary>
    private void OnLogEntryWritten(object? sender, LogEntry entry)
    {
        OnUiThread(() =>
        {
            Logs.Add(entry);
            while (Logs.Count > MaxDisplayLogEntries)
            {
                Logs.RemoveAt(0);
            }
        });
    }

    /// <summary>
    /// 每秒更新数据年龄和新鲜度。
    /// 只有状态或显示文本实际变化时才触发通知，减少布局和命令重查开销。
    /// </summary>
    private void OnFreshnessTimerTick(object? sender, EventArgs args)
    {
        var isFresh = DataIsFresh;
        if (isFresh != _lastDataIsFresh)
        {
            _lastDataIsFresh = isFresh;
            RaisePropertyChanged(nameof(DataIsFresh));
            RefreshCommandStates();
        }

        var ageText = DataAgeText;
        if (!string.Equals(ageText, _lastDataAgeText, StringComparison.Ordinal))
        {
            _lastDataAgeText = ageText;
            RaisePropertyChanged(nameof(DataAgeText));
        }
    }

    /// <summary>仅清空界面日志集合，磁盘日志和日志文件保留策略不受影响。</summary>
    private void ClearLogView()
    {
        Logs.Clear();
    }

    /// <summary>
    /// 通知所有 DelegateCommand 重新计算 CanExecute。
    /// 连接、忙碌、快照或新鲜度变化后调用，确保界面按钮与联锁条件同步。
    /// </summary>
    private void RefreshCommandStates()
    {
        ConnectCommand?.RaiseCanExecuteChanged();
        DisconnectCommand?.RaiseCanExecuteChanged();
        ReadRawCommand?.RaiseCanExecuteChanged();
        WriteRawCommand?.RaiseCanExecuteChanged();
        StartTaskCommand?.RaiseCanExecuteChanged();
        InventoryResetCommand?.RaiseCanExecuteChanged();
        InitializeCommand?.RaiseCanExecuteChanged();
        ConfirmCompleteCommand?.RaiseCanExecuteChanged();
        ClearFaultCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 显示高风险操作确认框。
    /// 默认按钮为“否”，关闭对话框或误按回车都不会执行写入或车辆动作。
    /// </summary>
    private static bool Confirm(string message)
    {
        var owner = Application.Current?.MainWindow;
        if (owner is null)
        {
            // 窗口尚未创建或已关闭时，MessageBox 不接受 null owner，改用无 owner 重载。
            return MessageBox.Show(
                       message,
                       "操作确认",
                       MessageBoxButton.YesNo,
                       MessageBoxImage.Warning,
                       MessageBoxResult.No) == MessageBoxResult.Yes;
        }

        return MessageBox.Show(
                   owner,
                   message,
                   "操作确认",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    /// <summary>
    /// 使用密码学随机数生成非零 Int32 任务号，降低应用重启后重复任务号的概率。
    /// 任务号是否允许重复最终仍须服从 PLC 握手规则。
    /// </summary>
    private static int GenerateTaskNumber()
    {
        return RandomNumberGenerator.GetInt32(1, int.MaxValue);
    }

    /// <summary>
    /// 将界面整数安全转换为协议 Int16 范围；超出范围时不发送任何数据。
    /// </summary>
    private static bool TryCreateShort(int value, string fieldName, out short result, out string error)
    {
        if (value is < short.MinValue or > short.MaxValue)
        {
            result = 0;
            error = $"{fieldName}必须在 {short.MinValue}～{short.MaxValue} 之间。";
            return false;
        }

        result = (short)value;
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// 解析十进制或 0x 前缀十六进制的无符号 16 位寄存器值。
    /// 解析失败时不修改通信状态，由调用方显示输入错误。
    /// </summary>
    private static bool TryParseUShort(string? text, out ushort value)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(
                trimmed[2..],
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value);
        }

        return ushort.TryParse(
            trimmed,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    /// <summary>
    /// 将空白、逗号或分号分隔的文本解析为寄存器数组。
    /// 任一值非法就整体拒绝，避免只写入部分寄存器造成设备状态不一致。
    /// </summary>
    private static bool TryParseWriteValues(
        string? text,
        out IReadOnlyList<ushort> values,
        out string error)
    {
        var result = new List<ushort>();
        var parts = (text ?? string.Empty).Split(
            [',', ';', ' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (!TryParseUShort(part, out var value))
            {
                values = Array.Empty<ushort>();
                error = $"写入值“{part}”格式无效。";
                return false;
            }

            result.Add(value);
        }

        values = result;
        error = result.Count == 0 ? "请至少输入一个写入值。" : string.Empty;
        return result.Count > 0;
    }

    /// <summary>
    /// 将回调切换到 WPF UI 线程。
    /// 没有 Application 或已在 UI 线程时直接执行，便于单元测试和非 WPF 宿主复用。
    /// </summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// 通用属性赋值器：值未变化时返回 false，避免无效的 WPF 绑定和布局刷新。
    /// </summary>
    private bool Set<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    /// <summary>
    /// 幂等释放界面侧资源：停止定时器并解除日志和通信事件订阅。
    /// 不在此处阻塞等待网络断开；连接生命周期由 IShuttleCarClient 实现管理。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _freshnessTimer.Stop();
        _freshnessTimer.Tick -= OnFreshnessTimerTick;
        _logService.EntryWritten -= OnLogEntryWritten;

        if (_shuttleCarClient is not null)
        {
            _shuttleCarClient.StateChanged -= OnClientStateChanged;
            _shuttleCarClient.SnapshotUpdated -= OnSnapshotUpdated;
        }
    }
}
