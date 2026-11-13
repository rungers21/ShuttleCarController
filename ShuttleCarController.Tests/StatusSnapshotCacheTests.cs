using ShuttleCarController.Models;
using ShuttleCarController.Services;

namespace ShuttleCarController.Tests;

/// <summary>轮询热路径帧缓存的性能回归测试。</summary>
public sealed class StatusSnapshotCacheTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Decode_SameFrame_ReusesFieldAndAlarmCollections()
    {
        var cache = new StatusSnapshotCache();
        var frame = new ushort[ProtocolMap.TotalLength];

        var first = cache.Decode(frame, 0, Start);
        var second = cache.Decode(frame, 0, Start.AddMilliseconds(500));

        Assert.Same(first.Fields, second.Fields);
        Assert.Same(first.Alarms, second.Alarms);
        Assert.Equal(Start.AddMilliseconds(500), second.UpdatedAt);

        // 字段自身的时间戳记录“数值最后变化时间”，复用帧时不应被刷新。
        Assert.All(second.Fields, row => Assert.Equal(Start, row.UpdatedAt));
    }

    [Fact]
    public void Decode_ChangedRegister_RebuildsSnapshot()
    {
        var cache = new StatusSnapshotCache();
        var frame = new ushort[ProtocolMap.TotalLength];
        var first = cache.Decode(frame, 0, Start);

        frame[ProtocolMap.StatusCarStateRegister] = 2;
        var second = cache.Decode(frame, 0, Start.AddMilliseconds(500));

        Assert.NotSame(first.Fields, second.Fields);
        Assert.Equal(2, second.CarStateRaw);
        Assert.Contains(second.Fields, row => row.Name == "小车状态" && row.DecodedValue == "2");
    }

    [Fact]
    public void Decode_BaseAddressChanged_DoesNotReuseFrame()
    {
        var cache = new StatusSnapshotCache();
        var frame = new ushort[ProtocolMap.TotalLength];
        var first = cache.Decode(frame, 0, Start);

        var second = cache.Decode(frame, 100, Start.AddMilliseconds(500));

        Assert.NotSame(first.Fields, second.Fields);
        Assert.Equal("100", second.Fields[0].ModbusAddress);
    }

    [Fact]
    public void Decode_ThousandIdenticalFrames_KeepsSingleDecodedTable()
    {
        var cache = new StatusSnapshotCache();
        var frame = new ushort[ProtocolMap.TotalLength];
        ShuttleCarSnapshot? first = null;

        for (var i = 0; i < 1000; i++)
        {
            var snapshot = cache.Decode(frame, 0, Start.AddMilliseconds(i * 500));
            first ??= snapshot;
            Assert.Same(first.Fields, snapshot.Fields);
        }

        Assert.NotNull(first);
        Assert.Equal(ProtocolMap.Fields.Count, first.Fields.Count);
    }

    [Fact]
    public void Decode_FaultBitSet_ProducesNewAlarmList()
    {
        var cache = new StatusSnapshotCache();
        var frame = new ushort[ProtocolMap.TotalLength];
        var clean = cache.Decode(frame, 0, Start);

        frame[ProtocolMap.InteractionFaultRegister] =
            ProtocolCodec.SetBit(frame[ProtocolMap.InteractionFaultRegister], 2, true);
        var faulty = cache.Decode(frame, 0, Start.AddMilliseconds(500));

        Assert.NotSame(clean.Alarms, faulty.Alarms);
        Assert.True(faulty.HasAlarm);
    }
}
