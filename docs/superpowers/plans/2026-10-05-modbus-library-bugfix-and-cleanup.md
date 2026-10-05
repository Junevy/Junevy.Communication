# Junevy.Communication.Modbus 审查修复实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复代码审查发现的全部隐藏 Bug（高危/中等/次要），完成设计优化（错误契约统一、TCP/RTU 去重、真异步化）与命名规范化。

**Architecture:** 分五个阶段，每阶段结束时代码库可编译、测试全绿：Phase 1-3 按严重度修 Bug（TDD，先写复现测试）；Phase 4 做设计优化（结构化错误码替换字符串匹配 → 异常码枚举拆分 → 公共传输基类去重 → 真异步 → 模型职责收敛）；Phase 5 一次性做破坏性重命名（放最后，避免重命名被后续重构反复冲击）。

**Tech Stack:** C# / .NET（net472 + net8.0 多目标，Task 0.2 完成 net6.0 → net8.0 升级）、xUnit 2.5.3 + Moq 4.20.72、System.IO.Ports 8.0、Microsoft.Extensions.Logging.Abstractions 8.0。

**Spec:** 本计划实现 2026-10-05 代码审查报告的全部结论。审查报告结论已内化为下方「问题清单速查」，执行者无需其他文档。

## 关键决策（已于 2026-10-05 经用户确认，全部采纳）

| # | 决策 | 方案 | 备选（未采用） |
|---|------|------|------|
| D1 | 允许破坏性 API 变更 | **是**（v1.0.0 未发布 NuGet，内部 API 仍在演变） | 全部走兼容层 |
| D2 | TCP 事务 ID 管理方式 | **库内自增**：每次请求由客户端分配并回写 `request.TransactionId`，帧构建/解析按精确值匹配（对齐 NModbus 等主流实现） | 保留用户手动管理 |
| D3 | 真异步化范围 | **TCP 与 RTU 都做**（Task 4.4） | 先只做 TCP；或整体延后 |
| D4 | `ModbusRequest` 上的两个 Change 事件 | **移除**（DTO 不该有 UI 事件；WPF 示例改为仅由 `OnLengthChanged` 驱动 DataGrid 行数） | 保留事件 |
| D5 | 目标框架升级到 .NET 8 | **库与测试项目 net6.0 → net8.0，net472 保留**；6.0.0 时代的依赖包升到 8.0.0（WPF 示例已是 net8.0-windows，无需变更）——见 Task 0.2 | 连 net472 一并放弃 |

## 全局约束（Global Constraints）

- 多目标 `net472;net8.0`（Task 0.2 升级后）：不得使用 net472 上不可用的 API（如 `Task.WaitAsync`、`ArgumentNullException.ThrowIfNull` 等）；守卫一律用经典写法（`if (x == null) throw new ArgumentNullException(...)`）；确需新版 API 时用 `#if NET8_0_OR_GREATER` 条件编译，并为 net472 提供降级实现。
- 库项目不新增任何外部包依赖（测试项目维持现有 xUnit/Moq 集合；仅允许给库加 `Microsoft.NETFramework.ReferenceAssemblies`（PrivateAssets=all，仅构建期））。
- 保持同步 + 异步两套公开 API（`Request`/`RequestAsync`）。
- 每个 Task 结束：`dotnet build` 无警告级错误、`dotnet test` 全绿、独立 commit。
- 错误消息统一英文（现状如此），日志措辞改动必须同步更新所有依赖该文案的测试。
- WPF 示例项目（Junevy.Communication.Test）与 README 随 API 变更同步更新，任何时刻不得处于编译不过状态。

## Review Focus（规格未显式覆盖、最容易咬人的五类输入）

1. **TCP 半包超时后的残留字节**：读响应头一半后超时，socket 缓冲区留有残帧 → 期望：下一次请求读到的是新响应而不是残帧（实现：超时归类 `ErrorKind.Timeout` → 销毁连接重连）。由 Task 4.1 的测试固定。
2. **从站返回异常响应（FC|0x80）**：期望 `IsSuccess=false` 且错误消息含异常码（如 `0x02`）。由 Task 1.2 固定。
3. **同一个 `ModbusRequest` 对象连续两次请求**：期望两次都成功、线上 TID 依次为 0、1（自增）。由 Task 1.1 固定。
4. **并发 `GetOrAdd` 同名 key**：期望最终仅一个实例存活在册，输家实例被 `Dispose`。由 Task 2.2 固定。
5. **取消令牌在阻塞读取期间触发**（net8.0）：期望尽快返回 `Fail(Cancelled)`，而非干等满 `ReadTimeOut`。由 Task 4.4 固定（net472 上退化为"下一个 I/O 边界生效"，文档注明）。

## 问题清单速查（审查报告 → 任务映射）

| 编号 | 严重度 | 问题 | 位置 | 修复任务 |
|---|---|---|---|---|
| B1 | 高危 | Modbus 异常响应在裸 `IModbus.Request` 层 `IsSuccess=true` | TcpProtocolParser.cs:86-90、RtuProtocolParser.cs:147 | 1.2 |
| B2 | 高危 | TCP 事务 ID 上线值 = `TransactionId+1` 且从不自增/回写 | ModbusFrameBuilder.cs:97、TcpProtocolParser.cs:56 | 1.1 |
| B3 | 中 | 结果式 API 漏抛异常；同步/异步行为不一致 | ModbusTCP.cs:362-366、ModbusRTU.cs:286/475 | 2.1 |
| B4 | 中 | `GetOrAdd` 并发下 valueFactory 多次执行 → 实例泄漏 | ModbusConnectionManager.cs:80-84 | 2.2 |
| B5 | 中 | DI 注册与工厂内部两套 ConnectionManager | ModbusServiceCollectionExtensions.cs:38、ModbusFactory.cs:51/67 | 2.3 |
| B6 | 中 | README 示例编译不过（`Port` private set）、命名空间过期 | readme.md、ModbusTCPConfig.cs:9 | 2.4 |
| B7 | 次 | 库篡改调用者对象（`request.ProtocolType`、config 默认值改写） | ModbusTCP.cs:196、ModbusRTU.cs:169/401 | 3.1 |
| B8 | 次 | RTU 恰满 256 字节完整帧被误判溢出 | ModbusRTU.cs:275 | 3.2 |
| B9 | 次 | Diagnostics 校验阈值不一致（构建允许 1 字节数据、校验要求 ≥4） | ModbusExtensions.cs:471 vs ModbusHelper.cs:41 | 3.3 |
| B10 | 次 | `VerifyPort` 禁止 1024 以下非 502 端口，且与 SetPort 重复实现 | ModbusHelper.cs:127、ModbusTCPConfig.cs:40 | 2.4 |
| B11 | 次 | Connect/Disconnect 与 Request 无互斥 | ModbusTCP.cs:61-115、ModbusRTU.cs:64-137 | 3.4 |
| B12 | 次 | 死代码：ByteCount、AddCrc16、CrcBigEndian、InvokeOnProtocolTypeChanged、ModbusResult 注释块 | 各处 | 3.5 |

---

# Phase 1：高危 Bug 修复

### Task 1.1: 修复 TCP 事务 ID（库内自增 + 精确匹配）

**Files:**
- Modify: `Junevy.Communication.Modbus/Core/Framing/ModbusFrameBuilder.cs:97`（去掉 `+1`）
- Modify: `Junevy.Communication.Modbus/Core/Parsing/TcpProtocolParser.cs:56`（改为精确匹配）
- Modify: `Junevy.Communication.Modbus/TCP/ModbusTCP.cs`（新增自增计数器，在锁内回写 request）
- Modify: `Junevy.Communication.Modbus/Core/Models/ModbusRequest.cs:20-21`（修正 TransactionId XML 注释）
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces: `ModbusTCP/ModbusRTU` 每次成功进入重试循环前，在 `requestLock` 内执行 `request.TransactionId = transactionId++;`（ushort 自然回绕）；`TcpProtocolParser` 校验 `transactionId == request.TransactionId`（精确相等）；`ModbusFrameBuilder` 上线写入 `request.TransactionId` 原值。

- [ ] **Step 1: 写失败测试**（先改旧测试预期 + 新增自增测试）

在 `ModbusProtocolTests.cs` 中修改两处旧预期、新增一个测试：

```csharp
// 修改 FrameBuilder_TryWriteTcpRequestFrame_WritesWithoutCompatibilityArray 的断言：
// TransactionId = 0 时，线上第一个两字节现在是 0x00,0x00（原为 0x00,0x01）
Assert.Equal([0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x02, 0x03, 0x00, 0x10, 0x00, 0x02],
    destination[..written].ToArray());

// 修改 TcpParser_SlaveMismatch_ReturnsFailure 的响应帧首两字节（TID 与 request.TransactionId=0 精确一致）：
byte[] response = [0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x02, 0x03, 0x02, 0x12, 0x34];

// 修改 TcpRequest_AutoConnectsWhenReconnectEnabled 服务端 response 首两字节为 [0x00, 0x00]。

// 新增测试：
[Fact]
public void TcpParser_TransactionIdMustMatchExactly()
{
    var request = new ModbusRequest
    {
        TransactionId = 7,
        SlaveId = 1,
        FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
        Start = 0,
        Length = 1
    };
    // 旧约定下 (7+1) 会通过；精确匹配下必须失败
    byte[] response = [0x00, 0x08, 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34];

    var result = new TcpProtocolParser().ParseResponse(response, request);

    Assert.False(result.IsSuccess);
    Assert.Contains("Transaction ID", result.ErrorMessage);
}

[Fact]
public async Task TcpClient_AssignsSequentialTransactionIds()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var seenTids = new List<byte[]>();
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        for (int i = 0; i < 2; i++)
        {
            var req = new byte[12];
            int read = 0;
            while (read < req.Length)
                read += await stream.ReadAsync(req, read, req.Length - read);
            seenTids.Add(req[..2].ToArray());
            byte[] resp = [req[0], req[1], 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34];
            await stream.WriteAsync(resp, 0, resp.Length);
        }
    });

    using var tcp = new ModbusTCP(new ModbusTCPConfig
    {
        Address = "127.0.0.1",
        ReadTimeOut = 1000,
        WriteTimeOut = 1000,
        ConnectTimeout = 1000,
        Reconnect = true
    });
    tcp.Config.SetPort(port);

    var request = new ModbusRequest { SlaveId = 1, FunctionCode = ModbusFunctionCode.ReadHoldingRegisters, Start = 0, Length = 1 };
    var r1 = tcp.Request(request);
    var r2 = tcp.Request(request);
    listener.Stop();
    await server;

    Assert.True(r1.IsSuccess, r1.ErrorMessage);
    Assert.True(r2.IsSuccess, r2.ErrorMessage);
    Assert.Equal(new byte[] { 0x00, 0x00 }, seenTids[0]);
    Assert.Equal(new byte[] { 0x00, 0x01 }, seenTids[1]);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~TcpParser_TransactionIdMustMatchExactly|FullyQualifiedName~TcpClient_AssignsSequentialTransactionIds|FullyQualifiedName~FrameBuilder_TryWriteTcpRequestFrame|FullyQualifiedName~TcpRequest_AutoConnects"`
Expected: 新旧相关用例 FAIL（当前实现写/校验 `+1`）

- [ ] **Step 3: 实现**

`ModbusFrameBuilder.cs:97`：
```csharp
// 修改前
ushort transactionId = (ushort)(request.TransactionId + 1);
// 修改后
ushort transactionId = request.TransactionId;
```

`TcpProtocolParser.cs:56`：
```csharp
// 修改前
ushort expectedTransactionId = (ushort)(request.TransactionId + 1);
// 修改后
ushort expectedTransactionId = request.TransactionId;
```
（同函数内两处引用 `expectedTransactionId` 的日志/比较保持变量名不变。）

`ModbusRequest.cs` XML 注释：
```csharp
/// <summary>
/// TCP 事务标识。由 ModbusTCP 客户端在每次请求时自动分配（从 0 递增、回绕），
/// 用于响应匹配；手动赋值仅对裸帧构建 API（ModbusHelper.BuildRequestFrame）生效。
/// </summary>
public ushort TransactionId { get; set; } = 0x0000;
```

`ModbusTCP.cs`：新增私有字段 `private ushort transactionId;`；在 `Request` 与 `RequestAsync` 中、`requestLock.Wait()` 成功后、调用 `ExecuteRequestWithRetry(Async)` 之前加入：
```csharp
request.TransactionId = transactionId++;
```

- [ ] **Step 4: 运行全部测试确认通过**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(tcp): manage transaction id in-library (auto-increment, exact match)"
```

---

### Task 1.2: Modbus 异常响应在解析器层返回 Fail

**Files:**
- Modify: `Junevy.Communication.Modbus/Core/Parsing/TcpProtocolParser.cs:86-90`
- Modify: `Junevy.Communication.Modbus/Core/Parsing/RtuProtocolParser.cs:144-148`
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces: 解析器对异常响应返回 `Fail("Modbus exception response. Function=0x{f:X2}, Code=0x{c:X2}.", 原始帧切片)`；`Data` 仍携带完整原始帧（调用方需要时可自行解读）。扩展层 `NormalizeRawResult` 的二次检查保留（纵深防御）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void TcpParser_ExceptionResponse_ReturnsFailureWithCode()
{
    var request = new ModbusRequest
    {
        TransactionId = 0,
        SlaveId = 1,
        FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
        Start = 0,
        Length = 1
    };
    byte[] response = [0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x01, 0x83, 0x02];

    var result = new TcpProtocolParser().ParseResponse(response, request);

    Assert.False(result.IsSuccess);
    Assert.Contains("0x02", result.ErrorMessage);
    Assert.Equal(9, result.Data.Length);
}

[Fact]
public void RtuParser_ExceptionResponse_ReturnsFailureWithCode()
{
    var request = new ModbusRequest
    {
        SlaveId = 1,
        FunctionCode = ModbusFunctionCode.ReadHoldingRegisters
    };
    byte[] response = [0x01, 0x83, 0x02, 0x00, 0x00];
    var crc = Crc16Helper.CrcLittleEndian(response.AsSpan(0, 3));
    response[3] = crc[0];
    response[4] = crc[1];

    var result = new RtuProtocolParser().ParseResponse(response, request);

    Assert.False(result.IsSuccess);
    Assert.Contains("0x02", result.ErrorMessage);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~ExceptionResponse"`
Expected: 两个用例 FAIL（当前返回 Success）

- [ ] **Step 3: 实现**

`TcpProtocolParser.ParseResponse` 异常分支（原 86-90 行）：
```csharp
// Exception response
if (funcCode == (byte)((byte)request.FunctionCode | 0x80))
{
    if (totalLength < TcpPduOffset + 3)
    {
        logger.LogWarning(" [TcpParser] Exception response too short: {Length} bytes.", totalLength);
        return ModbusResult<ReadOnlyMemory<byte>>.Fail(" [TcpParser] Exception response too short.", response);
    }

    byte exceptionCode = span[TcpPduOffset + 2];
    logger.LogWarning(" [TcpParser] Modbus exception. Function=0x{Function:X2}, Code=0x{Code:X2}.", funcCode, exceptionCode);
    return ModbusResult<ReadOnlyMemory<byte>>.Fail(
        $"Modbus exception response. Function=0x{funcCode:X2}, Code=0x{exceptionCode:X2}.",
        response.Slice(0, totalLength));
}
```

`RtuProtocolParser.HandleRtuException`（原 143-148 行）CRC 通过分支：
```csharp
var candidate = span.Slice(0, exceptionLength);
if (Crc16Helper.VerifyCrc(candidate))
{
    logger.Rx("SerialPort", candidate, stopwatch, ref lastTimestamp);
    byte exceptionCode = span[2];
    return (ModbusResult<ReadOnlyMemory<byte>>.Fail(
        $"Modbus exception response. Function=0x{span[1]:X2}, Code=0x{exceptionCode:X2}.",
        response.Slice(0, exceptionLength)), false);
}
```

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS（`TcpRequest_AutoConnectsWhenReconnectEnabled` 等既有用例不受影响）

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(parser): surface modbus exception responses as failed results"
```

---

# Phase 2：中等 Bug 修复

### Task 2.1: 消除结果式 API 的异常逃逸（同步/异步行为对齐）

**Files:**
- Modify: `Junevy.Communication.Modbus/TCP/ModbusTCP.cs:362-366`（`Read` 内非法 PDU 长度改为返回 Fail）
- Modify: `Junevy.Communication.Modbus/RTU/ModbusRTU.cs:283-287`（同步 `Read` 零长解析帧改为返回 Fail）
- Modify: `Junevy.Communication.Modbus/RTU/ModbusRTU.cs:472-476`（异步 `ReadAsync` 零长解析帧改为返回 Fail，与同步一致）
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces: `IModbus.Request/RequestAsync` 除参数校验与 `ObjectDisposedException` 外不再抛异常；协议违规一律 `Fail` 返回。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task TcpClient_InvalidPduLength_ReturnsFailureInsteadOfThrowing()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var req = new byte[12];
        int read = 0;
        while (read < req.Length)
            read += await stream.ReadAsync(req, read, req.Length - read);
        // MBAP length 字段 = 0：协议违规
        byte[] resp = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        await stream.WriteAsync(resp, 0, resp.Length);
        await Task.Delay(200); // 给客户端留出读取时间
    });

    using var tcp = new ModbusTCP(new ModbusTCPConfig
    {
        Address = "127.0.0.1",
        ReadTimeOut = 1000,
        WriteTimeOut = 1000,
        ConnectTimeout = 1000,
        Reconnect = true,
        RetryCount = 0   // 单次尝试，保证最终错误消息就是"PDU length"而不是后续超时
    });
    tcp.Config.SetPort(port);

    ModbusResult<byte[]> result = null!;
    var ex = await Record.ExceptionAsync(() =>
    {
        result = tcp.Request(new ModbusRequest
        {
            SlaveId = 1,
            FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
            Start = 0,
            Length = 1
        });
        return Task.CompletedTask;
    });
    listener.Stop();
    await server;

    Assert.Null(ex);                       // 修复前：这里会捕获到 ModbusException
    Assert.False(result.IsSuccess);
    Assert.Contains("PDU length", result.ErrorMessage);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~InvalidPduLength"`
Expected: FAIL（`ex` 为 ModbusException）

- [ ] **Step 3: 实现**

`ModbusTCP.Read`（原 362-366 行）：
```csharp
if (pduLength < 1 || pduLength > 254)
{
    logger.LogError(" [Read] Invalid PDU length: {PduLength}.", pduLength);
    return ModbusResult<byte[]>.Fail($" [Read] Invalid PDU length: {pduLength}.");
}
```

`ModbusRTU.Read`（原 283-287 行）与 `ModbusRTU.ReadAsync`（原 472-476 行）统一为：
```csharp
if (parseResult.Data.Length <= 0)
{
    logger.LogWarning(" [Read] Parsed frame has zero length.");
    return ModbusResult<byte[]>.Fail(" [Read] Parsed frame has zero length.");
}
```
（ReadAsync 版本日志前缀用 `[ReadAsync]`，消息文本保持一致。）

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(transport): return protocol violations as failed results, align sync/async"
```

---

### Task 2.2: 修复 `ModbusConnectionManager.GetOrAdd` 并发实例泄漏

**Files:**
- Modify: `Junevy.Communication.Modbus/Factory/ModbusConnectionManager.cs:70-89`
- Test: `Junevy.Communication.Modbus.Tests/ModbusFactoryTests.cs`

**Interfaces:**
- Produces: `GetOrAdd` 语义不变；并发竞态下输家创建的实例会被立即 `Dispose`，字典中始终只有赢家一个实例。

- [ ] **Step 1: 写失败测试**（添加到 `ModbusFactoryTests.cs`）

```csharp
[Fact]
public void GetOrAdd_ConcurrentRace_DisposesLosingInstances()
{
    var manager = new ModbusConnectionManager();
    var created = new List<IModbus>();
    var sync = new object();

    var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        manager.GetOrAdd("race", key =>
        {
            var mock = new Mock<IModbus>();
            lock (sync) { created.Add(mock.Object); }
            return mock.Object;
        }))).ToArray();

    Task.WaitAll(tasks);
    var winner = tasks[0].Result;

    Assert.All(tasks, t => Assert.Same(winner, t.Result));
    foreach (var instance in created)
    {
        if (!ReferenceEquals(instance, winner))
            Mock.Get(instance).Verify(m => m.Dispose(), Times.Once);
    }
    Assert.Equal(1, manager.Count);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~GetOrAdd_ConcurrentRace"`
Expected: FAIL（当前 `ConcurrentDictionary.GetOrAdd` valueFactory 竞态下会创建多个实例且输家不被 Dispose；若碰巧单次创建则偶发通过——用 16 并发 + 重复运行 3 次验证稳定性）

- [ ] **Step 3: 实现**（替换 `GetOrAdd` 方法体）

```csharp
public IModbus GetOrAdd(string key, Func<string, IModbus> factory)
{
    ThrowIfDisposed();
    if (factory == null)
        throw new ArgumentNullException(nameof(factory));
    if (string.IsNullOrEmpty(key))
        throw new ArgumentException("Key must not be null or empty.", nameof(key));

    // ConcurrentDictionary.GetOrAdd 的 valueFactory 在竞态下可能执行多次，
    // 会创建多份连接实例且除赢家外全部泄漏 —— 改为 TryGetValue/TryAdd 自旋，
    // 输家立即释放自己创建的实例。
    while (true)
    {
        if (entries.TryGetValue(key, out var existing))
        {
            return ResolveFromEntry(key, existing) ?? throw new InvalidOperationException(
                $"Failed to resolve Modbus instance for key '{key}'.");
        }

        var instance = factory(key);
        if (entries.TryAdd(key, new Entry(instance)))
            return instance;

        instance.Dispose();
        logger.LogWarning(" [GetOrAdd] Lost creation race for '{Key}', disposed duplicate instance.", key);
    }
}
```

- [ ] **Step 4: 运行全部测试**（重复 3 次验证并发稳定性）

Run: `dotnet test Junevy.Communication.Modbus.Tests`（执行 3 遍）
Expected: 全部 PASS ×3（包括既有 `ConcurrentAddRemove_IsThreadSafe`）

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(factory): dispose duplicate instances lost in GetOrAdd creation race"
```

---

### Task 2.3: DI 注册与工厂共用同一个 ConnectionManager

**Files:**
- Modify: `Junevy.Communication.Modbus/Factory/ModbusFactory.cs:71-77`（6 参构造 `internal` → `public`）
- Test: `Junevy.Communication.Modbus.Tests/ModbusFactoryTests.cs`

**Interfaces:**
- Produces: `ModbusFactory` 新公开构造 `ModbusFactory(ILogger<ModbusFactory>, ILoggerFactory, TcpProtocolParser?, RtuProtocolParser?, IModbusFrameBuilder?, IModbusConnectionManager?)`；DI 容器中 `IModbusConnectionManager` 与 `IModbusFactory` 共享同一注册表。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void AddModbusFactory_FactoryAndConnectionManagerShareRegistry()
{
    var services = new ServiceCollection();
    services.AddModbusFactory();
    using var provider = services.BuildServiceProvider();

    var factory = provider.GetRequiredService<IModbusFactory>();
    var manager = provider.GetRequiredService<IModbusConnectionManager>();

    factory.TryAdd("shared", new ModbusTCPConfig(), out _);

    Assert.True(manager.TryGet("shared", out var resolved));
    Assert.NotNull(resolved);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~ShareRegistry"`
Expected: FAIL（DI 解析出的 manager 是独立空实例）

- [ ] **Step 3: 实现**

`ModbusFactory.cs` 将内部 6 参构造改为 public（其余不变）：
```csharp
public ModbusFactory(
    ILogger<ModbusFactory> logger,
    ILoggerFactory loggerFactory,
    TcpProtocolParser? tcpParser,
    RtuProtocolParser? rtuParser,
    IModbusFrameBuilder? frameBuilder,
    IModbusConnectionManager? manager)
```
（Microsoft DI 自动选择可满足的参数最多构造，注册不变；`manager` 参数缺省时内部仍兜底 `new ModbusConnectionManager(...)`。）

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(di): share one connection manager between factory and container"
```

---

### Task 2.4: `Port` 改为可写属性、放宽端口校验、修 README

**Files:**
- Modify: `Junevy.Communication.Modbus/TCP/ModbusTCPConfig.cs`（删除 `SetPort`，`Port` 改 `{ get; set; }`）
- Modify: `Junevy.Communication.Modbus/Utils/ModbusHelper.cs:127`（`VerifyPort` 放宽为 1..65535）
- Modify: `Junevy.Communication.Modbus/Factory/ModbusFactory.cs:221-222`（`SetPort(502)` → `config.Port = 502`）
- Modify: `readme.md`（命名空间 `Communication.Modbus.*` → `Junevy.Communication.Modbus.*`；`Port = 502` 现在合法；`RepositoryUrl` 占位符从 `git remote get-url origin` 取值）
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces: `ModbusTCPConfig.Port` 为普通可写 int 属性（默认 502）；`ModbusHelper.VerifyPort(int)` 接受 1..65535；`SetPort` 删除。

- [ ] **Step 1: 改测试**（替换 `TcpConfig_SetPort_AllowsWellKnownModbusPort`）

```csharp
[Fact]
public void TcpConfig_PortIsDirectlySettable()
{
    var config = new ModbusTCPConfig { Port = 1502 };
    Assert.Equal(1502, config.Port);
}

[Fact]
public void VerifyPort_AcceptsAnyValidPort()
{
    Assert.True(ModbusHelper.VerifyPort(1));
    Assert.True(ModbusHelper.VerifyPort(502));
    Assert.True(ModbusHelper.VerifyPort(65535));
    Assert.False(ModbusHelper.VerifyPort(0));
    Assert.False(ModbusHelper.VerifyPort(65536));
}
```
同时把 `TcpRequest_AutoConnectsWhenReconnectEnabled` 与 `TcpClient_AssignsSequentialTransactionIds` 中的 `config.SetPort(port)` 改为 `tcp.Config.Port = port;`。

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build Junevy.Communication.Modbus.Tests && dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~PortIsDirectlySettable|FullyQualifiedName~VerifyPort_AcceptsAnyValidPort"`
Expected: 编译失败（`Port` 不可写、`SetPort` 尚在）

- [ ] **Step 3: 实现**

`ModbusTCPConfig.cs`：
```csharp
/// <summary>TCP 端口（1..65535，Modbus 标准端口 502）。</summary>
public int Port { get; set; } = 502;
```
（删除 `SetPort` 方法与 `private set`。）

`ModbusHelper.cs`：
```csharp
public static bool VerifyPort(int port) => port is >= 1 and <= 65535;
```

`ModbusFactory.ValidateAndFillDefaults` 中 `config.SetPort(502)` → `config.Port = 502`。

README 全文替换命名空间（`using Communication.Modbus.Extensions;` → `using Junevy.Communication.Modbus.Extensions;` 等 4 处 using），执行 `git remote get-url origin` 将结果写入 `RepositoryUrl`（若 remote 为空则写入 `https://github.com/Junevy/Junevy.Communication` 并在提交信息中注明待定）。

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(config): make Port a plain settable property, accept any valid port, fix README"
```

---

# Phase 3：次要 Bug 修复

### Task 3.1: 帧构建协议显式化——库不再篡改调用者的 `ModbusRequest`

**Files:**
- Modify: `Junevy.Communication.Modbus/Core/Interfaces/IModbusFrameBuilder.cs`（方法增加 `ModbusProtocolType protocolType` 参数）
- Modify: `Junevy.Communication.Modbus/Core/Framing/ModbusFrameBuilder.cs`（内部改用参数，不再读 `request.ProtocolType`）
- Modify: `Junevy.Communication.Modbus/Utils/ModbusHelper.cs:67-68`（兼容入口传 `request.ProtocolType`）
- Modify: `Junevy.Communication.Modbus/TCP/ModbusTCP.cs:196`、`Junevy.Communication.Modbus/RTU/ModbusRTU.cs:169/401`（删除 `request.ProtocolType = ProtocolType;`，改传自身协议）
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces（最终签名，后续任务一律按此调用）:
```csharp
public interface IModbusFrameBuilder
{
    int GetRequestFrameLength(ModbusRequest request, ModbusProtocolType protocolType);
    bool TryWriteRequestFrame(ModbusRequest request, ModbusProtocolType protocolType, Span<byte> destination, out int bytesWritten);
    byte[] BuildRequestFrame(ModbusRequest request, ModbusProtocolType protocolType);
}
```
- `ModbusHelper.BuildRequestFrame(request)` 保持签名不变，内部以 `request.ProtocolType` 调用（兼容语义：用户显式设置的协议生效，库不再改写）。
- `ModbusRequest.ProtocolType` 退化为"裸帧构建 API 的元数据"，XML 注释注明客户端不再读写它。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void FrameBuilder_ProtocolComesFromArgumentNotRequest()
{
    var request = new ModbusRequest
    {
        ProtocolType = ModbusProtocolType.RTU,   // 故意设错
        SlaveId = 2,
        FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
        Start = 0x0010,
        Length = 2
    };
    var builder = new ModbusFrameBuilder();
    Span<byte> destination = stackalloc byte[ModbusFrameBuilder.MaxTcpAduLength];

    Assert.True(builder.TryWriteRequestFrame(request, ModbusProtocolType.TCP, destination, out int written));
    Assert.Equal(12, written);
    // TCP 帧应有 MBAP 头（前 6 字节非从站 ID 开头）
    Assert.Equal(0x00, destination[2]); // protocol id hi

    // 且 request 对象未被修改
    Assert.Equal(ModbusProtocolType.RTU, request.ProtocolType);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build Junevy.Communication.Modbus.Tests`
Expected: 编译失败（接口无两参重载）

- [ ] **Step 3: 实现**

- `IModbusFrameBuilder` 三个方法签名按上文修改。
- `ModbusFrameBuilder` 内部所有 `request.ProtocolType switch` 改为 `protocolType switch`；`GetRequestFrameLength` 的 null/有效性检查保留。
- `ModbusHelper.BuildRequestFrame`：`=> FrameBuilder.BuildRequestFrame(request, request.ProtocolType);`
- `ModbusTCP.Send`：删除 `request.ProtocolType = ProtocolType;`，调用改为
  `frameBuilder.TryWriteRequestFrame(request, ProtocolType, frame, out int bytesWritten)`。
- `ModbusRTU.Send` / `ModbusRTU.SendAsync`：同样删除赋值行，调用改为
  `frameBuilder.TryWriteRequestFrame(request, ProtocolType, requestFrame, out int bytesWritten)`；
  `SendAsync` 中 `frameBuilder.GetRequestFrameLength(request)` → `frameBuilder.GetRequestFrameLength(request, ProtocolType)`。
- 同步更新直接调用 builder 的既有测试：`FrameBuilder_TryWriteTcpRequestFrame_WritesWithoutCompatibilityArray`
  改为 `builder.TryWriteRequestFrame(request, ModbusProtocolType.TCP, destination, out int written)`。
- `ModbusRequest.ProtocolType` XML 注释追加："传输层以客户端自身协议为准，本属性仅由裸帧构建 API 读取，库不会修改它。"

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS（既有帧构建测试用 `ModbusHelper.BuildRequestFrame`，签名未变）

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(framing): make protocol an explicit argument, stop mutating caller requests"
```

---

### Task 3.2: RTU 接收缓冲上界修正（256 → 257）

**Files:**
- Modify: `Junevy.Communication.Modbus/RTU/ModbusRTU.cs:253`、`ModbusRTU.cs:442`（两处 `Rent(256)`）
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces: `ModbusRTU` 的接收缓冲可容纳完整 256 字节 RTU ADU（缓冲上界 = `ModbusFrameBuilder.MaxRtuAduLength + 1`，溢出判断 `>=` 语义不变）。

- [ ] **Step 1: 写失败测试**（纯逻辑上无法脱离串口直测私有缓冲大小；以常量+结构约束固定：）

```csharp
[Fact]
public void RtuFrameBuilder_MaxRtuAduLength_Is256()
{
    // 缓冲必须比最大 ADU 大 1 字节，使"读满 256 字节完整帧"不会触发溢出分支
    Assert.Equal(256, ModbusFrameBuilder.MaxRtuAduLength);
}
```
该测试现在即通过（守卫常量）；真正的验证是 Step 3 的实现改动 + 人工冒烟（执行者备注：无串口环境下无法自动化，信任实现审查）。

- [ ] **Step 2: 运行测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~MaxRtuAduLength"`
Expected: PASS（守卫测试）

- [ ] **Step 3: 实现**

`ModbusRTU.Read` 与 `ModbusRTU.ReadAsync` 中：
```csharp
// 修改前
var pool = System.Buffers.ArrayPool<byte>.Shared.Rent(256);
// 修改后（Rent 至少 MaxRtuAduLength + 1，保证 256 字节完整帧不会命中溢出分支）
var pool = System.Buffers.ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxRtuAduLength + 1);
```

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(rtu): accept full 256-byte ADU by renting one extra buffer byte"
```

---

### Task 3.3: Diagnostics 校验阈值对齐

**Files:**
- Modify: `Junevy.Communication.Modbus/Utils/ModbusHelper.cs:40-41`（`Data.Length >= 4` → `>= 3`）
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces: `Diagnostics(slaveId, subFunction, byte[] data)` 允许 `data.Length` 为 1..250（构建端 `BuildDiagnosticsData` 与校验端 `CheckRequest` 规则一致：合成 payload = sub(2)+data = 3..252 字节）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void CheckRequest_Diagnostics_OneByteData_IsValid()
{
    var request = new ModbusRequest
    {
        ProtocolType = ModbusProtocolType.RTU,
        SlaveId = 1,
        FunctionCode = ModbusFunctionCode.Diagnostics,
        Data = [0x00, 0x00, 0xA5]   // sub=0x0000 + 1 字节数据
    };
    Assert.True(ModbusHelper.CheckRequest(request));
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~Diagnostics_OneByteData"`
Expected: FAIL

- [ ] **Step 3: 实现**

`ModbusHelper.CheckRequest` 的 Diagnostics 分支：
```csharp
ModbusFunctionCode.Diagnostics =>
    request.Data is not null && request.Data.Length >= 3 && request.Data.Length <= 252,
```

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(helper): align diagnostics request validation with builder (3..252 bytes)"
```

---

### Task 3.4: Connect/Disconnect 与请求互斥

**Files:**
- Modify: `Junevy.Connunication.Modbus/TCP/ModbusTCP.cs`（`Connect`/`Disconnect` 提取 `ConnectCore`/`DisconnectCore` 并在公开方法中持有 `requestLock`；`EnsureConnected` 改调 `ConnectCore`）
- Modify: `Junevy.Communication.Modbus/RTU/ModbusRTU.cs`（同构改造）
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces: 公开 `Connect()`/`ConnectAsync()`/`Disconnect()` 内部获取 `requestLock`（与 Request 串行化）；内部重连路径（`EnsureConnected` → `ConnectCore`）不再加锁（避免重入死锁）。注意：**同步锁不可重入**，`ExecuteRequestWithRetry` 已持锁，重连必须走无锁核心方法。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task TcpClient_ConcurrentConnectAndRequest_DoNotInterleave()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        while (listener.Pending())
        {
            var client = await listener.AcceptTcpClientAsync();
            // 保持连接打开；响应逻辑省略，客户端侧只验证无异常
        }
    });

    using var tcp = new ModbusTCP(new ModbusTCPConfig
    {
        Address = "127.0.0.1",
        ReadTimeOut = 200,
        WriteTimeOut = 200,
        ConnectTimeout = 500
    });
    tcp.Config.Port = port;   // Task 2.4 之后 Port 为可写属性

    var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
    {
        var ex = await Record.ExceptionAsync(() =>
        {
            if (i % 2 == 0) { tcp.Connect(); return Task.CompletedTask; }
            return tcp.RequestAsync(new ModbusRequest
            {
                SlaveId = 1,
                FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                Start = 0,
                Length = 1
            });
        });
        // 修复前：ResetSocket 可能在发送/读取中销毁 socket，抛出未归类的 ObjectDisposedException
        Assert.True(ex is null or IOException or SocketException or TimeoutException
            or OperationCanceledException or ModbusException,
            $"Unexpected exception: {ex}");
    })).ToArray();

    await Task.WhenAll(tasks);
    listener.Stop();
}
```

- [ ] **Step 2: 运行观察**

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~ConcurrentConnectAndRequest"`
Expected: 修复前可能偶发 FAIL（竞态）；此测试同时充当修复后的回归守卫

- [ ] **Step 3: 实现**（TCP 侧；RTU 同构）

```csharp
public bool Connect()
{
    ThrowIfDisposed();
    requestLock.Wait();
    try { return ConnectCore(); }
    finally { requestLock.Release(); }
}

public void Disconnect()
{
    requestLock.Wait();
    try { DisconnectCore(); }
    finally { requestLock.Release(); }
}

private bool ConnectCore()
{
    // 原 Connect 方法体（ResetSocket + BeginConnect/EndConnect 逻辑）原样移入
}

private void DisconnectCore()
{
    // 原 Disconnect 方法体原样移入
}

private bool EnsureConnected()
{
    if (IsConnected) return true;
    if (!Config.Reconnect) return false;
    logger.LogInformation(" [Reconnect] TCP connection is not available. Reconnecting to {Address}:{Port}.", Config.Address, Config.Port);
    try { return ConnectCore(); }               // 已在请求锁内，走无锁核心
    catch (Exception ex) when (IsCommunicationException(ex))
    {
        logger.LogWarning(ex, " [Reconnect] TCP reconnect failed.");
        return false;
    }
}
```
`EnsureConnectedAsync` 改调 `Task.Run(ConnectCore)`（在请求锁内，不可再走公开 `Connect`）。`ModbusRTU` 做同构拆分（`ConnectCore` = Close-if-open + ConfigurePort + Open）。

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "fix(transport): serialize connect/disconnect with requests via requestLock"
```

---

### Task 3.5: 死代码清理

**Files:**
- Modify: `Junevy.Communication.Modbus/Core/Models/ModbusRequest.cs:34`（删除 `ByteCount`）、`:72-80`（删除 `InvokeOnProtocolTypeChanged`；`InvokeOnFunctionCodeChanged` 随 Task 4.5 一并删除，此处保留）
- Modify: `Junevy.Communication.Modbus/Utils/Crc16Helper.cs:24-27, 57-73`（删除 `AddCrc16`、`CrcLittleEndian(byte[])` 之外的重复重载视引用而定、`CrcBigEndian`）
- Modify: `Junevy.Communication.Modbus/Core/Models/ModbusResult.cs:13, 43-44`（删除注释掉的 `IsException`/`Exception` 块）

**Interfaces:**
- Produces: 无新接口；纯删除。删除前执行 `grep -rn "<成员名>" --include="*.cs" .` 确认零引用（当前审查已确认）。

- [ ] **Step 1: 删除成员**（按下表逐项 grep 确认零引用后删除）

| 成员 | 位置 |
|---|---|
| `ModbusRequest.ByteCount` | ModbusRequest.cs:34 |
| `Crc16Helper.AddCrc16` | Crc16Helper.cs:24-27 |
| `Crc16Helper.CrcBigEndian` | Crc16Helper.cs:69-73 |
| `Crc16Helper.CrcLittleEndian(byte[])`（byte[] 重载，Span 重载被测试使用） | Crc16Helper.cs:57-61 |
| `ModbusRequest.InvokeOnProtocolTypeChanged` | ModbusRequest.cs:77-80 |
| `ModbusResult` 注释掉的 `IsException`/`Exception` | ModbusResult.cs:13, 43-44 |

- [ ] **Step 2: 全量编译 + 测试**

Run: `dotnet build Junevy.Communication.Modbus.Tests && dotnet test Junevy.Communication.Modbus.Tests`
Expected: 编译无错、全部 PASS

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "chore: remove dead code (ByteCount, unused CRC helpers, event invoker, commented blocks)"
```

---

# Phase 4：设计优化

### Task 4.1: 结构化错误分类 `ModbusErrorKind`，替换字符串匹配重连判断

**Files:**
- Modify: `Junevy.Communication.Modbus/Core/Models/ModbusResult.cs`（新增 `ErrorKind` 属性与 `Fail` 重载）
- Create: `Junevy.Communication.Modbus/Core/Models/ModbusErrorKind.cs`
- Modify: `Junevy.Communication.Modbus/TCP/ModbusTCP.cs`（所有 `Fail` 调用点归类；`ShouldReconnectAfterFailure` 改查 `ErrorKind`）
- Modify: `Junevy.Communication.Modbus/RTU/ModbusRTU.cs`（所有 `Fail` 调用点归类）
- Modify: `Junevy.Communication.Modbus/Extensions/ModbusExtensions.cs`（`NormalizeRawResult` 转发 ErrorKind）
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces:
```csharp
namespace Junevy.Communication.Modbus.Core.Models;

/// <summary>请求失败的机器可读分类，供调用方做重连/重试/告警决策。</summary>
public enum ModbusErrorKind
{
    /// <summary>成功（未失败）。</summary>
    None = 0,
    /// <summary>未分类失败（历史兼容默认值）。</summary>
    Unspecified = 1,
    /// <summary>本地请求校验失败。</summary>
    InvalidRequest = 2,
    /// <summary>连接已断开/发送失败。</summary>
    ConnectionClosed = 3,
    /// <summary>超时（连接/读/写）。</summary>
    Timeout = 4,
    /// <summary>帧格式/CRC/长度违规。</summary>
    ProtocolViolation = 5,
    /// <summary>从站返回 Modbus 异常。</summary>
    ModbusException = 6,
    /// <summary>操作被取消。</summary>
    Cancelled = 7,
}
```
- `ModbusResult<T>` 增加 `public ModbusErrorKind ErrorKind { get; set; }`；`Fail` 增加 `kind` 参数：`public static ModbusResult<T> Fail(string errMsg, ModbusErrorKind kind = ModbusErrorKind.Unspecified, T? data = default)`。原两参 `Fail(string, T? data)` 保留重载（`data` 语义不变），避免一次性改动全部调用点。
- `ModbusTCP`：`ShouldReconnectAfterFailure(ModbusResult<byte[]> result)` 改为 `result.ErrorKind is ModbusErrorKind.Timeout or ModbusErrorKind.ConnectionClosed`；调用点 `MarkConnectionFaulted` 逻辑不变。
- 失败归类表（ModbusTCP）：send 失败/远端关闭 → `ConnectionClosed`；三处 timeout 消息 → `Timeout`；"Invalid PDU length" → `ProtocolViolation`；cancelled → `Cancelled`；"Invalid request" → `InvalidRequest`；其余保持 `Unspecified`。RTU 同表执行（Send 失败 → `ConnectionClosed`；读超时 → `Timeout`；其余 `Unspecified`）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void ModbusResult_Fail_DefaultsToUnspecifiedKind()
{
    var result = ModbusResult<byte[]>.Fail("boom");
    Assert.Equal(ModbusErrorKind.Unspecified, result.ErrorKind);
}

[Fact]
public void ModbusResult_Fail_CanCarryKind()
{
    var result = ModbusResult<byte[]>.Fail("timeout", ModbusErrorKind.Timeout);
    Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
}

[Fact]
public async Task TcpClient_ReadTimeout_IsClassifiedAsTimeout()
{
    // 监听但不回复 → 客户端读超时
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var tcp = new ModbusTCP(new ModbusTCPConfig
    {
        Address = "127.0.0.1",
        ReadTimeOut = 300,
        WriteTimeOut = 300,
        ConnectTimeout = 1000,
        RetryCount = 0   // 单次尝试：否则重连失败后最终 ErrorKind 会变成 ConnectionClosed 而非 Timeout
    });
    tcp.Config.Port = port;
    Assert.True(tcp.Connect());

    var result = tcp.Request(new ModbusRequest
    {
        SlaveId = 1,
        FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
        Start = 0,
        Length = 1
    });
    listener.Stop();

    Assert.False(result.IsSuccess);
    Assert.Equal(ModbusErrorKind.Timeout, result.ErrorKind);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build Junevy.Communication.Modbus.Tests && dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~ErrorKind|FullyQualifiedName~ClassifiedAsTimeout"`
Expected: 编译失败（无 ErrorKind/枚举）

- [ ] **Step 3: 实现**

- 新建 `ModbusErrorKind.cs`（上文枚举）。
- `ModbusResult<T>`：加属性与新 `Fail` 重载：
```csharp
public ModbusErrorKind ErrorKind { get; set; }

public static ModbusResult<T> Fail(string errMsg, ModbusErrorKind kind, T? data = default)
    => new() { IsSuccess = false, ErrorMessage = errMsg, ErrorKind = kind, Data = data };
```
- `ModbusTCP`：`ExecuteRequestWithRetry(Async)` 中各 `Fail` 调用按归类表补 kind 参数（如 `Fail(" [Request] Send failed.", ModbusErrorKind.ConnectionClosed)`）；`Read` 中三处超时/关闭 Fail 补 kind；`ShouldReconnectAfterFailure` 重写：
```csharp
private static bool ShouldReconnectAfterFailure(ModbusResult<byte[]> result)
    => result.ErrorKind is ModbusErrorKind.Timeout or ModbusErrorKind.ConnectionClosed;
```
- `ModbusRTU`：同样为各 Fail 补 kind（读超时 → Timeout，Send 失败 → ConnectionClosed，取消 → Cancelled）。
- `ModbusExtensions.NormalizeRawResult`：失败转发 `ErrorKind`（`Fail(msg, result.ErrorKind, result.Data)`）；`pdu[1]&0x80` 分支的 Fail 使用 `ModbusErrorKind.ModbusException`。
- 两个解析器中 Task 1.2 引入的"Modbus exception response"Fail 补 `ModbusErrorKind.ModbusException`；解析器其余失败（CRC/长度/校验不匹配）补 `ModbusErrorKind.ProtocolViolation`。

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS（含 Task 1.1/2.1 的集成测试）

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(result): add machine-readable ModbusErrorKind, replace string-based reconnect heuristic"
```

---

### Task 4.2: 异常码枚举拆分（线上异常码 vs 客户端错误）

**Files:**
- Create: `Junevy.Communication.Modbus/Core/Models/ModbusExceptionCode.cs`
- Delete: `Junevy.Communication.Modbus/Core/Models/ModbusException.cs` 中的 `ModbusErrorCode` 枚举（`ModbusException` 类保留，`ErrorCode` 类型改为 `ModbusExceptionCode`）
- Modify: `Junevy.Communication.Modbus/Extensions/ModbusExtensions.cs`（`Validate*`/`PackCoils`/`BuildDiagnosticsData` 改抛 `ArgumentException`）
- Modify: `Junevy.Communication.Modbus/Factory/ModbusFactory.cs:242`（PortName 校验改抛 `ArgumentException`）
- Modify: `Junevy.Communication.Modbus/Factory/ModbusConnectionManager.cs:39`（GetRequired 改抛 `InvalidOperationException`）
- Test: `Junevy.Communication.Modbus.Tests/ModbusFactoryTests.cs`（两处断言更新）+ 新增描述测试

**Interfaces:**
- Produces:
```csharp
namespace Junevy.Communication.Modbus.Core.Models;

/// <summary>Modbus 响应异常码（MODBUS Application Protocol V1.1b3 第 7 节）。</summary>
public enum ModbusExceptionCode
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    ServerDeviceFailure = 0x04,
    Acknowledge = 0x05,
    ServerDeviceBusy = 0x06,
    MemoryParityError = 0x08,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetDeviceFailedToRespond = 0x0B,
}

public static class ModbusExceptionCodeExtensions
{
    public static string Describe(this ModbusExceptionCode code) => code switch
    {
        ModbusExceptionCode.IllegalFunction => "Illegal function",
        ModbusExceptionCode.IllegalDataAddress => "Illegal data address",
        ModbusExceptionCode.IllegalDataValue => "Illegal data value",
        ModbusExceptionCode.ServerDeviceFailure => "Server device failure",
        ModbusExceptionCode.Acknowledge => "Acknowledge",
        ModbusExceptionCode.ServerDeviceBusy => "Server device busy",
        ModbusExceptionCode.MemoryParityError => "Memory parity error",
        ModbusExceptionCode.GatewayPathUnavailable => "Gateway path unavailable",
        ModbusExceptionCode.GatewayTargetDeviceFailedToRespond => "Gateway target device failed to respond",
        _ => $"Unknown exception code 0x{(byte)code:X2}",
    };
}
```
- `ModbusException.ErrorCode` 类型 → `ModbusExceptionCode`；`ModbusException` 仅用于表达"从站异常响应"（扩展层 `NormalizeRawResult` 未来可选抛出/由调用方解析）与库内少数保留场景。
- 参数校验类错误全部改抛标准异常：`ModbusExtensions.ValidateBitQuantity/ValidateRegisterQuantity/ValidateWriteRegisters/PackCoils/BuildDiagnosticsData` → `new ArgumentException(message, paramName?)`；工厂 PortName → `ArgumentException`；`GetRequired` → `InvalidOperationException`。

- [ ] **Step 1: 更新/新增测试**

```csharp
// ModbusFactoryTests.cs 修改两处：
[Fact]
public void TryAdd_RTU_EmptyPortName_ThrowsArgumentException()
{
    var factory = new ModbusFactory();
    Assert.Throws<ArgumentException>(() =>
        factory.TryAdd("key", new ModbusRTUConfig { PortName = "" }, out _));
}

[Fact]
public void GetRequired_WrongType_ThrowsInvalidOperation()
{
    var factory = new ModbusFactory();
    factory.TryAdd("tcp", new ModbusTCPConfig(), out _);
    Assert.Throws<InvalidOperationException>(() => factory.GetRequired<ModbusRTU>("tcp"));
}

// ModbusProtocolTests.cs 新增：
[Fact]
public void ExceptionCode_Describe_ReturnsKnownText()
{
    Assert.Equal("Illegal data address", ((ModbusExceptionCode)0x02).Describe());
    Assert.Contains("Unknown", ((ModbusExceptionCode)0x77).Describe());
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet build Junevy.Communication.Modbus.Tests`
Expected: 编译失败（`ModbusExceptionCode` 不存在）

- [ ] **Step 3: 实现**

- 新建 `ModbusExceptionCode.cs`（上文代码）。
- `ModbusException.cs`：删除 `ModbusErrorCode` 枚举，`ErrorCode` 属性与两个构造函数参数类型改为 `ModbusExceptionCode`。
- 按上文替换三处抛异常点；`ModbusExtensions` 中 `ModbusErrorCode.InvalidQuantity/InvalidValue/InvalidData` 的引用全部改为 `ArgumentException`（消息文本保留）。
- `TcpProtocolParser/RtuProtocolParser`（Task 1.2 后已无 `ModbusErrorCode` 引用，确认即可）。

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(errors): split wire exception codes from client errors, use standard exceptions for validation"
```

---

### Task 4.3: 提取 TCP/RTU 公共传输基类，消除重试骨架重复

**Files:**
- Create: `Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs`
- Modify: `Junevy.Communication.Modbus/TCP/ModbusTCP.cs`（继承基类，删除重复骨架）
- Modify: `Junevy.Communication.Modbus/RTU/ModbusRTU.cs`（同上）

**Interfaces:**
- Produces:
```csharp
public abstract class ModbusTransportBase : IModbus
{
    protected ModbusTransportBase(ILogger logger, IResponseParser responseParser, IModbusFrameBuilder frameBuilder);

    // —— IModbus 公开 API 在基类实现（Connect/Disconnect 带锁、Request/RequestAsync）——
    // —— 子类只需实现以下钩子：——
    public abstract ModbusProtocolType ProtocolType { get; }
    public abstract bool IsConnected { get; }
    protected abstract bool OpenConnection();                          // 无锁连接核心（TCP: 校验+ResetSocket+连接；RTU: ConfigurePort+Open）
    protected abstract Task<bool> OpenConnectionAsync(CancellationToken cancellationToken);
    protected abstract void CloseConnection();                         // Disconnect 核心
    protected abstract void InvalidateConnection();                    // 原 MarkConnectionFaulted
    protected abstract bool SendFrame(ModbusRequest request);          // 原 Send（已含协议参数）
    protected abstract Task<bool> SendFrameAsync(ModbusRequest request, CancellationToken cancellationToken);
    protected abstract ModbusResult<byte[]> ReceiveFrame(ModbusRequest request);   // 原 Read
    protected abstract Task<ModbusResult<byte[]>> ReceiveFrameAsync(ModbusRequest request, CancellationToken cancellationToken);

    // —— 基类持有并提供：——
    protected ILogger Logger { get; }
    protected IResponseParser ResponseParser { get; }
    protected IModbusFrameBuilder FrameBuilder { get; }
    protected abstract int RetryCount { get; }         // 从各自 Config 转发
    protected abstract int RetryInterval { get; }
    protected void LogTx(string name, byte[] data);    // 封装 stopwatch/lastTimestamp 状态
    protected void LogRx(string name, ReadOnlySpan<byte> data);
    protected static bool IsCommunicationException(Exception ex);  // 两表合并：
    // SocketException || TimeoutException || IOException || ObjectDisposedException
    // || UnauthorizedAccessException || InvalidOperationException || EndOfStreamException
}
```
- 基类实现 `Request`/`RequestAsync` 的完整重试循环（合并现有 4 份 for 循环为 2 份），事务 ID 分配（Task 1.1 引入的 `transactionId++`）也上移到基类。
- `ModbusTCP`/`ModbusRTU` 只保留：构造函数、Config 暴露、协议钩子实现、协议特定收发（TCP：6 字节头 + payload；RTU：DiscardInBuffer + Read-until-frame 循环）。
- 两个 `IsCommunicationException` 合并为一（超集），`InvalidOperationException` 保留在表内（串口未打开场景），在基类注释中注明该取舍。

- [ ] **Step 1: 先为重试循环补一条可测行为**（重试次数语义：`RetryCount` 为首次失败后的重试次数）

```csharp
[Fact]
public async Task TcpClient_RetryCount_RetriesThenSucceeds()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var requestCount = 0;
    var server = Task.Run(async () =>
    {
        // 循环 accept：第一次尝试超时后客户端会销毁连接并重连（新连接），必须逐个接受
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch { break; }   // listener.Stop() 后退出
            _ = HandleClientAsync(client);
        }
    });
    async Task HandleClientAsync(TcpClient client)
    {
        using var _ = client;
        var stream = client.GetStream();
        var req = new byte[12];
        int read = 0;
        while (read < req.Length)
            read += await stream.ReadAsync(req, read, req.Length - read);
        Interlocked.Increment(ref requestCount);
        if (Volatile.Read(ref requestCount) == 1)
            return; // 第一次不回复，制造超时
        byte[] resp = [req[0], req[1], 0x00, 0x00, 0x00, 0x05, 0x01, 0x03, 0x02, 0x12, 0x34];
        await stream.WriteAsync(resp, 0, resp.Length);
    }

    using var tcp = new ModbusTCP(new ModbusTCPConfig
    {
        Address = "127.0.0.1",
        ReadTimeOut = 300,
        WriteTimeOut = 300,
        ConnectTimeout = 1000,
        Reconnect = true,
        RetryCount = 2,
        RetryInterval = 10
    });
    tcp.Config.Port = port;

    var result = tcp.Request(new ModbusRequest
    {
        SlaveId = 1,
        FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
        Start = 0,
        Length = 1
    });
    listener.Stop();
    await server;

    Assert.True(result.IsSuccess, result.ErrorMessage);
    Assert.Equal(2, Volatile.Read(ref requestCount));   // 首次超时 + 第二次成功
}
```

- [ ] **Step 2: 运行确认通过**（重构前的行为基线）

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~RetryCount_RetriesThenSucceeds"`
Expected: PASS（先固定现状，重构后必须仍然 PASS）

- [ ] **Step 3: 实现基类并迁移**（机械重构；每迁移一个类立即编译）

- 新建 `ModbusTransportBase`，把 `ModbusTCP` 的以下成员原样上移改写：`Request`、`RequestAsync`、`ExecuteRequestWithRetry(Async)`（内部 `Send`→`SendFrame`、`Read`→`ReceiveFrame`）、`EnsureConnected(Async)`（`Connect()`→`OpenConnection()`）、`WaitBeforeRetry(Async)`、`GetAttemptCount`、`IsCommunicationException`、`ThrowIfDisposed`、`requestLock`/`stopwatch`/`lastTimestamp`/`transactionId`/`disposed`。
- `ModbusTCP` 继承基类：保留 `Config`、`ProtocolType`、`IsConnected`、`ConnectCore`→`OpenConnection`、`DisconnectCore`→`CloseConnection`、`MarkConnectionFaulted`→`InvalidateConnection`、`Send`→`SendFrame`、`Read`→`ReceiveFrame`、异步版本钩子（本任务先以 `Task.Run(同步钩子)` 过渡实现，Task 4.4 再替换为真异步）。
- `ModbusRTU` 同样迁移；`ConnectAsync` 过渡期保留 `Task.Run(Connect)`。
- 行为保持：`WaitBeforeRetry`（sync `Thread.Sleep` / async `Task.Delay`）、错误消息文本逐字不变。

- [ ] **Step 4: 运行全部测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全部 PASS（尤其 RetryCount、AutoConnects、SequentialTransactionIds、ConcurrentConnectAndRequest）

- [ ] **Step 5: 统计重复行数下降并 Commit**

Run: `wc -l Junevy.Communication.Modbus/TCP/ModbusTCP.cs Junevy.Communication.Modbus/RTU/ModbusRTU.cs`（记录基线 555+608 → 预期合计下降 ≥300 行）
```bash
git add -A
git commit -m "refactor(transport): extract shared ModbusTransportBase, deduplicate retry/reconnect scaffolding"
```

---

### Task 4.4: 真异步化（TCP NetworkStream / RTU BaseStream，全链路 CancellationToken）

**Files:**
- Modify: `Junevy.Communication.Modbus/TCP/ModbusTCP.cs`（`SendFrameAsync`/`ReceiveFrameAsync` 改为真异步：持有 `NetworkStream`，`WriteAsync`/`ReadAsync` 带 token；`ConnectAsync` 去掉 `Task.Run`，用 APM 包装 + 超时 CTS）
- Modify: `Junevy.Connection.Modbus/RTU/ModbusRTU.cs`（`SendFrameAsync`/`ReceiveFrameAsync` 改用 `serialPort.BaseStream.WriteAsync/ReadAsync` + linked CTS）
- Test: `Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs`

**Interfaces:**
- Produces: `RequestAsync` 的 I/O 不再占用阻塞线程池线程；`cancellationToken` 在 I/O 挂起时可生效（net8.0 即时；net472 在下一个 I/O 边界生效——在基类 XML 注释中注明）；同步 API 保持原实现不变（阻塞语义本就如此）。

- [ ] **Step 1: 写失败测试**（取消及时性——在 net8.0 测试项目上运行；文件顶部需补 `using System.Diagnostics;`）

```csharp
[Fact]
public async Task TcpClient_RequestAsync_CancelledDuringRead_ReturnsCancelledQuickly()
{
    var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    _ = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync();
        // 接受连接后保持沉默
        await Task.Delay(5000);
    });

    using var tcp = new ModbusTCP(new ModbusTCPConfig
    {
        Address = "127.0.0.1",
        ReadTimeOut = 30_000,        // 故意大于取消时间
        WriteTimeOut = 30_000,
        ConnectTimeout = 1000,
        RetryCount = 0
    });
    tcp.Config.Port = port;
    Assert.True(tcp.Connect());

    using var cts = new CancellationTokenSource(500);
    var sw = Stopwatch.StartNew();
    var result = await tcp.RequestAsync(new ModbusRequest
    {
        SlaveId = 1,
        FunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
        Start = 0,
        Length = 1
    }, cts.Token);
    sw.Stop();
    listener.Stop();

    Assert.False(result.IsSuccess);
    Assert.Equal(ModbusErrorKind.Cancelled, result.ErrorKind);
    Assert.True(sw.ElapsedMilliseconds < 5000, $"Cancellation took {sw.ElapsedMilliseconds}ms");
}
```

- [ ] **Step 2: 运行确认失败**

Run: `dotnet test Junevy.Communication.Modbus.Tests --filter "FullyQualifiedName~CancelledDuringRead"`
Expected: FAIL（当前 Task.Run + 阻塞读取使取消无法中断，等待满 30s 超时或耗时 ≫500ms）

- [ ] **Step 3: 实现（TCP 侧）**

- `ModbusTCP` 增加字段 `private NetworkStream? stream;`；`OpenConnection` 成功后 `stream = new NetworkStream(socket, ownsSocket: false);`；`CloseConnection`/`InvalidateConnection` 中 dispose 并置 null。
- `SendFrameAsync`：
```csharp
protected override async Task<bool> SendFrameAsync(ModbusRequest request, CancellationToken cancellationToken)
{
    var target = stream;
    if (target is null) return false;

    var frame = ArrayPool<byte>.Shared.Rent(ModbusFrameBuilder.MaxTcpAduLength);
    try
    {
        if (!frameBuilder.TryWriteRequestFrame(request, ProtocolType, frame, out int bytesWritten))
            return false;

        await target.WriteAsync(frame, 0, bytesWritten, cancellationToken);
        LogTx("ModbusTCP", new ArraySegment<byte>(frame, 0, bytesWritten).ToArray());
        return true;
    }
    catch (OperationCanceledException) { throw; }   // 由基类归为 Cancelled
    catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
    {
        Logger.LogError(ex, " [SendAsync] Send failed.");
        return false;
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(frame);
    }
}
```
- `ReceiveFrameAsync`：与 `ReceiveFrame` 同构，但用 `stream.ReadAsync(buffer, offset, count, cancellationToken)` 的 `ReceiveExactAsync`；读超时兜底靠 `socket.ReceiveTimeout`（捕获 `SocketException(TimedOut)` → `Fail(Timeout)`）；取消 → `OperationCanceledException` 上抛，基类请求循环统一 `catch (OperationCanceledException) → Fail(Cancelled)`。
- `ConnectAsync` 去掉 `Task.Run`：

```csharp
public async Task<bool> ConnectAsync()
{
    ThrowIfDisposed();
    await requestLock.WaitAsync();
    try
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        timeoutCts.CancelAfter(Config.ConnectTimeout);
        try
        {
            var asyncResult = socket!.BeginConnect(Config.Address, Config.Port, null, null);
            await Task.Factory.FromAsync(asyncResult, socket.EndConnect)
                .WaitAsync(timeoutCts.Token);   // net8.0 命中 NET8_0_OR_GREATER；net472 分支见下
            return true;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(" [ConnectAsync] Connection timed out: {Timeout}ms.", Config.ConnectTimeout);
            InvalidateConnection();
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, " [ConnectAsync] Connection failed.");
            InvalidateConnection();
            return false;
        }
    }
    finally { requestLock.Release(); }
}
```
net472 无 `Task.WaitAsync`：用 `#if NET8_0_OR_GREATER` 包住 `WaitAsync` 路径（net8.0 命中），`#else` 分支退化为 `Task.Run(() => ConnectCore())` 包装（超时由 `ConnectCore` 内既有 `AsyncWaitHandle.WaitOne(Config.ConnectTimeout)` 保证），保证多目标编译。
- 基类请求循环的取消处理：`catch (OperationCanceledException) → Fail("cancelled", ModbusErrorKind.Cancelled)`（现有逻辑已有，确认 ErrorKind）。

- [ ] **Step 4: 实现（RTU 侧）**

- `SendFrameAsync`：`await serialPort.BaseStream.WriteAsync(requestFrame, 0, bytesWritten, token);`（替换现有实现中的该行，其余结构不变）。
- `ReceiveFrameAsync`：linked CTS `CancelAfter(Config.ReadTimeOut)`（注意：属性重命名在 Task 5.2 才发生，此处用现行名） + `await serialPort.BaseStream.ReadAsync(pool, readCounts, pool.Length - readCounts, readTimeoutToken.Token)`；`OperationCanceledException` → `Fail("read cancelled", ModbusErrorKind.Cancelled)`；`TimeoutException`（SerialPort.ReadTimeout 兜底）→ `Fail(Timeout)`。删除现有 `Task.Run(() => serialPort.Read(...))` 包装。

- [ ] **Step 5: 运行全部测试（重复 2 次验证时序稳定性）**

Run: `dotnet test Junevy.Communication.Modbus.Tests`（×2）
Expected: 全部 PASS ×2（含取消及时性 <5s）

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(async): true async I/O for tcp/rtu with full cancellation support"
```

---

### Task 4.5: `ModbusRequest` 职责收敛（移除 UI 事件，明确 Data 语义）

**Files:**
- Modify: `Junevy.Communication.Modbus/Core/Models/ModbusRequest.cs`（删除 `OnFunctionCodeChanged`、`OnProtocolTypeChanged`、`InvokeOnFunctionCodeChanged`；为 `Data` 补全语义 XML 注释）
- Modify: `Junevy.Communication.Test/MainWindowViewModel.cs:49-74`（删除事件订阅块；DataList 行数完全由 `OnLengthChanged` 驱动）
- Test: `Junevy.Communication.Modbus.Tests`（确认无测试引用事件）

**Interfaces:**
- Produces: `ModbusRequest` 成为纯数据类；`Data` 字段语义文档化：
```csharp
/// <summary>
/// 请求数据，含义随 <see cref="FunctionCode"/> 变化：
/// 0x05 WriteSingleCoil / 0x06 WriteSingleRegister：恰好 2 字节（大端值）；
/// 0x0F WriteMultipleCoils / 0x10 WriteMultipleRegisters：按位/按寄存器打包的数据；
/// 0x16 MaskWriteRegister：[AndMask(2), OrMask(2)]；
/// 0x08 Diagnostics：[SubFunction(2), Data...]（原始 PDU 余部）；
/// 0x17 ReadWriteMultipleRegisters：[ReadStart(2), ReadQty(2), WriteStart(2), WriteQty(2), ByteCount(1), WriteData...]（原始 PDU 余部）；
/// 其余功能码为 null。
/// </summary>
public byte[]? Data { get; set; }
```

- [ ] **Step 1: 确认引用面**

Run: `grep -rn "OnFunctionCodeChanged\|OnProtocolTypeChanged\|InvokeOnFunctionCodeChanged" --include="*.cs" .`
Expected: 仅 `ModbusRequest.cs` 与 `Junevy.Communication.Test/MainWindowViewModel.cs:50`

- [ ] **Step 2: 修改 WPF 示例**

`MainWindowViewModel.cs` 删除构造函数中的 `Tx.OnFunctionCodeChanged += ...` 订阅块（49-74 行）。DataGrid 行数逻辑已由 `OnLengthChanged`（153-166 行）覆盖，无需替代实现。

- [ ] **Step 3: 删除事件成员并编译**

删除 `ModbusRequest` 的两个事件、两个 Invoke 方法及 FunctionCode setter 中的 `InvokeOnFunctionCodeChanged()` 调用。

- [ ] **Step 4: 全量构建 + 测试**

Run: `dotnet build Junevy.Communication.Modbus.Tests && dotnet build Junevy.Communication.Test && dotnet test Junevy.Communication.Modbus.Tests`
Expected: 两个项目编译通过、测试全绿

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(model): make ModbusRequest a pure data class, document per-function-code Data layout"
```

---

### Task 4.6: 打包元数据修复

**Files:**
- Modify: `Junevy.Communication.Modbus/Junevy.Communication.Modbus.csproj`

**Interfaces:**
- Produces: NuGet 元数据完整（文档文件生成、真实仓库地址、许可证表达）。

- [ ] **Step 1: 修改 csproj**

```xml
<PropertyGroup>
  <!-- 其余保持不变，追加： -->
  <GenerateDocumentationFile>true</GenerateDocumentationFile>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
</PropertyGroup>
```
（`RepositoryUrl` 已在 Task 2.4 写入真实值；许可证默认 MIT——若用户审查计划时另有指定，按指定修改。）

- [ ] **Step 2: 编译并处理 XML 注释警告**

Run: `dotnet build Junevy.Communication.Modbus -warnaserror` 若因历史成员缺 XML 注释大量报 CS1591，则回退为 `GenerateDocumentationFile=true` 不带 warnaserror，并把缺注释的公共成员补齐（主要是 `ModbusRTUConfig`/`ModbusTCPConfig` 部分属性）。

- [ ] **Step 3: 全量测试 + Commit**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
```bash
git add -A
git commit -m "chore(packaging): generate docs file, add license expression"
```

---

# Phase 5：命名规范化（破坏性重命名，一次性完成）

### Task 5.1: 类型与命名空间重命名

**Files:**
- Rename: `Junevy.Communication.Modbus/TCP/` → `Junevy.Communication.Modbus/Tcp/`；`Junevy.Communication.Modbus/RTU/` → `Junevy.Communication.Modbus/Rtu/`（含文件名 `ModbusTCP.cs`→`ModbusTcpClient.cs`、`ModbusRTU.cs`→`ModbusRtuClient.cs`、`ModbusTCPConfig.cs`→`ModbusTcpClientConfig.cs`、`ModbusRTUConfig.cs`→`ModbusRtuClientConfig.cs`）
- Modify: 上述文件内 `namespace Junevy.Communication.Modbus.TCP` → `Junevy.Communication.Modbus.Tcp`；`...RTU` → `...Rtu`
- Modify: 全库 `ModbusTCP` → `ModbusTcpClient`、`ModbusRTU` → `ModbusRtuClient`、`ModbusTCPConfig` → `ModbusTcpClientConfig`、`ModbusRTUConfig` → `ModbusRtuClientConfig`（库、测试、WPF 示例、README 全量替换）

**Interfaces:**
- Produces（公共类型最终名）：
  - `Junevy.Communication.Modbus.Tcp.ModbusTcpClient` / `ModbusTcpClientConfig`
  - `Junevy.Communication.Modbus.Rtu.ModbusRtuClient` / `ModbusRtuClientConfig`
  - 日志类别名随泛型参数自动变为 `ModbusTcpClient` 等。

- [ ] **Step 1: 目录/文件重命名 + 全局替换**

```bash
cd Junevy.Communication.Modbus
git mv TCP Tcp && git mv RTU Rtu
git mv Tcp/ModbusTCP.cs Tcp/ModbusTcpClient.cs
git mv Tcp/ModbusTCPConfig.cs Tcp/ModbusTcpClientConfig.cs
git mv Rtu/ModbusRTU.cs Rtu/ModbusRtuClient.cs
git mv Rtu/ModbusRTUConfig.cs Rtu/ModbusRtuClientConfig.cs
# 仓库根目录下全局替换（库、测试、示例、README）：
grep -rl "ModbusTCPConfig" --include="*.cs" --include="*.md" .. | xargs sed -i 's/ModbusTCPConfig/ModbusTcpClientConfig/g'
grep -rl "ModbusRTUConfig" --include="*.cs" --include="*.md" .. | xargs sed -i 's/ModbusRTUConfig/ModbusRtuClientConfig/g'
grep -rl "ModbusTCP" --include="*.cs" --include="*.md" .. | xargs sed -i 's/ModbusTCP/ModbusTcpClient/g'
grep -rl "ModbusRTU" --include="*.cs" --include="*.md" .. | xargs sed -i 's/ModbusRTU/ModbusRtuClient/g'
grep -rl "Communication.Modbus.TCP\|Modbus\.TCP" --include="*.cs" .. | xargs sed -i 's/Modbus\.TCP/Modbus.Tcp/g'
grep -rl "Communication.Modbus.RTU\|Modbus\.RTU" --include="*.cs" .. | xargs sed -i 's/Modbus\.RTU/Modbus.Rtu/g'
```
（README 中 `using Junevy.Communication.Modbus.TCP;` 等同步替换；字符串字面量 `"ModbusTCP"`（日志名）改为 `"ModbusTcpClient"`。）

- [ ] **Step 2: 全量编译 + 测试**

Run: `dotnet build Junevy.Communication.Modbus.Tests && dotnet build Junevy.Communication.Test && dotnet test Junevy.Communication.Modbus.Tests`
Expected: 编译通过、全部 PASS

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "refactor!: rename ModbusTCP/ModbusRTU to ModbusTcpClient/ModbusRtuClient (breaking)"
```

---

### Task 5.2: 成员重命名（配置/请求属性）

**Files:**
- Modify: `Junevy.Communication.Modbus/Tcp/ModbusTcpClientConfig.cs`、`Rtu/ModbusRtuClientConfig.cs`、`Core/Interfaces/IModbusConfig.cs`、`Core/Models/ModbusRequest.cs` 及全部引用点

**Interfaces:**
- Produces（映射表，sed 逐项执行）：

| 旧名 | 新名 | 影响范围 |
|---|---|---|
| `ReadTimeOut` | `ReadTimeout` | IModbusConfig、两个 Config、全部使用点、README |
| `WriteTimeOut` | `WriteTimeout` | 同上 |
| `ModbusRequest.Start` | `ModbusRequest.StartAddress` | ModbusRequest、FrameBuilder、PduVerifier 参数名同步、测试 |
| `ModbusRequest.Length` | `ModbusRequest.Quantity` | ModbusRequest、FrameBuilder、扩展层、测试、WPF 示例（`Tx.Length` → `Tx.Quantity`） |
| `IntervalTime` | `FrameReadInterval` | ModbusRtuClientConfig、ModbusRTU 内部两处、工厂默认值填充 |

- [ ] **Step 1: 全局替换**（注意 `Length` 仅为 `ModbusRequest` 成员——`request.Length`/`request\.Length` 模式替换，避免误伤局部变量）

```bash
grep -rl "ReadTimeOut" --include="*.cs" --include="*.md" .. | xargs sed -i 's/ReadTimeOut/ReadTimeout/g'
grep -rl "WriteTimeOut" --include="*.cs" --include="*.md" .. | xargs sed -i 's/WriteTimeOut/WriteTimeout/g'
grep -rl "IntervalTime" --include="*.cs" .. | xargs sed -i 's/IntervalTime/FrameReadInterval/g'
grep -rl "request\.Start\b" --include="*.cs" .. | xargs sed -i 's/request\.Start\b/request.StartAddress/g'
grep -rl "request\.Length\b" --include="*.cs" .. | xargs sed -i 's/request\.Length\b/request.Quantity/g'
# ModbusRequest.cs 内属性声明本身手工改名；测试中 new ModbusRequest{ Start = ..., Length = ... } 对象初始化器逐个替换（编译错误驱动，共约 15 处）
```
（对象初始化器里的 `Start =` / `Length =` 编译器会报错指路，逐一改为 `StartAddress =` / `Quantity =`。WPF 示例中 `Tx.Length = newValue;` 同步改为 `Tx.Quantity = newValue;`。）

- [ ] **Step 2: 全量编译 + 测试**

Run: `dotnet build Junevy.Communication.Modbus.Tests && dotnet build Junevy.Communication.Test && dotnet test Junevy.Communication.Modbus.Tests`
Expected: 编译通过、全部 PASS

- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "refactor!: rename config/request members to conventional names (breaking)"
```

---

### Task 5.3: 二进制工具与验证器参数名清理

**Files:**
- Delete: `Junevy.Communication.Modbus/Extensions/BinaryExtensions.cs` 中的 `ToUshort(byte lowByte, byte highByte)`
- Modify: 全部 13 处调用点改为 `BinaryPrimitives.ReadUInt16BigEndian`（语义等价、自解释）
- Modify: `Junevy.Communication.Modbus/Core/Parsing/ModbusPduVerifier.cs`（参数 `pdu` → `frame`，补 XML 注释说明 RTU 调用传入含 CRC 部分帧）

**Interfaces:**
- Produces: 调用点对照表（`ToUshort(a, b)` 中 a=低字节、b=高字节 → `BinaryPrimitives.ReadUInt16BigEndian(slice)` 需按大端两字节切片）：

| 调用点 | 替换 |
|---|---|
| TcpProtocolParser.cs:51-52,55（MBAP 三字段） | `BinaryPrimitives.ReadUInt16BigEndian(span.Slice(2, 2))` 等 |
| ModbusPduVerifier.cs:71,90,97,122,129,136 | `BinaryPrimitives.ReadUInt16BigEndian(frame.Slice(n, 2))` |
| RtuProtocolParser.cs:227-228（Data 手工字节） | `BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0, 2))` / `(data.AsSpan(2, 2))` |
| ModbusExtensions.cs:432-433,449-451（EventCounter/Log） | `BinaryPrimitives.ReadUInt16BigEndian(result.Data.AsSpan(2, 2))` 等 |
| TcpProtocolParser.cs:61（HandleTcpMaskWrite 内） | 同 Verifier 模式 |

- [ ] **Step 1: 逐点替换并删除 `ToUshort`**

每处替换后立即编译（`dotnet build Junevy.Communication.Modbus.Tests`），全部替换后删除 `BinaryExtensions.ToUshort`。

- [ ] **Step 2: `ModbusPduVerifier` 参数改名**

`VerifyReadPdu(ReadOnlySpan<byte> pdu, ...)` 等四个方法的参数 `pdu` → `frame`，方法 XML 注释注明："frame 为从 UnitId/SlaveId 起始的字节序列；RTU 调用方传入含 CRC 的完整候选帧，TCP 调用方传入 PDU 段。"

- [ ] **Step 3: 全量测试 + Commit**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
```bash
git add -A
git commit -m "refactor: replace confusing ToUshort(low, high) with BinaryPrimitives, rename PDU verifier params"
```

---

### Task 5.4: API 表面清理 + README 终稿

**Files:**
- Modify: `Junevy.Communication.Modbus/Tcp/ModbusTcpClient.cs`（删除 `CheckConnection()`）
- Modify: `Junevy.Communication.Modbus/Rtu/ModbusRtuClient.cs`（`InitialConnection()` → `ConfigurePort()`）
- Modify: `readme.md`（全文按新 API 重写使用示例；补一节"事务 ID 由客户端自动管理"；补 `ModbusErrorKind` 语义说明）

**Interfaces:**
- Produces: 无 `CheckConnection`；`ConfigurePort` 为 `ModbusRtuClient` 私有方法（原 `InitialConnection` 仅内部使用）。

- [ ] **Step 1: 删除/重命名并编译**

Run: `dotnet build Junevy.Communication.Modbus.Tests && dotnet build Junevy.Communication.Test`
（`CheckConnection` 全库零引用已确认；`InitialConnection` 仅内部 2 处调用。）

- [ ] **Step 2: 重写 README**（按当前 API 校对每个示例可编译：类型名、命名空间、`Port`、`ReadTimeout`、`Quantity` 语义、事务 ID 说明、`ModbusErrorKind`）

- [ ] **Step 3: 全量测试 + Commit**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
```bash
git add -A
git commit -m "refactor!: final API surface cleanup and README rewrite (breaking)"
```

---

## 执行顺序与依赖

```
Phase 0 (0.1 → 0.2) ──► Phase 1 (1.1 → 1.2) ──► Phase 2 (2.1 → 2.2 → 2.3 → 2.4) ──► Phase 3 (3.1 → 3.2 → 3.3 → 3.4 → 3.5)
      ──► Phase 4 (4.1 → 4.2 → 4.3 → 4.4 → 4.5 → 4.6) ──► Phase 5 (5.1 → 5.2 → 5.3 → 5.4)
```
- Task 0.2（.NET 8 升级）必须最先完成：此后所有任务的测试均以 net8.0 为主目标运行。
- Task 1.1 必须先于 1.2（异常响应测试的 TID 预期依赖精确匹配约定）。
- Task 4.1（ErrorKind）先于 4.3/4.4（基类与异步复用 kind）。
- Task 3.1（协议显式化接口）是 4.3/4.4 钩子签名的前置。
- Phase 5 各任务内部保持"重命名 → 编译 → 测试 → 提交"闭环；放最后避免与设计重构互相踩踏。

## 明确不做（审查提到、本计划有意不处理的事项）

| 事项 | 理由 |
|---|---|
| `Disconnect()` 后 `Reconnect=true` 导致的"静默重连" | 这是 `Reconnect` 配置的既定语义（README 已描述）；保持现状，Task 5.4 在 README 中显式说明该行为 |
| `LogExtensions.Tx/Rx` 公开方法重命名与 `ref lastTimestamp` 状态传递 | 内部状态经 Task 4.3 的基类 `LogTx/LogRx` 收敛封装；公开 API 留待下个大版本 |
| `ModbusHelper` 的结构重组（校验/解析/端口工具彻底拆分） | Task 3.3/4.2 已缓解其不一致；完整重组收益低、波及面大，留待后续 |
| `requestLock` 与 `Dispose` 的经典竞态（在途请求释放已释放的锁） | 业界通行接受度：文档注明"Dispose 与并发请求互斥由调用方保证"；引入生命周期状态机属过度设计 |
| RTU 半双工回环下 WriteSingle 请求回显可能被当作响应 | 回显与真实响应内容恰好一致，无实际危害；现有逐字节扫描已兜底 |


## Phase 0：执行前置（计划获批后第一件事）

### Task 0.1: 基线验证与工作分支

**Files:** 无代码改动（仅可能新增 ReferenceAssemblies 构建期包引用）

- [ ] **Step 1: 基线编译**

Run: `dotnet build Junevy.Communication.Modbus.Tests`
—— 若 net472 目标报缺少 targeting pack，给库 csproj 加 `<PackageReference Include="Microsoft.NETFramework.ReferenceAssemblies" Version="1.0.3" PrivateAssets="all" />` 后重试。

- [ ] **Step 2: 基线测试**

Run: `dotnet test Junevy.Communication.Modbus.Tests`
Expected: 全绿，记录基线通过数

- [ ] **Step 3: 示例编译**

Run: `dotnet build Junevy.Communication.Test`
Expected: WPF 示例编译通过

- [ ] **Step 4: 建立工作分支**

```bash
git checkout -b fix/modbus-review-findings
```

### Task 0.2: 升级到 .NET 8（net6.0 → net8.0，net472 保留）

**Files:**
- Modify: `Junevy.Communication.Modbus/Junevy.Communication.Modbus.csproj`
- Modify: `Junevy.Communication.Modbus.Tests/Junevy.Communication.Modbus.Tests.csproj`
- Modify: `readme.md`（Target Frameworks 小节）

**Interfaces:**
- Produces: 库目标框架 `net472;net8.0`；测试项目 `net8.0`；WPF 示例已是 `net8.0-windows` 无需变更。此后所有任务在 net8.0 上运行测试；`#if` 条件编译一律使用 `NET8_0_OR_GREATER`。

- [ ] **Step 1: 修改库 csproj**

```xml
<TargetFrameworks>net472;net8.0</TargetFrameworks>
...
<ItemGroup>
  <PackageReference Include="Microsoft.Bcl.AsyncInterfaces" Version="8.0.0" />
  <PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="8.0.0" />
  <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.0.0" />
  <PackageReference Include="System.Buffers" Version="4.5.1" />
  <PackageReference Include="System.IO.Ports" Version="8.0.0" />
  <PackageReference Include="System.Memory" Version="4.5.5" />
  <PackageReference Include="System.Threading.Tasks.Extensions" Version="4.5.4" />
</ItemGroup>
```
仅升级 6.0.0 时代的四个包（Bcl.AsyncInterfaces、DI.Abstractions、Logging.Abstractions、System.IO.Ports）；System.Buffers/System.Memory/System.Threading.Tasks.Extensions 与 .NET 6/8 无关，保持不变。四个包均提供 net462/netstandard2.0 支持，net472 目标不受影响。

- [ ] **Step 2: 修改测试项目 csproj**

```xml
<TargetFramework>net8.0</TargetFramework>
...
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.0" />
```
（xunit 2.5.3 / Moq 4.20.72 / Microsoft.NET.Test.Sdk 17.8.0 / coverlet 在 net8.0 下均兼容，保持不变。）

- [ ] **Step 3: 更新 README 目标框架小节**

`- .NET 6 (\`net6.0\`)` → `- .NET 8 (\`net8.0\`)`。

- [ ] **Step 4: 双目标编译 + 全量测试 + 示例编译**

Run: `dotnet build Junevy.Communication.Modbus.Tests && dotnet test Junevy.Communication.Modbus.Tests && dotnet build Junevy.Communication.Test`
Expected: net472 与 net8.0 均编译通过、测试全部 PASS（与 Task 0.1 基线一致）、WPF 示例编译通过

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "build: target net8.0 alongside net472, bump extensions/system.io.ports to 8.0.0"
```
