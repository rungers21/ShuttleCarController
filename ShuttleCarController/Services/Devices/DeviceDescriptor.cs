using ShuttleCarController.Models;

namespace ShuttleCarController.Services.Devices;

/// <summary>
/// 一台设备的完整描述：设备标识、类型、协议画像与连接参数。
/// 未来多车/多车型场景用该描述选择客户端，不需要在主工程里写死车型判断。
/// </summary>
/// <param name="DeviceId">设备唯一标识（例如车位号或调度系统 ID）。</param>
/// <param name="DeviceType">设备类型。</param>
/// <param name="Protocol">该设备使用的协议画像。</param>
/// <param name="Connection">连接参数。</param>
public sealed record DeviceDescriptor(
    string DeviceId,
    DeviceType DeviceType,
    IDeviceProtocolProfile Protocol,
    ConnectionSettings Connection);
