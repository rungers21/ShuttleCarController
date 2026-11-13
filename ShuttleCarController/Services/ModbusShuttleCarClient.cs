using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NModbus;
using ShuttleCarController.Models;

namespace ShuttleCarController.Services;

/// <summary>
/// 基于 NModbus 的 Modbus TCP 通信实现。
/// 负责连接、状态轮询、读写串行化、超时和断线重连；不做协议地址猜测。
/// </summary>
/// <remarks>
/// 所有 NModbus 调用都通过同步 API 在后台线程执行，并由同一个 <see cref="SemaphoreSlim"/> 串行化，
/// 避免 NModbus 异步实现的内部调度问题和并发占用同一连接。
/// 轮询块和业务命令都按 <see cref="ProtocolMap"/> 的联调映射解析；该映射不是已确认的现场协议。
/// </remarks>
public sealed class ModbusShuttleCarClient : IShuttleCarClient
{
    /// <summary>Modbus 协议规定的单次读取寄存器上限。</summary>
    private const int MaxReadQuantity = 125;

    /// <summary>FC16 单次写入寄存器上限。</summary>
    private const int MaxWriteQuantity = 123;

    private readonly ILogService _logService;
    private readonly ModbusFactory _factory = new();
    private readonly SemaphoreSlim _ioLock = new(1, 1);

    // 当前连接使用的配置快照；轮询和原始读写都只读该副本。
    private volatile ConnectionSettings _settings = new();

    private TcpClient? _tcpClient;
    private IModbusMaster? _master;
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;
    private volatile ConnectionState _state = ConnectionState.Disconnected;
    private bool _disposed;

    // 最近一次成功快照：读取失败时保留数值，只降低数据质量。
    private ShuttleCarSnapshot _lastSnapshot = ShuttleCarSnapshot.Empty;

    /// <summary>创建 Modbus TCP 客户端，日志用于记录连接、重连和轮询异常。</summary>
    public ModbusShuttleCarClient(ILogService logService)
    {
        _logService = logService;
    }

    /// <inheritdoc />
    public ConnectionState State => _state;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public event EventHandler<ShuttleCarSnapshot>? SnapshotUpdated;

    /// <inheritdoc />
    public async Task ConnectAsync(ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 允许从故障或重连状态直接重新连接：先释放旧 socket，再按新参数连接。
        await StopPollingAsync().ConfigureAwait(false);

        _settings = settings.Clone();
        SetState(ConnectionState.Connecting, $"正在连接 {_settings.IpAddress}:{_settings.Port}…");

        try
        {
            await OpenAsync(_settings, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetState(ConnectionState.Disconnected, "连接已取消");
            throw;
        }
        catch (Exception exception)
        {
            SetState(ConnectionState.Faulted, $"连接失败：{exception.Message}");
            _logService.Error(LogCategory.Connection, "Modbus TCP 连接失败。", exception.ToString());
            throw;
        }

        SetState(ConnectionState.Connected, $"已连接 {_settings.IpAddress}:{_settings.Port}");
        _logService.Info(
            LogCategory.Connection,
            "Modbus TCP 连接成功。",
            $"IP={_settings.IpAddress}, Port={_settings.Port}, UnitId={_settings.UnitId}");

        _pollCts = new CancellationTokenSource();
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), CancellationToken.None);
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await StopPollingAsync().ConfigureAwait(false);
        SetState(ConnectionState.Disconnected, "已断开");
    }

    /// <inheritdoc />
    public async Task<RawReadResult> ReadRawAsync(RawReadRequest request, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        if (request.FunctionCode is not (3 or 4))
        {
            return new RawReadResult(false, Array.Empty<ushort>(), $"不支持读功能码 {request.FunctionCode}。", watch.Elapsed);
        }

        if (request.Quantity is < 1 or > MaxReadQuantity)
        {
            return new RawReadResult(false, Array.Empty<ushort>(), $"读取数量必须在 1～{MaxReadQuantity} 之间。", watch.Elapsed);
        }

        if (!TryGetMaster(out _))
        {
            return new RawReadResult(false, Array.Empty<ushort>(), "Modbus 连接未建立。", watch.Elapsed);
        }

        var unitId = request.UnitId;
        var start = request.StartAddress;
        var quantity = request.Quantity;
        try
        {
            var values = await ExecuteAsync(
                master => request.FunctionCode == 3
                    ? master.ReadHoldingRegisters(unitId, start, quantity)
                    : master.ReadInputRegisters(unitId, start, quantity),
                cancellationToken).ConfigureAwait(false);

            return new RawReadResult(
                true,
                values,
                $"FC{request.FunctionCode} 读取 {values.Length} 个寄存器",
                watch.Elapsed);
        }
        catch (SlaveException exception)
        {
            return new RawReadResult(false, Array.Empty<ushort>(), $"从站异常：{exception.Message}", watch.Elapsed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new RawReadResult(false, Array.Empty<ushort>(), $"通信错误：{exception.Message}", watch.Elapsed);
        }
    }

    /// <inheritdoc />
    public async Task<RawWriteResult> WriteRawAsync(RawWriteRequest request, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        if (request.FunctionCode is not (6 or 16))
        {
            return new RawWriteResult(false, false, $"不支持写功能码 {request.FunctionCode}。", watch.Elapsed);
        }

        if (request.FunctionCode == 6 && request.Values.Count != 1)
        {
            return new RawWriteResult(false, false, "功能码 06 只能写入一个寄存器。", watch.Elapsed);
        }

        if (request.FunctionCode == 16 && request.Values.Count is < 1 or > MaxWriteQuantity)
        {
            return new RawWriteResult(false, false, $"功能码 16 的写入数量必须在 1～{MaxWriteQuantity} 之间。", watch.Elapsed);
        }

        if (!TryGetMaster(out _))
        {
            return new RawWriteResult(false, false, "Modbus 连接未建立，未发送任何数据。", watch.Elapsed);
        }

        var unitId = request.UnitId;
        var start = request.StartAddress;
        var values = request.Values.ToArray();
        try
        {
            await ExecuteAsync<bool>(
                master =>
                {
                    if (request.FunctionCode == 6)
                    {
                        master.WriteSingleRegister(unitId, start, values[0]);
                    }
                    else
                    {
                        master.WriteMultipleRegisters(unitId, start, values);
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            return new RawWriteResult(true, false, $"FC{request.FunctionCode} 写入成功", watch.Elapsed);
        }
        catch (SlaveException exception)
        {
            // 从站明确应答异常：写入未生效，可以安全重试。
            return new RawWriteResult(false, false, $"从站拒绝写入：{exception.Message}", watch.Elapsed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 超时或断线可能发生在从站已经执行之后，因此只能报“结果未知”，不能提示失败。
            return new RawWriteResult(
                false,
                true,
                $"写入结果未知，请核对设备状态：{exception.Message}",
                watch.Elapsed);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// 按 <see cref="ProtocolMap"/> 的联调映射写入命令区；任务握手采用“写入—等待车辆回显”方式，
    /// 等待超时返回 ResultUnknown，由操作人员核对实车，禁止自动重发。
    /// </remarks>
    public async Task<CommandResult> ExecuteCommandAsync(ShuttleCarCommand command, CancellationToken cancellationToken = default)
    {
        if (!TryGetMaster(out _))
        {
            return new CommandResult(CommandStatus.CommunicationError, "Modbus 连接未建立。");
        }

        // 命令区与状态区共用同一基址；等待窗口按请求超时的 3 倍计算，低速链路也留有余量。
        var baseAddress = Math.Clamp(_settings.PollStartAddress, 0, ushort.MaxValue - ProtocolMap.TotalLength);
        var waitTimeout = TimeSpan.FromMilliseconds(Math.Max(1500, _settings.RequestTimeoutMs * 3));
        try
        {
            return command.Type switch
            {
                ShuttleCarCommandType.StartTask => await StartTaskAsync(command, baseAddress, waitTimeout, cancellationToken).ConfigureAwait(false),
                ShuttleCarCommandType.ConfirmComplete => await ConfirmCompleteAsync(command, baseAddress, waitTimeout, cancellationToken).ConfigureAwait(false),
                ShuttleCarCommandType.InventoryReset => await PulseAsync(
                    baseAddress + ProtocolMap.CommandInventoryResetRegister, "盘点清零", cancellationToken).ConfigureAwait(false),
                ShuttleCarCommandType.Initialize => await PulseAsync(
                    baseAddress + ProtocolMap.CommandInitializeRegister, "初始化", cancellationToken).ConfigureAwait(false),
                ShuttleCarCommandType.ClearFault => await ClearFaultAsync(baseAddress, cancellationToken).ConfigureAwait(false),
                _ => new CommandResult(CommandStatus.NotSupported, $"不支持命令 {command.Type}。")
            };
        }
        catch (SlaveException exception)
        {
            return new CommandResult(CommandStatus.Rejected, $"从站拒绝命令：{exception.Message}");
        }
        catch (OperationCanceledException)
        {
            return new CommandResult(CommandStatus.ResultUnknown, "命令等待被取消，结果未知，请核对车辆状态。");
        }
        catch (Exception exception)
        {
            return new CommandResult(CommandStatus.ResultUnknown, $"通信错误，结果未知，请核对车辆状态：{exception.Message}");
        }
    }

    /// <summary>写入任务参数区，并等待车辆在状态区回显同一任务号。</summary>
    private async Task<CommandResult> StartTaskAsync(
        ShuttleCarCommand command,
        int baseAddress,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        if (command.TaskNumber is not > 0)
        {
            return new CommandResult(CommandStatus.Rejected, "下发任务必须提供大于 0 的任务号。");
        }

        var taskNumber = command.TaskNumber.Value;
        var values = new ushort[ProtocolMap.CommandTaskBlockLength];
        values[0] = unchecked((ushort)(command.TaskType ?? 0));
        values[2] = unchecked((ushort)(command.OutboundMode ?? 0));
        values[3] = unchecked((ushort)(command.BodyDirection ?? 0));
        values[6] = (ushort)(taskNumber >> 16);
        values[7] = (ushort)(taskNumber & 0xFFFF);
        values[8] = unchecked((ushort)(command.EntryBack ?? 0));

        await WriteRegistersAsync(baseAddress + ProtocolMap.CommandTaskBlockRegister, values, cancellationToken).ConfigureAwait(false);

        var accepted = await WaitForStatusAsync(
            block => ReadTaskNumber(block) == taskNumber,
            waitTimeout,
            cancellationToken).ConfigureAwait(false);

        return accepted
            ? new CommandResult(CommandStatus.Accepted, $"车辆已接收任务 {taskNumber}。", taskNumber)
            : new CommandResult(
                CommandStatus.ResultUnknown,
                $"任务 {taskNumber} 已下发，但未在 {waitTimeout.TotalMilliseconds:F0} ms 内确认接收，请核对车辆状态。",
                taskNumber);
    }

    /// <summary>置位“确认完成”，并等待车辆清除任务号和完成位。</summary>
    private async Task<CommandResult> ConfirmCompleteAsync(
        ShuttleCarCommand command,
        int baseAddress,
        TimeSpan waitTimeout,
        CancellationToken cancellationToken)
    {
        if (command.TaskNumber is not > 0)
        {
            return new CommandResult(CommandStatus.Rejected, "完成确认必须携带任务号。");
        }

        var taskNumber = command.TaskNumber.Value;
        await SetFlagBitAsync(baseAddress, ProtocolMap.ConfirmCompleteBit, true, cancellationToken).ConfigureAwait(false);

        var cleared = await WaitForStatusAsync(
            block => ReadTaskNumber(block) == 0 && !ProtocolMap.IsTaskCompleted(block),
            waitTimeout,
            cancellationToken).ConfigureAwait(false);

        return cleared
            ? new CommandResult(CommandStatus.Completed, $"车辆已确认任务 {taskNumber} 完成。", taskNumber)
            : new CommandResult(
                CommandStatus.ResultUnknown,
                $"完成确认已发送，但车辆未在 {waitTimeout.TotalMilliseconds:F0} ms 内清除任务 {taskNumber}，请核对车辆状态。",
                taskNumber);
    }

    /// <summary>置位后 200ms 复位的“清除异常”脉冲。</summary>
    private async Task<CommandResult> ClearFaultAsync(int baseAddress, CancellationToken cancellationToken)
    {
        await SetFlagBitAsync(baseAddress, ProtocolMap.ClearFaultBit, true, cancellationToken).ConfigureAwait(false);
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        await SetFlagBitAsync(baseAddress, ProtocolMap.ClearFaultBit, false, cancellationToken).ConfigureAwait(false);
        return new CommandResult(CommandStatus.Accepted, "已发送清除异常脉冲，请以最新快照中的报警状态为准。");
    }

    /// <summary>写 1 后 200ms 写 0 的单次命令脉冲，用于盘点清零和初始化。</summary>
    private async Task<CommandResult> PulseAsync(int register, string name, CancellationToken cancellationToken)
    {
        await WriteRegisterAsync(register, 1, cancellationToken).ConfigureAwait(false);
        await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        await WriteRegisterAsync(register, 0, cancellationToken).ConfigureAwait(false);
        return new CommandResult(CommandStatus.Accepted, $"{name}脉冲已发送，请以最新快照确认车辆状态。");
    }

    /// <summary>
    /// 读—改—写命令标志位。
    /// shortcut: 依赖 PLC 不会并发修改同一字；实车必须按方案 7.2 的位复位方式确认后再使用。
    /// </summary>
    private async Task SetFlagBitAsync(int baseAddress, int bit, bool value, CancellationToken cancellationToken)
    {
        var address = baseAddress + ProtocolMap.CommandFlagsRegister;
        var current = await ExecuteAsync(
            master => master.ReadHoldingRegisters(_settings.UnitId, (ushort)address, 1),
            cancellationToken).ConfigureAwait(false);

        await WriteRegisterAsync(address, ProtocolCodec.SetBit(current[0], bit, value), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>在超时窗口内反复读取状态区，直到条件成立。</summary>
    private async Task<bool> WaitForStatusAsync(
        Func<ushort[], bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var start = (ushort)Math.Clamp(_settings.PollStartAddress, 0, ushort.MaxValue);
        while (watch.Elapsed < timeout)
        {
            var block = await ExecuteAsync(
                master => master.ReadHoldingRegisters(_settings.UnitId, start, ProtocolMap.StatusLength),
                cancellationToken).ConfigureAwait(false);

            if (predicate(block))
            {
                return true;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>状态区任务号（DInt@26，高字在前）。</summary>
    private static int ReadTaskNumber(ushort[] block)
    {
        return ProtocolCodec.ToInt32(
            block[ProtocolMap.StatusTaskNumberRegister],
            block[ProtocolMap.StatusTaskNumberRegister + 1]);
    }

    private Task WriteRegisterAsync(int address, ushort value, CancellationToken cancellationToken)
    {
        var unitId = _settings.UnitId;
        return ExecuteAsync<bool>(
            master =>
            {
                master.WriteSingleRegister(unitId, (ushort)address, value);
                return true;
            },
            cancellationToken);
    }

    private Task WriteRegistersAsync(int address, ushort[] values, CancellationToken cancellationToken)
    {
        var unitId = _settings.UnitId;
        return ExecuteAsync<bool>(
            master =>
            {
                master.WriteMultipleRegisters(unitId, (ushort)address, values);
                return true;
            },
            cancellationToken);
    }

    /// <summary>释放连接、停止轮询并释放通信锁。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopPollingAsync().ConfigureAwait(false);
        _ioLock.Dispose();
    }

    /// <summary>
    /// 建立 TCP 连接并创建 NModbus master。
    /// 超时使用连接配置的请求超时；Retries 固定为 0，避免 NModbus 自动重发导致重复写入。
    /// </summary>
    private async Task OpenAsync(ConnectionSettings settings, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(Math.Max(1000, settings.RequestTimeoutMs));
            await client.ConnectAsync(settings.IpAddress, settings.Port, timeoutCts.Token).ConfigureAwait(false);

            var master = _factory.CreateMaster(client);
            master.Transport.ReadTimeout = settings.RequestTimeoutMs;
            master.Transport.WriteTimeout = settings.RequestTimeoutMs;
            master.Transport.Retries = 0;

            _tcpClient = client;
            _master = master;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 周期读取轮询块并发布快照。
    /// 从站异常只标记数据质量（链路仍可用）；传输异常触发重连。
    /// </summary>
    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var consecutiveRejections = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var registers = await ReadPollBlockAsync(cancellationToken).ConfigureAwait(false);
                consecutiveRejections = 0;
                PublishSnapshot(registers);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SlaveException exception)
            {
                // 从站应答“非法地址/数量”属于配置问题，重连无意义，也不每秒刷日志。
                if (++consecutiveRejections == 1)
                {
                    _logService.Warning(
                        LogCategory.Read,
                        "轮询读取被从站拒绝，请确认轮询起始地址和数量。",
                        exception.Message);
                    PublishFaultSnapshot(DataQuality.NotMapped, $"从站拒绝读取：{exception.Message}");
                }
            }
            catch (Exception exception)
            {
                _logService.Warning(LogCategory.Connection, "轮询通信中断，开始重连。", exception.Message);
                PublishFaultSnapshot(DataQuality.Error, "通信中断，正在重连…");
                if (!await TryReconnectAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            try
            {
                await Task.Delay(Math.Max(50, _settings.PollIntervalMs), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>按配置的起始地址和数量读取一块保持寄存器，与原始读写共享同一通信锁。</summary>
    private Task<ushort[]> ReadPollBlockAsync(CancellationToken cancellationToken)
    {
        var unitId = _settings.UnitId;
        var start = (ushort)Math.Clamp(_settings.PollStartAddress, 0, ushort.MaxValue);
        var quantity = (ushort)Math.Clamp(_settings.PollQuantity, 1, MaxReadQuantity);
        return ExecuteAsync(master => master.ReadHoldingRegisters(unitId, start, quantity), cancellationToken);
    }

    /// <summary>按 ProtocolMap 解码轮询块并发布快照。</summary>
    private void PublishSnapshot(IReadOnlyList<ushort> registers)
    {
        var snapshot = ProtocolMap.Decode(registers, _settings.PollStartAddress, DateTimeOffset.Now);
        _lastSnapshot = snapshot;
        SnapshotUpdated?.Invoke(this, snapshot);
    }

    /// <summary>读取失败时保留上一次数值，只把数据质量降级，避免界面清空后误判车辆状态。</summary>
    private void PublishFaultSnapshot(DataQuality quality, string message)
    {
        SnapshotUpdated?.Invoke(this, _lastSnapshot with { Quality = quality, QualityMessage = message });
    }

    /// <summary>按重连间隔反复尝试连接，直到成功或轮询被取消。</summary>
    private async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
    {
        SetState(ConnectionState.Reconnecting, "通信中断，正在重连…");
        Close();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Math.Max(500, _settings.ReconnectIntervalMs), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            try
            {
                await OpenAsync(_settings, cancellationToken).ConfigureAwait(false);
                SetState(ConnectionState.Connected, "重连成功");
                _logService.Info(LogCategory.Connection, "Modbus 重连成功。");
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception exception)
            {
                _logService.Warning(LogCategory.Connection, "重连失败，稍后重试。", exception.Message);
            }
        }

        return false;
    }

    /// <summary>停止轮询任务并释放 socket；可重复调用。</summary>
    private async Task StopPollingAsync()
    {
        var cts = Interlocked.Exchange(ref _pollCts, null);
        var pollTask = Interlocked.Exchange(ref _pollTask, null);
        cts?.Cancel();

        // 先关闭 socket，使阻塞中的 NModbus 读取立即抛异常退出，再等待轮询任务结束。
        Close();
        if (pollTask is not null)
        {
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常取消路径。
            }
        }

        cts?.Dispose();

        // 等正在执行的原始读写释放通信锁，避免返回后仍有后台请求占用旧连接。
        await _ioLock.WaitAsync().ConfigureAwait(false);
        _ioLock.Release();
    }

    /// <summary>在通信锁内用后台线程执行一次 NModbus 同步调用。</summary>
    private async Task<T> ExecuteAsync<T>(Func<IModbusMaster, T> action, CancellationToken cancellationToken)
    {
        if (!TryGetMaster(out var master))
        {
            throw new InvalidOperationException("Modbus 连接未建立。");
        }

        await _ioLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => action(master), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioLock.Release();
        }
    }

    /// <summary>返回当前 master；未连接时返回 false，调用方负责给出明确错误提示。</summary>
    private bool TryGetMaster(out IModbusMaster master)
    {
        var current = _master;
        master = current!;
        return current is not null;
    }

    /// <summary>释放 socket 和 master；Err 状态由后续连接或断开流程重新设置。</summary>
    private void Close()
    {
        var master = Interlocked.Exchange(ref _master, null);
        var client = Interlocked.Exchange(ref _tcpClient, null);
        master?.Dispose();
        client?.Dispose();
    }

    /// <summary>更新状态并通知订阅者；状态未变化时保持安静。</summary>
    private void SetState(ConnectionState state, string? message = null)
    {
        var previous = _state;
        if (previous == state && message is null)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previous, state, message));
    }
}
