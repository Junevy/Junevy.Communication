# 帧格式与CRC

<cite>
**本文引用的文件**
- [Junevy.Communication.Modbus/Core/Framing/ModbusFrameBuilder.cs](file://Junevy.Communication.Modbus/Core/Framing/ModbusFrameBuilder.cs)
- [Junevy.Communication.Modbus/Utils/Crc16Helper.cs](file://Junevy.Communication.Modbus/Utils/Crc16Helper.cs)
- [Junevy.Communication.Modbus/Utils/ModbusHelper.cs](file://Junevy.Communication.Modbus/Utils/ModbusHelper.cs)
- [Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs](file://Junevy.Communication.Modbus.Tests/ModbusProtocolTests.cs)
</cite>

## 目录
1. [TCP ADU（MBAP）](#tcp-adumbap)
2. [RTU ADU](#rtu-adu)
3. [每功能码的帧长表](#每功能码的帧长表)
4. [CRC16](#crc16)
5. [构建 API 与缓冲约定](#构建-api-与缓冲约定)

## TCP ADU（MBAP）

标准 MBAP 头 7 字节（TID 2 + PID 2 + Length 2 + UnitId 1），后接功能码负载。**实现上拆为"6 字节头 + 从 UnitId 起的 PDU"**（与传输层读帧方式一致）：

| 字段 | 长度 | 写入值 |
|---|---|---|
| TransactionId | 2 | 大端 `request.TransactionId`（传输调用由客户端自增分配） |
| ProtocolId | 2 | 恒 `0x0000`（Modbus 协议） |
| Length | 2 | 大端 `RTU帧长 - 2`（即不含 CRC 的 PDU 长度） |
| UnitId | 1 | `request.SlaveId` |
| PDU | n | 功能码负载 |

TCP ADU 总长 = RTU 帧长 + 4（减 CRC 加 MBAP）。响应侧：解析器校验 ProtocolId==0、TID 精确相等、UnitId 相等、`6 + Length <= 帧长`。

## RTU ADU

```text
[SlaveId(1)] [FunctionCode(1)] [Data(n)] [CRC16 低字节(1)] [CRC16 高字节(1)]
```

- CRC 是**小端**在线（低字节在前）——`Crc16Helper.CrcLittleEndian`。
- 发送前传输层 `DiscardInBuffer/DiscardOutBuffer`（帧间噪声清理）。
- 帧间静默（3.5 字符时间）由"Read-until-frame + CRC 裁决"策略替代，未实现显式 T3.5 计时。

## 每功能码的帧长表

`ModbusFrameBuilder.GetRtuFrameLength`（私有，逻辑权威）：

| 功能码 | RTU 帧长 | 说明 |
|---|---|---|
| 0x01–0x04 读类 | 8 | SlaveId+FC+Start(2)+Qty(2)+CRC(2) |
| 0x05 / 0x06 单写 | 8 | SlaveId+FC+Start(2)+Data(2)+CRC |
| 0x07 / 0x0B / 0x0C / 0x11 | 4 | 仅 SlaveId+FC+CRC |
| 0x08 Diagnostics | `4 + Data.Length` | SubFunction 在 Data 里 |
| 0x0F / 0x10 多写 | `9 + Data.Length` | +Start(2)+Qty(2)+ByteCount(1)+Data |
| 0x16 MaskWrite | 10 | +Start(2)+And(2)+Or(2) |
| 0x17 ReadWrite | `4 + Data.Length` | Data 含 0x17 完整 PDU 余部 |

TCP ADU 长 = 上表 + 4。多写 ByteCount 字节：0x0F 为 `(Quantity+7)/8`、0x10 为 `Quantity*2`（`GetWriteByteCount`）。

## CRC16

Modbus RTU 标准算法（`Crc16Helper.ComputeCrc`）：

- 初值 `0xFFFF`；多项式 `0xA001`（反射 0x8005）；逐字节异或后 8 次右移迭代。
- `VerifyCrc(span)`：取**末尾 2 字节**按小端为接收值，与前 n-2 字节计算值比对；长度 <2 直接 false。
- `CrcLittleEndian(span)`：计算并输出 2 字节小端数组（构建请求/测试造帧都用它）。

## 构建 API 与缓冲约定

- `ModbusFrameBuilder.TryWriteRequestFrame(request, protocol, Span<byte>, out written)`：零分配主路径；缓冲不足/请求非法返回 false（不抛）。
- `GetRequestFrameLength`：请求非法抛 `ArgumentException`；未知功能码/协议同样抛。
- `ModbusHelper.BuildRequestFrame(request)`：**兼容入口**——协议取自 `request.ProtocolType`（唯一读取该属性的地方）；库不修改请求对象（测试 `FrameBuilder_ProtocolComesFromArgumentNotRequest` 固定：故意把 ProtocolType 设成 RTU 再按 TCP 参数构建，帧是 TCP 且请求对象未被改）。
- 常量：`MaxRtuAduLength = 256`、`MaxTcpAduLength = 260`（TCP 260 = 6 头 + 254 PDU 上限）。RTU 接收缓冲租 257（256+1），保证"恰满 256 的完整帧"不触发溢出分支（测试 `RtuFrameBuilder_MaxRtuAduLength_Is256` 锁定该约定）。

## 逐功能码 PDU 写入细节（TryWriteRtuPdu）

| 功能码 | PDU 布局（SlaveId/FC 之后） |
|---|---|
| 0x01–0x04 | Start(2 大端) + Qty(2 大端) |
| 0x05 / 0x06 | Start(2) + Data[0..2] 原样 |
| 0x07 / 0x0B / 0x0C / 0x11 | 无 |
| 0x08 | Data 原样（含 SubFunction） |
| 0x0F / 0x10 | Start(2) + Qty(2) + ByteCount(1) + Data |
| 0x16 | Start(2) + Data[0..4]（And+Or） |
| 0x17 | Data 原样（扩展层已组装完整布局） |
