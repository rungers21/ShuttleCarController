using System.Collections.Generic;

namespace ShuttleCarController.Services.Devices;

/// <summary>
/// 设备客户端工厂：按协议画像创建通信客户端。
/// 上层（ViewModel / 未来的多车调度）只依赖该接口，不直接 new 具体传输实现，
/// 为 Modbus RTU、四向车、提升机等新设备保留 DI 扩展点。
/// </summary>
public interface IDeviceClientFactory
{
    /// <summary>当前可用的协议画像。</summary>
    IReadOnlyList<IDeviceProtocolProfile> Profiles { get; }

    /// <summary>按配置中的画像标识解析画像（空/未知时回退默认画像）。</summary>
    IDeviceProtocolProfile ResolveProfile(string? profileId);

    /// <summary>按画像标识创建客户端。</summary>
    IShuttleCarClient CreateClient(string? profileId);

    /// <summary>按显式画像创建客户端。</summary>
    IShuttleCarClient CreateClient(IDeviceProtocolProfile profile);

    /// <summary>按设备描述创建客户端。</summary>
    IShuttleCarClient CreateClient(DeviceDescriptor descriptor);
}
