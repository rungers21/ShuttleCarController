using System.IO;
using ShuttleCarController.Models;
using ShuttleCarController.Services;
using ShuttleCarController.ViewModels;

namespace ShuttleCarController.Tests;

/// <summary>主 ViewModel 的联锁、筛选与本地任务重置测试（不启动 WPF 窗口）。</summary>
public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "shuttlecar-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 测试临时目录清理失败不影响断言结果。
        }
    }

    private MainWindowViewModel CreateViewModel(
        FakeShuttleCarClient client,
        LogService logService,
        Func<string, bool>? confirm = null)
    {
        var vm = new MainWindowViewModel(
            new AppConfigStore(Path.Combine(_root, "config")),
            logService,
            client,
            confirm ?? (_ => true));
        vm.Settings.IpAddress = "127.0.0.1";
        return vm;
    }

    private LogService CreateLogService() => new(Path.Combine(_root, "logs"));

    private static ShuttleCarSnapshot FreshSnapshot(
        Action<ushort[]>? mutate = null,
        DateTimeOffset? updatedAt = null)
    {
        var block = new ushort[ProtocolMap.TotalLength];
        mutate?.Invoke(block);
        return ProtocolMap.Decode(block, 0, updatedAt ?? DateTimeOffset.Now);
    }

    [Fact]
    public void StartTask_Enabled_WhenConnectedFreshAndIdle()
    {
        using var log = CreateLogService();
        var client = new FakeShuttleCarClient();
        using var vm = CreateViewModel(client, log);

        vm.ConnectCommand.Execute();
        client.Publish(FreshSnapshot());

        Assert.True(vm.StartTaskCommand.CanExecute());
    }

    [Fact]
    public void StartTask_Disabled_WhenDataStale()
    {
        using var log = CreateLogService();
        var client = new FakeShuttleCarClient();
        using var vm = CreateViewModel(client, log);

        vm.ConnectCommand.Execute();
        client.Publish(FreshSnapshot(updatedAt: DateTimeOffset.Now.AddSeconds(-10)));

        Assert.False(vm.DataIsFresh);
        Assert.False(vm.StartTaskCommand.CanExecute());
    }

    [Fact]
    public void StartTask_Disabled_WhenActiveAlarm()
    {
        using var log = CreateLogService();
        var client = new FakeShuttleCarClient();
        using var vm = CreateViewModel(client, log);

        vm.ConnectCommand.Execute();
        client.Publish(FreshSnapshot(block =>
            block[ProtocolMap.InteractionFaultRegister] =
                ProtocolCodec.SetBit(block[ProtocolMap.InteractionFaultRegister], 2, true)));

        Assert.True(vm.Snapshot.HasAlarm);
        Assert.False(vm.StartTaskCommand.CanExecute());
    }

    [Fact]
    public void ActiveAlarms_OnlyProjectsActiveFaults()
    {
        using var log = CreateLogService();
        var client = new FakeShuttleCarClient();
        using var vm = CreateViewModel(client, log);

        client.Publish(FreshSnapshot(block =>
            block[ProtocolMap.InteractionFaultRegister] =
                ProtocolCodec.SetBit(block[ProtocolMap.InteractionFaultRegister], 2, true)));

        var alarm = Assert.Single(vm.ActiveAlarms);
        Assert.True(alarm.IsActive);
        Assert.True(vm.HasActiveAlarm);

        client.Publish(FreshSnapshot());
        Assert.Empty(vm.ActiveAlarms);
        Assert.False(vm.HasActiveAlarm);
    }

    [Fact]
    public void FilteredFields_AppliesKeywordAreaZeroAndReservedFilters()
    {
        using var log = CreateLogService();
        var client = new FakeShuttleCarClient();
        using var vm = CreateViewModel(client, log);

        client.Publish(FreshSnapshot(block => block[ProtocolMap.InteractionBatteryRegister] = 88));
        var total = vm.FilteredFields.Count;
        Assert.True(total > 0);

        vm.FieldFilterText = "电量";
        Assert.NotEmpty(vm.FilteredFields);
        Assert.All(vm.FilteredFields, row => Assert.Contains("电量", row.Name, StringComparison.Ordinal));

        vm.FieldFilterText = string.Empty;
        vm.SelectedFieldArea = "上位机交互";
        Assert.All(vm.FilteredFields, row => Assert.Equal("上位机交互", row.Area));

        vm.SelectedFieldArea = "全部";
        vm.OnlyNonZeroFields = true;
        Assert.NotEmpty(vm.FilteredFields);
        Assert.All(vm.FilteredFields, row => Assert.NotEqual("0x0000", row.RawValue));
        Assert.Contains(vm.FilteredFields, row => row.Name == "电量" && row.RawValue == "0x0058");

        vm.OnlyNonZeroFields = false;
        vm.HideReservedFields = true;
        Assert.DoesNotContain(vm.FilteredFields, row => row.Name.Contains("备用", StringComparison.Ordinal));
        Assert.True(vm.FilteredFields.Count < total);
    }

    [Fact]
    public void FilteredLogs_AppliesLevelFilter()
    {
        using var log = CreateLogService();
        var client = new FakeShuttleCarClient();
        using var vm = CreateViewModel(client, log);

        vm.ClearLogViewCommand.Execute();
        log.Info(LogCategory.System, "信息");
        log.Warning(LogCategory.System, "警告");
        log.Error(LogCategory.System, "错误");

        Assert.Equal(3, vm.FilteredLogs.Count);

        vm.SelectedLogLevelFilter = "仅警告与错误";
        Assert.Equal(2, vm.FilteredLogs.Count);
        Assert.DoesNotContain(vm.FilteredLogs, entry => entry.Level == LogLevel.Info);

        vm.SelectedLogLevelFilter = "仅错误";
        var onlyError = Assert.Single(vm.FilteredLogs);
        Assert.Equal(LogLevel.Error, onlyError.Level);

        // 无匹配时新日志不进入过滤视图，但原始集合仍保留。
        vm.SelectedLogLevelFilter = "全部";
        Assert.Equal(3, vm.FilteredLogs.Count);
    }

    [Fact]
    public void ResetLocalTaskState_ClearsLocalTask_WithoutSendingCommand()
    {
        using var log = CreateLogService();
        var client = new FakeShuttleCarClient();
        using var vm = CreateViewModel(client, log);

        vm.ConnectCommand.Execute();
        client.Publish(FreshSnapshot());
        vm.TaskNumberInput = 12345;

        vm.StartTaskCommand.Execute();
        Assert.True(SpinWait.SpinUntil(() => vm.ActiveTaskNumber == 12345, TimeSpan.FromSeconds(2)));

        var sent = client.Commands.Count;
        vm.ResetLocalTaskStateCommand.Execute();

        Assert.Null(vm.ActiveTaskNumber);
        Assert.Equal(sent, client.Commands.Count);
        Assert.Contains("本地任务状态已人工重置", vm.CommandPhase, StringComparison.Ordinal);
    }

    [Fact]
    public void ResetLocalTaskState_KeepsTask_WhenConfirmationDeclined()
    {
        using var log = CreateLogService();
        var client = new FakeShuttleCarClient();
        var confirmations = 0;
        // 第一次确认用于下发任务（通过），第二次确认用于重置（拒绝）。
        using var vm = CreateViewModel(client, log, _ => ++confirmations == 1);

        vm.ConnectCommand.Execute();
        client.Publish(FreshSnapshot());
        vm.TaskNumberInput = 4321;
        vm.StartTaskCommand.Execute();
        Assert.True(SpinWait.SpinUntil(() => vm.ActiveTaskNumber == 4321, TimeSpan.FromSeconds(2)));

        vm.ResetLocalTaskStateCommand.Execute();

        Assert.Equal(4321, vm.ActiveTaskNumber);
        Assert.Equal(2, confirmations);
    }

    [Fact]
    public void ConfirmComplete_Enabled_OnlyAfterVehicleReportsCompletion()
    {
        using var log = CreateLogService();
        var client = new FakeShuttleCarClient();
        using var vm = CreateViewModel(client, log);

        vm.ConnectCommand.Execute();
        client.Publish(FreshSnapshot());
        vm.TaskNumberInput = 777;
        vm.StartTaskCommand.Execute();
        Assert.True(SpinWait.SpinUntil(() => vm.ActiveTaskNumber == 777, TimeSpan.FromSeconds(2)));

        Assert.False(vm.ConfirmCompleteCommand.CanExecute());

        client.Publish(FreshSnapshot(block =>
        {
            block[ProtocolMap.StatusTaskNumberRegister] = 0;
            block[ProtocolMap.StatusTaskNumberRegister + 1] = 777;
            block[ProtocolMap.StatusFlagsRegister] =
                ProtocolCodec.SetBit(block[ProtocolMap.StatusFlagsRegister], ProtocolMap.StatusTaskCompletedBit, true);
        }));

        Assert.True(vm.Snapshot.TaskCompletedFlag);
        Assert.True(vm.ConfirmCompleteCommand.CanExecute());
    }
}
