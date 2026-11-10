using System;

namespace ShuttleCarController.Services;

/// <summary>
/// 纯协议转换工具，不包含任何通信逻辑。
/// 只有寄存器地址、字节顺序和位映射经实车固定值验证后，客户端实现才应使用这些方法。
/// </summary>
public static class ProtocolCodec
{
    /// <summary>
    /// 将一个原始 Modbus 寄存器按有符号 Int16 解释。
    /// unchecked 保证 0xFFFF 转换为 -1，而不是抛出溢出异常。
    /// </summary>
    public static short ToInt16(ushort value)
    {
        return unchecked((short)value);
    }

    /// <summary>
    /// 将连续两个寄存器组合为有符号 Int32。
    /// </summary>
    /// <param name="first">设备返回顺序中的第一个寄存器。</param>
    /// <param name="second">设备返回顺序中的第二个寄存器。</param>
    /// <param name="swapWords">
    /// 是否交换高低字。false 表示 first 为高 16 位；true 表示 second 为高 16 位。
    /// 寄存器内部的字节顺序应在调用前按网关协议处理。
    /// </param>
    public static int ToInt32(ushort first, ushort second, bool swapWords = false)
    {
        var high = swapWords ? second : first;
        var low = swapWords ? first : second;
        return unchecked((int)(((uint)high << 16) | low));
    }

    /// <summary>
    /// 读取一个已规范化寄存器中的指定位。
    /// bitIndex 取 0～15，其中 0 表示该寄存器最低位；它不等同于 Excel 的 DB 字节位偏移。
    /// </summary>
    public static bool GetBit(ushort value, int bitIndex)
    {
        if (bitIndex is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(bitIndex), "位序号必须在 0～15 之间。");
        }

        return (value & (1 << bitIndex)) != 0;
    }

    /// <summary>
    /// 修改寄存器中的一个位，同时保留其余 15 位。
    /// 该方法只做内存位运算；若用于“读—改—写”，通信实现必须确认该字不会被 PLC 并发修改。
    /// </summary>
    public static ushort SetBit(ushort value, int bitIndex, bool enabled)
    {
        if (bitIndex is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(bitIndex), "位序号必须在 0～15 之间。");
        }

        var mask = (ushort)(1 << bitIndex);
        return enabled
            ? (ushort)(value | mask)
            : (ushort)(value & ~mask);
    }
}
