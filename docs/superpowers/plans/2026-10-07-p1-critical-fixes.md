# 计划一：严重缺陷修复（P1）

> 来源：2026-10-07 代码审查。本计划对应审查结论中的"先修"组：第 1 条（异步读超时）、第 2 条（别名泄漏）、第 3 条（ErrorKind 丢失）、第 4 条（重试/重连解耦），以及 `EnsureConnectedAsync` 改用 `OpenConnectionAsync`。
> 版本：完成后 `Junevy.Communication.Modbus.csproj` 的 `Version` 由 `1.0.1` 改为 `1.0.2`。
> 后续：计划二（`2026-10-07-p2-robustness.md`）、计划三（`2026-10-07-p3-architecture-refactor.md`）依赖本计划合入 master 之后才能开始。

## 0. 执行规则（每个 Task 都适用）

1. 分支：从 master 新建分支 `fix/modbus-p1-critical`，所有 Task 在该分支上顺序提交，每个 Task 一个 commit。
2. 每个 Task 的步骤顺序固定为：先写测试并确认测试在改动前失败（红）→ 改生产代码 → 确认测试通过（绿）→ 清理被替换的冗余代码 → 更新文档 → 提交。
3. 每个 Task 完成后必须依次运行以下命令，三条命令全部成功才能提交：
   ```bash
   dotnet build Junevy.Communication.slnx -c Debug
   dotnet build Junevy.Communication.Modbus/Junevy.Communication.Modbus.csproj -c Release -f net472
   dotnet test Junevy.Communication.Modbus.Tests/Junevy.Communication.Modbus.Tests.csproj -c Debug
   ```
4. 新增的套接字测试统一使用 `ConnectTimeout = 10000`，与现有测试一致，避免并行负载下误报。
5. 不修改 `Junevy.Communication.Wiki/` 目录下的任何文件。
6. 不在本计划内处理的缺陷见文末"未纳入本计划的条目"，执行者遇到时只记录，不修改。
7. 每个 Task 的 CHANGELOG 条目写入 `CHANGELOG.md` 的 `[Unreleased]` 段，日期格式 `YYYY-MM-DD`，填写执行当天的日期。
8. 每个 Task 结束时检查 `Skills/using-junevy-modbus/SKILL.md` 是否需要更新；本计划中需要更新的位置已在各 Task 中逐条列出，未列出的位置不改。

## 1. Task 0：记录基线

目的：区分"本计划引入的失败"和"已有的偶发失败"。

步骤：
1. 在 master 上连续运行 3 次 `dotnet test Junevy.Communication.Modbus.Tests/Junevy.Communication.Modbus.Tests.csproj -c Debug`。
2. 记录每次的"失败/通过/总计"数，以及失败用例的完整名称。2026-10-07 审查时观察到 `TcpClient_ExceptionResponse_IsTerminal_NoRetry` 在全量并行运行时失败 1 次，单独运行通过。
3. 把记录写入分支的第一个 commit 的提交信息正文，不修改任何文件，使用 `git commit --allow-empty -m "chore: record test baseline"`。

验收：提交信息正文包含 3 次运行的结果。

后续判定规则：本计划任一 Task 之后，如果 `TcpClient_ExceptionResponse_IsTerminal_NoRetry` 之外的用例失败，必须修复；如果仅该用例失败，用 `dotnet test --filter "FullyQualifiedName~TcpClient_ExceptionResponse_IsTerminal_NoRetry"` 单独重跑，单独通过则记录为已知偶发，不阻塞提交。

## 2. 公共测试辅助（Task 1 创建，后续 Task 复用）

在 `Junevy.Communication.Modbus.Tests/TestSupport/` 目录新增两个文件。

### 2.1 `SilentTcpServer.cs`

用途：接受 TCP 连接，读取并计数收到的数据，从不回复。

公开成员（全部必须实现，名称固定）：
- `static SilentTcpServer Start()`：监听 `IPAddress.Loopback` 的随机端口。
- `int Port { get; }`
- `int AcceptedConnectionCount { get; }`：已接受的连接数（线程安全，使用 `Interlocked`）。
- `int ReceivedBytes { get; }`：所有连接累计收到的字节数（线程安全）。
- `Action<System.Net.Sockets.NetworkStream>? OnConnected { get; set; }`：连接建立后同步回调，默认 `null`；Task 1 的用例通过它发送"半个响应头"。
- `void Dispose()`：停止监听并关闭所有已接受的连接。

### 2.2 `FakeTransport.cs`（Task 2 创建）

用途：继承 `ModbusTransportBase`，不使用任何真实 I/O，供基类逻辑的确定性测试使用。

类声明：`internal sealed class FakeTransport : ModbusTransportBase`。构造参数：`(bool reconnectEnabled, int retryCount, int retryInterval)`。

可由测试读写的公开字段/属性（名称固定）：
- `bool Connected`：`IsConnected` 的返回值，初始值 `true`。
- `int OpenConnectionCalls`、`int OpenConnectionAsyncCalls`、`int SendFrameCalls`、`int ReceiveFrameCalls`、`int InvalidateConnectionCalls`：各钩子被调用的次数。
- `Func<bool> OnOpenConnection`：默认返回 `true`，且把 `Connected` 置为 `true`。
- `Func<CancellationToken, Task<bool>> OnOpenConnectionAsync`：默认返回 `true`，且把 `Connected` 置为 `true`。
- `Func<bool> OnSendFrame`：默认返回 `true`；允许测试返回 `false` 或抛出异常。
- `Queue<ModbusResult<byte[]>> ReceiveResults`：`ReceiveFrame` 与 `ReceiveFrameAsync` 每次调用出队一个结果；队列为空时返回 `ModbusResult<byte[]>.Success(new byte[] { 0, 0, 0, 0, 0, 0, 1, 3, 2, 0, 1 })`。
- `InvalidateConnection()` 的实现：`InvalidateConnectionCalls++` 并把 `Connected` 置为 `false`。
- `CloseConnection()`、`DisposeConnection()`：把 `Connected` 置为 `false`。
- `ProtocolType` 返回 `ModbusProtocolType.TCP`；`AssignsTransactionId` 返回 `true`。
- 所有消息钩子（`GetNotConnectedMessage`、`GetSendFailedMessage`、`GetRequestFailedLogText`、`LogReconnectAttempt`、`LogReconnectFailed`）返回固定字符串 `"fake"` 或空实现。
- `RequiresNewConnection` 的覆写：见 Task 3。

## 3. Task 1：异步 TCP 读写超时生效（审查第 1 条）

### 3.1 问题

`Socket.ReceiveTimeout` 与 `Socket.SendTimeout` 只作用于同步调用。`ModbusTcpClient.ReceiveFrameAsync` 与 `SendFrameAsync` 通过 `NetworkStream.ReadAsync/WriteAsync` 执行，不受这两个属性约束，`ModbusTcpClientConfig.ReadTimeout` 与 `WriteTimeout` 在异步路径上没有任何效果。2026-10-07 探针实测：`ReadTimeout=500`、服务端不回包，`ReadHoldingRegistersAsync` 在 5000 ms 后仍未返回。

### 3.2 修改文件

- `Junevy.Communication.Modbus/Tcp/ModbusTcpClient.cs`：方法 `SendFrameAsync`、`ReceiveFrameAsync`、`ReceiveExactAsync`。

### 3.3 新增文件

- `Junevy.Communication.Modbus.Tests/TestSupport/SilentTcpServer.cs`（见 2.1）。
- `Junevy.Communication.Modbus.Tests/TcpAsyncTimeoutTests.cs`。

### 3.4 步骤

1. 创建 `SilentTcpServer.cs`。
2. 创建 `TcpAsyncTimeoutTests.cs`，写入 3.5 列出的 6 个测试，运行，确认 `*_ReturnsTimeout*` 与 `*_WithinReadTimeout` 共 4 个用例失败（挂起超过 10 秒或不满足断言）。每个测试自身必须用 `Task.WhenAny(requestTask, Task.Delay(10000))` 保护，避免失败时测试进程挂起。
3. 修改 `ReceiveFrameAsync`，规则如下：
   - 方法开头创建 `using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);`，随后调用 `timeoutCts.CancelAfter(Config.ReadTimeout);`。
   - `ReadTimeout` 的语义固定为：从进入 `ReceiveFrameAsync` 开始，到完整响应帧（6 字节头加负载）读完为止的总时限，不是每次 `ReadAsync` 的时限。
   - `ReceiveExactAsync` 增加参数 `CancellationToken`，传入 `timeoutCts.Token`，内部 `ReadAsync` 使用该令牌。
   - 在 `#if !NET8_0_OR_GREATER` 分支中（net472），`NetworkStream.ReadAsync` 不响应取消令牌，必须额外注册取消回调：先 `var activeSocket = socket;`（局部变量捕获），再 `using var abortRegistration = timeoutCts.Token.Register(() => activeSocket?.Dispose());`。回调执行后 `ReadAsync` 会抛出 `ObjectDisposedException` 或 `IOException`，由下一条规则处理。
   - 异常处理新增一个 `catch` 子句，置于现有 `catch (SocketException ...)` 之后、`catch (OperationCanceledException)` 之前，过滤条件为 `when (timeoutCts.IsCancellationRequested && (ex is OperationCanceledException || ex is ObjectDisposedException || ex is IOException || ex is SocketException))`。子句体：
     - 如果 `cancellationToken.IsCancellationRequested` 为 `true`，执行 `throw new OperationCanceledException(cancellationToken);`（由基类归为 `ModbusErrorKind.Cancelled`）。
     - 否则记录日志 `" [ReadAsync] Read timed out: {Timeout}ms."` 并返回 `ModbusResult<byte[]>.Fail(" [ReadAsync] Read timeout.", ModbusErrorKind.Timeout)`。
4. 修改 `SendFrameAsync`，规则与第 3 步相同，差异只有三点：时限使用 `Config.WriteTimeout`；写超时时记录日志 `" [SendAsync] Write timed out: {Timeout}ms."` 并 `return false`（与同步路径 `SocketError.TimedOut` 时 `return false` 的行为一致，基类随后标记 `ConnectionClosed` 并销毁连接）；用户取消时重新抛出 `OperationCanceledException`。
5. 运行 3.5 的测试，确认全部通过；再运行已有测试 `TcpClient_RequestAsync_CancelledDuringRead_ReturnsCancelledQuickly`，确认仍通过。
6. net472 运行时验证（测试项目只有 net8.0，所以单独做）：
   1. 在 scratchpad 目录新建控制台项目，`TargetFramework` 为 `net472`，`ProjectReference` 指向 `Junevy.Communication.Modbus.csproj`。
   2. 程序内启动 `TcpListener`（接受后不回复），创建 `ModbusTcpClient`（`ReadTimeout=500`、`RetryCount=0`），调用 `ReadHoldingRegistersAsync(1, 0, 1)`，用 `Stopwatch` 计时。
   3. 通过判定：返回的 `ErrorKind` 为 `Timeout` 且耗时在 450 ms 到 2500 ms 之间。
   4. 把控制台输出粘贴到 commit 信息正文。
7. 删除 `ModbusTcpClient.cs` 中 `ReceiveExactAsync` 里已失效的注释"读超时兜底靠 socket.ReceiveTimeout"。

### 3.5 测试（类 `TcpAsyncTimeoutTests`，全部使用 `SilentTcpServer` 或下列指定的服务端）

| 测试名 | 配置 | 动作 | 断言 |
|---|---|---|---|
| `RequestAsync_ServerNeverReplies_ReturnsTimeout` | `ReadTimeout=500, RetryCount=0, Reconnect=false` | `Connect()` 后 `RequestAsync` 读 1 个保持寄存器 | 任务在 10 s 内完成；`ErrorKind == Timeout`；耗时 ≥ 450 ms 且 ≤ 2500 ms |
| `RequestAsync_ServerSendsPartialHeader_ReturnsTimeout` | 同上 | `SilentTcpServer.OnConnected` 向流写入 3 个字节 `00 00 00` 后不再发送 | 同上 |
| `ReadHoldingRegistersAsync_ServerNeverReplies_ReturnsWithinReadTimeout` | 同上 | 调用扩展方法 `ReadHoldingRegistersAsync(1, 0, 1)` | 任务在 10 s 内完成；`IsSuccess == false`（`ErrorKind` 的断言在 Task 4 之后才成立，本 Task 不断言 `ErrorKind`） |
| `RequestAsync_ServerRepliesWithinTimeout_Succeeds` | `ReadTimeout=1000` | 自定义服务端：收到 12 字节请求后等待 200 ms，再回复合法的"读 1 个寄存器"响应（TID 回显请求 TID，值 `00 01`） | `IsSuccess == true`；耗时 < 1000 ms |
| `RequestAsync_UserCancelsBeforeReadTimeout_ReturnsCancelled` | `ReadTimeout=30000` | `CancellationTokenSource` 在 200 ms 后取消 | `ErrorKind == Cancelled`；耗时 < 1500 ms |
| `RequestAsync_ReadTimeout_ConnectionIsInvalidatedAfterTimeout` | `ReadTimeout=300, RetryCount=0, Reconnect=false` | 超时后检查 `tcp.IsConnected` | `IsConnected == false` |

### 3.6 文档

- `CHANGELOG.md` `### 修复（Fixed）` 增加：`{日期} TCP 异步请求的 ReadTimeout / WriteTimeout 生效（此前 Socket.ReceiveTimeout 对 NetworkStream.ReadAsync/WriteAsync 无效，服务端不应答时 RequestAsync 永久挂起）；ReadTimeout 为整帧总时限，超时返回 ErrorKind.Timeout，用户取消返回 ErrorKind.Cancelled。`
- `Skills/using-junevy-modbus/SKILL.md` "Behavioral Contracts" 的 Thread-safety 条目之后新增一条：`- **Timeouts**: ReadTimeout is the total deadline for receiving one complete response frame (sync and async). WriteTimeout is the deadline for sending one request frame. A timeout returns ErrorKind.Timeout.`
- `readme.md`、`Junevy.Communication.Modbus/README.md`：本 Task 不修改。

### 3.7 提交

`fix(tcp): enforce ReadTimeout/WriteTimeout on the async I/O path`

## 4. Task 2：重连使用真正的异步连接（审查"假异步"第 1 条）

### 4.1 问题

`ModbusTransportBase.EnsureConnectedAsync` 使用 `Task.Run(OpenConnection)`，不使用已有的 `OpenConnectionAsync`：占用线程池线程直到连接超时，且取消令牌无效。`ModbusTcpClient.OpenConnectionAsync` 把用户取消与连接超时都当作超时处理（返回 `false`）。

### 4.2 修改文件

- `Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs`：方法 `EnsureConnectedAsync`。
- `Junevy.Communication.Modbus/Tcp/ModbusTcpClient.cs`：方法 `OpenConnectionAsync`（仅 `#if NET8_0_OR_GREATER` 分支）。

### 4.3 新增文件

- `Junevy.Communication.Modbus.Tests/TestSupport/FakeTransport.cs`（见 2.2）。
- `Junevy.Communication.Modbus.Tests/TransportBaseAsyncConnectTests.cs`。

### 4.4 步骤

1. 创建 `FakeTransport.cs`。`ModbusTransportBase` 的全部抽象成员都必须实现，`ReceiveFrame`/`ReceiveFrameAsync` 出队 `ReceiveResults`，`SendFrame` 调用 `OnSendFrame`，`SendFrameAsync` 返回 `Task.FromResult(OnSendFrame())`。
2. 创建 `TransportBaseAsyncConnectTests.cs`，写入 4.5 的 3 个测试，运行，确认 `RequestAsync_Reconnect_UsesAsyncOpenNotSyncOpen` 与 `RequestAsync_CancelledDuringReconnect_ReturnsCancelled` 失败。
3. 修改 `EnsureConnectedAsync`：把 `return await Task.Run(OpenConnection);` 替换为 `return await OpenConnectionAsync(cancellationToken);`。保留方法开头的 `cancellationToken.ThrowIfCancellationRequested();`。`catch (Exception ex) when (IsCommunicationException(ex))` 子句保持不变（`OperationCanceledException` 不属于通信异常，会继续向上传播，由 `RequestAsync` 归为 `Cancelled`）。删除旧的注释"已在请求锁内，走无锁核心，避免重入死锁"，换成注释：`// 调用方已持有 requestLock；OpenConnectionAsync 是无锁核心，不会重入。`
4. 修改 `ModbusTcpClient.OpenConnectionAsync` 的 net8 分支，在现有 `catch (OperationCanceledException)` 之前增加：
   ```csharp
   catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
   {
       InvalidateConnection();
       throw;
   }
   ```
   现有的 `catch (OperationCanceledException)`（连接超时）保持不变。
5. 运行 4.5 的测试和全量测试，确认通过。

### 4.5 测试（类 `TransportBaseAsyncConnectTests`，使用 `FakeTransport`）

| 测试名 | 配置 | 动作 | 断言 |
|---|---|---|---|
| `RequestAsync_Reconnect_UsesAsyncOpenNotSyncOpen` | `reconnectEnabled=true, retryCount=0, retryInterval=0`；`Connected=false` | `RequestAsync` 发送合法的读保持寄存器请求 | `OpenConnectionAsyncCalls == 1`；`OpenConnectionCalls == 0` |
| `RequestAsync_CancelledDuringReconnect_ReturnsCancelled` | 同上；`OnOpenConnectionAsync = async ct => { await Task.Delay(Timeout.Infinite, ct); return false; }`（令牌取消时抛出 `TaskCanceledException`） | 令牌在 100 ms 后取消 | 任务在 1500 ms 内完成；`ErrorKind == Cancelled` |
| `Connect_UsesLockedAsyncOpen` | `Connected=false` | `await ConnectAsync()` | 返回 `true`；`OpenConnectionAsyncCalls == 1` |

### 4.6 文档

- `CHANGELOG.md` `### 修复（Fixed）` 增加：`{日期} 异步请求的自动重连改用 OpenConnectionAsync（此前 Task.Run 包装同步连接，占用线程池线程且不响应取消）；TCP 异步连接中用户取消不再被当作连接超时吞掉。`
- `Skills/using-junevy-modbus/SKILL.md`：本 Task 不修改。

### 4.7 提交

`fix(transport): reconnect via OpenConnectionAsync instead of Task.Run`

## 5. Task 3：重试与重连解耦（审查第 4 条）

### 5.1 问题

默认配置 `Reconnect=false`、`RetryCount=3`：第一次读超时后连接被销毁，后续 3 次重试全部在 `EnsureConnected` 处失败，最终返回 `Not connected`（`ErrorKind.ConnectionClosed`），真实的超时原因被覆盖，且白白等待 3 个 `RetryInterval`。2026-10-07 探针实测：总耗时 356 ms，错误消息 `Not connected`。

### 5.2 新的固定规则

对 `Request` 与 `RequestAsync` 的重试循环（`ExecuteRequestWithRetry`、`ExecuteRequestWithRetryAsync`）统一采用下表，同步与异步完全一致：

| 编号 | 触发条件 | `ReconnectEnabled == false` | `ReconnectEnabled == true` |
|---|---|---|---|
| A | 循环每次尝试开始时 `IsConnected == false` | 立即返回 `Fail(GetNotConnectedMessage, ConnectionClosed)`，不等待，不再尝试 | 调用 `EnsureConnected`/`EnsureConnectedAsync` 重连；重连失败则记录结果、等待 `RetryInterval`、进入下一次尝试（与现状一致） |
| B | `SendFrame` 返回 `false`，或 `SendFrame`/`ReceiveFrame` 抛出通信异常（`IsCommunicationException(ex) == true`） | 调用 `InvalidateConnection()` 后立即返回该次的失败结果（`ConnectionClosed`），不再尝试 | 调用 `InvalidateConnection()`，等待 `RetryInterval`，进入下一次尝试 |
| C | 接收结果失败，且 `RequiresNewConnection(result) == true` | 调用 `InvalidateConnection()` 后立即返回该次接收结果（保留真实的 `ErrorKind`，例如 `Timeout`），不再尝试 | 调用 `InvalidateConnection()`，等待 `RetryInterval`，进入下一次尝试 |
| D | 接收结果失败，且 `RequiresNewConnection(result) == false`，且不是 `ModbusException` | 等待 `RetryInterval`，在同一连接上进入下一次尝试，次数受 `RetryCount` 限制 | 同左 |
| E | 接收结果为 `ModbusException` | 立即返回（终态，与现状一致） | 同左 |
| F | 接收成功 | 返回成功 | 同左 |

钩子重命名：基类的 `protected virtual bool ShouldReconnectAfterFailure(ModbusResult<byte[]> result)` 重命名为 `protected virtual bool RequiresNewConnection(ModbusResult<byte[]> result)`，默认返回 `false`；`ModbusTcpClient` 的覆写条件保持不变（`ErrorKind` 为 `Timeout` 或 `ConnectionClosed` 时返回 `true`）；`ModbusRtuClient` 不覆写。重命名后，全仓库搜索 `ShouldReconnectAfterFailure`，结果必须为 0 处（含测试与注释）。

### 5.3 修改文件

- `Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs`：`ExecuteRequestWithRetry`、`ExecuteRequestWithRetryAsync`、`ShouldReconnectAfterFailure`（重命名）。
- `Junevy.Communication.Modbus/Tcp/ModbusTcpClient.cs`：覆写重命名。
- `Junevy.Communication.Modbus/Tcp/ModbusTcpClientConfig.cs`、`Junevy.Communication.Modbus/Rtu/ModbusRtuClientConfig.cs`：`Reconnect`、`RetryCount` 的 XML 注释。

### 5.4 新增文件

- `Junevy.Communication.Modbus.Tests/TransportBaseRetryTests.cs`。

### 5.5 步骤

1. 创建 `TransportBaseRetryTests.cs`，写入 5.6 的全部测试，运行，确认 `R1`、`R3`、`R5` 对应用例失败（旧实现会多次重试）。
2. 在 `FakeTransport` 中加入 `RequiresNewConnection` 覆写：返回 `result.ErrorKind == ModbusErrorKind.Timeout || result.ErrorKind == ModbusErrorKind.ConnectionClosed`。
3. 按 5.2 的表格重写两个重试循环。实现要求：
   - 规则 A 的"立即返回"在循环体内、调用 `EnsureConnected` 之前判断：`if (!IsConnected && !ReconnectEnabled) return ModbusResult<byte[]>.Fail(GetNotConnectedMessage(...), ModbusErrorKind.ConnectionClosed);`。
   - 规则 B、C 的"立即返回"在 `InvalidateConnection()` 之后判断：`if (!ReconnectEnabled) return lastResult;`。
   - 规则 B 中 `catch (Exception ex) when (IsCommunicationException(ex))` 子句内的赋值 `lastResult = ...` 保持，再按规则 B 处理。
4. 重命名 `ShouldReconnectAfterFailure` 为 `RequiresNewConnection`，同步修改基类调用点与 `ModbusTcpClient` 覆写。
5. 修改两个配置类的 XML 注释：
   - `Reconnect`：`连接被销毁后（超时、对端关闭、发送失败）是否在下一次尝试前自动重建连接。为 false 时，上述失败发生后请求立即返回，不再重试，需要调用方手动 Connect()。`
   - `RetryCount`：`首次尝试失败后的最大重试次数。TCP 在超时或连接关闭后必须重建连接才能重试，因此 Reconnect 为 false 时这类失败不会重试；不需要换连接的失败（例如 RTU 的 CRC 错误）按此次数重试。`
6. 运行全部测试。已有测试均使用 `Reconnect = true` 或 `RetryCount = 0`，不受规则影响；如有用例因规则变化失败，先阅读用例确认其意图，再决定修改用例，不允许为让测试通过而放宽 5.2 的规则。

### 5.6 测试（类 `TransportBaseRetryTests`，使用 `FakeTransport`；`retryInterval` 统一设为 1000，用于断言"没有等待"）

| 编号 | 测试名 | 配置与脚本 | 断言 |
|---|---|---|---|
| R1 | `Request_NoReconnect_TimeoutDoesNotRetry` | `reconnect=false, retryCount=3`；`ReceiveResults` 入队 1 个 `Fail("t", Timeout)` | `ErrorKind == Timeout`；`SendFrameCalls == 1`；`InvalidateConnectionCalls == 1`；耗时 < 500 ms |
| R2 | `Request_Reconnect_TimeoutRetriesWithNewConnections` | `reconnect=true, retryCount=3, retryInterval=10`；`ReceiveResults` 入队 4 个 `Fail("t", Timeout)` | `ErrorKind == Timeout`；`SendFrameCalls == 4`；`OpenConnectionCalls == 3` |
| R3 | `Request_NoReconnect_NotConnectedReturnsImmediately` | `reconnect=false, retryCount=3`；`Connected=false` | `ErrorKind == ConnectionClosed`；`SendFrameCalls == 0`；`OpenConnectionCalls == 0`；耗时 < 500 ms |
| R4 | `Request_NoReconnect_ProtocolViolationRetriesOnSameConnection` | `reconnect=false, retryCount=2, retryInterval=10`；`ReceiveResults` 入队 3 个 `Fail("p", ProtocolViolation)`；`FakeTransport.RequiresNewConnection` 对 `ProtocolViolation` 返回 `false` | `SendFrameCalls == 3`；`InvalidateConnectionCalls == 0` |
| R5 | `Request_NoReconnect_SendFailureDoesNotRetry` | `reconnect=false, retryCount=3`；`OnSendFrame = () => false` | `ErrorKind == ConnectionClosed`；`SendFrameCalls == 1`；耗时 < 500 ms |
| R6 | `Request_ModbusException_IsTerminal` | `reconnect=true, retryCount=3`；`ReceiveResults` 入队 `Fail("e", ModbusException)` | `SendFrameCalls == 1` |
| R7 | `RequestAsync_NoReconnect_TimeoutDoesNotRetry` | 同 R1，调用 `RequestAsync` | 同 R1 |
| R8 | `RequestAsync_NoReconnect_NotConnectedReturnsImmediately` | 同 R3，调用 `RequestAsync` | 同 R3 |
| R9 | `TcpClient_DefaultConfig_ServerNeverReplies_SendsExactlyOneRequest` | 真实 `ModbusTcpClient`，使用 `SilentTcpServer`，`ReadTimeout=300`，其余配置取默认值（`Reconnect=false, RetryCount=3`） | `ErrorKind == Timeout`；服务端 `ReceivedBytes == 12`（正好一个请求帧）；总耗时 < 1200 ms |

### 5.7 文档

- `CHANGELOG.md` `### 变更（Changed）` 增加：`{日期} 重试与重连解耦：Reconnect=false 时，需要重建连接的失败（超时、连接关闭、发送失败）立即返回真实错误，不再空转重试并被覆盖为 "Not connected"；未连接且 Reconnect=false 时请求立即返回，不再等待 RetryInterval。Reconnect=true 的行为不变。`
- `readme.md` "Reconnect and Retry" 小节（约第 222-223 行的要点列表）：把 `RetryCount` 的要点改为 `RetryCount is the number of retries after the first attempt. For TCP, a timeout or a closed connection can only be retried when Reconnect = true; with Reconnect = false the request returns immediately with the real error (Timeout / ConnectionClosed).`
- `Junevy.Communication.Modbus/README.md` "Reconnect" 小节：在 Task 6 统一重写，本 Task 不改。
- `Skills/using-junevy-modbus/SKILL.md` "Behavioral Contracts" 的 Retry 条目改为：`- **Retry**: RetryCount = retries after the first attempt; RetryInterval ms between attempts. A failure that destroys the connection (TCP Timeout/ConnectionClosed, send failure) is retried only when Reconnect=true; with Reconnect=false the request returns immediately with the real ErrorKind.`

### 5.8 提交

`fix(transport)!: decouple retry from reconnect; stop retrying on a destroyed connection`

## 6. Task 4：高层 API 保留 ErrorKind（审查第 3 条）

### 6.1 问题

`ModbusExtensions` 中的高层方法在底层失败时用 `ModbusResult<T>.Fail(result.ErrorMessage ?? "...")` 重新构造结果，`ErrorKind` 被重置为 `Unspecified`。2026-10-07 探针实测：底层 `ConnectionClosed`，`ReadHoldingRegisters` 返回 `Unspecified`。另外底层返回成功但数据过短时，`ModbusHelper.ParseRegisters/ParseCoils` 会抛出 `ArgumentException`，违反"结果式 API 不抛异常"的约定；`ParseCoils` 的长度检查 `2 + expectedByteCount` 比实际读取位置（下标 3 起）少 1。

### 6.2 修改文件

- `Junevy.Communication.Modbus/Extensions/ModbusExtensions.cs`
- `Junevy.Communication.Modbus/Utils/ModbusHelper.cs`
- `Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs`
- `Junevy.Communication.Modbus/Rtu/ModbusRtuClient.cs`

### 6.3 新增文件

- `Junevy.Communication.Modbus.Tests/ModbusExtensionsErrorKindTests.cs`

### 6.4 步骤

1. 创建 `ModbusExtensionsErrorKindTests.cs`，写入 6.5 的测试，运行，确认失败。
2. `ModbusExtensions.cs`：把下表中每一处 `Fail(result.ErrorMessage ?? "<文本>")` 改为 `Fail(result.ErrorMessage ?? "<文本>", result.ErrorKind)`（行号为 2026-10-07 时的行号，以方法名为准）：

   | 方法 | 行号 |
   |---|---|
   | `ExecuteReadRequest<T>` | 20 |
   | `ExecuteReadRequestAsync<T>` | 37 |
   | `ReadExceptionStatus` | 278 |
   | `ReadExceptionStatusAsync` | 289 |
   | `ReadWriteMultipleRegisters` | 402 |
   | `ReadWriteMultipleRegistersAsync` | 421 |
   | `ParseCommEventCounter` | 427 |
   | `ParseCommEventLog` | 439 |
   | `ExtractByteCountPayload` | 458 |

3. 上表中的方法，当 `result.IsSuccess == true` 但数据长度不足（`ParseCommEventCounter` 的 `Data.Length < 6`，`ParseCommEventLog` 的 `Data.Length < 9`，`ExtractByteCountPayload` 与 `ReadExceptionStatus` 的 `Data.Length < 3`）时，必须返回 `Fail("<说明>", ModbusErrorKind.ProtocolViolation)`；仅当 `result.IsSuccess == false` 时才透传 `result.ErrorKind`。因此这些方法里原来合并在一个 `if` 中的两种情况要拆成两个分支。
4. `ModbusHelper.cs`：
   - 修正 `ParseCoils` 的长度检查：`response.Length < 3 + expectedByteCount`（原为 `2 + expectedByteCount`）。
   - 新增 `internal static bool TryParseCoils(byte[] response, int length, out bool[] values)` 与 `internal static bool TryParseRegisters(byte[] response, int length, out ushort[] values)`：长度不足或 `length <= 0` 时返回 `false` 且 `values` 为空数组，不抛异常；满足条件时填充结果并返回 `true`。`ParseCoils`、`ParseRegisters` 保持公开且保持抛异常的行为，内部不得改为调用 `Try*`（避免行为变化）。
5. `ModbusExtensions.cs`：`ExecuteReadRequest<T>`、`ExecuteReadRequestAsync<T>` 的委托参数 `Func<byte[], int, T[]> parser` 改为 `TryParse<T>` 委托（`delegate bool TryParse<T>(byte[] response, int length, out T[] values)`，声明为 `ModbusExtensions` 内的 `private delegate`）；调用 `ModbusHelper.TryParseCoils/TryParseRegisters`；返回 `false` 时返回 `Fail("Response data is shorter than the requested quantity.", ModbusErrorKind.ProtocolViolation)`。`ReadWriteMultipleRegisters`、`ReadWriteMultipleRegistersAsync` 同样改用 `TryParseRegisters`。
6. `ModbusTransportBase.cs`：新增 `private static ModbusErrorKind ClassifyCommunicationException(Exception ex) => ex is TimeoutException ? ModbusErrorKind.Timeout : ModbusErrorKind.ConnectionClosed;`，并把下列四处无 `ErrorKind` 的 `Fail` 改为带 `ClassifyCommunicationException(ex)`：
   - `Request` 的 `catch` 中的 `Fail($" [Request] Request failed: {ex.Message}")`
   - `RequestAsync` 的 `catch` 中的 `Fail($" [RequestAsync] Request failed: {ex.Message}")`
   - `ExecuteRequestWithRetry` 的 `catch` 中的 `Fail($" [Request] {ex.Message}")`
   - `ExecuteRequestWithRetryAsync` 的 `catch` 中的 `Fail($" [RequestAsync] {ex.Message}")`
7. `ModbusRtuClient.cs`：`ReceiveFrame` 与 `ReceiveFrameAsync` 中两处 `"Receive buffer is full before a valid RTU frame was parsed."` 的 `Fail` 增加第二个参数 `ModbusErrorKind.ProtocolViolation`。
8. 运行 6.5 的测试与全量测试，确认通过。

### 6.5 测试

文件 `ModbusExtensionsErrorKindTests.cs`，使用 Moq 模拟 `IModbus`（`ProtocolType` 返回 `ModbusProtocolType.RTU`，使 PDU 偏移为 0）。

- 测试 `HighLevelApi_PropagatesErrorKind`：`[Theory]`，数据为（操作名，错误类型）的笛卡尔积。操作名共 16 个同步 + 16 个异步（`ReadCoils`、`ReadDiscreteInputs`、`ReadHoldingRegisters`、`ReadInputRegisters`、`WriteSingleCoil`、`WriteSingleRegister`、`WriteMultipleCoils`、`WriteMultipleRegisters`、`ReadExceptionStatus`、`Diagnostics`（ushort 重载）、`Diagnostics`（byte[] 重载）、`GetCommEventCounter`、`GetCommEventLog`、`ReportServerId`、`MaskWriteRegister`、`ReadWriteMultipleRegisters`）；错误类型取 `Timeout`、`ConnectionClosed`、`Cancelled`、`ModbusException`、`ProtocolViolation`、`InvalidRequest`。模拟的 `Request`/`RequestAsync` 返回 `ModbusResult<byte[]>.Fail("x", kind)`。断言：`IsSuccess == false` 且 `ErrorKind == kind`。
- 测试 `HighLevelApi_ShortData_ReturnsProtocolViolationWithoutThrowing`：模拟返回 `Success(new byte[] { 1, 3, 2 })`，调用 `ReadHoldingRegisters(1, 0, 10)`、`ReadCoils(1, 0, 16)`、`ReadWriteMultipleRegisters`（读 10 个）、`GetCommEventCounter`、`GetCommEventLog`、`ReportServerId`、`ReadExceptionStatus` 各一次（及其异步版本）。断言：无异常；`IsSuccess == false`；`ErrorKind == ProtocolViolation`。
- 测试 `ParseCoils_ShortByOne_ThrowsArgumentException`：`ModbusHelper.ParseCoils(new byte[] { 1, 1, 1 }, 8)`（长度 3，需要 4）抛出 `ArgumentException`，而不是 `IndexOutOfRangeException`。
- 测试 `Transport_CommunicationException_IsClassified`（使用 `FakeTransport`）：`OnSendFrame` 抛出 `System.Net.Sockets.SocketException` 时，`Request` 的 `ErrorKind == ConnectionClosed`；抛出 `TimeoutException` 时，`ErrorKind == Timeout`；`reconnectEnabled=false, retryCount=0`。
- 回补 Task 1 的测试：`ReadHoldingRegistersAsync_ServerNeverReplies_ReturnsWithinReadTimeout` 增加断言 `ErrorKind == Timeout`。

### 6.6 文档

- `CHANGELOG.md` `### 修复（Fixed）` 增加：`{日期} 高层 API（ReadCoils / ReadHoldingRegisters / ReadWriteMultipleRegisters / GetCommEvent* / ReportServerId / ReadExceptionStatus 等）保留底层的 ErrorKind（此前一律重置为 Unspecified）；底层返回数据过短时返回 ProtocolViolation，不再抛出 ArgumentException；ModbusHelper.ParseCoils 的长度检查修正为 3 + 字节数。`
- `Skills/using-junevy-modbus/SKILL.md` "Common Mistakes" 之前不新增内容；第 69 行的示例已读取高层结果的 `ErrorKind`，本 Task 后该示例才正确，无需改文字。

### 6.7 提交

`fix(extensions): propagate ErrorKind from high-level API; never throw on short data`

## 7. Task 5：别名与移除（审查第 2 条）

### 7.1 问题

`ModbusConnectionManager.TryRemove(key)` 在存在指向 `key` 的别名时，只删除 `key` 的条目，不释放实例。此后别名解析失败（`Get("alias")` 返回 `null`），`manager.Dispose()` 也遍历不到该实例，TCP 连接或串口永久不释放。2026-10-07 探针实测确认。另外 `RegisterAlias` 允许指向不存在的键；`RemoveAlias` 通过"先删除、再放回"实现，非原子。

### 7.2 新的固定语义

1. `TryRemove(key)`：
   - `key` 不存在：返回 `false`。
   - `key` 是别名：只删除该别名，返回 `true`。
   - `key` 是直接实例：删除该条目，同时删除所有直接或间接指向它的别名（别名链 `a → b → key` 中的 `a`、`b` 都删除），然后释放实例（捕获并记录释放时的异常），返回 `true`。若同一个实例对象还被另一个直接条目引用（`ReferenceEquals`），则不释放实例。
2. `RegisterAlias(aliasKey, existingKey)`：
   - 任一参数为 `null` 或空字符串：抛出 `ArgumentException`（与现状一致）。
   - `aliasKey == existingKey`（`StringComparison.Ordinal`）：记录警告，返回 `false`。
   - `existingKey` 在注册表中不存在（既不是直接实例也不是别名）：记录警告，返回 `false`。
   - `aliasKey` 已存在：记录警告，返回 `false`（与现状一致）。
   - 其余情况：添加别名，返回 `true`。
3. `RemoveAlias(aliasKey)`：条目不存在返回 `false`；条目是直接实例返回 `false` 并记录警告，注册表保持不变（不再"删除后放回"）；条目是别名则删除并返回 `true`。
4. 并发：`TryRemove`、`RegisterAlias`、`RemoveAlias` 三个方法的主体在同一个私有锁 `private readonly object registryLock = new object();` 内执行。`Add`、`GetOrAdd`、`Get`、`TryGet` 保持无锁。

### 7.3 修改文件

- `Junevy.Communication.Modbus/Factory/ModbusConnectionManager.cs`：`TryRemove`、`RegisterAlias`、`RemoveAlias`。
- `Junevy.Communication.Modbus/Factory/IModbusFactory.cs`：`TryRemove`、`RegisterAlias` 的 XML 注释。
- `Junevy.Communication.Modbus/Factory/IModbusConnectionManager.cs`：同上。

### 7.4 新增文件

- `Junevy.Communication.Modbus.Tests/ConnectionManagerAliasTests.cs`

### 7.5 步骤

1. 创建 `ConnectionManagerAliasTests.cs`，测试内定义 `private sealed class TrackedModbus : IModbus`：`DisposeCount` 计数属性，`Dispose()` 递增计数，其余成员抛出 `NotSupportedException`。写入 7.6 的测试，运行，确认 `TryRemove_MasterWithAlias_*` 与 `RegisterAlias_MissingTarget_*` 失败。
2. 按 7.2 修改 `ModbusConnectionManager` 的 `TryRemove`、`RegisterAlias`、`RemoveAlias` 三个方法，并新增 `registryLock` 字段。收集间接别名的算法固定为：令集合 `S = { key }`；循环：遍历所有别名条目，把 `AliasTarget ∈ S` 的别名键加入 `S`；直到一轮遍历没有新增；最后删除 `S` 中除 `key` 之外的所有别名键。
3. 更新 `IModbusFactory.TryRemove` 的 XML 注释，替换原句 `If the instance is referenced by aliases, only this name is removed.` 为：`Removes the instance registered under the given name, every alias that resolves to it, and disposes the instance. Removing an alias only removes the alias.`；更新 `RegisterAlias` 的 XML 注释，增加一句：`Returns false when existingKey is not registered, when aliasKey equals existingKey, or when aliasKey already exists.`。`IModbusConnectionManager` 的对应注释同样更新。
4. 删除 `ModbusConnectionManager.TryRemove` 中记录 `"Removed '{Key}' but kept instance alive (aliases exist)."` 的分支。
5. 运行测试，确认通过。

### 7.6 测试（类 `ConnectionManagerAliasTests`）

| 测试名 | 动作 | 断言 |
|---|---|---|
| `TryRemove_MasterWithAlias_DisposesInstanceAndRemovesAlias` | `Add("m", t)`；`RegisterAlias("a", "m")`；`TryRemove("m")` | 返回 `true`；`t.DisposeCount == 1`；`Get("a") == null`；`Keys` 为空；`Count == 0` |
| `TryRemove_MasterWithAliasChain_RemovesWholeChain` | `Add("m", t)`；`RegisterAlias("a", "m")`；`RegisterAlias("b", "a")`；`TryRemove("m")` | `Keys` 为空；`t.DisposeCount == 1` |
| `TryRemove_Alias_KeepsInstance` | `Add("m", t)`；`RegisterAlias("a", "m")`；`TryRemove("a")` | 返回 `true`；`Get("m") == t`；`t.DisposeCount == 0` |
| `TryRemove_SameInstanceUnderTwoDirectKeys_DoesNotDispose` | `Add("m1", t)`；`Add("m2", t)`；`TryRemove("m1")` | `t.DisposeCount == 0`；`Get("m2") == t` |
| `RegisterAlias_MissingTarget_ReturnsFalse` | `RegisterAlias("a", "nope")` | 返回 `false`；`Keys` 为空 |
| `RegisterAlias_SameKey_ReturnsFalse` | `Add("m", t)`；`RegisterAlias("m", "m")` | 返回 `false` |
| `RegisterAlias_ToAlias_Resolves` | `Add("m", t)`；`RegisterAlias("a", "m")`；`RegisterAlias("b", "a")` | `Get("b") == t` |
| `RemoveAlias_DirectEntry_ReturnsFalseAndKeepsEntry` | `Add("m", t)`；`RemoveAlias("m")` | 返回 `false`；`Get("m") == t` |
| `Dispose_AfterRemovingMasterWithAlias_LeavesNoLeak` | `Add("m", t)`；`RegisterAlias("a", "m")`；`TryRemove("m")`；`Dispose()` | `t.DisposeCount == 1`（只释放一次） |
| `Concurrent_AddAliasRemove_PreservesInvariants` | 8 个线程，每线程 500 次循环，从键集合 `k0..k3` 随机选键执行 `Add(新 TrackedModbus)`（返回 `false` 时测试自行 `Dispose` 该实例）、`RegisterAlias("a" + 随机 0..3, 随机键)`、`TryRemove(随机键)`、`RemoveAlias("a" + 随机 0..3)`；所有线程结束后 | 对 `Keys` 中每个键，`Get(key) != null`；`创建总数 - 全部实例的 DisposeCount 之和 == mgr.Count` |

### 7.7 文档

- `CHANGELOG.md` `### 修复（Fixed）` 增加：`{日期} ModbusConnectionManager.TryRemove 移除带别名的主连接时，同时移除所有指向它的别名并释放实例（此前实例既不释放也不可达，串口/套接字泄漏）。`；`### 变更（Changed）` 增加：`{日期} RegisterAlias 在目标键不存在或别名键等于目标键时返回 false（此前允许创建悬空别名）；RemoveAlias 对直接实例不再"删除后放回"。`
- `Skills/using-junevy-modbus/SKILL.md` 第 97-100 行的 RS-485 示例之后新增一行：`Removing the master key (factory.TryRemove("rs485-bus")) also removes every alias that points to it and disposes the connection. RegisterAlias returns false when the target key does not exist.`
- `readme.md`：搜索 `alias`/`Alias`，如果存在描述"移除主连接保留实例"的文字，改为上述语义；如果不存在，不修改。

### 7.8 提交

`fix(factory)!: removing a master connection also removes its aliases and disposes it`

## 8. Task 6：收尾与验收

1. 修改 `Junevy.Communication.Modbus/Junevy.Communication.Modbus.csproj`：`<Version>1.0.1</Version>` 改为 `<Version>1.0.2</Version>`。不执行 `nuget push`。
2. 重写 `Junevy.Communication.Modbus/README.md`（当前内容仍是旧 API：`ModbusTCP`、`Communication.Modbus.*` 命名空间、`net6.0`）：
   - "Target Frameworks" 改为 `net472` 与 `net8.0`。
   - 所有示例中的类名、命名空间、配置类名与根目录 `readme.md` 中的 "Minimal Usage" 与 "Recommended Usage" 小节保持逐字一致（从根 `readme.md` 复制）。
   - "Reconnect" 小节替换为与根 `readme.md` "Reconnect and Retry" 小节相同的内容。
3. 清理冗余代码：全仓库搜索 `Task.Run(OpenConnection)`、`ShouldReconnectAfterFailure`、`kept instance alive`、`读超时兜底靠 socket.ReceiveTimeout`，结果必须为 0 处（`CHANGELOG.md` 中的历史描述除外）。
4. 运行第 0 节的三条命令，全部成功。
5. 运行完整验收脚本（在 scratchpad 建控制台项目 `net8.0`，引用库项目，打印以下 5 行并人工核对）：
   1. 异步读超时：`ReadTimeout=500`，服务端不回包，期望 `Timeout`，耗时 450-2500 ms。
   2. 默认配置首次超时：`RetryCount=3`，服务端不回包，期望 `Timeout`，耗时 < 1200 ms，服务端只收到 1 个请求。
   3. 高层 API：服务端关闭连接后调用 `ReadHoldingRegisters`，期望 `ConnectionClosed` 或 `Timeout`，不是 `Unspecified`。
   4. 别名：`Add` → `RegisterAlias` → `TryRemove(主键)`，期望实例被释放，别名解析为 `null`。
   5. 取消：`RequestAsync` 带 200 ms 后取消的令牌，`ReadTimeout=30000`，期望 `Cancelled`，耗时 < 1500 ms。
6. 把第 5 步的输出粘贴到合并请求描述中。
7. 提交：`chore: bump version to 1.0.2 and refresh package README`。

## 9. 未纳入本计划的条目（只记录，不修改）

下列审查发现不在本计划范围内，执行者遇到时不得顺手修改：
- 审查第 5 条（`ProtocolViolation` 不销毁 TCP 连接）：计划二。
- 审查第 6 条（RTU 异步读 `ReadAsync` 返回 0 时空转）。
- 审查第 7 条（RTU 取消吞掉 `OperationCanceledException`、`ErrorMessage` 含堆栈）。
- 审查第 8 条（解析器不校验响应功能码）：计划三 Task 3 顺带处理。
- 审查第 9 条（`Dispose` 与在途请求互斥）：计划二。
- 审查第 11 条（日志热路径 `ToHex()` 提前求值、RTU 重复 RX 日志）。
- 审查第 12 条（直接构造客户端时配置不校验）。
- 审查第 13、14 条（`Socket.Disconnect` 跨平台行为、`SerialStream.ReadAsync` 取消行为，均未验证）。
- `ModbusFactory.ValidateAndFillDefaults` 改写调用者配置对象。
- `ModbusRequest.TransactionId` 被基类回写。
