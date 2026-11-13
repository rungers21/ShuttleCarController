using System.Collections.Generic;

namespace ShuttleCarController.Services.Devices;

/// <summary>
/// 一条协议无关的寄存器写入指令。
/// 命令编码器把“下发任务/确认完成/脉冲命令”统一描述为若干步写入，
/// 传输层只负责按顺序执行，不再硬编码任何寄存器偏移。
/// </summary>
/// <param name="Address">写入的绝对 Modbus 保持寄存器地址（已包含轮询基址）。</param>
/// <param name="Values">
/// 连续写入的寄存器值；单元素时使用 FC06，多元素时使用 FC16。
/// 标志位写入时可以为空，由 <paramref name="SetFlagBit"/> / <paramref name="ClearFlagBit"/> 指定。
/// </param>
/// <param name="DelayAfterMs">本步写完后等待的毫秒数，用于“置位—延时—复位”的脉冲命令。</param>
/// <param name="SetFlagBit">需要读—改—写置位的位序号；为空表示不操作标志位。</param>
/// <param name="ClearFlagBit">需要读—改—写复位的位序号；为空表示不操作标志位。</param>
public sealed record DeviceWriteStep(
    ushort Address,
    IReadOnlyList<ushort> Values,
    int DelayAfterMs = 0,
    int? SetFlagBit = null,
    int? ClearFlagBit = null)
{
    /// <summary>便捷构造：单个寄存器值。</summary>
    public static DeviceWriteStep Single(ushort address, ushort value, int delayAfterMs = 0)
        => new(address, [value], delayAfterMs);

    /// <summary>便捷构造：置位标志位。</summary>
    public static DeviceWriteStep SetFlag(ushort address, int bit, int delayAfterMs = 0)
        => new(address, [], delayAfterMs, SetFlagBit: bit);

    /// <summary>便捷构造：复位标志位。</summary>
    public static DeviceWriteStep ClearFlag(ushort address, int bit, int delayAfterMs = 0)
        => new(address, [], delayAfterMs, ClearFlagBit: bit);
}
