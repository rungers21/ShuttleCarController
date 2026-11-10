using System;
using System.Collections.Generic;
using System.Linq;

namespace ShuttleCarController.Models;

/// <summary>
/// 原始寄存器读取请求，仅供协议联调和诊断使用。
/// 业务界面不依赖该类，避免操作人员直接接触寄存器地址。
/// </summary>
/// <param name="UnitId">目标 Modbus 站号。</param>
/// <param name="FunctionCode">读功能码，当前界面允许 03（保持寄存器）或 04（输入寄存器）。</param>
/// <param name="StartAddress">起始寄存器地址，零基/一基换算由通信实现按最终协议处理。</param>
/// <param name="Quantity">读取寄存器数量，界面限制为 1～125。</param>
public sealed record RawReadRequest(
    byte UnitId,
    byte FunctionCode,
    ushort StartAddress,
    ushort Quantity);

/// <summary>原始寄存器读取结果，保留原始 ushort 值以便检查字节序和协议映射。</summary>
/// <param name="Success">请求是否明确成功。</param>
/// <param name="Values">按设备返回顺序保存的寄存器数组，尚未做业务类型转换。</param>
/// <param name="Message">成功说明、Modbus 异常或通信错误信息。</param>
/// <param name="Elapsed">本次请求耗时，用于诊断链路延迟。</param>
public sealed record RawReadResult(
    bool Success,
    IReadOnlyList<ushort> Values,
    string Message,
    TimeSpan Elapsed)
{
    /// <summary>以四位十六进制展示全部原始寄存器，便于和 PLC 监控值逐字核对。</summary>
    public string ValuesText => Values.Count == 0
        ? "--"
        : string.Join(", ", Values.Select(value => $"0x{value:X4}"));

    /// <summary>面向界面的结果摘要，包含成功状态、耗时和详细消息。</summary>
    public string Summary => $"{(Success ? "成功" : "失败")}，耗时 {Elapsed.TotalMilliseconds:F0} ms：{Message}";
}

/// <summary>
/// 原始寄存器写入请求。
/// FC06 要求 Values 仅含一个元素；FC16 允许连续写入多个寄存器。
/// </summary>
/// <param name="UnitId">目标 Modbus 站号。</param>
/// <param name="FunctionCode">写功能码，当前界面允许 06 或 16。</param>
/// <param name="StartAddress">起始寄存器地址。</param>
/// <param name="Values">待写入的原始寄存器值，按连续地址顺序排列。</param>
public sealed record RawWriteRequest(
    byte UnitId,
    byte FunctionCode,
    ushort StartAddress,
    IReadOnlyList<ushort> Values);

/// <summary>
/// 原始寄存器写入结果。
/// 写超时不能简单视为失败，因此使用 ResultUnknown 单独标记“可能已经生效”的情况。
/// </summary>
/// <param name="Success">设备是否明确确认写入成功。</param>
/// <param name="ResultUnknown">超时或断线导致结果无法确认；此时禁止自动重发。</param>
/// <param name="Message">设备响应或错误详情。</param>
/// <param name="Elapsed">写入请求耗时。</param>
public sealed record RawWriteResult(
    bool Success,
    bool ResultUnknown,
    string Message,
    TimeSpan Elapsed)
{
    /// <summary>根据“成功、失败、结果未知”生成不同的界面提示。</summary>
    public string Summary => ResultUnknown
        ? $"结果未知，耗时 {Elapsed.TotalMilliseconds:F0} ms：{Message}"
        : $"{(Success ? "成功" : "失败")}，耗时 {Elapsed.TotalMilliseconds:F0} ms：{Message}";
}
