using System;
using ShuttleCarController.Models;

namespace ShuttleCarController.Services;

/// <summary>
/// 应用日志抽象。
/// 通信、命令和界面代码只依赖该接口，不直接操作日志文件或 ObservableCollection。
/// </summary>
public interface ILogService
{
    /// <summary>
    /// 新日志产生事件，供 ViewModel 将日志追加到界面。
    /// 事件可能由后台线程触发，订阅者需要自行切换到 UI 线程。
    /// </summary>
    event EventHandler<LogEntry>? EntryWritten;

    /// <summary>
    /// 写入一条完整日志。
    /// 实现负责文本落盘和事件通知，日志写入失败不应中断车辆业务操作。
    /// </summary>
    void Write(LogLevel level, LogCategory category, string message, string? detail = null);

    /// <summary>写入普通信息日志。</summary>
    void Info(LogCategory category, string message, string? detail = null);

    /// <summary>写入警告日志，例如结果未知、配置损坏或报警产生。</summary>
    void Warning(LogCategory category, string message, string? detail = null);

    /// <summary>写入错误日志，并可在 detail 中附带异常堆栈。</summary>
    void Error(LogCategory category, string message, string? detail = null);
}
