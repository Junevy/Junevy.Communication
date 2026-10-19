---
tags: [架构设计, Junevy.Communication, Plc]
---

# Plc 地址型设备协议套件

> 代码基线：`Junevy.Communication.Plc`（net472;net8.0，`1.0.0-preview.1`，P2 建立、P2.5 收敛创建入口、P4 加字节粒度与字符串）。与代码冲突时以代码为准。设计依据与差异见设计文档第 12 节、第 20.2 节（P2 实施修订）与第 20.5 节（P4 实施修订）。

## 职责划分

套件负责所有"按地址读写存储区"的协议共有的部分；协议包只写协议差异。

| 归属 | 内容 | 代码位置 |
|---|---|---|
| 套件（Plc） | `IPlcClient`（含 `ParseAddress`、`ReadBytesAsync`、`WriteBytesAsync`）、`PlcClientBase<TAddress>`（请求管线与钩子契约）、类型化扩展、地址与软元件定义（三种单位）、选项、工厂、DI | [Junevy.Communication.Plc/Client/PlcClientBase.cs](file://Junevy.Communication.Plc/Client/PlcClientBase.cs)、[Junevy.Communication.Plc/Client/PlcClientBase.Hooks.cs](file://Junevy.Communication.Plc/Client/PlcClientBase.Hooks.cs)、[Junevy.Communication.Plc/Pipeline/](file://Junevy.Communication.Plc/Pipeline/)、[Junevy.Communication.Plc/Extensions/](file://Junevy.Communication.Plc/Extensions/)、[Junevy.Communication.Plc/Addressing/](file://Junevy.Communication.Plc/Addressing/) |
| 协议（MELSEC / S7 / FINS） | 地址类型与设备表（或自写解析器）、钩子（字与写位为抽象，读位、字节三钩子为可选覆写）与 `ValidateRange`、`CanWriteBitsNatively`、帧编解码与分帧声明、通道组件（分帧、关联、初始化器、心跳探测）、可选的握手与会话快照、客户端工厂、创建器、DI 扩展 | [[协议细节/MELSEC MC 协议]]、[[协议细节/S7 协议]]、[[协议细节/FINS 协议]]、[[Agent协作/新增协议指南]] |
| 公共工具（Core） | 字节序转换 `WordOrder`、`ByteTransform`（[Junevy.Communication.Core/Buffers/](file://Junevy.Communication.Core/Buffers/)） | 设计 P2-D3 |

## 请求管线

`PlcRequestPipeline` 对每个公开操作依次执行：

1. 参数验证，包括协议的 `ValidateRange`（不可表示的请求返回 `InvalidRequest`）。
2. 已释放检查：返回 `NotConnected`，消息 `The PLC client has been disposed.`。
3. 未连接检查：立即返回 `NotConnected`，不等待、不重试。
4. 获取客户端内部的操作锁。读-改-写与所有分包都在锁内完成，所以同一客户端内的操作顺序不会被打断（P2-D6）。
5. 按每次请求的单位上限拆包，逐包发送并解析；任一包失败则整体返回该失败，不返回部分数据。

边界处所有异常都收口为失败结果；调用方取消返回 `Cancelled`。公开操作不向外抛出运行期异常（P2-D14）。

## 重试（P2-D7）

- `RetryCount` 默认 `0`，快速失败；轮询循环本身就是重试。
- 重试针对单个请求（拆包时只重试失败的那一包）。
- `RemoteError`、`InvalidRequest`、`NotSupported`、`Cancelled`、`NotConnected` 不重试。
- `Timeout`、`ConnectionClosed`、`ProtocolViolation` 在 `RetryCount > 0` 且 `RetryInterval` 内 `WaitForConnectedAsync` 成功时重试，否则返回真实的 `ErrorKind`。解析期的协议违规也纳入（计划第 20 节 Task 4 审阅接受）。

## 单位与字节访问（P4-D3 的 S1–S4、P4-D4）

`PlcAddressUnit` 有三种单位，决定套件调用哪一组钩子、上限按什么计数：

| 单位 | 一个点 | 位号范围 | 钩子族 | 现有协议 |
|---|---|---|---|---|
| `Bit` | 1 位 | 不允许带位号（位软元件本身就是位） | 位钩子 | MELSEC 的 M、X、Y、SM 等 |
| `Word` | 2 个连续字节 | 0–15 | 字钩子；位读走覆盖字，位写由 `CanWriteBitsNatively` 决定 | MELSEC 的 D / W / R…、FINS 的 CIO / W / H / A / D / EM |
| `Byte` | 1 个字节 | 0–7 | 字节钩子（`offset` 与 `count` 按字节计） | S7 的 I / Q / M / DB |

- `PlcAccess` 增加 `Bytes`；`GetMaxReadPoints` / `GetMaxWritePoints` 在字节路径上返回字节数（`S7` 为协商 PDU − 18 / − 28）。
- `IPlcClient` 增加 `ParseAddress(string)`（同步、无 I/O、线程安全、释放后仍可调用，非法地址为 `InvalidRequest`，供宿主在加载配置时校验）、`ReadBytesAsync(address, count)`、`WriteBytesAsync(address, data)`；三者都不接受带位号的地址。对 `IPlcClient` 的外部实现者是破坏性变更（预览版允许）。
- 字与位软元件上的字节访问按字对齐：读取覆盖这些字节的整字（`ceil(count / 2)` 个）后返回规范化布局的前 `count` 个字节；写入要求偶数长度，否则 `InvalidRequest`。`ReadByteAsync` 返回规范化布局的首字节——对端字序决定它是字的哪一半（MELSEC 的 DCBA 是低字节、FINS 的大端字是高字节）。

## 位操作（P2-D10、P4-D6）

- 位软元件：走位钩子（MELSEC 的 M/X/Y）。
- 字中的位（`D100.5`）：**读**由套件默认实现（读整个字再取位）；**写**默认 `NotSupported`，`PlcClientOptions.AllowReadModifyWriteBits = true` 时在操作锁内读-改-写并每次记 Warning（读-改-写会与 PLC 程序竞争同一个字）。覆写 `CanWriteBitsNatively` 为 `true` 时（FINS 的字软元件）走原生位写，偏移按点计，绝对位置由协议算：`编号 × 每单位点数 + 位号（没有位号视为 0）+ 包内偏移`。
- 字节中的位（S7 的 `M12.3`）：**读**取覆盖的字节，点 `p` 在第 `p / 8` 字节的第 `p % 8` 位（位 0 是最低位）；**写**由 `CanWriteBitsNatively` 决定：S7 覆写为 `true`，走原生位写（每请求 1 位）；协议没有声明原生位写时返回 `NotSupported`，套件不为字节软元件实现读-改-写。

## 类型化读写（P2-D5、D8、D9，P4-D7 扩字符串）

- 类型化方法使用显式名称：`WriteInt16Async`、`WriteFloatAsync` 等。没有按类型重载的 `WriteAsync` 族，字面量不会绑定到错误宽度。
- 数据格式由协议选项决定（`PlcDataFormat`，null 时取协议默认）：MELSEC `DCBA` + ASCII、S7 `ABCD` + ASCII、FINS `CDAB` + ASCII。没有按调用覆盖。规范化布局等于设备内存的字节顺序，因此按内存顺序取字节即得到正确文本。
- 字符串：`ReadStringAsync(address, byteLength)` 走 `ReadBytesAsync`（字与位软元件上的结果与 P2 相同）；`WriteStringAsync(address, value)` 在字与位软元件上对奇数编码长度补一个 `0x00`，字节软元件精确写入；`WriteStringAsync(address, value, byteLength)` 是固定长度字段（超长 `InvalidRequest`、不足补 `0x00`，用来清掉短串覆盖长串后的残留字符；字与位软元件要求偶数长度）。
- 协议的私有字符串格式留在协议包：S7 的 `String` / `WString`（容量字节 + 实际长度字节、UTF-16 码字对）是 `S7Client` 的扩展方法，不在套件。
- 原始字节 `ReadAsync` / `WriteAsync` 使用协议的规范化布局，与编码方式无关。

## 钩子契约与握手（P4-D5、D8、D9）

- 抽象（所有协议必须实现）：`TryParseAddress`、两个上限方法（按 `PlcAccess` 的单位计数）、`BuildReadWordsRequest`、`ParseReadWordsResponse`、`BuildWriteWordsRequest`、`BuildWriteBitsRequest`、`ParseWriteResponse`。
- 可选覆写：`BuildReadBitsRequest` / `ParseReadBitsResponse`（表中有位软元件才需要）、`BuildReadBytesRequest` / `ParseReadBytesResponse` / `BuildWriteBytesRequest`（表中有字节软元件才需要）、`CanWriteBitsNatively`、`ValidateRange`。未覆写的可选钩子默认抛 `InvalidOperationException`，由流水线收口为 `Unspecified` 并记 Error——调用方拿到结果而不是异常，协议不必为用不到的粒度写死代码。
- 需要协商结果的协议用 `ChannelComponents.Initializer`（`IConnectionInitializer`）：每次重连都重新执行；握手结果写进不可变会话快照，用 `Volatile` 整体替换引用，钩子只读当前快照；快照由同一个 `Create` 同时交给初始化器与客户端。S7 取协商的 PDU 长度（小于 64 时握手返回 `ProtocolViolation`），FINS 取服务端与客户端节点号（连接前 `ClientNode` / `ServerNode` 为 null）。
- 没有关联键的协议（S7、FINS/TCP）用 `CorrelationMode.Sequential`：握手帧没有键，`Keyed` 模式会拒绝初始化器发出的请求；应答解码自行校验回显（PDU 引用号、SID 与命令码），不一致返回 `ProtocolViolation`。MELSEC 4E 仍用 `Keyed` + 键提取器。
- 只走单一传输的协议不提供接收已构造通道的公开构造函数，也不公开协议配置类（S7、FINS）；MELSEC 保留三个公开重载作为 `Create` 的快捷方式。

## 所有权（P2-D13）

客户端拥有通道。`Dispose` / `DisposeAsync` 释放通道，之后的操作返回 `NotConnected`。

## 工厂与依赖注入

- `PlcClientFactory`（[Junevy.Communication.Plc/Factory/PlcClientFactory.cs](file://Junevy.Communication.Plc/Factory/PlcClientFactory.cs)）按配置对象的精确运行时类型选择 `IPlcClientCreator`；没有对应创建器时抛出 `NotSupportedException`。
- `GetOrAdd` 名称已存在时返回已有实例，配置被忽略。名称与别名经 Core 的 `NamedRegistry` 管理。
- `PlcClientFactoryBuilder` 供没有 DI 容器的宿主（例如 Prism）使用，同一配置类型的两个创建器在 `Build` 时报错。
- `AddPlcClients()`（[Junevy.Communication.Plc/DependencyInjection/PlcServiceCollectionExtensions.cs](file://Junevy.Communication.Plc/DependencyInjection/PlcServiceCollectionExtensions.cs)）以 `TryAddSingleton` 注册工厂，可重复调用。协议包的 `AddX()` 通过 `TryAddEnumerable` 注册自己的创建器。

## 测试底座

- `Junevy.Communication.Testing` 提供 `PlcMemory`（线程安全的稀疏字与位存储）、`DeviceSimulator`（把请求流翻译为应答的设备端循环，可挂在 TCP、UDP 或流上）、`SequenceFactory`（分段的 `ReadOnlySequence<byte>`，用于跨缓冲边界的分帧测试）、`ScriptedTcpServer` 与 `DuplexClientChannel`。
- 协议的测试替身（MELSEC 为 `McDeviceAdapter`、S7 为 `S7DeviceSimulator`、FINS 为 `FinsDeviceSimulator`）把请求翻译成存储操作，挂在 `DeviceSimulator` 上。
- 已知短板（P4）：`PlcMemory` 只有字粒度存储，按字节组织的存储要替身自带（S7 的 `Dictionary<(S7Area, int), byte[]>` + "块不存在 / 越过块尾"两种错误码）；字软元件的原生位写要改写覆盖的字，与 `PlcMemory` 自带的位字典不能并存（F7）。测试库还缺"最小可重连 / 可心跳的 TCP 配置"与带超时的到达助手，导致端到端故障脚手架在协议之间同形（F6、F10）。

## P7 变更：只读地址与协议专有请求（P7-S1、S2）

P7 把 Modbus 迁移到套件时，套件补了两项通用能力，其余接口不变（[[变更与决策/Modbus v2 归档/API参考/核心接口与传输基类]] 的 2.x 设计已取代）：

- **`IsReadOnly(TAddress)`**（S1，虚方法，默认 `false`）：返回 `true` 的地址不能被写入，包括读-改-写。写入返回 `InvalidRequest`，消息为 `The address '<x>' is read-only.`，不发送任何数据。Modbus 用它拒绝写 `IR` 与 `DI`（[ModbusClient.Hooks.cs](file://Junevy.Communication.Modbus/Client/ModbusClient.Hooks.cs)）。
- **`SendProtocolRequestAsync<T>(operation, buildRequest, parseResponse, ct)`**（S2，`protected`）：发送协议专有的请求，即不属于标准读写钩子的命令（Modbus 的 0x16 掩码写与 0x17 读写多个寄存器）。它与普通操作共用操作锁、取消与异常收口，因此不会与同一客户端上的读写交错。`parseResponse` 接收应答字节，返回 `CommResult<T>`。

两项都是可选的：不覆写 `IsReadOnly` 的协议行为不变，不发送协议专有请求的协议也不需要知道 S2。

## 扩展性（P7 实测结论）

P7 是第四次扩展性验证：把 Modbus（2.x 的同步 + 异步双轨、自有的通道与重连）迁移到套件，用来检验"第四个协议能否在不改通道层和其他协议的前提下迁移"。

- **套件改动只有两项**：S1（约 14 个有效行）与 S2（约 40 个有效行）。Melsec、Siemens、Omron 的生产代码与测试未改动。
- **摩擦记录**（计划第 25.2 节）：F-S1、F-S2 是套件缺口，已由两项钩子覆盖；F-A1 是声明式地址解析器不能表达站号前缀与 Modicon 的一基地址，Modbus 因此自写解析器，这是**仍然存在的表达力缺口**，没有被 S1、S2 覆盖，留待审阅者决定是否纳入后续的套件扩展；F-A2 与 F-A3 是测试库的缺口（`IClientChannel` 测试替身只能复制，共享的设备替身同步回复）。
- **通道层未改**：RTU over TCP 上请求超时后，重建连接与紧接着的重试存在竞争（见 [[行为契约/重连重试与并发模型]] 的已知问题）。计划冻结了通道层，本期只记录。
- 具体的有效行对照与 Task 15 的复测数字见设计文档第 20.12 节与计划第 25.4 节。

## 扩展性（P2 实测结论）

P2 用 MELSEC 检验了套件的扩展性。协议只写了地址、帧、钩子、组件与工厂，连接、重连、心跳、超时、拆包、重试、类型化读写、日志与 DI 都由套件与通道族提供。摩擦记录（计划第 20.2 节）的主要结论：

- 构建钩子返回 `void`，不能报告"协议不可表示"的参数；这类检查放在 `ValidateRange`。
- `PlcAddress.Device` 为接口类型，协议的地址类型需要用 `new` 隐藏为具体类型。
- `IFrameCodecFactory` 拿不到 `FramingOptions.MaxFrameLength`，`ChannelComponents` 会静默覆盖同名的传输配置设置（协议需要守卫）。
- 通道族与套件的若干内部类型（统计对象的构造函数、透传编码器）只能通过替代方案获得；这些属于 Channels 的通用能力，P2 后评估。

## 扩展性（P4 实测结论）

P4 用 S7 与 FINS 两个协议复检第 12 节的设计预期"三者都不需要改套件"。结论（完整表见设计文档第 20.5 节）：

- **这句不成立，但缺口全在预告的位置**：套件确实需要修改，按第 12.1 节第 1 条处置为"作为通用能力补进套件"，共 8 项（`Byte` 单位、按单位的位号上限、`ParseAddress`、字节访问与三个字节钩子、读位钩子改 `virtual`、`CanWriteBitsNatively`、字节软元件上的位路由、字符串与单字节扩展）。除此之外第 12 节的接口形状没有被推翻，Plc 的生产有效行停在 1189 行。
- **设计预期成立的部分**：`IConnectionInitializer` 承载两种性质不同的握手（协商长度 / 协商路由）；`GetMaxReadPoints` 读会话快照足以表达协商上限；`ChannelComponents` 的四项声明与 `FrameCodecFactory` 的长度字段分帧覆盖 TPKT 与 FINS/TCP 外壳；`PlcChannelBinding.CreateChannel` + `IChannelCreator` 目录让"只走 TCP"的协议无需新工厂代码；请求管线、操作锁、Core 的 `Buffers` 全部按原样使用；MELSEC 在钩子契约变化后不受影响。
- **地址模型通过**：FINS 用声明式表 + 十进制位号进制完整表达 18 个符号（含 EM 十三组），最长前缀匹配足够；S7 的自写解析器是"多段数字"这一个例外的结果，不是模型的普遍失败。
- **套件之外仍缺的共用能力**：进程级序号计数器（三份 13–14 行，F3）、通道组件守卫与接线顺序（三份，生产重复窗口 33 → 54 的全部来源，F4 + F9）、大端字段读写（每协议一份，F8）、测试库的字节粒度内存与端到端脚手架（F7、F10）。前四项中的前三项已在 **P4.5** 抽成共用件，见下节。
- **钩子可测性**：钩子的 `internal` 可见性对协议自己的测试程序集无效，三个协议都需要"脱离通道与替身断言钩子"的门面（F5）。用户 2026-10-12 决定不做这个门面（它只为测试扩大公开面）；测试基建由 P4.6 整理。

## P4.5 变更：三个协议共用的接线与字段件

P4.5 是行为不变的重构（唯一的行为变化是 `CommResult<T>.Success(null)` 改为抛 `ArgumentNullException`），把上节列的"套件之外仍缺"的前三项抽成 Core / Plc 的共用件，第四个协议不必再写第四份。生产重复窗口 **54 → 34**。

### 组件接线：`PlcComponentWiring`

`Junevy.Communication.Plc.PlcComponentWiring.Apply(components, owner, frameCodec, correlation, keyExtractor, initializer, healthProbeFactory)` 统一了原先三个 profile 各自的 `RequireUnset` / `ThrowAlreadySet` 与逐项赋值：

- 协议**总是拥有** `FrameCodec`、`Correlation`、`KeyExtractor`、`HealthProbe`、`HealthProbeFactory` 五项。调用方已设置任一项时抛 `ArgumentException`（`ParamName` 为 `"components"`，消息 `ChannelComponents.{属性} is already set; {owner} sets it and does not override it.`），不静默覆盖。
- `Initializer` **只在协议提供了初始化器时**才属于协议：`initializer` 非 null 且调用方已设置才算冲突；为 null 时既不检查也不修改，调用方自己的握手钩子原样保留（MELSEC 就是这种情况）。
- 守卫按 `FrameCodec` → `Correlation` → `KeyExtractor` → `Initializer` → `HealthProbe` → `HealthProbeFactory` 的顺序执行，**全部通过后才赋值**，因此不会留下半配置的组件。
- `ReconnectPolicy` 不碰——重连退避是宿主的选择，不是协议的声明。
- **交付方式与介质的规则留在各协议的 profile，在调用之前检查**（S7 与 FINS 拒绝数据报；MELSEC 校验帧类型与介质）。规则随协议而不同，因此不进套件。

新协议的接线形状就是一句话加一条自己的交付规则：`S7ProtocolProfile` 与 `FinsProtocolProfile` 各 34 行（原先 58 行），`McProtocolProfile` 54 行（原先 89 行）。

### 大端字段读写：`BigEndianFields`

`Junevy.Communication.Core.Buffers.BigEndianFields` 取代了每协议一份的 `S7Fields` / `FinsFields`：宽度常量 `UInt8Bytes`…`UInt32Bytes`，写入 `WriteUInt8/16/24/32(IBufferWriter<byte>, …)` 与 `WriteBytes`，读取 `TryTakeUInt8/16/24/32(ref ReadOnlySpan<byte>, out …)` 与 `TryTakeBytes(…, length, out …)`。

关键约定是**读取在字节不足时返回 `false`，既不移动切片、`value` 也取默认值**——解码器据此返回结构错误的结果而不是抛异常（Q7）；只有参数错误（`output` 为 null、`UInt24` 超出 0–0xFFFFFF、`length` 为负）才抛。写入端比原 `S7Fields.WriteUInt24` 多一层范围校验，由 `S7Client.Hooks.ValidateRange` 与 `S7Device.MaxNumber` 保证运行时不会触发。

### 序号计数器：`SequenceCounter`

`Junevy.Communication.Core.Utils.SequenceCounter` 取代了三份同骨架实现（`McSerialNumbers`、`S7PduReferences`、`FinsSessionIds`）：`NextUInt16()` / `NextByte()`，`Interlocked.Increment` + 按宽度截断，**第一次取值是 1**，到界回绕。一个实例只按一种宽度使用；每个协议在**写那个字段的类型**上留一个进程级 `static readonly` 实例（`McFrameExchange.SerialNumbers`、`S7Message.PduReferences`、`FinsHead.SessionIds`），使客户端请求、握手与心跳探测共用一个来源——实例的位置跟着"谁写这个字段"走，不再为一个字段单独建类。回绕是安全的：同一个号在它的应答返回之前不会再被取到——每个通道同一时刻只有少量请求在途（S7、FINS 按发送顺序匹配并校验回显，MELSEC 4E 按序列号匹配），16 位或 8 位的范围足够让旧号早已应答完毕。

### `CommResult` 的可空性

`CommResult.IsSuccess` 与 `CommResult<T>.IsSuccess` 带 `[MemberNotNullWhen]`：成功分支内 `Data` 非空、失败分支内 `ErrorMessage` 非空，调用方不再写 `Data ?? 默认值` 或 null-forgiving 运算符（生产代码里 41 处 `.Data!` 与 3 处 `ErrorMessage!` 已全部删掉，`TreatWarningsAsErrors` 下 0 错误就是标注生效的证据）。net472 由 `build/Polyfills/NullableAttributes.cs` 的内部特性提供同样的标注——编译器按特性全名识别，引用方的流分析同样生效。`WebApiResult<T>` 与 `ModbusResult` **未**做同类标注。

相关笔记：[[架构设计/通讯库总体架构（协议族）]]、[[架构设计/通道与协议的扩展面（P2.5）]]、[[协议细节/S7 协议]]、[[协议细节/FINS 协议]]、[[API参考/通道与传输（TCP UDP 串口 TLS）]]。

## P2.5 变更：协议的创建入口

- 协议只提供一个创建入口 `Create(IChannelConfig transport, …)`；内置传输的重载只是它的快捷方式。
- 通道由 `PlcChannelBinding.CreateChannel(creator, name, transport, configureComponents, loggerFactory)` 创建：组件由创建器的 `Delivery` 与 `Medium` 决定，传输配置的类型必须是创建器的 `ConfigType`。
- `PlcClientFactory` 与 `ChannelFactory` 派生 Core 的 `NamedFactory<TItem>`；`TryGet` 的输出参数名为 `item`（P25-D12）。
- 详细说明见 [[架构设计/通道与协议的扩展面（P2.5）]]。
