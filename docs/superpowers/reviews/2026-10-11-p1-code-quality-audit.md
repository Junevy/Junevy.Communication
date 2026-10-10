# P1 代码质量补审（2026-10-11，只读审查，修复安排在 P2 合并之后）

范围：master（`baa4080`，即 P1 合并后的状态）上的 Core / Channels / Tcp / Udp / Serial 生产代码，约 13.6k 行。
方法：① 机械度量——文件行数、方法行数（括号匹配近似）、重复块扫描（规范化后 6 行与 4 行窗口，忽略注释、using、纯括号行）；② 人工审读重复区域与超大类型；③ 模式扫描（测试专用钩子、阻塞等待、`ConfigureAwait`、宽泛 catch、TODO / pragma）。
P1 验收时没有做这一轮——当时只审功能与行为（见对话中的回答）。

## 结论摘要

| 级别 | 问题 | 规模 |
|---|---|---|
| 高 | 字节流与数据报两套实现在三层平行复制：通道（`StreamChannel` ↔ `DatagramChannel`）、连接驱动（`StreamConnectionDriver` ↔ `DatagramConnectionDriver`）、运行参数（`StreamClientOptions` ↔ `DatagramClientOptions`） | 约 205 + 155 + 134 行重复 |
| 高 | `TcpServer` 是 1216 行的单一类型，承担监听、接入过滤、握手（TLS + 初始化器）、会话心跳与拆除、启停、事件派发六种职责 | 1 个文件 |
| 中 | 客户端外观重复：`UdpChannel` 重写了 `StreamClientChannel` 的 `IClientChannel` 转发、释放等待、状态判断 | 约 76 行 |
| 中 | 心跳配置复制与 `hex:` 解析在三处重复（两个客户端 Options + `TcpServerOptions`） | 约 41 行 |
| 中 | 生产代码中的测试专用钩子：`TcpServer.SimulateListenerFault()`（internal，仅供测试注入故障） | 1 处 |
| 中 | 超长方法（> 60 行）：`StreamChannel.ParseLoopAsync` 98、`TcpServerOptions` 构造 85、`HeartbeatMonitor.RunAsync` 84、`UdpChannelSettings.From` 83、`TcpServer.AdmitSocket` 66、`StreamConnectionDriver.OpenAsync` 66 等 10 个 | 10 处 |
| 低 | `NamedRegistry` 的同步方法（`GetOrAdd` 竞争失败、`TryRemove`、`Dispose`）以 `GetAwaiter().GetResult()` 同步等待异步释放（沿袭 Modbus 的 `ModbusConnectionManager`） | 3 处 |
| 低 | `SerialChannel` ↔ `TcpClientChannel` 的配置映射与校验辅助重复；`StreamConnectionDriver` ↔ `TcpSession` 的通道视图转发重复 | 约 25 + 24 行 |

未发现的问题：库代码的 `await` 全部带 `ConfigureAwait(false)`（扫描命中的 4 处是跨行写法，已逐一确认）；没有 TODO / `#pragma warning disable`；`.Result` 的读取都发生在任务已完成之后。

## 逐项说明与建议

### 1. 字节流 / 数据报三层平行复制（高）

- 通道层：`RequestAsync` 的编排（参数校验 → Sequential 请求锁 → 关闭检查 → 注册等待者 → 交换 → 超时 / 取消后持锁过迟到窗口 → finally 释放）两边逐行相同，差异只有三处：数据报要求目标地址；交换步骤（字节流"写出 + 启动计时 + 等应答"，数据报"按重试次数重发"）；关闭消息文字。故障报告与停止序列（`Fault` / `CompleteStopAsync` / 异常处理）同样平行。
- 驱动层：`Attach`、握手编排 `HandshakeAsync`、`WaitForStepAsync`、`StartInitializer`、`ResolveFactoryProbe`（P1 收尾补丁新增，约 25 行逐字重复）、`ReceiveAsync` 转发相同。
- 参数层：校验、心跳复制、负载探测创建、`ParseBytes` 相同。
- 建议：抽出 internal 的共享部件，而不是继承层次——`RequestCoordinator`（请求锁 + 关联表 + 迟到窗口，交换步骤以委托传入）、`ChannelStopSequence`、`HandshakeRunner`（握手时限、初始化器、工厂探测解析）、`ClientOptionsCore`（公共字段的校验与快照）。两种通道各自只保留传输差异。预计删除 400 行以上，且以后修一个行为只改一处（P1 收尾补丁的工厂探测就是"同一改动写两遍"的实例）。

### 2. TcpServer 过大（高）

- 建议按已有的分组注释拆分：`TcpListenerLoop`（绑定、接受、故障后重新监听）、`TcpSessionAdmission`（白名单、过滤器、会话上限）、`TcpSessionHandshake`（TLS + 初始化器；与驱动层的握手编排合并后可复用第 1 项的 `HandshakeRunner`）、`ServerEventDispatcher`（单循环事件派发与派发作用域）。`TcpServer` 保留公开 API 与组装。

### 3. 客户端外观重复（中）

- `UdpChannel` 不能继承 `StreamClientChannel`（后者是字节流的公开扩展基类）。建议用组合：internal 的 `SupervisedChannelCore` 持有监督器与驱动，提供状态、释放等待、当前视图；两个公开类型只做一行转发。

### 4. 心跳配置重复（中）

- 抽出 internal 的 `HeartbeatSettings`（复制、校验、`hex:` 解析、内置探测创建），三处共用（Tcp 经 `InternalsVisibleTo` 可见）。

### 5. 测试专用钩子（中）

- `SimulateListenerFault` 让生产代码带测试分支。建议把监听器抽象为 internal 接口（第 2 项拆分后自然出现），测试注入一个会失败的监听器实现。

### 6. 超长方法（中）

- 优先拆 `ParseLoopAsync`（解码、半帧计时、刷新三段）与两个配置构造函数（按配置分组的私有校验方法）。

### 7. NamedRegistry 同步等待（低）

- 记录为已知设计：同步 API 内释放实例只能阻塞；内部释放全部 `ConfigureAwait(false)`，在有同步上下文的宿主中不会死锁。可选改进：竞争失败时的重复实例改为后台释放。

## 处理安排

按用户决定：P2 合并之后单独开一个重构任务，按"高 → 中"顺序处理，每项附回归测试（行为不变）并重新跑本文件的度量脚本，重复行数与超长方法数作为验收指标。
