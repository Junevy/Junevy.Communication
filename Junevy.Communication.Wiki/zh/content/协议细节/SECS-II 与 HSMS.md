---
tags: [协议细节, Junevy.Communication, Secs, 通道族]
---

# SECS-II 与 HSMS（HSMS-SS 设备端）

> 代码基线：`Junevy.Communication.Secs`（net472;net8.0，`1.0.0-preview.1`，P5.1）。依据：SEMI E5（SECS-II）与 E37 / E37.1（HSMS、HSMS-SS）的公开描述，逐字段拼出计划第 17.1 节的黄金向量 V-H-1…V-H-18 与第 17.2 节的 V-I-1…V-I-17。**未经真机 / 真主机验证**（P51-D18）：仓库里没有 SEMI 标准原文，也没有真机。与代码冲突时以代码为准；与设计第 14.1 节的差异见设计文档第 20.7 节，执行过程见计划 `docs/superpowers/plans/2026-10-12-p51-secs-hsms.md` 第 20.3 节。

## 范围（P51-D1、D2）

- **只做设备端（Equipment）**：设备端监听（Passive）或去连主机（Active），主机发 `Select.req` 时被动端回状态 0。
- **只做 SECS-II + HSMS-SS**：单会话（`TcpServerConfig.MaxSessions = 1`）。**不做 SECS-I**（RS-232 块传输，用户 2026-10-13 决定）、**不做 GEM**（改为 P5.2）、**不做主机端角色**。
- **数据项**：支持 `L`、`B`、`BOOLEAN`、`A`、`I1`/`I2`/`I4`/`I8`、`U1`/`U2`/`U4`/`U8`、`F4`/`F8`；**不支持 `J`（JIS-8）与 `C2`（2 字节字符）**，解码遇到它们按"非法数据"处理（设备端回 S9F7）。`A` 项是**单字节字符**：编码只接受 U+0000–U+00FF，解码按 Latin-1 逐字节还原（计划 P51-D3、D4）。
- **不实现 `IPlcClient`，不把 HSMS 状态压成 `ConnectionState`**（P51-D2）。

## 报文结构

报文 = 4 字节长度（大端、**不含自身**）+ 10 字节头 + 正文（[Protocol/HsmsMessage.cs](file://Junevy.Communication.Secs/Protocol/HsmsMessage.cs)、[HsmsControl.cs](file://Junevy.Communication.Secs/Protocol/HsmsControl.cs)）：

| 字段 | 宽度 | 说明 |
|---|---|---|
| 长度 | 4 | 大端，不含自身；整帧 = 4 + 长度 |
| 会话号 | 2 | 数据消息 = 设备号（`HsmsEquipmentOptions.DeviceId`）；控制消息 = `0xFFFF`，`Reject.req` 例外（回送被拒消息的会话号） |
| 头字节 2 | 1 | 数据消息 = W 位 << 7 \| Stream；控制消息里承载状态码 / SType / PType / 原因码 |
| 头字节 3 | 1 | 数据消息 = Function；控制消息里承载 SType |
| PType | 1 | 数据消息恒为 0 |
| SType | 1 | 1 = Select、2 = Deselect、3 = Linktest、4 = Reject、5 = Separate |
| System Bytes | 4 | 进程级 16 位单调计数器零扩展（Core 的 `Junevy.Communication.Core.Utils.SequenceCounter`，挂在 [HsmsSystemBytes.cs](file://Junevy.Communication.Secs/Protocol/HsmsSystemBytes.cs)），**只用低 16 位**（P51-D8、P51-D18 ②） |

分帧声明在 [HsmsFraming.cs](file://Junevy.Communication.Secs/Protocol/HsmsFraming.cs)：长度字段偏移 0、宽 4、大端、修正值 0，**保留长度字段**（帧交给协议层时仍带 4 字节长度），最大帧长取 `MaxMessageLength`。

数据项的格式字节 = 格式码（`SecsFormat` 枚举值，即线上格式字节的高 6 位）| 长度字节数；编码取能放下长度的最短长度字节数（1–3 个），数值一律大端（[SecsItemCodec.cs](file://Junevy.Communication.Secs/SecsItemCodec.cs)）。解码的防御上限：嵌套深度 ≤ 64、列表声明的子项数不超过剩余字节数的一半、单条消息不超过 `MaxMessageLength`，**超出按非法数据处理且不按声明长度分配内存**（P51-D5）。

## 取键规则（P51-D7，最容易写错的一条）

关联用 `CorrelationMode.Keyed`，键是 System Bytes；取键由 [HsmsKeyExtractor.cs](file://Junevy.Communication.Secs/Protocol/HsmsKeyExtractor.cs) 给出，规则是**只有应答类消息取应答键**：

| 消息 | 请求键 | 应答键 |
|---|---|---|
| 数据主消息（奇数 Function） | ✓ | |
| 数据应答（偶数 Function，含 0） | | ✓ |
| `Select.req` / `Linktest.req` | ✓ | |
| `Deselect.req` / `Separate.req`（不等应答，不占在途事务） | | |
| `Select.rsp` / `Linktest.rsp` / `Deselect.rsp` / `Reject.req` | | ✓ |
| PType ≠ 0 的任何帧（不属于 SECS-II，2026-10-14 验收补充） | | |

**为什么**：主消息若也取应答键，对方的主消息（例如 S6F11 W）恰好带着与我在途请求相同的 System Bytes 时，会被关联表当成应答吃掉，业务消息凭空消失。System Bytes 只在发送方内唯一（P51-D8）。

## 状态与计时器（P51-D9、D10）

状态只有三态（[HsmsState.cs](file://Junevy.Communication.Secs/Hsms/HsmsState.cs)）：`NotConnected`（无会话 / 停止 / 握手失败）、`NotSelected`（TCP 已通但主机还没 Select）、`Selected`（已选中，收发数据消息）。TCP 连上只说明字节流通了，因此 `NotSelected` 是独立状态。状态变化经 `StateChanged` 按顺序发出并带原因，订阅者抛异常不影响状态机。

**状态从哪里来（2026-10-14 合并后修正，见 [HsmsSessionState.cs](file://Junevy.Communication.Secs/Protocol/HsmsSessionState.cs)）**：通道的 `StateChanged` 与服务端的 `SessionConnected` / `SessionClosed` 由各自的派发任务**排队送出**，可能晚于实际变化，甚至晚于下一条链路的握手。所以握手只报 `NotSelected`（开始）或复位（失败）；`Selected` 在链路被接管时才报——主动端收到 `Connected` 且通道此刻确实连着，被动端收到 `SessionConnected` 且会话还在（服务端保证它先于该会话的任何帧）；断开只在没有新的握手进行、链路此刻也确实不在时复位。原实现把主动端任何"不是 Connected"的事件都当断开：首次连接时晚到的 `Connecting` 会把 `NotSelected`、甚至 `Selected` 打回 `NotConnected`，之后 `SendAsync` 一律 `NotSelected`。这个缺陷只在全量并行（net8.0）下出现，单独跑 Secs 从不出现，用例 `HsmsLinkEventTests` 不依赖套接字逐个复现这些顺序。主动端的当前连接在启动时就挂上通道本身，不等 `Connected` 事件。

计时器映射（`HsmsEquipmentOptions`，全部可配）：

| 计时器 | 默认 | 用在哪 |
|---|---|---|
| T3 | 45000 ms | **每个带 W 位的主消息**的 `RequestOptions.Timeout`；**不用通道的 `RequestTimeout`**。两种模式都设 `ResetOnRequestTimeout = false`（它只作用于 `Sequential` 关联，`Keyed` 下请求超时本来就不断链，这里是显式声明；"T3 超时后链路仍在"由 `NoReplyWithinT3_SendsS9F9AndReportsAReplyTimeout` 直接守住）：T3 超时是事务失败（回 S9F9、`ReplyTimeout`），不是链路失败 |
| T5 | 10000 ms | Active 的 `ReconnectOptions` 固定间隔 |
| T6 | 5000 ms | Select / Linktest 请求的超时，以及 Active 的 `HandshakeTimeout` |
| T7 | 10000 ms | Passive 的 `SessionHandshakeTimeout`（未选中超时） |
| T8 | 5000 ms | `PartialFrameTimeout`（字符间隔超时） |
| `LinktestInterval` | 0（不发） | 心跳间隔；`> 0` 时启用心跳，超时 T6、`MaxFailures = 1`、只在空闲时探测 |

主动端的**连接超时**另给 5 秒常量（计划偏差 D21），不占用 T6——主机不可达时不该等满 T6。主动端的 `Host` 是对端地址，可以是 IP 或主机名（与其他 TCP 客户端一样由通道解析）；被动端的 `Host` 是本机监听地址，只接受 IP，null 表示所有地址。执行时两种模式都只接受 IP，2026-10-14 验收时放开了主动端（这条限制没有记为偏差，也与 Melsec / S7 / FINS 不一致）。

## Select 与 Linktest（P51-D11）

- **Passive**（[HsmsPassiveHandshake.cs](file://Junevy.Communication.Secs/Protocol/HsmsPassiveHandshake.cs)，`IConnectionInitializer`）：握手是**在 T7 窗口内的循环**——收到 `Select.req` → 回 `Select.rsp` 状态 0 并握手成功；`Linktest.req` → 回 `Linktest.rsp` 继续等；数据消息 → 回 `Reject.req` 原因 4 继续等；`Separate.req` → 不应答、握手失败并断开；其余控制消息 → `Reject.req` 原因 1。取帧时每次等一帧都**显式给超时 T7**（整个握手窗口由通道的 `SessionHandshakeTimeout` 兜底）。执行时用的是 200 毫秒轮询，2026-10-14 通道修复（F1）合入后改掉；显式超时仍是必须的（F2）。当初需要变通的原因：通道的握手积压与接收等待者注册之间有竞态，且 `ReceiveAsync` 不给超时时用通道的 `RequestTimeout`（默认 2000 毫秒）小于 T7（计划 20.2 节 F1 / F2）。
- **Active**（[HsmsActiveHandshake.cs](file://Junevy.Communication.Secs/Protocol/HsmsActiveHandshake.cs)）：发 `Select.req`（T6）并校验应答的**种类、System Bytes、状态码**；非 0、被 `Reject.req`、超时、答非所问或号不对都算握手失败 → 状态复位成 `NotConnected` → 通道按 T5 重连，**每次重连都重新 Select**。
- **Active 收到主机的 `Separate.req`**（2026-10-14 验收修正）：不应答、断开，**等 T5 之后再连**；那次再连失败由通道按 T5 继续（主动端的 `ReconnectOptions.OnInitialFailure = true`）。原因：通道的 `DisconnectAsync` 会让重连循环永久停下，而设备端仍处于"已启动"，原实现因此停在"永远不再连接"的状态，只能先停止再启动。停止或释放发生在 T5 的等待之内时立刻结束、不再连接。
- **停止与释放会取消进行中的启动**（同日修正）：启动与停止共用一个取消信号，主动端等第一个 `Select.rsp` 时停止，`StartAsync` 立刻返回 `ConnectionClosed`，而不是让停止等满 T6；调用方取消启动返回 `Cancelled`。
- **Linktest**（[HsmsLinktestProbe.cs](file://Junevy.Communication.Secs/Protocol/HsmsLinktestProbe.cs)，`IHealthProbe`）：收发一条 `Linktest.req` / `Linktest.rsp`，超时 T6。

## 设备端派发规则（P51-D12、D13）

`HsmsEquipment`（`Hsms/` 目录，公开 API 见 [HsmsEquipment.cs](file://Junevy.Communication.Secs/Hsms/HsmsEquipment.cs)、[HsmsEquipment.Dispatch.cs](file://Junevy.Communication.Secs/Hsms/HsmsEquipment.Dispatch.cs)、[HsmsEquipment.Send.cs](file://Junevy.Communication.Secs/Hsms/HsmsEquipment.Send.cs)、[HsmsEquipment.Start.cs](file://Junevy.Communication.Secs/Hsms/HsmsEquipment.Start.cs)）：

1. **控制消息**（已选中后）：`Linktest.req` → `Linktest.rsp`；重复 `Select.req` → `Select.rsp` 状态 1；`Deselect.req` → `Reject.req` 原因 1；`Separate.req` → **不应答、立即断开**（主动端随后按 T5 再连）；无主的 `*.rsp` → 原因 3；未知 SType → 原因 1。**PType ≠ 0 → `Reject.req` 原因 2**（头字节 2 是那个 PType），选中前的握手里也一样；执行时这一条被静默丢弃，2026-10-14 验收补上。
2. **数据主消息**：① 会话号 ≠ `DeviceId` → S9F1；② 正文解码失败 → S9F7；③ 该 Stream 无处理器 → S9F3，有 Stream 无该 Function → S9F5（Function 是完整的一个字节；执行时只查了 Function 0–127，处理器全在 128 以上时误回 S9F3，验收修正）；④ 调用处理器：带 W 位时返回的应答必须是同一 Stream、Function + 1、不带 W 位，返回 null / 形状不符 / 抛异常都记 Error 并回 `SxF0`；不带 W 位时忽略返回值（非 null 记 Warning）。
3. **按到达顺序逐个处理，同一连接上不并发**。注意：**通道的帧事件不等待**（`TcpSession.RaiseFrame` 与 `StreamClientChannel.RaiseFrameReceived` 都是"逐个隔离异常地调用"后返回已完成任务），因此设备端自己用一条 `Task` 链把派发串起来，停止时排空这条链，并用 `AsyncLocal` 识别"处理器内部调用 `StopAsync`"以免等自己（计划偏差 D20）。
4. **发消息**（`SendAsync` → `SecsResult`）：未选中 → `NotSelected` 且不发送；带 W 位按 T3 等应答——正常 / `SxF0` → `Aborted` / `Reject.req` → `Rejected`（带原因码）/ 超时 → 先发 S9F9 再 `ReplyTimeout` / 应答不合法 → 先发 S9F7 再 `InvalidReply`；不带 W 位只发送。允许并发（键是 System Bytes）。

## S9 与中止消息

由 [HsmsSystemMessages.cs](file://Junevy.Communication.Secs/Protocol/HsmsSystemMessages.cs) 一处构造：**S9F1**（会话号不对）、**S9F3**（无 Stream 处理器）、**S9F5**（无 Function 处理器）、**S9F7**（正文非法）、**S9F9**（带 W 位的主消息超过 T3）、**`SxF0`**（处理器答不上来）。S9 的正文一律是**被涉及消息的 10 字节头**（`B` 项），S9F9 的正文是我方主消息的头；`SxF0` 没有正文。

**System Bytes**：S9 是设备端新发起的主消息，取设备端自己的计数器；`SxF0` 是应答，回送主消息的号。执行时 S9 沿用了被涉及消息的号（主机会把它当成那条主消息的应答），用例又只比对了 S/F 与正文，2026-10-14 验收修正并把用例改成同时断言会话号与"号不等于被涉及消息"。

## 与通道族部件的对应表（P51-D9，一个字都没改通道）

| HSMS / SECS 要素 | 内部使用的部件 |
|---|---|
| Passive / Active 两种模式 | `TcpServer`（`MaxSessions = 1`）/ `TcpClientChannel` + `ReconnectOptions`（固定间隔 T5） |
| 4 字节长度前缀 | `LengthField`（偏移 0、宽 4、大端、修正值 0，保留长度字段） |
| Select / Linktest | `IConnectionInitializer`（被动端循环、主动端发起）+ `IHealthProbe` |
| System Bytes 关联 | `CorrelationMode.Keyed` + `IFrameKeyExtractor`（取键规则见上） |
| 双方都发主消息并等应答 | `FrameReceived` + 会话 / 客户端通道上的 `RequestAsync` |
| T3 / T5 / T6 / T7 / T8 | 每个带 W 位消息的 `RequestOptions.Timeout` / `ReconnectOptions` / `HandshakeTimeout` / `SessionHandshakeTimeout` / `PartialFrameTimeout` |
| 按名称管理设备端 | Core 的 `NamedFactory<HsmsEquipment>`（`DependencyInjection/HsmsEquipmentFactory.cs`）+ `AddSecs()` |
| System Bytes、日志、结果 | Core 的 `SequenceCounter`、`ILogger`（Debug 及以上不含正文）、`CommResult` |

## 未验证清单（P51-D18）

1. `Select.req` 用会话号 `0xFFFF`（HSMS-SS 常见做法）。
2. System Bytes 只用低 16 位。
3. 处理器返回 null 时回 `SxF0` 而不是 S9F7。
4. 不支持 `J` 与 `C2` 格式。
5. T3 超时后先发 S9F9 再返回 `ReplyTimeout`。

这五条只在计划第 17 节的黄金向量上验证过，全部是按公开资料推断的，接真机前必须逐条确认。下一期见 [[协议细节/P5.2 GEM（待办）]]。