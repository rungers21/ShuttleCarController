using ShuttleCarController.Services;

namespace ShuttleCarController.Tests;

/// <summary>协议基础编解码的边界测试：符号、字序和位操作不能污染相邻位。</summary>
public sealed class ProtocolCodecTests
{
    [Theory]
    [InlineData(0x0000, 0)]
    [InlineData(0x7FFF, 32767)]
    [InlineData(0x8000, -32768)]
    [InlineData(0xFFFF, -1)]
    public void ToInt16_ConvertsTwosComplement(ushort raw, int expected)
    {
        Assert.Equal((short)expected, ProtocolCodec.ToInt16(raw));
    }

    [Fact]
    public void ToInt32_DefaultTreatsFirstWordAsHighWord()
    {
        Assert.Equal(0x00010002, ProtocolCodec.ToInt32(0x0001, 0x0002));
    }

    [Fact]
    public void ToInt32_CanSwapWordOrder()
    {
        Assert.Equal(0x00020001, ProtocolCodec.ToInt32(0x0001, 0x0002, swapWords: true));
    }

    [Fact]
    public void ToInt32_NegativeHighWordIsPreserved()
    {
        Assert.Equal(-1, ProtocolCodec.ToInt32(0xFFFF, 0xFFFF));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(15)]
    public void SetBit_DoesNotAffectAdjacentBits(int bitIndex)
    {
        const ushort initial = 0b1010_1010_1010_1010;
        var set = ProtocolCodec.SetBit(initial, bitIndex, true);
        var cleared = ProtocolCodec.SetBit(initial, bitIndex, false);

        Assert.True(ProtocolCodec.GetBit(set, bitIndex));
        Assert.False(ProtocolCodec.GetBit(cleared, bitIndex));

        for (var other = 0; other < 16; other++)
        {
            if (other == bitIndex)
            {
                continue;
            }

            Assert.Equal(ProtocolCodec.GetBit(initial, other), ProtocolCodec.GetBit(set, other));
            Assert.Equal(ProtocolCodec.GetBit(initial, other), ProtocolCodec.GetBit(cleared, other));
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public void GetBit_OutOfRangeThrows(int bitIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolCodec.GetBit(0, bitIndex));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    public void SetBit_OutOfRangeThrows(int bitIndex)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProtocolCodec.SetBit(0, bitIndex, true));
    }
}
