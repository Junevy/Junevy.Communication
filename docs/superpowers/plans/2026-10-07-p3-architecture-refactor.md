# 计划三：架构重构（P3，破坏性）

> 来源：2026-10-07 代码审查，"最后"组：`ModbusResult` 不可变化、`ModbusExtensions` 拆分、Parser 拆分、工厂策略化。
> 前置条件：计划一、计划二已合入 master。
> 版本：完成后 `Version` 由 `1.1.0` 改为 `2.0.0`（含公开 API 的破坏性变更）。
> 独立性：Task 1 至 Task 4 彼此独立：删除其中任意一个 Task 不影响其余 Task；删除某个 Task 时，同时删除 Task 5 中与之对应的文档条目。Task 5 必须最后执行。

## 0. 已固定的设计决策（审阅时逐条确认；不同意的条目，删除对应 Task 即可）

| 编号 | 决策 | 影响的 Task |
|---|---|---|
| D1 | 解析器新增一项校验：响应功能码必须等于请求功能码，或等于请求功能码 `\| 0x80`；不满足返回 `ProtocolViolation`。这是本计划中**唯一**的解析行为变更，其余解析结果保持逐字节一致 | Task 3 |
| D2 | 删除公开类 `ModbusPduVerifier`（其方法全部是 `internal`，外部无法使用或扩展），由新增的公开接口 `IModbusPduValidator` 与公开类 `ModbusPduValidator` 取代；`TcpProtocolParser`、`RtuProtocolParser` 的构造函数参数改为 `(IModbusPduValidator? validator = null, ILogger<...>? logger = null)`（顺序与原来相反） | Task 3 |
| D3 | `IModbusFactory` 删除 4 个按配置类型区分的重载（`GetOrAdd(string, ModbusTcpClientConfig)`、`GetOrAdd(string, ModbusRtuClientConfig)`、`TryAdd(string, ModbusTcpClientConfig, out IModbus?)`、`TryAdd(string, ModbusRtuClientConfig, out IModbus?)`），新增 `GetOrAdd(string, IModbusConfig)` 与 `TryAdd(string, IModbusConfig, out IModbus?)`；调用方源码不需要修改 | Task 4 |
| D4 | `ModbusResult<T>` 改为 `sealed`，全部属性只读；`Fail` 的 `kind` 参数不允许为 `ModbusErrorKind.None`（传入时抛出 `ArgumentException`） | Task 1 |
| D5 | 删除静态类 `ModbusExtensions`，拆分为 `ModbusBitExtensions`、`ModbusRegisterExtensions`、`ModbusDiagnosticsExtensions`，命名空间仍为 `Junevy.Communication.Modbus.Extensions`；所有扩展方法的名称、参数、返回类型不变，因此调用方源码不需要修改 | Task 2 |
| D6 | 版本号 `2.0.0`；不执行 `nuget push` | Task 5 |

## 1. 执行规则

与计划一第 0 节相同（分支改为 `refactor/modbus-p3-architecture`；基线记录在该分支第一个 commit 重新运行 3 次全量测试并写入提交信息）。额外规则：

- 每个 Task 开始前，先执行"特征测试"步骤：在不修改生产代码的前提下写出测试并确认它们在当前代码上通过；这些测试是重构的回归保障。
- 破坏性变更在 `CHANGELOG.md` 的 `### 变更（Changed，破坏性）` 中登记，每条写明"旧 API → 新 API"。
- 重构 Task 不得顺带修复其他缺陷；遇到的缺陷只记录在合并请求描述里。

## 2. Task 1：`ModbusResult<T>` 不可变化（D4）

### 2.1 目标形态（固定）

文件 `Junevy.Communication.Modbus/Core/Models/ModbusResult.cs` 的最终内容结构：

```csharp
public sealed class ModbusResult<T>
{
    private ModbusResult(bool isSuccess, T? data, string? errorMessage, ModbusErrorKind errorKind)
    {
        IsSuccess = isSuccess;
        Data = data;
        ErrorMessage = errorMessage;
        ErrorKind = errorKind;
    }

    public bool IsSuccess { get; }
    public T? Data { get; }
    public string? ErrorMessage { get; }
    public ModbusErrorKind ErrorKind { get; }

    public static ModbusResult<T> Success(T data)
        => new ModbusResult<T>(true, data, null, ModbusErrorKind.None);

    public static ModbusResult<T> Fail(string errMsg, T? data = default)
        => new ModbusResult<T>(false, data, errMsg, ModbusErrorKind.Unspecified);

    public static ModbusResult<T> Fail(string errMsg, ModbusErrorKind kind, T? data = default)
    {
        if (kind == ModbusErrorKind.None)
            throw new ArgumentException("A failed result cannot carry ModbusErrorKind.None.", nameof(kind));
        return new ModbusResult<T>(false, data, errMsg, kind);
    }
}
```

同时删除 XML 注释中多余的 `<param name="rawData">` 标记（编译警告 CS1572）。

### 2.2 步骤

1. 特征测试：在 `ModbusResultTests.cs` 中写入下表的前 3 个测试，确认在当前代码上通过；写入后 3 个测试，确认失败。
2. 按 2.1 重写文件。
3. 编译。2026-10-07 全仓库搜索确认没有任何位置对 `ModbusResult` 的属性赋值或使用对象初始化器；如果编译出现此类错误，改为调用 `Success`/`Fail`。
4. 运行全量测试。如果有用例因 `Fail(..., ModbusErrorKind.None)` 抛出 `ArgumentException` 而失败，说明该处传入的 `ErrorKind` 来源可能为 `None`：定位到产生该值的 `Fail` 之上，改为传入明确的非 `None` 类型（解析器产生的失败使用 `ProtocolViolation`，其余使用 `Unspecified`）。

### 2.3 测试（类 `ModbusResultTests`）

| 测试名 | 断言 |
|---|---|
| `Success_HasNoneKindAndData` | `IsSuccess == true`；`ErrorKind == None`；`ErrorMessage == null`；`Data` 等于传入值 |
| `Fail_DefaultKind_IsUnspecified` | `Fail("x")` 的 `ErrorKind == Unspecified` |
| `Fail_WithKind_CarriesKindAndData` | `Fail("x", Timeout, data)` 的 `ErrorKind == Timeout`、`Data == data` |
| `Properties_HaveNoPublicSetters` | 对 `typeof(ModbusResult<int>)` 的所有公开实例属性，`GetSetMethod(false) == null` |
| `Type_IsSealed` | `typeof(ModbusResult<int>).IsSealed == true` |
| `Fail_WithNoneKind_Throws` | `Fail("x", ModbusErrorKind.None)` 抛出 `ArgumentException` |

### 2.4 文档

- `CHANGELOG.md` `### 变更（Changed，破坏性）`：`{日期} ModbusResult<T> 改为 sealed，IsSuccess / Data / ErrorMessage / ErrorKind 只读（旧：public set）；Fail(msg, ModbusErrorKind.None) 抛出 ArgumentException。使用 Success()/Fail() 工厂方法的代码不受影响。`
- `Skills/using-junevy-modbus/SKILL.md` 第 12 行附近（Result-based 条目）末尾追加：`ModbusResult<T> is immutable and sealed; create results only through ModbusResult<T>.Success / Fail.`
- `readme.md`、`Junevy.Communication.Modbus/README.md`：搜索 `ModbusResult`，示例只读取属性，不需要修改；若发现赋值示例则改为工厂方法。

### 2.5 提交

`refactor(models)!: make ModbusResult immutable and sealed`

## 3. Task 2：拆分 `ModbusExtensions`（D5）

### 3.1 目标结构（固定）

| 文件 | 可见性 | 内容 |
|---|---|---|
| `Extensions/ModbusBitExtensions.cs` | `public static class ModbusBitExtensions` | `ReadCoils`、`ReadDiscreteInputs`、`WriteSingleCoil`、`WriteMultipleCoils`（各含 `Async`） |
| `Extensions/ModbusRegisterExtensions.cs` | `public static class ModbusRegisterExtensions` | `ReadHoldingRegisters`、`ReadInputRegisters`、`WriteSingleRegister`、`WriteMultipleRegisters`、`MaskWriteRegister`、`ReadWriteMultipleRegisters`（各含 `Async`） |
| `Extensions/ModbusDiagnosticsExtensions.cs` | `public static class ModbusDiagnosticsExtensions` | `ReadExceptionStatus`、`Diagnostics`（`ushort` 与 `byte[]` 两个重载）、`GetCommEventCounter`、`GetCommEventLog`、`ReportServerId`（各含 `Async`） |
| `Extensions/ModbusRequestExecutor.cs` | `internal static class` | 发送请求并规范化结果：`Execute(IModbus, ModbusRequest)`、`ExecuteAsync(IModbus, ModbusRequest, CancellationToken)`、`NormalizeRawResult`、`ExtractPdu`、`CreateRequest(slaveId, functionCode, start, quantity, data)` |
| `Extensions/ModbusPayloadCodec.cs` | `internal static class` | 请求数据打包与参数校验：`PackCoils`、`BuildDiagnosticsData`、`BuildReadWriteMultipleRegistersData`、`Combine`、`ValidateBitQuantity`、`ValidateRegisterQuantity`、`ValidateWriteRegisters`；响应映射：`MapRead<T>`、`MapCommEventCounter`、`MapCommEventLog`、`MapByteCountPayload`、`MapExceptionStatus` |

删除 `Extensions/ModbusExtensions.cs`。

每个公开操作的实现模式固定为（同步与异步共用同一个 `Create*Request` 与同一个 `Map*`）：

```csharp
public static ModbusResult<ushort[]> ReadHoldingRegisters(this IModbus modBus, byte slaveId, ushort start, ushort length)
{
    var request = CreateReadHoldingRegistersRequest(slaveId, start, length);   // 参数校验在此抛出 ArgumentException
    return ModbusPayloadCodec.MapRead(ModbusRequestExecutor.Execute(modBus, request), length, ModbusHelper.TryParseRegisters);
}

public static ValueTask<ModbusResult<ushort[]>> ReadHoldingRegistersAsync(this IModbus modBus, byte slaveId, ushort start, ushort length, CancellationToken cancellationToken = default)
{
    var request = CreateReadHoldingRegistersRequest(slaveId, start, length);   // 与同步版相同：同步抛出 ArgumentException，不包装进 ValueTask
    return ModbusRequestExecutor.ExecuteAndMapAsync(modBus, request, cancellationToken,
        raw => ModbusPayloadCodec.MapRead(raw, length, ModbusHelper.TryParseRegisters));
}
```

`ModbusRequestExecutor.ExecuteAndMapAsync<TResult>(IModbus, ModbusRequest, CancellationToken, Func<ModbusResult<byte[]>, TResult>)` 是 `async ValueTask<TResult>` 的内部辅助方法。每个 `Create*Request` 是同一文件内的 `private static` 方法。

### 3.2 步骤

1. 特征测试 A（公开表面不变）：新增 `ExtensionSurfaceTests.cs`。测试通过反射收集程序集 `Junevy.Communication.Modbus` 中所有满足以下条件的公开静态方法：声明类型位于命名空间 `Junevy.Communication.Modbus.Extensions`、带 `ExtensionAttribute`、第一个参数类型为 `IModbus`。每个方法格式化为字符串 `返回类型全名 方法名(参数类型全名列表)`，排序后与测试内嵌的字符串数组 `ExpectedSurface` 比较（数组长度和内容必须完全相等）。先写一个临时测试把当前方法列表打印出来，把输出复制进 `ExpectedSurface`，再删除临时测试。当前代码上运行，必须通过。
2. 特征测试 B（请求构造不变）：新增 `ExtensionRequestTests.cs`。使用 Moq 模拟 `IModbus`（`ProtocolType` 返回 `RTU`），捕获传给 `Request`/`RequestAsync` 的 `ModbusRequest`。对下表每一行，同步和异步各调用一次，断言捕获的请求的 `SlaveId`、`FunctionCode`、`StartAddress`、`Quantity`、`Data`（十六进制字符串，`null` 表示 `Data == null`）：

   | 调用 | FunctionCode | StartAddress | Quantity | Data |
   |---|---|---|---|---|
   | `ReadCoils(1, 10, 16)` | `ReadCoils` | 10 | 16 | null |
   | `ReadDiscreteInputs(1, 10, 16)` | `ReadDiscreteInputs` | 10 | 16 | null |
   | `ReadHoldingRegisters(1, 10, 4)` | `ReadHoldingRegisters` | 10 | 4 | null |
   | `ReadInputRegisters(1, 10, 4)` | `ReadInputRegisters` | 10 | 4 | null |
   | `WriteSingleCoil(1, 10, true)` | `WriteCoil` | 10 | 1 | `FF00` |
   | `WriteSingleCoil(1, 10, false)` | `WriteCoil` | 10 | 1 | `0000` |
   | `WriteSingleRegister(1, 10, 0x1234)` | `WriteHoldingRegister` | 10 | 1 | `1234` |
   | `WriteMultipleCoils(1, 10, [T,F,T,T,F,F,F,F,T])` | `WriteMultipleCoils` | 10 | 9 | `0D01` |
   | `WriteMultipleRegisters(1, 10, [0x0001, 0x0203])` | `WriteMultipleHoldingRegisters` | 10 | 2 | `00010203` |
   | `ReadExceptionStatus(1)` | `ReadExceptionStatus` | 0 | 0 | null |
   | `Diagnostics(1, 0x0000, (ushort)0x1234)` | `Diagnostics` | 0 | 0 | `00001234` |
   | `Diagnostics(1, 0x0001, new byte[] {1,2,3})` | `Diagnostics` | 0 | 0 | `0001010203` |
   | `GetCommEventCounter(1)` | `GetCommEventCounter` | 0 | 0 | null |
   | `GetCommEventLog(1)` | `GetCommEventLog` | 0 | 0 | null |
   | `ReportServerId(1)` | `ReportServerId` | 0 | 0 | null |
   | `MaskWriteRegister(1, 10, 0xF2F2, 0x2525)` | `MaskWriteRegister` | 10 | 1 | `F2F22525` |
   | `ReadWriteMultipleRegisters(1, 3, 6, 14, [0x00FF, 0x00FF, 0x00FF])` | `ReadWriteMultipleRegisters` | 3 | 6 | `00030006000E00030600FF00FF00FF` |

   参数校验行为（同步抛出）：`ReadHoldingRegisters(1, 0, 0)`、`ReadHoldingRegisters(1, 0, 126)`、`ReadCoils(1, 0, 2001)`、`WriteMultipleRegisters(1, 0, new ushort[0])`、`WriteMultipleCoils(1, 0, new bool[1969])` 对同步版本与异步版本都在调用点同步抛出 `ArgumentException`（异步版本不是返回已故障的任务）。
   运行，必须在当前代码上通过。
3. 创建 `ModbusRequestExecutor.cs`、`ModbusPayloadCodec.cs`，把 `ModbusExtensions.cs` 中对应的私有方法移入（保持方法体不变，仅改可见性为 `internal` 与所在类），编译。
4. 创建三个公开静态类文件，按 3.1 的模式把每个公开方法迁移过去；方法的参数名、默认值（`cancellationToken = default`）、返回类型与原来逐字相同。
5. 删除 `ModbusExtensions.cs`。全仓库搜索 `ModbusExtensions.`（带类名的静态调用），如果有则改为对应的新类名（2026-10-07 搜索结果：测试和示例项目均使用扩展方法语法，没有静态调用）。
6. 运行特征测试 A、B 和全量测试，全部通过。
7. 清理：确认 `ModbusRequestExecutor.cs`、`ModbusPayloadCodec.cs` 中没有未被引用的私有或内部方法（逐个方法在解决方案中搜索引用）。

### 3.3 文档

- `CHANGELOG.md` `### 变更（Changed，破坏性）`：`{日期} 静态类 ModbusExtensions 拆分为 ModbusBitExtensions / ModbusRegisterExtensions / ModbusDiagnosticsExtensions（命名空间不变，扩展方法签名不变，使用扩展方法语法的调用方无需修改；直接以 ModbusExtensions.Xxx(...) 静态调用的代码需要改类名）。`
- `Skills/using-junevy-modbus/SKILL.md` 第 104 行 "Extension methods (all 15 function codes)" 之前插入一行：`Extension methods live in the namespace Junevy.Communication.Modbus.Extensions and are grouped into ModbusBitExtensions (0x01 0x02 0x05 0x0F), ModbusRegisterExtensions (0x03 0x04 0x06 0x10 0x16 0x17) and ModbusDiagnosticsExtensions (0x07 0x08 0x0B 0x0C 0x11); always call them with extension-method syntax.`
- `readme.md` "Supported Function Codes" 小节（第 16 行起）：在表格或列表之后追加同一句分组说明（英文原文相同）。

### 3.4 提交

`refactor(extensions)!: split ModbusExtensions by function-code group`

## 4. Task 3：拆分 Parser 为"解帧"与"PDU 校验"（D1、D2）

### 4.1 目标结构（固定）

| 类型 | 职责 | 状态 |
|---|---|---|
| `IResponseParser`（`Core/Interfaces/IResponseParser.cs`） | 输入原始响应缓冲区与请求，输出校验后的**完整帧切片**（TCP：含 MBAP；RTU：含从站号与 CRC） | 契约与签名不变 |
| `TcpProtocolParser` | 只负责 MBAP 解析：协议 ID、事务 ID、从站号、长度字段；把 PDU 交给 `IModbusPduValidator` | 重写 |
| `RtuProtocolParser` | 只负责 RTU 扫描：按从站号定位帧起点、按期望长度截取、CRC 校验；把 PDU 交给 `IModbusPduValidator` | 重写 |
| `IModbusPduValidator`（`Core/Interfaces/IModbusPduValidator.cs`，新增） | 与传输无关的 PDU 语义：期望长度、功能码一致性、字节数、回显字段 | 新增 |
| `ModbusPduValidator`（`Core/Parsing/ModbusPduValidator.cs`，新增，`public sealed`） | 上述接口的唯一实现 | 新增 |
| `ModbusPduVerifier` | 被 `ModbusPduValidator` 取代 | 删除 |

`IModbusPduValidator` 固定声明：

```csharp
public interface IModbusPduValidator
{
    /// <summary>
    /// 返回响应 PDU（从功能码字节开始，不含从站号、MBAP、CRC）的期望总字节数。
    /// pduPrefix 的内容不足以确定长度时返回 -1。pduPrefix[0] 最高位为 1（异常响应）时返回 2。
    /// </summary>
    int GetExpectedPduLength(ReadOnlySpan<byte> pduPrefix, ModbusRequest request);

    /// <summary>
    /// 校验一个完整的响应 PDU 与请求一致。pdu 的长度必须不小于 GetExpectedPduLength 的返回值。
    /// 成功返回 pdu 的前 GetExpectedPduLength 个字节；异常响应返回 ErrorKind.ModbusException；
    /// 其余校验失败返回 ErrorKind.ProtocolViolation。
    /// </summary>
    ModbusResult<ReadOnlyMemory<byte>> Validate(ReadOnlyMemory<byte> pdu, ModbusRequest request);
}
```

### 4.2 期望 PDU 长度表（`GetExpectedPduLength` 的固定规则）

`pduPrefix[0]` 的最高位为 1 时返回 2。否则按 `request.FunctionCode` 决定：

| 功能码 | 期望 PDU 长度 | 需要的前缀字节数 |
|---|---|---|
| 0x01、0x02、0x03、0x04、0x17 | `2 + pduPrefix[1]` | 2（不足返回 -1） |
| 0x05、0x06 | 5 | 1 |
| 0x0F、0x10 | 5 | 1 |
| 0x16 | 7 | 1 |
| 0x07 | 2 | 1 |
| 0x08 | `1 + request.Data.Length` | 1 |
| 0x0B | 5 | 1 |
| 0x0C、0x11 | `2 + pduPrefix[1]` | 2（不足返回 -1） |

`pduPrefix.Length == 0` 时一律返回 -1。

### 4.3 `Validate` 的固定校验顺序

1. `pdu.Length < 1`：`ProtocolViolation`。
2. `pdu[0]` 最高位为 1：若 `pdu[0] != (byte)request.FunctionCode | 0x80`，返回 `ProtocolViolation`（消息含期望与实际功能码）；否则若 `pdu.Length < 2` 返回 `ProtocolViolation`；否则返回 `ModbusException`，消息固定为 `$"Modbus exception response. Function=0x{pdu[0]:X2}, Code=0x{pdu[1]:X2}."`（与现有解析器逐字相同）。
3. `pdu[0] != (byte)request.FunctionCode`：`ProtocolViolation`（**D1，唯一的行为变更**）。
4. `pdu.Length < GetExpectedPduLength(...)`（或其返回 -1）：`ProtocolViolation`，消息含 `too short`。
5. 按功能码校验内容：
   - 0x01、0x02：`pdu[1] == (request.Quantity + 7) / 8`。
   - 0x03、0x04、0x17：`pdu[1] == request.Quantity * 2`。
   - 0x05、0x06：地址（`pdu[1..3]` 大端）`== request.StartAddress`，且 `pdu[3..5]` 与 `request.Data[0..2]` 逐字节相等。
   - 0x0F、0x10：地址 `== request.StartAddress`，数量（`pdu[3..5]` 大端）`== request.Quantity`。
   - 0x16：地址 `== request.StartAddress`，`pdu[3..5]` 与 `request.Data[0..2]` 相等，`pdu[5..7]` 与 `request.Data[2..4]` 相等。
   - 0x07、0x08、0x0B、0x0C、0x11：不做内容校验。
6. 成功：返回 `Success(pdu.Slice(0, 期望长度))`。

日志级别与现有 `ModbusPduVerifier` 一致（校验失败为 `Warning`）。校验器不记录 RX 日志。

### 4.4 两个解析器的固定算法

**`TcpProtocolParser.ParseResponse(response, request)`**（错误消息文本沿用现有文本；以下"Fail"均为 `ProtocolViolation`，除非注明）：

1. `response.Length == 0` → Fail `" [TcpParser] The response is empty."`。
2. `!ModbusHelper.CheckRequest(request)` → Fail `" [TcpParser] The request is invalid."`。
3. `response.Length < 9` → Fail `" [TcpParser] Response too short."`（`Data = response`）。
4. 协议 ID（字节 2-3）非 0 → Fail `"Invalid protocol ID: {id}."`。
5. 事务 ID（字节 0-1）`!= request.TransactionId` → Fail `"Transaction ID mismatch. Expected {e}, actual {a}."`。
6. 从站号（字节 6）`!= request.SlaveId` → Fail `"Slave ID mismatch. Expected {e}, actual {a}."`。
7. `totalLength = 6 + MBAP长度字段`；`response.Length < totalLength` → Fail `"Invalid response length. Expected {e}, actual {a}."`。
8. `pdu = response.Slice(7, totalLength - 7)`；`pdu.Length < 1` → Fail `" [TcpParser] Response too short."`。
9. `n = validator.GetExpectedPduLength(pdu.Span, request)`；`n < 0` 或 `pdu.Length < n` → Fail `" [TcpParser] Response too short."`。
10. `v = validator.Validate(pdu, request)`：
    - `v.ErrorKind == ModbusException` → `Fail(v.ErrorMessage, ModbusException, response.Slice(0, totalLength))`。
    - 其他失败 → `Fail(v.ErrorMessage, ProtocolViolation, response)`。
    - 成功 → `Success(response.Slice(0, 7 + n))`。

**`RtuProtocolParser.ParseResponse(response, request)`**：

1. 空响应、非法请求：与 TCP 相同，文本前缀为 `" [RtuParser]"`。
2. `offset = 0`；当 `offset + 5 <= response.Length` 时循环：
   1. `response.Span[offset] != request.SlaveId` → 记录警告，`offset++`，继续。
   2. `frame = response.Slice(offset)`；`n = validator.GetExpectedPduLength(frame.Span.Slice(1), request)`。
   3. `n < 0` → 返回 Fail `" [RtuParser] Response too short."`（`Data = frame`；调用方据此继续读取更多字节，不跳过）。
   4. `total = 1 + n + 2`；`frame.Length < total` → 返回 Fail `" [RtuParser] Response too short. Expected {total}, actual {len}."`（`Data = frame`，不跳过）。
   5. `candidate = frame.Slice(0, total)`；`v = validator.Validate(candidate.Slice(1, n), request)`。
   6. `!v.IsSuccess && v.ErrorKind != ModbusException` → `offset++`，继续。
   7. `!Crc16Helper.VerifyCrc(candidate.Span)` → `offset++`，继续。
   8. `v.IsSuccess` → 返回 `Success(candidate)`。
   9. 否则（`ModbusException`）→ 返回 `Fail(v.ErrorMessage, ModbusException, candidate)`。
3. 循环结束 → Fail `" [RtuParser] Failed to match response."`（`Data = response`）。

两个解析器都不再持有 `Stopwatch`、`lastTimestamp`，不再调用 `logger.Rx(...)`；RX 日志只由客户端（`ModbusTransportBase.LogRx`）记录。构造函数：`TcpProtocolParser(IModbusPduValidator? validator = null, ILogger<TcpProtocolParser>? logger = null)`，`RtuProtocolParser` 同理；`validator` 为 `null` 时使用 `new ModbusPduValidator()`。`ModbusPduValidator(ILogger<ModbusPduValidator>? logger = null)`。

### 4.5 步骤

1. **差分测试准备**：把当前的 `TcpProtocolParser.cs`、`RtuProtocolParser.cs`、`ModbusPduVerifier.cs` 复制到 `Junevy.Communication.Modbus.Tests/Legacy/`，命名空间改为 `Junevy.Communication.Modbus.Tests.Legacy`，类名加前缀 `Legacy`（`LegacyTcpProtocolParser`、`LegacyRtuProtocolParser`、`LegacyPduVerifier`），`ModbusPduVerifier` 的引用改为 `LegacyPduVerifier`。这三个文件是临时文件，本 Task 最后一步删除。
2. **语料生成器**：新增 `Junevy.Communication.Modbus.Tests/ParserCorpus.cs`。对下表前 15 行的功能码各构造一个请求和一个合法响应 PDU，最后一行构造一个异常响应（TCP 请求：事务 ID `0x0007`、从站号 `1`；RTU：从站号 `1`）：

   | 功能码 | 请求参数 | 合法响应 PDU（十六进制） |
   |---|---|---|
   | 0x01 | 起始 0，数量 10 | `01 02 AA 01` |
   | 0x02 | 起始 0，数量 10 | `02 02 AA 01` |
   | 0x03 | 起始 0，数量 2 | `03 04 00 01 00 02` |
   | 0x04 | 起始 0，数量 2 | `04 04 00 01 00 02` |
   | 0x05 | 起始 5，Data `FF00` | `05 00 05 FF 00` |
   | 0x06 | 起始 5，Data `1234` | `06 00 05 12 34` |
   | 0x07 | 无 | `07 6D` |
   | 0x08 | Data `00001234` | `08 00 00 12 34` |
   | 0x0B | 无 | `0B 00 00 00 08` |
   | 0x0C | 无 | `0C 08 00 00 00 08 00 0A 01 02` |
   | 0x0F | 起始 3，数量 10，Data 2 字节 | `0F 00 03 00 0A` |
   | 0x10 | 起始 3，数量 2，Data 4 字节 | `10 00 03 00 02` |
   | 0x11 | 无 | `11 03 01 FF 55` |
   | 0x16 | 起始 4，Data `00F20025` | `16 00 04 00 F2 00 25` |
   | 0x17 | 读起始 0、读数量 2、写起始 0、写数量 1、写字节数 2、写数据 `0005`（Data 按 `ModbusRequest.Data` 文档的布局） | `17 04 00 01 00 02` |
   | 异常（对 0x03） | 起始 0，数量 2 | `83 02` |

   生成器把 PDU 封装为 TCP 帧（`TID(2) 0000 长度(2) 从站号 PDU`）和 RTU 帧（`从站号 PDU CRC(低字节在前)`）。
3. **差分测试**：新增 `ParserDifferentialTests.cs`。对每个（功能码、协议）的合法帧，生成下列变体，分别送入旧解析器和新解析器，比较结果：
   - 原始合法帧；
   - 截断到长度 `0..n-1` 的每一个前缀；
   - 逐字节将第 `i` 个字节取反（`^= 0xFF`），`i` 取遍整个帧（含 CRC）；
   - RTU 专用：在帧前加 1、2、3 个值为 `0xAA` 的垃圾字节；在帧后加 1、2、3 个值为 `0x55` 的字节；在帧前加一个合法的其他功能码响应帧；
   - TCP 专用：在帧后加 1、2、3 个值为 `0x55` 的字节。

   比较规则：比较 `IsSuccess` 与 `ErrorKind`；当 `IsSuccess` 为 `true` 或 `ErrorKind == ModbusException` 时，额外逐字节比较 `Data`。旧解析器抛出异常时，只要求新解析器不抛出异常，不做其他比较。**跳过规则**：被取反的字节位于功能码字节位置（TCP 为下标 7；RTU 为从站号之后的第一个字节）的变体不比较（D1 允许新旧不同）。
4. 在**未修改任何生产代码**的状态下运行差分测试：此时新旧解析器指的是同一份逻辑的两份拷贝，必须全部通过（用于验证测试本身没有缺陷）。
5. 新增 `IModbusPduValidator`、`ModbusPduValidator`，并为它写 `ModbusPduValidatorTests`（见 4.6）。
6. 重写 `TcpProtocolParser`、`RtuProtocolParser`（4.4），删除 `ModbusPduVerifier.cs`。修改 `ModbusServiceCollectionExtensions.AddModbusFactory`：删除 `services.TryAddSingleton<ModbusPduVerifier>();`，替换为 `services.TryAddSingleton<IModbusPduValidator, ModbusPduValidator>();`。`ModbusFactory`、`ModbusFactoryBuilder` 中对 `TcpProtocolParser`/`RtuProtocolParser` 的 `new` 保持不变（它们的无参使用方式仍然有效）。
7. 运行差分测试，全部通过；运行全量测试，全部通过。差分测试失败时，先判断是否属于 D1 的跳过范围；不属于时修复新实现，不放宽比较规则。
8. 删除 `Tests/Legacy/` 目录、`ParserDifferentialTests.cs`。保留 `ParserCorpus.cs`，并新增 `ParserSpecTests.cs`（4.6）作为长期回归测试。
9. 清理：全仓库搜索 `ModbusPduVerifier`、`FunctionCodeCategory`、`DefaultUnmatched`、`TcpMinFrameLength`、`HandleTcp`、`HandleRtu`，结果必须为 0 处（CHANGELOG 历史描述除外）。

### 4.6 长期测试

`ModbusPduValidatorTests`：

| 测试名 | 断言 |
|---|---|
| `GetExpectedPduLength_MatchesTable`（`[Theory]`，覆盖 4.2 表的每一行，各取 1 个典型前缀） | 返回值与表一致 |
| `GetExpectedPduLength_InsufficientPrefix_ReturnsMinusOne` | 0x03 的前缀只有 1 字节、前缀为空 → `-1` |
| `GetExpectedPduLength_ExceptionPdu_ReturnsTwo` | 前缀 `83` → `2` |
| `Validate_FunctionCodeMismatch_ReturnsProtocolViolation` | 请求 0x03，PDU 以 `04` 开头 → `ProtocolViolation` |
| `Validate_ExceptionPdu_ReturnsModbusExceptionWithCode` | PDU `83 02` → `ModbusException`，消息含 `0x02` |
| `Validate_ExceptionWithWrongFunction_ReturnsProtocolViolation` | 请求 0x03，PDU `84 02` → `ProtocolViolation` |
| `Validate_ByteCountMismatch_ReturnsProtocolViolation` | 请求 0x03 数量 2，PDU `03 02 00 01` → `ProtocolViolation` |
| `Validate_EchoMismatch_ReturnsProtocolViolation`（覆盖 0x05、0x06、0x0F、0x10、0x16 各 1 个地址错误与 1 个值/数量错误） | `ProtocolViolation` |
| `Validate_ValidPdu_ReturnsExactSlice`（覆盖 4.5 第 2 步表中全部合法 PDU，PDU 尾部附加 3 个多余字节） | `IsSuccess == true`，返回长度等于期望长度 |

`ParserSpecTests`（基于 `ParserCorpus`）：

| 测试名 | 断言 |
|---|---|
| `Tcp_ValidFrames_ReturnFullFrameSlice`（15 个合法帧） | `IsSuccess == true`；`Data` 等于去掉尾部多余字节后的帧 |
| `Rtu_ValidFrames_ReturnFullFrameSlice`（15 个合法帧） | 同上，包含 CRC |
| `Rtu_LeadingGarbage_IsSkipped` | 帧前加 3 字节 `AA`，`IsSuccess == true`，`Data` 等于合法帧 |
| `Rtu_BadCrc_ReturnsFailure` | 取反最后一个字节 → `IsSuccess == false`，不抛异常 |
| `Rtu_PartialFrame_ReturnsProtocolViolationWithoutSkipping` | 截断到少 1 字节 → `ErrorKind == ProtocolViolation`，消息含 `too short` |
| `Tcp_FunctionCodeMismatch_ReturnsProtocolViolation` | 请求 0x03，响应功能码 0x04 → `ProtocolViolation`（D1） |
| `Rtu_FunctionCodeMismatch_ReturnsFailure` | 同上，RTU（D1） |
| `Tcp_Exception_ReturnsModbusExceptionWithCode` | 消息含 `0x02`，`Data` 为完整 9 字节帧 |
| `Rtu_Exception_ReturnsModbusExceptionWithCode` | 消息含 `0x02`，`Data` 为完整 5 字节帧 |
| `Tcp_TransactionIdMismatch`、`Tcp_SlaveMismatch`、`Tcp_ProtocolIdNonZero` | `ProtocolViolation`，消息分别含 `Transaction ID`、`Slave ID`、`protocol ID` |
| `Parsers_NeverThrow_OnAnyTruncationOrByteFlip` | 对语料中所有截断与取反变体，两个解析器均不抛异常 |

### 4.7 文档

- `CHANGELOG.md` `### 变更（Changed，破坏性）`：`{日期} ModbusPduVerifier（公开类，方法均为 internal）删除，由 IModbusPduValidator / ModbusPduValidator 取代；TcpProtocolParser(ILogger?, ModbusPduVerifier?) → TcpProtocolParser(IModbusPduValidator?, ILogger?)，RtuProtocolParser 同理；解析器不再记录 RX 日志（RX 日志由客户端记录，消除 RTU 成功帧的重复 RX 日志）。`；`### 修复（Fixed）`：`{日期} 解析器校验响应功能码必须等于请求功能码（或其 |0x80），不一致返回 ProtocolViolation。`
- `Skills/using-junevy-modbus/SKILL.md`：在 "Behavioral Contracts" 新增一条：`- **Response validation**: the parser checks MBAP/CRC framing, then the response PDU (function code equals the request function code, byte count, echoed address/quantity/value). Custom parsers implement IResponseParser; custom PDU rules implement IModbusPduValidator.`
- `readme.md`：在 "Manual Construction Without Microsoft DI" 小节之后新增 "Custom response validation" 小节，内容固定为两句：`Implement IModbusPduValidator and pass it to new TcpProtocolParser(validator) / new RtuProtocolParser(validator).` 和 `Pass the parser to ModbusFactoryBuilder.WithTcpParser / WithRtuParser.`（`WithTcpParser`/`WithRtuParser` 在 Task 4 之后接受 `IResponseParser`；若 Task 4 被删除，第二句改为 `Pass the parser to the ModbusTcpClient / ModbusRtuClient constructor.`）。

### 4.8 提交

`refactor(parsing)!: split framing from PDU validation; validate response function code`

## 5. Task 4：工厂策略化（D3）

### 5.1 目标结构（固定）

新增文件：

- `Factory/IModbusClientCreator.cs`：
  ```csharp
  public interface IModbusClientCreator
  {
      /// <summary>本创建器处理的配置类型（精确类型）。</summary>
      Type ConfigType { get; }

      /// <summary>校验并补全默认值。会修改传入的配置对象。配置非法时抛出 ArgumentException。</summary>
      void Normalize(IModbusConfig config);

      /// <summary>创建客户端。config 已通过 Normalize。</summary>
      IModbus Create(IModbusConfig config);

      /// <summary>用于日志的目标描述，例如 "192.168.1.100:502" 或 "COM3"。</summary>
      string Describe(IModbusConfig config);
  }
  ```
- `Factory/TcpClientCreator.cs`：`public sealed class TcpClientCreator : IModbusClientCreator`，`ConfigType` 为 `typeof(ModbusTcpClientConfig)`；构造函数 `(ILoggerFactory? loggerFactory = null, IResponseParser? parser = null, IModbusFrameBuilder? frameBuilder = null)`，参数为 `null` 时分别使用 `NullLoggerFactory.Instance`、`new TcpProtocolParser()`、`new ModbusFrameBuilder()`。`Normalize` 的逻辑从 `ModbusFactory.ValidateAndFillDefaults(ModbusTcpClientConfig, string)` 原样迁入（去掉其中的 `config == null` 与 `key` 检查，这两项留在工厂）。`Create` 返回 `new ModbusTcpClient(config, loggerFactory.CreateLogger<ModbusTcpClient>(), parser, frameBuilder)`。
- `Factory/RtuClientCreator.cs`：`ConfigType` 为 `typeof(ModbusRtuClientConfig)`；其余对应 RTU，`Normalize` 从 `ValidateAndFillDefaults(ModbusRtuClientConfig, string)` 原样迁入（`PortName` 为空仍抛出 `ArgumentException`）。

`IModbusFactory` 的修改：删除 4 个按配置类型的重载，新增
```csharp
IModbus GetOrAdd(string key, IModbusConfig config);
bool TryAdd(string key, IModbusConfig config, out IModbus? modbus);
```
`ModbusFactory` 的修改：
- 构造函数只保留两个：`ModbusFactory()` 与 `ModbusFactory(ILogger<ModbusFactory> logger, IEnumerable<IModbusClientCreator> creators, IModbusConnectionManager manager)`。前者等价于 `(NullLogger<ModbusFactory>.Instance, 默认创建器, new ModbusConnectionManager(...))`。其余 4 个构造函数删除。`creators` 或 `manager` 为 `null` 时抛出 `ArgumentNullException`。
- 删除字段 `loggerFactory`、`tcpParser`、`rtuParser`、`frameBuilder` 以及两个 `ValidateAndFillDefaults` 方法。
- 新增 `private readonly Dictionary<Type, IModbusClientCreator> creators`。构造时按枚举顺序遍历，`creators[creator.ConfigType] = creator`（**同一 `ConfigType` 出现多次时，枚举顺序靠后的覆盖靠前的**，与 Microsoft DI 的"最后注册者生效"惯例一致）。
- 新增 `internal static IReadOnlyList<IModbusClientCreator> CreateDefaultCreators(ILoggerFactory loggerFactory, IResponseParser? tcpParser, IResponseParser? rtuParser, IModbusFrameBuilder? frameBuilder)`，返回 `TcpClientCreator` 与 `RtuClientCreator` 各一个。
- `GetOrAdd(key, config)` 与 `TryAdd(key, config, out modbus)` 的固定执行顺序：`ThrowIfDisposed()` → `config == null` 抛 `ArgumentNullException` → `key` 为空抛 `ArgumentException` → 解析创建器（从 `config.GetType()` 开始沿 `BaseType` 链向上查找第一个已注册的 `ConfigType`；找不到抛 `NotSupportedException`，消息含配置类型全名） → `creator.Normalize(config)` → 记录日志（`creator.Describe(config)`） → `manager.GetOrAdd(key, _ => creator.Create(config))` 或 `manager.Add`（`TryAdd` 的失败回收逻辑保持：`Add` 返回 `false` 时 `Dispose` 刚创建的实例）。

`ModbusFactoryBuilder` 的修改：
- `WithTcpParser(TcpProtocolParser)` → `WithTcpParser(IResponseParser)`；`WithRtuParser(RtuProtocolParser)` → `WithRtuParser(IResponseParser)`（字段类型同步修改）。
- 新增 `WithCreator(IModbusClientCreator creator)`：`null` 抛 `ArgumentNullException`；保存到列表。
- `Build()`：`creators = CreateDefaultCreators(...)` 之后追加 `WithCreator` 添加的创建器（因此同一 `ConfigType` 的自定义创建器覆盖默认创建器）；调用 `new ModbusFactory(logger, creators, connectionManager ?? new ModbusConnectionManager(NullLogger<ModbusConnectionManager>.Instance))`。同时消除当前 `Build()` 的编译警告 CS8604。

`ModbusServiceCollectionExtensions.AddModbusFactory` 的修改：
- 保留现有的 `ILoggerFactory`、`ILogger<ModbusFactory>`、`IModbusPduValidator`（Task 3）、`IModbusFrameBuilder`、`TcpProtocolParser`、`RtuProtocolParser`、`IModbusConnectionManager`、`IModbusFactory` 注册。
- 新增两条注册（使用 `AddSingleton`，不使用 `TryAdd`）：
  ```csharp
  services.AddSingleton<IModbusClientCreator>(sp => new TcpClientCreator(
      sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<TcpProtocolParser>(), sp.GetRequiredService<IModbusFrameBuilder>()));
  services.AddSingleton<IModbusClientCreator>(sp => new RtuClientCreator(
      sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<RtuProtocolParser>(), sp.GetRequiredService<IModbusFrameBuilder>()));
  ```
- 约定：自定义创建器必须在 `AddModbusFactory()` **之后**注册，才能覆盖内置创建器（后注册者生效）。

### 5.2 步骤

1. 特征测试：确认 `ModbusFactoryTests` 中涉及工厂的现有测试（`TryAdd_*`、`TryGet_*`、`TryRemove_*`、`GetOrAdd_*`、`Builder_*`、`AddModbusFactory_*`）在当前代码上通过。
2. 新增 5.3 的测试 `FactoryStrategyTests`，运行，确认编译失败或失败（接口尚不存在）。
3. 新增三个创建器文件；修改 `IModbusFactory`、`ModbusFactory`、`ModbusFactoryBuilder`、`ModbusServiceCollectionExtensions`。
4. 运行 5.3 的测试和全量测试。现有测试中 `factory.TryAdd("k", (ModbusTcpClientConfig)null!, out _)`、`factory.TryAdd("", new ModbusTcpClientConfig(), out _)`、`factory.TryAdd("key", new ModbusRtuClientConfig { PortName = "" }, out _)` 会绑定到新的 `IModbusConfig` 重载，行为必须与改动前一致（分别抛出 `ArgumentNullException`、`ArgumentException`、`ArgumentException`）。
5. 编译 `Junevy.Communication.Test`（WPF 示例）。`factory.GetOrAdd("test", new ModbusTcpClientConfig())` 绑定到新重载，不需要修改示例。
6. 清理：全仓库搜索 `ValidateAndFillDefaults`，结果必须为 0 处。

### 5.3 测试（类 `FactoryStrategyTests`）

测试内定义：`private sealed class FakeConfig : IModbusConfig`（实现 `IModbusConfig` 的 5 个属性）、`private sealed class DerivedTcpConfig : ModbusTcpClientConfig`、`private sealed class TrackedModbus : IModbus`（其余成员抛 `NotSupportedException`，`Dispose()` 计数）、`private sealed class FakeCreator : IModbusClientCreator`（`ConfigType` 由构造参数给定；`Create` 返回 `TrackedModbus` 并记录调用；`Normalize` 记录调用次数）。

| 测试名 | 动作 | 断言 |
|---|---|---|
| `GetOrAdd_TcpConfig_CreatesTcpClient` | `new ModbusFactory().GetOrAdd("k", new ModbusTcpClientConfig())` | 返回值 `is ModbusTcpClient` |
| `GetOrAdd_RtuConfig_CreatesRtuClient` | `GetOrAdd("k", new ModbusRtuClientConfig { PortName = "COM1" })` | 返回值 `is ModbusRtuClient` |
| `GetOrAdd_UnknownConfigType_ThrowsNotSupported` | `GetOrAdd("k", new FakeConfig())` | 抛出 `NotSupportedException`，消息含 `FakeConfig` |
| `GetOrAdd_DerivedConfig_UsesBaseTypeCreator` | `GetOrAdd("k", new DerivedTcpConfig())` | 返回值 `is ModbusTcpClient` |
| `GetOrAdd_CustomCreator_IsUsed` | 工厂构造时传入 `FakeCreator(typeof(FakeConfig))`；`GetOrAdd("k", new FakeConfig())` | 返回 `TrackedModbus`；`Normalize` 被调用 1 次；`Create` 被调用 1 次 |
| `GetOrAdd_SameKeyTwice_CreatesOnce_NormalizesTwice` | 同一个 `FakeConfig` 连续 `GetOrAdd("k", ...)` 两次 | `Create` 调用 1 次；`Normalize` 调用 2 次；两次返回同一实例 |
| `Creators_SameConfigType_LaterWins` | 传入两个 `ConfigType == typeof(FakeConfig)` 的 `FakeCreator`（A 在前，B 在后） | 创建出的实例来自 B |
| `Ctor_NullCreators_Throws` | `new ModbusFactory(logger, null!, manager)` | 抛出 `ArgumentNullException` |
| `TryAdd_CustomCreator_DuplicateKey_DisposesNewInstance` | `TryAdd` 两次相同 key | 第二次返回 `false`；第二次创建的 `TrackedModbus.DisposeCount == 1` |
| `Builder_WithCreator_OverridesDefault` | `ModbusFactoryBuilder.Create().WithCreator(new FakeCreator(typeof(ModbusTcpClientConfig))).Build()`，`GetOrAdd("k", new ModbusTcpClientConfig())` | 返回 `TrackedModbus` |
| `Builder_WithTcpParser_AcceptsAnyResponseParser` | `WithTcpParser(Mock.Of<IResponseParser>())` 通过编译，`Build()` 后 `GetOrAdd` TCP 配置成功 | 返回值 `is ModbusTcpClient` |
| `Di_CustomCreatorRegisteredAfterAddModbusFactory_Wins` | `services.AddModbusFactory(); services.AddSingleton<IModbusClientCreator>(new FakeCreator(typeof(ModbusTcpClientConfig)));` 解析 `IModbusFactory` 后 `GetOrAdd` TCP 配置 | 返回 `TrackedModbus` |
| `Di_DefaultRegistration_CreatesBuiltInClients` | 仅 `AddModbusFactory()` | TCP、RTU 配置分别创建出 `ModbusTcpClient`、`ModbusRtuClient` |
| `Di_AddModbusFactoryTwice_StillWorks` | 调用两次 `AddModbusFactory()` | 解析工厂不抛异常，TCP 配置可创建 |
| `Interface_HasNoConcreteConfigOverloads` | 反射遍历 `typeof(IModbusFactory).GetMethods()` 的参数类型 | 没有任何参数类型是 `ModbusTcpClientConfig` 或 `ModbusRtuClientConfig` |

### 5.4 文档

- `CHANGELOG.md` `### 变更（Changed，破坏性）`：
  - `{日期} IModbusFactory.GetOrAdd / TryAdd 的 4 个按配置类型的重载（ModbusTcpClientConfig、ModbusRtuClientConfig）合并为 GetOrAdd(string, IModbusConfig) 与 TryAdd(string, IModbusConfig, out IModbus?)；调用方源码不需要修改，未注册的配置类型抛出 NotSupportedException。`
  - `{日期} ModbusFactory 的 6 个构造函数缩减为 2 个：ModbusFactory() 与 ModbusFactory(ILogger<ModbusFactory>, IEnumerable<IModbusClientCreator>, IModbusConnectionManager)。`
  - `{日期} ModbusFactoryBuilder.WithTcpParser / WithRtuParser 的参数类型由具体解析器类改为 IResponseParser。`
- `CHANGELOG.md` `### 新增（Added）`：`{日期} IModbusClientCreator（传输创建策略）、TcpClientCreator、RtuClientCreator、ModbusFactoryBuilder.WithCreator：新增一种传输（例如 RTU over TCP）只需实现 IModbusClientCreator 并注册，无需修改工厂。`
- `Skills/using-junevy-modbus/SKILL.md`：
  - 第 13 行 "Config-object clients" 之后新增：`Factory: GetOrAdd(key, IModbusConfig) / TryAdd(key, IModbusConfig, out modbus) resolve a creator by config type (base-type chain). Unknown config types throw NotSupportedException. Custom transports implement IModbusClientCreator; register it AFTER AddModbusFactory() (later registration wins) or pass it to ModbusFactoryBuilder.WithCreator.`
  - 第 3 行 description 与第 92-100 行示例不需要修改。
- `readme.md`：在 "Manual Construction Without Microsoft DI (Prism, etc.)" 小节之后新增 "Custom Transports" 小节，内容固定为：一段 5 行内的 `IModbusClientCreator` 实现示例（`ConfigType`、`Normalize`、`Create`、`Describe`），一句 `Register it after AddModbusFactory(): services.AddSingleton<IModbusClientCreator, MyCreator>();`，一句 `Without a DI container: ModbusFactoryBuilder.Create().WithCreator(new MyCreator()).Build()`。
- `Junevy.Communication.Modbus/README.md`：在 Task 5 统一同步。

### 5.5 提交

`refactor(factory)!: resolve client creation through IModbusClientCreator strategies`

## 6. Task 5：收尾与验收

1. `Junevy.Communication.Modbus.csproj`：`<Version>1.1.0</Version>` 改为 `<Version>2.0.0</Version>`。不执行 `nuget push`。
2. 在 `CHANGELOG.md` 的 `[Unreleased]` 段顶部新增小节 `### 迁移指南（1.x → 2.0）`，内容固定为下表（按未删除的 Task 保留对应行）：

   | 旧 | 新 |
   |---|---|
   | `result.IsSuccess = ...` 等对 `ModbusResult` 属性赋值 | 使用 `ModbusResult<T>.Success(...)` / `Fail(...)` |
   | `ModbusExtensions.ReadCoils(modbus, ...)` 静态调用 | `modbus.ReadCoils(...)`（扩展方法语法）或 `ModbusBitExtensions.ReadCoils(modbus, ...)` |
   | `new TcpProtocolParser(logger, verifier)` | `new TcpProtocolParser(validator, logger)` |
   | `ModbusPduVerifier` | `IModbusPduValidator` / `ModbusPduValidator` |
   | `factory.GetOrAdd(key, ModbusTcpClientConfig)`（按类型的重载） | `factory.GetOrAdd(key, config)`（`IModbusConfig` 重载，调用写法不变） |
   | `new ModbusFactory(logger, loggerFactory, tcpParser, rtuParser, frameBuilder, manager)` | `new ModbusFactory(logger, creators, manager)`；或使用 `ModbusFactoryBuilder` |
   | `ModbusFactoryBuilder.WithTcpParser(TcpProtocolParser)` | `WithTcpParser(IResponseParser)` |

3. 同步 `Junevy.Communication.Modbus/README.md`：补入 "Custom Transports" 与 "Custom response validation" 两个小节（文字与根 `readme.md` 相同），"Target Frameworks" 保持 `net472` 与 `net8.0`。
4. 检查 `Skills/using-junevy-modbus/SKILL.md` 的全部内容与 2.0 API 一致：搜索 `ModbusPduVerifier`、`ModbusExtensions`、`new ModbusFactory(`、`ValidateAndFillDefaults`，结果必须为 0 处。
5. 运行计划一第 0 节的三条命令，全部成功；再运行 `dotnet build Junevy.Communication.Test/Junevy.Communication.Test.csproj -c Debug`，成功。
6. 全量回归：连续运行 3 次完整测试，3 次结果一致（允许计划一 Task 0 记录的已知偶发用例按其判定规则处理）。
7. 验收脚本（scratchpad 控制台项目，`net8.0`）：
   1. 通过 `ModbusFactoryBuilder.Create().Build()` 创建工厂，对本机 Modbus TCP 测试服务端（`ScriptedTcpServer` 风格，自行实现回显）依次执行 15 个功能码的读写，全部 `IsSuccess == true`。
   2. 注册一个自定义 `IModbusClientCreator`（`ConfigType` 为自定义配置类），`GetOrAdd` 返回自定义客户端。
   3. 对 `ModbusResult<T>` 反射检查，无公开 setter。
8. 把脚本输出粘贴到合并请求描述中。
9. 提交：`chore: bump version to 2.0.0 and add migration guide`。

## 7. 未纳入本计划的条目（只记录，不修改）

- `ModbusFactory.ValidateAndFillDefaults` 改写调用者配置对象（逻辑已迁入 `IModbusClientCreator.Normalize`，行为保持不变）。
- `ModbusRequest.TransactionId` 被基类回写。
- `IModbusFactory` 同时承担"创建"与"注册表"两项职责的拆分。
- `ModbusFactory.Dispose` 释放通过 `WithConnectionManager` 传入、并不归它所有的管理器。
- 扩展方法返回 `ValueTask` 与接口返回 `Task` 不一致。
- `ModbusTransportBase` 中以 `isAsync` 布尔参数区分的消息/日志钩子的收敛。
- 日志热路径 `ToHex()` 提前求值。
