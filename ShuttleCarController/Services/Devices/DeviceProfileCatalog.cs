using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ShuttleCarController.Services.Devices;

/// <summary>
/// 内置协议画像目录。新增设备时在 <see cref="Profiles"/> 中登记实现即可，
/// 配置文件的 <c>DeviceProfileId</c> 按 <see cref="IDeviceProtocolProfile.Id"/> 匹配。
/// </summary>
public static class DeviceProfileCatalog
{
    private static readonly IReadOnlyList<IDeviceProtocolProfile> RegisteredProfiles =
    [
        TwoWayShuttleProtocolProfile.Instance
    ];

    /// <summary>当前程序集内置的全部协议画像。</summary>
    public static IReadOnlyList<IDeviceProtocolProfile> Profiles => RegisteredProfiles;

    /// <summary>
    /// 按标识解析画像；空标识使用二向穿梭车默认画像。
    /// 未知标识不猜测设备类型，回退默认画像并写调试跟踪，避免启动失败后误连实车。
    /// </summary>
    public static IDeviceProtocolProfile Resolve(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return TwoWayShuttleProtocolProfile.Instance;
        }

        var profile = RegisteredProfiles.FirstOrDefault(
            candidate => string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase));
        if (profile is not null)
        {
            return profile;
        }

        Trace.TraceWarning($"未找到设备协议画像“{profileId}”，回退默认画像 {TwoWayShuttleProtocolProfile.ProfileId}。");
        return TwoWayShuttleProtocolProfile.Instance;
    }
}
