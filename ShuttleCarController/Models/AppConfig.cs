namespace ShuttleCarController.Models;

/// <summary>
/// 应用程序持久化配置根对象。
/// 仅保存连接偏好，不保存“已连接”状态或未完成任务，防止重启后自动恢复危险操作。
/// </summary>
public sealed class AppConfig
{
    /// <summary>配置结构版本，后续字段发生不兼容变化时用于迁移。</summary>
    public int Version { get; set; } = 1;

    /// <summary>最近一次使用的 Modbus 连接与轮询参数。</summary>
    public ConnectionSettings Connection { get; set; } = new();
}
