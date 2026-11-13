using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShuttleCarController.Models;

/// <summary>
/// 一次完整的穿梭车状态快照。
/// 通信实现应在后台完成寄存器读取和协议转换，再以不可变快照的形式通知 UI；
/// 这样可避免界面读取到“半新半旧”的车辆状态。
/// 原始数值用于诊断，文本值由适配层按已确认协议生成。
/// </summary>
public sealed record ShuttleCarSnapshot
{
    /// <summary>
    /// 应用启动时使用的空快照。
    /// 其数据质量为 Unknown，因此所有依赖有效车辆状态的控制按钮都会保持禁用。
    /// </summary>
    public static ShuttleCarSnapshot Empty { get; } = new()
    {
        QualityMessage = "尚未读取车辆数据"
    };

    /// <summary>
    /// 本快照成功生成的时间。
    /// 数据新鲜度使用该时间判断，而不是使用界面收到事件的时间，避免排队延迟被隐藏。
    /// </summary>
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>整批数据的可信程度，决定界面能否开放业务控制。</summary>
    public DataQuality Quality { get; init; } = DataQuality.Unknown;

    /// <summary>对数据质量的补充说明，例如“通信中断”或“地址未映射”。</summary>
    public string QualityMessage { get; init; } = string.Empty;

    /// <summary>小车状态原始值，对应“穿梭车发送上位机”Int@0；枚举含义待最终协议确认。</summary>
    public int? CarStateRaw { get; init; }

    /// <summary>由协议适配层生成的小车状态文本；未接入或未知时显示占位符。</summary>
    public string CarStateText { get; init; } = "--";

    /// <summary>
    /// 任务完成整数值，对应 Int@2。
    /// 它与 Bool@38.0 的 TaskCompletedFlag 分开保存，不在未确认协议前混用。
    /// </summary>
    public int? TaskCompletedRaw { get; init; }

    /// <summary>出库方式解析文本，对应 Int@4；FIFO/FILO 数值编码由适配层确认。</summary>
    public string OutboundModeText { get; init; } = "--";

    /// <summary>车体方向解析文本，对应 Int@6；A/B 面数值编码由适配层确认。</summary>
    public string BodyDirectionText { get; init; } = "--";

    /// <summary>巷道状态解析文本，对应 Int@8；可能是枚举、位掩码或状态码。</summary>
    public string AisleStateText { get; init; } = "--";

    /// <summary>
    /// 总报警原始值，对应 Int@12。
    /// 非零时按保守策略视为存在报警，即使尚无对应的故障位说明。
    /// </summary>
    public int AlarmCode { get; init; }

    /// <summary>车上有无货解析文本，对应 Int@14。</summary>
    public string CargoStateText { get; init; } = "--";

    /// <summary>出入库未完成状态解析文本，对应 Int@16。</summary>
    public string InOutStateText { get; init; } = "--";

    /// <summary>小车位置原始值，对应 Int@18；协议未确认前不添加长度单位或比例。</summary>
    public int? PositionRaw { get; init; }

    /// <summary>电量原始值，对应 Int@20；协议未确认前不擅自显示百分比。</summary>
    public int? BatteryRaw { get; init; }

    /// <summary>车号原始值，对应 Int@22，用于核对当前监控对象。</summary>
    public int? CarNumberRaw { get; init; }

    /// <summary>
    /// 车辆侧连接状态原始值，对应 Int@24。
    /// 该值来自 PLC，不等同于本机 Modbus 链路是否连通。
    /// </summary>
    public int? VehicleConnectionStateRaw { get; init; }

    /// <summary>车辆侧连接状态的解析文本。</summary>
    public string VehicleConnectionText { get; init; } = "--";

    /// <summary>
    /// 车辆当前任务号，对应 DInt@26。
    /// 上位机下发任务后应核对该值，避免把其他任务的完成状态误认为本任务完成。
    /// </summary>
    public int? TaskNumber { get; init; }

    /// <summary>当前层，对应 Int@30。</summary>
    public int? CurrentLayer { get; init; }

    /// <summary>当前列，对应 Int@32。</summary>
    public int? CurrentColumn { get; init; }

    /// <summary>当前排，对应 Int@34。</summary>
    public int? CurrentRow { get; init; }

    /// <summary>当前巷道，对应 Int@36。</summary>
    public int? CurrentAisle { get; init; }

    /// <summary>任务完成 Bool 状态，对应 Bool@38.0，是完成握手候选字段。</summary>
    public bool TaskCompletedFlag { get; init; }

    /// <summary>车辆是否在 A 侧，对应 Bool@38.1。</summary>
    public bool AtSideA { get; init; }

    /// <summary>车辆是否在 B 侧，对应 Bool@38.2。</summary>
    public bool AtSideB { get; init; }

    /// <summary>入库未完成，对应“上位机交互”Bool@10.0。</summary>
    public bool InboundPending { get; init; }

    /// <summary>出库未完成，对应“上位机交互”Bool@10.1。</summary>
    public bool OutboundPending { get; init; }

    /// <summary>上方有货，对应“上位机交互”Bool@3.7。</summary>
    public bool HasGoods { get; init; }

    /// <summary>
    /// 上方无货，对应“上位机交互”Bool@4.0。
    /// HasGoods 与 NoGoods 同时为 true 属于状态冲突，适配层应明确标记。
    /// </summary>
    public bool NoGoods { get; init; }

    /// <summary>
    /// 已解析的故障明细。
    /// 适配层可只提供当前需要展示的项目；总报警码仍独立保留，避免漏报未知故障。
    /// </summary>
    public IReadOnlyList<ShuttleCarAlarm> Alarms { get; init; } = Array.Empty<ShuttleCarAlarm>();

    /// <summary>
    /// 数据表使用的完整字段列表。
    /// 每项同时包含 DB 偏移、Modbus 地址、原始值、解析值、质量和更新时间。
    /// </summary>
    public IReadOnlyList<ShuttleCarFieldValue> Fields { get; init; } = Array.Empty<ShuttleCarFieldValue>();

    /// <summary>
    /// 是否存在需要保守处理的报警。
    /// 只要总报警码非零或任一故障明细有效就返回 true。
    /// </summary>
    public bool HasAlarm => AlarmCode != 0 || Alarms.Any(alarm => alarm.IsActive);

    /// <summary>当前有效故障明细数量，不包含仅由未知总报警码表示的故障。</summary>
    public int ActiveAlarmCount => Alarms.Count(alarm => alarm.IsActive);

    /// <summary>快照时间在本机时区下的显示文本，精确到毫秒。</summary>
    public string UpdatedAtText => UpdatedAt == default
        ? "--"
        : UpdatedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>数据质量的中文显示文本。</summary>
    public string QualityText => Quality switch
    {
        DataQuality.Good => "正常",
        DataQuality.Stale => "已过期",
        DataQuality.Error => "读取错误",
        DataQuality.NotMapped => "未映射",
        _ => "未知"
    };

    /// <summary>任务号显示文本；null 和 0 都视为当前没有可展示的有效任务号。</summary>
    public string TaskNumberText => TaskNumber is null or 0
        ? "--"
        : TaskNumber.Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>电量原始值显示文本，不附加未经协议确认的单位。</summary>
    public string BatteryText => BatteryRaw?.ToString(CultureInfo.InvariantCulture) ?? "--";

    /// <summary>小车位置原始值显示文本。</summary>
    public string PositionText => PositionRaw?.ToString(CultureInfo.InvariantCulture) ?? "--";

    /// <summary>车号显示文本。</summary>
    public string CarNumberText => CarNumberRaw?.ToString(CultureInfo.InvariantCulture) ?? "--";

    /// <summary>
    /// 参数解析区的报警摘要。
    /// 同时呈现总报警码和已知故障数量，避免只显示其中一种信息。
    /// </summary>
    public string AlarmText => HasAlarm
        ? AlarmCode == 0
            ? $"故障 {ActiveAlarmCount} 项"
            : $"报警码 {AlarmCode}，故障 {ActiveAlarmCount} 项"
        : "无";

    /// <summary>Bool@38.0 对应的中文完成状态。</summary>
    public string TaskCompletedText => TaskCompletedFlag ? "已完成" : "未完成";

    /// <summary>A 侧状态的中文显示。</summary>
    public string AtSideAText => AtSideA ? "是" : "否";

    /// <summary>B 侧状态的中文显示。</summary>
    public string AtSideBText => AtSideB ? "是" : "否";

    /// <summary>根据“上方有货”位生成的简单文本；完整冲突判断由适配层负责。</summary>
    public string HasGoodsText => HasGoods ? "有货" : "无货";

    /// <summary>根据“上方无货”位生成的简单文本；完整冲突判断由适配层负责。</summary>
    public string NoGoodsText => NoGoods ? "无货" : "有货";

    /// <summary>
    /// 交互区“上方有货/上方无货”两个位的一致化显示。
    /// 同时为真表示协议状态冲突，同时为假表示货物状态未确定，均不能猜测为“正常”。
    /// </summary>
    public string CargoPresenceText => (HasGoods, NoGoods) switch
    {
        (true, true) => "状态冲突（有货/无货同时为真）",
        (false, false) => "未确定",
        (true, false) => "有货",
        _ => "无货"
    };

    /// <summary>当前层显示文本。</summary>
    public string PositionLayerText => CurrentLayer?.ToString(CultureInfo.InvariantCulture) ?? "--";

    /// <summary>当前列显示文本。</summary>
    public string PositionColumnText => CurrentColumn?.ToString(CultureInfo.InvariantCulture) ?? "--";

    /// <summary>当前排显示文本。</summary>
    public string PositionRowText => CurrentRow?.ToString(CultureInfo.InvariantCulture) ?? "--";

    /// <summary>当前巷道显示文本。</summary>
    public string PositionAisleText => CurrentAisle?.ToString(CultureInfo.InvariantCulture) ?? "--";
}

/// <summary>一条已解析的车辆故障或报警状态。</summary>
/// <param name="Name">故障名称，例如“顶升超时”或“A侧急停故障”。</param>
/// <param name="IsActive">该故障当前是否有效。</param>
/// <param name="Detail">可选的协议备注、故障码或“未写入”等接入说明。</param>
public sealed record ShuttleCarAlarm(string Name, bool IsActive, string? Detail = null)
{
    /// <summary>故障状态的中文显示。</summary>
    public string ActiveText => IsActive ? "故障" : "正常";

    /// <summary>将故障名称和可选说明组合为界面显示文本。</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Detail) ? Name : $"{Name}（{Detail}）";
}

/// <summary>
/// 数据表中的一行协议字段，既展示业务解析结果，也保留诊断所需的原始信息。
/// </summary>
/// <param name="Area">逻辑数据区，例如“车辆→上位机”或“上位机交互”。</param>
/// <param name="Name">协议字段名称。</param>
/// <param name="DbOffset">Excel 中的 Siemens DB 偏移，例如 26 或 38.1。</param>
/// <param name="ModbusAddress">已确认的 Modbus 地址；未确认时可显示“待配置”。</param>
/// <param name="DataType">协议类型，例如 Int、DInt 或 Bool。</param>
/// <param name="RawValue">寄存器或位的原始值，建议使用十六进制以便联调。</param>
/// <param name="DecodedValue">按最终协议解析后的业务值。</param>
/// <param name="Quality">该字段的数据质量。</param>
/// <param name="UpdatedAt">该字段最近一次成功更新的时间。</param>
public sealed record ShuttleCarFieldValue(
    string Area,
    string Name,
    string DbOffset,
    string ModbusAddress,
    string DataType,
    string RawValue,
    string DecodedValue,
    DataQuality Quality,
    DateTimeOffset UpdatedAt)
{
    /// <summary>字段数据质量的中文短文本。</summary>
    public string QualityText => Quality switch
    {
        DataQuality.Good => "正常",
        DataQuality.Stale => "过期",
        DataQuality.Error => "错误",
        DataQuality.NotMapped => "未映射",
        _ => "未知"
    };

    /// <summary>字段更新时间，精确到毫秒。</summary>
    public string UpdatedAtText => UpdatedAt == default
        ? "--"
        : UpdatedAt.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
