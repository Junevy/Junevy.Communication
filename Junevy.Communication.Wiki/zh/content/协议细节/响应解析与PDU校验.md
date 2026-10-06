# 响应解析与PDU校验

<cite>
**本文引用的文件**
- [Junevy.Communication.Modbus/Core/Parsing/TcpProtocolParser.cs](file://Junevy.Communication.Modbus/Core/Parsing/TcpProtocolParser.cs)
- [Junevy.Communication.Modbus/Core/Parsing/RtuProtocolParser.cs](file://Junevy.Communication.Modbus/Core/Parsing/RtuProtocolParser.cs)
- [Junevy.Communication.Modbus/Core/Parsing/ModbusPduVerifier.cs](file://Junevy.Communication.Modbus/Core/Parsing/ModbusPduVerifier.cs)
</cite>

两个解析器都实现 `IResponseParser`（`ParseResponse(ReadOnlyMemory<byte>, ModbusRequest)`），由客户端构造参数或工厂注入，**可整体替换**以适配非标准从站。共享校验逻辑在 `ModbusPduVerifier`（DI 中是单例）。

## TcpProtocolParser（TCP 响应）

输入完整 ADU（含 6 字节 MBAP 头）。**最小合法帧 9 字节**（6 头 + UnitId + FC + ≥1）。校验顺序：

1. 空帧 / 请求非法 → `ProtocolViolation`；
2. `ProtocolId != 0` → Fail（`ProtocolViolation`）；
3. **TransactionId 精确匹配**（`==`，不是 ±1 旧约定；测试 `TcpParser_TransactionIdMustMatchExactly` 固定 TID=7 收到 8 必须失败）；
4. UnitId == request.SlaveId（测试 `TcpParser_SlaveMismatch`）；
5. `6 + Length字段 <= 实际帧长`，不足 Fail；
6. **异常响应**：`funcCode == request.FunctionCode | 0x80` → 检查异常码字节 → `Fail(ModbusException, 消息含 Code=0xXX)`，Data 携带整帧；
7. 特例 **0x16 MaskWrite**：响应是请求回显（Start+And+Or），用 `VerifyMaskWritePdu` 校验；
8. 0x07/0x08/0x0B/0x0C/0x11 **直接放行**（长度由传输层分帧保证，扩展层再按需解析）；
9. 其余按 `FunctionCodeCategory` 分发到 Read / WriteSingle / WriteMulti 校验（见下）。

成功返回**裁剪到声明长度**的帧切片（`response.Slice(0, 6+Length)`）——容忍粘包时尾部多出的下一帧字节。

## RtuProtocolParser（RTU 响应）

输入累计接收缓冲（可能含噪声/粘包）。**最小合法帧 5 字节**。核心是**逐字节扫描**：

```text
offset = 0
while (offset + 5 <= 缓冲长):
    SlaveId 不符 → offset++（跳过噪声字节）
    FC|0x80 候选 → 5 字节异常帧：CRC 通过→Fail(ModbusException) 终态；CRC 不过→offset++ 继续扫
    常规 FC → 按类别算期望长度：
        读类     3 + ByteCount + 2(CRC)
        单写     8
        多写     8
        0x16     10
        0x07     5   0x0B 8   0x0C 3+ByteCount+2   0x11 3+ByteCount+2
        0x08     4 + request.Data.Length
    长度不足 → Fail(ProtocolViolation, false)（不再跳字节）
    PDU 校验失败 / CRC 失败 → offset++ 继续扫
    全部通过 → Success(帧切片) 终态
扫描到底仍未匹配 → Fail("Failed to match response.", ProtocolViolation)
```

要点：

- **CRC 是唯一裁决**：候选帧 PDU 对了但 CRC 错 → 当噪声跳字节继续扫（不把 CRC 噪声误报为协议错误，也不误报为从站异常）。
- 传输层配合：解析不成功就等 `FrameReadInterval` 再读（Read-until-frame），所以"长度不足"分支在实际流里很少触发。
- 请求非法（`CheckRequest` false）→ Fail，不扫描。

## ModbusPduVerifier（共享 PDU 校验）

### FunctionCodeCategory（CategorizeFunctionCode）

| 类别 | 功能码 | 响应校验方式 |
|---|---|---|
| `Read` | 0x01–0x04、0x17 | 校验 ByteCount：寄存器类 = Qty×2，位类 = (Qty+7)/8（`VerifyReadPdu`） |
| `WriteSingle` | 0x05、0x06、**0x16** | 响应回显请求：Start 地址 + 2 字节数据逐一比对（`VerifySingleWritePdu`；0x16 走 `VerifyMaskWritePdu` 比 And/Or） |
| `WriteMulti` | 0x0F、0x10 | 响应回显：Start + 数量比对（`VerifyMultiWritePdu`） |
| `Unknown` | 0x07/0x08/0x0B/0x0C/0x11 | 不走类别分发（TCP 直接放行 / RTU 按 per-FC 长度+CRC） |

### 校验失败的处理差异

- **TCP**：PDU 校验失败 → `Fail(ProtocolViolation)`（帧已由 MBAP 定界，没有再扫描的意义）。
- **RTU**：PDU 校验失败 → `Retry=true` → **offset++ 继续扫**（可能只是从噪声中间开始了）。
- 全部校验方法失败都记 Warning 日志（含期望值 vs 实际值），成功记 Debug。

## 扩展层二次解析

解析器产出的是**帧级**结果；扩展方法（`ModbusExtensions`）再做语义解析：`ParseCoils`/`ParseRegisters`（ModbusHelper，从 PDU 偏移 3 起解位/寄存器）、0x0B/0x0C 结构体、0x11 ByteCount 负载。两层职责划分：**解析器管"帧对不对"，扩展层管"数据是什么"**。

## 可替换场景示例

某厂商从站对 0x03 响应多送 2 字节诊断尾巴 → 自定义 `IResponseParser` 包装内置 `TcpProtocolParser`，在其结果上截尾；或完全自写（参考 `ModbusPduVerifier` 的类别分发骨架）。工厂层 `WithTcpParser/WithRtuParser` 注入一次即对该工厂创建的所有客户端生效。
