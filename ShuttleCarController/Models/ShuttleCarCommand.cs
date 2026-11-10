namespace ShuttleCarController.Models;

/// <summary>
/// 强类型穿梭车业务命令。
/// 这里只描述“执行什么动作”，不包含 Modbus 地址、字节序和握手细节。
/// </summary>
public sealed record ShuttleCarCommand
{
    /// <summary>命令类别，决定通信实现应执行的协议流程。</summary>
    public required ShuttleCarCommandType Type { get; init; }

    /// <summary>
    /// 任务号，对应协议中的 DInt@12。
    /// 新任务和完成确认必须携带任务号；维护类命令通常不需要。
    /// </summary>
    public int? TaskNumber { get; init; }

    /// <summary>任务类型原始值，对应 Int@0；具体枚举含义须由最终协议确认。</summary>
    public short? TaskType { get; init; }

    /// <summary>出库方式原始值，对应 Int@4；FIFO/FILO 的数值编码由通信实现映射。</summary>
    public short? OutboundMode { get; init; }

    /// <summary>车体方向原始值，对应 Int@6；A/B 面的数值编码由通信实现映射。</summary>
    public short? BodyDirection { get; init; }

    /// <summary>入库后退参数，对应 Int@16；当前不擅自按 Bool 处理。</summary>
    public short? EntryBack { get; init; }
}

/// <summary>
/// 业务命令的统一返回结果。
/// </summary>
/// <param name="Status">命令状态，用于区分已接收、已完成、被拒绝和结果未知。</param>
/// <param name="Message">供界面展示和日志追踪的设备反馈或错误说明。</param>
/// <param name="TaskNumber">设备实际关联的任务号，可用于核对命令是否作用于预期任务。</param>
public sealed record CommandResult(CommandStatus Status, string Message, int? TaskNumber = null)
{
    /// <summary>
    /// 命令是否被设备明确接受。
    /// Accepted 仅表示已接收，并不必然代表整个任务已经执行结束。
    /// </summary>
    public bool IsAccepted => Status is CommandStatus.Accepted or CommandStatus.Completed;

    /// <summary>
    /// 命令结果是否无法确认。
    /// 为 true 时应禁止自动重发，并提示操作人员核对车辆实际状态。
    /// </summary>
    public bool IsUnknown => Status is CommandStatus.TimedOut or CommandStatus.ResultUnknown;
}
