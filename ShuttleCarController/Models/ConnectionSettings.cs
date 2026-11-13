namespace ShuttleCarController.Models;

/// <summary>
/// Modbus 连接及调度参数。
/// 该类是可变的配置对象，便于 WPF 直接双向绑定；传给通信实现前应通过 Clone 创建快照。
/// </summary>
public sealed class ConnectionSettings
{
    /// <summary>
    /// 穿梭车或 Modbus 网关的 IPv4 地址。
    /// 默认留空是为了避免误连现场设备，由用户按实际网络填写。
    /// </summary>
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>Modbus TCP 端口，标准默认值为 502，有效范围为 1～65535。</summary>
    public int Port { get; set; } = 502;

    /// <summary>
    /// Modbus 站号/Unit Id。
    /// 某些 TCP 网关会忽略该值，而串口服务器通常用它选择下游设备。
    /// </summary>
    public byte UnitId { get; set; } = 1;

    /// <summary>单次 Modbus 请求的超时时间，单位毫秒；默认 1000ms。</summary>
    public int RequestTimeoutMs { get; set; } = 1000;

    /// <summary>
    /// 状态轮询周期，单位毫秒；默认 500ms。
    /// 该值同时参与数据过期判断，修改后应及时保存并应用到轮询器。
    /// </summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>通信异常后的自动重连间隔，单位毫秒；默认 3000ms。</summary>
    public int ReconnectIntervalMs { get; set; } = 3000;

    /// <summary>
    /// 状态轮询使用的起始寄存器地址（FC03 保持寄存器）。
    /// 默认 0 只代表一块可配置的观察区，不代表已确认的协议地址。
    /// </summary>
    public int PollStartAddress { get; set; }

    /// <summary>
    /// 状态轮询读取的寄存器数量，默认覆盖 ProtocolMap 的全部三个数据区（38 个寄存器）。
    /// NModbus/Modbus 协议允许范围为 1～125；小于 38 时交互区字段显示为缺失。
    /// </summary>
    public int PollQuantity { get; set; } = 38;

    /// <summary>
    /// 复制当前配置，防止通信后台读取配置时被界面编辑操作并发修改。
    /// </summary>
    public ConnectionSettings Clone()
    {
        return (ConnectionSettings)MemberwiseClone();
    }
}
