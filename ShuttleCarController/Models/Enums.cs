namespace ShuttleCarController.Models;

/// <summary>
/// 上位机与穿梭车之间的连接状态。
/// 该状态描述本机通信链路，不等同于 PLC 上报的“车辆连接状态”。
/// </summary>
public enum ConnectionState
{
    /// <summary>尚未建立连接，或连接已由用户主动断开。</summary>
    Disconnected,

    /// <summary>正在建立连接；此阶段禁止重复发起连接。</summary>
    Connecting,

    /// <summary>连接已建立，可以读取状态；业务命令仍须检查数据新鲜度和车辆状态。</summary>
    Connected,

    /// <summary>通信中断后正在重连；重连成功前不得恢复或补发旧命令。</summary>
    Reconnecting,

    /// <summary>连接、读取或协议处理发生异常，需要检查网络参数和设备状态。</summary>
    Faulted
}

/// <summary>
/// 某一批车辆数据的可信程度。界面可以保留最后一次成功值，但必须同时展示数据质量。
/// </summary>
public enum DataQuality
{
    /// <summary>尚未收到数据，不能据此判断车辆状态。</summary>
    Unknown,

    /// <summary>本次读取和解析均成功，且数据处于有效期内。</summary>
    Good,

    /// <summary>显示的是历史值，最近读取失败或超过允许的数据年龄。</summary>
    Stale,

    /// <summary>读取、协议转换或数据处理失败。</summary>
    Error,

    /// <summary>协议字段已定义，但尚未配置或确认对应的 Modbus 地址。</summary>
    NotMapped
}

/// <summary>界面和文件日志的严重程度。</summary>
public enum LogLevel
{
    /// <summary>细粒度诊断信息，通常只在排查问题时使用。</summary>
    Trace,

    /// <summary>正常操作或状态变化。</summary>
    Info,

    /// <summary>当前操作未完全成功或结果需要人工注意，但程序仍可继续运行。</summary>
    Warning,

    /// <summary>操作失败、通信异常或未预期错误。</summary>
    Error
}

/// <summary>日志来源分类，便于在界面和日志文件中筛选。</summary>
public enum LogCategory
{
    /// <summary>程序启动、退出、配置以及未分类系统事件。</summary>
    System,

    /// <summary>连接、断开、重连及链路状态变化。</summary>
    Connection,

    /// <summary>寄存器读取、状态轮询和读取异常。</summary>
    Read,

    /// <summary>原始寄存器写入及其结果。</summary>
    Write,

    /// <summary>任务、初始化、盘点清零、确认完成等业务命令。</summary>
    Command,

    /// <summary>车辆报警产生、恢复以及报警数据异常。</summary>
    Alarm
}

/// <summary>
/// 上位机支持的业务命令类型。
/// 枚举只表达业务意图，不包含寄存器地址；具体编码由 IShuttleCarClient 实现决定。
/// </summary>
public enum ShuttleCarCommandType
{
    /// <summary>携带任务类型、任务号和方向等参数，下发一条新任务。</summary>
    StartTask,

    /// <summary>请求车辆执行盘点清零；属于需要二次确认的高风险操作。</summary>
    InventoryReset,

    /// <summary>请求车辆初始化；执行前应确认车辆空闲且现场安全。</summary>
    Initialize,

    /// <summary>车辆报告任务完成后，上位机发送完成确认以闭环任务握手。</summary>
    ConfirmComplete,

    /// <summary>请求清除可恢复的异常状态，不能替代现场故障排查。</summary>
    ClearFault
}

/// <summary>业务命令执行结果，特别区分“明确失败”和“结果未知”。</summary>
public enum CommandStatus
{
    /// <summary>设备已接受命令，任务可能仍在执行。</summary>
    Accepted,

    /// <summary>命令或任务已经完整执行完成。</summary>
    Completed,

    /// <summary>设备明确拒绝命令，例如前置条件不满足。</summary>
    Rejected,

    /// <summary>当前设备或协议实现不支持该命令。</summary>
    NotSupported,

    /// <summary>等待结果超时；命令可能已经生效，禁止直接自动重发。</summary>
    TimedOut,

    /// <summary>通信过程中发生错误，需要结合设备状态判断命令是否生效。</summary>
    CommunicationError,

    /// <summary>无法确认设备最终状态，应由操作人员核对实车。</summary>
    ResultUnknown
}
