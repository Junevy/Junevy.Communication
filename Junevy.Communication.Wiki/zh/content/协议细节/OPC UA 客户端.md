---
tags: [协议细节, Junevy.Communication, OpcUa, OPC UA]
---

# OPC UA 客户端

`Junevy.Communication.OpcUa` 是本库第二个 **SDK 适配包**（P6.2）：只做客户端，**只面向 net8.0**。协议本身由 [OPC Foundation .NET Standard Stack 2.0.x](https://github.com/OPCFoundation/UA-.NETStandard)（MIT）实现，本包是一层薄适配——公开 API 里没有 SDK 类型，所有失败都翻译成 Core 的 `CommResult`。

> 版本 `1.0.0-preview.1`。**只与进程内托管的官方服务器联调过，没有对 KEPServerEX / Prosys / 西门子 / 倍福等真实服务器验证过。**
> **只支持 `None` 安全策略**，登录方式为匿名或用户名密码（2026-10-26 起）；很多现场服务器默认关掉了 `None`，或只在加密连接上接受用户名，这种连接直接返回 `NotSupported`，连请求都不会发。

## 连接流程（P62-D3）

① 用 `EndpointUrl` 发现端点 → ② 自己挑出 `SecurityMode = None` 且 `SecurityPolicyUri = None` 的端点 → ③ 检查它是否提供选项要的用户令牌策略（`UserName` 为 null 时匿名，否则用户名）→ ④ 建会话。

**②③ 必须由本包判定**：实测"只开放加密端点""不允许匿名"这两种服务器配置下，SDK 的 `ConnectAsync` 都是**一直阻塞**到调用方令牌触发，拿不到任何状态码（计划 F2）。照"等 SDK 报错再分类"写，三条否定用例一条都测不了。

`UseEndpoint(url, mode, policy)` 之后 **服务器报告的主机名与配置不同时，SDK 自己就把端点地址钉回发现地址**（计划 F19），因此第 ④ 步不需要本库改写。

## 登录身份（2026-10-26 加用户名登录）

- 公开 API 只多了 `OpcUaClientOptions.UserName` / `Password`：`UserName` 为 null（默认）即匿名；给了就不能是空白；只给 `Password` 不给 `UserName` 由校验器拒绝（多半是配置写漏，不悄悄按匿名处理）；`Password` 为 null 按空密码发送。范围由用户选定：**只在 `None` 安全策略上**，不连带安全连接。
- **身份只在 `Sdk/OpcUaSdkIdentity` 一处决定**：端点筛选（第 ③ 步）问它要哪种令牌、会话用它造 `UserIdentity`、`NotSupported` 的消息用它说明缺的是 "anonymous login" 还是 "user name login"。别处不再判断身份。
- 密码按 UTF-8 交给 SDK。令牌策略带安全策略时 SDK 用服务器证书加密密码（不需要客户端证书）；不带时在 `None` 通道上明文。官方服务器在 `None` 端点上给用户名令牌自动挂 `Basic256Sha256`（F35），所以测试走的是**加密**那条路，明文那条没有验证过。
- **首次连接被拒不能等 SDK 自己停**（F34）：`ManagedSession` 把首次连接失败也交给重连策略，用户名或密码错时无限重试，`ConnectAsync` 挂到 `ConnectTimeout` 才以 `Timeout` 返回。`Sdk/OpcUaSdkReconnectPolicy` 包住 SDK 的 `ReconnectPolicy`：会话建立之前，状态码经 `OpcUaSdkErrorMap.KindOf` 归为 `AuthenticationFailed` 的就停止重试，`ConnectAsync` 随即以服务器的状态码（通常 `BadUserAccessDenied`）返回 `AuthenticationFailed`，约 1 个 `ReconnectInterval`（SDK 第一次问策略时拿到的还是 Good）。会话建立之后（`MarkEstablished`）不再拦截，P62-D2 的无限重连不变。这条也修正了"匿名被拒"原先同样表现为 `Timeout` 的问题。
- 密码不进日志，也不进错误消息（测试断言）。
- 测试服务器：`OpcUaTestServerOptions.UserName` / `Password` 设置后公布用户名策略并注册 `UserNamePasswordAuthenticator`；匿名验证器不受影响（F35 负控）。只公布用户名策略却不注册验证器时，SDK 的 `ConnectAsync` 会阻塞（F2）。

## NodeId 写法（P62-D5）

对外只有 OPC UA 标准文本，SDK 的 `NodeId` 不外露。接受 `i=` / `ns=` 三种标识，以及**命名空间 URI 长形式** `nsu=<URI>;s=…`。

- `nsu=` 只能走 `NodeId.ParseLongForm(text, session.NamespaceUris)`，SDK 的 `NodeId.Parse` 不认识它；
- 解析失败有**两种异常类型**（`ArgumentException` 与带 `NodeIdParseError` 的 `ServiceResultException`），两种都要收成 `InvalidRequest`；
- **空串会被 SDK 解析成 `i=0`**，必须由本包先拒绝；
- **配置里应当用 `nsu=`**：命名空间索引只对某一次服务器进程有效，重启后同一个变量可能落到别的索引上，写死索引会静默指向错误的东西。

## 值映射（P62-D6、Q41）

唯一的映射点是 `Sdk/OpcUaSdkValues`，读、写、订阅三条路径共用，计划第 17.2 节的每一行都有读与写两个方向的测试。

**两张表都必须按 `Type` 精确相等分派，不能用 C# 类型模式**：.NET 7 起支持"扩展数组转换"，`sbyte[]` 在 `is byte[]` 下为真、硬转换也成功，用类型模式会把 `sbyte[]` 误判成 `byte[]`（ByteString）。

2.0 的 SDK 把 `object` 换成了 struct `Variant`，带来三条连带变化：

- Guid 与 DateTime 在 SDK 侧变成 `Uuid` 与 `DateTimeUtc`（数组同理），对外仍是 BCL 的 `Guid` / `DateTime`，转换只发生在 `Sdk/` 里；
- `Variant.From<T>` 要求 `T` 是 struct，**`Variant.From<object>(42)` 编译不过**——写入路径只能按 .NET 类型逐个构造；
- `byte[]` 与 ByteString 的歧义是真的：同一个 `byte[]` 值写进 ByteString 节点按 ByteString 解释、写进 Byte 数组节点按 Byte 数组解释，只能靠节点的数据类型区分。

写入**不做类型转换**：类型必须与节点的数据类型一致（Int16 节点要写 `short`），不符由服务器逐项返回 `BadTypeMismatch`。映射表里没有的 .NET 类型**整批失败**并返回 `InvalidRequest`（继续写其余项只会造成"半写成功"，对现场排障更糟）。

## 批量与逐项状态（P62-D7）

读写返回与输入**同序、同长**的逐项结果，逐项状态码来自服务器，一项 Bad 不影响其余项。空列表直接成功、不发请求。

**分批由 SDK 做，本包不写第二份**：读写走 `SessionClientBatched`（按服务器的 `MaxNodesPerRead` / `MaxNodesPerWrite` 自动切包），浏览走 SDK 的 `Opc.Ua.Client.Browser`。

浏览的两个坑：

- `requestedMaxReferencesPerNode = 0`（"服务器默认"）时服务器的 `MaxNodesPerBrowse` **不生效**，一次返回全部引用、根本不产生继续点——本包必须显式传一个很小的上限；
- 手写 `BrowseAsync` + `BrowseNextAsync` 循环在 2.0 里**第一次续取就会被判 `BadContinuationPointInvalid`**，而且 `BrowseDescription` 在 2.0 没有 `ContinuationPoint` 成员（继续点只在响应里）。

## 状态与重连（P62-D2、D11）

状态只有一个来源：`OpcUaSessionStateMachine`。四个状态 `Disconnected` / `Connecting` / `Connected` / `Reconnecting`，**只有状态真的变了才通知**——SDK 的 `ConnectionStateChanged` **每次重试都触发**，必须在这里合并（F17）。

重连由 SDK 的 `ManagedSession` 做（指数退避 + 抖动，不限次数），本包**不写第二个重连循环**，也不接入通道族的 Supervisor。

**`Reconnecting` 期间四个操作必须返回 `NotConnected`，而且拦截条件是"状态"而不是"有会话对象"**（计划 Task 7 的变异记录）：放开这一层之后，读请求会交给 SDK，而 SDK 的重连重试次数不限，那次读就一直挂下去（实测超过 5 分钟不返回）。

`ConnectAsync` 在 `Connecting` / `Reconnecting` 期间返回 `InvalidRequest`（不排队），已 `Connected` 时直接成功。首次连接失败不启动重连（P6-C11）；首次连接进行中 SDK 自己会重试到 `ConnectTimeout`，但身份或证书类拒绝立即停止（见"登录身份"，F34）。

**会话只在调用方还在等的时候挂上**（P6.2 验收 R1）：调用方因超时、取消或释放被摘出去之后，SDK 那条后台连接照样可能连上（端点发现不理令牌，F23）。最初的实现在后台任务里直接挂会话，结果是"调用方拿到 `Cancelled`，客户端却挂着一个会话、服务器上多一个会话"，再连一次还会把它覆盖成泄漏。现在后台任务只返回会话，由 `OpenAsync` 挂上；摘出去之后才连上的会话由续体当场关掉。

**断开与晚到的状态事件**（R2）：断开、释放都是**先摘下会话、再改状态、最后慢慢关**；SDK 状态事件的处理器只认当前挂着的那个会话，摘下的会话晚到的事件直接丢掉，否则断开之后状态可能被改回 `Reconnecting` / `Connected`，而客户端手里已经没有会话（与 SECS 的 R8 是同一类问题）。连接成功后用 `TryMoveTo(Connecting → Connected)`：会话挂上那一刻 SDK 若已报断线，不能再被改回 `Connected`。

## 订阅（P62-D10）

- 处理器**作为参数**传入而不是订阅之后才挂事件，因此初始值不会丢；
- `SubscribeAsync` 返回前等每个监控项创建完（F15：创建是异步的），逐项状态在 `ItemStatuses`，一项 Bad 不影响其余项；
- **三个必须记住的 SDK 陷阱**：2.0 的 `SubscriptionOptions.PublishingEnabled` **默认 false**（不显式打开就一个通知都不会来，极易误判成"订阅没实现"）；必须先 `session.AddSubscription(...)` 再 `CreateAsync`；监控项是否建好要看 `MonitoredItem.Status` 而不是 `MonitoredItem.Created`；
- **通知值是 `IEncodeable`，真正的 `DataValue` 在 `MonitoredItemNotification.Value` 上**（F27）。Task 7 之前的实现读错了这一层（把 `DataValue` 赋成 `default` 就把通知丢了），每条通知带的都是空值——事件型通知要用类型判断忽略，不能靠一个永远不会抛的强制转换。
- 通知经 `EventSubscribers.InvokeEach` 交付，sender 是客户端：多播处理器里一个订阅者抛异常，其余订阅者照常收到（验收 R7）。
- `SubscribeAsync` 的令牌只管创建；创建整体受 `OperationTimeout` 约束，失败时把已挂到会话上的订阅摘掉（验收 R6）。订阅选项在 `SubscribeAsync` 里校验，抛 `ArgumentException` 族，与 `Create` 一致（验收 R5）。

**删除订阅的两个 SDK 陷阱**（验收 R3、R4，F30、F32）：

1. `Subscription.DeleteAsync` **只删服务器上的订阅，不从会话摘下**。订阅还挂在会话上，会话恢复时 SDK 会把它在新服务器上重建出来——释放过的订阅"复活"。必须用 `session.RemoveSubscriptionAsync`。
2. **摘除不能被取消，也不能在重连期间先发出去**。重连期间 SDK 把这次请求压到会话重新激活的那一刻发出，此时订阅还拿着旧的订阅号，服务器回 `BadSubscriptionIdInvalid`，订阅留在会话上随后被重建；若这时再把 SDK 的订阅对象 `Dispose` 掉，之后**每一次重连都会因为"启动已释放订阅的保活计时器"失败**，同时在服务器上留下一个重建出来的订阅（实测 20 次重试留下 20 个）。现在的做法：先等 SDK 报 `Connected`（它在转移或重建完订阅之后才报），再摘除；只有摘除成功才释放 SDK 对象；重连期间 `DisposeAsync` 立刻返回，摘除由后台完成。

## 错误分类

全包**唯一**一处由 SDK 的异常或状态码推出 `CommErrorKind` 的地方是 `Sdk/OpcUaSdkErrorMap`（P6-C7、Q38），完整表见包 README 与计划第 17.1 节，每一行都有测试。判定规则：

- **状态码的 Good / Uncertain / Bad 由本包自己从最高两位算**，不复用 SDK 的判定函数——SDK 的 `IsUncertain(0xC0000000)` 与 `IsBad(0xC0000000)` **会同时为真**（F7）；
- `StatusCode.LookupSymbolicId` 对未知码返回**空串**（不是 `0x…` 形式）且**忽略子码**（`0x00000001` → `"Good"`），空串时本包回退成 `0x{code:X8}`（F6）；
- **符号名不用 `new StatusCode(code).SymbolicId`**（2026-10-25）：SDK 2.0.1 的 `StatusCode` 与 `StatusCodes` 的静态构造函数互相引用，两个线程同时首次触及时，`StatusCode` 的静态构造函数可能把已登记的完整名称表重置为 31 项，此后整个进程查不到其余码的名字（调试助手全量并行测试中复现两次，显示 `0x80340000`）。`Sdk/StatusNames` 改为从 `StatusCodes` 的 274 个公开字段建表（字段自带名字），查不到再问 SDK，最后回退十六进制；`StatusNamesTests` 逐字段核对并做并发查询；
- **超时与取消归本包判定**：`ManagedSessionBuilder.ConnectAsync` 没有超时参数，端点发现阶段**完全不理会取消令牌**（F23）。因此本包开 `TimeoutScope`，再用 `Task.WaitAsync` 把调用方摘出去；SDK 那条后台任务继续跑完并挂 `ContinueWith` 观察掉，摘出去之后才连上的会话当场关掉。
- **读、写、浏览、订阅也各包一个 `OperationTimeout` 作用域**，取消与超时以作用域为准：SDK 会把调用方的取消包成带 Bad 码的服务异常（F31，订阅时实测归成了 `RemoteError`）。
- 服务器停机后的读抛 `ServiceResultException(BadConnectionClosed)`，**不是逐项状态码**（F16），状态码要沿异常链取。
- 并发的断开刚把会话关掉时，SDK 连命名空间表都不给（抛"未连接"），调用中的会话被释放则抛 `ObjectDisposedException`：前者归 `NotConnected`，后者归 `ConnectionClosed`，都不能让异常逃出公开方法（验收 R8，原实现会抛 `InvalidOperationException`）。
- 逐项结果是**数据**，不是失败；只有单节点便捷方法（P62-D8）与浏览（P62-D9）会把 Bad 状态变成失败。

## 证书

**`None` 策略下 SDK 不需要客户端证书**：`DisableCertificateAutoCreation = true` 时 pki 目录里一个文件都没有。但 `AddSecurityConfiguration(subjectName, pkiRoot, …)` 本身是必需的一步，因此保留 `PkiRoot` 选项（默认 `%LocalAppData%\Junevy.Communication\OpcUa\pki`），本包**不会**生成自签名证书。计划第 6 节的公开 API 变动只有这一项，且已预先批准。

## SDK 隔离的结构（P6-C4、C5）

```
Junevy.Communication.OpcUa/
  OpcUaSessionState.cs / OpcUaNodeClass.cs / OpcUaStatusCode.cs   公开薄类型
  OpcUaValue.cs / OpcUaWriteValue.cs / OpcUaReference.cs           值模型
  OpcUaClientOptions.cs / OpcUaSubscriptionOptions.cs             POCO，可从 appsettings 绑定
  OpcUaOptionsValidator.cs       校验集中在一处
  OpcUaClient.cs                 公开门面（四个操作的准入判断在 TryBeginOperation）
  OpcUaClientExtensions.cs       单节点便捷方法
  OpcUaLifecycle.cs              连接 / 断开 / 释放（都经过生命周期异步锁）
  OpcUaSessionStateMachine.cs    状态的唯一来源
  DependencyInjection/          OpcUaClientFactory、AddOpcUaClients()
  Sdk/                           ← 只有这里直接用 SDK 类型，全部 internal
    OpcUaSdkConnection.cs        建配置 → 选端点 → 建会话 → 收状态
    OpcUaSdkSession.cs           ManagedSession 外面的薄壳
    OpcUaSdkConfiguration.cs     应用配置、遥测上下文、端点选择
    OpcUaSdkIdentity.cs          登录身份的唯一选择点（匿名 / 用户名）
    OpcUaSdkReconnectPolicy.cs   SDK 重连策略外面一层：首次连接被拒时停止重试
    OpcUaSdkOperations.cs        读写浏览的请求组装与逐项映射
    OpcUaSdkSubscribeRequest.cs  订阅请求的装配
    OpcUaSdkSubscription.cs      监控项、等待创建、交付通知、删除
    OpcUaSdkValues.cs            值映射（Q41 单一来源）
    OpcUaSdkNodeId.cs / OpcUaSdkErrorMap.cs / StatusNames.cs
```

`OpcUaLifecycle` 只管策略（序列化、状态、释放），SDK 交互全在 `Sdk/OpcUaSdkConnection`——架构测试抓到过一次真实越界（生命周期类直接用了 `Opc.Ua`）。

## 测试服务器

进程内托管官方服务器，走**通用宿主**（`HostApplicationBuilder` + `AddOpcUa().AddOpcTcpTransport().AddServer(...).AddNodeManager(...)`），不是 `StandardServer` 直调：只有 `OpcUaServerHostedService` 才是真正监听端口的那一步。实测的四个坑：

- `OpcUaServerOptions.OperationLimits` **默认 null**，直接赋值会 NRE，要用 `ConfigureBuilder = b => b.SetOperationLimits(...)`（F10）；
- 地址空间里只写 `OnRead` **不够**（服务器一直回 `BadWaitingForInitialData`），必须 `Bind` + `SetValue`；不设 `Writable(true)` 就永远 `BadNotWritable`（F11）；
- 自定义 `IRebasableNodeIdFactory` 会让服务器**起不来**，因此保留默认数字 NodeId，建地址空间时把 NodeId 记下来（F12）；
- `AddNodeManager` 的回调是在宿主启动期间调用的，此时 TCP 已在监听但地址空间还没建好，连上去会拿到 `BadServerHalted`；`AddStartupTask` 注册的回调**不会被调用**（F20）。因此起好之后要用一次真实会话探路。

监控项本身也不可靠：`NotifyChange` 之后不一定每次都发通知，后续通知里的值与状态也不可靠（F29）——"变更后再来一条"只能反复写入直到满足条件。

**服务器侧的计数**：宿主不把 `StandardServer` 放进容器，测试服务器给 `IOpcUaServerFactory` 包一层把实例记下来，再从 `CurrentInstance` 读 `SessionCount` / `SubscriptionCount`，并用 `SessionManager.SessionActivated` 数"后台连接是否真的连上过"（`ActivatedSessions`）。会话泄漏、订阅复活这类缺陷只能靠这几个数看出来。

## 依赖影响

`OPCFoundation.NetStandard.Opc.Ua.Client` 2.0.1 传递依赖 33 个包，把 `Microsoft.Extensions.*` 带到 **10.0.12 或更高**，外加 `System.Text.Json` / `System.IO.Pipelines` / `System.Threading.RateLimiting` 10.0.12、`Polly` 8.4.2、`BitFaster.Caching` 2.6.1。测试项目因此在自己 csproj 里用 `PackageReference Update` 把 `Microsoft.Extensions.DependencyInjection` 提到同一版本（否则 NU1605 降级错误），**不改共享的 `Tests.props`**。README 开头写明这一影响。

## 日志

`ILoggerFactory` 可选。状态变化记 Information，每次操作记 Debug（节点数、耗时），失败记 Warning。**任何级别的日志都不出现节点的值**。SDK 2.0 自带 `ILogger` 集成，通过 `DefaultTelemetry.Create(...)` 构造的遥测上下文交给它。

## 相关

- 作者视角的坑与做法：[[Agent协作/新增协议指南]] 的"SDK 协议适配"一节
- 复用件：Core 的 `CommResult`、`EventSubscribers`、`TimeoutScope`、`NamedFactory`
- 设计：设计文档 `docs/superpowers/specs/2026-10-10-communication-core-design.md` 第 14.2 节与第 20.9 节"实施修订（P6.2）"；SDK 摩擦 F1–F35 见 `docs/superpowers/plans/2026-10-12-p62-opcua.md` 第 19.2.13 节；协议族的总体位置见 [[架构设计/通讯库总体架构（协议族）]]