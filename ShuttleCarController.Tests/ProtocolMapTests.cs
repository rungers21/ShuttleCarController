using ShuttleCarController.Models;
using ShuttleCarController.Services;

namespace ShuttleCarController.Tests;

/// <summary>ProtocolMap 解码、冲突检测和“未写入”标注的回归测试。</summary>
public sealed class ProtocolMapTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ushort[] EmptyBlock()
    {
        return new ushort[ProtocolMap.TotalLength];
    }

    private static void SetBit(ushort[] block, int registerOffset, int bitIndex, bool value = true)
    {
        block[registerOffset] = ProtocolCodec.SetBit(block[registerOffset], bitIndex, value);
    }

    [Fact]
    public void Fields_ExposeAllDocumentedRows()
    {
        Assert.Equal(74, ProtocolMap.Fields.Count);
        Assert.Equal(15, ProtocolMap.FaultFields.Count);
    }

    [Fact]
    public void Decode_NegativeCarState_PreservesRawValue()
    {
        // 0x8000 在 Int16 解释下是 -32768，不能被静默当成正常值处理。
        var block = EmptyBlock();
        block[ProtocolMap.StatusCarStateRegister] = 0x8000;

        var snapshot = ProtocolMap.Decode(block, 0, Timestamp);

        Assert.Equal(DataQuality.Good, snapshot.Quality);
        Assert.Equal(-32768, snapshot.CarStateRaw);
        Assert.Contains("未确认", snapshot.CarStateText);
    }

    [Fact]
    public void Decode_TooShortBlock_ReturnsError()
    {
        var snapshot = ProtocolMap.Decode(new ushort[19], 0, Timestamp);

        Assert.Equal(DataQuality.Error, snapshot.Quality);
        Assert.Contains("不足状态区", snapshot.QualityMessage);
        Assert.Empty(snapshot.Fields);
    }

    [Fact]
    public void Decode_TaskNumberUsesHighWordFirst()
    {
        var block = EmptyBlock();
        block[ProtocolMap.StatusTaskNumberRegister] = 0x0001;
        block[ProtocolMap.StatusTaskNumberRegister + 1] = 0x0002;

        var snapshot = ProtocolMap.Decode(block, 0, Timestamp);

        Assert.Equal(65538, snapshot.TaskNumber);
    }

    [Fact]
    public void Decode_BothSideFlags_ProduceConflictAlarm()
    {
        var block = EmptyBlock();
        SetBit(block, ProtocolMap.StatusFlagsRegister, ProtocolMap.StatusAtSideABit);
        SetBit(block, ProtocolMap.StatusFlagsRegister, ProtocolMap.StatusAtSideBBit);

        var snapshot = ProtocolMap.Decode(block, 0, Timestamp);

        Assert.True(snapshot.AtSideA);
        Assert.True(snapshot.AtSideB);
        Assert.Contains(snapshot.Alarms, alarm => alarm.Name == "方向状态冲突" && alarm.IsActive);
        Assert.True(snapshot.HasAlarm);
    }

    [Fact]
    public void Decode_GoodsFlagsBothTrue_ProduceConflictAlarm()
    {
        var block = EmptyBlock();
        SetBit(block, ProtocolMap.InteractionFaultRegister, ProtocolMap.HasGoodsBit);
        SetBit(block, ProtocolMap.InteractionCargoRegister, ProtocolMap.NoGoodsBit);

        var snapshot = ProtocolMap.Decode(block, 0, Timestamp);

        Assert.True(snapshot.HasGoods);
        Assert.True(snapshot.NoGoods);
        Assert.Contains(snapshot.Alarms, alarm => alarm.Name == "货物状态冲突" && alarm.IsActive);
        Assert.Equal("状态冲突（有货/无货同时为真）", snapshot.CargoPresenceText);
    }

    [Fact]
    public void Decode_GoodsFlagsBothFalse_ReportUndetermined()
    {
        var snapshot = ProtocolMap.Decode(EmptyBlock(), 0, Timestamp);

        Assert.False(snapshot.HasGoods);
        Assert.False(snapshot.NoGoods);
        Assert.Equal("未确定", snapshot.CargoPresenceText);
    }

    [Fact]
    public void Decode_FifoMirrorAndStateMismatch_ProduceConflictAlarm()
    {
        var block = EmptyBlock();
        SetBit(block, ProtocolMap.InteractionRunStateRegister, ProtocolMap.InteractionFifoBit);
        SetBit(block, ProtocolMap.InteractionRunStateRegister, ProtocolMap.InteractionFiloStateBit);

        var snapshot = ProtocolMap.Decode(block, 0, Timestamp);

        Assert.Contains(snapshot.Alarms, alarm => alarm.Name == "出库方式状态冲突" && alarm.IsActive);
        Assert.True(snapshot.HasAlarm);
    }

    [Fact]
    public void Decode_FifoAndFiloMirrorsBothTrue_ProduceConflictAlarm()
    {
        var block = EmptyBlock();
        SetBit(block, ProtocolMap.InteractionRunStateRegister, ProtocolMap.InteractionFifoBit);
        SetBit(block, ProtocolMap.InteractionRunStateRegister, ProtocolMap.InteractionFiloBit);

        var snapshot = ProtocolMap.Decode(block, 0, Timestamp);

        Assert.Contains(snapshot.Alarms, alarm => alarm.Name == "出库方式状态冲突" && alarm.IsActive);
    }

    [Theory]
    [InlineData("2.0", 0)]
    [InlineData("2.1", 1)]
    [InlineData("3.1", 9)]
    public void Decode_UnwrittenFaultBits_AreAnnotated(string dbOffset, int bitIndex)
    {
        var block = EmptyBlock();
        SetBit(block, ProtocolMap.InteractionFaultRegister, bitIndex);

        var snapshot = ProtocolMap.Decode(block, 0, Timestamp);

        var field = ProtocolMap.Fields.Single(item => item.DbOffset == dbOffset);
        Assert.True(field.Unwritten);

        var alarm = Assert.Single(snapshot.Alarms, item => item.Name == field.Name && item.IsActive);
        Assert.Equal("未写入", alarm.Detail);
        Assert.Contains("未写入", alarm.DisplayName);

        var row = Assert.Single(snapshot.Fields, item => item.DbOffset == dbOffset);
        Assert.Contains("未写入", row.Name);
    }

    [Fact]
    public void Decode_AlarmCodeNonZero_IsAlarmEvenWithoutFaultBits()
    {
        var block = EmptyBlock();
        block[ProtocolMap.StatusAlarmRegister] = 7;

        var snapshot = ProtocolMap.Decode(block, 0, Timestamp);

        Assert.True(snapshot.HasAlarm);
        Assert.Contains("报警码 7", snapshot.AlarmText);
    }

    [Fact]
    public void Decode_InteractionMissing_StillReturnsStatusFields()
    {
        var block = new ushort[ProtocolMap.StatusLength];

        var snapshot = ProtocolMap.Decode(block, 0, Timestamp);

        Assert.Equal(DataQuality.Good, snapshot.Quality);
        Assert.Empty(snapshot.Alarms);
        Assert.False(snapshot.HasGoods);
        Assert.DoesNotContain(snapshot.Fields, row => row.Area == "上位机交互");
    }

    [Fact]
    public void Decode_AddressesAreOffsetByBaseAddress()
    {
        var block = EmptyBlock();

        var snapshot = ProtocolMap.Decode(block, 100, Timestamp);

        var carState = Assert.Single(
            snapshot.Fields,
            row => row.Area == "车辆→上位机" && row.DbOffset == "0");
        Assert.Equal("100", carState.ModbusAddress);

        var atSideA = Assert.Single(snapshot.Fields, row => row.DbOffset == "38.1");
        Assert.Equal("119.1", atSideA.ModbusAddress);
    }

    [Fact]
    public void IsTaskCompleted_RequiresFullFlagsRegister()
    {
        Assert.False(ProtocolMap.IsTaskCompleted(new ushort[ProtocolMap.StatusFlagsRegister]));
        Assert.False(ProtocolMap.IsTaskCompleted(EmptyBlock()));

        var block = EmptyBlock();
        SetBit(block, ProtocolMap.StatusFlagsRegister, ProtocolMap.StatusTaskCompletedBit);
        Assert.True(ProtocolMap.IsTaskCompleted(block));
    }
}
