using System.IO;
using ShuttleCarController.Models;
using ShuttleCarController.Services;

namespace ShuttleCarController.Tests;

/// <summary>日志后台写队列测试：不阻塞调用线程、退出时落盘、事件同步通知。</summary>
public sealed class LogServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "shuttlecar-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Write_RaisesEntryWrittenSynchronously()
    {
        using var service = new LogService(_directory);
        LogEntry? received = null;
        service.EntryWritten += (_, entry) => received = entry;

        service.Warning(LogCategory.Connection, "测试警告", "细节");

        Assert.NotNull(received);
        Assert.Equal("测试警告", received.Message);
        Assert.Equal(LogLevel.Warning, received.Level);
        Assert.Equal("细节", received.Detail);
    }

    [Fact]
    public void Dispose_FlushesAllQueuedEntriesToDailyFile()
    {
        var service = new LogService(_directory);
        for (var i = 0; i < 500; i++)
        {
            service.Info(LogCategory.System, $"第 {i} 条", new string('x', 50));
        }

        service.Dispose();

        var file = Assert.Single(Directory.GetFiles(_directory, "*.log"));
        var lines = File.ReadAllLines(file);
        Assert.Equal(500, lines.Length);
        Assert.Contains(lines, line => line.Contains("第 499 条", StringComparison.Ordinal));
        Assert.Contains(" | Info | System | 第 0 条 | ", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var service = new LogService(_directory);
        service.Info(LogCategory.System, "一次性日志");

        service.Dispose();
        service.Dispose();

        Assert.Single(Directory.GetFiles(_directory, "*.log"));
    }

    [Fact]
    public void Write_AfterDispose_DoesNotThrow()
    {
        var service = new LogService(_directory);
        service.Dispose();

        service.Error(LogCategory.System, "退出后日志");
    }

    [Fact]
    public void MultiLineDetail_IsWrittenAsSingleLine()
    {
        var service = new LogService(_directory);
        service.Error(LogCategory.System, "异常", "第一行\r\n第二行");
        service.Dispose();

        var lines = File.ReadAllLines(Assert.Single(Directory.GetFiles(_directory, "*.log")));
        Assert.Single(lines);
        Assert.Contains("第一行 第二行", lines[0], StringComparison.Ordinal);
    }
}
