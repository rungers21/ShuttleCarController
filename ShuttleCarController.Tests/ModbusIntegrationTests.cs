using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using ShuttleCarController.Models;
using ShuttleCarController.Services;
using ShuttleCarController.Services.Devices;
using ShuttleCarSimulator.Services;
using Xunit;

namespace ShuttleCarController.Tests;

/// <summary>
/// 主从站端到端集成测试：在回环端口启动真实 <see cref="ModbusSlaveSimulator"/>，
/// 用真实 <see cref="ModbusShuttleCarClient"/> 跑通连接、轮询、任务闭环、故障注入与断线重连。
/// </summary>
public sealed class ModbusIntegrationTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "shuttle-e2e-" + Guid.NewGuid().ToString("N"));
    private ShuttleCarSnapshot? _latest;
    private LogService? _logService;

    [Fact]
    public async Task ConnectAndPoll_ReceivesGoodSnapshot()
    {
        var (simulator, client, _) = await StartAsync();
        try
        {
            await WaitUntilAsync(() => _latest is { Quality: DataQuality.Good }, 5000);

            Assert.Equal(ConnectionState.Connected, client.State);
            Assert.NotNull(_latest);
            Assert.True(_latest!.Fields.Count > 0);
            Assert.Equal(0, _latest.AlarmCode);
        }
        finally
        {
            await StopAsync(simulator, client);
        }
    }

    [Fact]
    public async Task StartTask_SimulatorCompletes_ConfirmClosesLoop()
    {
        var (simulator, client, _) = await StartAsync();
        try
        {
            const int taskNumber = 4242;
            var start = await client.ExecuteCommandAsync(new ShuttleCarCommand
            {
                Type = ShuttleCarCommandType.StartTask,
                TaskNumber = taskNumber,
                TaskType = 1,
                OutboundMode = 1,
                BodyDirection = 1,
                EntryBack = 0
            });

            Assert.Equal(CommandStatus.Accepted, start.Status);
            await WaitUntilAsync(() => _latest?.TaskNumber == taskNumber, 5000);

            simulator.SetTaskComplete();
            await WaitUntilAsync(() => _latest?.TaskCompletedFlag == true, 5000);

            var confirm = await client.ExecuteCommandAsync(new ShuttleCarCommand
            {
                Type = ShuttleCarCommandType.ConfirmComplete,
                TaskNumber = taskNumber
            });

            Assert.Equal(CommandStatus.Completed, confirm.Status);
            await WaitUntilAsync(() => _latest is { TaskNumber: null or 0, TaskCompletedFlag: false }, 5000);
        }
        finally
        {
            await StopAsync(simulator, client);
        }
    }

    [Fact]
    public async Task FaultInjection_RaisesAlarm_AndClearRecovers()
    {
        var (simulator, client, _) = await StartAsync();
        try
        {
            simulator.SetFault(ProtocolMap.FaultFields[0], true);
            await WaitUntilAsync(() => _latest?.HasAlarm == true, 5000);
            Assert.Contains(_latest!.Alarms, alarm => alarm.Name == ProtocolMap.FaultFields[0].Name && alarm.IsActive);

            var clearResult = await client.ExecuteCommandAsync(new ShuttleCarCommand
            {
                Type = ShuttleCarCommandType.ClearFault
            });
            Assert.Equal(CommandStatus.Accepted, clearResult.Status);
            await WaitUntilAsync(() => _latest?.HasAlarm == false, 5000);
        }
        finally
        {
            await StopAsync(simulator, client);
        }
    }

    [Fact]
    public async Task WriteAfterSlaveStops_ReturnsResultUnknown()
    {
        var (simulator, client, _) = await StartAsync();
        try
        {
            await simulator.StopAsync();

            var result = await client.WriteRawAsync(new RawWriteRequest(1, 6, 20, [1]));

            Assert.False(result.Success);
            Assert.True(result.ResultUnknown);
        }
        finally
        {
            await StopAsync(simulator, client);
        }
    }

    [Fact]
    public async Task ClientReconnects_AfterSlaveRestarts()
    {
        var (simulator, client, port) = await StartAsync();
        try
        {
            await simulator.StopAsync();
            await WaitUntilAsync(() => client.State is ConnectionState.Reconnecting or ConnectionState.Faulted, 5000);

            await simulator.StartAsync(port, 1);
            await WaitUntilAsync(() => client.State == ConnectionState.Connected, 10000);
            await WaitUntilAsync(() => _latest is { Quality: DataQuality.Good }, 10000);
        }
        finally
        {
            await StopAsync(simulator, client);
        }
    }

    private async Task<(ModbusSlaveSimulator Simulator, ModbusShuttleCarClient Client, int Port)> StartAsync()
    {
        var port = GetFreePort();
        var simulator = new ModbusSlaveSimulator();
        await simulator.StartAsync(port, 1);

        var client = new ModbusShuttleCarClient(
            _logService = new LogService(_logDirectory),
            TwoWayShuttleProtocolProfile.Instance);
        client.SnapshotUpdated += (_, snapshot) => _latest = snapshot;

        await client.ConnectAsync(new ConnectionSettings
        {
            IpAddress = "127.0.0.1",
            Port = port,
            UnitId = 1,
            RequestTimeoutMs = 500,
            PollIntervalMs = 100,
            ReconnectIntervalMs = 500,
            PollStartAddress = 0,
            PollQuantity = ProtocolMap.TotalLength
        });

        await WaitUntilAsync(() => _latest is { Quality: DataQuality.Good }, 5000);
        return (simulator, client, port);
    }

    private async Task StopAsync(ModbusSlaveSimulator simulator, ModbusShuttleCarClient client)
    {
        await client.DisposeAsync();
        await simulator.DisposeAsync();
        _logService?.Dispose();
        _logService = null;
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"条件在 {timeoutMs} ms 内未满足。");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_logDirectory))
            {
                Directory.Delete(_logDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 日志后台队列可能尚未释放文件句柄，清理失败不影响测试结论。
        }
    }
}
