using System;
using System.IO;
using System.Text;
using ShuttleCarController.Models;

namespace ShuttleCarController.Services;

/// <summary>
/// 轻量日志实现：界面事件通知与本地文本日志共用同一条 LogEntry。
/// 文件按天拆分并保留最近七天，成功周期读取不在这里逐条记录，避免日志无界增长。
/// </summary>
public sealed class LogService : ILogService
{
    /// <summary>日志文件保留天数。</summary>
    private const int RetentionDays = 7;

    /// <summary>
    /// 文件写入锁。
    /// 通信线程、命令线程和 UI 线程可能同时写日志，必须保证单次追加不会相互穿插。
    /// </summary>
    private readonly object _syncRoot = new();

    /// <summary>当前用户的日志目录。</summary>
    private readonly string _logDirectory;

    /// <summary>创建日志目录，并在启动时清理过期文件。</summary>
    public LogService()
    {
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShuttleCarController",
            "Logs");

        try
        {
            Directory.CreateDirectory(_logDirectory);
            DeleteExpiredLogs();
        }
        catch
        {
            // 日志目录不可写时界面仍可运行；文件异常不能阻止主程序启动。
        }
    }

    /// <summary>
    /// 新日志事件。
    /// 文件追加完成后触发；调用方可能位于后台线程，UI 订阅者必须切换线程。
    /// </summary>
    public event EventHandler<LogEntry>? EntryWritten;

    /// <summary>日志文件所在目录，供诊断或界面提示使用。</summary>
    public string LogDirectory => _logDirectory;

    /// <summary>
    /// 写入一条日志并通知界面。
    /// 文件操作使用锁保证线程安全；事件在锁外逐个调用，避免订阅者造成文件写入死锁。
    /// </summary>
    public void Write(LogLevel level, LogCategory category, string message, string? detail = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, message, detail);

        try
        {
            lock (_syncRoot)
            {
                Directory.CreateDirectory(_logDirectory);
                var filePath = Path.Combine(_logDirectory, $"{DateTime.Now:yyyy-MM-dd}.log");
                var line = new StringBuilder()
                    .Append(entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" | ").Append(level)
                    .Append(" | ").Append(category)
                    .Append(" | ").Append(message);

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    // 详细异常可能包含多行堆栈，写入单行便于后续检索和按行解析。
                    line.Append(" | ").Append(detail.ReplaceLineEndings(" "));
                }

                File.AppendAllText(filePath, line.AppendLine().ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // 文件落盘失败不应中断通信或界面操作；界面事件仍会继续发送。
        }

        var handlers = EntryWritten;
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<LogEntry> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, entry);
            }
            catch
            {
                // 单个订阅者异常不能影响其他订阅者和业务线程。
            }
        }
    }

    /// <summary>写入普通信息日志。</summary>
    public void Info(LogCategory category, string message, string? detail = null)
    {
        Write(LogLevel.Info, category, message, detail);
    }

    /// <summary>写入警告日志。</summary>
    public void Warning(LogCategory category, string message, string? detail = null)
    {
        Write(LogLevel.Warning, category, message, detail);
    }

    /// <summary>写入错误日志。</summary>
    public void Error(LogCategory category, string message, string? detail = null)
    {
        Write(LogLevel.Error, category, message, detail);
    }

    /// <summary>
    /// 删除超过保留期的 .log 文件。
    /// 只处理当前应用日志目录，不递归删除其他目录或文件类型。
    /// </summary>
    private void DeleteExpiredLogs()
    {
        var threshold = DateTime.Now.Date.AddDays(-RetentionDays);
        foreach (var filePath in Directory.EnumerateFiles(_logDirectory, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(filePath) < threshold)
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
                // 单个旧日志被占用或无权限删除，不影响其他日志和程序启动。
            }
        }
    }
}
