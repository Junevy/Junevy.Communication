---
tags: [协议细节, Junevy.Communication, Melsec]
---

# MELSEC MC 协议

> 代码基线：`Junevy.Communication.Melsec`（net472;net8.0，`1.0.0-preview.1`，P2）。依据：MELSEC 通信协议参考手册（SH-080008）。**未经真机验证**，仅用手册示例（计划第 18 节黄金向量 V1–V10）核对；P2-D16 的两项为图示推断。

## 支持范围（P2-D2）

- 帧：3E、4E（TCP、UDP）；4C 格式 4（ASCII）与格式 5（二进制，可选和校验）（串口）。
- 命令：批量读 `0401`、批量写 `1401`（字单位子命令 `0000`，位单位子命令 `0001`）、回环测试 `0619`（心跳与人工检查）。
- 软元件：Q/L 兼容指定；表见 [Junevy.Communication.Melsec/Addressing/McDeviceTable.cs](file://Junevy.Communication.Melsec/Addressing/McDeviceTable.cs)（28 行）。
- 不做：随机读写 `0403` / `1402`、多块读写 `0406` / `1406`、远程 RUN / STOP、读 CPU 型号、iQ-R 扩展软元件指定（`0002` / `0003`）、扩展指定（`008x`）、1E / 1C / 2C / 3C 帧、4C 格式 1–3。

## 常量

| 常量 | 值 | 来源 |
|---|---|---|
| 3E 请求子头部 | `5000`（二进制 `50 00`） | [McCommands.cs](file://Junevy.Communication.Melsec/Protocol/McCommands.cs) |
| 3E 应答子头部 | `D000` | 同上 |
| 4E 请求子头部 / 应答子头部 | `5400` / `D400` | 同上 |
| 串口控制码 | ENQ `05`、STX `02`、ETX `03`、ACK `06`、NAK `15`、DLE `10` | [McSerialCodes.cs](file://Junevy.Communication.Melsec/Protocol/McSerialCodes.cs) |
| 帧识别号 | `F8` | 同上 |
| 4C 格式 5 应答中帧识别号之后的标识 | `FFFF` | 同上 |

默认访问路径：3E / 4E 二进制 `00 FF FF 03 00`，ASCII `00FF03FF00`；4C 二进制 `00 00 FF FF 03 00 00`，ASCII `0000FF03FF0000`（[McClientOptions.cs](file://Junevy.Communication.Melsec/Options/McClientOptions.cs)）。

## 以太网帧（3E / 4E）

请求：子头部，4E 另有 2 字节序列号与 `0000`；访问路径（网络号、PC 号、请求目标模块 I/O 号、站号）；请求数据长度（2 字节，含监视定时器与请求数据；ASCII 按字符计）；监视定时器（2 字节，单位 250 ms，默认 `0004` 即 1 s，P2-D15）；命令；子命令；数据。

应答：子头部，4E 另有序列号与 `0000`；访问路径（原样回显）；数据长度（含结束码）；结束码（2 字节）；数据。副头部、长度、序列号与数据长度错误为 `ProtocolViolation`；访问路径回显不校验。非 0 结束码为 `RemoteError`，`ProtocolErrorCode` 携带原始码。

- **字数据**：二进制每字 2 字节，小端；ASCII 应答的十六进制文本转换为同一规范化布局（P2-D5）。
- **位数据**：二进制每字节两点，高半字节为较小的点号；ASCII 每点一个字符 `0` 或 `1`。手册 p.72 的五点示例：二进制 `10 10 10`，ASCII `10101`。
- **软元件指定**：二进制 4 字节，前 3 字节为编号（小端），第 4 字节为软元件代码；ASCII 2 字符代码加 6 字符编号，编号按软元件的进制书写。
- **编号上限**：二进制与十六进制软元件（ASCII 时）为 `0xFFFFFF`；十进制软元件（ASCII 时）为 `999999`。超出返回 `InvalidRequest`（[McFieldCodec.cs](file://Junevy.Communication.Melsec/Protocol/McFieldCodec.cs)）。

## 4C 串口帧

**格式 4（ASCII，`Encoding.Ascii`）**：

- 请求：`ENQ` + 帧识别号（`F8`）+ 访问路径（14 字符）+ ASCII 请求数据。
- 应答：有数据时 `STX` + 帧识别号 + 数据 + `ETX` + 和校验（2 字符）；无数据时 `ACK` + 帧识别号；异常时 `NAK` + 帧识别号 + 结束码（4 字符）。
- 和校验覆盖帧识别号到 `ETX`（P2-D16 ①，未经真机验证）。CR LF 由通道的分隔符处理（[McProtocolProfile.cs](file://Junevy.Communication.Melsec/Options/McProtocolProfile.cs)）。

**格式 5（二进制，`Encoding.Binary`）**：

- 帧：`DLE STX` + 转义后的（字节数 2 字节、帧识别号、访问路径 7 字节、请求数据）+ `DLE ETX` + 和校验（2 个 ASCII 十六进制字符）。
- 字节数与和校验不含附加码。数据中的 `10H` 写成 `10H 10H`（附加码），解码时成对的 `10H 10H` 从左到右配对（[McDleFrame.cs](file://Junevy.Communication.Melsec/Protocol/McDleFrame.cs)）。
- 和校验为各字节之和的低 8 位，覆盖字节数到数据末尾（不含附加码）。
- 异常应答的 2 字节错误码按小端解析（P2-D16 ②，未经真机验证）。

分帧器：[Junevy.Communication.Melsec/Framing/Mc4CBinaryFrameDecoder.cs](file://Junevy.Communication.Melsec/Framing/Mc4CBinaryFrameDecoder.cs) 按 DLE 边界切帧；帧前垃圾、非法 DLE 序列与超过 `MaxFrameLength` 抛出 `FrameDecodeException`。

## 回环与心跳（0619）

请求：子命令 `0000`、字节数（2）、数据。应答必须逐字节回显。心跳探测（[McLoopbackProbe.cs](file://Junevy.Communication.Melsec/Connection/McLoopbackProbe.cs)）发送固定数据 `1234ABCD`，不一致为 `ProtocolViolation`。手册 p.185 的 ASCII 回环示例：`0619 0000 0005 ABCDE`，应答 `0005 ABCDE`。

## 上限（每个请求）

| 请求 | 点数 |
|---|---|
| 字读写 | 960 |
| 以太网位，二进制 | 7168 |
| 以太网位，ASCII | 3584 |
| 串口位 | 7904 |

上限由 [McLimits.cs](file://Junevy.Communication.Melsec/Protocol/McLimits.cs) 给出；`McClient` 的 `GetMaxReadPoints` / `GetMaxWritePoints` 按传输与编码选择。超出的操作由套件拆包，例如 2000 字读为 960、960、80 字。

## 结束码

- 非 0 结束码 → `RemoteError`，`ProtocolErrorCode` 为原始码，消息 `MC end code 0xC051.`。
- 不做说明表（P2-D12）：手册 p.44 指出错误码含义在各模块手册中，本手册没有说明表，编造会误导现场排查。

## 序列号与迟到应答（P2-D11）

4E 序列号来自进程级单调计数器（`Interlocked`，取低 16 位）。客户端请求与心跳探测共用计数器，同一通道上不会撞号。通道的 `Keyed` 关联丢弃迟到或错配的应答；UDP 的迟到应答被丢弃，后续请求拿到自己的应答（端到端测试 `McEndToEndUdpTests`）。

## 代码与测试位置

- 协议层（无通道依赖）：[Junevy.Communication.Melsec/Protocol/](file://Junevy.Communication.Melsec/Protocol/)（字段编码、请求体、3E/4E 帧、4C 帧、校验、应答解码）。
- 客户端：[McClient.cs](file://Junevy.Communication.Melsec/Client/McClient.cs)、[McClient.Hooks.cs](file://Junevy.Communication.Melsec/Client/McClient.Hooks.cs)、[McFrameExchange.cs](file://Junevy.Communication.Melsec/Protocol/McFrameExchange.cs)。
- 测试：[Junevy.Communication.Melsec.Tests/](file://Tests/Junevy.Communication.Melsec.Tests/)。黄金向量的单元测试在 `McEthernetFrameTests.cs` 与 `Mc4CFrameTests.cs`；端到端在 `McEndToEnd*Tests.cs`；真实串口为 `McSerialPairTests.cs`（需 `JUNEVY_SERIAL_PAIR`，默认跳过）。

## 未验证项（P2-D16）

1. 格式 4 应答的和校验范围包含 `ETX`（手册图示：格式 1–4 的范围从首个控制码之后到和校验码之前；格式 5 的图示明确排除 DLE ETX）。
2. 格式 5 异常应答的 2 字节错误码按小端解析（与其他 2 字节数值一致）。

两项都写在 README 与代码注释中，待真机验证。

相关笔记：[[架构设计/Plc 地址型设备协议套件]]、[[API参考/通道与传输（TCP UDP 串口 TLS）]]。

## P2.5 变更：创建入口与通道组件

- 唯一的创建入口为 `McClient.Create(transport, options[, loggerFactory])`；`Create(transport, creator, options[, loggerFactory])` 用于自定义传输。`CreateTcp`、`CreateUdp`、`CreateSerial` 已删除（P25-D4）。
- 通道组件由 `McProtocolProfile.Configure(components, options, delivery, medium)` 填入：字节流设置分帧，数据报不设分帧；帧类型与介质的组合在此一处校验。
- 传输配置与组件的同名设置冲突（分帧、关联）时抛出 `ArgumentException`（P25-D3）。
