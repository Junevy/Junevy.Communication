# Changelog

本项目所有显著变更记录于此。格式参考 Keep a Changelog，版本遵循 SemVer。

## [Unreleased] — 通讯通道族 P1（Core / Channels / Tcp / Udp / Serial 1.0.0-preview.1）

### 新增（Added）

- 2026-10-10 [Core, Channels, Tcp, Udp, Serial, Testing] 通道族项目骨架（版本 `1.0.0-preview.1`，目标框架 `net472;net8.0`）：新增 6 个库项目与 5 个对应测试项目；公共设置位于 `build/Junevy.Communication.Common.props` 与 `build/Junevy.Communication.Tests.props`，各 csproj 显式 Import（不引入 `Directory.Build.props`，Modbus 项目不受影响）；库项目开启 `TreatWarningsAsErrors` 并保留 CS1591；net472 的可空性特性由 `build/Polyfills/NullableAttributes.cs` 提供；每个测试项目含 `ProjectSmokeTests.Runtime_MatchesTargetFramework`，证明测试在 .NET Framework 与 .NET 8 上实际执行。
- 2026-10-10 [Core] 新增 Core 通用工具：`CommErrorKind`（0–7 与 `ModbusErrorKind` 数值对齐，8–11 为通道族新增）；不可变的 `CommResult` / `CommResult<T>`（失败必须携带非 `None` 分类；`As<T>` 对成功结果抛 `InvalidOperationException`）；`IBackoffPolicy`、`FixedIntervalBackoff`、`ExponentialBackoff`（指数退避 + 抖动，随机数访问加锁，测试可注入种子）；`TimeoutScope`（区分超时与用户取消，`onAbort` 至多调用一次，`Dispose` 后不再调用）；`HexFormatter`（`AA-BB-CC` 格式，超长截断）；`NamedRegistry<T>`（由 `ModbusConnectionManager` 泛化：别名级联删除、别名环检测、`GetOrAdd` 竞态输家释放、同一实例只释放一次；释放优先 `DisposeAsync`，同步路径同步等待）。Modbus 项目未改动。同时为 net472 测试宿主增加 `build/Junevy.Communication.Tests.app.config`（`System.Memory` 绑定重定向，经 `build/Junevy.Communication.Tests.props` 的 `AppConfig` 仅对 net472 生效），修复 `FileLoadException`。

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
