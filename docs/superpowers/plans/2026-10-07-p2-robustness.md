# 计划二：稳健性改进（P2）

> 来源：2026-10-07 代码审查，"然后"组：TCP 改用 `ConnectAsync(CancellationToken)`（net8.0）与 `FromAsync + WhenAny`（net472）、`ProtocolViolation` 销毁连接、`Dispose` 与请求互斥、`ConnectAsync` 增加取消令牌。
> 前置条件：计划一（`2026-10-07-p1-critical-fixes.md`）已合入 master，`FakeTransport`、`SilentTcpServer` 已存在，基类已有 `RequiresNewConnection` 钩子与 5.2 节的重试规则。
> 版本：完成后 `Version` 由 `1.0.2` 改为 `1.1.0`。`IModbus.ConnectAsync` 增加一个带默认值的参数：调用方源码兼容；自行实现 `IModbus` 的第三方类型与在表达式树中调用 `ConnectAsync()` 的代码（例如 Moq 的 `Setup(m => m.ConnectAsync())`）需要修改，CHANGELOG 中按"变更"登记。
> 后续：计划三依赖本计划合入。

## 0. 执行规则

与计划一第 0 节完全相同（分支改为 `feat/modbus-p2-robustness`；基线记录改为在该分支第一个 commit 重新运行 3 次全量测试并写入提交信息；CHANGELOG 日期、Skills 检查、禁止修改 Wiki 目录的规则不变）。额外规则：

- 访问 `10.255.255.1`（不可路由地址）的测试统一标记 `[Trait("Category", "Network")]`。如果执行环境对该地址立即返回"网络不可达"（单次连接尝试在 300 ms 内失败），这类测试在该环境不具备判定能力：用 `dotnet test --filter "Category!=Network"` 排除，并在合并请求描述中写明"Network 类测试在本环境被排除，原因：立即返回不可达"。排除之后，必须在 Task 1 的 net472 探针里补做同样的场景。

## 1. Task 1：TCP 连接改为真异步

### 1.1 问题

- `ModbusTcpClient.OpenConnectionAsync`（net8）用 `BeginConnect + Task.Factory.FromAsync + WaitAsync` 包装旧式异步模型：超时后底层连接仍在后台尝试，`socket` 被销毁时 `EndConnect` 抛出的异常无人观察。
- net472 分支直接调用同步 `OpenConnection()`，调用线程被阻塞到 `ConnectTimeout`。
- 同步 `OpenConnection` 不释放 `AsyncWaitHandle`。

### 1.2 修改文件

- `Junevy.Communication.Modbus/Tcp/ModbusTcpClient.cs`：方法 `OpenConnection`、`OpenConnectionAsync`。

### 1.3 新增文件

- `Junevy.Communication.Modbus.Tests/TcpConnectTests.cs`。

### 1.4 步骤

1. 创建 `TcpConnectTests.cs`，写入 1.5 的测试，运行，记录当前结果（`ConnectAsync_UnroutableAddress_*` 在改动前可能已通过，这是预期的；改动的价值由 net472 探针和代码审查验证）。
2. 重写 `OpenConnectionAsync`，不再使用 `#if` 分出同步退化分支，整个方法的固定结构如下：
   ```csharp
   protected override async Task<bool> OpenConnectionAsync(CancellationToken cancellationToken)
   {
       if (!ModbusHelper.VerifyAddress(Config.Address) || !ModbusHelper.VerifyPort(Config.Port))
           return false;

       ResetSocket();
       var connectSocket = socket!;

       using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
       timeoutCts.CancelAfter(Config.ConnectTimeout);
       try
       {
   #if NET8_0_OR_GREATER
           await connectSocket.ConnectAsync(Config.Address, Config.Port, timeoutCts.Token).ConfigureAwait(false);
   #else
           var connectTask = Task.Factory.FromAsync(
               connectSocket.BeginConnect(Config.Address, Config.Port, null, null),
               connectSocket.EndConnect);
           var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
           var finished = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
           if (finished != connectTask)
           {
               connectSocket.Dispose();
               connectTask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
               timeoutCts.Token.ThrowIfCancellationRequested();
           }
           await connectTask.ConfigureAwait(false);
   #endif
           stream = new NetworkStream(connectSocket, ownsSocket: false);
           Logger.LogDebug(" [ConnectAsync] Connected to {Address}:{Port}.", Config.Address, Config.Port);
           return true;
       }
       catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
       {
           InvalidateConnection();
           throw;
       }
       catch (OperationCanceledException)
       {
           Logger.LogWarning(" [ConnectAsync] Connection timed out: {Timeout}ms.", Config.ConnectTimeout);
           InvalidateConnection();
           return false;
       }
       catch (Exception ex)
       {
           Logger.LogError(ex, " [ConnectAsync] Connection failed.");
           InvalidateConnection();
           return false;
       }
   }
   ```
   说明：net472 分支中，`Task.WhenAny` 返回的是超时任务时，`timeoutCts.Token.ThrowIfCancellationRequested()` 抛出 `OperationCanceledException`，由上面两个 `catch` 按"用户取消"与"超时"分流。
3. 修改同步 `OpenConnection`：把 `var result = socket!.BeginConnect(...)` 之后的逻辑放入 `try { ... } finally { result.AsyncWaitHandle.Close(); }`，其余逻辑（`WaitOne` 超时、`EndConnect`、`new NetworkStream`）保持不变。
4. 删除 `OpenConnectionAsync` 中旧的 `#else` 分支及其注释（"net472 无 Task.WaitAsync：退化为同步核心…"）。
5. 运行 1.5 的测试与全量测试。
6. net472 探针（运行时验证，必须执行）：
   1. 在 scratchpad 新建 `net472` 控制台项目，引用 `Junevy.Communication.Modbus.csproj`。
   2. 场景 A：本机 `TcpListener` 在监听，`await tcp.ConnectAsync()`，期望返回 `true`。
   3. 场景 B：本机一个已关闭的端口，`await tcp.ConnectAsync()`，期望返回 `false` 且耗时 < `ConnectTimeout`。
   4. 场景 C：地址 `10.255.255.1`，`ConnectTimeout=500`。先记录 `var sw = Stopwatch.StartNew(); var t = tcp.ConnectAsync();` 之后立即读取 `sw.ElapsedMilliseconds` 与 `t.IsCompleted`，期望 `ElapsedMilliseconds < 100` 且 `IsCompleted == false`（证明调用线程没有被阻塞）；然后 `await t`，期望返回 `false`，总耗时在 450 ms 到 2500 ms 之间。
   5. 场景 D：与场景 C 相同的地址，`ConnectTimeout=10000`，传入 200 ms 后取消的令牌（使用 Task 2 完成后的 `ConnectAsync(token)`；若尚未完成 Task 2，则在 Task 2 结束后补做本场景），期望抛出 `OperationCanceledException`，耗时 < 1500 ms。
   6. 把输出粘贴到 commit 信息正文。

### 1.5 测试（类 `TcpConnectTests`）

| 测试名 | 配置 | 动作 | 断言 |
|---|---|---|---|
| `ConnectAsync_ListeningPort_ReturnsTrue` | `ConnectTimeout=10000`，目标为本机 `TcpListener` | `await tcp.ConnectAsync()` | 返回 `true`；`tcp.IsConnected == true` |
| `ConnectAsync_ClosedPort_ReturnsFalse` | 先启动 `TcpListener` 取得端口再 `Stop()` | `await tcp.ConnectAsync()` | 返回 `false`；`IsConnected == false`；耗时 < 5000 ms |
| `Connect_ClosedPort_ReturnsFalse` | 同上 | 同步 `tcp.Connect()` | 返回 `false`；`IsConnected == false` |
| `ConnectAsync_UnroutableAddress_ShortTimeout_ReturnsFalse`（`Category=Network`） | `Address="10.255.255.1"`，`ConnectTimeout=1` | `await tcp.ConnectAsync()` | 不抛异常；返回 `false`；`IsConnected == false` |
| `Connect_UnroutableAddress_RespectsConnectTimeout`（`Category=Network`） | `Address="10.255.255.1"`，`ConnectTimeout=500` | 同步 `tcp.Connect()` | 返回 `false`；耗时 ≥ 450 ms 且 ≤ 2500 ms |
| `ConnectAsync_EmptyAddress_ReturnsFalse` | `Address=""` | `await tcp.ConnectAsync()` | 返回 `false` |

### 1.6 文档

- `CHANGELOG.md` `### 修复（Fixed）` 增加：`{日期} TCP 异步连接：net8.0 使用 Socket.ConnectAsync(CancellationToken)，net472 使用 FromAsync + Task.WhenAny 实现真异步（此前 net472 阻塞调用线程到 ConnectTimeout）；同步连接释放 AsyncWaitHandle。`
- `Skills/using-junevy-modbus/SKILL.md` "Behavioral Contracts" 的 Thread-safety 条目中，把 `(instant on net8.0; next-I/O-boundary on net472)` 改为 `(net8.0: instant; net472: reads and connects are aborted by closing the socket, so cancellation also takes effect immediately)`。

### 1.7 提交

`refactor(tcp): use ConnectAsync on net8.0 and FromAsync+WhenAny on net472`

## 2. Task 2：`ConnectAsync` 增加取消令牌

### 2.1 固定契约

- 新签名：`Task<bool> ConnectAsync(CancellationToken cancellationToken = default);`（`IModbus` 接口与 `ModbusTransportBase`）。
- 令牌在两处生效：等待 `requestLock`（`await requestLock.WaitAsync(cancellationToken)`）与 `OpenConnectionAsync(cancellationToken)`。
- 用户取消：抛出 `OperationCanceledException`（不返回 `false`，因为 `Task<bool>` 无法表达"已取消"）。
- 连接失败或连接超时：返回 `false`（与现状一致）。

### 2.2 修改文件

- `Junevy.Communication.Modbus/Core/Interfaces/IModbus.cs`：`ConnectAsync` 签名与 XML 注释（注释中写明上面三条契约）。
- `Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs`：`ConnectAsync`。
- 全仓库搜索 `ConnectAsync()` 的调用点与 `IModbus` 的其他实现：2026-10-07 搜索结果为仅有基类实现；若执行时发现其他实现或表达式树调用，逐一修改。

### 2.3 新增文件

- `Junevy.Communication.Modbus.Tests/ConnectAsyncTokenTests.cs`。

### 2.4 步骤

1. 创建测试文件，写入 2.5 的测试，运行，确认编译失败（签名尚未改变）。
2. 修改 `IModbus.ConnectAsync` 与 `ModbusTransportBase.ConnectAsync`：
   ```csharp
   public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
   {
       ThrowIfDisposed();

       await requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
       try
       {
           return await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
       }
       finally
       {
           requestLock.Release();
       }
   }
   ```
3. 运行 2.5 的测试和全量测试。
4. 补做 Task 1 第 6 步的场景 D。

### 2.5 测试（类 `ConnectAsyncTokenTests`，使用 `FakeTransport`）

| 测试名 | 动作 | 断言 |
|---|---|---|
| `ConnectAsync_PreCancelledToken_ThrowsOperationCanceled` | 传入已取消的令牌 | 抛出 `OperationCanceledException`（`TaskCanceledException` 也满足）；`OpenConnectionAsyncCalls == 0` |
| `ConnectAsync_TokenIsPassedToOpenConnectionAsync` | `OnOpenConnectionAsync` 记录收到的令牌；传入未取消的 `cts.Token` | 记录的令牌 `== cts.Token` |
| `ConnectAsync_CancelledWhileWaitingForLock_Throws` | 先启动一个 `Request`，其 `OnSendFrame` 阻塞在 `ManualResetEventSlim` 上（占用 `requestLock`）；再调用 `ConnectAsync(cts.Token)`，100 ms 后取消 | 抛出 `OperationCanceledException`；`OpenConnectionAsyncCalls == 0`；随后放行 `ManualResetEventSlim`，第一个请求正常结束 |
| `ConnectAsync_CancelledDuringOpen_Throws` | `OnOpenConnectionAsync = async ct => { await Task.Delay(Timeout.Infinite, ct); return false; }`，100 ms 后取消 | 抛出 `OperationCanceledException`，耗时 < 1500 ms |
| `ConnectAsync_DefaultToken_StillWorks` | `await transport.ConnectAsync()`（不传参数） | 返回 `true` |

### 2.6 文档

- `CHANGELOG.md` `### 变更（Changed，破坏性）` 增加：`{日期} IModbus.ConnectAsync 增加参数 CancellationToken cancellationToken = default：调用方源码兼容；自行实现 IModbus 的类型需要更新签名；用户取消时抛出 OperationCanceledException，连接失败或超时仍返回 false。`
- `Skills/using-junevy-modbus/SKILL.md`：第 13 行 `no ConnectAsync(host, port) overloads` 之后追加一句：`ConnectAsync(CancellationToken) throws OperationCanceledException when cancelled and returns false when the connection fails or times out.`
- `readme.md`：搜索 `ConnectAsync`，如有示例则补充 `CancellationToken` 用法；没有则不修改。

### 2.7 提交

`feat(api)!: ConnectAsync accepts a CancellationToken`

## 3. Task 3：`ProtocolViolation` 销毁 TCP 连接

### 3.1 问题

TCP 的 `RequiresNewConnection` 只覆盖 `Timeout` 与 `ConnectionClosed`。非法 PDU 长度（响应头 6 字节已读，负载未读）、事务 ID 不匹配、协议 ID 非零、从站号不匹配等 `ProtocolViolation` 之后，流中仍有未读字节或迟到的响应，下一次请求会读到残帧。

### 3.2 固定规则

`ModbusTcpClient.RequiresNewConnection(result)` 的返回值改为：`result.ErrorKind` 是 `Timeout`、`ConnectionClosed` 或 `ProtocolViolation` 之一时返回 `true`，否则返回 `false`。`ModbusException` 不在其中（异常响应是完整且合法的应答，连接可继续使用）。`ModbusRtuClient` 不改（串口通过 `DiscardInBuffer` 与帧扫描恢复同步）。重试行为由计划一 5.2 节的规则 C 决定：`Reconnect=false` 立即返回；`Reconnect=true` 销毁连接后在下一次尝试前重连。

### 3.3 修改文件

- `Junevy.Communication.Modbus/Tcp/ModbusTcpClient.cs`：`RequiresNewConnection` 覆写。

### 3.4 新增文件

- `Junevy.Communication.Modbus.Tests/TestSupport/ScriptedTcpServer.cs`
- `Junevy.Communication.Modbus.Tests/TcpProtocolViolationTests.cs`

`ScriptedTcpServer` 的公开成员（名称固定）：
- `static ScriptedTcpServer Start(Func<int, NetworkStream, CancellationToken, Task> handler)`：监听本机随机端口；每接受一个连接，以连接序号（从 0 开始）、该连接的 `NetworkStream`、服务端关闭令牌调用一次 `handler`，不等待其完成。
- `int Port { get; }`
- `int AcceptedConnectionCount { get; }`
- `void Dispose()`：停止监听，取消令牌，关闭所有连接。

### 3.5 步骤

1. 创建 `ScriptedTcpServer.cs` 与 `TcpProtocolViolationTests.cs`，写入 3.6 的测试，运行，确认 `PV1`、`PV3` 失败。
2. 修改 `RequiresNewConnection` 覆写，加入 `ProtocolViolation`。
3. 运行 3.6 的测试与全量测试。已有测试 `TcpClient_InvalidPduLength_ReturnsFailureInsteadOfThrowing`（`Reconnect=true, RetryCount=0`）必须仍然通过。

### 3.6 测试（类 `TcpProtocolViolationTests`）

辅助：合法的"读 1 个保持寄存器"响应帧 = `[TID(2), 00 00, 00 05, 01, 03, 02, 00 01]`（TID 取自收到的请求前 2 字节）。请求帧固定 12 字节。

| 编号 | 测试名 | 配置与服务端脚本 | 断言 |
|---|---|---|---|
| PV1 | `TidMismatch_NoReconnect_ConnectionIsDestroyed` | `Reconnect=false, RetryCount=3`；服务端读 12 字节请求，回复 TID 为 `请求TID + 1` 的合法帧 | `ErrorKind == ProtocolViolation`；`tcp.IsConnected == false`；服务端 `AcceptedConnectionCount == 1`；服务端收到的请求数 == 1 |
| PV2 | `TidMismatch_Reconnect_RetriesOnNewConnection` | `Reconnect=true, RetryCount=1, RetryInterval=10`；连接 0：回复错误 TID 的帧；连接 1：回复正确 TID 的合法帧 | `IsSuccess == true`；返回的寄存器值为 `1`；服务端 `AcceptedConnectionCount == 2` |
| PV3 | `InvalidPduLength_Reconnect_NextRequestUsesFreshConnection` | `Reconnect=true, RetryCount=0`；连接 0：读请求后回复头 `[TID, 00 00, 01 2C]`（长度 300）及 10 个字节的垃圾；连接 1：对每个请求回复合法帧 | 第一次请求 `ErrorKind == ProtocolViolation`；第二次请求 `IsSuccess == true`；`AcceptedConnectionCount == 2` |
| PV4 | `ModbusException_DoesNotDestroyConnection` | `Reconnect=false, RetryCount=3`；服务端对第 1 个请求回复异常帧 `[TID, 00 00, 00 03, 01, 83, 02]`，对第 2 个请求回复合法帧（同一连接） | 第一次 `ErrorKind == ModbusException` 且 `tcp.IsConnected == true`；第二次 `IsSuccess == true`；`AcceptedConnectionCount == 1` |

### 3.7 文档

- `CHANGELOG.md` `### 修复（Fixed）` 增加：`{日期} TCP 收到 ProtocolViolation（非法 PDU 长度、事务 ID 不匹配、协议 ID 非零、从站号不匹配）后销毁连接，避免残帧污染下一次请求；Modbus 异常响应不受影响。`
- `Skills/using-junevy-modbus/SKILL.md` "Behavioral Contracts" 的 Reconnect 条目中，把 `after failures marked `Timeout`/`ConnectionClosed`` 改为 `after failures marked `Timeout`/`ConnectionClosed`/`ProtocolViolation` (TCP only)`。
- `readme.md` "Error Classification" 小节：在 `ProtocolViolation` 的说明后追加 `(TCP: the connection is discarded)`。

### 3.8 提交

`fix(tcp): discard the connection after a protocol violation`

## 4. Task 4：`Dispose` 与在途请求互斥

### 4.1 问题

`ModbusTransportBase.Dispose()` 先 `DisposeConnection()`，再 `requestLock.Dispose()`，不等待在途请求。请求线程的 `finally { requestLock.Release(); }` 在锁已释放资源后可能抛出 `ObjectDisposedException`，从结果式 API 中逃逸；排队中的请求在 `requestLock.Wait()` 处同样抛出。TCP 同步路径还使用 `socket!`，`DisposeConnection` 把字段置 `null` 后在途线程出现 `NullReferenceException`（不在 `IsCommunicationException` 内，也会逃逸）。

### 4.2 固定契约

1. `Dispose()` 幂等，可被多个线程同时调用，只有第一个调用执行释放。
2. `Dispose()` 返回时：底层套接字/串口已关闭；在途请求已经退出；不会再有请求使用连接。
3. 在 `Dispose()` 开始之前已进入 `Request`/`RequestAsync` 的调用（在途或排队）：返回 `Fail(..., ModbusErrorKind.ConnectionClosed)`，错误消息为 `" [Request] Client disposed."`（异步为 `" [RequestAsync] Client disposed."`），不抛异常。
4. 在 `Dispose()` 开始之后才调用 `Request`/`RequestAsync`/`Connect`/`ConnectAsync`：抛出 `ObjectDisposedException`（属于使用方编程错误，与 .NET 惯例一致）。
5. `Disconnect()` 在 `Dispose()` 之后调用：什么都不做，不抛异常。
6. `RequestAsync` 在途时 `Dispose()`：返回 `ConnectionClosed`（不是 `Cancelled`）；仅当调用方自己的令牌被取消时才返回 `Cancelled`。

### 4.3 修改文件

- `Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs`：字段、`Dispose`、`Request`、`RequestAsync`、`Connect`、`ConnectAsync`、`Disconnect`、两个重试循环。
- `Junevy.Communication.Modbus/Tcp/ModbusTcpClient.cs`：`SendFrame`、`ReceiveExact`、`ReceiveExactAsync`。
- `Junevy.Communication.Modbus.Tests/TestSupport/SilentTcpServer.cs`：新增属性 `int ClosedByPeerCount { get; }`（读到 0 字节而结束的连接数，线程安全）。

### 4.4 新增文件

- `Junevy.Communication.Modbus.Tests/DisposeConcurrencyTests.cs`

### 4.5 步骤

1. 修改 `SilentTcpServer`，加入 `ClosedByPeerCount`。
2. 创建 `DisposeConcurrencyTests.cs`，写入 4.6 的测试，运行，确认 `D1`、`D2`、`D3` 在改动前失败或出现异常。
3. `ModbusTransportBase` 字段与 `Dispose`：
   - 删除 `requestLock.Dispose()`。`SemaphoreSlim` 只有在使用 `AvailableWaitHandle` 时才持有需要释放的资源，本类不使用，因此不释放；在字段声明处加注释 `// 不 Dispose：排队中的请求仍会等待该信号量，释放后它们会抛出 ObjectDisposedException。SemaphoreSlim 未使用 AvailableWaitHandle，无非托管资源。`
   - 新增 `private int disposeState;` 与 `private readonly CancellationTokenSource disposeCts = new CancellationTokenSource();`（同样不 Dispose，注释说明原因：未使用 `CancelAfter`/链接，无计时器资源；Dispose 之后仍会被 `Token` 属性访问）。
   - 把 `protected bool disposed;` 改为 `protected volatile bool disposed;`。
   - `Dispose()` 的固定实现：
     ```csharp
     public void Dispose()
     {
         if (Interlocked.Exchange(ref disposeState, 1) == 1)
             return;

         disposed = true;
         disposeCts.Cancel();
         DisposeConnection();          // 立即中断在途 I/O，使在途请求尽快失败退出

         requestLock.Wait();           // 等待在途请求退出
         try
         {
             DisposeConnection();      // 清理在途请求在 Dispose 期间可能重建的连接
         }
         finally
         {
             requestLock.Release();
         }

         Logger.LogDebug(" [Dispose] {Transport} disposed.", GetType().Name);
     }
     ```
4. `Request`：方法第一行调用 `ThrowIfDisposed();`；获取 `requestLock` 之后、`AssignsTransactionId` 之前插入 `if (disposed) return ModbusResult<byte[]>.Fail(" [Request] Client disposed.", ModbusErrorKind.ConnectionClosed);`。
5. `RequestAsync`：
   - 方法第一行调用 `ThrowIfDisposed();`。
   - 创建 `using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposeCts.Token);`，此后方法内所有使用 `cancellationToken` 的位置（`requestLock.WaitAsync`、`ExecuteRequestWithRetryAsync`）改用 `linked.Token`。
   - 获取锁之后插入 `if (disposed) return ModbusResult<byte[]>.Fail(" [RequestAsync] Client disposed.", ModbusErrorKind.ConnectionClosed);`。
   - 现有 `catch (OperationCanceledException ex)` 子句体开头插入：`if (disposed && !cancellationToken.IsCancellationRequested) return ModbusResult<byte[]>.Fail(" [RequestAsync] Client disposed.", ModbusErrorKind.ConnectionClosed);`。
6. 两个重试循环（`ExecuteRequestWithRetry`、`ExecuteRequestWithRetryAsync`）：每次尝试开始时（`for` 循环体第一行）插入 `if (disposed) return ModbusResult<byte[]>.Fail("<同步/异步对应的 Client disposed 文本>", ModbusErrorKind.ConnectionClosed);`。
7. `Connect`：`ThrowIfDisposed();` 保持，获取锁之后再调用一次 `ThrowIfDisposed();`。`ConnectAsync` 同理（锁之后再检查）。`Disconnect`：方法第一行 `if (disposed) return;`，获取锁之后再检查一次 `if (disposed) return;`（在 `try` 内的 `CloseConnection()` 之前）。
8. `ModbusTcpClient`：
   - `SendFrame` 与 `ReceiveExact` 方法开头使用局部变量 `var activeSocket = socket ?? throw new ObjectDisposedException(nameof(ModbusTcpClient));`，方法内用 `activeSocket` 替代所有 `socket!`。
   - `ReceiveExactAsync` 的 `var target = stream;` 之后增加 `if (target is null) throw new ObjectDisposedException(nameof(ModbusTcpClient));`（同时消除编译警告 CS8602）。
9. 运行 4.6 的测试和全量测试。
10. 循环稳定性验证：对 `DisposeConcurrencyTests` 连续运行 20 次（`for i in $(seq 1 20); do dotnet test --no-build --filter "FullyQualifiedName~DisposeConcurrencyTests"; done`），20 次全部通过才算完成。
11. RTU 路径无法用自动化测试覆盖（没有串口硬件）。如果执行环境有虚拟串口对（例如 com0com），手动验证：RTU 客户端 `ReadTimeout=10000` 发出请求、对端不应答，300 ms 后调用 `Dispose()`，请求在 3 秒内返回 `ConnectionClosed`，且无异常。没有虚拟串口对时，在合并请求描述中写明"RTU 在途 Dispose 未验证"。

### 4.6 测试（类 `DisposeConcurrencyTests`，使用 `SilentTcpServer`，`ReadTimeout=10000, RetryCount=0, Reconnect=false`）

| 编号 | 测试名 | 动作 | 断言 |
|---|---|---|---|
| D1 | `Dispose_DuringSyncRequest_ReturnsConnectionClosed` | `Task.Run(() => tcp.Request(...))`；300 ms 后 `tcp.Dispose()`；整个场景循环 20 次，每次使用新的服务端与客户端 | 每次请求任务在 3000 ms 内完成；无异常；`IsSuccess == false`；`ErrorKind == ConnectionClosed` |
| D2 | `Dispose_DuringAsyncRequest_ReturnsConnectionClosed` | 同 D1，使用 `RequestAsync` | 同 D1 |
| D3 | `Dispose_WithQueuedRequests_AllReturnConnectionClosed` | 1 个在途 `RequestAsync` 加 3 个排队 `RequestAsync`（启动后等待 100 ms 保证排队）；300 ms 后 `Dispose()` | 4 个任务在 3000 ms 内全部完成；无异常；全部 `IsSuccess == false` 且 `ErrorKind == ConnectionClosed` |
| D4 | `Request_AfterDispose_ThrowsObjectDisposed` | `Dispose()` 后调用 `Request` | 抛出 `ObjectDisposedException` |
| D5 | `RequestAsync_AfterDispose_ThrowsObjectDisposed` | `Dispose()` 后 `await RequestAsync` | 抛出 `ObjectDisposedException` |
| D6 | `Connect_AfterDispose_ThrowsObjectDisposed` | `Dispose()` 后 `Connect()` 与 `await ConnectAsync()` | 均抛出 `ObjectDisposedException` |
| D7 | `Dispose_Twice_DoesNotThrow` | 连续调用两次，以及两个线程同时调用 | 无异常 |
| D8 | `Disconnect_AfterDispose_IsNoOp` | `Dispose()` 后 `Disconnect()` | 无异常 |
| D9 | `Dispose_ClosesSocket` | 连接成功后 `Dispose()`，等待 500 ms | `server.ClosedByPeerCount == 1` |
| D10 | `RequestAsync_UserCancelsWhileNotDisposed_StillReturnsCancelled` | 令牌 200 ms 后取消，`ReadTimeout=30000` | `ErrorKind == Cancelled`（确认第 4.2 条第 6 点没有吞掉用户取消） |

### 4.7 文档

- `CHANGELOG.md` `### 修复（Fixed）` 增加：`{日期} Dispose 与在途/排队请求互斥：在途请求返回 ConnectionClosed（不再抛 ObjectDisposedException 或 NullReferenceException），Dispose 返回时连接已关闭且无请求在途；Dispose 之后调用 Request/Connect 抛出 ObjectDisposedException，Disconnect 为空操作；不再释放内部 SemaphoreSlim。`
- `Skills/using-junevy-modbus/SKILL.md` "Behavioral Contracts" 新增一条：`- **Dispose**: Dispose() aborts in-flight I/O and waits for the in-flight request to exit. Requests that were running or queued return ErrorKind.ConnectionClosed; calling Request/Connect after Dispose throws ObjectDisposedException; Disconnect after Dispose is a no-op.`
- `readme.md` "Notes" 小节：追加同一条说明（英文原文相同）。

### 4.8 提交

`fix(transport): make Dispose wait for in-flight requests and fail them with ConnectionClosed`

## 5. Task 5：收尾与验收

1. `Junevy.Communication.Modbus.csproj`：`<Version>1.0.2</Version>` 改为 `<Version>1.1.0</Version>`。不执行 `nuget push`。
2. `Junevy.Communication.Modbus/README.md`：补充 `ConnectAsync(CancellationToken)`、Dispose 契约各一句，文字与根 `readme.md` 对应位置一致。
3. 清理冗余代码：全仓库搜索 `net472 无 Task.WaitAsync`、`WaitAsync(timeoutCts.Token)`、`socket!.`（`ModbusTcpClient.cs` 内），结果必须为 0 处。
4. 运行计划一第 0 节的三条命令，全部成功。
5. 验收脚本（scratchpad 控制台项目，分别以 `net8.0` 与 `net472` 运行，打印并人工核对）：
   1. `ConnectAsync` 在不可路由地址、`ConnectTimeout=500`：返回 `false`，耗时 450-2500 ms，调用返回时任务未完成（`IsCompleted == false`，调用耗时 < 100 ms）。
   2. `ConnectAsync(token)` 在 200 ms 后取消：抛出 `OperationCanceledException`，耗时 < 1500 ms。
   3. 服务端回复错误 TID：第一次请求 `ProtocolViolation`，`IsConnected == false`；`Reconnect=true` 时下一次请求成功且服务端看到第 2 个连接。
   4. 在途 `RequestAsync` 时 `Dispose()`：3 秒内返回 `ConnectionClosed`，服务端 `ClosedByPeerCount == 1`。
6. 把脚本输出粘贴到合并请求描述中。
7. 提交：`chore: bump version to 1.1.0`。

## 6. 未纳入本计划的条目（只记录，不修改）

- 审查第 6 条（RTU 异步读 `ReadAsync` 返回 0 时空转）。
- 审查第 7 条（RTU 取消吞掉 `OperationCanceledException`、`ErrorMessage` 含堆栈）。
- 审查第 8 条（解析器不校验响应功能码）：计划三 Task 3。
- 审查第 11 条（日志热路径 `ToHex()` 提前求值、RTU 重复 RX 日志）。
- 审查第 12 条（直接构造客户端时配置不校验）。
- 审查第 13、14 条（`Socket.Disconnect` 跨平台行为、`SerialStream.ReadAsync` 取消行为，均未验证）。
- TCP `CreateSocket()` 固定 `AddressFamily.InterNetwork`（IPv6 地址不支持）。
- `Disconnect()` 没有异步版本，请求在途时仍会同步等待 `requestLock`。
- 扩展方法返回 `ValueTask` 与接口返回 `Task` 不一致。
