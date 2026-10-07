# ModbusTcpClient

<cite>
**本文引用的文件**
- [Junevy.Communication.Modbus/Tcp/ModbusTcpClient.cs](file://Junevy.Communication.Modbus/Tcp/ModbusTcpClient.cs)
- [Junevy.Communication.Modbus/Tcp/ModbusTcpClientConfig.cs](file://Junevy.Communication.Modbus/Tcp/ModbusTcpClientConfig.cs)
- [Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs](file://Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs)
</cite>

命名空间 `Junevy.Communication.Modbus.Tcp`。sealed 类，继承 `ModbusTransportBase`。旧名 `ModbusTCP`（1.0.x 破坏性更名，见 [[变更与决策/版本历史与破坏性变更]]）。

## 构造重载（4 个，逐级注入依赖）

```csharp
new ModbusTcpClient(ModbusTcpClientConfig config)
new ModbusTcpClient(config, ILogger<ModbusTcpClient> logger)
new ModbusTcpClient(config, logger, IResponseParser responseParser)        // 默认 TcpProtocolParser
new ModbusTcpClient(config, logger, responseParser, IModbusFrameBuilder)   // 默认 ModbusFrameBuilder
```

`config` 为 null 抛 `ArgumentNullException`。构造时即创建 IPv4 TCP Socket。

## ModbusTcpClientConfig 全字段

| 属性 | 类型 / 默认 | 说明 |
|---|---|---|
| `Address` | string / `"127.0.0.1"` | 主机名或 IP（校验仅查非空，不做格式校验） |
| `Port` | int / `502` | 1–65535；**普通可写属性**（可构造后改，测试里就有 `tcp.Config.Port = port;`），无 `SetPort()` 方法 |
| `ConnectTimeout` | int / `2000` | 连接超时毫秒（RTU 配置**没有**此项） |
| `ReadTimeout` | int / `2000` | 读超时毫秒。同步路径设为 `Socket.ReceiveTimeout`；异步路径由 `CancelAfter` 强制，且为**整帧总时限** |
| `WriteTimeout` | int / `2000` | 写超时毫秒。同步路径设为 `Socket.SendTimeout`；异步路径由 `CancelAfter` 强制 |
| `RetryCount` | int / `3` | 首次失败后重试次数（总尝试 4）。**需重建连接的失败仅在 `Reconnect=true` 时才重试** |
| `RetryInterval` | int / `100` | 重试间隔毫秒 |
| `Reconnect` | bool / `false` | 懒重连开关。为 `false` 时超时/连接关闭/发送失败立即返回（保留真实 `ErrorKind`），不再空转重试 |

## 连接行为

- `ResetSocket()`：丢弃旧 socket → 新建 → `ReceiveTimeout/SendTimeout` 赋值 → `NoDelay=true` → `KeepAlive=true`。
- 同步 `OpenConnection`：`BeginConnect` + `AsyncWaitHandle.WaitOne(ConnectTimeout)`，超时/异常都回收 socket 返回 false。
- 异步 `OpenConnectionAsync`：
  - **net8.0**：`BeginConnect/EndConnect` + `Task.WaitAsync(linked CTS CancelAfter(ConnectTimeout))`——连接期取消令牌即时生效。
  - **net472**：降级为同步核心（先 `ThrowIfCancellationRequested` 一次），取消在下一个 I/O 边界生效。
- `IsConnected`：`socket.Connected && !(Poll(0, SelectRead) && Available==0)`——基于探活而非缓存值。
- Socket 选项固定 IPv4（`AddressFamily.InterNetwork`）。

## 收发行为

- **发送**：`ArrayPool` 租 `MaxTcpAduLength=260` 缓冲 → `TryWriteRequestFrame(request, ProtocolType, ...)` → 循环 send 直到写满（sent==0 判失败）→ `LogTx` hex 日志。Socket 超时归类发送失败；其他异常上抛（由骨架通信异常白名单接住）。异步写超时（`CancelAfter(WriteTimeout)`）返回 `false`，与同步路径 `SocketError.TimedOut` 行为一致。
- **接收**：先读**恰好 6 字节** MBAP 头 → `BinaryPrimitives.ReadUInt16BigEndian(frame[4..6])` 取 PDU 长度，**合法域 1–254**（否则 `Fail(ProtocolViolation)`，不抛）→ 再读 PDU → `ResponseParser.ParseResponse`。远端关流（read==0）→ `Fail(ConnectionClosed)`。
- **超时**（异步，`RequestAsync`）：`ReceiveFrameAsync` 开头 `CreateLinkedTokenSource(ct)` + `CancelAfter(ReadTimeout)`，`ReadTimeout` 是**从进入方法到收满整帧的总时限**（不是每次 `ReadAsync` 的时限），令牌传给 `ReceiveExactAsync`；超时时日志 `" [SendAsync]/[ReadAsync] ... timed out: {Timeout}ms."` 并归类 `Timeout`。net472 上 `ReadAsync` 不响应令牌，额外注册回调 dispose socket 逼出异常。**2026-10-07 之前异步路径完全不受 `ReadTimeout` 约束**（服务端不应答时永久挂起）。
- **取消**（异步）：`OperationCanceledException` 在收发层直接上抛，由骨架统一归类 `Fail(Cancelled)`；用户令牌已触发时优先归 `Cancelled` 而非 `Timeout`；net8.0 读阻塞期间取消即时生效。
- 解析结果转交骨架前复制为数组（`parsed.Data.ToArray()`），失败时也携带原始帧便于诊断。

## 配置转发 / 骨架钩子取值

`AssignsTransactionId=true`；`RequiresNewConnection` = `ErrorKind ∈ {Timeout, ConnectionClosed, ProtocolViolation}`（2026-10-08 加入 `ProtocolViolation`）；消息文本钩子用 " [Request] Not connected." / " [Request] Send failed." 措辞（与 RTU 不同，测试可能依赖文案）。

## 使用注意

1. `Disconnect()` 后若 `Reconnect=true`，下一次请求会**静默重连**——想保持断开就设 `Reconnect=false`。
2. 每个实例 = 一条 TCP 连接 = 一次一个在途请求；并发请多实例（工厂多 key）。
3. 半包残留安全：读超时归类 `Timeout` → 骨架销毁连接重连，残帧不会污染下一次请求（审查报告关注点 1，有测试）。⚠️ 只有 `Reconnect=true` 才会真的重连；`Reconnect=false`（默认）下超时立即以 `Timeout` 返回，由调用方决定是否重连。
4. TID 由库自增管理并精确匹配，**不要**为传输调用手动设置 `request.TransactionId`。
