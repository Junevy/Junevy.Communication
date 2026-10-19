---
tags: [协议细节, Junevy.Communication, Modbus, Plc]
---

# Modbus 协议

> 代码基线：`Junevy.Communication.Modbus`（net472;net8.0，`3.0.0-preview.1`，P7）。依据：Modbus Application Protocol V1.1b3、Modbus over Serial Line V1.02，以及计划第 21 节的黄金向量（V-P、V-R、V-M 系列，CRC 用独立脚本计算）。**未经真机验证**（P7-D17）：只与测试里的设备替身联调过，没有用 Modbus Poll、Modbus Slave 或任何真实设备核对。与代码冲突时以代码为准；设计差异见设计文档第 20.12 节。2.x 的笔记已移到 [[变更与决策/Modbus v2 归档/API参考/ModbusRtuClient]] 等处，仅供历史参考。

## 支持范围（P7-D1）

- 传输：MBAP over TCP；RTU over 串口；RTU over TCP（透传网关）；RTU over UDP（按规范实现，很少见）。
- 功能码：01、02、03、04、05、06、0F、10、16、17。
- 不做：ASCII、从站（服务端）模式、诊断（07、08、0B、0C、11）、文件记录（14、15）、FIFO（18）、设备标识（2B）、广播（站号 0）、自定义功能码与原始 PDU 入口。
- 异步 API 只有一套（P7-D13）：没有同步版本。

## 地址（P7-D4）

`[s=<站号>;]<区域><号码>[.<位>]`，大小写不敏感。

| 写法 | 例 | 含义 |
|---|---|---|
| 符号（零基） | `HR100`、`C5`、`DI7`、`IR3` | 协议地址 100 的保持寄存器；区域为 `C` 线圈、`DI` 离散输入、`IR` 输入寄存器、`HR` 保持寄存器 |
| Modicon 一基（5 位） | `40101` | 保持寄存器 100；首位选区域（0 线圈、1 离散输入、3 输入寄存器、4 保持寄存器） |
| Modicon 一基（6 位） | `400101` | 同上，号码可到 65535 |
| 位 | `HR100.5` | 寄存器 100 的第 5 位（第 0 位最低）；只允许在 `IR`、`HR` 上使用，位号 0–15 |
| 站号前缀 | `s=2;HR100` | 本次请求发往站号 2（0–255） |

- 号码为十进制，0–65535；地址不能越过 65535。
- 没有前缀时使用 `ModbusClientOptions.UnitId`（默认 1）。RTU 的站号必须在 1–247（0 是广播，不支持；248–255 保留）。
- 站号前缀与一基写法超出了声明式解析器的表达力（P7 摩擦记录 F-A1），所以 Modbus 自写解析器（`ModbusAddressParser`，内部类型）。

## 功能码选择与单包上限

| 操作 | 功能码 | 说明 |
|---|---|---|
| 读字（`HR` / `IR`） | 03 / 04 | |
| 读位（`C` / `DI`） | 01 / 02 | |
| 写字（`HR`） | 06（单字）或 10 | `UseSingleWriteFunctions` 为 true 时，单字用 06 |
| 写位（`C`） | 05（单点）或 0F | 同上 |
| 写寄存器的位 | 16（掩码写） | `UseMaskWriteForBits` 为 true 时使用；否则 `AllowReadModifyWriteBits` 为 true 时读-改-写；都不开则 `NotSupported` |
| 写 `IR` / `DI` | — | `InvalidRequest`，不发送任何数据 |
| 读写多个寄存器 | 17 | `ReadWriteMultipleRegistersAsync`，不拆包 |

单包上限（超出时自动拆包；写单字 / 单点的规则按包判断，124 个字的写入发 123 个字（10）加 1 个字（06））：

| 项 | 上限 |
|---|---|
| 读寄存器 | 125 |
| 读位 | 2000（0x7D0） |
| 写寄存器 | 123（0x7B） |
| 写线圈 | 1968（0x7B0） |
| 掩码写 | 每次一个位，不拆包 |
| 0x17 | 读 125、写 121（242 字节） |

## 帧格式

### MBAP（TCP）

| 字段 | 长度 | 说明 |
|---|---|---|
| 事务号 | 2 | 进程级 `SequenceCounter.NextUInt16()`，写 MBAP 头时分配，请求与心跳共用 |
| 协议号 | 2 | 必须为 0，否则 `ProtocolViolation` |
| 长度 | 2 | 站号 + PDU 的字节数 |
| 站号 | 1 | |
| PDU | — | 功能码 + 数据 |

分帧：套件的 LengthField，偏移 4、宽 2、大端、修正值 0，`MaxFrameLength` 260（[Framing/ModbusStreamFraming.cs](file://Junevy.Communication.Modbus/Framing/ModbusStreamFraming.cs)）。关联：`CorrelationMode.Keyed`，键提取器 `ModbusTransactionKeyExtractor` 取第 0–1 字节（P7-D9）。超时后到达的迟到应答因为事务号不匹配被丢弃，**不需要重建 TCP 连接**。

### RTU（串口、RTU over TCP）

| 字段 | 长度 | 说明 |
|---|---|---|
| 站号 | 1 | |
| PDU | — | |
| CRC-16/MODBUS | 2 | 低字节在前（[Protocol/ModbusCrc.cs](file://Junevy.Communication.Modbus/Protocol/ModbusCrc.cs)） |

应答的帧长由功能码与字节数推出，不依赖时序（P7-D8）：

| 功能码 | 应答总长 | 请求总长（替身） |
|---|---|---|
| 01、02、03、04 | 3 + 字节数（第 2 字节）+ 2 | 8 |
| 05、06 | 8 | 8 |
| 0F、10 | 8 | 7 + 字节数（第 6 字节）+ 2 |
| 16 | 10 | 10 |
| 17 | 3 + 字节数（第 2 字节）+ 2 | 11 + 字节数（第 10 字节）+ 2 |
| 异常（功能码 \| 0x80） | 5 | — |
| 其他 | 丢弃 1 字节，重新同步 | — |

- 解码器（[Framing/ModbusRtuFrameDecoder.cs](file://Junevy.Communication.Modbus/Framing/ModbusRtuFrameDecoder.cs)）收全候选帧后校验 CRC；CRC 不符或功能码未知时丢弃 1 个字节后重新同步，不抛异常。`MaxFrameLength` 为 256。
- 关联：`CorrelationMode.Sequential`。串口依靠通道的迟到应答丢弃窗口；RTU over TCP 依靠 `TcpClientChannelConfig.ResetOnRequestTimeout`（默认 true）在超时后重建连接。
- 数据报（UDP）：不设分帧，一个数据报就是一帧，CRC 在应答解析时校验。

## 解析与校验（P7-D10）

构造请求时记下这一包的期望（[Protocol/ModbusExpectation.cs](file://Junevy.Communication.Modbus/Protocol/ModbusExpectation.cs)）：帧格式、站号、功能码、起始地址、数量或回显值。解析同一包的应答时使用这份期望；套件的操作锁保证一包的发与收不被打断。

| 情况 | 结果 |
|---|---|
| 异常应答（功能码 \| 0x80） | `RemoteError`，`ProtocolErrorCode` 为异常码，消息如 `Modbus exception 0x02 (Illegal Data Address) for function 0x03.` |
| MBAP 协议号不是 0，或长度与帧长不一致 | `ProtocolViolation` |
| 站号、功能码或字节数不符 | `ProtocolViolation` |
| 写应答回显不符（05 / 06 回显地址与值，0F / 10 回显地址与数量，16 回显全部） | `ProtocolViolation` |
| RTU 数据报的 CRC 不符 | `ProtocolViolation` |

`ProtocolViolation` 与超时、连接关闭一样可以按 `RetryCount` 重试（[[行为契约/错误处理与错误分类]]）。异常应答是设备的权威答复，不重试。

### 异常码

名称表只使用下列 9 个码，其他码的消息不带名称：

| 码 | 名称 |
|---|---|
| 01 | Illegal Function |
| 02 | Illegal Data Address |
| 03 | Illegal Data Value |
| 04 | Server Device Failure |
| 05 | Acknowledge |
| 06 | Server Device Busy |
| 08 | Memory Parity Error |
| 0A | Gateway Path Unavailable |
| 0B | Gateway Target Device Failed to Respond |

## 数据格式（P7-D11）

- 默认 `PlcDataFormat(WordOrder.ABCD, Encoding.ASCII)`：高字在前，字符串按线上字节顺序（每个寄存器的高字节在前）。
- 规范化布局等于线上字节（每个寄存器大端）。32 位数的字序因设备而异，用 `DataFormat` 指定（例如 `CDAB`）。
- 寄存器的位 n 是寄存器值的第 n 位，与套件 `PlcBitPacking` 的约定一致。

## 心跳（P7-D12）

`ModbusReadProbe` 向 `UnitId` 发 03 读 `HR0` 1 个字。格式正确的应答（站号与功能码匹配）即为健康，正常应答与异常应答都算：设备回了异常应答也说明链路与协议栈正常。默认关闭，由通道配置的 `Heartbeat` 启用。

## 已知问题

- ~~RTU over TCP 的重试竞争~~：**已修复**（设计文档 20.1 第 43 项，2026-10-19 通道修复）。请求超时后客户端当场离开 `Connected`，`RetryCount` 的重试等待重建的连接。
- ~~字面的 256 字节上限~~：**已修正**（P7 第一轮验收 R1）。读应答的字节数大于 250（规范上限）时视为噪声，丢 1 个字节重新同步，不再抛 `FrameDecodeException`。
- **噪声中的等待**（P7-D17 ②）：候选帧的字节数未到达时分帧器等待；噪声中的字节数字段很大时，分帧器可能等待最多 260 字节才裁决，之后的好帧会被推迟。没有超时兜底。

## 未验证清单（P7-D17）

1. 只与测试里的设备替身联调过，没有对真实设备或 Modbus Poll / Modbus Slave 这类工具验证。
2. RTU 按字节重新同步在强噪声线路上的表现未测量。
3. 0x16 的支持因设备而异，不支持的设备返回异常码 01。
4. 少数设备只接受 10 而不接受 06，需要把 `UseSingleWriteFunctions` 设为 false。
5. 站号 0 和 255 在 TCP 网关上的含义因厂商而异。
6. RTU over UDP 很少见，只按规范实现。

## 黄金向量

计划第 21 节的 V-P-1 至 V-P-15（PDU）、V-R-1 至 V-R-20（RTU，★ 标记的 5 条与公开资料一致，可用于先验证 CRC）、V-M-1 至 V-M-5（MBAP）。测试位于 `Tests/Junevy.Communication.Modbus.Tests/Protocol/`。

## 相关

- 套件与钩子：[[架构设计/Plc 地址型设备协议套件]]（S1 只读地址、S2 协议专有请求）。
- 迁移指南：`Junevy.Communication.Modbus/README.md` 的 "Migrating from 2.x"，以及 `Skills/using-junevy-modbus`。
- 设计差异：设计文档第 20.12 节。
