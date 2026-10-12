---
tags: [协议细节, Junevy.Communication, Omron, Plc]
---

# FINS 协议（FINS over TCP）

> 代码基线：`Junevy.Communication.Omron`（net472;net8.0，`1.0.0-preview.1`，P4）。依据：欧姆龙 FINS 手册的帧结构与 FINS/TCP 的公开线路资料，逐字段拼出计划第 17.2 节的黄金向量 V-F-1…V-F-9。**未经真机验证**（P4-D20）：只在替身 CPU 上跑通。与代码冲突时以代码为准；设计差异见设计文档第 20.5 节。

## 支持范围（P4-D1、P4-D2）

- 传输：FINS over TCP，端口 9600，字节流交付。**不做 FINS/UDP**（通道族的 UDP 与请求-应答关联照常可用，但本期不实现也不测试）、不做 Host Link 串口、不做 NJ/NX 的变量名（标签）访问。
- 命令：内存区读取 `0x0101`、内存区写入 `0x0102`，外加 FINS/TCP 的节点地址请求（命令 0）。
- PLC 侧设置：CPU 的 Ethernet 端口要启用 **FINS（TCP）服务**（默认端口 9600）；节点号由握手取得，不需要在 CPU 上预先给客户端分配；直连同网段时 DNA（`DestinationNetwork`）与 DA2（`DestinationUnit`）保持 0，SNA 与 SA2 固定为 0。跨网段路由不在本期范围。

## 帧结构

FINS/TCP 外壳 16 字节，之后才是 FINS 帧本体（[Protocol/FinsTcpFrame.cs](file://Junevy.Communication.Omron/Protocol/FinsTcpFrame.cs)）：

| 字段 | 宽度 | 说明 |
|---|---|---|
| 标识 | 4 | `46 49 4E 53`（"FINS"） |
| 长度 | 4 | 大端，**计长度字段之后的字节数**（因此整帧长度 = 8 + 长度） |
| 命令 | 4 | 0 = 节点地址请求、1 = 节点地址应答、2 = FINS 帧 |
| 错误码 | 4 | 非 0 为外壳层错误，`RemoteError`，消息 `FINS/TCP error 0x00000002.` |

分帧声明在 [Framing/FinsFraming.cs](file://Junevy.Communication.Omron/Framing/FinsFraming.cs)：长度字段偏移 4、宽 4、大端、修正值 0，最大帧长取默认 65536。

FINS 帧本体的头 10 字节依次为 ICF、RSV、GCT、DNA、DA1、DA2、SNA、SA1、SA2、SID（[Protocol/FinsHead.cs](file://Junevy.Communication.Omron/Protocol/FinsHead.cs)）。请求 ICF = `0x80`，应答 = `0xC0`（应答位置 `0x40` 置位）。DA1 / SA1 来自握手，DNA / DA2 / SNA / SA2 来自选项。内存区读写的参数为区域（1）+ 字地址（2）+ 位号（1）+ 数量（2）。SID 取进程级 8 位单调计数器（Core 的 `Junevy.Communication.Core.Utils.SequenceCounter`，挂在 [Protocol/FinsHead.cs](file://Junevy.Communication.Omron/Protocol/FinsHead.cs) 上的 `FinsHead.SessionIds`），客户端请求与心跳探测共用；应答解码校验 SID 与命令码回显，不一致为 `ProtocolViolation`（P4-D8）。

## 区域代码（计划 17.3 节）

| 软元件 | 字区代码 | 位区代码 | 单位 |
|---|---|---|---|
| `CIO` | `0xB0` | `0x30` | Word |
| `W` | `0xB1` | `0x31` | Word |
| `H` | `0xB2` | `0x32` | Word |
| `A` | `0xB3` | `0x33` | Word |
| `D` | `0x82` | `0x02` | Word |
| `E0_`–`EC_`（EM 第 0–C 组） | `0xA0 + n` | `0x20 + n` | Word |

表在 [FinsDeviceTable.cs](file://Junevy.Communication.Omron/FinsDeviceTable.cs)，`FinsDevice` 同时暴露 `WordAreaCode` 与 `BitAreaCode`；探测与钩子都从表里取代码，不再写一遍常量。EM 的区域代码是推导值，未经真机验证（P4-D20 ④）。

## 握手与会话（P4-D9、D18）

`ConnectAsync` 在 TCP 连通后先发一帧节点地址请求（命令 0），读回应答（命令 1）里的客户端节点号与服务端节点号（[FinsNodeHandshake.cs](file://Junevy.Communication.Omron/FinsNodeHandshake.cs)），写进不可变会话快照（[FinsSession.cs](file://Junevy.Communication.Omron/FinsSession.cs)，`Volatile` 整体替换）：

- **写入请求的 SA1 用的是应答返回的节点号，而不是请求里写的值**——请求发 0 表示"由 CPU 分配"（P4-D20 ⑥：固定客户端节点号时 CPU 究竟答什么，未验证）。
- `FinsClient.ClientNode` 与 `FinsClient.ServerNode` 是 `byte?`，**握手之前为 null**；重连后可能变化（CPU 重新分配），要读这两个属性而不是沿用配置里的数字。
- 选项 `ClientNode` 默认 0（自动分配）、1–254 为固定值、255 是 FINS 的广播地址，构造时抛 `ArgumentOutOfRangeException`。`DestinationNetwork`（DNA）与 `DestinationUnit`（DA2）默认 0。
- 尚未取得路由时（绕过 `FinsClient.Create` 自己搭通道，或在连接前调用），操作返回 `Unspecified`，消息 `The FINS route is unavailable because the node address handshake has not run on this channel.`，且不发帧。
- 握手失败的三种分类：结构错误 `ProtocolViolation`、外壳错误码非 0 `RemoteError`、通道失败原样返回——三者都不写会话，所以上面那条"无路由"的结果会一直持续。

## 地址（P4-D16）

`D100`、`CIO0.00`、`W10.03`、`E0_20.0`：前缀 + **单个十进制编号** + 可选十进制位号（0–15），因此 18 个符号（含 EM 十三组）用套件的声明式解析器即可，位号进制为 10、最长前缀匹配区分 `CIO` / `E0_` / `W`。字号上限 65535（超出实际范围由 CPU 用结束码回答，客户端不猜型号范围）。

- 位号写法是十进制且**不省略前导零**也接受（`W10.03`）；非十进制位号 → `'W10.0A' is not a valid decimal bit index.`；越界 → `The bit index in address 'W10.16' must be between 0 and 15.`
- 跨过字号上限时 `ValidateRange` 返回 `InvalidRequest`：`The access on '<x>' ends at word N, beyond the address field limit 65535.`

## 位访问是原生能力（P4-D6）

FINS 的字软元件支持原生位读写：`FinsClient` 覆写 `CanWriteBitsNatively` 为 `true`，`WriteBitsAsync("W10.03", …)` 直接走位写命令，绝对位号 = `字号 × 16 + 地址的位号（没有位号视为 0）+ 包内偏移`，位数据**每点 1 字节**（`01` / `00`），因此不需要 `AllowReadModifyWriteBits`，也不会有与 PLC 程序竞争整字的问题。读位用位区代码，同样每点 1 字节。

## 单包上限（P4-D12）

| 请求 | 点数 |
|---|---|
| 读字 / 读位 | 999 |
| 写字 / 写位 | 996 |

由 FINS 帧 2012 字节上限推得，写入取保守值。**996 是推导值，未经真机验证（P4-D20 ③）。** 超出上限的操作由套件拆包，逐包各自重试。

## 数据格式与字符串

字内部大端、32 位值的低字在低地址，即默认 `CDAB` + ASCII（P4-D11）；规范化布局就是 CPU 内存的字节顺序（每字高字节在前）。FINS 没有带长度头的字符串类型，只用套件的 `ReadStringAsync` / `WriteStringAsync`：字符占字的高字节，按内存顺序取字节即得到 CPU 显示的顺序；字软元件上的奇数长度写入补一个 `0x00`。`ReadByteAsync("D100")` 读覆盖字并返回规范化布局的**首字节，即字的高字节**（MELSEC 的 DCBA 返回的是低字节）。

## 结束码（P4-D14）

- 应答末 2 字节是结束码：主码 & 次码。**主码的第 7 位（`0x80`）与次码的高 2 位（`0xC0`）是 CPU 状态标志，不参与错误判定**：屏蔽规则为主码 `& 0x7F`、次码 `& 0x3F`，屏蔽后为 0 即成功（原码非 0 时记 `Debug`：`FINS end code 0x0040 succeeded after masking the status flags.`），否则 `RemoteError`、`ProtocolErrorCode` 取屏蔽后的码，消息形如 `FINS end code 0x1103.`。屏蔽规则本身未经真机验证（P4-D20 ⑤）。
- 不做结束码说明表（P2-D12）：`0x1103`、`0x1102` 一类码的含义随 CPU 型号与手册版本不同，编造会误导现场排查。
- 外壳错误码与结束码是两层：外壳错误说明 FINS/TCP 层就失败了（`FINS/TCP error 0x00000002.`），结束码说明 CPU 处理了命令但失败。

## 心跳与重连（P4-D15）

[FinsReadProbe.cs](file://Junevy.Communication.Omron/FinsReadProbe.cs) 读 `D0` 的 1 个字，收到格式正确的应答即判健康（结束码不参与判定）。请求超时或连接中断后通道重建，**重新握手会重新取得节点号**；静默设备 + 心跳失效时状态进入 `Reconnecting`（端到端用例覆盖）。

## 代码与测试位置

- 协议层（无通道依赖）：[Junevy.Communication.Omron/Protocol/](file://Junevy.Communication.Omron/Protocol/)（外壳帧、帧头、内存区、节点地址、字段编码、结束码、SID）。
- 客户端：[FinsClient.cs](file://Junevy.Communication.Omron/FinsClient.cs)、[FinsClient.Hooks.cs](file://Junevy.Communication.Omron/FinsClient.Hooks.cs)、[FinsProtocolProfile.cs](file://Junevy.Communication.Omron/FinsProtocolProfile.cs)。
- 依赖注入：[DependencyInjection/](file://Junevy.Communication.Omron/DependencyInjection/)（`FinsClientConfig` 只有 `Tcp` 与 `Options`，没有传输开关；`AddOmronFins()`；JSON 段名 `"Omron"`；`Tcp` 为 null 时创建器抛 `ArgumentException`）。
- 测试：`Tests/Junevy.Communication.Omron.Tests/`。黄金向量逐字节核对；替身 `FinsDeviceSimulator` 踩在共享测试库 `PlcMemory` 的**字存储**上，位读写通过对覆盖的字做读-改-写实现（因此位与字共用同一份物理内存，表达不了真机上"位区与字区各自独立"的差异——本期没有用例依赖它）；真实套接字的端到端与故障用例在 `FinsEndToEnd*Tests.cs`，标注 `SocketTiming` 集合串行执行。

## 未验证项（P4-D20）

1. 写入上限 996 点由 2012 字节帧上限保守推导。
2. `EM` 的区域代码（字 `0xA0 + 组号`、位 `0x20 + 组号`，组 0–C）与展开成 13 个符号的写法。
3. 结束码的屏蔽规则（主码 `& 0x7F`、次码 `& 0x3F`，屏蔽后为 0 即成功）。
4. 固定客户端节点号时 CPU 对节点地址请求的应答内容——客户端一律遵循应答而不是请求值。

四项都写在 README 与代码注释中，待真机验证。

相关笔记：[[架构设计/Plc 地址型设备协议套件]]、[[协议细节/S7 协议]]、[[协议细节/MELSEC MC 协议]]、[[Agent协作/新增协议指南]]。
