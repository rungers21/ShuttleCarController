using System;
using System.Collections.Generic;
using ShuttleCarController.Models;
using ShuttleCarController.Services.Devices;

namespace ShuttleCarController.Services;

/// <summary>
/// 轮询热路径的帧缓存。
/// 500 ms 轮询下大量帧内容完全相同；此时复用上一帧已解码的 74 行字段表和 15 项报警列表，
/// 只把快照时间戳推进到当前时刻，避免 WPF 每周期重建整表引用而触发 DataGrid 重排。
/// </summary>
internal sealed class StatusSnapshotCache
{
    private readonly IDeviceProtocolProfile _profile;
    private ushort[] _frame = [];
    private int _baseAddress;
    private ShuttleCarSnapshot? _snapshot;

    /// <summary>默认使用二向穿梭车画像，保持既有调用方兼容。</summary>
    public StatusSnapshotCache()
        : this(TwoWayShuttleProtocolProfile.Instance)
    {
    }

    /// <summary>按指定设备画像解码，供新车型或测试注入。</summary>
    public StatusSnapshotCache(IDeviceProtocolProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
    }

    /// <summary>连接参数变化时丢弃缓存，防止把上一次连接的帧当成新数据。</summary>
    public void Reset()
    {
        _frame = [];
        _snapshot = null;
    }

    /// <summary>
    /// 解码一个轮询块；块内容、基址和上一帧质量都未变化时复用上一帧。
    /// 复用帧的 <see cref="ShuttleCarSnapshot.UpdatedAt"/> 会更新，但各字段的
    /// <see cref="ShuttleCarFieldValue.UpdatedAt"/> 保留“数值最后一次变化”的时间，
    /// 便于操作员区分“数值没变”和“根本没读到”。
    /// </summary>
    public ShuttleCarSnapshot Decode(IReadOnlyList<ushort> block, int baseAddress, DateTimeOffset updatedAt)
    {
        if (_snapshot is not null
            && _baseAddress == baseAddress
            && _snapshot.Quality == DataQuality.Good
            && FrameEquals(block))
        {
            return _snapshot with { UpdatedAt = updatedAt };
        }

        var snapshot = _profile.Decode(block, baseAddress, updatedAt);
        _frame = Copy(block);
        _baseAddress = baseAddress;
        _snapshot = snapshot;
        return snapshot;
    }

    private bool FrameEquals(IReadOnlyList<ushort> block)
    {
        if (block.Count != _frame.Length)
        {
            return false;
        }

        for (var i = 0; i < _frame.Length; i++)
        {
            if (block[i] != _frame[i])
            {
                return false;
            }
        }

        return true;
    }

    private static ushort[] Copy(IReadOnlyList<ushort> block)
    {
        var copy = new ushort[block.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = block[i];
        }

        return copy;
    }
}
