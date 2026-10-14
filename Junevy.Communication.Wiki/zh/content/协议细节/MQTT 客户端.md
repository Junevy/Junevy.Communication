---
tags: [协议细节, Junevy.Communication, Mqtt, MQTT]
---

# MQTT 客户端

`Junevy.Communication.Mqtt` 是本库第一个 **SDK 适配包**：只做客户端，只支持 MQTT 3.1.1 与 5.0，**只面向 net8.0**。协议本身由 [MQTTnet 5](https://github.com/dotnet/MQTTnet)（MIT）实现，本包是一层薄适配——公开 API 里没有 SDK 类型，所有失败都翻译成 Core 的 `CommResult`。

> 版本 `1.0.0-preview.1`。**只与进程内的 MQTTnet.Server 联调过，没有对 Mosquitto / EMQX / HiveMQ 等真实 Broker 验证过。**

## 状态与重连

状态只有一个来源：`MqttConnectionStateMachine`（Q35）。四个状态 `Disconnected` / `Connecting` / `Connected` / `Reconnecting`，**只有状态真的变了才通知**；`StateChanged` 按发生顺序触发，单个订阅者抛异常只记 Error（Core 的 `EventSubscribers.InvokeEach`）。

`AutoReconnect` 默认为 **true**（P6-C11：会话恢复是 MQTT 的原生语义，与通道族"默认关闭"不同）。意外断线后：

1. 状态转 `Reconnecting`，`Reason` 取 SDK 给出的原因串，**没有原因串时回退到原因码**（保证非空）。
2. `MqttReconnectLoop` 在**后台任务**里重试，退避用 Core 的 `ExponentialBackoff`，不限次数，每次尝试前先等待。**不在 SDK 的事件回调里发起重连**——MQTTnet 明确警告这样会死锁。
3. 每一次尝试都走 `MqttLifecycleGate`（与 `ConnectAsync` / `DisconnectAsync` 同一把异步锁）。
4. 成功且 **Session Present = false** 时，按订阅表逐个重新订阅；**恢复完毕才报 `Connected`**。为 true 时不重新订阅（避免 Broker 重发保留消息）。
5. `DisconnectAsync` 与释放会停止循环**并等待它退出**——释放之后不留残留后台任务。`DisconnectAsync` **只结束当前这一轮**：之后再 `ConnectAsync`，下一次意外断线照样重连；只有释放才永久停止（验收时修正，见下）。
6. SDK 的断线事件以"比较并交换"把状态从 `Connected` 改为 `Reconnecting`，不会覆盖应用刚做出的断开；重连尝试拿到锁后发现状态已不是 `Reconnecting` 就直接结束。

几个容易踩的点：

- **首次 `ConnectAsync` 失败不启动重连**（P6-C11）。
- `AutoReconnect = false` 时断线直接 `Disconnected`，之后不再尝试。
- 循环退出时必须清掉 `running` 句柄，否则**第二次断线永远停在 `Reconnecting`**（Task 5 修掉的真实缺陷）。
- 订阅表自己维护**首次订阅的顺序**——`ConcurrentDictionary` 的枚举顺序没有保证，而"逐个恢复"要求顺序可复现。
- 只有被 Broker **明确拒绝**的过滤器（SUBACK 原因码 ≥ 0x80，即 `RemoteError`）在恢复时**移出订阅表**，否则每次重连都重试同一个坏订阅。恢复时**超时或断线**是链路问题：这一轮重连算失败、断开后重试，订阅表不变。
- 验收时修正的两个缺陷（2026-10-12）：① 原实现在 `DisconnectAsync` 里把循环永久停掉，断开后又连上的客户端再掉线时状态永远停在 `Reconnecting`；② 原实现对恢复时的任何失败都移出订阅表，一次超时就永久丢掉订阅。两者都有用例钉住（`MqttReconnectRecoveryTests`）。

## 释放

- `Dispose` / `DisposeAsync` 可重复调用：先取消仍在进行的首次连接（那次 `ConnectAsync` 返回 `ConnectionClosed`），再永久停止重连并等它退出，最后**在生命周期锁内**释放 SDK 客户端——SDK 不允许释放与连接、断开并发。
- 释放之后发布、订阅、退订返回 `ConnectionClosed`（P6-C8）；`ConnectAsync` 与 `DisconnectAsync` 抛 `ObjectDisposedException`。
- 生命周期锁的信号量**不释放**：释放时可能还有操作握着租约，释放了信号量那次 `Release` 就会抛异常（验收时修正）。

## 发布与接收

- QoS 0 写出即成功；QoS 1 等 PUBACK；QoS 2 等 PUBCOMP。原因码 ≥ 0x80 视为失败。
- 主题或过滤器非法、QoS 不是定义值 → `InvalidRequest`，**不发送**。合法性交给 SDK 的 `MqttTopicValidator`，本库不另写一份。
- 不是 `Connected` 时发布 / 订阅 / 退订 → `NotConnected`。
- 发布过程中断线 → `ConnectionClosed`，**不自动重发**：Broker 是否已收到未知，由宿主决定（设计第 22 节，没有离线队列）。
- `MessageReceived` 在 SDK 的接收回调线程上**同步触发**，用 Core 的 `EventSubscribers.InvokeEach`。订阅者全部返回之后 SDK 才发 QoS 确认，因此**订阅者不能阻塞**。
- 收到的正文、关联数据、用户属性在 `Sdk/MqttSdkMessages` 里**复制**后才交给宿主（SDK 的缓冲区可能被复用）。
- MQTT 5 属性（`ContentType` / `ResponseTopic` / `CorrelationData` / `MessageExpiryInterval` / `UserProperties`）在 3.1.1 连接上返回 `InvalidRequest` 且**不发送**——不静默丢弃。

## 错误分类

全包**唯一**一处由 SDK 的异常或原因码推出 `CommErrorKind` 的地方是 `Sdk/MqttErrorMap`（P6-C7、Q38）。完整表见包 README 与计划第 15 节，判定规则：

- **原因码以 0x80 为界**：小于它就是确认，算成功（例如 MQTT 5 PUBACK 的 `0x10 No matching subscribers`）。
- CONNACK 的 134 / 135 / 138 / 140 → `AuthenticationFailed`；137 / 151 / 159 → `ResourceExhausted`；132 → `NotSupported`；其余失败码 → `RemoteError`。
- **超时与取消归本包判定**：MQTTnet 在连接被中止时抛的是笼统的 `MqttCommunicationException`，类型里读不出原因。每次连接由本包开一个 `TimeoutScope`（`Timeout` 同时是连接超时与等待确认的时间），`MqttErrorMap.Runtime(exception, scope)` **先看作用域再看异常类型**——与 WebApi 的 `HttpStatusClassifier` 同一做法。
- **TLS 认证异常被 SDK 包进 `MqttCommunicationException`**，因此映射沿内层异常链找 `AuthenticationException`；只判最外层类型会把证书不受信任误分类成 `ConnectionClosed`。
- **`AllowUntrustedCertificates = true` 用校验回调一律放行**：SDK 自己的 `AllowUntrustedCertificates` 只放过证书链错误，主机名不符照样拒绝（验收时实测，`MqttTlsTests` 钉住）。
- SDK 的异常不能逃出公开方法；`Exception` 只挂在 `CommResult.Exception` 上作诊断。

## 遗嘱消息

`WillTopic` / `WillPayload` / `WillQos` / `WillRetain` 放在**连接选项**里，因此**每次连接（含重连）都在 CONNECT 中携带**。正文是 UTF-8 文本以便从 appsettings 绑定。

- Broker 只在**异常断开**时发布遗嘱。`DisconnectAsync` 与释放发的是 DISCONNECT，按 MQTT 规定 Broker 丢弃遗嘱。
- 常见用法：`WillTopic = "devices/{id}/status"`、`WillPayload = "offline"`、`WillRetain = true`，连接成功后由宿主自己发一条保留的 `online`——**库不自动发上线消息**。
- 实时投递的遗嘱 `Retain` 为 `false`（保留标志只在"因新订阅补发保留消息"时为 true）；后来者收到的保留副本才是 `true`。

## SDK 隔离的结构（P6-C4、P6-C5）

```
Junevy.Communication.Mqtt/
  MqttProtocolVersion.cs / MqttQos.cs / MqttConnectionState.cs / MqttUserProperty.cs   公开薄类型
  MqttMessage.cs                不可变消息模型（构造与 init 都复制）
  MqttClientOptions.cs          POCO，可从 appsettings 绑定；内部 Copy() 取快照
  MqttOptionsValidator.cs       校验集中在一处
  MqttClient.cs                 公开门面
  MqttLifecycle.cs              连接 / 断开 / 释放（都经过生命周期锁）
  MqttMessaging.cs              发布 / 订阅 / 退订（不经过生命周期锁，可并发）
  MqttSubscriptionTable.cs      订阅表（有序，线程安全）
  MqttConnectionStateMachine.cs 状态的唯一来源
  MqttLifecycleGate.cs          生命周期异步锁
  MqttReconnectLoop.cs          带退避的后台重试驱动
  MqttReconnectController.cs    MQTT 特有的重连策略
  DependencyInjection/          MqttClientFactory、AddMqttClients()
  Sdk/                          ← 只有这里直接用 SDK 类型，全部 internal
```

由三条架构测试强制（`Tests/Junevy.Communication.Architecture.Tests/SdkAdapterArchitectureTests.cs`，只编译 net8.0）：

1. 适配包只引用 Core（不引用 Channels / Tcp 等）。
2. **公开 API 不出现 SDK 类型**：反射遍历导出类型的基类、接口与公开 / 受保护的字段、属性、方法、事件、构造函数，展开数组 / 指针 / byref / 泛型实参后检查程序集名（`SdkPublicApiWalker`）。私有成员不检查——`Sdk/` 里的内部类型用 SDK 类型是允许的。
3. `Sdk` 命名空间以外的类型不依赖 SDK（NetArchTest 读 IL，因此方法体里的引用也算）。

外加 `ArchitectureTests.SdkPrefixes` 登记的**程序集引用白名单**（Mqtt → `MQTTnet`；P6.2 → `Opc.Ua`；P6.3 → `FluentFTP`）。新增一个 SDK 包要改四处，每处一行：`SdkAdapterArchitectureTests.Adapters`（适配程序集、SDK 前缀、`Sdk` 命名空间）、`ArchitectureTests.SdkPrefixes`、`ArchitectureTests.LayerTable`（放在 `#if NET8_0_OR_GREATER` 里），以及 `Architecture.Tests.csproj` 里只在 net8.0 生效的 `ProjectReference`。

SDK 类型与本库同名时用 `using SdkXxx = MQTTnet.Xxx;` 别名引用（P6-C5）。实测撞名的至少有 `MqttClient`、`MqttClientOptions`、`MqttProtocolVersion`、`MqttUserProperty`。

## 日志

`ILoggerFactory` 可选。状态变化记 Information，每次操作记 Debug（主题、字节数或数量、耗时），失败记 Warning。**密码与正文任何级别都不记录**。SDK 的日志桥接到同一个工厂、类别 `Junevy.Communication.Mqtt.Sdk`，**只桥接 Warning 及以上**——无法逐条确认 SDK 的每一条日志都不含凭据与正文，按 P6-C10 的保守做法处理。

## 相关

- 作者视角的坑与做法：[[Agent协作/新增协议指南]] 的"SDK 协议适配"一节
- 复用件：Core 的 `CommResult`、`EventSubscribers`、`ExponentialBackoff`、`TimeoutScope`、`NamedFactory`
- 设计：设计文档 `docs/superpowers/specs/2026-10-10-communication-core-design.md` 第 14.3 节与第 20.8 节"实施修订（P6.1）"；协议族的总体位置见 [[架构设计/通讯库总体架构（协议族）]]