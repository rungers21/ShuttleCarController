using System;
using System.Collections.Generic;
using ShuttleCarController.Models;

namespace ShuttleCarController.Services.Devices;

/// <summary>
/// 二向穿梭车默认协议画像：寄存器偏移、命令编码和握手判定仍以 <see cref="ProtocolMap"/> 的
/// 联调表为唯一来源，<see cref="ProtocolMap"/> 同时保留为向后兼容门面。
/// </summary>
public sealed class TwoWayShuttleProtocolProfile : IDeviceProtocolProfile
{
    /// <summary>配置文件中的默认画像标识。</summary>
    public const string ProfileId = "two-way-shuttle";

    /// <summary>无状态单例。</summary>
    public static TwoWayShuttleProtocolProfile Instance { get; } = new();

    private TwoWayShuttleProtocolProfile()
    {
    }

    /// <inheritdoc />
    public string Id => ProfileId;

    /// <inheritdoc />
    public string DisplayName => "二向穿梭车";

    /// <inheritdoc />
    public int TotalLength => ProtocolMap.TotalLength;

    /// <inheritdoc />
    public IReadOnlyList<ProtocolField> Fields => ProtocolMap.Fields;

    /// <inheritdoc />
    public IReadOnlyList<ProtocolField> FaultFields => ProtocolMap.FaultFields;

    /// <inheritdoc />
    public ShuttleCarSnapshot Decode(IReadOnlyList<ushort> block, int baseAddress, DateTimeOffset updatedAt)
        => ProtocolMap.DecodeCore(block, baseAddress, updatedAt);

    /// <inheritdoc />
    public IReadOnlyList<DeviceWriteStep> EncodeCommand(ShuttleCarCommand command, int baseAddress)
    {
        switch (command.Type)
        {
            case ShuttleCarCommandType.StartTask:
            {
                var taskNumber = command.TaskNumber ?? 0;
                var values = new ushort[ProtocolMap.CommandTaskBlockLength];
                values[0] = unchecked((ushort)(command.TaskType ?? 0));
                values[2] = unchecked((ushort)(command.OutboundMode ?? 0));
                values[3] = unchecked((ushort)(command.BodyDirection ?? 0));
                values[6] = (ushort)(taskNumber >> 16);
                values[7] = (ushort)(taskNumber & 0xFFFF);
                values[8] = unchecked((ushort)(command.EntryBack ?? 0));
                return [new DeviceWriteStep((ushort)(baseAddress + ProtocolMap.CommandTaskBlockRegister), values)];
            }

            case ShuttleCarCommandType.ConfirmComplete:
                return [DeviceWriteStep.SetFlag((ushort)(baseAddress + ProtocolMap.CommandFlagsRegister), ProtocolMap.ConfirmCompleteBit)];

            case ShuttleCarCommandType.InventoryReset:
            {
                var address = (ushort)(baseAddress + ProtocolMap.CommandInventoryResetRegister);
                return [DeviceWriteStep.Single(address, 1, 200), DeviceWriteStep.Single(address, 0)];
            }

            case ShuttleCarCommandType.Initialize:
            {
                var address = (ushort)(baseAddress + ProtocolMap.CommandInitializeRegister);
                return [DeviceWriteStep.Single(address, 1, 200), DeviceWriteStep.Single(address, 0)];
            }

            case ShuttleCarCommandType.ClearFault:
            {
                var address = (ushort)(baseAddress + ProtocolMap.CommandFlagsRegister);
                return
                [
                    DeviceWriteStep.SetFlag(address, ProtocolMap.ClearFaultBit, 200),
                    DeviceWriteStep.ClearFlag(address, ProtocolMap.ClearFaultBit)
                ];
            }

            default:
                return [];
        }
    }

    /// <inheritdoc />
    public bool IsCommandSatisfied(ShuttleCarCommand command, ShuttleCarSnapshot snapshot)
    {
        return command.Type switch
        {
            ShuttleCarCommandType.StartTask => command.TaskNumber is { } number && snapshot.TaskNumber == number,
            ShuttleCarCommandType.ConfirmComplete => snapshot.TaskNumber is null or 0 && !snapshot.TaskCompletedFlag,
            _ => true
        };
    }

    /// <inheritdoc />
    public bool ShouldWaitForSatisfaction(ShuttleCarCommandType type)
        => type is ShuttleCarCommandType.StartTask or ShuttleCarCommandType.ConfirmComplete;
}
