using System;

namespace ShuttleCarController.Models;

/// <summary>
/// 通信客户端状态变化事件参数。
/// 事件可能由后台线程触发，ViewModel 必须先切换到 UI 线程再更新绑定属性。
/// </summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>创建一次连接状态变化记录。</summary>
    /// <param name="previousState">变化前的链路状态。</param>
    /// <param name="currentState">变化后的链路状态。</param>
    /// <param name="message">面向用户的补充说明，例如断开原因或重连错误。</param>
    public ConnectionStateChangedEventArgs(ConnectionState previousState, ConnectionState currentState, string? message = null)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Message = message;
    }

    /// <summary>变化前的状态，可用于日志和状态迁移判断。</summary>
    public ConnectionState PreviousState { get; }

    /// <summary>变化后的状态，是界面当前应展示的链路状态。</summary>
    public ConnectionState CurrentState { get; }

    /// <summary>可选的状态说明；没有补充信息时为 null。</summary>
    public string? Message { get; }
}
