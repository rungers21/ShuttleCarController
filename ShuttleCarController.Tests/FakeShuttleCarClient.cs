using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShuttleCarController.Models;
using ShuttleCarController.Services;

namespace ShuttleCarController.Tests;

/// <summary>可编程的通信客户端替身：只记录调用并允许测试主动推送快照。</summary>
internal sealed class FakeShuttleCarClient : IShuttleCarClient
{
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public event EventHandler<ShuttleCarSnapshot>? SnapshotUpdated;

    public List<ShuttleCarCommand> Commands { get; } = [];

    /// <summary>下一个命令的返回值；默认“已接收”并回显任务号。</summary>
    public Func<ShuttleCarCommand, CommandResult> CommandHandler { get; set; } =
        command => new CommandResult(CommandStatus.Accepted, "已接收", command.TaskNumber);

    public Task ConnectAsync(ConnectionSettings settings, CancellationToken cancellationToken = default)
    {
        var previous = State;
        State = ConnectionState.Connected;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previous, State, "已连接"));
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var previous = State;
        State = ConnectionState.Disconnected;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previous, State, "已断开"));
        return Task.CompletedTask;
    }

    public Task<RawReadResult> ReadRawAsync(RawReadRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new RawReadResult(true, [1, 2], "ok", TimeSpan.Zero));

    public Task<RawWriteResult> WriteRawAsync(RawWriteRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new RawWriteResult(true, false, "ok", TimeSpan.Zero));

    public Task<CommandResult> ExecuteCommandAsync(ShuttleCarCommand command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        return Task.FromResult(CommandHandler(command));
    }

    public ValueTask DisposeAsync()
    {
        State = ConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    /// <summary>模拟客户端发布一帧快照。</summary>
    public void Publish(ShuttleCarSnapshot snapshot) => SnapshotUpdated?.Invoke(this, snapshot);
}
