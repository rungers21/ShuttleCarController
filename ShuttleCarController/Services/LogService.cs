using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ShuttleCarController.Models;

namespace ShuttleCarController.Services;

/// <summary>
/// 轻量日志实现：界面事件通知与本地文本日志共用同一条 LogEntry。
/// 文件按天拆分并保留最近七天，成功周期读取不在这里逐条记录，避免日志无界增长。
/// 落盘由后台单写线程批量完成，调用线程（通信/命令/UI）不再等待磁盘 I/O。
/// </summary>
public sealed class LogService : ILogService, IDisposable
{
    /// <summary>日志文件保留天数。</summary>
    private const int RetentionDays = 7;

    /// <summary>内存队列容量；写盘严重滞后时丢弃最旧日志，保证业务线程永不阻塞。</summary>
    private const int QueueCapacity = 8192;

    /// <summary>单批最多写盘条数，避免一次持锁过久。</summary>
    private const int BatchSize = 128;

    /// <summary>当前用户的日志目录。</summary>
    private readonly string _logDirectory;

    /// <summary>待落盘日志队列；单读者由后台写线程独占。</summary>
    private readonly Channel<LogEntry> _queue;

    /// <summary>后台写线程；仅由 Dispose 等待。</summary>
    private readonly Task _writerTask;

    /// <summary>当前打开的日志流及其对应日期，跨天时重新打开。</summary>
    private StreamWriter? _writer;
    private DateOnly _writerDate;

    /// <summary>后台写线程是否已释放流。</summary>
    private bool _disposed;

    /// <summary>创建日志目录，并在启动时清理过期文件。</summary>
    public LogService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShuttleCarController",
            "Logs"))
    {
    }

    /// <summary>测试和诊断场景使用指定目录，避免读写当前用户的真实日志。</summary>
    internal LogService(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _logDirectory = logDirectory;

        try
        {
            Directory.CreateDirectory(_logDirectory);
            DeleteExpiredLogs();
        }
        catch
        {
            // 日志目录不可写时界面仍可运行；文件异常不能阻止主程序启动。
        }

        _queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _writerTask = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// 新日志事件。
    /// 在调用 Write 的线程上同步触发；调用方可能位于后台线程，UI 订阅者必须切换线程。
    /// </summary>
    public event EventHandler<LogEntry>? EntryWritten;

    /// <summary>日志文件所在目录，供诊断或界面提示使用。</summary>
    public string LogDirectory => _logDirectory;

    /// <summary>
    /// 写入一条日志并通知界面。
    /// 入队后立即返回，不执行目录创建或文件追加；订阅者异常不会影响业务线程。
    /// </summary>
    public void Write(LogLevel level, LogCategory category, string message, string? detail = null)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, category, message, detail);

        // 队列已满时 DropOldest 会淘汰最旧记录，TryWrite 仍然成功；
        // 仅在 Dispose 完成后可能返回 false，此时静默丢弃。
        _queue.Writer.TryWrite(entry);

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
    /// 后台单写循环：批量取队列、按天打开流、写完后刷新。
    /// 队列空时刷新一次，保证程序空闲时日志已落盘。
    /// </summary>
    private async Task WriteLoopAsync()
    {
        var batch = new List<LogEntry>(BatchSize);

        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                batch.Clear();
                while (batch.Count < BatchSize && _queue.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                WriteBatch(batch);
                _writer?.Flush();
            }
        }
        catch
        {
            // 磁盘故障只影响文件日志，界面事件已在 Write 中发送。
        }
        finally
        {
            try
            {
                while (_queue.Reader.TryRead(out var entry))
                {
                    AppendLine(entry);
                }

                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // 退出阶段不再向外抛异常。
            }

            _writer = null;
        }
    }

    private void WriteBatch(List<LogEntry> batch)
    {
        foreach (var entry in batch)
        {
            AppendLine(entry);
        }
    }

    /// <summary>把单条日志追加到当天的流；仅在跨天或首次写入时打开文件。</summary>
    private void AppendLine(LogEntry entry)
    {
        var date = DateOnly.FromDateTime(entry.Timestamp.ToLocalTime().DateTime);
        if (_writer is null || _writerDate != date)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;

            Directory.CreateDirectory(_logDirectory);
            var filePath = Path.Combine(_logDirectory, $"{date:yyyy-MM-dd}.log");
            _writer = new StreamWriter(
                new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _writerDate = date;
        }

        var line = new StringBuilder()
            .Append(entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" | ").Append(entry.Level)
            .Append(" | ").Append(entry.Category)
            .Append(" | ").Append(entry.Message);

        if (!string.IsNullOrWhiteSpace(entry.Detail))
        {
            // 详细异常可能包含多行堆栈，写入单行便于后续检索和按行解析。
            line.Append(" | ").Append(entry.Detail.ReplaceLineEndings(" "));
        }

        _writer.WriteLine(line.ToString());
    }

    /// <summary>
    /// 停止接收新日志，等待后台队列清空并关闭文件流。
    /// 程序退出时必须调用，否则最后几条日志可能留在内存中。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.Writer.TryComplete();

        try
        {
            // 正常情况下队列很快清空；超时保护避免退出流程被磁盘故障无限挂起。
            _writerTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // 退出阶段忽略等待异常。
        }
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
