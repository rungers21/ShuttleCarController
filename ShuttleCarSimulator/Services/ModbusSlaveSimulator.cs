using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NModbus;
using ShuttleCarController.Services;

namespace ShuttleCarSimulator.Services;

/// <summary>
/// 穿梭车 PLC 的 Modbus TCP 从站仿真器。无 UI 依赖，可在单元/集成测试中直接构造使用。
/// 所有寄存器访问通过同一个内部锁保护；NModbus 主站写入命令区后，由 100 ms 的泵循环应用为仿真状态。
/// </summary>
public sealed class ModbusSlaveSimulator : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly ushort[] _registers = new ushort[ProtocolMap.TotalLength];

    private TcpListener? _listener;
    private IModbusSlaveNetwork? _network;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _pumpTask;

    private int _port;
    private byte _unitId;
    private bool _isRunning;
    private string? _lastError;
    private DateTime _lastBatteryDrain = DateTime.UtcNow;
    private DateTime? _taskStartTime;
    private DateTime? _lastPositionIncrement;
    private DateTime? _inventoryResetExpiry;
    private int _acceptedTaskNumber;
    private bool _manualTaskComplete;
    private int? _manualAlarmCode;

    /// <summary>仿真器是否正在监听 Modbus TCP 连接。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>最近一次启动/运行过程中捕获的错误信息。</summary>
    public string? LastError
    {
        get { lock (_lock) return _lastError; }
        private set { lock (_lock) _lastError = value; }
    }

    /// <summary>构造一个已初始化为默认待机状态的仿真器。</summary>
    public ModbusSlaveSimulator()
    {
        InitializeRegistersLocked();
    }

    /// <summary>获取当前状态的不可变快照。</summary>
    public SimulatorStatus Status
    {
        get
        {
            lock (_lock)
            {
                var registers = new ushort[ProtocolMap.TotalLength];
                Array.Copy(_registers, registers, ProtocolMap.TotalLength);

                var message = _lastError
                    ?? (_isRunning
                        ? $"Modbus TCP 从站运行中（端口 {_port}，站号 {_unitId}）"
                        : "Modbus TCP 从站已停止");

                return new SimulatorStatus(
                    _isRunning,
                    _port,
                    _unitId,
                    ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusCarStateRegister]),
                    ProtocolCodec.ToInt32(
                        _registers[ProtocolMap.StatusTaskNumberRegister],
                        _registers[ProtocolMap.StatusTaskNumberRegister + 1]),
                    ProtocolCodec.GetBit(_registers[ProtocolMap.StatusFlagsRegister], ProtocolMap.StatusTaskCompletedBit),
                    ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusAlarmRegister]),
                    ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusOffset + 10]),
                    ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusOffset + 9]),
                    ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusOffset + 15]),
                    ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusOffset + 16]),
                    ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusOffset + 17]),
                    ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusOffset + 18]),
                    ProtocolCodec.GetBit(_registers[ProtocolMap.StatusFlagsRegister], ProtocolMap.StatusAtSideABit),
                    ProtocolCodec.GetBit(_registers[ProtocolMap.StatusFlagsRegister], ProtocolMap.StatusAtSideBBit),
                    ProtocolCodec.GetBit(_registers[ProtocolMap.InteractionFaultRegister], ProtocolMap.HasGoodsBit),
                    ProtocolCodec.GetBit(_registers[ProtocolMap.InteractionCargoRegister], ProtocolMap.NoGoodsBit),
                    registers,
                    message);
            }
        }
    }

    /// <summary>
    /// 在指定端口启动 Modbus TCP 从站。已处于运行状态时调用会直接返回，不会抛异常。
    /// </summary>
    public async Task StartAsync(int port, byte unitId)
    {
        if (_isRunning)
        {
            return;
        }

        _port = port;
        _unitId = unitId;
        _cts = new CancellationTokenSource();

        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();

            var factory = new ModbusFactory();
            var store = new SimulatorDataStore(this);
            _network = factory.CreateSlaveNetwork(_listener);
            _network.AddSlave(factory.CreateSlave(unitId, store));

            _lastBatteryDrain = DateTime.UtcNow;
            _isRunning = true;
            LastError = null;

            _listenTask = Task.Run(() => ListenLoop(_network, _cts.Token));
            _pumpTask = Task.Run(() => PumpLoop(_cts.Token));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _isRunning = false;
            _listener?.Stop();
            _cts?.Cancel();
            throw;
        }
    }

    /// <summary>停止 Modbus TCP 从站。可重复调用。</summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
        {
            return;
        }

        _cts?.Cancel();

        try { _listener?.Stop(); }
        catch { /* 监听已释放 */ }

        if (_network is IDisposable disposable)
        {
            try { disposable.Dispose(); }
            catch { }
        }

        if (_listenTask != null)
        {
            try { await _listenTask.ConfigureAwait(false); }
            catch { }
        }

        if (_pumpTask != null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
            catch { }
        }

        _cts?.Dispose();
        _cts = null;
        _network = null;
        _listener = null;
        _listenTask = null;
        _pumpTask = null;
        _isRunning = false;
    }

    /// <summary>异步释放：等价于调用 <see cref="StopAsync"/>。</summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    /// <summary>设置或清除指定协议位字段（支持 <see cref="ProtocolMap.FaultFields"/> 以及 3.7/4.0 货物位）。</summary>
    public void SetFault(ProtocolField field, bool active)
    {
        if (field.BitIndex < 0)
        {
            throw new ArgumentException("字段必须是 Bool 位字段", nameof(field));
        }

        lock (_lock)
        {
            if (field.RegisterOffset >= _registers.Length)
            {
                return;
            }

            _registers[field.RegisterOffset] = ProtocolCodec.SetBit(
                _registers[field.RegisterOffset], field.BitIndex, active);
            UpdateAlarmAndNormalLocked();
        }
    }

    /// <summary>立即将当前任务标记为完成（模拟任务正常结束）。</summary>
    public void SetTaskComplete()
    {
        lock (_lock)
        {
            var taskNumber = ProtocolCodec.ToInt32(
                _registers[ProtocolMap.StatusTaskNumberRegister],
                _registers[ProtocolMap.StatusTaskNumberRegister + 1]);

            if (taskNumber != 0 || _registers[ProtocolMap.StatusCarStateRegister] != 0)
            {
                _manualTaskComplete = true;
            }
        }
    }

    /// <summary>清除所有故障位、报警码，并恢复“正常”状态。</summary>
    public void ClearFaults()
    {
        lock (_lock)
        {
            var word = _registers[ProtocolMap.InteractionFaultRegister];
            for (var bit = 0; bit < 15; bit++)
            {
                word = ProtocolCodec.SetBit(word, bit, false);
            }

            _registers[ProtocolMap.InteractionFaultRegister] = word;
            _manualAlarmCode = null;
            UpdateAlarmAndNormalLocked();
        }
    }

    /// <summary>设置当前电量（5～100），并自动触发电量不足故障位。</summary>
    public void SetBattery(int value)
    {
        lock (_lock)
        {
            var battery = (short)Math.Clamp(value, 5, 100);
            _registers[ProtocolMap.StatusOffset + 10] = (ushort)battery;
            _registers[ProtocolMap.InteractionBatteryRegister] = (ushort)battery;
            SetBitLocked(ProtocolMap.InteractionFaultRegister, 14, battery <= 20);
            UpdateAlarmAndNormalLocked();
        }
    }

    /// <summary>手动设置状态区报警码（覆盖故障计数，清除后恢复自动计数）。</summary>
    public void SetAlarmCode(int code)
    {
        lock (_lock)
        {
            _manualAlarmCode = code;
            UpdateAlarmAndNormalLocked();
        }
    }

    private async Task ListenLoop(IModbusSlaveNetwork network, CancellationToken ct)
    {
        try
        {
            await network.ListenAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (ObjectDisposedException)
        {
            // 监听套接字已释放
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private async Task PumpLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private void Tick()
    {
        lock (_lock)
        {
            if (!_isRunning)
            {
                return;
            }

            var now = DateTime.UtcNow;

            // 电量：任务运行期间每 10 秒下降 1，最低 5。
            if (_registers[ProtocolMap.StatusCarStateRegister] != 0
                && (now - _lastBatteryDrain).TotalSeconds >= 10)
            {
                _lastBatteryDrain = now;
                var battery = ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusOffset + 10]);
                if (battery > 5)
                {
                    battery--;
                    _registers[ProtocolMap.StatusOffset + 10] = (ushort)battery;
                    _registers[ProtocolMap.InteractionBatteryRegister] = (ushort)battery;
                    SetBitLocked(ProtocolMap.InteractionFaultRegister, 14, battery <= 20);
                }
            }

            // 盘点清零脉冲回零。
            if (_inventoryResetExpiry.HasValue && now >= _inventoryResetExpiry.Value)
            {
                _inventoryResetExpiry = null;
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 1, false);
            }

            // 读取命令区。
            var cmdTaskNumber = ProtocolCodec.ToInt32(
                _registers[ProtocolMap.CommandTaskNumberRegister],
                _registers[ProtocolMap.CommandTaskNumberRegister + 1]);
            var cmdOutboundMode = _registers[ProtocolMap.CommandOutboundModeRegister];
            var cmdBodyDirection = _registers[ProtocolMap.CommandBodyDirectionRegister];
            var confirmComplete = ProtocolCodec.GetBit(_registers[ProtocolMap.CommandFlagsRegister], ProtocolMap.ConfirmCompleteBit);
            var clearFault = ProtocolCodec.GetBit(_registers[ProtocolMap.CommandFlagsRegister], ProtocolMap.ClearFaultBit);
            var inventoryReset = _registers[ProtocolMap.CommandInventoryResetRegister];
            var initialize = _registers[ProtocolMap.CommandInitializeRegister];

            var statusTaskNumber = ProtocolCodec.ToInt32(
                _registers[ProtocolMap.StatusTaskNumberRegister],
                _registers[ProtocolMap.StatusTaskNumberRegister + 1]);
            var taskActive = statusTaskNumber != 0 || _registers[ProtocolMap.StatusCarStateRegister] != 0;

            // 初始化：脉冲回零并恢复待机状态。
            if (initialize == 1)
            {
                _registers[ProtocolMap.CommandInitializeRegister] = 0;
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 0, true);   // 待机中
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 8, true);   // 待机
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 14, true);  // 小车启动完成
                _registers[ProtocolMap.StatusCarStateRegister] = 0;
            }

            // 盘点清零：脉冲回零并置位镜像 1.5 秒。
            if (inventoryReset == 1)
            {
                _registers[ProtocolMap.CommandInventoryResetRegister] = 0;
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 1, true);
                _inventoryResetExpiry = now.AddSeconds(1.5);
            }

            // 清除异常。
            if (clearFault)
            {
                var faultWord = _registers[ProtocolMap.InteractionFaultRegister];
                for (var bit = 0; bit < 15; bit++)
                {
                    faultWord = ProtocolCodec.SetBit(faultWord, bit, false);
                }

                _registers[ProtocolMap.InteractionFaultRegister] = faultWord;
                _manualAlarmCode = null;
                SetBitLocked(ProtocolMap.CommandFlagsRegister, ProtocolMap.ClearFaultBit, false);
            }

            // 确认完成。
            if (confirmComplete && statusTaskNumber != 0)
            {
                _registers[ProtocolMap.StatusTaskNumberRegister] = 0;
                _registers[ProtocolMap.StatusTaskNumberRegister + 1] = 0;
                _registers[ProtocolMap.StatusOffset + 1] = 0;
                SetBitLocked(ProtocolMap.StatusFlagsRegister, ProtocolMap.StatusTaskCompletedBit, false);
                SetBitLocked(ProtocolMap.InteractionTaskStateRegister, ProtocolMap.InboundPendingBit, false);
                SetBitLocked(ProtocolMap.InteractionTaskStateRegister, ProtocolMap.OutboundPendingBit, false);
                _registers[ProtocolMap.StatusCarStateRegister] = 0;
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 0, true);
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 8, true);
                SetBitLocked(ProtocolMap.CommandFlagsRegister, ProtocolMap.ConfirmCompleteBit, false);
                _manualTaskComplete = false;
                _taskStartTime = null;
                _lastPositionIncrement = null;

                // 命令区同步复位：清空任务类型、任务号、入库后退等 9 个命令寄存器，
                // 否则主站下一次下发相同任务号时会被误判为“没有新任务”。
                for (var register = ProtocolMap.CommandTaskBlockRegister;
                     register < ProtocolMap.CommandTaskBlockRegister + ProtocolMap.CommandTaskBlockLength;
                     register++)
                {
                    _registers[register] = 0;
                }

                _acceptedTaskNumber = 0;
            }

            // 新任务：任务号变化即视为新任务，任务类型 0 也接受，便于界面用默认参数联调。
            if (cmdTaskNumber == 0)
            {
                _acceptedTaskNumber = 0;
            }
            else if (cmdTaskNumber != _acceptedTaskNumber)
            {
                _acceptedTaskNumber = cmdTaskNumber;
                _registers[ProtocolMap.StatusOffset + 2] = cmdOutboundMode;
                _registers[ProtocolMap.StatusOffset + 3] = cmdBodyDirection;
                _registers[ProtocolMap.StatusTaskNumberRegister] = _registers[ProtocolMap.CommandTaskNumberRegister];
                _registers[ProtocolMap.StatusTaskNumberRegister + 1] = _registers[ProtocolMap.CommandTaskNumberRegister + 1];
                _registers[ProtocolMap.StatusOffset + 1] = 0;
                SetBitLocked(ProtocolMap.StatusFlagsRegister, ProtocolMap.StatusTaskCompletedBit, false);
                _registers[ProtocolMap.StatusCarStateRegister] = 1;
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 0, false);
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 8, false);
                SetBitLocked(ProtocolMap.InteractionTaskStateRegister, ProtocolMap.InboundPendingBit, false);
                SetBitLocked(ProtocolMap.InteractionTaskStateRegister, ProtocolMap.OutboundPendingBit, true);
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 9, false);
                _taskStartTime = now;
                _lastPositionIncrement = now;
                _manualTaskComplete = false;
            }

            // 任务执行中的位置变化与超时完成。
            if (taskActive && !_manualTaskComplete)
            {
                if (_taskStartTime.HasValue && (now - _taskStartTime.Value).TotalSeconds >= 3)
                {
                    _manualTaskComplete = true;
                }

                if (_lastPositionIncrement.HasValue && (now - _lastPositionIncrement.Value).TotalMilliseconds >= 250)
                {
                    _lastPositionIncrement = now;
                    var position = ProtocolCodec.ToInt16(_registers[ProtocolMap.StatusOffset + 9]);
                    _registers[ProtocolMap.StatusOffset + 9] = (ushort)(position + 1);
                }
            }

            // 应用任务完成。
            if (_manualTaskComplete && taskActive)
            {
                _registers[ProtocolMap.StatusOffset + 1] = 1;
                SetBitLocked(ProtocolMap.StatusFlagsRegister, ProtocolMap.StatusTaskCompletedBit, true);
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 0, true);
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 8, true);
                _registers[ProtocolMap.StatusCarStateRegister] = 0;
                SetBitLocked(ProtocolMap.InteractionTaskStateRegister, ProtocolMap.OutboundPendingBit, false);
                SetBitLocked(ProtocolMap.InteractionRunStateRegister, 9, true);
                _taskStartTime = null;
                _lastPositionIncrement = null;
            }

            UpdateAlarmAndNormalLocked();
        }
    }

    private void UpdateAlarmAndNormalLocked()
    {
        var faultWord = _registers[ProtocolMap.InteractionFaultRegister];
        var count = 0;
        for (var bit = 0; bit < 15; bit++)
        {
            if (ProtocolCodec.GetBit(faultWord, bit))
            {
                count++;
            }
        }

        var code = _manualAlarmCode ?? count;
        _registers[ProtocolMap.StatusAlarmRegister] = (ushort)code;
        SetBitLocked(ProtocolMap.InteractionRunStateRegister, 15, code == 0 && count == 0);
    }

    private void InitializeRegistersLocked()
    {
        Array.Clear(_registers, 0, _registers.Length);

        _registers[ProtocolMap.StatusOffset + 10] = 100;   // 电量
        _registers[ProtocolMap.StatusOffset + 11] = 1;     // 车号
        _registers[ProtocolMap.StatusOffset + 12] = 1;     // 连接状态
        _registers[ProtocolMap.StatusOffset + 15] = 1;     // 当前层
        _registers[ProtocolMap.StatusOffset + 16] = 1;     // 当前列
        _registers[ProtocolMap.StatusOffset + 17] = 1;     // 当前排
        _registers[ProtocolMap.StatusOffset + 18] = 1;     // 当前巷道

        SetBitLocked(ProtocolMap.StatusFlagsRegister, ProtocolMap.StatusAtSideABit, true);
        SetBitLocked(ProtocolMap.InteractionCargoRegister, 1, true); // 在 A 侧

        SetBitLocked(ProtocolMap.InteractionRunStateRegister, 0, true);   // 待机中
        SetBitLocked(ProtocolMap.InteractionRunStateRegister, 8, true);   // 待机
        SetBitLocked(ProtocolMap.InteractionRunStateRegister, 14, true);  // 小车启动完成
        SetBitLocked(ProtocolMap.InteractionRunStateRegister, 15, true);  // 正常

        _registers[ProtocolMap.InteractionBatteryRegister] = 100;
        UpdateAlarmAndNormalLocked();
    }

    private void SetBitLocked(int register, int bit, bool value)
    {
        _registers[register] = ProtocolCodec.SetBit(_registers[register], bit, value);
    }

    #region NModbus 数据存储实现

    private sealed class SimulatorDataStore(ModbusSlaveSimulator simulator) : ISlaveDataStore
    {
        public IPointSource<bool> CoilDiscretes { get; } = new EmptyPointSource<bool>();
        public IPointSource<bool> CoilInputs { get; } = new EmptyPointSource<bool>();
        public IPointSource<ushort> HoldingRegisters { get; } = new RegistersPointSource(simulator);
        public IPointSource<ushort> InputRegisters { get; } = new EmptyPointSource<ushort>();
    }

    private sealed class RegistersPointSource(ModbusSlaveSimulator simulator) : IPointSource<ushort>
    {
        public ushort[] ReadPoints(ushort startAddress, ushort numberOfPoints)
        {
            if (numberOfPoints == 0)
            {
                return Array.Empty<ushort>();
            }

            lock (simulator._lock)
            {
                var length = simulator._registers.Length;
                if (startAddress >= length)
                {
                    return Array.Empty<ushort>();
                }

                var count = Math.Min(numberOfPoints, length - startAddress);
                var result = new ushort[count];
                Array.Copy(simulator._registers, startAddress, result, 0, count);
                return result;
            }
        }

        public void WritePoints(ushort startAddress, ushort[] points)
        {
            if (points.Length == 0)
            {
                return;
            }

            lock (simulator._lock)
            {
                var length = simulator._registers.Length;
                if (startAddress >= length)
                {
                    return;
                }

                var count = Math.Min(points.Length, length - startAddress);
                Array.Copy(points, 0, simulator._registers, startAddress, count);
            }
        }
    }

    private sealed class EmptyPointSource<T> : IPointSource<T> where T : struct
    {
        public T[] ReadPoints(ushort startAddress, ushort numberOfPoints)
        {
            return new T[numberOfPoints];
        }

        public void WritePoints(ushort startAddress, T[] points)
        {
            // 仿真器不使用线圈/输入寄存器。
        }
    }

    #endregion
}
