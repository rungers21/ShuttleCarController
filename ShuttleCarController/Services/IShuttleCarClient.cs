using System;
using System.Threading;
using System.Threading.Tasks;
using ShuttleCarController.Models;

namespace ShuttleCarController.Services;

/// <summary>
/// 穿梭车通信边界，是 UI/业务层与具体 Modbus 实现之间唯一的硬件交互接口。
/// 本项目的默认实现为 <see cref="ModbusShuttleCarClient"/>（Modbus TCP）。
/// </summary>
/// <remarks>
/// 实现方负责：连接管理、周期轮询、读写串行化、地址与字节序映射、任务握手、
/// 数据新鲜度判断以及断线重连。View 和 ViewModel 不应直接依赖 NModbus。
/// 所有耗时方法都必须支持取消；事件可能由后台线程触发。
/// </remarks>
public interface IShuttleCarClient : IAsyncDisposable
{
    /// <summary>当前通信链路状态，供 ViewModel 在事件之外主动查询。</summary>
    ConnectionState State { get; }

    /// <summary>
    /// 连接状态变化事件。
    /// 实现可能在后台线程触发，订阅者不得直接在该线程更新 WPF 可视元素。
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 新一轮状态轮询完成事件。
    /// 每次应提供完整、不可变的快照；实现可能在后台线程触发。
    /// </summary>
    event EventHandler<ShuttleCarSnapshot>? SnapshotUpdated;

    /// <summary>
    /// 建立连接并启动状态轮询。
    /// </summary>
    /// <param name="settings">本次连接使用的不可变配置副本。</param>
    /// <param name="cancellationToken">用于取消连接等待或程序退出。</param>
    /// <remarks>
    /// 连接失败时应抛出异常或进入 Faulted 状态。
    /// 连接成功后只允许开始读取，不得自动补发上一次未完成的任务或写入。
    /// </remarks>
    Task ConnectAsync(ConnectionSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止轮询并释放当前连接。
    /// 该操作应可重复调用，并取消仍在等待执行的写操作。
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行一次原始寄存器读取，供协议联调和诊断使用。
    /// 该请求必须与后台轮询共享同一通信串行化机制。
    /// </summary>
    Task<RawReadResult> ReadRawAsync(RawReadRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行一次原始寄存器写入。
    /// 写入超时或断线时必须返回 ResultUnknown，不得把结果简单当作失败后自动重发。
    /// </summary>
    Task<RawWriteResult> WriteRawAsync(RawWriteRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行强类型业务命令。
    /// 实现负责前置条件复核、参数编码、必要的分帧写入、回读以及任务完成握手。
    /// 写入结果不明确时应返回 TimedOut/ResultUnknown，防止重复触发车辆动作。
    /// </summary>
    Task<CommandResult> ExecuteCommandAsync(ShuttleCarCommand command, CancellationToken cancellationToken = default);
}
