# 响应解析与PDU校验

<cite>
**本文引用的文件**
- [Junevy.Communication.Modbus/Core/Parsing/TcpProtocolParser.cs](file://Junevy.Communication.Modbus/Core/Parsing/TcpProtocolParser.cs)
- [Junevy.Communication.Modbus/Core/Parsing/RtuProtocolParser.cs](file://Junevy.Communication.Modbus/Core/Parsing/RtuProtocolParser.cs)
- [Junevy.Communication.Modbus/Core/Parsing/ModbusPduValidator.cs](file://Junevy.Communication.Modbus/Core/Parsing/ModbusPduValidator.cs)
- [Junevy.Communication.Modbus/Core/Interfaces/IModbusPduValidator.cs](file://Junevy.Communication.Modbus/Core/Interfaces/IModbusPduValidator.cs)
</cite>

**职责分层（2026-10-08 P3 重构后）**：解析器只管"**解帧**"，PDU 语义校验全部交给 `IModbusPduValidator`。两个解析器都实现 `IResponseParser`（`ParseResponse(ReadOnlyMemory<byte>, ModbusRequest)`），由客户端构造参数或工厂注入，**可整体替换**；校验规则也可单独替换（`new TcpProtocolParser(validator, logger)`）。

| 层 | 职责 | 可替换方式 |
|---|---|---|
| `IResponseParser` | 定位帧边界（TCP：MBAP；RTU：扫描+CRC） | 客户端第 3 构造参数 / `WithTcpParser` / `WithRtuParser` |
| `IModbusPduValidator` | 期望长度、功能码一致性、字节数、回显字段 | `new TcpProtocolParser(validator)` / DI 注册 `IModbusPduValidator` |

旧类 `ModbusPduVerifier`（公开类但方法全是 `internal`，外部无法使用或扩展）已删除，由 `IModbusPduValidator` / `ModbusPduValidator` 取代。

## TcpProtocolParser（TCP 解帧）

输入完整 ADU（含 6 字节 MBAP 头）。**最小合法帧 9 字节**（6 头 + UnitId + FC + ≥1）。校验顺序：

1. 空帧 / 请求非法 → `ProtocolViolation`；
2. 帧长 < 9 → `ProtocolViolation`（Data 带原帧）；
3. `ProtocolId != 0` → `ProtocolViolation`；
4. **TransactionId 精确匹配**（`==`，不是 ±1 旧约定）；
5. UnitId == request.SlaveId；
6. `6 + Length字段 <= 实际帧长`，不足 Fail；
7. 切出 PDU（`response.Slice(7, totalLength-7)`），长度 < 1 → Fail；
8. `n = validator.GetExpectedPduLength(pdu.Span, request)`；`n < 0` 或 `pdu.Length < n` → Fail；
9. `v = validator.Validate(pdu, request)`：
   - `v.ErrorKind == ModbusException` → `Fail(v.ErrorMessage, ModbusException, response.Slice(0, totalLength))`
   - 其他失败 → `Fail(v.ErrorMessage, ProtocolViolation, response)`
   - 成功 → `Success(response.Slice(0, 7 + n))` —— **裁剪到精确期望长度**，容忍粘包时尾部多出的下一帧字节。

## RtuProtocolParser（RTU 解帧）

输入累计接收缓冲（可能含噪声/粘包）。核心是**逐字节扫描**：

```text
offset = 0
while (offset + 5 <= 缓冲长):
    SlaveId 不符 → 记警告，offset++（跳过噪声字节）
    n = validator.GetExpectedPduLength(帧.Slice(1), request)
    n < 0                    → Fail("too short", Data=剩余帧)（不跳过，调用方继续读）
    total = 1 + n + 2(CRC)
    帧长 < total             → Fail("too short. Expected {total}, actual {len}.")(不跳过)
    v = validator.Validate(candidate.Slice(1, n), request)
    !v.IsSuccess && 非 ModbusException → offset++ 继续扫（可能只是从噪声中间开始）
    CRC 不过                   → offset++ 继续扫
    v.IsSuccess → Success(完整候选帧，含 CRC)
    v 是 ModbusException → Fail(v.ErrorMessage, ModbusException, 候选帧)
扫描到底仍未匹配 → Fail("Failed to match response.", ProtocolViolation, 整段)
```

要点：

- **CRC 是唯一裁决**：PDU 校验过了但 CRC 错 → 当噪声跳字节继续扫（不把 CRC 噪声误报为协议错误，也不误报为从站异常）。
- **"长度不足"不跳字节**：返回失败并把剩余帧放进 `Data`，客户端据此等 `FrameReadInterval` 再读（Read-until-frame），凑齐后重试。
- 请求非法（`CheckRequest` false）→ Fail，不扫描。

## ModbusPduValidator（PDU 语义校验）

### GetExpectedPduLength（期望 PDU 总长，从功能码字节起算）

`pduPrefix.Length == 0` → `-1`；`pduPrefix[0] & 0x80`（异常响应）→ `2`。否则按 `request.FunctionCode`：

| 功能码 | 期望 PDU 长度 | 需要的前缀字节数 |
|---|---|---|
| 0x01、0x02、0x03、0x04、0x17、0x0C、0x11 | `2 + pduPrefix[1]`（功能码 + 字节数 + 数据） | 2（不足返回 -1） |
| 0x05、0x06、0x0F、0x10 | 5 | 1 |
| 0x16 | 7 | 1 |
| 0x07 | 2 | 1 |
| 0x08 | `1 + request.Data.Length` | 1 |
| 0x0B | 5 | 1 |

### Validate（固定校验顺序）

1. `pdu.Length < 1` → `ProtocolViolation`；
2. `pdu[0] & 0x80`（异常响应）：若 `pdu[0] != request.FunctionCode | 0x80` → `ProtocolViolation`（消息含期望与实际功能码）；否则 `pdu.Length < 2` → `ProtocolViolation`；否则 → `ModbusException`，消息固定为 `"Modbus exception response. Function=0xXX, Code=0xYY."`；
3. **`pdu[0] != request.FunctionCode` → `ProtocolViolation`**——⚠️ 这是 2026-10-08 P3 唯一的行为变更（D1）。此前解析器**不校验响应功能码**，可能把别的功能码的应答当成本次应答接受（差分测试中"帧前追加另一个功能码的响应帧"变体直接暴露了这点）；
4. `pdu.Length < GetExpectedPduLength(...)`（或其返回 -1）→ `ProtocolViolation`（消息含 `too short`）；
5. 按功能码校验内容：
   - 0x01、0x02：`pdu[1] == (request.Quantity + 7) / 8`
   - 0x03、0x04、0x17：`pdu[1] == request.Quantity * 2`
   - 0x05、0x06：地址 `== request.StartAddress`，且 `pdu[3..5]` 与 `request.Data[0..2]` 逐字节相等
   - 0x0F、0x10：地址 `== request.StartAddress`，数量（`pdu[3..5]` 大端）`== request.Quantity`
   - 0x16：地址 `== request.StartAddress`，`pdu[3..5]` 与 `request.Data[0..2]` 相等，`pdu[5..7]` 与 `request.Data[2..4]` 相等
   - 0x07、0x08、0x0B、0x0C、0x11：不做内容校验
6. 成功：返回 `Success(pdu.Slice(0, 期望长度))`。

校验失败记 Warning 日志（含期望值 vs 实际值），成功记 Debug。**校验器不记录 RX 日志**——RX 日志只由客户端 `ModbusTransportBase.LogRx` 记录（消除了 RTU 成功帧的重复 RX 日志）。

## 扩展层二次解析

解析器产出的是**帧级**结果；扩展方法（`ModbusBitExtensions` / `ModbusRegisterExtensions` / `ModbusDiagnosticsExtensions`，内部委托 `ModbusRequestExecutor` + `ModbusPayloadCodec`）再做语义解析：`ModbusHelper.ParseCoils`/`ParseRegisters`（从 PDU 偏移 3 起解位/寄存器）、0x0B/0x0C 结构体、0x11 ByteCount 负载。两层职责划分：**解析器管"帧对不对"，扩展层管"数据是什么"**。

## 可替换场景示例

- 某厂商从站对 0x03 响应多送 2 字节诊断尾巴 → 自定义 `IResponseParser` 包装内置 `TcpProtocolParser`，在其结果上截尾；或完全自写。
- 只想放宽/收紧某条 PDU 规则（例如该厂商 0x03 的 ByteCount 不可信）→ 自定义 `IModbusPduValidator`，不需要重写解帧逻辑。

工厂层 `WithTcpParser` / `WithRtuParser` 注入一次即对该工厂创建的所有客户端生效。

## 回归保障

- `ParserSpecTests`：TCP/RTU 合法帧返回完整帧、RTU 前导噪声跳过、坏 CRC 失败、部分帧 `ProtocolViolation` 且不跳过、D1 行为、异常响应的消息与 Data 长度、事务 ID / 从站号 / 协议 ID 不符、全部截断与取反变体下两个解析器均不抛异常。
- `ModbusPduValidatorTests`：期望长度表逐行、`-1` 情形、异常响应、功能码不一致（D1）、异常功能码错误、字节数不符、0x05/0x06/0x0F/0x10/0x16 的地址与值不符、合法 PDU 精确切片。
- `ParserCorpus`：16 个语料项（15 个功能码的合法响应 + 1 个异常响应）+ `WrapTcp` / `WrapRtu`。

⚠️ 语料里的请求必须是**合法请求**（能通过 `ModbusHelper.CheckRequest`），否则解析器直接返回"请求非法"，该语料项在测试中形同虚设——2026-10-08 曾因 0x17 的 `Data` 布局写错而发生。