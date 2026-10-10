# 通道与传输 API 参考（TCP / UDP / 串口 / TLS）

<cite>
**本文引用的文件**
- [Junevy.Communication.Core/Results/CommResult.cs](file://Junevy.Communication.Core/Results/CommResult.cs)
- [Junevy.Communication.Core/Results/CommErrorKind.cs](file://Junevy.Communication.Core/Results/CommErrorKind.cs)
- [Junevy.Communication.Core/Resilience/ExponentialBackoff.cs](file://Junevy.Communication.Core/Resilience/ExponentialBackoff.cs)
- [Junevy.Communication.Core/Registry/NamedRegistry.cs](file://Junevy.Communication.Core/Registry/NamedRegistry.cs)
- [Junevy.Communication.Core/Utils/TimeoutScope.cs](file://Junevy.Communication.Core/Utils/TimeoutScope.cs)
- [Junevy.Communication.Channels/Abstractions/IConnectable.cs](file://Junevy.Communication.Channels/Abstractions/IConnectable.cs)
- [Junevy.Communication.Channels/Abstractions/IByteChannel.cs](file://Junevy.Communication.Channels/Abstractions/IByteChannel.cs)
- [Junevy.Communication.Channels/Abstractions/IClientChannel.cs](file://Junevy.Communication.Channels/Abstractions/IClientChannel.cs)
- [Junevy.Communication.Channels/Abstractions/IFrameDecoder.cs](file://Junevy.Communication.Channels/Abstractions/IFrameDecoder.cs)
- [Junevy.Communication.Channels/Abstractions/IHealthProbe.cs](file://Junevy.Communication.Channels/Abstractions/IHealthProbe.cs)
- [Junevy.Communication.Channels/Abstractions/IConnectionInitializer.cs](file://Junevy.Communication.Channels/Abstractions/IConnectionInitializer.cs)
- [Junevy.Communication.Channels/Abstractions/IChannelCreator.cs](file://Junevy.Communication.Channels/Abstractions/IChannelCreator.cs)
- [Junevy.Communication.Channels/Models/ConnectionStatistics.cs](file://Junevy.Communication.Channels/Models/ConnectionStatistics.cs)
- [Junevy.Communication.Channels/Models/DisconnectReason.cs](file://Junevy.Communication.Channels/Models/DisconnectReason.cs)
- [Junevy.Communication.Channels/Options/ClientChannelSettings.cs](file://Junevy.Communication.Channels/Options/ClientChannelSettings.cs)
- [Junevy.Communication.Channels/Options/ChannelComponents.cs](file://Junevy.Communication.Channels/Options/ChannelComponents.cs)
- [Junevy.Communication.Channels/Options/FramingOptions.cs](file://Junevy.Communication.Channels/Options/FramingOptions.cs)
- [Junevy.Communication.Channels/Options/HeartbeatOptions.cs](file://Junevy.Communication.Channels/Options/HeartbeatOptions.cs)
- [Junevy.Communication.Channels/Options/ReconnectOptions.cs](file://Junevy.Communication.Channels/Options/ReconnectOptions.cs)
- [Junevy.Communication.Channels/Options/Enums.cs](file://Junevy.Communication.Channels/Options/Enums.cs)
- [Junevy.Communication.Channels/Lifecycle/StreamClientChannel.cs](file://Junevy.Communication.Channels/Lifecycle/StreamClientChannel.cs)
- [Junevy.Communication.Channels/Factory/ChannelFactory.cs](file://Junevy.Communication.Channels/Factory/ChannelFactory.cs)
- [Junevy.Communication.Channels/Factory/ChannelFactoryBuilder.cs](file://Junevy.Communication.Channels/Factory/ChannelFactoryBuilder.cs)
- [Junevy.Communication.Channels/DependencyInjection/ChannelServiceCollectionExtensions.cs](file://Junevy.Communication.Channels/DependencyInjection/ChannelServiceCollectionExtensions.cs)
- [Junevy.Communication.Channels/Extensions/ByteChannelTextExtensions.cs](file://Junevy.Communication.Channels/Extensions/ByteChannelTextExtensions.cs)
- [Junevy.Communication.Channels/Framing/FrameCodecFactory.cs](file://Junevy.Communication.Channels/Framing/FrameCodecFactory.cs)
- [Junevy.Communication.Channels/Framing/ByteSequenceParser.cs](file://Junevy.Communication.Channels/Framing/ByteSequenceParser.cs)
- [Junevy.Communication.Tcp/Client/TcpClientChannel.cs](file://Junevy.Communication.Tcp/Client/TcpClientChannel.cs)
- [Junevy.Communication.Tcp/Client/TcpClientChannelConfig.cs](file://Junevy.Communication.Tcp/Client/TcpClientChannelConfig.cs)
- [Junevy.Communication.Tcp/Client/TcpClientChannelCreator.cs](file://Junevy.Communication.Tcp/Client/TcpClientChannelCreator.cs)
- [Junevy.Communication.Tcp/Options/TcpChannelComponents.cs](file://Junevy.Communication.Tcp/Options/TcpChannelComponents.cs)
- [Junevy.Communication.Tcp/Options/TcpSocketOptions.cs](file://Junevy.Communication.Tcp/Options/TcpSocketOptions.cs)
- [Junevy.Communication.Tcp/Options/TcpKeepAliveOptions.cs](file://Junevy.Communication.Tcp/Options/TcpKeepAliveOptions.cs)
- [Junevy.Communication.Tcp/Server/TcpServer.cs](file://Junevy.Communication.Tcp/Server/TcpServer.cs)
- [Junevy.Communication.Tcp/Server/TcpServerConfig.cs](file://Junevy.Communication.Tcp/Server/TcpServerConfig.cs)
- [Junevy.Communication.Tcp/Server/ITcpServer.cs](file://Junevy.Communication.Tcp/Server/ITcpServer.cs)
- [Junevy.Communication.Tcp/Server/ITcpSession.cs](file://Junevy.Communication.Tcp/Server/ITcpSession.cs)
- [Junevy.Communication.Tcp/Server/IConnectionFilter.cs](file://Junevy.Communication.Tcp/Server/IConnectionFilter.cs)
- [Junevy.Communication.Tcp/Tls/CertificateSource.cs](file://Junevy.Communication.Tcp/Tls/CertificateSource.cs)
- [Junevy.Communication.Tcp/Tls/TcpClientTlsOptions.cs](file://Junevy.Communication.Tcp/Tls/TcpClientTlsOptions.cs)
- [Junevy.Communication.Tcp/Tls/TcpServerTlsOptions.cs](file://Junevy.Communication.Tcp/Tls/TcpServerTlsOptions.cs)
- [Junevy.Communication.Tcp/DependencyInjection/TcpChannelServiceCollectionExtensions.cs](file://Junevy.Communication.Tcp/DependencyInjection/TcpChannelServiceCollectionExtensions.cs)
- [Junevy.Communication.Udp/IUdpChannel.cs](file://Junevy.Communication.Udp/IUdpChannel.cs)
- [Junevy.Communication.Udp/UdpChannel.cs](file://Junevy.Communication.Udp/UdpChannel.cs)
- [Junevy.Communication.Udp/UdpChannelConfig.cs](file://Junevy.Communication.Udp/UdpChannelConfig.cs)
- [Junevy.Communication.Udp/UdpChannelCreator.cs](file://Junevy.Communication.Udp/UdpChannelCreator.cs)
- [Junevy.Communication.Udp/DependencyInjection/UdpServiceCollectionExtensions.cs](file://Junevy.Communication.Udp/DependencyInjection/UdpServiceCollectionExtensions.cs)
- [Junevy.Communication.Serial/ISerialChannel.cs](file://Junevy.Communication.Serial/ISerialChannel.cs)
- [Junevy.Communication.Serial/SerialChannel.cs](file://Junevy.Communication.Serial/SerialChannel.cs)
- [Junevy.Communication.Serial/SerialChannelConfig.cs](file://Junevy.Communication.Serial/SerialChannelConfig.cs)
- [Junevy.Communication.Serial/SerialChannelCreator.cs](file://Junevy.Communication.Serial/SerialChannelCreator.cs)
- [Junevy.Communication.Serial/DependencyInjection/SerialServiceCollectionExtensions.cs](file://Junevy.Communication.Serial/DependencyInjection/SerialServiceCollectionExtensions.cs)
</cite>

## 目录
1. [命名空间速查](#命名空间速查)
2. [Core 公开类型](#core-公开类型)
3. [通道接口与模型](#通道接口与模型)
4. [枚举](#枚举)
5. [共享配置选项](#共享配置选项)
6. [基类 ClientChannelSettings 与派生自定义通道](#基类-clientchannelsettings-与派生自定义通道)
7. [工厂、依赖注入与组件覆盖](#工厂依赖注入与组件覆盖)
8. [TCP 客户端](#tcp-客户端)
9. [TCP 服务端](#tcp-服务端)
10. [TLS 与套接字选项](#tls-与套接字选项)
11. [串口](#串口)
12. [UDP](#udp)
13. [文本扩展](#文本扩展)
14. [分帧工具](#分帧工具)

## 命名空间速查

| 包 | 命名空间 | 公开的主要类型 |
|---|---|---|
| `Junevy.Communication.Core` | `Junevy.Communication.Core.Results` / `.Resilience` / `.Registry` / `.Utils` / `.Diagnostics` | `CommResult`、`CommResult<T>`、`CommErrorKind`、`IBackoffPolicy`、`ExponentialBackoff`、`FixedIntervalBackoff`、`NamedRegistry<T>`、`TimeoutScope`、`HexFormatter` |
| `Junevy.Communication.Channels` | `Junevy.Communication.Channels`（抽象、模型、选项、工厂、扩展、基类） | `IClientChannel`、`IByteChannel`、`StreamClientChannel`、`ChannelFactory`、`ChannelComponents`、`ClientChannelSettings` 等 |
| 同上 | `Junevy.Communication.Channels.Framing` | `FrameCodecFactory`、`ByteSequenceParser`、`FrameDecodeException` |
| 同上 | `Junevy.Communication.Channels.DependencyInjection` | `ChannelServiceCollectionExtensions.AddChannels` |
| `Junevy.Communication.Tcp` | `Junevy.Communication.Tcp`（客户端、服务端、TLS、选项） | `TcpClientChannel`、`TcpServer`、`ITcpServer`、`ITcpSession` 等 |
| 同上 | `Junevy.Communication.Tcp.DependencyInjection` | `TcpChannelServiceCollectionExtensions.AddTcpChannels` |
| `Junevy.Communication.Udp` | `Junevy.Communication.Udp`；`.DependencyInjection` | `UdpChannel`、`IUdpChannel`、`UdpChannelConfig`；`UdpServiceCollectionExtensions.AddUdpChannels` |
| `Junevy.Communication.Serial` | `Junevy.Communication.Serial`；`.DependencyInjection` | `SerialChannel`、`ISerialChannel`、`SerialChannelConfig`；`SerialServiceCollectionExtensions.AddSerialChannels` |

内部类型（不对外）：`ConnectionSupervisor`、`StreamChannel`、`DatagramChannel`、`FrameRouter`、`PendingRequestTable`、`HeartbeatMonitor`、`TcpConnector`、`TcpSession`、`TcpSocketConfigurator`、`UdpDatagramTransport`、`SerialPortHandle` 等。

## Core 公开类型

| 类型 | 要点 |
|---|---|
| `CommResult` | 不可变、不带数据。`Success()` 返回缓存的单例；`Fail(string message, CommErrorKind kind, long? protocolErrorCode = null, Exception? exception = null)`，`kind` 不能为 `None`（抛 `ArgumentException`）。属性：`IsSuccess`、`ErrorKind`、`ErrorMessage`、`ProtocolErrorCode`、`Exception`；`As<T>()` 把失败转换为带数据类型的失败 |
| `CommResult<T>` | `Success(T data)`、`Fail(...)`；`Data`；`ToResult()`；`As<TOther>()` |
| `CommErrorKind` | `None`=0、`Unspecified`=1、`InvalidRequest`=2、`ConnectionClosed`=3、`Timeout`=4、`ProtocolViolation`=5、`RemoteError`=6、`Cancelled`=7、`NotConnected`=8、`AuthenticationFailed`=9、`ResourceExhausted`=10、`NotSupported`=11。0 至 7 与 Modbus 的 `ModbusErrorKind` 数值一致 |
| `IBackoffPolicy` | `int? GetDelay(int attempt)`：`attempt` 从 1 开始；返回毫秒数，返回 `null` 表示放弃。实现必须线程安全 |
| `FixedIntervalBackoff(int interval, int maxAttempts = 0)` | 每次等待相同的毫秒数；`maxAttempts = 0` 表示无限 |
| `ExponentialBackoff(int initialInterval, int maxInterval, double multiplier = 2.0, double jitter = 0.2, int maxAttempts = 0, Random? random = null)` | 第 n 次基础等待 `min(maxInterval, initialInterval × multiplier^(n−1))`，乘以 `1 + jitter × (2r − 1)`，结果取整并夹在 `[0, maxInterval]`。线程安全 |
| `NamedRegistry<T>`（`T : class`） | 构造 `NamedRegistry<T>(ILogger? logger = null)`；`TryGet`、`GetRequired<TResult>`、`TryAdd`、`GetOrAdd(key, Func<string, T>)`、`TryRemove`（级联删除指向它的别名）、`RegisterAlias`、`RemoveAlias`；`Count`（只数直接实例）、`Keys`（含别名）；`Dispose` / `DisposeAsync`。`GetOrAdd` 竞态输家立即释放 |
| `TimeoutScope` | `Start(int timeout, CancellationToken userToken, Action? onAbort = null)`。`timeout ≤ 0` 不设定时器。属性：`Token`、`IsTimedOut`（超时且用户未取消）、`IsUserCancelled`。`onAbort` 在超时或取消时调用，用于 net472 销毁套接字以中止阻塞 I/O |
| `HexFormatter.ToHex(ReadOnlySpan<byte> data, int maxBytes = 256)` | 十六进制文本，用于日志。调用方应先检查 `logger.IsEnabled(LogLevel.Debug)` |

## 通道接口与模型

| 类型 | 成员 |
|---|---|
| `IConnectable` | `string Name`；`ConnectionState State`；`bool IsConnected`；`ConnectionStatistics Statistics`；`event StateChanged`；`Task<CommResult> ConnectAsync(CancellationToken = default)`；`Task DisconnectAsync(CancellationToken = default)`；`Task<bool> WaitForConnectedAsync(int timeout, CancellationToken = default)` |
| `IByteChannel` | `Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken = default)`；`Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null, CancellationToken = default)`；`Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken = default)`；`event FrameReceived` |
| `IClientChannel` | `IConnectable` + `IByteChannel` + `IDisposable` + `IAsyncDisposable`。主动发起连接的通道实现它 |
| `IChannelConfig` | 标记接口，无成员 |
| `IChannelCreator` | `Type ConfigType`；`IClientChannel Create(string name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory)` |
| `IResponseMatcher` | `bool IsMatch(ReadOnlySpan<byte> request, ReadOnlySpan<byte> frame)`（`Matcher` 模式） |
| `IFrameKeyExtractor` | `bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key)`；`bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key)`（`Keyed` 模式） |
| `IHealthProbe` | `Task<CommResult> ProbeAsync(CancellationToken cancellationToken)` |
| `IConnectionInitializer` | `Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)`。每次连接（包括重连）都执行；服务端对每个新会话执行 |
| `IFrameDecoder` | `bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)`。数据非法时抛 `FrameDecodeException` |
| `IFlushableFrameDecoder : IFrameDecoder` | 增加 `int FlushTimeout` 与 `bool TryFlush(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)`。内置实现只有 `IdleGap` |
| `IFrameEncoder` | `void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)` |
| `IFrameCodecFactory` | `IFrameDecoder CreateDecoder()`；`IFrameEncoder CreateEncoder()` |
| `RequestOptions` | `int? Timeout`（`null` 使用配置的 `RequestTimeout`）；`IResponseMatcher? Matcher`（`null` 时按关联模式的默认规则） |
| `FrameReceivedEventArgs` | `byte[] Data`（独立副本）；`DateTimeOffset ReceivedAt`；`EndPoint? RemoteEndPoint`（UDP 为来源地址；字节流为 `null`） |
| `ConnectionStateChangedEventArgs` | `PreviousState`、`CurrentState`、`DisconnectReason Reason`、`Exception? Exception`、`int ReconnectAttempt` |
| `ConnectionStatistics` | 只读：`BytesSent`、`BytesReceived`、`FramesSent`、`FramesReceived`、`FramesDropped`、`ProtocolErrors`、`ReconnectCount`、`ConsecutiveHeartbeatFailures`、`LastSentAt`、`LastReceivedAt`、`ConnectedSince` |

`IByteChannel` 不声明 `IsConnected`。链路状态只在 `IConnectable` 上（设计 5.1 与计划 Task 3 的修正）。`ITcpSession` 因为不实现 `IConnectable`，自行声明 `IsConnected`。

## 枚举

| 枚举 | 成员（默认值加粗） |
|---|---|
| `ConnectionState` | `Disconnected`、`Connecting`、`Connected`、`Reconnecting`、`Disconnecting`、`Disposed` |
| `DisconnectReason` | `None`、`UserRequested`、`RemoteClosed`、`SendFailed`、`RequestTimeout`、`HeartbeatFailed`、`IdleTimeout`、`PartialFrameTimeout`、`ProtocolViolation`、`AuthenticationFailed`、`ReconnectExhausted`、`Error`、`Disposed` |
| `CorrelationMode` | **`Sequential`**、`Matcher`、`Keyed` |
| `QueueFullMode` | **`Wait`**、`DropOldest`、`DropNewest`（实际丢弃新到的帧，见字节通道族内部结构） |
| `FramingMode` | **`Raw`**、`Delimiter`、`FixedLength`、`LengthField`、`StartEnd`、`IdleGap` |
| `LengthFieldEncoding` | **`BinaryBigEndian`**、`BinaryLittleEndian`、`AsciiHex`、`AsciiDecimal` |
| `ReconnectMode` | `FixedInterval`、**`ExponentialBackoff`** |
| `PartialFrameAction` | **`Disconnect`**、`Discard`（串口在基类中固定为 `Discard`） |

## 共享配置选项

**`FramingOptions`**（分帧，`Junevy.Communication.Channels`）

| 属性 | 默认值 | 说明 |
|---|---|---|
| `Mode` | `Raw` | 分帧模式 |
| `MaxFrameLength` | 65536 | 最大帧长；`Delimiter` 按分隔符之前的内容计量 |
| `Delimiters` | `null` | 分隔符列表（`Delimiter` 模式必填，至少一个） |
| `KeepDelimiter` | `false` | 交付的帧是否保留分隔符 |
| `AppendDelimiterOnSend` | `true` | 发送时是否追加 `Delimiters[0]` |
| `FrameLength` | 0 | `FixedLength` 的帧长 |
| `LengthFieldOffset` / `LengthFieldSize` | 0 / 2 | `LengthField` 的长度字段位置与宽度 |
| `LengthFieldEncoding` | `BinaryBigEndian` | 长度字段编码 |
| `LengthAdjustment` / `InitialBytesToStrip` | 0 / 0 | 长度修正值与剥离字节数 |
| `StartMarker` / `EndMarker` | `null` | `StartEnd` 模式的起止标记 |
| `KeepMarkers` | `true` | `StartEnd` 是否保留标记 |
| `GapTimeout` | 20 | `IdleGap` 的静默时间（毫秒，必须大于 0） |

**`HeartbeatOptions`**（心跳）

| 属性 | 默认值 | 说明 |
|---|---|---|
| `Enabled` | `false` | 是否启用心跳 |
| `Interval` | 5000 | 探测周期（毫秒，启用时必须为正） |
| `Timeout` | 2000 | 单次探测时限（毫秒，启用时必须为正） |
| `MaxFailures` | 3 | 连续失败次数（启用时不小于 1） |
| `OnlyWhenIdle` | `true` | 上次判定后有收发流量时跳过本次探测 |
| `Payload` | `null` | 内置探测发送的内容：文本，或以 `hex:` 开头的十六进制 |
| `ExpectedReply` | `null` | 期望的应答（整帧精确比较）；为空时发送成功即健康（仅 TCP 允许） |

**`ReconnectOptions`**（重连）

| 属性 | 默认值 | 说明 |
|---|---|---|
| `Enabled` | `false` | 是否启用后台重连 |
| `Mode` | `ExponentialBackoff` | 退避方式 |
| `Interval` | 1000 | 基础间隔（毫秒） |
| `MaxInterval` | 30000 | 指数退避的上限（毫秒） |
| `MaxAttempts` | 0 | 最大重连次数，0 表示无限 |
| `OnInitialFailure` | `false` | 首次连接失败也转入后台重连（设备晚于软件上电时使用） |

## 基类 ClientChannelSettings 与派生自定义通道

`ClientChannelSettings`（公开、密封）是 `StreamClientChannel` 的配置。它的超时默认值与 `TcpClientChannelConfig` 对齐（测试 `ClientChannelSettings_DefaultsMatchTcpClient` 锁定），但有一个例外：`ResetOnRequestTimeout` 在基类中为 `false`，TCP 客户端配置中为 `true`。

| 属性 | 默认值 |
|---|---|
| `HandshakeTimeout` | 5000 |
| `SendTimeout` | 2000 |
| `RequestTimeout` | 2000 |
| `LateReplyWindow` | -1 |
| `IdleTimeout` | 0 |
| `PartialFrameTimeout` | 0 |
| `DisconnectTimeout` | 1000 |
| `ReceiveBufferSize` | 4096 |
| `Framing` | `new FramingOptions()` |
| `Correlation` | `Sequential` |
| `ResetOnRequestTimeout` | `false` |
| `Heartbeat` | `new HeartbeatOptions()` |
| `Reconnect` | `new ReconnectOptions()` |
| `ReceiveQueueCapacity` | 1024 |
| `QueueFullMode` | `Wait` |

**派生自定义字节流通道**：`StreamClientChannel` 是公开的抽象基类，用于新增字节流传输（如命名管道、蓝牙串口）。构造函数为 `protected StreamClientChannel(string name, ClientChannelSettings settings, ChannelComponents? components, ILogger logger)`。派生类需要实现或可以覆写的成员：

| 成员 | 类型 | 说明 |
|---|---|---|
| `OpenStreamAsync(CancellationToken)` | `abstract Task<CommResult<Stream>>` | 打开传输并返回流。超时由派生类自行控制（TCP 用 `ConnectTimeout`，串口用 `OpenTimeout`） |
| `SecureStreamAsync(Stream, CancellationToken)` | `virtual Task<CommResult<Stream>>` | 在握手时限内包装流（TLS）。默认原样返回 |
| `AbortTransport()` | `abstract void` | 强制中止传输，使挂起的读写抛出异常（如销毁套接字） |
| `OnClosingAsync(CancellationToken)` | `virtual Task` | 优雅关闭前的动作（TCP 为 `Shutdown(Send)`），在 `DisconnectTimeout` 内执行 |
| `PartialFrameAction` | `virtual` 属性，默认 `Disconnect` | 串口覆写为 `Discard` |
| `DescribeEndpoint()` | `abstract string` | 日志中描述端点 |

公开成员（由基类实现）：`Name`、`State`、`IsConnected`、`Statistics`、`StateChanged`、`FrameReceived`、`ConnectAsync`、`DisconnectAsync`、`WaitForConnectedAsync`、`SendAsync`、`RequestAsync`、`ReceiveAsync`、`Dispose`、`DisposeAsync`。

## 工厂、依赖注入与组件覆盖

**`ChannelFactory`**（密封，`IDisposable`、`IAsyncDisposable`）：只管理客户端通道（`IClientChannel`）。`TcpServer` 不在工厂中（D17）。

- 构造：`ChannelFactory(IEnumerable<IChannelCreator> creators, ILoggerFactory? loggerFactory = null)`。同一配置类型注册两个创建器时构造期抛 `ArgumentException`。
- `GetOrAdd(name, config, components = null)`：同名已存在时返回已注册的实例，**忽略新配置**；名称不存在时按配置的精确运行时类型选择创建器。
- `TryAdd(name, config, out channel, components = null)`：同名已存在返回 `false`，不创建。
- `TryGet(name, out channel)`；`GetRequired<T>(name)`（类型不符抛异常）；`TryRemove(name)`（级联删除别名并释放实例）；`RegisterAlias(alias, existing)`；`RemoveAlias(alias)`；`Keys`（含别名）；`Count`（只数实例）；`Dispose` / `DisposeAsync`。
- 找不到与配置类型对应的创建器时抛 `NotSupportedException`（不是 `ArgumentException`）。

**`ChannelFactoryBuilder`**（无 DI 容器的宿主使用）：`Create()`、`WithLoggerFactory(ILoggerFactory)`、`WithCreator(IChannelCreator)`、`Build()`。

**依赖注入**：

| 方法 | 命名空间 | 行为 |
|---|---|---|
| `services.AddChannels()` | `Junevy.Communication.Channels.DependencyInjection` | `TryAddSingleton<ChannelFactory>`，从容器中的 `IChannelCreator` 集合创建 |
| `services.AddTcpChannels()` | `Junevy.Communication.Tcp.DependencyInjection` | 调用 `AddChannels()`，再 `TryAddEnumerable` 注册 `TcpClientChannelCreator` |
| `services.AddUdpChannels()` | `Junevy.Communication.Udp.DependencyInjection` | 同上，注册 `UdpChannelCreator` |
| `services.AddSerialChannels()` | `Junevy.Communication.Serial.DependencyInjection` | 同上，注册 `SerialChannelCreator` |

重复调用是幂等的。`TcpServer` 不在容器与工厂中，由宿主直接 `new` 并持有（D17）。

**`ChannelComponents`**（代码级覆盖，优先于配置中的同类设置）：

| 属性 | 类型 | 说明 |
|---|---|---|
| `FrameCodec` | `IFrameCodecFactory?` | 替代配置的分帧。对 UDP 非法（数据报本身就是一帧） |
| `Correlation` | `CorrelationMode?` | 替代配置的关联模式 |
| `KeyExtractor` | `IFrameKeyExtractor?` | `Keyed` 模式必填 |
| `Initializer` | `IConnectionInitializer?` | 连接后的握手 |
| `HealthProbe` | `IHealthProbe?` | 替代内置探测。服务端不接受（见服务端一节） |
| `ReconnectPolicy` | `IBackoffPolicy?` | 替代由 `ReconnectOptions` 推导的退避策略，仍受 `Reconnect.Enabled` 控制 |

## TCP 客户端

**`TcpClientChannel`**（密封，实现 `ITcpClientChannel`，继承 `StreamClientChannel`）

- 构造：`TcpClientChannel(TcpClientChannelConfig config, ILogger<TcpClientChannel>? logger = null, TcpChannelComponents? components = null)`；带名称的重载 `TcpClientChannel(string name, TcpClientChannelConfig config, ...)`。
- 成员：`Config`（返回构造时传入的对象本身；运行行为只使用构造时的快照）、`RemoteEndPoint`、`LocalEndPoint`、`IsTlsActive`。

**`TcpClientChannelConfig`**

| 属性 | 默认值 | 说明 |
|---|---|---|
| `Host` | `"127.0.0.1"` | 远端地址或主机名 |
| `Port` | 0 | 远端端口 |
| `LocalAddress` / `LocalPort` | `null` / 0 | 绑定本地网卡 |
| `ConnectTimeout` | 2000 | 单次连接时限，必须为正 |
| `HandshakeTimeout` | 5000 | TLS 与初始化器共用的时限 |
| `SendTimeout` / `RequestTimeout` | 2000 / 2000 | 写出与请求超时 |
| `IdleTimeout` / `PartialFrameTimeout` | 0 / 0 | 空闲与半帧超时 |
| `DisconnectTimeout` | 1000 | 优雅断开的排空时限 |
| `Framing` | `new FramingOptions()`（`Raw`） | 分帧 |
| `Correlation` | `Sequential` | 关联模式 |
| `ResetOnRequestTimeout` | `true` | 请求超时后断开并重建 |
| `LateReplyWindow` | -1 | 仅在 `ResetOnRequestTimeout = false` 时生效 |
| `Heartbeat` / `Reconnect` | `new HeartbeatOptions()` / `new ReconnectOptions()` | 心跳与重连 |
| `Socket` | `new TcpSocketOptions()` | 套接字选项 |
| `Tls` | `new TcpClientTlsOptions()` | TLS，默认关闭 |
| `ReceiveQueueCapacity` / `QueueFullMode` | 1024 / `Wait` | 派发队列 |

**`TcpClientChannelCreator`**（公开密封，`IChannelCreator`，`ConfigType` 为 `TcpClientChannelConfig`）。

## TCP 服务端

**`TcpServer`**（密封，实现 `ITcpServer`）：构造 `TcpServer(TcpServerConfig config, ILogger<TcpServer>? logger = null, TcpChannelComponents? components = null)`。由宿主直接创建并持有，不通过工厂（D17）。

**`ITcpServer`**（继承 `IDisposable`、`IAsyncDisposable`）

| 成员 | 说明 |
|---|---|
| `Name`、`Config`、`State`（`ServerState`）、`LocalEndPoint`、`SessionCount`、`Sessions` | 状态与会话集合 |
| `event StateChanged`（`ServerStateChangedEventArgs`） | 服务端状态变化 |
| `event SessionConnected`（`TcpSessionEventArgs`） | 握手完成后触发 |
| `event SessionClosed`（`TcpSessionClosedEventArgs`） | 携带 `DisconnectReason` 与异常 |
| `event FrameReceived`（`TcpSessionFrameEventArgs`） | 任一会话收到的帧 |
| `Task<CommResult> StartAsync(CancellationToken = default)` | 绑定并开始监听 |
| `Task StopAsync(CancellationToken = default)` | 关闭全部会话，等待至多 `StopTimeout` |
| `bool TryGetSession(long sessionId, out ITcpSession? session)` | 按 ID 查找 |
| `Task<CommResult> SendAsync(long sessionId, ReadOnlyMemory<byte> payload, CancellationToken = default)` | 发送给指定会话 |
| `Task<int> BroadcastAsync(ReadOnlyMemory<byte> payload, Func<ITcpSession, bool>? filter = null, CancellationToken = default)` | 返回成功发送的会话数 |

**`ITcpSession`**（继承 `IByteChannel`）：`long Id`、`IPEndPoint RemoteEndPoint`、`IPEndPoint LocalEndPoint`、`DateTimeOffset ConnectedAt`、`bool IsConnected`、`bool IsTlsActive`、`ConnectionStatistics Statistics`、`IDictionary<string, object?> Items`、`Task CloseAsync(CancellationToken = default)`。会话可以对对端发起 `RequestAsync`（SECS 设备端需要）。

**`ServerState`**：`Stopped`、`Starting`、`Running`、`Stopping`、`Faulted`。

**`IConnectionFilter`**：`bool Accept(IPEndPoint remote)`。在接受连接之前调用，返回 `false` 立即关闭；抛异常视为拒绝。

**事件参数**：`TcpSessionEventArgs`（`ITcpSession Session`）；`TcpSessionClosedEventArgs : TcpSessionEventArgs`（`DisconnectReason Reason`、`Exception? Exception`）；`TcpSessionFrameEventArgs`（`ITcpSession Session`、`byte[] Data`、`DateTimeOffset ReceivedAt`）；`ServerStateChangedEventArgs`（`PreviousState`、`CurrentState`、`Exception?`）。

**`TcpServerConfig`**

| 属性 | 默认值 | 说明 |
|---|---|---|
| `ListenAddress` | `"0.0.0.0"` | 必须为 IP 字面量，否则构造时抛 `ArgumentException` |
| `Port` | 0 | 监听端口 |
| `Backlog` | 100 | 监听队列长度 |
| `MaxSessions` | 0 | 最大会话数，0 表示不限 |
| `AllowedRemoteAddresses` | `null` | IP 白名单；为空表示不限制 |
| `SessionHandshakeTimeout` | 10000 | 会话的 TLS 与初始化时限 |
| `SessionIdleTimeout` | 0 | 会话空闲超时 |
| `PartialFrameTimeout` | 0 | 半帧超时 |
| `SendTimeout` / `RequestTimeout` | 2000 / 2000 | 写出与请求超时 |
| `ResetOnRequestTimeout` | `true` | 请求超时后关闭该会话 |
| `LateReplyWindow` | -1 | 仅在 `ResetOnRequestTimeout = false` 时生效 |
| `StopTimeout` | 3000 | `StopAsync` 等待会话关闭的时限 |
| `Framing` / `Correlation` | `new FramingOptions()` / `Sequential` | 分帧与关联 |
| `Heartbeat` | `new HeartbeatOptions()` | 会话心跳；启用时需 `Payload` 或 `SessionHealthProbeFactory` |
| `RestartOnFault` | `new ReconnectOptions()`（`Enabled = false`） | 监听器故障后的重新监听策略 |
| `Socket` / `Tls` | `new TcpSocketOptions()` / `new TcpServerTlsOptions()` | 套接字与 TLS |
| `ReceiveQueueCapacity` | 1024 | 派发队列容量。服务端没有 `QueueFullMode`，固定为 `Wait` |

**服务端组件**：`TcpChannelComponents` 的 `ConnectionFilter` 与 `SessionHealthProbeFactory`（`Func<ITcpSession, IHealthProbe>`，按会话创建心跳探测）只对服务端有效。`ChannelComponents.HealthProbe` 传给 `TcpServer` 时构造即抛 `ArgumentException`。

## TLS 与套接字选项

**`TcpChannelComponents`**（继承 `ChannelComponents`，公开）：除基类成员外，增加 `X509Certificate2? ClientCertificate`（客户端证书，必须包含私钥，由调用方持有）、`X509Certificate2? ServerCertificate`（服务端证书，由调用方持有，服务端释放时不释放）、`RemoteCertificateValidationCallback? RemoteCertificateValidation`（客户端校验服务端证书，或服务端校验客户端证书）、`ConnectionFilter`、`SessionHealthProbeFactory`。

**`TcpClientTlsOptions`**：`Enabled`（`false`）、`TargetHost`（`null`，表示使用 `Host`）、`Protocols`（`SslProtocols.None`，交给操作系统）、`CheckCertificateRevocation`（`true`）、`AllowUntrustedServerCertificate`（`false`，仅限调试，开启后每次连接记 Warning）、`ClientCertificate`（`CertificateSource?`）。

**`TcpServerTlsOptions`**：`Enabled`（`false`）、`ServerCertificate`（`CertificateSource?`）、`ClientCertificateRequired`（`false`）、`Protocols`（`None`）、`CheckCertificateRevocation`（`true`）。

**`CertificateSource`**：`StoreLocation`（`LocalMachine`）、`StoreName`（`My`）、`Thumbprint`（去掉所有非十六进制字符后匹配，忽略大小写）、`PfxPath`、`PfxPasswordEnvironmentVariable`（**密码只能从环境变量读取**，配置中不允许明文密码）。

证书错误的归类：

| 情形 | 结果 |
|---|---|
| 构造期的结构错误（既无指纹也无 PFX 路径、环境变量未设置、证书缺少私钥） | 构造时抛 `ArgumentException` |
| 连接期加载失败（找不到指纹、PFX 损坏、客户端证书缺少私钥） | `InvalidRequest` |
| 证书校验失败或认证失败 | `AuthenticationFailed` |
| 握手超时 | `Timeout` |

服务端证书校验的优先级：`RemoteCertificateValidation` 存在时以其结果为准；否则 `AllowUntrustedServerCertificate` 为真时接受任何结果并记 Warning（设置了回调时不记）；否则要求没有任何校验错误。断开时不发送 TLS close_notify，只对套接字执行 `Shutdown(Send)`。UDP 上的 DTLS 不支持。

**`TcpSocketOptions`**：`NoDelay`（`true`）、`ReceiveBufferSize`（0，表示不设置）、`SendBufferSize`（0，表示不设置）、`LingerTime`（-1，表示不启用；否则为秒数）、`KeepAlive`（`TcpKeepAliveOptions`）。

**`TcpKeepAliveOptions`**：`Enabled`（`true`）、`Time`（30000 毫秒，必须为正）、`Interval`（5000 毫秒，必须为正）、`RetryCount`（3，必须不小于 1）。这是操作系统层的保活，用于发现半开连接；应用层心跳见行为契约笔记。两个目标的应用方式不同：net8.0 以整秒设置 `Time` 与 `Interval`，并设置 `RetryCount`；net472 经 `SIO_KEEPALIVE_VALS` 以毫秒设置，**`RetryCount` 不生效**，由系统决定（Windows 默认 10 次）。

## 串口

**`SerialChannel`**（密封，实现 `ISerialChannel`，继承 `StreamClientChannel`，基于 `SerialPort.BaseStream`，不使用 `DataReceived` 事件）

- 构造：`SerialChannel(SerialChannelConfig config, ILogger<SerialChannel>? logger = null, ChannelComponents? components = null)`；带名称的重载。
- `ISerialChannel : IClientChannel`，成员 `SerialChannelConfig Config`。

**`SerialChannelConfig`**

| 属性 | 默认值 | 说明 |
|---|---|---|
| `PortName` | `"COM1"` | 端口名 |
| `BaudRate` | 9600 | 波特率 |
| `DataBits` | 8 | 数据位 |
| `Parity` / `StopBits` | `None` / `One` | 校验与停止位 |
| `Handshake` | `None` | 流控 |
| `DtrEnable` / `RtsEnable` | `false` / `false` | 控制线 |
| `ReadBufferSize` | 4096 | 驱动接收缓冲，必须为偶数（`SerialPort` 的要求） |
| `WriteBufferSize` | 2048 | 驱动发送缓冲，必须为偶数 |
| `OpenTimeout` | 2000 | 整个打开过程的时限，必须为正 |
| `HandshakeTimeout` | 5000 | 初始化器时限 |
| `DisconnectTimeout` | 1000 | 排空时限 |
| `SendTimeout` / `RequestTimeout` | 2000 / 2000 | 写出与请求超时 |
| `IdleTimeout` / `PartialFrameTimeout` | 0 / 0 | 空闲与半帧超时 |
| `LateReplyWindow` | -1 | 迟到窗口（串口固定不重建连接，迟到应答由它处理） |
| `Framing` | `Mode = IdleGap`，`GapTimeout = 20` | 默认分帧 |
| `Correlation` | `Sequential` | 关联模式 |
| `Heartbeat` / `Reconnect` | `new HeartbeatOptions()` / `new ReconnectOptions()` | 心跳需要 `ExpectedReply` 或自定义 `HealthProbe` |
| `ReceiveQueueCapacity` / `QueueFullMode` | 1024 / `DropOldest` | 派发队列 |

串口固定的行为（不可配置）：`ResetOnRequestTimeout = false`（请求超时不重新打开端口）；`PartialFrameAction = Discard`（残余字节丢弃，连接保持）。

打开端口时遇到拒绝访问（`UnauthorizedAccessException`）会在 `OpenTimeout` 内每 20 ms 重试一次（驱动释放端口是异步的）；其他打开失败立即返回 `ConnectionClosed`，消息包含端口名与原始错误。

**`SerialChannelCreator`**（公开密封，`ConfigType` 为 `SerialChannelConfig`）。

## UDP

**`UdpChannel`**（密封，实现 `IUdpChannel`，**不继承** `StreamClientChannel`）

- 构造：`UdpChannel(UdpChannelConfig config, ILogger<UdpChannel>? logger = null, ChannelComponents? components = null)`；带名称的重载。
- `IUdpChannel : IClientChannel`：`UdpChannelConfig Config`；`IPEndPoint? LocalEndPoint`；`Task<CommResult> SendToAsync(IPEndPoint remote, ReadOnlyMemory<byte> payload, CancellationToken = default)`；`Task<CommResult<byte[]>> RequestToAsync(IPEndPoint remote, ReadOnlyMemory<byte> payload, RequestOptions? options = null, CancellationToken = default)`。

**`UdpChannelConfig`**

| 属性 | 默认值 | 说明 |
|---|---|---|
| `LocalAddress` / `LocalPort` | `"0.0.0.0"` / 0 | 绑定地址与端口 |
| `RemoteHost` / `RemotePort` | `null` / 0 | 设置后为**定向模式**；两者必须同时设置或同时不设置 |
| `EnableBroadcast` | `false` | 允许广播 |
| `MulticastGroups` | `null` | 加入的组播组（必须是组播地址） |
| `MulticastTimeToLive` | 1 | 组播 TTL |
| `MulticastLoopback` | `false` | 组播回环 |
| `ReuseAddress` | `false` | `SO_REUSEADDR` |
| `ReceiveBufferSize` | 65536 | 接收缓冲 |
| `MaxDatagramSize` | 65507 | 必须在 `[1, 65507]`；超过的数据报丢弃并计 `ProtocolErrors` |
| `HandshakeTimeout` | 5000 | 初始化器时限 |
| `DisconnectTimeout` | 1000 | 排空时限 |
| `SendTimeout` / `RequestTimeout` | 2000 / 2000 | 写出与请求超时 |
| `RequestRetryCount` | 0 | 请求超时后原样重发的次数 |
| `IdleTimeout` | 0 | 空闲超时 |
| `LateReplyWindow` | -1 | 迟到窗口 |
| `Correlation` | `Sequential` | 关联模式 |
| `Heartbeat` / `Reconnect` | `new HeartbeatOptions()` / `new ReconnectOptions()` | 内置心跳需要 `ExpectedReply`；非定向模式需要 `HealthProbe` |
| `ReceiveQueueCapacity` / `QueueFullMode` | 1024 / `DropOldest` | 派发队列（UDP 没有流控） |

**使用规则**：

- `ChannelComponents.FrameCodec` 必须为 `null`（数据报本身就是一帧）。
- **定向模式**：`RequestAsync` 请求远端；`RequestToAsync` 发往其他地址返回 `InvalidRequest`；`SendToAsync` 可以发往任意地址。只派发来自远端的数据报，其他来源计入 `FramesDropped`。
- **非定向模式**：`RequestAsync` 返回 `InvalidRequest`（提示改用 `RequestToAsync`）。应答匹配要求来源地址等于请求目标地址；这一检查先于自定义 `Matcher` 执行。
- `SendToAsync` / `RequestToAsync` 的端口为 0 时返回 `InvalidRequest`。
- 空负载：`SendAsync` 发送零长度数据报；`RequestAsync` / `RequestToAsync` 返回 `InvalidRequest`。
- `FrameReceived` 在定向与非定向模式下都携带来源地址（`RemoteEndPoint`）。

**`UdpChannelCreator`**（公开密封，`ConfigType` 为 `UdpChannelConfig`）。

## 文本扩展

`ByteChannelTextExtensions`（公开静态，命名空间 `Junevy.Communication.Channels`）适用于任何 `IByteChannel`：

| 方法 | 返回 |
|---|---|
| `SendTextAsync(this IByteChannel channel, string text, Encoding? encoding = null, CancellationToken = default)` | `Task<CommResult>` |
| `RequestTextAsync(this IByteChannel channel, string text, RequestOptions? options = null, Encoding? encoding = null, CancellationToken = default)` | `Task<CommResult<string>>` |
| `ReceiveTextAsync(this IByteChannel channel, RequestOptions? options = null, Encoding? encoding = null, CancellationToken = default)` | `Task<CommResult<string>>` |

默认编码为 UTF-8（无 BOM）。**不追加分隔符**：分隔符由 `Delimiter` 编码器的 `AppendDelimiterOnSend` 负责。失败结果原样透传。

## 分帧工具

**`FrameCodecFactory.Create(FramingOptions options)`**（公开静态，命名空间 `Junevy.Communication.Channels.Framing`）：返回 `IFrameCodecFactory`。内置的分帧器与编码器都是 `internal`。参数非法时抛 `ArgumentException` 或 `ArgumentOutOfRangeException`，例如 `Delimiter` 模式没有分隔符、`MaxFrameLength ≤ 0`、`GapTimeout ≤ 0`、不支持的长度字段编码。

**`ByteSequenceParser.Parse(string text)`**（公开静态）：解析配置中的字节序列（分隔符、起止标记、心跳内容、期望应答）。规则：

- 以 `hex:` 开头（大小写不敏感）时，其后为十六进制字节，可用空格或 `-` 分隔，例如 `hex:0D 0A`、`hex:0D-0A`、`hex:0D0A`；
- 否则按文本解析，支持转义 `\r`、`\n`、`\t`、`\0`、`\\`、`\xHH`，其余字符按 UTF-8 编码；
- 空字符串抛 `FormatException`。

**`FrameDecodeException`**（公开）：分帧器发现协议违规时抛出，通道把它映射为 `ProtocolViolation`。
