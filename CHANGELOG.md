# Changelog

本项目所有显著变更记录于此。格式参考 Keep a Changelog，版本遵循 SemVer。

## [Unreleased] — 通讯通道族 P1（Core / Channels / Tcp / Udp / Serial 1.0.0-preview.1）

### 新增（Added）

- 2026-10-10 [Core, Channels, Tcp, Udp, Serial, Testing] 通道族项目骨架（版本 `1.0.0-preview.1`，目标框架 `net472;net8.0`）：新增 6 个库项目与 5 个对应测试项目；公共设置位于 `build/Junevy.Communication.Common.props` 与 `build/Junevy.Communication.Tests.props`，各 csproj 显式 Import（不引入 `Directory.Build.props`，Modbus 项目不受影响）；库项目开启 `TreatWarningsAsErrors` 并保留 CS1591；net472 的可空性特性由 `build/Polyfills/NullableAttributes.cs` 提供；每个测试项目含 `ProjectSmokeTests.Runtime_MatchesTargetFramework`，证明测试在 .NET Framework 与 .NET 8 上实际执行。
- 2026-10-10 [Core] 新增 Core 通用工具：`CommErrorKind`（0–7 与 `ModbusErrorKind` 数值对齐，8–11 为通道族新增）；不可变的 `CommResult` / `CommResult<T>`（失败必须携带非 `None` 分类；`As<T>` 对成功结果抛 `InvalidOperationException`）；`IBackoffPolicy`、`FixedIntervalBackoff`、`ExponentialBackoff`（指数退避 + 抖动，随机数访问加锁，测试可注入种子）；`TimeoutScope`（区分超时与用户取消，`onAbort` 至多调用一次，`Dispose` 后不再调用）；`HexFormatter`（`AA-BB-CC` 格式，超长截断）；`NamedRegistry<T>`（由 `ModbusConnectionManager` 泛化：别名级联删除、别名环检测、`GetOrAdd` 竞态输家释放、同一实例只释放一次；释放优先 `DisposeAsync`，所有释放都在注册表锁外进行，同步路径（`TryRemove`、`GetOrAdd` 竞态输家、`Dispose`）同步等待）。Modbus 项目未改动。同时为 net472 测试宿主增加 `build/Junevy.Communication.Tests.app.config`（`System.Memory` 绑定重定向，经 `build/Junevy.Communication.Tests.props` 的 `AppConfig` 仅对 net472 生效），修复 `FileLoadException`。
- 2026-10-10 [Channels] 新增字节通道族的抽象、模型与配置：通道接口 `IConnectable` / `IByteChannel` / `IClientChannel`；分帧与编码接口 `IFrameDecoder` / `IFlushableFrameDecoder` / `IFrameEncoder` / `IFrameCodecFactory`；扩展点 `IResponseMatcher` / `IFrameKeyExtractor` / `IHealthProbe` / `IConnectionInitializer` / `IChannelConfig` / `IChannelCreator`；模型 `ConnectionState`、`DisconnectReason`、`ConnectionStateChangedEventArgs`、`FrameReceivedEventArgs`、`RequestOptions`、`ConnectionStatistics`（属性线程安全，递增方法为 internal，可空时间戳以 UTC ticks 保存）；配置 `FramingOptions`、`HeartbeatOptions`、`ReconnectOptions`、`ChannelComponents` 及 `FramingMode`、`LengthFieldEncoding`、`CorrelationMode`、`QueueFullMode`、`ReconnectMode`、`PartialFrameAction` 枚举。新增六种分帧器（Raw / Delimiter / FixedLength / LengthField / StartEnd / IdleGap）按多段 `ReadOnlySequence<byte>` 切帧、不复制缓冲；分隔符与定长编码器；`FrameCodecFactory.Create` 在创建时校验并复制配置（非法参数抛 `ArgumentException` 族，不修改调用方对象）；`FrameDecodeException`；`ByteSequenceParser`（`hex:` 格式与转义文本）。
- 2026-10-10 [Testing] 新增共享测试套件（P1 部分，内部使用，不发布）：`DuplexStreamPair`（两个内存管道组成的双工流，`Abort` 立即唤醒挂起的读写并使两端抛出 `IOException`，`pauseWriterThreshold` 映射到背压）；`ScriptedTcpServer`（由 Modbus 测试工程复制并泛化，脚本接收 `Stream`，新增 `ReceivedBytes`、`ClosedByPeerCount`、`CloseConnection(index)`）与 `SilentTcpServer`（补充 `ClosedByPeerCount`）；`ScriptedUdpPeer`；`DeviceSimulator`（在任意流上按分帧规则应答，`HostTcp()` 以 TCP 承载）；`TimingAssert`（耗时区间断言）；`TestLogger`（线程安全地收集日志条目，实现 `ILogger` 与 `ILoggerFactory`，按级别查询）；`ByteArrayBufferWriter`（net472 的 `IBufferWriter<byte>` 实现）。
- 2026-10-10 [Channels] 新增帧路由（内部，供 StreamChannel / DatagramChannel 使用）：`PendingRequestTable` 实现三种关联模式（Sequential 同时至多一个在途请求；Matcher 按注册顺序判定；Keyed 按关联键字典匹配，同键在途或提取不到键时立即以 `InvalidRequest` 完成）、接收等待者（FIFO，注册时先扫描握手积压；所有模式下都使用 Matcher，Keyed 模式的请求则忽略 Matcher、按关联键匹配）、超时与取消（分别以 `Timeout` / `Cancelled` 完成，完成只发生一次）、迟到应答窗口（Sequential 窗口；Keyed 近期超时键最多 256 个；Matcher 无法识别迟到应答）、握手积压（上限 64 帧，超出抛 `FrameDecodeException`）与 `FailAll`；`PendingRequest` 管理计时器与取消注册，计时从 `StartTimer()` 起算；`FrameRouter` 按"在途请求认领 → 迟到应答丢弃 → 接收等待者认领 → 握手积压或派发"的顺序判定入站帧（迟到应答不会被无 Matcher 的接收等待者拿走）；认领在调用线程上同步完成。未认领帧经有界派发队列（`Wait` 背压；`DropOldest` / `DropNewest` 计入 `FramesDropped`，每 100 次丢弃记一次 Warning）由派发循环串行派发，单个订阅者异常只记 Error 日志。`StopAsync(drainTimeout)` 在超时内排空队列，超时后取消派发循环，剩余帧计入 `FramesDropped` 并记 Warning；第一次调用决定排空窗口，之后的调用等待同一次停止完成；`DisposeAsync` 等同 `StopAsync(0)`；在 `FrameReceived` 处理器内调用 `StopAsync` 只发出停止信号（剩余帧计入 `FramesDropped`，不等待派发循环，避免死锁），派发循环在处理器返回后退出。`FrameRouterTests` 覆盖计划 7.3 的 21 项，并增加并发规则测试（只完成一次、续延不内联、认领不经过队列）、迟到应答与接收等待者的顺序测试、Keyed 模式接收 Matcher 测试，停止排空、重复停止等待与处理器内停止（不死锁）测试。
- 2026-10-10 [Channels] 新增流通道（内部，供 TCP / 串口 / TLS 复用）`StreamChannel`：填充循环把 `Stream` 读入内部 `Pipe`（背压阈值 1 MiB），解析循环从 `Pipe` 分帧并经 `FrameRouter` 路由（net472 使用数组重载，net8.0 使用 `Memory` 重载）；`Start` 同时启动路由的派发循环，路由的生命周期由通道管理。发送经发送锁整帧写出：等待发送锁时取消只返回 `Cancelled`；写出期间超时、取消或 I/O 错误都中止传输并报告 `SendFailed`，且先报告再中止，避免读取端抢先以 `RemoteClosed` 报告。请求在 Sequential 模式下经请求锁串行化：超时或取消等待后，`ResetOnRequestTimeout` 为 true 时报告 `RequestTimeout`，否则在迟到应答窗口（`LateReplyWindow`，-1 表示等于请求超时）内继续持有请求锁，窗口结束或通道停止时释放；空负载请求以 `InvalidRequest` 拒绝。半帧与静默计时（`PartialFrameTimeout`，或可刷新分帧器的 `FlushTimeout`）以截止时间戳判定，计时器只负责唤醒解析循环；分帧与握手积压溢出按 `PartialFrameAction` 断开（报告 `PartialFrameTimeout` / `ProtocolViolation`）或丢弃。连接丢失时以 `ConnectionClosed` 失败在途等待者，并至多调用一次 `onFault`；主动停止不报告。停止顺序为设置停止标志 → 中止传输 → 完成内部管道并唤醒解析循环 → 启动路由停止 → 在 `drainTimeout` 内等待两个循环退出 → 失败在途等待者 → 等待路由停止。路由停止先于循环等待，因此在 `FrameReceived` 处理器内发起停止时，即使解析循环挂起在满队列上也不会互相等待。`StreamChannelSettings`（运行参数，构造时复制并校验，D5）与 `PooledBufferWriter`（基于 `ArrayPool<byte>` 的 `IBufferWriter<byte>`，替代 net472 上不存在的 `ArrayBufferWriter<T>`）同时新增。`StreamChannelTests` 共 31 项：计划 8.3 的 18 项，审阅者要求的 4 项（空负载、迟到窗口持锁、处理器内停止且队列满、停止排空派发队列），补充的 9 项（对端正常关闭、停止时写出阻塞不报告故障，以及下方修复所涉及的对端回应答后关闭、IdleGap 数据后关闭、半帧后关闭，和忽略取消令牌的 4 项）。
- 2026-10-10 [Testing] 新增 `NonCancellableStream`（公开）：包装任意 `Stream` 并忽略取消令牌（读取、写入、刷新都以 `CancellationToken.None` 调用内部流），模拟 net472 上 `NetworkStream` / `SerialPort.BaseStream` 不响应取消的行为；通道测试据此验证超时、半帧计时与停止在这种流上仍成立（依靠 abortTransport 打断挂起的 I/O）。

### 修复（Fixed）
- 2026-10-10 [Channels] 对端关闭时先处理完已收到的数据，再报告故障：填充循环读到 0 字节或出错时只记录原因（0 字节与 IOException 为 RemoteClosed，其他为 Error）并完成管道写端；解析循环先分帧并派发全部完整帧，可刷新分帧器整段交出残留数据，其余残留半帧丢弃并记 Warning、递增 ProtocolErrors，然后报告记录的原因（停止过程中不记录）。此前对端回完应答即断开时，在途请求会以 ConnectionClosed 失败，而不是拿到已收到的应答。
- 2026-10-10 [Channels] 写出失败（超时、取消或 I/O 错误）时每次写出至多报告一次 SendFailed 并中止传输，且先报告再中止；取消写出时中止不再依赖 TimeoutScope 回调（回调可能随 scope 释放被注销，导致传输未被中止）。

## [Unreleased] — 分支 refactor/modbus-p3-architecture（v2.0.0）

### 迁移指南（1.x → 2.0）

| 旧 | 新 |
|---|---|
| `result.IsSuccess = ...` 等对 `ModbusResult` 属性赋值 | 使用 `ModbusResult<T>.Success(...)` / `Fail(...)` |
| `ModbusExtensions.ReadCoils(modbus, ...)` 静态调用 | `modbus.ReadCoils(...)`（扩展方法语法）或 `ModbusBitExtensions.ReadCoils(modbus, ...)` |
| `new TcpProtocolParser(logger, verifier)` | `new TcpProtocolParser(validator, logger)` |
| `ModbusPduVerifier` | `IModbusPduValidator` / `ModbusPduValidator` |
| `factory.GetOrAdd(key, ModbusTcpClientConfig)`（按类型的重载） | `factory.GetOrAdd(key, config)`（`IModbusConfig` 重载，调用写法不变） |
| `new ModbusFactory(logger, loggerFactory, tcpParser, rtuParser, frameBuilder, manager)` | `new ModbusFactory(logger, creators, manager)`；或使用 `ModbusFactoryBuilder` |
| `ModbusFactoryBuilder.WithTcpParser(TcpProtocolParser)` | `WithTcpParser(IResponseParser)` |

### 新增（Added）

- 2026-10-08 `IModbusClientCreator`（传输创建策略）、`TcpClientCreator`、`RtuClientCreator`、`ModbusFactoryBuilder.WithCreator`：新增一种传输（例如 RTU over TCP）只需实现 `IModbusClientCreator` 并注册，无需修改工厂。
- 2026-10-08 `IModbusPduValidator` / `ModbusPduValidator`：与传输无关的 PDU 语义校验可替换实现。
- 2026-10-05 `ModbusFactoryBuilder`：无 Microsoft DI 容器场景下的 `ModbusFactory` 流式构建器（适配 Prism 等第三方容器直接 `RegisterInstance`）。提供 `WithLoggerFactory` / `WithTcpParser` / `WithRtuParser` / `WithFrameBuilder` / `WithConnectionManager` / `Build`；未设置项与 `AddModbusFactory` 的 DI 默认值一致。
- 2026-10-05 `ModbusErrorKind` 结构化错误分类（`ModbusResult.ErrorKind` + `Fail` 重载），重连判断不再依赖错误消息字符串匹配。
- 2026-10-05 `ModbusExceptionCode` 线上异常码枚举（含 `Describe()` 扩展）；`ModbusException.ErrorCode` 类型同步更换，参数校验统一改抛标准异常。
- 2026-10-05 `ModbusTransportBase` 公共传输基类：TCP/RTU 的请求/重试/重连骨架去重（两文件 1233 行 → 752 行）。
- 2026-10-05 TCP 真异步 I/O（NetworkStream + 全链路 `CancellationToken`，net8.0 取消即时生效；net472 在下一个 I/O 边界生效）。

### 变更（Changed，破坏性）

- 2026-10-08 `IModbusFactory.GetOrAdd` / `TryAdd` 的 4 个按配置类型的重载（`ModbusTcpClientConfig`、`ModbusRtuClientConfig`）合并为 `GetOrAdd(string, IModbusConfig)` 与 `TryAdd(string, IModbusConfig, out IModbus?)`；调用方源码不需要修改，未注册的配置类型抛出 `NotSupportedException`。
- 2026-10-08 `ModbusFactory` 的 6 个构造函数缩减为 2 个：`ModbusFactory()` 与 `ModbusFactory(ILogger<ModbusFactory>, IEnumerable<IModbusClientCreator>, IModbusConnectionManager)`。
- 2026-10-08 `ModbusFactoryBuilder.WithTcpParser` / `WithRtuParser` 的参数类型由具体解析器类改为 `IResponseParser`。
- 2026-10-08 `ModbusPduVerifier`（公开类，其方法均为 `internal`）删除，由 `IModbusPduValidator` / `ModbusPduValidator` 取代；`TcpProtocolParser(ILogger?, ModbusPduVerifier?)` → `TcpProtocolParser(IModbusPduValidator?, ILogger?)`，`RtuProtocolParser` 同理；解析器不再记录 RX 日志（RX 日志由客户端记录，消除 RTU 成功帧的重复 RX 日志）。
- 2026-10-08 静态类 `ModbusExtensions` 拆分为 `ModbusBitExtensions` / `ModbusRegisterExtensions` / `ModbusDiagnosticsExtensions`（命名空间不变，扩展方法签名不变，使用扩展方法语法的调用方无需修改；直接以 `ModbusExtensions.Xxx(...)` 静态调用的代码需要改类名）。
- 2026-10-08 `ModbusResult<T>` 改为 `sealed`，`IsSuccess` / `Data` / `ErrorMessage` / `ErrorKind` 只读（旧：`public set`）；`Fail(msg, ModbusErrorKind.None)` 抛出 `ArgumentException`。使用 `Success()` / `Fail()` 工厂方法的代码不受影响。
- 2026-10-08 `IModbus.ConnectAsync` 增加参数 `CancellationToken cancellationToken = default`：调用方源码兼容；自行实现 `IModbus` 的类型需要更新签名（以及在表达式树中调用 `ConnectAsync()` 的代码，例如 Moq 的 `Setup(m => m.ConnectAsync())`）；用户取消时抛出 `OperationCanceledException`，连接失败或超时仍返回 false。
- 2026-10-05 `ModbusTCP`/`ModbusRTU` 更名 `ModbusTcpClient`/`ModbusRtuClient`；命名空间 `TCP`/`RTU` → `Tcp`/`Rtu`；配置类更名 `ModbusTcpClientConfig`/`ModbusRtuClientConfig`。
- 2026-10-05 `ReadTimeOut`/`WriteTimeOut` → `ReadTimeout`/`WriteTimeout`；`ModbusRequest.Start`/`Length` → `StartAddress`/`Quantity`；`IntervalTime` → `FrameReadInterval`；删除 `SetPort`（`Port` 改为普通可写属性）与 `CheckConnection`（用 `IsConnected`）。
- 2026-10-05 库不再修改调用者的 `ModbusRequest`；`IModbusFrameBuilder` 协议参数显式化；`ModbusRequest` 收敛为纯数据类（移除 UI 事件）。
- 2026-10-05 目标框架 `net472;net6.0` → `net472;net8.0`；Microsoft.Extensions.* 与 System.IO.Ports 升级至 8.0.0；生成 NuGet 文档文件、MIT 许可证表达式。
- 2026-10-07 重试与重连解耦：Reconnect=false 时，需要重建连接的失败（超时、连接关闭、发送失败）立即返回真实错误，不再空转重试并被覆盖为 "Not connected"；未连接且 Reconnect=false 时请求立即返回，不再等待 RetryInterval。Reconnect=true 的行为不变。
- 2026-10-07 RegisterAlias 在目标键不存在或别名键等于目标键时返回 false（此前允许创建悬空别名）；RemoveAlias 对直接实例不再"删除后放回"；TryRemove / RegisterAlias / RemoveAlias 的注册表结构变更在内部锁下执行，消除并发移除/建别名与主键移除的竞态。

### 修复（Fixed）

- 2026-10-08 `TcpProtocolParser` 在 MBAP 长度字段为 0 时不再抛 `ArgumentOutOfRangeException`（`Slice` 收到负长度），改为返回 `ProtocolViolation`——解析器"永不抛异常"的契约对公开扩展点同样成立（经 `ModbusTcpClient` 调用时已被传输层的长度检查挡住，但直接使用解析器可触发）。
- 2026-10-08 `ReportServerId` 在帧头声明的事务字节数超过实际可用数据时返回 `ProtocolViolation`，不再返回被截断的负载。
- 2026-10-08 解析器校验响应功能码必须等于请求功能码（或其 `|0x80`），不一致返回 `ProtocolViolation`（此前不校验功能码，可能把别的功能码的应答当成本次应答接受）。
- 2026-10-05 TCP 事务 ID 由库内自增管理并按精确值匹配响应，消除旧响应错配风险（此前上线值恒为 `TransactionId+1` 且从不递增）。
- 2026-10-05 Modbus 异常响应（FC|0x80）在解析器层即返回失败（含异常码），作为终态不重试——此前在裸 `Request` 层被误报为成功；RTU 侧不再被"等待后续帧"吞成超时。
- 2026-10-05 结果式 API 不再逃逸异常（非法 MBAP 长度、RTU 零长解析帧改为失败返回），同步/异步行为对齐。
- 2026-10-05 `ModbusConnectionManager.GetOrAdd` 并发竞态不再泄漏输家实例（输家安全释放后进入赢家路径）。
- 2026-10-05 DI 容器中 `IModbusConnectionManager` 与 `IModbusFactory` 共享同一注册表。
- 2026-10-05 Connect/Disconnect 与请求经 `requestLock` 串行化，消除连接重建与在途请求的竞态。
- 2026-10-07 TCP 异步请求的 ReadTimeout / WriteTimeout 生效（此前 Socket.ReceiveTimeout 对 NetworkStream.ReadAsync/WriteAsync 无效，服务端不应答时 RequestAsync 永久挂起）；ReadTimeout 为整帧总时限，超时返回 ErrorKind.Timeout，用户取消返回 ErrorKind.Cancelled。
- 2026-10-07 异步请求的自动重连改用 OpenConnectionAsync（此前 Task.Run 包装同步连接，占用线程池线程且不响应取消）；TCP 异步连接中用户取消不再被当作连接超时吞掉。
- 2026-10-07 高层 API（ReadCoils / ReadHoldingRegisters / ReadWriteMultipleRegisters / GetCommEvent* / ReportServerId / ReadExceptionStatus 等）保留底层的 ErrorKind（此前一律重置为 Unspecified）；底层返回数据过短时返回 ProtocolViolation，不再抛出 ArgumentException；ModbusHelper.ParseCoils 的长度检查修正为 3 + 字节数；通信异常按类型归类为 Timeout / ConnectionClosed 而非 Unspecified；RTU 接收缓冲溢出报 ProtocolViolation。
- 2026-10-07 ModbusConnectionManager.TryRemove 移除带别名的主连接时，同时移除所有指向它的别名并释放实例（此前实例既不释放也不可达，串口/套接字泄漏）；同一实例被多个直接键引用时仅在最后一个引用移除时释放。
- 2026-10-08 TCP 异步连接：net8.0 使用 Socket.ConnectAsync(CancellationToken)，net472 使用 FromAsync + Task.WhenAny 实现真异步（此前 net472 阻塞调用线程到 ConnectTimeout）；同步连接释放 AsyncWaitHandle。
- 2026-10-08 TCP 收到 ProtocolViolation（非法 PDU 长度、事务 ID 不匹配、协议 ID 非零、从站号不匹配）后销毁连接，避免残帧污染下一次请求；Modbus 异常响应不受影响。
- 2026-10-08 Dispose 与在途/排队请求互斥：在途请求返回 ConnectionClosed（不再抛 ObjectDisposedException 或 NullReferenceException），Dispose 返回时连接已关闭且无请求在途；Dispose 之后调用 Request/Connect 抛出 ObjectDisposedException，Disconnect 为空操作；不再释放内部 SemaphoreSlim。
