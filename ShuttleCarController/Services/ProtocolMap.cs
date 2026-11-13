using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ShuttleCarController.Models;
using ShuttleCarController.Services.Devices;

namespace ShuttleCarController.Services;

/// <summary>
/// 联调仿真用的 Modbus 地址映射。
/// 三个数据区按方案 4.3 的规则连续排列：寄存器索引 = 区基址 + DB 字节偏移 / 2，
/// 寄存器内的位序号 = (字节偏移 % 2) * 8 + 位偏移。
/// </summary>
/// <remarks>
/// 该映射只用于上位机与仿真从站联调，不是已确认的现场协议（方案 P0-02～P0-07 仍待硬件方确认）。
/// 三个区都放在同一地址空间，经 <c>ConnectionSettings.PollStartAddress</c> 整体平移；
/// 与实车对接前必须按最终映射替换本文件和从站的实现。
/// 枚举显示约定（FIFO=0/FILO=1、A面=0/B面=1、连接 0/1）同样只在仿真中有效。
/// </remarks>
public static class ProtocolMap
{
    /// <summary>车辆→上位机状态区的相对偏移。</summary>
    public const int StatusOffset = 0;

    /// <summary>状态区寄存器数量，对应 DB 字节 0～39。</summary>
    public const int StatusLength = 20;

    /// <summary>上位机→车辆命令区的相对偏移。</summary>
    public const int CommandOffset = 20;

    /// <summary>命令区寄存器数量，对应 DB 字节 0～21。</summary>
    public const int CommandLength = 11;

    /// <summary>上位机交互区的相对偏移。</summary>
    public const int InteractionOffset = 32;

    /// <summary>交互区寄存器数量，对应 DB 字节 0～11。</summary>
    public const int InteractionLength = 6;

    /// <summary>一次轮询读取整个映射所需的寄存器数量。</summary>
    public const int TotalLength = InteractionOffset + InteractionLength;

    // 状态区关键寄存器：其它字段通过 Fields 表按下标访问。
    /// <summary>小车状态 Int@0。</summary>
    public const int StatusCarStateRegister = StatusOffset + 0;

    /// <summary>报警码 Int@12。</summary>
    public const int StatusAlarmRegister = StatusOffset + 6;

    /// <summary>任务号 DInt@26，占 13/14 两个寄存器（高字在前）。</summary>
    public const int StatusTaskNumberRegister = StatusOffset + 13;

    /// <summary>Bool@38.0～38.2 所在寄存器。</summary>
    public const int StatusFlagsRegister = StatusOffset + 19;

    /// <summary>任务完成 Bool@38.0 的位序号。</summary>
    public const int StatusTaskCompletedBit = 0;

    /// <summary>在 A 侧 Bool@38.1 的位序号。</summary>
    public const int StatusAtSideABit = 1;

    /// <summary>在 B 侧 Bool@38.2 的位序号。</summary>
    public const int StatusAtSideBBit = 2;

    // 命令区关键寄存器。
    /// <summary>任务类型 Int@0。</summary>
    public const int CommandTaskTypeRegister = CommandOffset + 0;

    /// <summary>盘点清零 Int@2，写 1 后应恢复 0。</summary>
    public const int CommandInventoryResetRegister = CommandOffset + 1;

    /// <summary>出库方式 Int@4。</summary>
    public const int CommandOutboundModeRegister = CommandOffset + 2;

    /// <summary>车体方向 Int@6。</summary>
    public const int CommandBodyDirectionRegister = CommandOffset + 3;

    /// <summary>初始化 Int@10，写 1 后应恢复 0。</summary>
    public const int CommandInitializeRegister = CommandOffset + 5;

    /// <summary>任务号 DInt@12，占 6/7 两个寄存器（高字在前）。</summary>
    public const int CommandTaskNumberRegister = CommandOffset + 6;

    /// <summary>入库后退 Int@16。</summary>
    public const int CommandEntryBackRegister = CommandOffset + 8;

    /// <summary>Bool@20.0/20.1 所在寄存器。</summary>
    public const int CommandFlagsRegister = CommandOffset + 10;

    /// <summary>确认完成 Bool@20.0 的位序号。</summary>
    public const int ConfirmCompleteBit = 0;

    /// <summary>清除异常 Bool@20.1 的位序号。</summary>
    public const int ClearFaultBit = 1;

    /// <summary>下发任务时一次性写入的命令区起始寄存器（任务类型～入库后退）。</summary>
    public const int CommandTaskBlockRegister = CommandTaskTypeRegister;

    /// <summary>下发任务时写入的命令区寄存器数量。</summary>
    public const int CommandTaskBlockLength = 9;

    // 交互区关键寄存器。
    /// <summary>运行状态位（字节 0～1）。</summary>
    public const int InteractionRunStateRegister = InteractionOffset + 0;

    /// <summary>故障与货物位（字节 2～3）。</summary>
    public const int InteractionFaultRegister = InteractionOffset + 1;

    /// <summary>方向与货物位（字节 4～5）。</summary>
    public const int InteractionCargoRegister = InteractionOffset + 2;

    /// <summary>交互区电量 Int@6。</summary>
    public const int InteractionBatteryRegister = InteractionOffset + 3;

    /// <summary>原点位（字节 8～9）。</summary>
    public const int InteractionOriginRegister = InteractionOffset + 4;

    /// <summary>出入库状态位（字节 10～11）。</summary>
    public const int InteractionTaskStateRegister = InteractionOffset + 5;

    /// <summary>命令区镜像 FILO Bool@0.2 的位序号。</summary>
    public const int InteractionFiloBit = 2;

    /// <summary>命令区镜像 FIFO Bool@0.3 的位序号。</summary>
    public const int InteractionFifoBit = 3;

    /// <summary>状态回读 FIFO状态 Bool@1.2 的位序号。</summary>
    public const int InteractionFifoStateBit = 10;

    /// <summary>状态回读 FILO状态 Bool@1.3 的位序号。</summary>
    public const int InteractionFiloStateBit = 11;

    /// <summary>上方有货 Bool@3.7 的位序号。</summary>
    public const int HasGoodsBit = 15;

    /// <summary>上方无货 Bool@4.0 的位序号。</summary>
    public const int NoGoodsBit = 0;

    /// <summary>入库未完成 Bool@10.0 的位序号。</summary>
    public const int InboundPendingBit = 0;

    /// <summary>出库未完成 Bool@10.1 的位序号。</summary>
    public const int OutboundPendingBit = 1;

    /// <summary>全部协议字段，按状态区、命令区、交互区顺序排列。</summary>
    public static readonly IReadOnlyList<ProtocolField> Fields = BuildFieldTable();

    /// <summary>交互区中按故障处理的字段（2.0～3.6），用于生成报警列表。</summary>
    public static readonly IReadOnlyList<ProtocolField> FaultFields = Fields
        .Where(field => field.Area == InteractionArea && field.BitIndex >= 0)
        .Where(field => field.DbOffset.StartsWith("2.", StringComparison.Ordinal)
                        || (field.DbOffset.StartsWith("3.", StringComparison.Ordinal) && field.Name != "上方有货"))
        .ToArray();

    private const string StatusArea = "车辆→上位机";
    private const string CommandArea = "上位机→车辆";
    private const string InteractionArea = "上位机交互";

    /// <summary>
    /// 向后兼容门面：默认二向穿梭车画像的解码入口。
    /// 新设备应直接依赖 <see cref="IDeviceProtocolProfile"/>，不要扩展这里的静态映射。
    /// </summary>
    public static ShuttleCarSnapshot Decode(IReadOnlyList<ushort> block, int baseAddress, DateTimeOffset updatedAt)
        => TwoWayShuttleProtocolProfile.Instance.Decode(block, baseAddress, updatedAt);

    /// <summary>
    /// 把一次完整轮询块解析成车辆快照。
    /// 状态区不足 20 个寄存器时返回 Error；交互区缺失时只跳过对应字段。
    /// </summary>
    /// <param name="block">从 <paramref name="baseAddress"/> 开始读取的原始寄存器。</param>
    /// <param name="baseAddress">轮询起始地址，用于在数据表中显示真实 Modbus 地址。</param>
    /// <param name="updatedAt">本批数据成功读取的时间。</param>
    internal static ShuttleCarSnapshot DecodeCore(IReadOnlyList<ushort> block, int baseAddress, DateTimeOffset updatedAt)
    {
        if (block.Count < StatusLength)
        {
            return new ShuttleCarSnapshot
            {
                UpdatedAt = updatedAt,
                Quality = DataQuality.Error,
                QualityMessage = $"轮询块只有 {block.Count} 个寄存器，不足状态区 {StatusLength} 个。"
            };
        }

        ushort Word(int registerOffset) => block[registerOffset];
        short Int16(int registerOffset) => ProtocolCodec.ToInt16(block[registerOffset]);
        bool Flag(int registerOffset, int bit) => ProtocolCodec.GetBit(block[registerOffset], bit);
        bool Has(int registerOffset) => block.Count > registerOffset;

        var taskNumber = ProtocolCodec.ToInt32(Word(StatusTaskNumberRegister), Word(StatusTaskNumberRegister + 1));
        var interactionAvailable = Has(InteractionTaskStateRegister);

        var taskCompletedFlag = Flag(StatusFlagsRegister, StatusTaskCompletedBit);
        var atSideA = Flag(StatusFlagsRegister, StatusAtSideABit);
        var atSideB = Flag(StatusFlagsRegister, StatusAtSideBBit);
        var hasGoods = interactionAvailable && Flag(InteractionFaultRegister, HasGoodsBit);
        var noGoods = interactionAvailable && Flag(InteractionCargoRegister, NoGoodsBit);

        // 冲突项保留在固定故障明细之外单独追加，避免把“正常”干扰项混进报警列表。
        var alarms = new List<ShuttleCarAlarm>(FaultFields.Count + 3);
        if (interactionAvailable)
        {
            foreach (var field in FaultFields)
            {
                if (!Has(field.RegisterOffset))
                {
                    continue;
                }

                // Excel 原表标注“未写入”的位仍然解码，但必须明确说明其来源限制。
                alarms.Add(new ShuttleCarAlarm(
                    field.Name,
                    Flag(field.RegisterOffset, field.BitIndex),
                    field.Unwritten ? "未写入" : null));
            }

            if (atSideA && atSideB)
            {
                alarms.Add(new ShuttleCarAlarm("方向状态冲突", true, "在A侧与在B侧同时为真"));
            }

            if (hasGoods && noGoods)
            {
                alarms.Add(new ShuttleCarAlarm("货物状态冲突", true, "上方有货与上方无货同时为真"));
            }

            var fifo = Flag(InteractionRunStateRegister, InteractionFifoBit);
            var filo = Flag(InteractionRunStateRegister, InteractionFiloBit);
            var fifoState = Flag(InteractionRunStateRegister, InteractionFifoStateBit);
            var filoState = Flag(InteractionRunStateRegister, InteractionFiloStateBit);
            if ((fifo && filo)
                || (fifoState && filoState)
                || (fifo && filoState)
                || (filo && fifoState))
            {
                alarms.Add(new ShuttleCarAlarm(
                    "出库方式状态冲突",
                    true,
                    $"FIFO={fifo}, FILO={filo}, FIFO状态={fifoState}, FILO状态={filoState}"));
            }
        }

        return new ShuttleCarSnapshot
        {
            UpdatedAt = updatedAt,
            Quality = DataQuality.Good,
            QualityMessage = interactionAvailable
                ? $"FC03 轮询 {baseAddress} 起 {block.Count} 个寄存器"
                : $"轮询块缺少交互区（{InteractionOffset + InteractionLength} 个寄存器）",
            CarStateRaw = Int16(StatusCarStateRegister),
            CarStateText = $"未确认({Int16(StatusCarStateRegister)})",
            TaskCompletedRaw = Int16(StatusOffset + 1),
            OutboundModeText = FormatEnum(Int16(StatusOffset + 2), "FIFO", "FILO"),
            BodyDirectionText = FormatEnum(Int16(StatusOffset + 3), "A面", "B面"),
            AisleStateText = $"未确认({Int16(StatusOffset + 4)})",
            AlarmCode = Int16(StatusAlarmRegister),
            CargoStateText = $"未确认({Int16(StatusOffset + 7)})",
            InOutStateText = $"未确认({Int16(StatusOffset + 8)})",
            PositionRaw = Int16(StatusOffset + 9),
            BatteryRaw = Int16(StatusOffset + 10),
            CarNumberRaw = Int16(StatusOffset + 11),
            VehicleConnectionStateRaw = Int16(StatusOffset + 12),
            VehicleConnectionText = FormatEnum(Int16(StatusOffset + 12), "未连接", "已连接"),
            TaskNumber = taskNumber,
            CurrentLayer = Int16(StatusOffset + 15),
            CurrentColumn = Int16(StatusOffset + 16),
            CurrentRow = Int16(StatusOffset + 17),
            CurrentAisle = Int16(StatusOffset + 18),
            TaskCompletedFlag = taskCompletedFlag,
            AtSideA = atSideA,
            AtSideB = atSideB,
            InboundPending = interactionAvailable && Flag(InteractionTaskStateRegister, InboundPendingBit),
            OutboundPending = interactionAvailable && Flag(InteractionTaskStateRegister, OutboundPendingBit),
            HasGoods = hasGoods,
            NoGoods = noGoods,
            Alarms = alarms,
            Fields = BuildFieldValues(block, baseAddress, updatedAt)
        };
    }

    /// <summary>读取任务完成候选位（Bool@38.0），供命令握手复用。</summary>
    public static bool IsTaskCompleted(IReadOnlyList<ushort> block)
    {
        return block.Count > StatusFlagsRegister
               && ProtocolCodec.GetBit(block[StatusFlagsRegister], StatusTaskCompletedBit);
    }

    /// <summary>按协议类型格式化一个字段的解析值；未确认枚举统一显示原始值。</summary>
    public static string DecodeValue(ProtocolField field, IReadOnlyList<ushort> block)
    {
        if (field.RegisterOffset >= block.Count)
        {
            return "--";
        }

        if (field.DataType == "Bool")
        {
            return ProtocolCodec.GetBit(block[field.RegisterOffset], field.BitIndex) ? "是" : "否";
        }

        if (field.DataType == "DInt" && field.RegisterOffset + 1 < block.Count)
        {
            return ProtocolCodec
                .ToInt32(block[field.RegisterOffset], block[field.RegisterOffset + 1])
                .ToString(CultureInfo.InvariantCulture);
        }

        var word = ProtocolCodec.ToInt16(block[field.RegisterOffset]);
        return field.Name switch
        {
            "出库方式" => FormatEnum(word, "FIFO", "FILO"),
            "车体方向" => FormatEnum(word, "A面", "B面"),
            "连接状态" => FormatEnum(word, "未连接", "已连接"),
            _ => word.ToString(CultureInfo.InvariantCulture)
        };
    }

    /// <summary>取字段的原始值文本：Bool 显示位值，Int/DInt 显示十六进制寄存器。</summary>
    public static string RawValue(ProtocolField field, IReadOnlyList<ushort> block)
    {
        if (field.RegisterOffset >= block.Count)
        {
            return "--";
        }

        if (field.DataType == "Bool")
        {
            return ProtocolCodec.GetBit(block[field.RegisterOffset], field.BitIndex) ? "1" : "0";
        }

        if (field.DataType == "DInt" && field.RegisterOffset + 1 < block.Count)
        {
            return $"0x{block[field.RegisterOffset]:X4} 0x{block[field.RegisterOffset + 1]:X4}";
        }

        return $"0x{block[field.RegisterOffset]:X4}";
    }

    /// <summary>生成数据表行；轮询块未覆盖的字段不生成行。</summary>
    private static IReadOnlyList<ShuttleCarFieldValue> BuildFieldValues(
        IReadOnlyList<ushort> block,
        int baseAddress,
        DateTimeOffset updatedAt)
    {
        var rows = new List<ShuttleCarFieldValue>(Fields.Count);
        foreach (var field in Fields)
        {
            if (field.RegisterOffset >= block.Count)
            {
                continue;
            }

            rows.Add(new ShuttleCarFieldValue(
                field.Area,
                field.Unwritten ? $"{field.Name}（未写入）" : field.Name,
                field.DbOffset,
                field.BitIndex >= 0
                    ? $"{baseAddress + field.RegisterOffset}.{field.BitIndex}"
                    : (baseAddress + field.RegisterOffset).ToString(CultureInfo.InvariantCulture),
                field.DataType,
                RawValue(field, block),
                DecodeValue(field, block),
                DataQuality.Good,
                updatedAt));
        }

        return rows;
    }

    /// <summary>两值枚举显示：0/1 显示业务名，其它值保留原始数字，不猜测含义。</summary>
    private static string FormatEnum(int value, string zeroText, string oneText)
    {
        return value switch
        {
            0 => $"{zeroText}(0)",
            1 => $"{oneText}(1)",
            _ => $"未确认({value})"
        };
    }

    /// <summary>按方案 7.1～7.3 的字段表声明地址，顺序与数据表显示一致。</summary>
    private static IReadOnlyList<ProtocolField> BuildFieldTable()
    {
        return
        [
            // 车辆→上位机（DB 字节 0～39）
            new(StatusArea, "小车状态", "0", StatusOffset + 0, "Int"),
            new(StatusArea, "任务完成(Int)", "2", StatusOffset + 1, "Int"),
            new(StatusArea, "出库方式", "4", StatusOffset + 2, "Int"),
            new(StatusArea, "车体方向", "6", StatusOffset + 3, "Int"),
            new(StatusArea, "巷道状态", "8", StatusOffset + 4, "Int"),
            new(StatusArea, "备用1", "10", StatusOffset + 5, "Int"),
            new(StatusArea, "报警", "12", StatusOffset + 6, "Int"),
            new(StatusArea, "车上有无货", "14", StatusOffset + 7, "Int"),
            new(StatusArea, "出入库未完成", "16", StatusOffset + 8, "Int"),
            new(StatusArea, "小车位置", "18", StatusOffset + 9, "Int"),
            new(StatusArea, "电量", "20", StatusOffset + 10, "Int"),
            new(StatusArea, "车号", "22", StatusOffset + 11, "Int"),
            new(StatusArea, "连接状态", "24", StatusOffset + 12, "Int"),
            new(StatusArea, "任务号", "26", StatusOffset + 13, "DInt"),
            new(StatusArea, "当前层", "30", StatusOffset + 15, "Int"),
            new(StatusArea, "当前列", "32", StatusOffset + 16, "Int"),
            new(StatusArea, "当前排", "34", StatusOffset + 17, "Int"),
            new(StatusArea, "当前巷道", "36", StatusOffset + 18, "Int"),
            new(StatusArea, "任务完成_1", "38.0", StatusOffset + 19, "Bool", 0),
            new(StatusArea, "在A侧", "38.1", StatusOffset + 19, "Bool", 1),
            new(StatusArea, "在B侧", "38.2", StatusOffset + 19, "Bool", 2),

            // 上位机→车辆（DB 字节 0～21）
            new(CommandArea, "任务类型", "0", CommandOffset + 0, "Int"),
            new(CommandArea, "盘点清零", "2", CommandOffset + 1, "Int"),
            new(CommandArea, "出库方式", "4", CommandOffset + 2, "Int"),
            new(CommandArea, "车体方向", "6", CommandOffset + 3, "Int"),
            new(CommandArea, "备用1", "8", CommandOffset + 4, "Int"),
            new(CommandArea, "初始化", "10", CommandOffset + 5, "Int"),
            new(CommandArea, "任务号", "12", CommandOffset + 6, "DInt"),
            new(CommandArea, "入库后退", "16", CommandOffset + 8, "Int"),
            new(CommandArea, "备用2", "18", CommandOffset + 9, "Int"),
            new(CommandArea, "确认完成", "20.0", CommandOffset + 10, "Bool", 0),
            new(CommandArea, "清除异常", "20.1", CommandOffset + 10, "Bool", 1),

            // 上位机交互（DB 字节 0～11）：字节 0～1 命令镜像与运行状态
            new(InteractionArea, "待机中", "0.0", InteractionRunStateRegister, "Bool", 0),
            new(InteractionArea, "盘点清零", "0.1", InteractionRunStateRegister, "Bool", 1),
            new(InteractionArea, "FILO", "0.2", InteractionRunStateRegister, "Bool", 2),
            new(InteractionArea, "FIFO", "0.3", InteractionRunStateRegister, "Bool", 3),
            new(InteractionArea, "A面", "0.4", InteractionRunStateRegister, "Bool", 4),
            new(InteractionArea, "B面", "0.5", InteractionRunStateRegister, "Bool", 5),
            new(InteractionArea, "初始化", "0.6", InteractionRunStateRegister, "Bool", 6),
            new(InteractionArea, "入库后退", "0.7", InteractionRunStateRegister, "Bool", 7),
            new(InteractionArea, "待机", "1.0", InteractionRunStateRegister, "Bool", 8),
            new(InteractionArea, "任务完成状态", "1.1", InteractionRunStateRegister, "Bool", 9),
            new(InteractionArea, "FIFO状态", "1.2", InteractionRunStateRegister, "Bool", 10),
            new(InteractionArea, "FILO状态", "1.3", InteractionRunStateRegister, "Bool", 11),
            new(InteractionArea, "A面状态", "1.4", InteractionRunStateRegister, "Bool", 12),
            new(InteractionArea, "B面状态", "1.5", InteractionRunStateRegister, "Bool", 13),
            new(InteractionArea, "小车启动完成", "1.6", InteractionRunStateRegister, "Bool", 14),
            new(InteractionArea, "正常", "1.7", InteractionRunStateRegister, "Bool", 15),

            // 字节 2～3：故障、货物和方向
            new(InteractionArea, "前防撞保护", "2.0", InteractionFaultRegister, "Bool", 0, Unwritten: true),
            new(InteractionArea, "后防撞保护", "2.1", InteractionFaultRegister, "Bool", 1, Unwritten: true),
            new(InteractionArea, "顶升超时", "2.2", InteractionFaultRegister, "Bool", 2),
            new(InteractionArea, "行走超时", "2.3", InteractionFaultRegister, "Bool", 3),
            new(InteractionArea, "前减速故障", "2.4", InteractionFaultRegister, "Bool", 4),
            new(InteractionArea, "后减速故障", "2.5", InteractionFaultRegister, "Bool", 5),
            new(InteractionArea, "前向上故障", "2.6", InteractionFaultRegister, "Bool", 6),
            new(InteractionArea, "后向上故障", "2.7", InteractionFaultRegister, "Bool", 7),
            new(InteractionArea, "接近开关故障", "3.0", InteractionFaultRegister, "Bool", 8),
            new(InteractionArea, "行走堵转故障", "3.1", InteractionFaultRegister, "Bool", 9, Unwritten: true),
            new(InteractionArea, "A侧急停故障", "3.2", InteractionFaultRegister, "Bool", 10),
            new(InteractionArea, "B侧急停故障", "3.3", InteractionFaultRegister, "Bool", 11),
            new(InteractionArea, "行走电机故障", "3.4", InteractionFaultRegister, "Bool", 12),
            new(InteractionArea, "顶升电机故障", "3.5", InteractionFaultRegister, "Bool", 13),
            new(InteractionArea, "电量不足", "3.6", InteractionFaultRegister, "Bool", 14),
            new(InteractionArea, "上方有货", "3.7", InteractionFaultRegister, "Bool", 15),

            // 字节 4～5：方向与货物
            new(InteractionArea, "上方无货", "4.0", InteractionCargoRegister, "Bool", 0),
            new(InteractionArea, "在A侧", "4.1", InteractionCargoRegister, "Bool", 1),
            new(InteractionArea, "在B侧", "4.2", InteractionCargoRegister, "Bool", 2),

            // 字节 6～11：电量、原点和出入库状态
            new(InteractionArea, "电量", "6", InteractionBatteryRegister, "Int"),
            new(InteractionArea, "在A侧原点", "8.0", InteractionOriginRegister, "Bool", 0),
            new(InteractionArea, "在B侧原点", "8.1", InteractionOriginRegister, "Bool", 1),
            new(InteractionArea, "入库未完成", "10.0", InteractionTaskStateRegister, "Bool", 0),
            new(InteractionArea, "出库未完成", "10.1", InteractionTaskStateRegister, "Bool", 1),
            new(InteractionArea, "出库无托盘", "10.2", InteractionTaskStateRegister, "Bool", 2),
            new(InteractionArea, "入库巷道满", "10.3", InteractionTaskStateRegister, "Bool", 3)
        ];
    }
}

/// <summary>
/// 一条协议字段的地址定义。
/// </summary>
/// <param name="Area">数据区名称，与数据表“数据区”列一致。</param>
/// <param name="Name">字段名称。</param>
/// <param name="DbOffset">Excel 中的 DB 偏移，例如 26 或 38.1。</param>
/// <param name="RegisterOffset">该字段在轮询块中的寄存器下标。</param>
/// <param name="DataType">Int、DInt 或 Bool。</param>
/// <param name="BitIndex">Bool 字段在寄存器中的位序号；非 Bool 为 -1。</param>
/// <param name="Unwritten">
/// Excel 原表是否标注该位“未写入”。为 true 时报警明细必须附带“未写入”说明，
/// 但仿真从站仍可读写该位，便于联调时验证上位机的显示行为。
/// </param>
public sealed record ProtocolField(
    string Area,
    string Name,
    string DbOffset,
    int RegisterOffset,
    string DataType,
    int BitIndex = -1,
    bool Unwritten = false);
