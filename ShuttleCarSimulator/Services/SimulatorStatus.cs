using System.Collections.Generic;

namespace ShuttleCarSimulator.Services;

/// <summary>
/// Modbus 从站仿真器的一次性状态快照，所有字段均在获取时从寄存器副本计算。
/// </summary>
public sealed record SimulatorStatus(
    bool Running,
    int Port,
    byte UnitId,
    short CarState,
    int TaskNumber,
    bool TaskCompleted,
    short AlarmCode,
    int Battery,
    short Position,
    short Layer,
    short Column,
    short Row,
    short Aisle,
    bool AtSideA,
    bool AtSideB,
    bool HasGoods,
    bool NoGoods,
    IReadOnlyList<ushort> Registers,
    string Message);
