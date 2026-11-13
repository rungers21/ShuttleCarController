# AGENTS.md — ShuttleCarController 演进与自迭代指南

> **定位**：指导智能体（Agents）在不破坏现有工业安全联锁的前提下，对 `ShuttleCarController`（上位机）与 `ShuttleCarSimulator`（Modbus TCP 从站仿真器）进行缺陷修复、性能优化、界面降噪、设备接口解耦与自动化测试闭环，并自主完成 **5 轮迭代**。

---

## 1. 仓库现状与基线命令

| 模块 | 路径 | 职责 |
|---|---|---|
| 上位机主工程 | `ShuttleCarController/` | WPF (`net10.0-windows`) + Prism.DryIoc + NModbus 主站控制端 |
| 从站仿真器 | `ShuttleCarSimulator/` | WPF + NModbus TCP 从站（无 UI 强耦合的 `ModbusSlaveSimulator`） |
| 协议与联调文档 | `Docs/二向穿梭车上位机详细开发方案.md`、`Docs/联调仿真说明.md` | P0 协议边界、DB 偏移表、握手时序 |

**常用验证命令**：
```powershell
dotnet build ShuttleCarController.slnx -c Release
dotnet test ShuttleCarController.slnx -c Release   # 第 1 轮建立测试工程后启用
```

---

## 2. 工业安全红线（任何迭代严禁突破）

1. **写失败/超时绝不自动重发**：`Retries = 0`；写入超时或断线统一返回 `ResultUnknown`，锁定当前任务状态并交由人工核对实车，防止重复触发机械动作。
2. **单连接严格串行**：周期轮询、原始调试读写、业务命令写入、写后回读必须共享同一把通信锁（`SemaphoreSlim(1,1)`），严禁并发调用 `IModbusMaster`。
3. **控制前置联锁**：未连接、数据非 `Good`、数据年龄超过 `max(1000ms, 2 × PollIntervalMs)`、存在报警或活动任务未闭环时，必须禁用新任务下发。
4. **协议未知不猜测**：未确认的枚举、比例、单位和保留位必须保留原始数值并明确标记（如 `未确认(7)`、`状态冲突`），严禁静默兜底为“正常”。
5. **优雅退出与断线清理**：断开连接或关闭窗口必须取消轮询、释放 Socket/Timer，严禁在重连后自动补发断线前的写请求。

---

## 3. 核心优化目标与代码落点

### 3.1 修复已知错误与状态机漏洞（Bug Fixes）
1. **退出未释放通信套接字**：`ShuttleCarController/App.xaml.cs` 的 `OnExit` 仅调用了 `MainWindowViewModel.Dispose()`，未释放 `IShuttleCarClient`（`IAsyncDisposable`），导致进程退出时后台轮询与 TCP 连接未显式关闭。
2. **报警与状态冲突未识别**：`ProtocolMap.Decode` 未按开发方案 6.4 实现冲突检测：
   - `AtSideA && AtSideB`（A/B 侧同时为真 → 方向状态冲突）
   - `HasGoods && NoGoods`（上方有货与无货同时为真 → 货物状态冲突；同时为假 → 未确定）
   - 交互区 `FIFO(0.3)/FILO(0.2)` 与 `FIFO状态(1.2)/FILO状态(1.3)` 冲突检测
   - Excel 原表标注“未写入”的故障位（`2.0 前防撞`、`2.1 后防撞`、`3.1 行走堵转`）未附加 `"未写入"` 说明。
3. **跨站号/离线残留任务锁死**：当车辆在外部复位或切换连接后，若处于 `ResultUnknown` 或异常中断，缺少安全的人工“重置本地任务状态”入口，导致只能重启软件。
4. **仿真器命令处理细节**：`ModbusSlaveSimulator` 在处理 `ConfirmComplete` 或 `StartTask` 时，需确保命令寄存器与状态标志位同步复位，且异常端口/站号输入在 UI 上有明确反馈（`ShuttleCarSimulator/ViewModels/MainViewModel.cs:115`）。

### 3.2 优化性能表现（Performance）
1. **消除轮询热路径整表重建**：`ProtocolMap.Decode` 每 500ms 分配新的 `List<ShuttleCarFieldValue>(74)` 与 `List<ShuttleCarAlarm>(15)`，导致 WPF `DataGrid` 和 `ItemsControl` 每周期整表替换引用。应改为：
   - 在 `ProtocolMap.Decode` 中若原始 `ushort[]` 内容与上一帧一致（可对比 `ReadOnlySpan<ushort>.SequenceEqual`），仅更新时间戳与新鲜度，跳过字段解码与 UI 集合重建；
   - 或在 ViewModel 层对 74 行 `ObservableCollection` 做按索引增量更新，避免 `DataGrid` 频繁重排。
2. **报警列表降噪与零分配**：`Snapshot.Alarms` 当前每周期渲染全部 15 个故障项（含“正常”项）。仅在故障位变化时更新列表，默认只投影活动故障或置顶活动故障。
3. **日志落盘非阻塞化**：`LogService.Write` 当前在调用线程同步执行 `Directory.CreateDirectory` + `File.AppendAllText`。应改为内存队列/批量缓冲写入，且日期文件名只在跨天时打开流，避免阻塞通信或 UI 线程。

### 3.3 降低界面复杂度（UI Simplification）
1. **收起低频工程调试区**：将 `MainWindow.xaml` 中的“基础控制 / Modbus 原始读写”改为默认折叠的 `Expander`，把首屏空间留给“业务控制 + 参数解析 + 关键数据”。
2. **消除固定行高裁切**：移除右侧主网格硬编码的 `RowDefinition Height="190"` / `"165"`，改为自适应 `Auto` + 主内容区 `*`，防止 DPI 缩放或折叠后面板留白/裁切。
3. **精简参数解析与报警展示**：
   - 参数解析区只展示核心运行指标，正常状态下隐藏全部为“正常”的 15 条故障明细卡片，仅在 `HasAlarm == true` 时展开活动故障；
   - 数据表增加**关键字/数据区筛选框**及“仅看非零/变化项”或“隐藏备用字段”开关，将 74 行原始表降噪。
4. **日志区过滤与可读性**：增加按级别（全部/Warning+/Error）或分类过滤的轻量筛选，支持暂停自动滚动。

### 3.4 自动化测试闭环（Automated Testing）
新建 `ShuttleCarController.Tests`（xUnit，`net10.0-windows`），引用 `ShuttleCarController` 与 `ShuttleCarSimulator`，无需启动 WPF 窗口即可跑通全链路测试：
1. **协议编解码单元测试**：覆盖 `ProtocolCodec`（`Int16` 边界 `0x7FFF`/`0x8000`/`-1`、`ToInt32` 高低字序、`GetBit`/`SetBit` 不污染相邻位）与 `ProtocolMap.Decode`（短帧保护、冲突检测、字段映射）。
2. **主从站端到端集成测试**：在测试中实例化 `ModbusSlaveSimulator`（绑定随机空闲回环端口）与 `ModbusShuttleCarClient`，自动验证：
   - 连接 → 周期轮询收到 `DataQuality.Good` 快照；
   - `StartTask` → 状态区回显任务号 → `Simulator.SetTaskComplete()` → `ConfirmComplete` → 任务清零闭环；
   - 故障注入（`SetFault`）→ `Snapshot.HasAlarm == true` → `ClearFault` → 故障恢复；
   - 写入超时/断线 → 返回 `ResultUnknown` 且不重发；
   - 从站停止再启动 → 客户端进入 `Reconnecting` 并自动重连恢复。
3. **ViewModel 联锁与配置测试**：使用 Fake `IShuttleCarClient` 测试 `MainWindowViewModel` 的 `CanStartTask`、`CanConfirmComplete`、数据过期禁用及 `AppConfigStore` 损坏配置自动备份恢复。

### 3.5 为未来设备开放接口（Future Device Extensibility）
将当前硬编码绑定到“二向穿梭车 + 固定 `ProtocolMap` 静态类”的通信层解耦为可插拔设备架构，保持最小必要抽象（不引入插件框架或动态反射库）：
1. **协议画像接口 `IDeviceProtocolProfile`**：
   - 将静态 `ProtocolMap` 抽象为 `IDeviceProtocolProfile`（定义轮询区段、字段映射表、快照解码器 `Decode(...)`、命令编码器 `EncodeCommand(...)` 及完成判定谓词），默认实现为 `TwoWayShuttleProtocolProfile`。
   - 保留 `ProtocolMap` 作为向后兼容门面，方便仿真器与现有调用方平滑过渡。
2. **多设备/多传输扩展边界**：
   - 抽取设备描述 `DeviceDescriptor`（设备 ID、设备类型如 `TwoWayShuttle` / `FourWayShuttle` / `Lifter` / `Conveyor`、协议画像、连接配置）；
   - 新增 `IShuttleCarClientFactory`（或 `IDeviceClientFactory`），按 `ConnectionSettings` + `IDeviceProtocolProfile` 创建客户端实例，为未来接入 Modbus RTU、四向车、提升机或多车切换预留干净的 DI 扩展点。

---

## 4. 五轮自主迭代执行规范（5-Round Self-Iteration Protocol）

智能体收到优化或迭代指令后，必须严格按以下 **5 轮次序**自主推进。每一轮结束前必须执行编译与测试验证，不得跳轮或合并敷衍。

### 第 1 轮：自动化测试基建与已知缺陷清零（Tests & Bug Fixes）
- **目标**：建立安全网，修复生命周期、协议冲突显示与状态机缺陷。
- **动作**：
  1. 创建 `ShuttleCarController.Tests` 并加入 `ShuttleCarController.slnx`；编写 `ProtocolCodec`、`ProtocolMap`、`AppConfigStore` 基础测试。
  2. 修复 `App.xaml.cs` 退出时未 `DisposeAsync` 释放 `IShuttleCarClient` 的资源泄漏。
  3. 补全 `ProtocolMap.Decode` 中的 A/B 侧冲突、有货/无货冲突、未写入故障位标注（`2.0`/`2.1`/`3.1`），并在 `MainWindowViewModel` 增加受二次确认保护的“重置本地任务状态”命令以解除异常锁死。
  4. 修复 `ShuttleCarSimulator` 端口/站号非法输入静默无提示问题。
- **准出门槛**：`dotnet test ShuttleCarController.slnx` 全部通过，0 Error / 0 Warning。

### 第 2 轮：通信与界面渲染性能优化（Performance Optimization）
- **目标**：降低 500ms 轮询下的内存分配、Dispatcher 调度开销与磁盘 I/O 阻塞。
- **动作**：
  1. 在 `ModbusShuttleCarClient` / `ProtocolMap` 中实现寄存器帧比对或增量字段更新，帧内容未变时复用字段集合，仅刷新时间戳。
  2. 优化 `LogService`：移除每次写日志的 `Directory.CreateDirectory` 调用，采用后台单写队列或流复用，确保高频日志不卡 UI。
  3. 优化 `ShuttleCarSimulator` 刷新：仅在寄存器值实际变化时触发 UI 属性通知。
- **准出门槛**：新增性能/高频轮询回归测试（连续解码 1000 次无异常且相同帧复用对象），全部测试通过。

### 第 3 轮：界面降噪与交互复杂度收敛（UI Simplification）
- **目标**：降低操作员认知负荷，突出核心任务控制与异常报警。
- **动作**：
  1. 重构 `ShuttleCarController/Views/MainWindow.xaml`：将“基础控制 / Modbus 原始读写”收纳进默认折叠的 `Expander`；移除硬编码行高 `190`/`165`。
  2. 参数解析区仅在存在活动报警时展开故障明细列表，无报警时显示绿色“无故障”摘要。
  3. 为数据表添加搜索筛选（按字段名/区域过滤）与“隐藏备用项”开关；为日志区添加级别筛选（全部 / 仅警告与错误）。
- **准出门槛**：XAML 编译零警告，ViewModel 新增筛选属性具备单元测试覆盖。

### 第 4 轮：面向未来设备的接口解耦与扩展（Open Device Interfaces）
- **目标**：隔离设备协议差异，使上位机无需修改 ViewModel/传输层即可接入新车型或新协议版本。
- **动作**：
  1. 定义 `IDeviceProtocolProfile` 与 `DeviceDescriptor`，将二向车寄存器偏移、命令编码、握手谓词封装为默认实现 `TwoWayShuttleProtocolProfile`。
  2. 重构 `ModbusShuttleCarClient` 通过注入 `IDeviceProtocolProfile` 驱动轮询与命令执行，解除对静态硬编码偏移的直接耦合。
  3. 提供 `IDeviceClientFactory` 接口及默认注册，支持在配置文件中指定设备 Profile 标识。
- **准出门槛**：编写自定义 Mock 设备 Profile 测试，验证 `ModbusShuttleCarClient` 可在不改代码的情况下驱动不同寄存器布局的新设备。

### 第 5 轮：全链路仿真压力验证、代码精简与文档对齐（Convergence & Audit）
- **目标**：端到端闭环验证、清理冗余代码与死代码、同步更新项目文档。
- **动作**：
  1. 扩充 `ShuttleCarController.Tests` 端到端集成测试：覆盖 `ModbusSlaveSimulator` ⇄ `ModbusShuttleCarClient` 的完整任务闭环、故障注入与清除、断线自动重连、写超时 `ResultUnknown` 联锁。
  2. 审查全部改动：删除未使用的属性、冗余转换器或过度设计的包装层；检查所有 `shortcut:` 注释是否准确。
  3. 对齐更新 `Docs/联调仿真说明.md`，记录新增的测试工程运行方式、协议扩展接口与 UI 筛选用法。
- **准出门槛**：`dotnet build ShuttleCarController.slnx -c Release` 与 `dotnet test ShuttleCarController.slnx -c Release` 全绿通过，Git 工作区无临时垃圾文件。

---

## 5. 每轮迭代汇报模板

智能体在执行每轮迭代时，按以下极简格式输出阶段结论：

```text
[Round X/5] <本轮主题>
- 变更文件：<文件列表>
- 解决的问题：<1~3 条核心点>
- 验证结果：dotnet build / dotnet test 通过数与耗时
- 遗留至下轮：<如有>
```
