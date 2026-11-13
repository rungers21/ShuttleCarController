using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using ShuttleCarController.Services;
using ShuttleCarSimulator.Services;

namespace ShuttleCarSimulator.ViewModels;

/// <summary>
/// 主窗口的轻量视图模型，所有 UI 状态均来自 <see cref="ModbusSlaveSimulator.Status"/> 快照。
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ModbusSlaveSimulator _simulator;
    private SimulatorStatus _status;
    private string _portText = "15020";
    private string _unitIdText = "1";
    private string _batteryInput = "100";
    private string _alarmInput = "0";
    private string _inputError = string.Empty;

    public MainViewModel(ModbusSlaveSimulator simulator)
    {
        _simulator = simulator;
        _status = simulator.Status;

        // 初始化故障开关列表（2.0～3.6 的故障 + 3.7/4.0 货物位）。
        foreach (var field in ProtocolMap.FaultFields)
        {
            FaultItems.Add(new FaultItem(simulator, field));
        }

        var hasGoods = ProtocolMap.Fields.First(f => f.Name == "上方有货");
        var noGoods = ProtocolMap.Fields.First(f => f.Name == "上方无货");
        FaultItems.Add(new FaultItem(simulator, hasGoods));
        FaultItems.Add(new FaultItem(simulator, noGoods));

        // 初始化原始寄存器表格行。
        for (var address = 0; address < ProtocolMap.TotalLength; address++)
        {
            Registers.Add(new RegisterRow(address));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SimulatorStatus CurrentStatus
    {
        get => _status;
        private set
        {
            _status = value;
            OnPropertyChanged();
        }
    }

    public string PortText
    {
        get => _portText;
        set { _portText = value; OnPropertyChanged(); }
    }

    public string UnitIdText
    {
        get => _unitIdText;
        set { _unitIdText = value; OnPropertyChanged(); }
    }

    public string BatteryInput
    {
        get => _batteryInput;
        set { _batteryInput = value; OnPropertyChanged(); }
    }

    public string AlarmInput
    {
        get => _alarmInput;
        set { _alarmInput = value; OnPropertyChanged(); }
    }

    public string StartStopText => CurrentStatus.Running ? "停止服务" : "启动服务";

    /// <summary>
    /// 启停操作最近的输入错误。
    /// 非法端口/站号或端口占用时在这里给出明确提示，不再静默失败；
    /// 成功启动或停止服务后自动清空。
    /// </summary>
    public string InputError
    {
        get => _inputError;
        private set
        {
            if (_inputError == value)
            {
                return;
            }

            _inputError = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<FaultItem> FaultItems { get; } = new();
    public ObservableCollection<RegisterRow> Registers { get; } = new();

    /// <summary>从仿真器读取一次最新状态并刷新所有绑定。</summary>
    public void Refresh()
    {
        var next = _simulator.Status;

        // 寄存器内容和运行状态都没变时，不替换状态对象，避免整窗绑定每 300 ms 重新求值。
        if (StatusEquals(_status, next))
        {
            return;
        }

        CurrentStatus = next;

        for (var i = 0; i < Registers.Count && i < CurrentStatus.Registers.Count; i++)
        {
            Registers[i].RawValue = CurrentStatus.Registers[i];
        }

        foreach (var item in FaultItems)
        {
            item.Refresh(CurrentStatus.Registers);
        }

        OnPropertyChanged(nameof(StartStopText));
    }

    /// <summary>逐项比较快照；寄存器列表按内容比较，而不是按列表引用。</summary>
    private static bool StatusEquals(SimulatorStatus left, SimulatorStatus right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Running != right.Running
            || left.Port != right.Port
            || left.UnitId != right.UnitId
            || left.CarState != right.CarState
            || left.TaskNumber != right.TaskNumber
            || left.TaskCompleted != right.TaskCompleted
            || left.AlarmCode != right.AlarmCode
            || left.Battery != right.Battery
            || left.Position != right.Position
            || left.Layer != right.Layer
            || left.Column != right.Column
            || left.Row != right.Row
            || left.Aisle != right.Aisle
            || left.AtSideA != right.AtSideA
            || left.AtSideB != right.AtSideB
            || left.HasGoods != right.HasGoods
            || left.NoGoods != right.NoGoods
            || !string.Equals(left.Message, right.Message, StringComparison.Ordinal)
            || left.Registers.Count != right.Registers.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Registers.Count; i++)
        {
            if (left.Registers[i] != right.Registers[i])
            {
                return false;
            }
        }

        return true;
    }

    public async Task ToggleServiceAsync()
    {
        if (CurrentStatus.Running)
        {
            await _simulator.StopAsync().ConfigureAwait(false);
            InputError = string.Empty;
            return;
        }

        if (!int.TryParse(PortText.Trim(), out var port) || port is <= 0 or > 65535)
        {
            InputError = "端口无效：请输入 1～65535 之间的整数。";
            return;
        }

        if (!byte.TryParse(UnitIdText.Trim(), out var unitId) || unitId == 0)
        {
            InputError = "站号无效：请输入 1～255 之间的整数。";
            return;
        }

        try
        {
            await _simulator.StartAsync(port, unitId).ConfigureAwait(false);
            InputError = string.Empty;
        }
        catch (Exception exception)
        {
            // 端口占用、权限不足等启动错误在这里立即反馈，不再只依赖下一次状态刷新。
            InputError = $"启动失败：{exception.Message}";
        }
    }

    public void SimulateTaskComplete() => _simulator.SetTaskComplete();

    public void ClearFaults() => _simulator.ClearFaults();

    public void ApplyBattery()
    {
        if (int.TryParse(BatteryInput, out var value))
        {
            _simulator.SetBattery(value);
        }
    }

    public void ApplyAlarm()
    {
        if (int.TryParse(AlarmInput, out var value))
        {
            _simulator.SetAlarmCode(value);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>原始寄存器表格的一行。</summary>
public sealed class RegisterRow : INotifyPropertyChanged
{
    private ushort _rawValue;

    public RegisterRow(int address)
    {
        Address = address;
    }

    public int Address { get; }

    public ushort RawValue
    {
        get => _rawValue;
        set
        {
            if (_rawValue == value)
            {
                return;
            }

            _rawValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HexValue));
            OnPropertyChanged(nameof(DecimalValue));
        }
    }

    public string HexValue => $"0x{_rawValue:X4}";

    public string DecimalValue => ProtocolCodec.ToInt16(_rawValue).ToString();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>单个故障/货物开关。</summary>
public sealed class FaultItem : INotifyPropertyChanged
{
    private readonly ModbusSlaveSimulator _simulator;
    private bool _isActive;

    public FaultItem(ModbusSlaveSimulator simulator, ProtocolField field)
    {
        _simulator = simulator;
        Field = field;
        DisplayText = $"DB{field.DbOffset}  {field.Name}";
    }

    public ProtocolField Field { get; }
    public string DisplayText { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            _simulator.SetFault(Field, value);
            OnPropertyChanged();
        }
    }

    public void Refresh(IReadOnlyList<ushort> registers)
    {
        var active = Field.RegisterOffset < registers.Count
                     && ProtocolCodec.GetBit(registers[Field.RegisterOffset], Field.BitIndex);
        if (_isActive == active)
        {
            return;
        }

        _isActive = active;
        OnPropertyChanged(nameof(IsActive));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
