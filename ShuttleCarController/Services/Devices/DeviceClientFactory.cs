using System;
using System.Collections.Generic;

namespace ShuttleCarController.Services.Devices;

/// <summary>
/// 默认设备客户端工厂：当前只提供 Modbus TCP 传输，但创建入口已与设备画像解耦。
/// </summary>
public sealed class DeviceClientFactory : IDeviceClientFactory
{
    private readonly ILogService _logService;

    /// <summary>创建工厂。</summary>
    public DeviceClientFactory(ILogService logService)
    {
        ArgumentNullException.ThrowIfNull(logService);
        _logService = logService;
    }

    /// <inheritdoc />
    public IReadOnlyList<IDeviceProtocolProfile> Profiles => DeviceProfileCatalog.Profiles;

    /// <inheritdoc />
    public IDeviceProtocolProfile ResolveProfile(string? profileId) => DeviceProfileCatalog.Resolve(profileId);

    /// <inheritdoc />
    public IShuttleCarClient CreateClient(string? profileId) => CreateClient(ResolveProfile(profileId));

    /// <inheritdoc />
    public IShuttleCarClient CreateClient(IDeviceProtocolProfile profile)
        => new ModbusShuttleCarClient(_logService, profile);

    /// <inheritdoc />
    public IShuttleCarClient CreateClient(DeviceDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return CreateClient(descriptor.Protocol);
    }
}
