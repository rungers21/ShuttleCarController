namespace ShuttleCarController.Services.Devices;

/// <summary>
/// 设备类型。新增车型时在这里扩展，不需要修改通信客户端的协议处理逻辑。
/// </summary>
public enum DeviceType
{
    /// <summary>二向穿梭车（当前唯一已实现的协议画像）。</summary>
    TwoWayShuttle,

    /// <summary>四向穿梭车（预留，尚无已确认协议）。</summary>
    FourWayShuttle,

    /// <summary>提升机（预留，尚无已确认协议）。</summary>
    Lifter,

    /// <summary>输送线（预留，尚无已确认协议）。</summary>
    Conveyor
}
