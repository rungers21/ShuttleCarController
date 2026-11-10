using System;
using System.Globalization;

namespace ShuttleCarController.Models;

/// <summary>
/// 界面日志条目，同时用于 ObservableCollection 展示和文本文件落盘。
/// 该对象创建后不再修改，便于跨线程传递。
/// </summary>
/// <param name="Timestamp">事件发生时的本地时间戳，保留日期和时区偏移。</param>
/// <param name="Level">日志严重程度。</param>
/// <param name="Category">日志来源分类。</param>
/// <param name="Message">简短、稳定的事件摘要。</param>
/// <param name="Detail">可选的地址、任务号、异常堆栈等详细上下文。</param>
public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    LogCategory Category,
    string Message,
    string? Detail = null)
{
    /// <summary>日志表格使用的时间列，精确到毫秒。</summary>
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>将日志级别转换为界面中文文案。</summary>
    public string LevelText => Level switch
    {
        LogLevel.Trace => "跟踪",
        LogLevel.Info => "信息",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => Level.ToString()
    };

    /// <summary>将日志分类转换为界面中文文案。</summary>
    public string CategoryText => Category switch
    {
        LogCategory.System => "系统",
        LogCategory.Connection => "连接",
        LogCategory.Read => "读取",
        LogCategory.Write => "写入",
        LogCategory.Command => "命令",
        LogCategory.Alarm => "报警",
        _ => Category.ToString()
    };
}
