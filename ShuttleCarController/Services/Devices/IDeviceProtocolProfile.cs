using System;
using System.Collections.Generic;
using ShuttleCarController.Models;

namespace ShuttleCarController.Services.Devices;

/// <summary>
/// 设备协议画像：把“轮询区段、字段映射、快照解码、命令编码、完成判定”从通信客户端中隔离出来。
/// 新增车型或协议版本时实现该接口并注册到 <see cref="DeviceProfileCatalog"/>，
/// 无需修改 <see cref="ModbusShuttleCarClient"/> 或 ViewModel。
/// </summary>
public interface IDeviceProtocolProfile
{
    /// <summary>稳定标识，用于配置文件和工厂解析（例如 two-way-shuttle）。</summary>
    string Id { get; }

    /// <summary>界面与日志中显示的名称。</summary>
    string DisplayName { get; }

    /// <summary>轮询块所需的最大寄存器数量（基址到块尾）。</summary>
    int TotalLength { get; }

    /// <summary>用于数据表展示的全部字段映射。</summary>
    IReadOnlyList<ProtocolField> Fields { get; }

    /// <summary>故障位字段（交互区 2.x / 3.x 等）。</summary>
    IReadOnlyList<ProtocolField> FaultFields { get; }

    /// <summary>把一次轮询读到的寄存器块解码为设备无关快照。</summary>
    ShuttleCarSnapshot Decode(IReadOnlyList<ushort> block, int baseAddress, DateTimeOffset updatedAt);

    /// <summary>把业务命令编码为按顺序执行的寄存器写入步骤（地址已包含基址）。</summary>
    IReadOnlyList<DeviceWriteStep> EncodeCommand(ShuttleCarCommand command, int baseAddress);

    /// <summary>判断命令是否已在快照中达成（例如任务号回显、任务清零）。</summary>
    bool IsCommandSatisfied(ShuttleCarCommand command, ShuttleCarSnapshot snapshot);

    /// <summary>该命令是否需要等待“达成”；脉冲类命令不等待。</summary>
    bool ShouldWaitForSatisfaction(ShuttleCarCommandType type);
}
