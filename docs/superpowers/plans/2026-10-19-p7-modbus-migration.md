# P7 实施计划：Modbus 迁移到 Plc 套件（Modbus 3.0.0-preview.1）

- 日期：2026-10-19；分支：`feat/modbus-p7`（Task 0 从 master `c29a45b` 新建）。
- 执行者：另一个智能体。审阅者：在用户通知后验收，重点与前几期相同：
  - 代码：质量、clean code、架构设计；
  - 功能：实现是否完整，以及缺陷检查。
- 范围（用户 2026-10-19 决定，见第 0 节 P7-D1、D4、D5、D15、D16）：
  - `Junevy.Communication.Modbus` **原地替换**为 Plc 套件的第四个协议包，版本 `3.0.0-preview.1`。v2 代码在分支上删除，用 master 上的本地 tag `modbus-v2.0.0` 保留；
  - 站号写在地址前缀里（`s=2;HR100`），一个客户端就能轮询整条 RS-485 总线；
  - 读写存储区以外的功能码：Plc 套件补一个"协议专有请求"的通用入口，Modbus 只用它提供 0x16（掩码写）与 0x17（读写合一）。诊断类功能码不做；
  - 寄存器中的位（`HR100.5`）：默认拒绝写入，选项 `UseMaskWriteForBits` 打开后用 0x16 原子写入。
- 依据：
  - 设计文档 `docs/superpowers/specs/2026-10-10-communication-core-design.md`：第 3 节、第 11.2 节、第 12 节、**第 17 节（Modbus 的演进路径）**、第 19 节、第 20.5 / 20.6 节（P4 与 P4.5 的套件修订）。
  - P4 计划 `docs/superpowers/plans/2026-10-12-p4-s7-fins.md`：结构、Q1–Q28、扩展性摩擦记录的写法。
  - P4.5 计划（`SequenceCounter`、`BigEndianFields`、`PlcComponentWiring`）、P4.6 计划（共享测试库）。
  - 知识库 `Junevy.Communication.Wiki/zh/content/Agent协作/新增协议指南.md`（第一至第八节）、`行为契约/` 下的三篇笔记。
  - v2 的实现与测试：`git show modbus-v2.0.0:<路径>`（Task 3 删除工作区里的 v2 代码后，用这个方式查阅）。
- 协议资料：仓库里没有官方手册。第 21 节的黄金向量由审阅者按 *MODBUS Application Protocol Specification V1.1b3* 的示例与 *MODBUS over Serial Line V1.02* 的 RTU 帧格式逐字段拼出。CRC 由审阅者用独立脚本计算，其中 5 条与公开资料广泛引用的样例一致（第 21.2 节标注）。
- 本期目标有三个：
  1. 交付可用的 Modbus v3，覆盖 v2 的全部读写能力；
  2. **第四次验证套件的扩展性**。允许的套件改动只有 P7-D2 的两项，其余缺口按第 1 节第 8 条记录后停下；
  3. 写清迁移路径。v2 的使用者（AutomationSystem 的 `Communication.Modbus` 适配层、本仓库的 WPF 调试工具）要能照着第 20 节的对照表迁移。
- **可行性门槛**：里程碑 3 结束时（第 13 节），执行者汇报摩擦记录与度量。出现 S1、S2 以外的套件缺口，或者 v2 的某项读写能力在 v3 里表达不出来时，停下等用户决定是否继续。门槛未通过时整个分支可以放弃，master 不受影响。

---

## 0. 已固定的设计决策

| # | 决策 | 理由 |
|---|---|---|
| P7-D1 | **范围与包**（用户决定原地替换）：<br>• 包名 `Junevy.Communication.Modbus` 不变，版本 `3.0.0-preview.1`，与其他新包一样只构建、不 `nuget push`；<br>• csproj 改为 Import `build/Junevy.Communication.Common.props`（`net472;net8.0`、`TreatWarningsAsErrors`），在 Import 之后覆写 `<Version>`；依赖 Plc + Tcp + Udp + Serial；<br>• 支持的组合：Modbus TCP（MBAP）、RTU（串口）、RTU over TCP、Modbus UDP（MBAP）、RTU over UDP；<br>• 功能码：01、02、03、04、05、06、0F、10，以及 16、17（P7-D15）。<br>不做：Modbus ASCII、从站 / 服务端、诊断类（07、08、0B、0C、11）、文件记录（14、15）、FIFO（18）、设备标识（2B）、RTU 广播（站号 0）、自定义功能码与原始 PDU 入口、同步 API | 用户决定；设计第 17 节；v2 的读写能力全部保留，AutomationSystem 只用到 01–04、06、0F、10 |
| P7-D2 | **允许的套件改动只有这两项**（里程碑 1）：<br>• **S1** 只读地址钩子 `protected internal virtual bool IsReadOnly(TAddress address)`，默认 `false`（Task 1）；<br>• **S2** 协议专有请求 `protected Task<CommResult<T>> SendProtocolRequestAsync<T>(…)`（Task 2）。<br>Core、Channels、Tcp、Udp、Serial **不改**。Melsec、Siemens、Omron 的生产代码与测试**不改**，测试保持全绿 | S1：套件的 `ValidateRange` 不区分读写，无法拒绝"写输入寄存器"。如果让写钩子抛异常，会被收口成 `Unspecified`，错误分类不对。S2：用户决定"套件补通用能力"；S7 读 SZL、FINS 读运行状态以后都能用 |
| P7-D3 | **公开 API**（逐条，不多不少）：<br>• 命名空间 `Junevy.Communication.Modbus`：<br>　- `ModbusClient : PlcClientBase<ModbusAddress>`（sealed）：两个 `Create`（同 P4-D10）、`MaskWriteRegisterAsync`、`ReadWriteMultipleRegistersAsync`；**没有**公开构造函数；<br>　- `ModbusClientOptions : PlcClientOptions`；<br>　- `ModbusFraming`（`Mbap`、`Rtu`）；<br>　- `ModbusAddress : PlcAddress`：`new ModbusDevice Device`、`byte? UnitId`；<br>　- `ModbusDevice : IPlcDeviceDefinition`（sealed）：`Symbol`、`Unit`、`Radix`、`MaxNumber`、`IsReadOnly`，四个静态实例 `Coil`、`DiscreteInput`、`InputRegister`、`HoldingRegister`，构造函数 internal。<br>• 命名空间 `Junevy.Communication.Modbus.DependencyInjection`：`ModbusTransport`（`Tcp`、`Udp`、`Serial`）、`ModbusClientConfig : IPlcClientConfig`、`ModbusClientCreator : IPlcClientCreator`、`ModbusServiceCollectionExtensions.AddModbus()`。<br>其余类型全部 internal，包括协议层、分帧、键提取器、心跳探测、配置快照、`ModbusProtocolProfile`。共 9 个公开类型（上面逐条列出 9 个；本行原写"10"是计数错误，Task 15 用反射确认为 9） | 沿用 P2.5 的单一 `Create` 入口和 P4-D10 的最小公开面；Modbus 没有握手会话，但公开 profile 只会扩大公开面，没有使用者需要它 |
| P7-D4 | **地址语法**（自写 `ModbusAddressParser`，internal）：<br>• 形式：`[s=<站号>;]<存储区><编号>[.<位号>]`，大小写不敏感；空白的处理与套件的 `PlcAddressParser` 一致。<br>• 符号写法（**零基**，十进制编号 0–65535，就是协议地址）：`C` 线圈（位，读写）、`DI` 离散输入（位，只读）、`IR` 输入寄存器（字，只读）、`HR` 保持寄存器（字，读写）。<br>• Modicon 写法（**一基**，纯数字，首位表示存储区：0 线圈、1 离散输入、3 输入寄存器、4 保持寄存器）：<br>　- 5 位：后 4 位 `0001`–`9999`，对应编号 0–9998；<br>　- 6 位：后 5 位 `00001`–`65536`，对应编号 0–65535；<br>　- 其他长度、首位为 2 或 5–9、后几位为 0（如 `40000`）：`InvalidRequest`，消息提示 `HR100` 与 `40001` 两种写法。<br>• 位号 `.n`：只允许寄存器（`IR`、`HR` 及 3、4 开头的 Modicon 地址），十进制 0–15，位 0 是寄存器值的最低位；线圈与离散输入带位号时返回 `InvalidRequest`。<br>• 站号前缀 `s=n;`：十进制 0–255。<br>　- 由客户端的 `TryParseAddress` 按帧格式检查：MBAP 允许 0–255；RTU 只允许 1–247（0 是广播，不做；248–255 是保留值），超出时返回 `InvalidRequest`；<br>　- 没有前缀时 `ModbusAddress.UnitId` 为 null，请求使用 `ModbusClientOptions.UnitId`。<br>• `ModbusAddress.Text` 保留调用方的原文 | 设计第 17 节："地址写成 `40001` 或 `HR100`"。站号前缀是用户的决定（与 HslCommunication 相同）。声明式解析器表达不了前缀和 Modicon 写法，与 S7 同属 F1 类，Task 4 记一行摩擦 |
| P7-D5 | **功能码映射**（"一个点"指**一个分包**只含一个点，拆包后的最后一包也按这条规则判断）：<br>• `ReadAsync`：`HR` 用 03，`IR` 用 04；<br>• `ReadBitsAsync`：`C` 用 01，`DI` 用 02；在寄存器上读位，由套件读字后取位（03 / 04）；<br>• `WriteAsync`（只有 `HR`）：一个字并且 `UseSingleWriteFunctions` 为 true 时用 06，否则用 10；<br>• `WriteBitsAsync`：<br>　- `C`：一个点并且 `UseSingleWriteFunctions` 为 true 时用 05（`FF 00` / `00 00`），否则用 0F；<br>　- `HR` 并且 `UseMaskWriteForBits` 为 true：`CanWriteBitsNatively` 返回 true，每个请求用 16 写 1 位（AND 掩码 `~(1 << n)`，OR 掩码为写 1 时 `1 << n`、写 0 时 0）；<br>　- `HR` 并且 `UseMaskWriteForBits` 为 false：走套件默认，返回 `NotSupported`；开启 `AllowReadModifyWriteBits` 时读-改-写（03 加 06 或 10）；<br>• `IR`、`DI` 上的任何写入：S1 返回 `InvalidRequest`，不发请求；<br>• `C`、`DI` 上按字（`ReadAsync`、类型化读写）或按字节访问：`ValidateRange` 返回 `InvalidRequest`，消息说明线圈与离散输入只能按位访问 | 套件的类型化读写都走字路径；用户决定寄存器写位"默认拒绝、可选 0x16"。0x16 由设备原子执行，不会与 PLC 程序抢写同一个寄存器 |
| P7-D6 | **单包上限**（规范第 6 节）：读寄存器 125（03、04），读位 2000（01、02），写寄存器 123（10），写线圈 1968（0F），掩码写位每请求 1 位。`GetMaxReadPoints` / `GetMaxWritePoints` 按 `access` 的单位返回：`Words` 返回 125 / 123，`Bits` 在位存储区返回 2000 / 1968，在开启掩码写的 `HR` 上写位返回 1。<br>`ValidateRange`：<br>• 结束地址（字访问为 `编号 + 点数 − 1`；寄存器上的位访问换算到字）超过 65535 时返回 `InvalidRequest`；<br>• P7-D5 最后一条的拒绝也在这里。<br>超过单包上限时由套件自动拆包（**行为变化**：v2 对超限直接抛 `ArgumentException`） | 规范给出的上限；拆包是套件的能力 |
| P7-D7 | **帧格式与传输的组合**：<br>• 选项 `Framing` 为 `ModbusFraming?`，null 时按介质决定：串口用 `Rtu`，网络（TCP、UDP）用 `Mbap`；<br>• 网络 + 字节流（TCP）：`Mbap` 是 Modbus TCP，`Rtu` 是 RTU over TCP；<br>• 网络 + 数据报（UDP）：`Mbap` 是 Modbus UDP，`Rtu` 是 RTU over UDP；<br>• 串口 + `Mbap`：`Create` 抛 `ArgumentException`；<br>• 未定义的枚举值抛 `ArgumentOutOfRangeException`。<br>规则只写在 `ModbusProtocolProfile` 一处，并且在 `PlcComponentWiring.Apply` 之前检查（新增协议指南第八节 F4/F9） | 透传网关、串口服务器上常用 RTU over TCP；组合规则集中在一处，与 MELSEC 的做法相同 |
| P7-D8 | **分帧**：<br>• MBAP + 字节流：套件的 LengthField，偏移 4、宽度 2、`BinaryBigEndian`、修正值 0，`MaxFrameLength` 260（MBAP ADU 的上限）；<br>• RTU + 字节流（串口、TCP）：自写 `ModbusRtuFrameCodecFactory`（`Framing/`，编码器透传）：<br>　- 解码器按应答的功能码确定帧长（第 21.4 节长度表），收全后校验 CRC；<br>　- 功能码未知或 CRC 不符时**丢弃 1 个字节后重新同步**，不抛异常（沿用 v2 的"读到成帧并以 CRC 裁决"，知识库 `设计决策记录.md`）；<br>　- `MaxFrameLength` 256；<br>• 数据报（两种帧格式）：不设分帧，一个数据报就是一帧；RTU 的 CRC 在应答解析时校验。<br>串口通道配置里默认的 IdleGap 分帧会被协议声明的分帧取代，README 写明 | RTU 的帧长可以从功能码和字节数推出，不依赖时序，在 USB 转串口与 RTU over TCP 上都可靠；按字节重新同步能越过线路噪声和半双工的本地回显 |
| P7-D9 | **关联**：<br>• MBAP：`CorrelationMode.Keyed`，键提取器 `ModbusTransactionKeyExtractor` 从请求和应答的第 0–1 字节取事务号。事务号来自进程级 `SequenceCounter.NextUInt16()`，实例放在写 MBAP 头的类型上，客户端请求与心跳探测共用（新增协议指南 F3 的约定）。超时后到达的迟到应答因为事务号不匹配被丢弃，**不需要重建 TCP 连接**；<br>• RTU：`CorrelationMode.Sequential`。串口依靠通道的迟到应答丢弃窗口；RTU over TCP 依靠 `TcpClientChannelConfig.ResetOnRequestTimeout`（默认 true）在超时后重建连接 | MBAP 有事务号，Keyed 能消除"迟到应答被当成下一个请求的应答"；RTU 没有事务号，只能顺序关联 |
| P7-D10 | **应答校验与错误映射**：<br>• 构造请求时记下这一包的期望：帧格式、站号、功能码、起始地址、数量或回显值。解析同一包的应答时使用，操作锁保证一包的发与收不被打断（FINS `lastSessionId` 的做法）；<br>• 异常应答（功能码 \| 0x80）：`RemoteError`，`ProtocolErrorCode` 为异常码，消息为 `Modbus exception 0x02 (Illegal Data Address) for function 0x03.`。名称表只用第 21.5 节列出的 9 个码，其他码的消息不带名称；<br>• 以下情况为 `ProtocolViolation`（套件按 12.5 重试）：<br>　- MBAP 协议号不是 0，或长度字段与帧长不一致；<br>　- 站号不符、功能码不符、字节数不符；<br>　- 写应答的回显不符（05 / 06 回显地址与值，0F / 10 回显地址与数量，16 回显全部）；<br>　- 数据报 RTU 的 CRC 不符；<br>• MBAP 的事务号由通道按键匹配，解析时不再重复检查 | 设计 12.5、P2-D12；Modbus 的异常码是规范定义的小集合，可以带名称（P4-D14"只用列出的码"） |
| P7-D11 | **数据格式**：默认 `PlcDataFormat(WordOrder.ABCD, Encoding.ASCII)`；选项 `DataFormat` 为 `PlcDataFormat?`，null 时用默认值。规范化布局等于线上字节（每个寄存器大端）。寄存器的位 n 是寄存器值的第 n 位，与套件 `PlcBitPacking` 的约定一致。字符串按线上顺序取字节（每个寄存器的高字节在前） | 规范规定寄存器大端；32 位数的字序因设备而异，用 `DataFormat` 指定（例如 `CDAB`） |
| P7-D12 | **心跳探测** `ModbusReadProbe`：向 `ModbusClientOptions.UnitId` 发 03 读 `HR0` 1 个字。只要收到格式正确的应答（站号与功能码匹配，正常应答或异常应答都算）就判为健康。只在通道配置启用心跳时使用（默认关闭） | Modbus 没有通用的回环命令（诊断类只用于串行链路，很多 TCP 设备不支持）；设备回了异常应答也说明链路与协议栈正常（P4-D15 的原则） |
| P7-D13 | **连接与重试语义**（**行为变化**，写进 README 与 CHANGELOG 的"破坏性变更"）：<br>• v2 的"懒重连"（请求失败后在下一次请求前重建连接）改为通道的后台重连，默认关闭；<br>• `Disconnect` 之后请求不会悄悄连回；<br>• 不再隐式连接：断开期间的请求立即返回 `NotConnected`；<br>• `RetryCount` 默认值由 3 改为 0；<br>• 不提供同步 API（设计 21.1 Q2）；<br>• 超时改在通道配置里设置（第 20 节对照表） | 设计 11.2 已预告：P7 之后 Modbus 与其他协议一致，11.2 记录的差异消失 |
| P7-D14 | **依赖注入**：`ModbusClientConfig` 有 `Transport`、`Tcp`、`Udp`、`Serial`、`Options` 五个属性（与 `McClientConfig` 同形）；`ModbusClientCreator` 按 `Transport` 选择配置，选中的配置为 null 时抛 `ArgumentException`；`AddModbus()` 用 `TryAddEnumerable` 注册创建器，再调用 `AddPlcClients()`（幂等）。v2 的 `IModbusFactory`、`ModbusConnectionManager`、`ModbusFactoryBuilder`、`AddModbusFactory()` 由套件的 `PlcClientFactory`、`PlcClientFactoryBuilder` 取代。`PlcDataFormat` 不能由 JSON 绑定（与 P4 相同） | 设计 12.9、16；Modbus 支持三种传输，与 MELSEC 同形 |
| P7-D15 | **0x16 与 0x17**（用户决定只保留这两个；都经 S2 发送，享有操作锁、重试与错误归类）：<br>• `MaskWriteRegisterAsync(string address, ushort andMask, ushort orMask, CancellationToken)` 返回 `Task<CommResult>`：<br>　- 地址必须是不带位号的 `HR`。`IR` 由 S1 返回 `InvalidRequest`；`C`、`DI` 与带位号的地址返回 `InvalidRequest`；<br>　- 只发一个请求，应答要原样回显。<br>• `ReadWriteMultipleRegistersAsync(string readAddress, int readCount, string writeAddress, ReadOnlyMemory<byte> writeData, CancellationToken)` 返回 `Task<CommResult<byte[]>>`：<br>　- 两个地址都必须是不带位号的 `HR`，并且站号相同，否则返回 `InvalidRequest`；<br>　- `readCount` 为 1–125；`writeData` 长度为偶数，按字计 1–121；两段的结束地址不超过 65535；<br>　- **不拆包**，超限返回 `InvalidRequest`；<br>　- 返回读出的 `2 × readCount` 字节（规范化布局）。规范规定设备先写后读。<br>两个方法的数据参数与返回值都用字节，与 `IPlcClient.ReadAsync` / `WriteAsync` 一致 | 用户决定；0x17 是一个原子事务，拆包会改变语义 |
| P7-D16 | **v2 的去留与迁移**：<br>• Task 3 删除 v2 的全部生产代码与测试（`git rm`）。不保留兼容层，不加 `[Obsolete]` 转发（Q29 不留转发层）；<br>• README 新增 "Migrating from 2.x" 一节，内容按第 20 节；<br>• Modbus 的用法并入 Skill `using-junevy-plc`（与 MELSEC、S7、FINS 并列）。`Skills/using-junevy-modbus/SKILL.md` 改写为 v2 → v3 迁移指南，指向 `using-junevy-plc`；<br>• WPF 调试工具 `Junevy.Communication.Test` 在 Task 3 暂时去掉对 Modbus 的引用（保持可编译），在 Task 11 用 v3 恢复同等功能；<br>• AutomationSystem 的迁移不在本仓库做（第 24 节） | 用户决定原地替换；同一个包里并存两套 Modbus 只会增加维护面 |
| P7-D17 | **已知不确定项**，写进 README 的 "Verification status"：<br>① 只与测试里的设备替身联调过，没有对真实设备或 Modbus Poll / Modbus Slave 这类工具验证；<br>② RTU 按字节重新同步在强噪声线路上的表现；<br>③ 0x16 的支持因设备而异（不支持时返回异常码 01）；<br>④ 少数设备只接受 10 而不接受 06（把 `UseSingleWriteFunctions` 设为 false）；<br>⑤ 站号 0 和 255 在 TCP 网关上的含义因厂商而异；<br>⑥ RTU over UDP 很少见，只按规范实现 | 诚实记录，留待真机验证 |
| P7-D18 | **测试项目**：`Tests/Junevy.Communication.Modbus.Tests` 重写，改为 Import `build/Junevy.Communication.Tests.props`，目标 `net472;net8.0`（v2 的测试只有 net8.0）。使用共享测试库的 `DeviceSimulator`、`PlcMemory`、`DuplexClientChannel`、`ScriptedByteChannel`、`LoopbackTcp`、`Arrival`、`Polling`、`HexVector`。设备替身放在 `TestSupport/`，不放进共享测试库（第 1 节第 7 条） | 新增协议指南第四节；改共享测试库会让受影响的测试扩大到接近全量 |

---

## 1. 执行规则（每个 Task 都适用）

1. 每个 Task 一个提交，在 `feat/modbus-p7` 上提交。不 push、不 amend、不 rebase；除 Task 0 新建分支外，不切换分支。**只 `git add` 本 Task 的文件。**
2. 步骤顺序固定：**先写测试并确认失败**（新代码阶段无法编译也算失败）→ 写生产代码 → 测试通过 → 按第 2 节自查并清理 → 更新文档 → 提交。
3. 每个 Task 完成后依次运行以下三条命令，全部成功才能提交：
   ```powershell
   dotnet build Junevy.Communication.slnx -c Debug --no-incremental
   dotnet build Junevy.Communication.slnx -c Release --no-incremental
   pwsh build/test-affected.ps1 -Base <起点提交> -NoBuild
   ```
   - 起点提交是 Task 0 新建分支时的 HEAD（`c29a45b`），记入第 25.4 节；
   - 本期的选择范围：里程碑 1 改 Plc，会选中 Plc、Melsec、Siemens、Omron、Modbus、Architecture 的测试；之后只改 Modbus 时选中 Modbus 与 Architecture 的测试；
   - 测试宿主因内存紧张崩溃时（"Releasing the double mapped memory failed"，或启动超时 90 秒），先运行 `dotnet build-server shutdown` 再重跑。这类崩溃不当作失败去改代码，也不当作通过。
4. 代码约束沿用 P1–P4：
   - 只提供异步 API，库内每个 `await` 都写 `ConfigureAwait(false)`；
   - 文件作用域命名空间；错误消息与日志用英文，XML 注释用中文；日志模板必须是常量（CA2254）；
   - 遵守 net472 禁用清单：不用默认接口成员、`Enumerable.Append`、`SequenceReader<T>`、`Task.WaitAsync`、`ArgumentNullException.ThrowIfNull`、Index/Range、init 与 record。字节读取用 Core 的 `ByteSequenceReader` 或 `BigEndianFields.TryTake*`，写入用 `IBufferWriter<byte>` 与 `BigEndianFields.Write*`。
5. CHANGELOG：在 `CHANGELOG.md` 顶部新增一节 `## [Unreleased] — P7（Modbus 3.0.0-preview.1：迁移到 Plc 套件；Plc 套件扩展）`，下设 `### 新增（Added）`、`### 变更（Changed）`、`### 破坏性变更（Breaking）`、`### 移除（Removed）`。条目格式为 `- YYYY-MM-DD [包名] 内容`。每个 Task 提交时同步追加。
6. 不修改：
   - Core、Channels、Tcp、Udp、Serial 的生产代码；
   - Melsec、Siemens、Omron 的生产代码与测试。如果套件改动让它们无法编译或测试变红，说明套件改动破坏了兼容性：停下来汇报，不要改它们；
   - Secs、WebApi、WebApi.Server、Mqtt、OpcUa、Ftp 的全部代码；
   - Plc 的生产代码只允许按 P7-D2 修改；
   - 共享测试库 `Junevy.Communication.Testing`。确实需要时先记摩擦，由审阅者决定。
7. 知识库只在 Task 14 回写，**不提交**；汇报时附上 `git status --short -- Junevy.Communication.Wiki` 与 `git diff --stat -- Junevy.Communication.Wiki` 的输出。
8. **扩展性摩擦记录**（第 25.2 节）是本期的核心产出之一。从 Task 3 起，每当 Modbus 需要 Core、Channels、Plc 里没有的东西，或者不得不写变通代码，立即追加一行（现象 / 变通或缺口 / 建议的通用能力）。超出 S1、S2 的套件改动**不要自行实现**，记录后停下来汇报。
9. 本计划之外的问题记入第 24 节，只记录不修改。执行中偏离计划的地方（包括发现计划写错，尤其是第 21 节的向量）记入第 25.3 节。**不要为了让测试通过而修改编码器去迎合一个可疑的向量**，先逐字段核对。
10. 每个 Task 的汇报写进第 25.3 节，内容包括：
    - 新增、修改、删除的文件；
    - 测试先红后绿的证据；
    - 第 2 节逐条自查（符合 / 不符合，并说明）；
    - 涉及的第 3 节度量；
    - 三条命令的结果（每个测试项目在两个目标上的通过 / 失败 / 跳过数）。

    每个里程碑结束时写一段小结。里程碑 3 的小结就是可行性门槛的汇报（第 13 节）。其他里程碑是否等审阅者验收后再继续由用户决定，用户没有说明时继续执行。
11. 串口：只允许 COM20 / COM21（`JUNEVY_SERIAL_PAIR=COM20,COM21`），**绝不打开 COM3 / COM4**。真实串口用例（`SerialPairFact`）由执行者编写，由审阅者运行。
12. 用真实套接字的测试放进 `SocketTiming` 集合，耗时断言用区间，不用 `Thread.Sleep`。等待用 `Polling.WaitUntilAsync` 与 `Arrival.WithinAsync`，不要再写 `Task.WhenAny` + `Task.Delay`。
13. 事务号来自进程级计数器，测试**不要断言事务号的绝对值**：从 `ScriptedByteChannel.LastRequest` 取出事务号，再拼期望的应答。

---

## 2. 代码质量标准（审阅者逐条验收，不符合即打回）

P4 计划第 2 节的 Q1–Q28 全部适用（"三个 PLC 协议之间不重复"在本期读作"四个 PLC 协议之间不重复"，Q28 读作"套件改动不改变 MELSEC、S7、FINS 的行为"）。P4.5 的 Q29（不留转发层）、Q30（Core 与 Plc 的新类型协议无关）同样适用。另外：

- **Q44 协议层纯净**：`Protocol/` 下的 PDU、MBAP、RTU、CRC 编解码只处理字节，不依赖通道类型；每个编码器与解码器都有第 21 节黄金向量的测试，测试名或注释写明向量编号（例如 `V-R-6`）。
- **Q45 v2 零残留**：生产代码与测试里不出现 v2 的类型名（`IModbus`、`ModbusResult`、`ModbusErrorKind`、`ModbusRequest`、`ModbusTcpClient`、`ModbusRtuClient`、`ModbusTransportBase`、`IModbusFactory`、`ModbusConnectionManager` 等），不保留空目录与注释掉的旧代码。文档里出现 v2 类型名，只允许在迁移对照与历史记录中。
- **Q46 套件改动协议无关**：S1、S2 的代码、注释、消息与日志里不出现 "Modbus"；它们的测试在 Plc.Tests 里用套件自己的测试替身完成，不依赖 Modbus。
- **Q47 期望与解析单一来源**：每一包的期望只在构造请求时记录一次，四种组合（MBAP / RTU × 流 / 数据报）共用同一套 PDU 解码与回显校验；帧格式之间只有外壳（MBAP 头、站号 + CRC）不同。

---

## 3. 度量（Task 0 记录基线，Task 15 复测）

生产代码范围：P6.3 结束时的 15 个目录（Core、Channels、Tcp、Udp、Serial、Plc、Melsec、Siemens、Omron、Secs、WebApi、WebApi.Server、Mqtt、OpcUa、Ftp），**加上 Modbus**，逐个 `git ls-files -- <目录>` 再筛 `*.cs`。脚本在 `build/metrics/`。

| 指标 | 命令 | 基线 | P7 目标 |
|---|---|---|---|
| 生产重复窗口（6 行） | `python build/metrics/dupscan.py 6 <文件>` | 15 个目录为 35（10 个集群）；Task 0 另测含 v2 Modbus 的 16 个目录 | 15 个目录不增加；Modbus 内部、Modbus 与其他程序集之间都为 0 |
| 超过 60 行的方法 | `python build/metrics/methodlen.py 61 <文件>` | 15 个目录为 0 | 0（含 Modbus） |
| 超过 300 行的文件 | `wc -l` | Task 0 实测 | 不增加；Modbus 没有超过 300 行的文件 |
| 生产程序集之间的 IVT | 架构测试 | 2 | 2 |
| 有效行 | `python build/metrics/effective_lines.py <项目>` | Modbus v2 **3304**、Plc 1231、Melsec 1247、Siemens 1271、Omron 692 | 记录 Modbus v3 的有效行与 Plc 的增量（S1、S2），与 v2 和另外三个协议对照 |
| 构建警告 | 两条构建命令 | 31 条（Modbus v2 与 WPF 调试工具） | Modbus 0 条；总数减少，记录减少了多少 |
| 测试重复窗口（P4.6 口径） | `dupscan.py 6`（`Tests/` 下全部 `*.cs`） | Task 0 实测（P6.3 结束时为 192，52 个集群） | Modbus.Tests 内部、它与其他测试项目之间都为 0 |
| 套件改动 | 第 25.2 节 | — | 只有 S1、S2 |

---

## 4. Task 0：基线

1. 确认 master 干净且 HEAD 为 `c29a45b`；`git tag modbus-v2.0.0 c29a45b`（本地 tag，不 push）；`git switch -c feat/modbus-p7`。
2. 两条构建命令：记录警告总数，以及其中来自 Modbus 的条数（按项目去重）。
3. 第 3 节全部度量的基线，写进第 25.4 节。
4. 清点 v2 的测试：按文件数出 `[Fact]` / `[Theory]` 与用例数，填入第 22 节的"用例数"一列。
5. 用 tag 上的 v2 `Crc16Helper` 写一个一次性脚本（放在 scratchpad，不提交），对第 21.2 节的每条 RTU 向量重算 CRC，结果写进第 25.3 节。任何一条不一致都先停下汇报。
6. 不跑测试（新增协议指南第六节）。提交计划文件的第 25 节：`docs(plan): P7 baseline`。

---

## 里程碑 1：套件补强（S1、S2）

### 5. Task 1：只读地址（S1）

1. 测试（Plc.Tests，用 `FakePlcClient` 一类的套件替身，按需给它加一个只读设备）：
   - `IsReadOnly` 为 true 的地址上，`WriteAsync`、`WriteBitsAsync`（包括原生位写和读-改-写两条路径）、`WriteBytesAsync` 都返回 `InvalidRequest`，消息为 `The address '<Text>' is read-only.`；
   - 不发请求、不进操作锁：替身通道的请求数为 0；断开状态下同样先返回 `InvalidRequest`（地址错误优先于连接状态，与非法地址的现有顺序一致，执行者先读流水线确认顺序，写进汇报）；
   - 同一地址上的读取不受影响；
   - 默认实现（未覆写）对所有地址返回 false：MELSEC、S7、FINS 的测试不改并保持全绿就是证据。
2. 生产代码：`PlcClientBase.Hooks.cs` 增加钩子与 XML 注释；流水线三个写入口在地址解析之后、点数检查之前判断。类级 XML 注释的钩子契约表增加一行。
3. 文档：CHANGELOG（Plc，Added）。Skill `using-junevy-plc` 的 Protocol Author Guide 与知识库的钩子契约表留到 Task 12 / Task 14。
4. 提交：`feat(plc): read-only address hook (P7 S1)`。

### 6. Task 2：协议专有请求（S2）

签名（`PlcClientBase.cs`，`protected`，不是钩子）：

```csharp
protected Task<CommResult<T>> SendProtocolRequestAsync<T>(
    string operation,
    Action<IBufferWriter<byte>> buildRequest,
    Func<byte[], CommResult<T>> parseResponse,
    CancellationToken cancellationToken = default);
```

1. 语义与读写入口相同：
   - 已释放：返回 `NotConnected`，消息与现有的释放消息相同；
   - 未连接：立即返回 `NotConnected`，不发请求；
   - 在操作锁内执行，与读写互相排队；
   - 重试规则同设计 12.5：`Timeout`、`ConnectionClosed`、`ProtocolViolation` 按 `RetryCount` / `RetryInterval` 重试，`RemoteError` 与 `InvalidRequest` 不重试；
   - `buildRequest` 或 `parseResponse` 抛出的异常收口为 `Unspecified` 并记 Error；调用方取消时返回 `Cancelled`；
   - 记一条 Debug 日志，格式与分包日志一致，`Address` 位置写 `operation`。
2. 参数为 null 时抛 `ArgumentNullException`，`operation` 为空白时抛 `ArgumentException`（编程错误，与构造函数的约定一致）。
3. 实现：复用 `PlcPacketExchange.ExchangeAsync`、`ExclusiveAsync`、`GuardAsync`，**不复制第二份重试循环**。`ExchangeAsync` 的解析委托返回非泛型 `CommResult`，泛型结果用闭包带出，或把 `ExchangeAsync` 改成泛型并让现有调用方传 `bool`，执行者择一并说明理由。
4. 测试（Plc.Tests，套件替身派生一个调用 `SendProtocolRequestAsync` 的公开测试方法）：成功；已释放；未连接时请求数为 0；`Timeout` 重试一次后成功；`RemoteError` 不重试；解析抛异常时返回 `Unspecified` 并有 Error 日志；调用方取消；与 `ReadAsync` 并发时串行（用可控的替身通道证明第二个请求在第一个完成前没有发出）。
5. 提交：`feat(plc): protocol-specific request entry point (P7 S2)`。

**里程碑 1 小结**：Plc 的有效行增量；Melsec、Siemens、Omron 的测试数与 Task 0 一致且全绿；`git diff c29a45b -- Junevy.Communication.Melsec Junevy.Communication.Siemens Junevy.Communication.Omron Tests/Junevy.Communication.Melsec.Tests Tests/Junevy.Communication.Siemens.Tests Tests/Junevy.Communication.Omron.Tests` 为空。

---

## 里程碑 2：Modbus 协议层

### 7. Task 3：替换项目骨架

1. `git rm` v2 的全部生产代码（`Junevy.Communication.Modbus/` 下除 csproj 与 README 外的 `*.cs`）与全部测试（`Tests/Junevy.Communication.Modbus.Tests/` 下的 `*.cs`，包括 `TestSupport/`）。README 留到 Task 12 重写。
2. 新 csproj：Import `..\build\Junevy.Communication.Common.props`，之后覆写 `<Version>3.0.0-preview.1</Version>`；`PackageId` 不变；`Description` 改为一句话说明（Plc 套件上的 Modbus TCP / RTU / UDP 客户端）；`PackageReadmeFile`；引用 Plc、Tcp、Udp、Serial；`InternalsVisibleTo` 只给 `Junevy.Communication.Modbus.Tests`。去掉 v2 的 `CS1591` 抑制以外的特殊设置（`CopyLocalLockFileAssemblies`、直接的 `System.IO.Ports` 引用等）；CS1591 的处理与 Melsec 一致。
3. 测试 csproj 与 Melsec.Tests 一致（Import `Tests.props`，引用 Modbus 与 Testing），加 `ProjectSmokeTests` 与 `SocketTimingCollection`。
4. 架构测试：删除 Modbus 的特殊处理（`typeof(ModbusTcpClient).Assembly` 那一行和对应的 `using`）。骨架里还没有公开类型可以取程序集，分层表的新行在 Task 4 加（**不得**为此新增计划外的公开类型）。
5. WPF 调试工具：去掉对 Modbus 的项目引用与 `App.xaml.cs`、`MainWindowViewModel.cs` 中的 Modbus 代码，保持可编译（不留注释掉的代码），Task 11 恢复。
6. 提交：`refactor(modbus)!: replace the v2 implementation with a Plc-suite skeleton`。

### 8. Task 4：存储区与地址（P7-D4）

1. `ModbusDevice`：四个静态实例；`Symbol` 为 `C` / `DI` / `IR` / `HR`；`Unit` 为 `Bit` / `Bit` / `Word` / `Word`；`Radix` 为 10；`MaxNumber` 为 65535；`IsReadOnly` 为 false / true / true / false。读写功能码作为 internal 属性。
2. `ModbusAddress`：`new ModbusDevice Device`、`byte? UnitId`。
3. `ModbusAddressParser`（internal，纯函数，不知道帧格式）：解析 P7-D4 的全部写法，得到存储区、编号、位号与可选站号。
4. 测试（表驱动，每条注明期望的存储区、编号、位号、站号）：
   - 符号写法：`HR0`、`hr65535`、`C0`、`DI100`、`IR7`、`HR100.15`；
   - Modicon 写法：`00001`→C0、`10001`→DI0、`30001`→IR0、`40001`→HR0、`49999`→HR9998、`400001`→HR0、`465536`→HR65535、`40001.3`；
   - 站号前缀：`s=2;HR100`、`S=0;40001`、`s=255;C0`；
   - 非法：`HR65536`、`40000`、`465537`、`20001`、`50001`、`100`、`4000001`、`C1.0`、`DI1.0`、`HR1.16`、`s=256;HR1`、`s=;HR1`、`s=2HR1`、`X100`、空串。每条断言错误消息的关键词。
5. 架构测试：分层表增加一行，Modbus 允许 Core、Channels、Plc、Tcp、Udp、Serial（用 `typeof(ModbusDevice).Assembly`）。Modbus 从此参加分层与第三方白名单两项检查。
6. 摩擦记录：声明式解析器表达不了站号前缀与 Modicon 写法（与 F1 同类）。
7. 提交：`feat(modbus): addresses and parser`。

### 9. Task 5：协议层（纯字节，Q44）

`Protocol/` 下的 internal 类型（命名可调整，职责不变）：

- `ModbusCrc`：CRC-16/MODBUS（初值 0xFFFF，多项式 0xA001，低字节在前），从 tag 上的 v2 `Crc16Helper` 移植算法，按本库风格重写；
- `ModbusPdu`：01–06、0F、10、16、17 的请求编码，正常应答与异常应答的解码；按期望校验字节数与回显（P7-D10），返回 `CommResult` / `CommResult<byte[]>` / `CommResult<bool[]>`；
- `ModbusExpectation`：一包的期望（站号、功能码、起始地址、数量或回显值）；
- `MbapHeader`：写头（事务号、协议号 0、长度、站号）、校验与剥离；进程级 `TransactionIds`（`SequenceCounter`）；
- `RtuAdu`：加站号与 CRC、校验与剥离；
- `ModbusExceptionCodes`：第 21.5 节的名称表与消息格式。

测试：第 21.1–21.3 节的每条向量，凡是给了两个方向的都双向测试；另加：
- 回显不符（05 的值、06 的地址、0F 的数量、16 的掩码各一条）返回 `ProtocolViolation`；
- 字节数不符返回 `ProtocolViolation`；
- 未知异常码的消息不带名称；
- 位打包（第 21.1 节 V-P-1、V-P-7 的逐位期望）；
- MBAP 长度字段与帧长不一致、协议号非 0（V-M-5）。

提交：`feat(modbus): PDU, MBAP and RTU codecs`。

### 10. Task 6：分帧与关联（P7-D8、D9）

1. `Framing/ModbusStreamFraming`：MBAP 返回 LengthField 的 `FramingOptions`（P7-D8），RTU 返回 `ModbusRtuFrameCodecFactory`。
2. `ModbusRtuFrameDecoder`：第 21.4 节的长度表；收全后校验 CRC；功能码未知或 CRC 不符时丢弃 1 字节并继续；超过 256 字节抛 `FrameDecodeException`。
3. `ModbusTransactionKeyExtractor`：请求与应答都取第 0–1 字节；不足 2 字节时返回 false。
4. 测试：
   - 解码器：单帧；逐字节到达；两帧粘连；异常应答帧（V-R-15）；帧前有 3 个噪声字节；CRC 错 1 位后紧跟一个正确帧，只交出正确帧；未知功能码后紧跟正确帧；本地回显（先到达一份请求 V-R-1，再到达应答 V-R-3），只交出应答；
   - 键提取器：V-M-6。
5. 提交：`feat(modbus): stream framing and transaction keys`。

**里程碑 2 小结**：Modbus 当前的有效行；协议层的向量覆盖清单（第 21 节每条向量对应的测试名）。

---

## 里程碑 3：客户端

### 11. Task 7：配置快照、组件声明与心跳探测（P7-D7、D9、D11、D12）

1. `ModbusProtocolSettings`（internal，不可变快照，`From(options)`）：校验 `Framing` 的枚举值、`DataFormat` 默认值、`UnitId`；`RetryCount` 等交给套件的设置类校验。`UnitId` 与帧格式有关（RTU 为 1–247），而帧格式可能要到看见介质时才确定，所以范围检查放在 profile 里，在介质确定之后做，`Create` 时抛 `ArgumentOutOfRangeException`。
2. `ModbusProtocolProfile.Configure(components, settings, delivery, medium)`（internal）：先检查 P7-D7 的规则，再调用 `PlcComponentWiring.Apply`：
   - `frameCodec`：字节流时按帧格式选，数据报为 null；
   - `correlation`、`keyExtractor`：按 P7-D9；
   - `initializer`：null；
   - `healthProbeFactory`：`ModbusReadProbe`。
3. `ModbusReadProbe`：P7-D12；MBAP 的事务号来自同一个进程级计数器。
4. 测试：规则表的每个格子（含串口 + MBAP 抛异常、未定义枚举）；组件已被调用方设置时抛 `ArgumentException`（`PlcComponentWiring` 的守卫，抽一项验证即可）；探测的请求字节（MBAP 与 RTU 各一，RTU 即 V-R-2）；探测把异常应答判为健康，把站号不符判为不健康。
5. 提交：`feat(modbus): protocol settings, channel components and read probe`。

### 12. Task 8：ModbusClient（P7-D3、D5、D6、D10）

1. `ModbusClient`：
   - `BuiltInCreators` 为 Tcp、Udp、Serial 三个创建器；两个公开 `Create`，加一个供创建器使用的 internal 命名重载（同 `McClient`）；构造失败时释放通道；
   - 钩子：`TryParseAddress`（解析器 + 按帧格式检查站号）、`GetMaxReadPoints`、`GetMaxWritePoints`、字与位的 Build / Parse、`ParseWriteResponse`、`CanWriteBitsNatively`、`ValidateRange`、`IsReadOnly`；
   - 每个 Build 钩子记下本包的期望（P7-D10）。
2. 文件按职责拆分（`Client/ModbusClient.cs`、`Client/ModbusClient.Hooks.cs`，必要时再拆），遵守 300 / 60 行。
3. 测试（`ScriptedByteChannel` 断言线上字节，用第 21 节的向量）：
   - 每条 P7-D5 路径各一条：03、04、01、02、06、10、05、0F、16 写位、读-改-写（03 + 06）、写位 `NotSupported`；
   - `UseSingleWriteFunctions = false` 时一个字走 10、一个点走 0F；
   - 写 `IR` / `DI` 返回 `InvalidRequest` 且请求数为 0；`ReadAsync("C0", 1)` 返回 `InvalidRequest`；
   - 站号：无前缀用 `UnitId`，`s=2;` 前缀出现在线上（V-R-16）；RTU 客户端上 `s=0;` 与 `s=248;` 返回 `InvalidRequest`，MBAP 客户端上 `s=0;` 合法；
   - 拆包边界：读 125 / 126 字、读 2000 / 2001 位、写 123 / 124 字、写 1968 / 1969 线圈，126 字的第二包是 1 个字时，读取仍用 03（单功能码规则只影响写）；
   - 结束地址越界（`HR65535` 读 2 个字）返回 `InvalidRequest`；
   - 错误映射：异常应答为 `RemoteError` 加异常码；回显不符、站号不符、功能码不符为 `ProtocolViolation`，并在 `RetryCount = 1` 时重试一次；
   - 类型化读写：`ReadInt32Async` / `WriteFloatAsync` 在 `ABCD` 与 `CDAB` 下的线上字节；`ReadBoolAsync("HR100.5")` 取到寄存器值的第 5 位。
4. 提交：`feat(modbus): ModbusClient on the Plc suite`。

### 13. Task 9：0x16 与 0x17（P7-D15）

1. `Client/ModbusClient.Commands.cs`：两个公开方法，经 S2 发送；地址校验复用解析与 `ValidateRange` 的逻辑，不复制。
2. 测试：两个方法的线上字节（V-R-13、V-R-14 及其 MBAP 版本）；P7-D15 的每条拒绝规则（`IR`、`C`、带位号、两个站号不同、`readCount` 0 / 126、写入 122 个字、奇数长度、结束地址越界）都返回 `InvalidRequest` 且请求数为 0；异常应答映射；超时后按 `RetryCount` 重试。
3. 提交：`feat(modbus): mask write and read/write multiple registers`。

**里程碑 3 小结 = 可行性门槛**。汇报内容：
- 摩擦记录全文。**是否出现 S1、S2 以外的套件缺口**；
- v2 的读写能力逐项对照（第 20 节表格的每一行在 v3 里都有对应、并且已有测试）；
- Modbus 当前的有效行与 v2 的 3304 对照；
- 结论："继续"或"停下"。出现 S1、S2 以外的缺口，或者某项能力表达不出来时，**停下等用户决定**，不进入里程碑 4。

---

## 里程碑 4：设备替身、端到端与依赖注入

### 14. Task 10：设备替身与端到端

1. `TestSupport/ModbusDeviceSimulator`：
   - 基于 `PlcMemory`（存储区符号 `C`、`DI`、`IR`、`HR`），实现 01–06、0F、10、16、17；
   - 可配置应答的站号集合，不在集合里的站号不回应，用来测超时；
   - 越过配置大小的地址返回异常码 02，未实现的功能码返回 01；
   - 可注入：丢弃第 n 个请求、延迟应答（迟到应答用例）、在应答前插入噪声字节；
   - 同时支持 MBAP 与 RTU。RTU 请求的分帧用一个测试专用的请求解码器（按请求功能码定长，第 21.4 节），不要用 IdleGap（时序相关，会偶发失败）。
   - 应答编码复用协议层；替身目标 200 行以内，超过时按职责拆文件。
2. 端到端矩阵（TCP + MBAP、TCP + RTU、UDP + MBAP、UDP + RTU、串口 RTU。串口经 IVT 调用 `ModbusProtocolProfile.Configure(…, Stream, Serial)` 得到组件，再交给 `DuplexClientChannel`，与 Melsec 4C 的做法相同）：
   - 四个存储区的读写；
   - 拆包边界；
   - 类型化读写（Int32、Float、String）；
   - 一个 RTU 客户端轮询站号 1、2；未应答的站号 3 返回 `Timeout`，之后站号 1 仍然正常；
   - 异常码 02 返回 `RemoteError`；
   - **MBAP 迟到应答**：替身把第一个应答延迟到超时之后才发；下一次读取得到正确数据，TCP 连接没有重建（`AcceptedConnectionCount` 仍为 1）；
   - **RTU over TCP 迟到应答**：超时后连接被重建（`AcceptedConnectionCount` 为 2），下一次读取得到正确数据；
   - 应答前的噪声字节不影响解析（RTU 流）；
   - 静默设备加心跳进入 `Reconnecting`；断开后重连再读取；
   - 正在请求时 `Dispose`，返回失败结果而不是抛异常（循环 20 次）；
   - `RetryCount = 1` 且第一个请求被丢弃时，第二次成功；
   - `UseMaskWriteForBits` 写位后，替身里同一寄存器的其他位不变。
3. 真实串口（`SerialPairFact`，COM20 / COM21，由审阅者运行）：RTU 读写保持寄存器与线圈各一条。
4. 提交：`test(modbus): device simulator and end-to-end matrix`。

### 15. Task 11：依赖注入与 WPF 调试工具（P7-D14、D16）

1. `DependencyInjection/`：`ModbusTransport`、`ModbusClientConfig`、`ModbusClientCreator`、`ModbusServiceCollectionExtensions.AddModbus()`。
2. 测试：三种传输都能通过 `PlcClientFactory` 创建；选中的配置为 null 时抛 `ArgumentException`；配置类型不符时抛 `ArgumentException`；`AddModbus()` 重复调用幂等，并能与 `AddMelsec()` 共存；`PlcClientFactoryBuilder` 加 `ModbusClientCreator` 也能创建。
3. WPF 调试工具：用 v3 恢复连接、断开、读写保持寄存器与线圈的功能（`AddModbus()` + `PlcClientFactory`），不追求与 v2 界面逐项相同。
4. 提交：`feat(modbus): dependency injection; port the WPF debug tool to v3`。

---

## 里程碑 5：文档与验收

### 16. Task 12：README、Skill、CHANGELOG

1. `Junevy.Communication.Modbus/README.md`（英文）重写：
   - 安装；TCP、RTU、RTU over TCP 的快速开始；
   - 地址表（符号写法、Modicon 写法、位号、站号前缀）；
   - 功能码映射与单包上限；选项表；错误处理；心跳与重连；依赖注入；
   - 0x16 / 0x17；
   - **Migrating from 2.x**（第 20 节）；
   - **Verification status**（P7-D17）。
2. Skill：
   - `using-junevy-plc`：增加 "Modbus Quick Start" 与地址、帧格式小节；Protocol Author Guide 的钩子表加 `IsReadOnly`，并增加 `SendProtocolRequestAsync` 的说明；"Lessons" 增加 P7 一节（来自摩擦记录）；
   - `using-junevy-modbus`：改写为 v2 → v3 迁移指南，指向 `using-junevy-plc`。
3. README 与两个 Skill 的代码块按新增协议指南第五节编译：用脚本抽出代码块，临时控制台项目设 `ImplicitUsings=disable`、`Nullable=enable`、`TreatWarningsAsErrors=true`，net8.0 与 net472 均 0 错误、0 警告。
4. 根 `readme.md`：协议表中 Modbus 一行改为 v3 的描述并链接到包 README。
5. CHANGELOG：补齐本期各节；"破坏性变更"逐条列出 P7-D13 与第 20 节的变化；"移除"列出 v2 的公开类型。
6. 提交：`docs(modbus): README, skills and changelog for v3`。

### 17. Task 13：设计文档

1. 第 3 节的图与 3.1 表：Modbus 一行改为"Plc + Tcp + Udp + Serial，P7"，图里的 `MBV3` 去掉"可选迁移"。
2. 第 11.2 节：加一句"P7 之后 Modbus 与其他协议一致，本节的差异不再存在"。
3. 第 17 节：改为指向 20.12。
4. 第 19 节：P7 标记为已完成。
5. 新增 **20.12 实施修订（P7）**：差异表（原设计 / 实际实现 / 原因），加上"第四次扩展性验证"的结论（套件改动、摩擦记录、有效行对照）。
6. 21.1 的 Q4 后面注明"已在 P7 迁移（2026-10-19 用户决定原地替换）"。
7. 提交：`docs(spec): P7 implementation revisions`。

### 18. Task 14：知识库（只写不提交）

1. 新增 `协议细节/Modbus 协议.md`（与 MELSEC、S7、FINS 的笔记同构：地址、帧格式、分帧、关联、错误映射、上限、未验证清单），挂进 `知识库首页.md`。
2. 只描述 v2 的笔记移到 `变更与决策/Modbus v2 归档/`，开头加一行说明"适用于 2.x，已被 3.0 取代，迁移见 [[协议细节/Modbus 协议]]"：
   - `API参考/`：`ModbusRtuClient.md`、`ModbusTcpClient.md`、`功能码扩展方法.md`、`工厂与依赖注入.md`、`数据模型.md`、`核心接口与传输基类.md`；
   - `协议细节/`：`响应解析与PDU校验.md`、`帧格式与CRC.md`；
   - `架构设计/`：`整体架构概览.md`、`请求生命周期.md`。
   
   移动前先 Grep 全库的 `[[…]]` 引用，一并改掉，不留断链。
3. 更新：
   - `行为契约/` 三篇：去掉"Modbus 例外"的描述；
   - `架构设计/Plc 地址型设备协议套件.md`：S1、S2；
   - `架构设计/通讯库总体架构（协议族）.md`：分期表与第 127 行附近的"未来迁移"；
   - `Agent协作/新增协议指南.md`：P7 的结论；
   - `Agent协作/开发约定与验收基线.md`：去掉"同步 + 异步双轨只适用于 Modbus"一类的例外；
   - `变更与决策/设计决策记录.md`（P7 决策）、`变更与决策/版本历史与破坏性变更.md`（3.0.0-preview.1）；
   - `项目概述/` 下的四篇（`待办与路线图.md` 把 P7 标记为已完成）。
4. 汇报附上 `git status --short -- Junevy.Communication.Wiki` 与 `git diff --stat -- Junevy.Communication.Wiki`。不提交。

### 19. Task 15：最终度量与扩展性结论

1. 第 3 节全部度量复测，写进第 25.4 节。
2. `pwsh build/test-affected.ps1 -Base c29a45b -NoBuild -Repeat 3`：三遍结果一致，0 失败。
3. 扩展性结论：Modbus v3 的有效行，与 v2（3304）以及 Melsec / Siemens / Omron 对照；套件只改了 S1、S2；摩擦记录的条目与处置。写进设计 20.12（Task 13 已建好位置）。
4. 提交：`docs(plan): P7 final metrics`。

---

## 20. v2 → v3 迁移对照（README "Migrating from 2.x" 的内容来源）

| v2 | v3 | 说明 |
|---|---|---|
| `new ModbusTcpClient(new ModbusTcpClientConfig { Address, Port })` | `ModbusClient.Create(new TcpClientChannelConfig { Host, Port }, new ModbusClientOptions())` | 也可以经 `PlcClientFactory` 与 `ModbusClientConfig` 创建 |
| `new ModbusRtuClient(new ModbusRtuClientConfig { PortName, BaudRate, … })` | `ModbusClient.Create(new SerialChannelConfig { PortName, BaudRate, … }, new ModbusClientOptions())` | `FrameReadInterval` 取消，RTU 按功能码定长分帧 |
| 透传网关上的 RTU（v2 不支持） | `Create(new TcpClientChannelConfig { … }, new ModbusClientOptions { Framing = ModbusFraming.Rtu })` | 新能力 |
| `Connect()` / `ConnectAsync()` 返回 `bool` | `ConnectAsync()` 返回 `CommResult` | 没有同步版 |
| `Disconnect()` | `DisconnectAsync()` | 之后的请求返回 `NotConnected`，不会悄悄连回 |
| `IsConnected` | `IsConnected`、`State`、`StateChanged` | |
| `Reconnect = true`（懒重连） | 通道配置的 `Reconnect`（后台重连） | 默认都关闭 |
| `RetryCount`（默认 3）、`RetryInterval` | `ModbusClientOptions.RetryCount`（默认 0）、`RetryInterval` | 只重试 `Timeout`、`ConnectionClosed`、`ProtocolViolation` |
| `ConnectTimeout` / `ReadTimeout` / `WriteTimeout` | 通道配置的连接（TCP）/ 打开（串口）超时、请求超时、发送超时 | 属性名以各配置类为准，Task 12 编译验证 |
| `ReadHoldingRegistersAsync(slave, start, n)` → `ModbusResult<ushort[]>` | `ReadUInt16Async($"s={slave};HR{start}", n)` → `CommResult<ushort[]>` | `ReadAsync` 返回原始字节；`ReadInt32Async` 等按 `DataFormat` 解码 |
| `ReadInputRegistersAsync` | `ReadUInt16Async("IR…", n)` | |
| `ReadCoilsAsync` / `ReadDiscreteInputsAsync` | `ReadBitsAsync("C…", n)` / `ReadBitsAsync("DI…", n)` | |
| `WriteSingleRegisterAsync(slave, a, v)` | `WriteUInt16Async("HR…", v)` | 默认用 06（`UseSingleWriteFunctions`） |
| `WriteMultipleRegistersAsync(slave, a, values)` | `WriteUInt16Async("HR…", values)` 或 `WriteAsync("HR…", bytes)` | 超过 123 个字时自动拆包 |
| `WriteSingleCoilAsync` / `WriteMultipleCoilsAsync` | `WriteBoolAsync("C…", v)` / `WriteBitsAsync("C…", values)` | |
| `MaskWriteRegisterAsync(slave, a, and, or)` | `client.MaskWriteRegisterAsync("HR…", and, or)` | 寄存器写位也可以用 `UseMaskWriteForBits` |
| `ReadWriteMultipleRegistersAsync(…)` | `client.ReadWriteMultipleRegistersAsync(readAddress, readCount, writeAddress, writeData)` | 数据用字节 |
| `ReadExceptionStatus`、`Diagnostics`、`GetCommEventCounter`、`GetCommEventLog`、`ReportServerId` | 不提供 | P7-D1 |
| `Request(ModbusRequest)` / `RequestAsync(ModbusRequest)`、`ModbusHelper.BuildRequestFrame` | 不提供 | 没有原始 PDU 入口 |
| `ModbusResult<T>`、`ModbusErrorKind` | `CommResult<T>`、`CommErrorKind` | 数值对齐：`ModbusException`（6）对应 `RemoteError`（6），异常码在 `ProtocolErrorCode` |
| `ModbusException`（抛出） | 不抛出，参数错误一律返回 `InvalidRequest` | |
| `IModbusFactory`、`ModbusConnectionManager`、`ModbusFactoryBuilder`、`AddModbusFactory()` | `PlcClientFactory`、`PlcClientFactoryBuilder`、`AddModbus()` | |
| RS-485 多从站：一个客户端加多个别名，每次调用传 `slaveId` | 一个客户端，地址前缀 `s=n;` | |
| `IModbusPduValidator`、`IModbusFrameBuilder` 注入 | 不提供 | 校验是协议内部的事 |

---

## 21. 黄金向量

以下向量由审阅者按文首的协议资料逐字段拼出，CRC 用独立脚本计算。实现与向量不一致时先逐字段核对；确认是向量错误时，记入第 25.3 节并在汇报中说明（第 1 节第 9 条）。

### 21.1 PDU（规范 V1.1b3 第 6 节的示例；地址为协议地址，零基）

| 编号 | 说明 | 请求 | 应答 |
|---|---|---|---|
| V-P-1 | 01 读线圈，起始 19，19 点 | `01 00 13 00 13` | `01 03 CD 6B 05`：第 19–26 点为 1 0 1 1 0 0 1 1，27–34 点为 1 1 0 1 0 1 1 0，35–37 点为 1 0 1（每字节最低位在前，末字节高位补 0） |
| V-P-2 | 02 读离散输入，起始 196，22 点 | `02 00 C4 00 16` | `02 03 AC DB 35` |
| V-P-3 | 03 读保持寄存器，起始 107，3 个字 | `03 00 6B 00 03` | `03 06 02 2B 00 00 00 64`：HR107 = 555，HR108 = 0，HR109 = 100 |
| V-P-4 | 04 读输入寄存器，起始 8，1 个字 | `04 00 08 00 01` | `04 02 00 0A` |
| V-P-5 | 05 写线圈 172 = ON | `05 00 AC FF 00` | 原样回显 |
| V-P-6 | 06 写 HR1 = 3 | `06 00 01 00 03` | 原样回显 |
| V-P-7 | 0F 写线圈，起始 19，10 点 | `0F 00 13 00 0A 02 CD 01`：1 0 1 1 0 0 1 1 1 0 | `0F 00 13 00 0A` |
| V-P-8 | 10 写 HR1–HR2 = 0x000A、0x0102 | `10 00 01 00 02 04 00 0A 01 02` | `10 00 01 00 02` |
| V-P-9 | 16 掩码写 HR4，AND 0x00F2，OR 0x0025 | `16 00 04 00 F2 00 25` | 原样回显 |
| V-P-10 | 17 读 HR3 起 6 个字，写 HR14 起 3 个字 = 0x00FF × 3 | `17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF` | `17 0C 00 FE 0A CD 00 01 00 03 00 0D 00 FF` |
| V-P-11 | 03 的异常应答，异常码 02 | — | `83 02` → `RemoteError`，`ProtocolErrorCode = 2`，消息 `Modbus exception 0x02 (Illegal Data Address) for function 0x03.` |
| V-P-12 | 掩码写位 HR100.5 = 1 / = 0 | `16 00 64 FF DF 00 20` / `16 00 64 FF DF 00 00` | 原样回显 |
| V-P-13 | 05 写线圈 0 = OFF | `05 00 00 00 00` | 原样回显 |
| V-P-14 | 0F 写线圈 0–2 = 1 0 1 | `0F 00 00 00 03 01 05` | `0F 00 00 00 03` |
| V-P-15 | 06 写 HR100 = 0x1234 | `06 00 64 12 34` | 原样回显 |

### 21.2 RTU（站号 + PDU + CRC，CRC 低字节在前）

标 ★ 的 5 条与公开资料广泛引用的样例一致，可以先用来验证 CRC 实现。

| 编号 | 说明 | 字节 |
|---|---|---|
| V-R-1 ★ | 03 读 HR0 起 10 个字 | `01 03 00 00 00 0A C5 CD` |
| V-R-2 ★ | 03 读 HR0 1 个字（站号 1 的心跳探测） | `01 03 00 00 00 01 84 0A` |
| V-R-3 ★ | 03 应答，值 1 | `01 03 02 00 01 79 84` |
| V-R-4 ★ | 06 写 HR1 = 3（应答原样回显） | `01 06 00 01 00 03 98 0B` |
| V-R-5 ★ | 05 写线圈 0 = ON（应答原样回显） | `01 05 00 00 FF 00 8C 3A` |
| V-R-6 | V-P-3 | 请求 `01 03 00 6B 00 03 74 17`；应答 `01 03 06 02 2B 00 00 00 64 05 7A` |
| V-R-7 | V-P-1 | 请求 `01 01 00 13 00 13 8C 02`；应答 `01 01 03 CD 6B 05 42 82` |
| V-R-8 | V-P-2 | 请求 `01 02 00 C4 00 16 B8 39`；应答 `01 02 03 AC DB 35 22 88` |
| V-R-9 | V-P-4 | 请求 `01 04 00 08 00 01 B0 08`；应答 `01 04 02 00 0A 39 37` |
| V-R-10 | V-P-5（应答原样回显） | `01 05 00 AC FF 00 4C 1B` |
| V-R-11 | V-P-7 | 请求 `01 0F 00 13 00 0A 02 CD 01 72 CB`；应答 `01 0F 00 13 00 0A 24 09` |
| V-R-12 | V-P-8 | 请求 `01 10 00 01 00 02 04 00 0A 01 02 92 30`；应答 `01 10 00 01 00 02 10 08` |
| V-R-13 | V-P-9（应答原样回显） | `01 16 00 04 00 F2 00 25 67 EE` |
| V-R-14 | V-P-10 | 请求 `01 17 00 03 00 06 00 0E 00 03 06 00 FF 00 FF 00 FF 46 91`；应答 `01 17 0C 00 FE 0A CD 00 01 00 03 00 0D 00 FF 1D 79` |
| V-R-15 | 03 的异常应答，异常码 02 | `01 83 02 C0 F1` |
| V-R-16 | 站号 2：03 读 HR100 起 2 个字，值 `12 34 56 78` | 请求 `02 03 00 64 00 02 85 E7`；应答 `02 03 04 12 34 56 78 B2 07` |
| V-R-17 | V-P-12（应答原样回显） | 置位 `01 16 00 64 FF DF 00 20 87 F8`；清位 `01 16 00 64 FF DF 00 00 86 20` |
| V-R-18 | V-P-15 与回读 | 写 `01 06 00 64 12 34 C5 62`；读请求 `01 03 00 64 00 01 C5 D5`；读应答 `01 03 02 12 34 B5 33` |
| V-R-19 | V-P-13、V-P-14 与回读 | 写 OFF `01 05 00 00 00 00 CD CA`；写 3 点 `01 0F 00 00 00 03 01 05 4F 54`；读 3 点的应答 `01 01 01 05 91 8B` |
| V-R-20 | 10 的异常应答，异常码 03 | `01 90 03 0C 01` |

### 21.3 MBAP（事务号 2 字节 + 协议号 `00 00` + 长度 2 字节〔计站号与 PDU〕+ 站号 + PDU）

| 编号 | 说明 | 字节 |
|---|---|---|
| V-M-1 | V-P-3，事务号 1，站号 1 | 请求 `00 01 00 00 00 06 01 03 00 6B 00 03`；应答 `00 01 00 00 00 09 01 03 06 02 2B 00 00 00 64` |
| V-M-2 | V-P-8，事务号 2 | 请求 `00 02 00 00 00 0B 01 10 00 01 00 02 04 00 0A 01 02`；应答 `00 02 00 00 00 06 01 10 00 01 00 02` |
| V-M-3 | 异常应答，事务号 3 | `00 03 00 00 00 03 01 83 02` |
| V-M-4 | 站号 0xFF，V-P-1，事务号 4 | 请求 `00 04 00 00 00 06 FF 01 00 13 00 13`；应答 `00 04 00 00 00 06 FF 01 03 CD 6B 05` |
| V-M-5 | 协议号非 0（`ProtocolViolation`） | `00 05 00 01 00 05 01 03 02 00 01` |
| V-M-6 | 键提取：V-M-1 的请求与应答都取到 1；事务号 `FF FF` 取到 65535；不足 2 字节返回 false | — |

### 21.4 RTU 应答帧长（`ModbusRtuFrameDecoder`）与请求帧长（测试替身）

| 功能码 | 应答总长 | 请求总长（替身） |
|---|---|---|
| 01、02、03、04 | 3 + 字节数（第 2 字节）+ 2 | 8 |
| 05、06 | 8 | 8 |
| 0F、10 | 8 | 7 + 字节数（第 6 字节）+ 2 |
| 16 | 10 | 10 |
| 17 | 3 + 字节数（第 2 字节）+ 2 | 11 + 字节数（第 10 字节）+ 2 |
| 异常（功能码 \| 0x80） | 5 | — |
| 其他 | 丢弃 1 字节，重新同步 | 替身不回应 |

### 21.5 码表与上限

| 类别 | 值 |
|---|---|
| 功能码 | 01 Read Coils，02 Read Discrete Inputs，03 Read Holding Registers，04 Read Input Registers，05 Write Single Coil，06 Write Single Register，0F Write Multiple Coils，10 Write Multiple Registers，16 Mask Write Register，17 Read/Write Multiple Registers |
| 异常码名称（只用这 9 个） | 01 Illegal Function，02 Illegal Data Address，03 Illegal Data Value，04 Server Device Failure，05 Acknowledge，06 Server Device Busy，08 Memory Parity Error，0A Gateway Path Unavailable，0B Gateway Target Device Failed to Respond |
| 单包上限 | 读位 2000（0x7D0），读寄存器 125（0x7D），写线圈 1968（0x7B0），写寄存器 123（0x7B），0x17 读 125、写 121 |
| 帧上限 | RTU ADU 256 字节；MBAP ADU 260 字节 |
| 线圈值 | ON `FF 00`，OFF `00 00` |
| 站号 | RTU 1–247（0 广播，248–255 保留）；MBAP 0–255 |

---

## 22. v2 测试的处置（Task 0 填"用例数"，审阅者据此核对迁移没有漏掉行为）

| v2 测试文件 | 用例数 | 处置 |
|---|---|---|
| `ParserCorpus.cs`、`ParserSpecTests.cs` | 13 Fact（`ParserCorpus.cs` 无 `[Fact]`，是数据源） | 本期范围内的功能码改写为第 21 节的向量测试（Task 5）；诊断类不做（P7-D1） |
| `ModbusProtocolTests.cs` | 31 Fact | CRC 与帧封装 → V-R、V-M 向量（Task 5） |
| `ModbusPduValidatorTests.cs` | 23（8 Fact + 1 Theory 的 15 行） | 回显与字节数校验 → Task 5 的 `ProtocolViolation` 用例 |
| `ExtensionRequestTests.cs`、`ExtensionSurfaceTests.cs` | 3 Theory†（行数未展开）+ 1 Fact | 功能码请求构造 → Task 8 的线上字节用例；扩展方法表面由迁移对照取代 |
| `ModbusExtensionsErrorKindTests.cs` | 9 Fact + 1 Theory† | 错误分类 → Task 8 的错误映射用例 |
| `TransportBaseRetryTests.cs` | 9 Fact | 重试规则 → 套件已有（Plc.Tests 的 `PlcClientRetryTests`）+ Task 8、Task 10 的重试用例 |
| `TransportBaseAsyncConnectTests.cs`、`ConnectAsyncTokenTests.cs`、`TcpConnectTests.cs` | 3 + 5 + 6 = 14 Fact | 连接语义 → 通道族已有（Tcp.Tests、Channels.Tests）；Modbus 只保留端到端的断开重连 |
| `TcpAsyncTimeoutTests.cs`、`TcpProtocolViolationTests.cs` | 6 + 4 = 10 Fact | 超时与协议违规 → 通道族已有 + Task 10 的迟到应答与噪声用例 |
| `DisposeConcurrencyTests.cs` | 10 Fact | 释放并发 → 套件与通道已有 + Task 10 的"请求中释放"循环 |
| `ModbusFactoryTests.cs`、`FactoryStrategyTests.cs` | 23 + 15 = 38 Fact | → Task 11 的依赖注入用例；注册表行为由 Core 的 `NamedRegistry` 测试覆盖 |
| `ConnectionManagerAliasTests.cs` | 10 Fact | 别名由 Core 的 `NamedRegistry` 测试覆盖；多从站改为站号前缀（Task 8、Task 10） |
| `ModbusResultTests.cs` | 6 Fact | 类型已删除；`CommResult` 由 Core.Tests 覆盖 |

---

## 23. 审阅者的验收清单（执行者据此自查）

**代码**

- 第 2 节 Q1–Q30、Q44–Q47 逐条检查；第 3 节度量达标。
- 架构测试通过，Modbus 在分层表中，且不再有特殊处理。
- 公开 API 与 P7-D3 逐条比对，不多不少（用反射列出 Modbus 程序集的公开类型与成员，贴进汇报）。
- 套件改动只有 S1、S2，Melsec / Siemens / Omron 的 diff 为空。
- `ModbusProtocolProfile` 的组合规则在 `PlcComponentWiring.Apply` 之前检查。
- 期望只在 Build 钩子里记录一次（Q47）。
- `Create` 构造失败时释放通道。
- README 与 Skill 的示例能够编译。
- v2 零残留（Q45）：`git grep` 上述类型名，只在 CHANGELOG、迁移文档、计划与归档笔记中出现。

**功能**（审阅者会复跑全部受影响测试三遍，并做变异检查：临时改坏下列规则，确认有测试失败）

- Modicon 换算的减 1，以及 5 位写法的上限 9999；
- RTU 站号范围 1–247；
- 单功能码规则（06 / 05 只在一个点时使用）；
- 掩码的取反（`~(1 << n)`）；
- 线圈打包的位序（每字节最低位在前）；
- RTU 解码器的 CRC 校验与重新同步；
- MBAP 的 Keyed 关联（改成 Sequential 后，迟到应答用例必须失败）；
- 回显校验（05、06、0F、10、16 各一处）；
- 异常应答映射为 `RemoteError` 并带异常码；
- S1：写只读地址时不发请求；
- S2：`RemoteError` 不重试；
- 0x17 不拆包。

---

## 24. 未纳入本计划的条目

- Modbus ASCII、从站 / 服务端（Modbus 服务器）、诊断类功能码（07、08、0B、0C、11）、文件记录（14、15）、FIFO（18）、设备标识（2B）、RTU 广播、自定义功能码与原始 PDU 入口。
- 字符串的字节序独立于数值字序（例如寄存器内低字节在前的字符串）：与 P4 相同，只能通过 `DataFormat` 整体指定。
- AutomationSystem 的 `Communication.Modbus` 适配层迁移到 v3（在 AutomationSystem 仓库单独做，参照第 20 节；它目前只用 01–04、06、0F、10，每个通道一个站号，自己做值编码）。
- 正式发布与 `nuget push`（知识库 `待办与路线图.md` 的"正式发布"）。
- 真机验证（P7-D17）。

---

## 25. 验收记录

### 25.1 各 Task 验收（审阅者填写）

#### 第一轮验收（2026-10-19，审阅者）

**结论：不通过，修正后复验。** 架构与主体实现质量高，问题集中在一个分帧器的健壮性缺陷、一个测试缺口和两处会误导使用者的文档。R1–R3 修完、R4–R10 处理完之后复验。

**复跑结果**

- 构建：`dotnet build Junevy.Communication.slnx -c Debug --no-incremental` 与 Release 各 0 错误、2 条警告（同一条 WPF 调试工具 `App.xaml.cs(15,33)` 的 CS8618，P7 之前就有）。基线 31 条，Modbus 0 条。
- 测试：`pwsh build/test-affected.ps1 -Base c29a45b -NoBuild -Repeat 3`，三遍一致：每遍通过 2727、失败 0、跳过 4（Architecture 15/12，Melsec 184+1/184+1，Modbus 307+1/307+1，Omron 260/260，Plc 283/283，Siemens 316/316）。与 25.3 Task 15 一致。
- 真实串口对：`JUNEVY_SERIAL_PAIR=COM20,COM21` 运行 `RtuReadAndWrite_OverARealSerialPair`，net8.0 与 net472 均通过。
- 度量（第 3 节口径，16 个生产目录）：重复窗口 35（10 个集群），Modbus 贡献 0；超过 60 行的方法 0；Modbus 没有超过 300 行的文件；有效行 Modbus 1215、Plc 1285。与 25.4 一致。
- 公开面：9 个类型，与 P7-D3 一致（计划原写的"10"是审阅者的计数错误，执行者改正正确）。
- 套件改动只有 S1、S2；`git diff c29a45b` 对 Melsec、Siemens、Omron 的生产代码与测试为空；Core、Channels、Tcp、Udp、Serial、Testing 为空。

**变异检查**（每次只改一处，重编 net8.0 并运行受影响的测试项目，完成后 `git checkout` 还原）：24 个变异里 23 个被测试发现。

| # | 变异 | 结果 |
|---|---|---|
| M1 | Modicon 换算不减 1 | 发现（9 个用例失败） |
| M2 | 6 位 Modicon 上限 65536 → 65537 | 发现 |
| M3 | RTU 站号上限 247 → 248 | 发现 |
| M4 / M5 | 单功能码规则（06 / 05）失效 | 发现 |
| M6 | AND 掩码不取反 | 发现 |
| M7 | 线圈打包改为最高位在前 | 发现 |
| M8 | RTU 解码器不校验 CRC | 发现 |
| M9 | MBAP 改为 Sequential | 发现（迟到应答用例） |
| M10 | 不校验回显 | 发现 |
| M11 | 异常应答映射为 `ProtocolViolation` | 发现 |
| M12 | S1：只读检查失效 | 发现（Plc 5、Modbus 2） |
| M13 | S2：`RemoteError` 被重试 | 发现 |
| M14 | 0x17 读字数上限放宽 | 发现 |
| M15 | 不校验 MBAP 长度字段 | 发现 |
| M16 | 不校验 RTU 站号 | 发现 |
| M17 | 不校验字节数 | 发现 |
| M18 | 读上限 125 → 126 | 发现 |
| M19 | 探测把异常应答判为不健康 | 发现 |
| M20 | 结束地址上限放宽 1 | 发现 |
| M21 | IR 改为可写 | 发现 |
| M22 | RTU 剥离不校验 CRC | 发现 |
| M23 | 线圈允许按字访问 | 发现 |
| M24 | S2 去掉操作锁 | 发现 |
| **M25** | 开启 `UseMaskWriteForBits` 时，`HR` 写位的单包上限 1 → 16 | **未发现**（见 R2） |

**必须修**

- **R1 RTU 分帧器在帧长超限时抛异常**（`Framing/ModbusRtuFrameDecoder.cs`）。字节数 252–255 时声明帧长超过 256，解码器抛 `FrameDecodeException`。合法应答的字节数最多 250（125 个字或 2000 个点），超过就只可能是噪声。抛异常的后果是：RTU over TCP 上通道以 `ProtocolViolation` 断开连接；串口上通道丢弃整个缓冲，把紧随其后的正确应答一起丢掉。这与 P7-D8 的"丢弃 1 个字节后重新同步，不抛异常"矛盾。矛盾来自计划本身（第 10 节第 2 条写了"抛 FrameDecodeException"），执行者已在 Task 6 报告里指出，**按 P7-D8 处理**：字节数大于 250 视为功能码未知，丢弃 1 个字节后继续。`DeclaredLengthBeyondTheMaximum_IsAFrameDecodeException` 改为"丢弃后交出紧随其后的正确帧"。第 10 节第 2 条的文字同步改正。
- **R2 掩码写位的多点写入没有测试**（M25 存活）。`BuildWriteBitsRequest` 在寄存器上只取 `values[0]`，依赖 `GetMaxWritePoints(Bits) == 1` 把多点写入拆成每包一位；这个上限改成 16 后，多点写入会悄悄只写第一位，现有测试全部通过。补一条线上字节测试：`UseMaskWriteForBits = true` 时 `WriteBitsAsync("HR100.14", true, false, true)` 发出 3 个 16 请求，第三个落在 `HR101` 的位 0；再补一条端到端测试，核对替身内存里三个位与相邻位。补完后 M25 必须被发现。
- **R3 README 的重连说明有两处错误**（`Junevy.Communication.Modbus/README.md` "Heartbeat and reconnect" 的最后一段）：
  - "On a byte-stream transport, a request timeout resets the connection by default"：MBAP 是 Keyed 关联，超时**不**重建连接（端到端用例 `MbapLateReply_…` 断言连接数为 1），只有 RTU over TCP（Sequential）才重建；
  - "wait for it with `WaitForConnectedAsync` before the next request"：照做没有用。请求超时后，通道要等后台任务处理完断开才离开 `Connected`，在这之前 `WaitForConnectedAsync` 立即返回 true（这正是执行者报告的竞争）。测试里的做法是先等 `IsConnected` 变为 false，再等重连。

  改为如实说明：MBAP 不重建；RTU over TCP 超时后重建，紧接着的请求或重试可能因此失败（已知问题，见 R8），并给出测试里那种可行的等待方式。

**应该修**

- **R4 `Protocol/ModbusBuffer.cs` 与 Core 重复**：`WriteByte`、`Write` 就是 Core 的 `BigEndianFields.WriteUInt8`、`WriteBytes`（P4.5 抽出的共用件，新增协议指南第八节 F8："新协议直接用，不要再写第四份"）。删除 `ModbusBuffer`，调用处改用 `BigEndianFields`。
- **R5 RTU 站号范围写了两份**：`ModbusClient.Hooks.cs` 与 `ModbusProtocolProfile.cs` 各有一对 `MinimumRtuUnitId` / `MaximumRtuUnitId`。只保留一处（例如 `RtuAdu` 上的两个常量），两处引用它。
- **R6 CHANGELOG 留有中途的过时说明**，改为最终状态：
  - "破坏性变更"中的"3.0.0-preview.1 尚无替代类型……README 的 'Migrating from 2.x' 在 Task 12 补充"；
  - "变更"中的"本次提交尚无公开类型""Modbus 在 Task 4 登记进分层表之前……""调试页签的绑定在 Task 11 恢复前不可用"；
  - "新增"中分帧一条的"帧长超过 256 字节时抛出 `FrameDecodeException`"（随 R1 修改）。
- **R7 F-A2 的决定：把 `IClientChannel` 的脚本替身移进共享测试库。** `ScriptedModbusChannel` 是 `Plc.Tests/TestSupport/ScriptedClientChannel.cs` 的删减副本，与它形成 37 个测试重复窗口，这正是 P4.6 要消除的那类重复。把两份合并为 `Junevy.Communication.Testing` 里的一个类型（保留两边用到的全部成员：`Enqueue`、`LastRequest`、`WaitTimeouts`、`HasStateChangedSubscribers` 等），Plc.Tests 与 Modbus.Tests 都改用它，删除两份旧文件，不留转发层。改 Testing 会让 `test-affected` 选中几乎全部测试项目，三遍全量运行一次。
- **R8 重试竞争的描述范围不对。** 25.2、25.3 Task 10、设计 20.12、Skill 的 P7 教训与知识库 `协议细节/Modbus 协议.md` 都写成"RTU over TCP 的问题"。实际上任何字节流通道上 `Sequential` 加 `ResetOnRequestTimeout` 的客户端都会遇到：S7、FINS 与 TCP 上的 MELSEC 3E 也一样。原因是 `StreamChannel.ResetAfterExpiry` → `ConnectionSupervisor.ReportConnectionLost` 只调度后台的 `HandleConnectionLostAsync`，状态在那之前仍是 `Connected`，`PlcPacketExchange` 的重试调用 `WaitForConnectedAsync` 会立即返回 true。几处文字改为"字节流 + Sequential + `ResetOnRequestTimeout` 的通用问题，P7 之前就存在"。修复不在本期范围内，作为后续任务单独处理。
- **R9 其他程序集里指向已删除 v2 类型的注释**：
  - `Channels/Abstractions/IConnectable.cs:26`（`IModbus.ConnectAsync`）；
  - `Channels/Factory/ChannelFactoryBuilder.cs:6`、`WebApi/Factory/WebApiClientFactoryBuilder.cs:6`（`ModbusFactoryBuilder`）；
  - `Core/Registry/NamedRegistry.cs:10`（`ModbusConnectionManager`）；
  - `Core/Results/CommErrorKind.cs:5`（"未来 Modbus 迁移时可直接映射"）；
  - `Tcp/Client/TcpConnector.cs:201`（`ModbusTcpClient.OpenConnectionAsync`）；
  - `Tests/Junevy.Communication.Core.Tests/CommResultTests.cs:101`。

  执行者遵守第 1 节第 6 条没有改，这是正确的。现在**作为例外允许只改注释**：改成指向 v2 的 tag 或去掉过时部分，例如"规则来自 v2 的 `ModbusConnectionManager`（tag `modbus-v2.0.0`）"，CommErrorKind 改为"Modbus 3.0 已按此映射"。
- **R10 小的整理**：`PlcRequestPipeline.TryResolveWritable` 的结尾 `if (failure is not null) return false; return true;` 改为 `return failure is null;`，它和 `CheckWritable` 上方两段几乎相同的注释合并为一段。

**对执行者提出的开放问题的决定**

- 256 字节的处理：按 P7-D8 重新同步（R1）。
- F-A1（声明式解析器的表达力）：**本期与近期都不扩展套件。** 两个自写解析器（S7、Modbus）各自清楚，抽象它们没有第三个使用者；记在设计 20.12 即可。
- F-A2：移进共享测试库（R7）。
- F-A3：`DelayingStream` 留在 Modbus.Tests。只有一个使用者，与其他项目之间只有 Stream 覆写样板的 2 个窗口，接受。
- Modbus.Tests 内部 2 个窗口（TCP 与 UDP 的 `Theory` 样板）：接受。
- Task 6–11 没有先单独跑红，改用变异检查替代：执行者诚实记录了，本轮的 24 个变异也证明测试有效，接受。下一期仍按"先红后绿"执行。
- 第 25.3 节缺 Task 14（知识库）的汇报：补上，附 `git status --short -- Junevy.Communication.Wiki` 与 `git diff --stat -- Junevy.Communication.Wiki`。

### 25.2 扩展性摩擦记录（执行者从 Task 3 起填写；格式：现象 / 变通或缺口 / 建议的通用能力）

| # | Task | 现象 | 变通或缺口 | 建议的通用能力 |
|---|---|---|---|---|
| F-S1 | 计划 | 套件的 `ValidateRange` 不区分读写，无法拒绝写只读存储区 | 已列为 S1（P7-D2） | `IsReadOnly` 钩子 |
| F-S2 | 计划 | 套件只有读写入口，0x16 / 0x17 无法复用操作锁与重试 | 已列为 S2（P7-D2） | `SendProtocolRequestAsync<T>` |
| F-A1 | Task 4 | `PlcAddressParser` 只能解析“符号 + 编号 + 位号”，无法表达站号前缀 `s=n;`，也无法表达 Modicon 的一基写法（首位表示存储区，编号按位数换算） | 自写 `ModbusAddressParser`（internal），空白与大小写约定与套件一致；不改套件 | 地址解析的前缀段与编号换算钩子（与 S7 的 F1 同类），供以后的协议声明式地表达 |
| F-A2 | Task 8 | `PlcClientBase` 的测试替身（`ScriptedClientChannel`）在 Plc.Tests 内部，共享测试库只有 `ScriptedByteChannel`（`IByteChannel`），无法给 `IClientChannel` 的客户端使用，因此 Modbus.Tests 复制了一份（`ScriptedModbusChannel`） | 复制一个同形的替身（约 200 行）；计划第 1 节第 6 条要求改共享测试库时先记摩擦，由审阅者决定 | 在 `Junevy.Communication.Testing` 提供 `IClientChannel` 的脚本替身，供所有协议测试共用（审阅者决定） |
| F-A3 | Task 10 | 共享测试库的 `DeviceSimulator` 的回复是同步的（回调返回即写出），无法在 TCP 上只延迟一个回复而不阻塞后续请求；`DeviceSimulatorTcpHost` 没有回复延迟 | 在替身外层包一个 `DelayingStream`：延迟的写入在后台完成，分帧循环立即继续（`TestSupport/DelayingStream.cs`，`ModbusTcpSlave.cs`） | 在共享测试库提供异步的回复延迟（审阅者决定） |

### 25.3 执行汇报与偏差（执行者填写：每个 Task 的汇报、里程碑小结、偏差与是否需要审阅者决定）

#### Task 0（基线，2026-10-19）

- 新建：本地 tag `modbus-v2.0.0` 指向 `c29a45b`（未 push）；分支 `feat/modbus-p7` 从 `c29a45b` 切出。
- 工作区的预先状态（不是本计划的改动，未纳入任何提交）：`Junevy.Communication.Wiki/zh/content/项目概述/待办与路线图.md` 有未提交修改。Task 14 要改同一文件，届时需先确认它的去向。因此"master 干净"只在 HEAD 与代码层面成立。
- 构建（Task 0 不跑测试）：
  - Debug：31 个警告、0 个错误；Release：31 个警告、0 个错误（均 `--no-incremental`）。
  - 来自 Modbus 的 27 条：库 `Junevy.Communication.Modbus` 的 2 条（net472 与 net8.0 各一次，共 4 次出现）+ 测试项目 23 条；按项目去重后为 25 条。
  - WPF 调试工具 `Junevy.Communication.Test` 2 条，另有其临时项目 `…_wpftmp` 2 条。
- 第 21.2 节 RTU 向量的 CRC：用 tag 上的 v2 `Crc16Helper.ComputeCrc` 与 `BinaryExtensions` 编译的一次性控制台程序（scratchpad，未提交）重算，33 帧全部一致，0 个不一致。★ 的 5 条也一致。V-R-20 的 CRC 字节为 `0C 01`，与计算值一致。
- 第 22 节"用例数"：静态计数（`[Fact]` 与 `[InlineData]` 行数），未运行测试。
- 偏差：第 3 节 P4.6 口径的测试重复窗口基线写的是 52 个集群，实测为 53 个集群（窗口数 192 一致）。按实测记录，计划文字不改。

#### Task 1（S1 只读地址，2026-10-19）

- 修改：`Junevy.Communication.Plc/Client/PlcClientBase.Hooks.cs`（`IsReadOnly`，默认 false）、`PlcClientBase.cs`（类注释的钩子契约）、`Pipeline/PlcRequestPipeline.cs`（`CheckWritable`）、`PlcRequestPipeline.Words/Bits/Bytes.cs`（三个写入口在地址解析后调用）、`Junevy.Communication.Plc/README.md`（钩子表增加一行）、`CHANGELOG.md`（P7 节，新增）。
- 测试：`Tests/Junevy.Communication.Plc.Tests/PlcClientReadOnlyTests.cs`（8 个用例）；`TestSupport/FakePlcClient.cs` 增加只读软元件 R（字）、N（位）、Y（字节），并覆写 `IsReadOnly`。
- 先红：未改生产代码时，`FakePlcClient` 覆写 `IsReadOnly` 报 CS0115（net472 与 net8.0 各一次），即编译失败。
- 后绿：Plc.Tests 270/270（net472 与 net8.0）。
- 顺序（执行者确认，写进汇报）：只读检查位于地址解析之后、点数与值检查之前，并且早于连接检查。断开状态下写只读地址返回 `InvalidRequest`，不返回 `NotConnected`。
- 第 2 节自查：符合。注释为中文，错误消息为英文，没有新增 await，没有使用 net472 禁用 API；S1 的代码、注释与消息中不含 "Modbus"（Q46）。
- 度量：Plc 有效行 1231 → 1245（+14）。
- 三条命令：Debug 31 个警告、0 个错误；Release 31 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12（net8.0 / net472），Melsec 184（跳过 1）/ 184（跳过 1），Omron 260 / 260，Plc 270 / 270，Siemens 316 / 316。
- 偏差：计划预期本里程碑会选中 Modbus.Tests，实际未被选中，因为 Modbus 尚未引用 Plc（Task 3 之后才会）。记录在此，不影响结论。Melsec 的跳过用例是 `SerialPairFact`（未设置 `JUNEVY_SERIAL_PAIR`），与 master 一致。

#### Task 2（S2 协议专有请求，2026-10-19）

- 新增：`Junevy.Communication.Plc/Pipeline/PlcRequestPipeline.Protocol.cs`（守卫与操作锁）、`Tests/Junevy.Communication.Plc.Tests/PlcClientProtocolRequestTests.cs`（13 个用例，含 `ProtocolRequestClient` 测试替身）。
- 修改：`PlcClientBase.cs`（`SendProtocolRequestAsync<T>`，参数校验同步抛出）、`PlcPacketExchange.cs`（`ExchangeProtocolRequestAsync<T>`，复用 `ExchangeAsync` 的重试循环与 `RunPacketsAsync` 的日志，没有复制第二份循环）、`PlcRequestPipeline.cs`（持有包交换的引用）、`Plc/README.md`（新增 "Protocol-specific requests (P7-S2)" 一节）、`CHANGELOG.md`。
- 解析委托的处理：选择闭包方式（`ExchangeAsync` 的解析委托保持非泛型），没有改动 `ExchangeAsync` 的签名，与分包读取的 `Collect*` 做法一致。
- 日志偏差：分包日志的模板含 `{Points}`，协议请求没有点数。复用同一模板，记为 1 点、1 包，因此日志是 `SendProtocolRequestAsync <操作名>: 1 point(s) in 1 packet(s), …`。"point" 用词不贴切，记在此处，不另建模板。
- 先红：未实现 `SendProtocolRequestAsync` 时，测试项目报 CS0103（两个目标各一次）；另外修正了测试代码中两处 CS0411（泛型无法从 `null!` 或只抛异常的 lambda 推断），之后仍是 CS0103。
- 后绿：Plc.Tests 283/283（net472 与 net8.0）。
- 变异检查：临时去掉操作锁（`ProtocolRequestAsync` 直接调用 `ExchangeProtocolRequestAsync`），`RunsSeriallyWithReadAsync` 在两个目标上都失败（"The collection contained 2 items"），随后还原并确认文件与测试时完全一致。
- 串行测试的说明：负向断言（读取在协议请求解析期间没有发出）依赖"等待 100 次让出"。有锁时它必然通过，无锁时大概率失败；变异检查证明它能发现缺失的锁。它不是严格的时序证明，审阅者如需要可以换成更强的机制。
- 第 2 节自查：符合。`ConfigureAwait(false)` 写在包交换的新 await 上；注释为中文，消息为英文；S2 的代码与消息中没有 "Modbus"（Q46 通过 grep 确认）；没有重复的重试循环（Q47 不适用于本 Task，但遵守了"不复制"的要求）。
- 度量：Plc 有效行 1231（基线）→ 1245（S1，+14）→ 1285（S2，+40）；累计 +54。
- 三条命令：Debug 31 个警告、0 个错误；Release 31 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。
- 里程碑 1 小结：
  - S1、S2 是计划允许的两项套件改动，共 54 个有效行（Plc 1231 → 1285）。
  - Melsec、Siemens、Omron：`git diff c29a45b --stat` 对它们的生产代码与测试为空；测试数与 Task 1 的运行一致（Melsec 184 + 1 跳过，Siemens 316，Omron 260）。Task 0 没有运行测试（计划第 4 节第 6 条），因此"与 Task 0 一致"只能对照 Task 1 之后的运行。
  - 三个协议的测试全部通过。
  - 未发现 S1、S2 以外的套件缺口，里程碑 2 可以开始。

#### Task 3（替换项目骨架，2026-10-19）

- 删除（`git rm`）：`Junevy.Communication.Modbus/` 下的 2.x 生产代码 41 个 `.cs`（Core、DependencyInjection、Extensions、Factory、Rtu、Tcp、Utils）；`Tests/Junevy.Communication.Modbus.Tests/` 下的 2.x 测试 23 个 `.cs`（含 `TestSupport/`）。
- 修改：`Junevy.Communication.Modbus/Junevy.Communication.Modbus.csproj`（导入 Common.props，覆写 `3.0.0-preview.1`，依赖 Plc、Tcp、Udp、Serial，`InternalsVisibleTo` 只给测试项目，去掉 v2 的 NuGet 直接引用与 `CopyLocalLockFileAssemblies`）；`Tests/…Modbus.Tests.csproj`（导入 Tests.props，引用 Modbus 与 Testing，与 Melsec.Tests 同形）；`Tests/Junevy.Communication.Architecture.Tests/ArchitectureTests.cs`（删除 Modbus 的特殊处理，两行）；WPF 调试工具（`Junevy.Communication.Test` 的 csproj、`App.xaml.cs`、`MainWindowViewModel.cs`）；`Junevy.Communication.Modbus/README.md`（顶部加一行迁移提示，正文在 Task 12 重写）；`CHANGELOG.md`。
- 新增：`Tests/Junevy.Communication.Modbus.Tests/ProjectSmokeTests.cs`（2 个用例：程序集版本 3.0.0.0；信息版本以 `3.0.0-preview.1` 开头）、`SocketTimingCollection.cs`（与 Plc.Tests 同形，命名空间改为 Modbus.Tests）。
- 先红：在 v2 代码上运行 `ProjectSmokeTests`，2 个用例失败（版本 2.0.0.0；信息版本不符）。之后才替换项目。
- 后绿：`ProjectSmokeTests` 2/2（net472 与 net8.0）。
- WPF 调试工具的处理：视图模型只保留窗口骨架（`MainWindowViewModel` 无成员），`App.xaml.cs` 去掉 `AddModbusFactory`。`MainWindow.xaml` 的 "Modbus RTU Test" 页签的绑定现在没有目标，运行时会有绑定错误，编译不受影响；Task 11 恢复。`ModbusData.cs`、`MbCmd.cs` 不依赖库，保留给 Task 11 使用。
- 偏差：计划第 4 节 Task 3 没有提到 `README.md` 的提示，AGENTS.md 要求破坏性变更时更新 README。我在 README 顶部加一行迁移提示，正文仍由 Task 12 重写。
- 偏差：Architecture 测试在 Modbus 登记进分层表（Task 4）之前不检查 Modbus 的 InternalsVisibleTo。这是计划的安排（第 3 节第 4 条），记录在此。
- Q45：生产代码、测试与 WPF 工具中没有 v2 类型名（grep 确认）；`ModbusData` 不是 v2 类型。
- 度量：Modbus 有效行 3304 → 0（骨架）；Plc 仍为 1285；Modbus 警告 0。
- 三条命令：Debug 2 个警告（均为 WPF 工具 `App.xaml.cs` 的 CS8618，改动前即存在，原 31 中的一部分）、0 个错误；Release 2 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Modbus 2 / 2，Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。

#### Task 4（存储区与地址，P7-D4，2026-10-19）

- 新增：`Junevy.Communication.Modbus/Addressing/ModbusDevice.cs`（sealed，四个静态实例，`IPlcDeviceDefinition`；内部的读写功能码）、`ModbusAddress.cs`（`PlcAddress` 派生，`Device` 收窄，`UnitId`）、`ModbusAddressParser.cs`（internal 静态类，纯语法）；测试 `Tests/…Modbus.Tests/ModbusAddressParserTests.cs`（表驱动，37 个用例 + 2 个存储区用例）。
- 修改：`Tests/Junevy.Communication.Architecture.Tests/ArchitectureTests.cs`（分层表加一行：Modbus 允许 Plc、Channels、Core、Tcp、Udp、Serial）；`CHANGELOG.md`；本计划的 25.2 摩擦记录（F-A1）。
- 先红：未实现时，测试项目报 CS0103 / CS0246 / CS1061（解析器、地址类型与 `Device` 等不存在）。
- 后绿：`Modbus.Tests` 39/39（net472 与 net8.0；其中 2 个是 Task 3 的骨架用例）。
- 表驱动覆盖：计划第 4 节列出的全部有效写法与全部非法写法；每条非法写法断言错误消息的关键词（`between`、`Modicon`、`bit index`、`unit ID`、`';'`、`after the unit ID`、`Unknown device`、`empty`）。
- 实现决定：
  - 站号前缀的解析只做语法（0–255）。RTU 的 1–247 与 MBAP 的 0–255 由客户端在 Task 8 检查，解析器不知道帧格式（P7-D4）。
  - Modicon 的一基换算：5 位为后 4 位 1–9999，6 位为后 5 位 1–65536，编号 = 数值 − 1；首位只接受 0、1、3、4。
  - 位号只在寄存器上接受（IR、HR，以及 3、4 开头的 Modicon）。线圈与离散输入带位号时返回 InvalidRequest。
  - 符号写法按符号长度降序匹配（与 `PlcAddressParser` 的最长前缀约定一致）。
  - 空白：整体去掉首尾空白，与套件一致；`ModbusAddress.Text` 为去掉空白后的原始文本。
- 偏差：计划第 4 节 Task 4 的第 2 项写的是"`ModbusAddress`：`new ModbusDevice Device`、`byte? UnitId`"。我把构造函数设为 internal（只有解析器创建），这与"其余类型全部 internal"一致，公开面不变。
- 第 2 节自查：符合。文件作用域命名空间；公共成员有中文 XML 注释（CS1591 在目标中是错误，构建通过即是证据）；错误消息为英文；没有 await，因此没有 ConfigureAwait；没有 net472 禁用 API。Q45：没有 v2 类型名。
- 度量：Modbus 有效行 0 → 228（`effective_lines.py`）。
- 三条命令：Debug 2 个警告、0 个错误；Release 2 个警告、0 个错误（均为 WPF 工具的 CS8618，改动前即存在）；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Modbus 39 / 39，Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。

#### Task 5（协议层，纯字节，Q44，2026-10-19）

- 新增（`Junevy.Communication.Modbus/Protocol/`，全部 internal，不引用通道类型）：`ModbusFunctionCodes`、`ModbusBuffer`（写字节的小工具）、`ModbusCrc`（CRC-16/MODBUS，支持分段累加）、`ModbusBits`（位打包，最低位在前）、`ModbusExceptionCodes`（九个标准码的名称与消息）、`ModbusExpectation`（一包的期望）、`ModbusRequestPdu`（请求编码）、`ModbusResponsePdu`（应答解码与回显校验）、`MbapHeader`（写头、校验与剥离，进程级 `TransactionIds`）、`RtuAdu`（封装与剥离）。
- 命名偏差：计划的 `ModbusPdu` 拆成 `ModbusRequestPdu` 与 `ModbusResponsePdu`，两者都在 300 行以内；职责不变（计划允许命名调整）。
- 测试：`Tests/…Modbus.Tests/Protocol/`：`ModbusRequestPduTests`（V-P-1…15 的请求）、`ModbusResponsePduTests`（V-P-1…15 的应答、异常、回显与字节数校验、位打包）、`ModbusFrameTests`（33 条 RTU 向量的 CRC、RTU 封装与剥离、MBAP 封装与剥离、V-M-1…5、事务号不检查）。共 113 个用例。
- 向量覆盖：
  - 21.1：V-P-1…15 的请求全部逐字节比对；应答中 V-P-1、2、3、4、10 解码为数据，V-P-5…9、12…15 为回显，V-P-11 为异常（`RemoteError` 码 2，消息逐字比对）。
  - 21.2：33 条整帧的 CRC 全部重算比对（`Crc_MatchesTheTrailingChecksumOfEveryRtuVector`）；请求帧与应答帧分别有封装与剥离用例；V-R-15 与 V-R-20 的异常路径有剥离用例。
  - 21.3：V-M-1、2、4 的封装与剥离，V-M-3 异常，V-M-5 协议号非 0 的 `ProtocolViolation`。V-M-6（键提取）归 Task 6。
  - 21.4 帧长表、21.5 上限：归 Task 6（分帧）与 Task 8（上限）。
- 第 21 节之外的要求（计划第 9 条）：回显不符（05 的值、06 的地址、0F 的数量、16 的掩码）各一条；字节数不符；未知异常码的消息不带名称；位打包（V-P-1、V-P-7）的逐位期望；MBAP 长度字段与帧长不一致。
- 向量与计划对照：没有发现计划或向量的错误。21.2 的 33 条 CRC 与 Task 0 用 v2 `Crc16Helper` 的结果一致。
- 先红：未实现时，测试项目报 CS0234 / CS0246（协议类型与命名空间不存在）。
- 后绿：`Modbus.Tests` 152/152（net472 与 net8.0；其中 2 个是 Task 3 的骨架用例，37 个是 Task 4 的解析用例）。
- 修正（针对 Task 2 的报告）：Task 2 的 `Send_RunsSeriallyWithReadAsync` 在一次 `test-affected` 的负载运行中（net472）失败，原因是测试在解析委托里阻塞线程池线程（`ManualResetEventSlim.Wait`）。负载下 `Task.Yield` 饥饿，读取要等到 10 秒超时后才放行，断言在那时才看到第二个请求。我把挂起的方式改为 `GatedClientChannel`（`TestSupport/`）：应答异步挂起，不占用线程。Task 2 报告中"负载下稳定"的说法不成立，以本次为准。修正后：Plc.Tests 连续三次 283/283；变异检查（去掉操作锁）在新测试上仍然失败（29 ms）。
- 第 2 节自查：符合。`Protocol/` 只依赖 Core（`BigEndianFields`、`ByteBufferWriter` 的测试用、`CommResult`、`SequenceCounter`）；没有 `Index`/`Range`（net472 禁用清单）；公共成员无；错误消息英文，注释中文。Q44：`Protocol/` 中没有通道类型引用（grep 确认）。
- 度量：Modbus 有效行 228 → 543（Protocol 层 315 行）。
- 三条命令：Debug 2 个警告、0 个错误（WPF 工具的 CS8618，改动前即存在）；Release 2 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Modbus 152 / 152，Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。

#### Task 6（分帧与关联，P7-D8、D9，2026-10-19）

- 新增（`Junevy.Communication.Modbus/Framing/`，全部 internal）：`ModbusRtuFrameDecoder`（`IFrameDecoder`，按第 21.4 节的长度表分帧，收全后以 CRC 裁决，功能码未知或 CRC 不符时丢 1 字节重新同步）、`ModbusRtuFrameCodecFactory`（`IFrameCodecFactory`，编码器用套件的 `PassthroughFrameEncoder.Instance`）、`ModbusStreamFraming`（`MbapOptions()` 返回 MBAP 的 `FramingOptions`；`RtuCodecs()` 返回 RTU 工厂）、`ModbusTransactionKeyExtractor`（`IFrameKeyExtractor`，前两个字节为事务号）。
- 测试：`ModbusRtuFrameDecoderTests`（9 个用例：单帧、逐字节到达、两帧粘连、异常应答、帧前噪声、CRC 错后接正确帧、未知功能码、本地回显、超过 256 字节的异常）、`ModbusFramingTests`（7 个用例：V-M-6 的键与不足两字节，MBAP 的长度字段分帧与逐字节等待，RTU 工厂的解码）。
- 先红的证据（诚实说明）：本 Task 的测试与生产代码在同一次编辑中写入，没有先单独运行一次红。第一次编译的唯一错误是 `PassthroughFrameEncoder` 没有公开构造函数（改为 `.Instance`）。因此改用变异检查替代：临时去掉 RTU 的 CRC 比较（改为接受任何帧），`LocalEcho`、`CrcError`、`UnknownFunctionCode`、`NoiseBeforeTheFrame` 四个用例失败；恢复后全部通过。
- 后绿：`Modbus.Tests` 168/168（net472 与 net8.0）。
- 解释的决定（计划没有给出细节，记录在此）：
  - "超过 256 字节抛 FrameDecodeException" 按字面实现：候选帧的声明长度超过 256 时（即字节数 253–255）立即抛出，不等待数据。RTU ADU 的上限确实是 256，因此这样的候选不可能是帧。但是噪声中的字节可能凑出这样的候选，抛出会让通道把连接当作出错。这与 P7-D8 的"不抛异常"有张力。请审阅者决定：字面实现（抛出），或改为丢 1 字节（与功能码未知的处理一致）。
  - 候选帧需要的字节数未到达时分帧器等待。噪声中的字节数字段很大（最多 255）时，分帧器可能等待最多 260 字节才裁决，之后的好帧会被推迟。这是 P7-D17 第 ② 项（强噪声线路上的重新同步）的一部分，没有超时兜底。
- 第 2 节自查：符合。`IFrameDecoder` 与 `IFrameKeyExtractor` 的实现只依赖 Channels 的公开类型（`FramingOptions`、`FrameCodecFactory`、`PassthroughFrameEncoder`、`FrameDecodeException`）；没有 `Index`/`Range`；窗口缓冲在解码器实例内复用，没有在循环中使用 `stackalloc`（避免栈增长）。
- 度量：Modbus 有效行 543 → 677。
- 三条命令：Debug 2 个警告、0 个错误（WPF 工具的 CS8618，改动前即存在）；Release 2 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Modbus 168 / 168，Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。

#### 里程碑 2 小结

- 协议层（Task 5、6）全部为 internal，只依赖字节与 Core/Channels 的公开类型。
- 第 21 节的向量覆盖：21.1 的请求与应答（V-P-1…15）、21.2 的 33 条 RTU 整帧（CRC 与封装/剥离）、21.3 的 V-M-1…5 与 V-M-6、21.4 的长度表（分帧器）、21.5 的码表（`ModbusExceptionCodes`）。
- 有效行：Modbus 0（Task 3 骨架）→ 228（地址）→ 543（协议层）→ 677（分帧）。v2 为 3304（对照在 Task 15 给出）。
- 未发现 S1、S2 以外的套件缺口。两个决定（上面的 256 字节与噪声等待）需要审阅者知悉，但不影响里程碑 3 的门槛判断。

#### Task 7（配置快照、组件声明与心跳探测，P7-D7、D9、D11、D12，2026-10-19）

- 新增：`Junevy.Communication.Modbus/Options/ModbusFraming.cs`（公开枚举 `Mbap`、`Rtu`）、`Options/ModbusClientOptions.cs`（公开，`PlcClientOptions` 派生；`Framing`、`UnitId` 默认 1、`UseSingleWriteFunctions` 默认 true、`UseMaskWriteForBits`、`DataFormat`）、`Options/ModbusProtocolSettings.cs`（internal 快照，`From`）、`Options/ModbusProtocolProfile.cs`（internal，`Configure`）、`Connection/ModbusReadProbe.cs`（internal，`IHealthProbe`）。
- 测试：`Options/ModbusProtocolProfileTests.cs`（22 个用例：规则表的每个格子、串口上的 MBAP、未定义的枚举、RTU 站号范围、组件已被设置时的守卫、探测工厂、快照的默认值与校验），`Connection/ModbusReadProbeTests.cs`（9 个用例）。
- 先红的证据（诚实说明）：与 Task 6 相同，测试与生产代码在同一次编辑中写入，没有单独运行一次红。改用变异检查：(a) 把 MBAP 的关联改为 Sequential，`DatagramWithoutAFramingChoice`、`NetworkStreamMbap`、`DatagramMbap` 失败；(b) 把探测对异常应答的处理改为不健康，`Rtu_ExceptionResponse_IsHealthy` 失败。恢复后全部通过。
- 后绿：`Modbus.Tests` 199/199（net472 与 net8.0）。
- 决定（计划没有给出细节）：
  - `ModbusClientOptions` 与 `ModbusFraming` 在本 Task 加入，因为 `ModbusProtocolSettings.From` 需要它们；P7-D3 的公开面（10 个类型）在 Task 8 结束时核对。
  - 串口上的 MBAP 在 `ModbusProtocolProfile` 中抛 `ArgumentException`（参数名 `settings`）；未定义的枚举、交付方式与介质抛 `ArgumentOutOfRangeException`。
  - 探测使用的 MBAP 事务号来自 `MbapHeader.TransactionIds`（与客户端请求共用计数器）。测试不断言绝对值，从 `ScriptedByteChannel.LastRequest` 取出请求。
  - 探测的站号范围检查与 RTU 的 1–247 在 `Configure` 中完成（P7-D7 的要求：在 `PlcComponentWiring.Apply` 之前）。
- 第 2 节自查：符合。`ModbusProtocolSettings` 与选项都在构造时取快照（与 MELSEC 的做法一致）；文件作用域命名空间；公共成员有中文注释；错误消息英文。
- 度量：Modbus 有效行 677 → 822。
- 三条命令：Debug 2 个警告、0 个错误（WPF 工具的 CS8618，改动前即存在）；Release 2 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Modbus 199 / 199，Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。

#### Task 8（ModbusClient，P7-D3、D5、D6、D10，2026-10-19）

- 新增：`Junevy.Communication.Modbus/Client/ModbusClient.cs`（公开的 sealed 分部类；两个公开 `Create`；内部的 `Create(name, …)` 与内部构造函数；创建失败时释放通道）、`Client/ModbusClient.Hooks.cs`（协议钩子）、`Client/ModbusFrames.cs`（internal，外壳的封装与剥离，Q47）；测试 `Tests/…Modbus.Tests/Client/`：`ModbusClientWireTests`（26 个用例）、`ModbusClientErrorTests`（11 个用例）、`ModbusClientCreationTests`（10 个用例），以及 `TestSupport/ScriptedModbusChannel.cs`（脚本化的 `IClientChannel` 替身）与 `TestSupport/TestModbus.cs`（构造客户端、取出线上的 PDU 与站号、按请求的帧格式生成应答）。
- 修改：`Client/…` 之外，`ModbusReadProbe` 改用 `ModbusFrames`（去掉重复的外壳代码）。
- 公开面：`ModbusClient` 的公开成员为两个 `Create`（P7-D3）；构造函数是 internal（测试与 `Create` 使用），没有公开构造函数（由反射测试钉住）。
- 决定：
  - 钩子以 `protected override` 声明：Plc 的钩子是 `protected internal`，跨程序集的重写必须用 `protected override`（与 MELSEC 的 `McClient.Hooks.cs` 一致）。
  - 一包的期望只在 Build 钩子中记录一次（`Expect`），解析钩子通过 `TakeExpectation` 取出（Q47）；重试时每次尝试都重新记录。
  - 外壳（MBAP 头或 RTU 帧）由 `ModbusFrames` 统一处理，PDU 的编解码与回显校验只有一份（Q47）。
  - MBAP 的事务号在 Build 时由 `MbapHeader.TransactionIds` 分配，解析时不检查（P7-D10）。
  - 写入的功能码映射：一个字的包用 06（`UseSingleWriteFunctions`），否则 10；一个点的线圈包用 05，否则 0F；寄存器位在 `UseMaskWriteForBits` 开启时用 16，每个请求一位（`GetMaxWritePoints` 返回 1）。
  - 地址的站号前缀优先，否则用选项中的默认站号；RTU 的站号范围在地址解析时检查（`TryParseAddress` 覆写中）。
  - 测试替身不能用共享测试库的 `ScriptedByteChannel`（那是 `IByteChannel`），因此复制了一份 `ScriptedModbusChannel`（摩擦 F-A2）。
- 先红的证据（诚实说明）：与 Task 6、7 相同，测试与生产代码在同一次编辑中写入，没有单独运行一次红。第一次编译的错误是 `protected internal` 的重写（CS0507）与缺少 `using`，修正后测试 246 个全部通过。改用变异检查：(a) 去掉"一个点用 06"的规则（`registers == 1 && settings.UseSingleWriteFunctions && false`），(b) 去掉"线圈不能按字访问"的检查，两者合起来使 `WriteOneRegister_UsesFunction06`、`WriteOf123Registers_…`、`ReadWordsOfACoil_…`、`WriteRegisterBitByReadModifyWrite_…` 失败；恢复后全部通过。
- 后绿：`Modbus.Tests` 246/246（net472 与 net8.0）。
- 第 2 节自查：符合。`Client/` 下的类型不引用通道的具体实现，只引用 `IClientChannel`；没有 `Index`/`Range`；公共成员的 XML 注释为中文；错误消息英文；`ConfigureAwait` 不需要（客户端没有新的 await）。
- 度量：Modbus 有效行 822 → 1065。
- 三条命令：Debug 2 个警告、0 个错误（WPF 工具的 CS8618，改动前即存在）；Release 2 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Modbus 246 / 246，Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。

#### Task 9（0x16 与 0x17，P7-D15，2026-10-19）

- 新增：`Junevy.Communication.Modbus/Client/ModbusClient.Commands.cs`（两个公开方法）；测试 `Tests/…Modbus.Tests/Client/ModbusClientCommandTests.cs`（25 个用例）。
- 修改：`CHANGELOG.md`。
- 公开面：`MaskWriteRegisterAsync(string, ushort, ushort, CancellationToken)` 返回 `Task<CommResult>`；`ReadWriteMultipleRegistersAsync(string, int, string, ReadOnlyMemory<byte>, CancellationToken)` 返回 `Task<CommResult<byte[]>>`（P7-D15 的签名）。
- 地址与参数的检查在发请求之前完成，不进入操作锁：掩码写要求不带位号的保持寄存器（IR、C、DI、带位号都返回 `InvalidRequest`）；读写多个要求两个地址都是不带位号的保持寄存器、站号相同、读 1–125 字、写 1–121 字（偶数字节）、两段结束地址不超过 65535，且不拆包。
- 线上字节：V-P-9（16）、V-P-10（17）、V-R-13（RTU 的 16）、V-R-14（RTU 的 17）逐字节比对；掩码写的应答必须回显地址与两个掩码，读写多个返回读出的 `2 × readCount` 字节。
- 先红的证据（诚实说明）：与 Task 6–8 相同，没有单独运行一次红。变异检查：去掉"同一站号"与"保持寄存器"的判断，9 个用例失败；恢复后通过。
- 后绿：`Modbus.Tests` 271/271（net472 与 net8.0）。
- 第 2 节自查：符合。两个公开方法的 XML 注释为中文；await 都带 `ConfigureAwait(false)`；错误消息英文；没有 net472 禁用的 API。
- 度量：Modbus 有效行 1065 → 1157。
- 三条命令：Debug 2 个警告、0 个错误（WPF 工具的 CS8618，改动前即存在）；Release 2 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Modbus 271 / 271，Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。

#### 里程碑 3 小结（可行性门槛，第 13 节）

- 摩擦记录（第 25.2 节）：F-S1（只读钩子，已列为 S1）、F-S2（协议专有请求，已列为 S2）、F-A1（地址解析器无法表达站号前缀与 Modicon 写法，自写 `ModbusAddressParser`）、F-A2（`IClientChannel` 的测试替身只能复制，共享测试库只有 `IByteChannel` 的替身）。
- 是否出现 S1、S2 以外的套件缺口：**否**。Task 7–9 只用到了 S1、S2 与套件已有的组件装配（`PlcComponentWiring`）、通道创建（`PlcChannelBinding`）与类型化读写扩展。
- v2 的读写能力逐项对照（第 20 节，只列读写与连接相关的行）：
  - 连接与断开（`ConnectAsync`、`DisconnectAsync`、`IsConnected`、`State`、`StateChanged`）：基类提供，Plc.Tests 的生命周期用例覆盖；Modbus 的 `Disconnect` 之后请求返回 `NotConnected` 由套件的测试覆盖。
  - 重试与超时：`RetryCount`（默认 0）、`RetryInterval`，`EchoMismatch_IsProtocolViolationAndIsRetried`、`Timeout_IsReportedAndRetriedWhenConfigured` 覆盖。
  - 读保持寄存器、读输入寄存器（`ReadHoldingRegisters`、`ReadInputRegisters`）：`ReadHoldingRegisters_SendsFunction03…`、`ReadInputRegisters_SendsFunction04`，类型化读取 `ReadUInt16Async`、`ReadInt32Async` 等。
  - 读线圈、读离散输入（`ReadCoils`、`ReadDiscreteInputs`）：`ReadCoils_SendsFunction01…`、`ReadDiscreteInputs_SendsFunction02`。
  - 单个与多个寄存器写入（`WriteSingleRegister`、`WriteMultipleRegisters`）：`WriteOneRegister_UsesFunction06`、`WriteTwoRegisters_UsesFunction10`；单功能码规则 `SingleWriteFunctionsOff_UseTheMultipleWriteFunctions`。
  - 单个与多个线圈写入（`WriteSingleCoil`、`WriteMultipleCoils`）：`WriteOneCoil_UsesFunction05…`、`WriteManyCoils_UsesFunction0FWithPackedBits`。
  - 掩码写（`MaskWriteRegister`）：`MaskWriteRegister_SendsFunction16…`（Task 9）。
  - 读写多个（`ReadWriteMultipleRegisters`）：`ReadWriteMultipleRegisters_SendsFunction17…`（Task 9）。
  - 站号（v2 的 `slave` 参数）：站号前缀 `s=n;` 与默认站号，`StationPrefix_IsOnTheWireInTheUnitIdPosition`，`NoStationPrefix_UsesTheDefaultUnitId`。
  - 有意不提供（第 20 节与 P7-D1）：`ReadExceptionStatus`、`Diagnostics`、`GetCommEventCounter`、`GetCommEventLog`、`ReportServerId`、原始 PDU 入口（`Request`、`RequestAsync`）、`ModbusHelper.BuildRequestFrame`、同步 API、`IModbusPduValidator` 注入。这些都在迁移对照表中写明，不属于能力缺失。
  - 工厂与依赖注入（`IModbusFactory` → `PlcClientFactory`）：归 Task 11，尚未验证。
- 有效行：v2 3304 → v3 1157（Protocol 与 Client 与 Options 与 Framing 与 Addressing 与 Connection 合计）。
- 结论：**继续**。没有出现 S1、S2 以外的套件缺口，也没有 v2 的读写能力表达不出来的情况，按计划不停下，进入里程碑 4。
- 需要审阅者知悉的两个决定（不影响门槛判断）：
  - 第 6 节 "超过 256 字节抛 FrameDecodeException" 的字面实现（噪声可能让通道把连接当作出错），以及对应的 P7-D8 "不抛异常" 的张力。
  - 分帧器在候选帧的字节数未到达时等待（P7-D17 ②）。

#### Task 10（设备替身与端到端，2026-10-19）

- 新增（仅测试）：`TestSupport/ModbusSlave.cs`（+ `ModbusSlave.Writes.cs`，partial）：在 `PlcMemory` 上实现 01–06、0F、10、16、17；越界返回异常码 02，数量越界 03，未知功能码 01；站号集合之外不回复；可丢弃第 n 个请求；回复前可加噪声字节。`TestSupport/ModbusRtuRequestCodecs.cs`（设备一侧的 RTU 请求分帧，按第 21.4 节的请求列）、`TestSupport/ModbusTcpSlave.cs`（TCP 承载，回复可按序号延迟）、`TestSupport/DelayingStream.cs`（写入前延迟，延迟的写入在后台完成）。端到端：`EndToEnd/ModbusEndToEndTcpTests.cs`（MBAP 与 RTU over TCP）、`EndToEnd/ModbusEndToEndUdpTests.cs`（MBAP 与 RTU over UDP）、`EndToEnd/ModbusEndToEndSerialTests.cs`（`DuplexClientChannel` 上的 RTU，以及真实串口对的用例）。
- 覆盖的矩阵（计划第 14 节第 2 条）：四个存储区的读写（TCP 的两种帧，UDP 的两种帧）；拆包边界（126 字、2001 点）；类型化读写（Int32、Float、String，仅 TCP）；站号轮询（1、2 正常，3 超时，然后 1 仍正常）；异常码 02；MBAP 迟到应答（不重建连接，`AcceptedConnectionCount` 为 1）；RTU over TCP 迟到应答（重建连接，`AcceptedConnectionCount` 为 2）；UDP 迟到应答（每个请求独立）；帧前与帧间的噪声（TCP 与串口）；静默设备加心跳进入 `Reconnecting` 并恢复；请求中释放（20 次，无异常）；掩码写只改一位。
- 与计划不同之处：
  - 重试用例只测 MBAP。RTU over TCP 的超时重建与重试存在竞争（见下方发现），因此不把它当作期望。
  - 类型化读写只在 TCP 上跑，没有在串口与 UDP 上跑（矩阵的其余部分已覆盖）。
  - 替身的文件大小：`ModbusSlave` 拆成两个 partial 文件，每个 ≤ 165 行，符合计划的"按职责拆文件"。
- 发现（需要审阅者知悉，本期未改通道层）：RTU over TCP 上，请求超时后 `ResetOnRequestTimeout` 会断开连接，重连在后台进行。紧接着的请求（包括 `RetryCount > 0` 的重试）可能早于重连完成而得到 `NotConnected`。代码路径推断：`ConnectionSupervisor.WaitForConnectedAsync` 在状态仍为 `Connected` 时立即返回 true，而断开是异步的。测试在超时后先等待断开再等待重连（`AwaitRebuiltConnectionAsync`），因此没有掩盖问题，只是不把竞争当作期望。
- 先红的证据（诚实说明）：本 Task 是测试与替身，没有生产代码变化。测试在第一次运行时暴露了三个问题（重连未完成、重试竞争、`Polling` 用法），已逐个修正并在报告中说明。变异检查：把 MBAP 的关联改为 Sequential，`MbapLateReply_…` 与 `StationPolling_…(Mbap)` 失败；恢复后通过。
- 后绿：`Modbus.Tests` 295/295，1 个跳过（`RtuReadAndWrite_OverARealSerialPair`，未设置 `JUNEVY_SERIAL_PAIR`；执行者不运行，由审阅者在有串口对的机器上运行）。
- 第 2 节自查：测试代码不进入库；替身的回复编码复用生产的 MBAP 与 RTU 封装（这些已由第 21 节向量覆盖）。
- 度量：生产代码无变化（Modbus 有效行 1157；测试不计入）。
- 三条命令：Debug 2 个警告、0 个错误（WPF 工具的 CS8618，改动前即存在）；Release 2 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Modbus 295 / 295（跳过 1），Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。

#### Task 11（依赖注入与 WPF 调试工具，P7-D14、D16，2026-10-19）

- 新增：`Junevy.Communication.Modbus/DependencyInjection/`：`ModbusTransport`、`ModbusClientConfig`、`ModbusClientCreator`、`ModbusServiceCollectionExtensions.AddModbus()`（`TryAddEnumerable` 注册创建器，再调用 `AddPlcClients()`，幂等）。测试 `Tests/Junevy.Communication.Modbus.Tests/DependencyInjection/ModbusDependencyInjectionTests.cs`（11 个用例）。
- 修改：WPF 调试工具（`Junevy.Communication.Test`）：项目引用 Modbus；`App.xaml.cs` 调用 `AddModbus()`；`MainWindowViewModel.cs` 重写为 v3 视图模型（经 `PlcClientFactory` 创建客户端，连接、断开、读、写；TCP 与串口；C、DI、IR、HR）；`MainWindow.xaml` 重写为对应的窗口（不追求与 2.x 界面相同）。删除没有引用的旧文件：`MbCmd.cs`、`Regions.cs`、`EnumToBoolConverter.cs`、`LengthEnableConverter.cs`（它们只服务于 2.x 界面）。`ModbusData.cs` 保留，用于结果表格。
- 决定：每次连接使用新的客户端名称（`debug-N`），因为工厂按名称返回已注册的客户端；修改配置后重新连接即可生效。
- 先红的证据（诚实说明）：没有单独运行一次红（与 Task 6–10 相同）。第一次编译的错误是 WPF 视图模型缺少 `Core.Results` 的 using，修正后通过。`Creator_ConfigOfAnotherType_ThrowsArgumentException` 是后加的，用于覆盖计划的"配置类型不符"。
- 后绿：`Modbus.Tests` 307/307，1 个跳过（真实串口对）。
- 第 2 节自查：符合。依赖注入类型的公共成员有中文注释；错误消息英文；创建客户端不打开连接或端口。WPF 工具只通过公开 API 使用库。
- 度量：Modbus 有效行 1157 → 1215（DI 类型 58 行）。
- 三条命令：Debug 2 个警告、0 个错误（WPF 工具的 CS8618，改动前即存在）；Release 2 个警告、0 个错误；`test-affected -Base c29a45b -NoBuild` 全部通过，0 失败：Architecture 15 / 12，Melsec 184（跳过 1）/ 184（跳过 1），Modbus 307 / 307（跳过 1），Omron 260 / 260，Plc 283 / 283，Siemens 316 / 316。

#### Task 12（README、Skill、CHANGELOG，2026-10-19）

- 修改：`Junevy.Communication.Modbus/README.md`（英文，整体重写：安装、快速开始（TCP、串口 RTU、RTU over TCP、多站）、地址表、功能码映射与单包上限、类型化读写、选项表、掩码写与读写多个、错误处理、心跳与重连、依赖注入、不提供的功能、"Migrating from 2.x"（第 20 节的对照表）、"Verification status"（P7-D17 的五项）加上 `IsReadOnly` 与协议专有请求的说明）；`Skills/using-junevy-modbus/SKILL.md`（改写为 v2 到 v3 的迁移指南，指向 `using-junevy-plc`）；`Skills/using-junevy-plc/SKILL.md`（概述与包表增加 Modbus；新增 "Modbus Quick Start" 一节；钩子表增加 `IsReadOnly`；新增 "Protocol-specific requests" 小节；新增 "Lessons From the P7 Friction Log"）；根 `readme.md`（协议表的 Modbus 行改为 3.0.0-preview.1 的描述）；`CHANGELOG.md`（P7 节的破坏性变更逐条列出 P7-D13 与第 20 节的变化）。
- 代码块编译：从 `Junevy.Communication.Modbus/README.md`、`using-junevy-modbus` 与 `using-junevy-plc` 中的 Modbus 相关示例抽出（共 10 个），放进临时项目（`ImplicitUsings=disable`、`Nullable=enable`、`TreatWarningsAsErrors=true`，net8.0 与 net472 同时构建），结果为 0 错误、0 警告。过程中补齐了示例缺少的 `using`（`Core.Results`、`Channels`、`Modbus`、`Tcp`）与未声明的 `client`，使每个示例自成一体。临时项目在 scratchpad，未提交。
- 未编译的内容：`using-junevy-plc` 中 MELSEC、S7、FINS 的示例（既有内容，不是本期改动）。它们含有类型声明，临时项目的包装方式无法承载，因此没有编译；这一点留给审阅者决定是否补做。
- 发现的文档问题（已在 Skill 的 P7 教训中记录）：RTU over TCP 的重试与超时后的重连存在竞争（见第 25.2 节的说明）。README 的心跳一节写明了需要等待重连。
- 先红：文档没有先红。改动前后用 `dotnet build` 验证示例（示例先失败：缺少 `using` 与未声明的 `client`，修正后通过）。
- 第 2 节自查：符合。文档与代码中的名称一致（`MaskWriteRegisterAsync`、`ReadWriteMultipleRegistersAsync`、`AddModbus`、`ModbusClientConfig`）；v2 类型名只出现在迁移对照与 Skill 的迁移指南中（Q45 允许的位置）。
- 三条命令：本 Task 不改代码；`test-affected` 在 Task 11 的结果仍然有效（见 Task 11）。

#### Task 13（设计文档，2026-10-19）

- 修改：`docs/superpowers/specs/2026-10-10-communication-core-design.md`：
  1. 第 1 节的协议族表与第 3 节的图：Modbus 行改为"v3，P7 已迁移"，图中 `MBV3` 去掉"可选迁移"；第 3.1 节程序集表的 Modbus 行改为"Plc + Tcp + Udp + Serial，3.0.0-preview.1（P7 已迁移）"。
  2. 第 11.2 节加一句："P7 之后 Modbus 与其他协议一致，本节的差异不再存在（见 20.12）。"
  3. 第 17 节改为指向 20.12（P7 已完成）。
  4. 第 19 节的 P7 标记为已完成。
  5. 新增 20.12 实施修订（P7）：差异表（原设计、实际实现、原因，共 13 行）、第四次扩展性验证的结论（套件改动、摩擦记录、有效行对照、仍然存在的表达力缺口 F-A1、RTU over TCP 的发现）。20.12 的数字为 Task 11 时点，Task 15 复测后更新。
  6. 第 21.1 节 Q4 注明"已在 P7 迁移（2026-10-19，用户决定原地替换）"。
- 后绿：文档改动，无代码变化；`test-affected` 结果与 Task 11 相同（见 Task 11）。
- 决定：20.12 的差异表按"原设计 → 实际 → 原因"组织，每一行都能追溯到计划中的编号（P7-Dx）或第 20 节。
- 第 2 节自查：文档中的编号与计划一致（P7-D1、D2、D4 至 D9、D13 至 D16）。

#### Task 15（最终度量与扩展性结论，2026-10-19）

- 度量：第 3 节全部指标按第 25.4 节的"最终"列复测。生产重复窗口为 35（与基线相同）；Modbus 没有增加重复窗口，内部为 0；方法长度与超过 300 行的文件与基线相同，Modbus 为 0。
- 偏差 1（已修正，属于 Task 15 的度量修正）：Task 11 之后、Task 15 之前，15 个目录的重复窗口为 36（比基线多 1）。来源是 `PlcRequestPipeline` 的 Words、Bits、Bytes 三个写入入口各自的只读检查（S1，Task 1）组成了相同的六行窗口。修正方式：新增私有入口 `TryResolveWritable`（解析地址后检查只读），三处写入入口改用它；行为不变，Plc.Tests 283 项全部通过。修正后为 35。CHANGELOG 的 P7 变更节已记录。
- 偏差 2（已修正，测试）：Modbus 端到端的 TCP、UDP、串口三个测试类重复了四区域往返断言。抽为 `TestSupport/ModbusScenarios.FourAreasRoundTripAsync`，三处改为调用。仅测试，断言不变。
- 偏差 3（未达成，留给审阅者）：计划第 3 节要求"Modbus.Tests 内部与它与其他测试项目之间都为 0"。实测：Modbus.Tests 内部为 2（一对：TCP 与 UDP 的 Theory 的属性与签名样板，两行窗口）；与其他测试项目之间为 37 个窗口（`ScriptedModbusChannel` 与 `Plc.Tests` 的 `ScriptedClientChannel`，即 F-A2）加上 `DelayingStream` 与 Channels、Ftp、Testing 的若干窗口（即 F-A3 的相关部分）。这两项是计划第 25.2 节的开放决策，本期没有合并，交由审阅者决定。
- 偏差 4（已修正，计数错误）：P7-D3 写的是"共 10 个公开类型"，但逐条列出的是 9 个。反射确认为 9（见下），已在 P7-D3 的文字中改正。
- 公开面（反射，`Junevy.Communication.Modbus.dll`，net8.0 Debug）：导出类型 9 个，与 P7-D3 逐条列表一致：`ModbusClient`、`ModbusClientOptions`、`ModbusFraming`、`ModbusAddress`、`ModbusDevice`、`ModbusTransport`、`ModbusClientConfig`、`ModbusClientCreator`、`ModbusServiceCollectionExtensions`。其余类型全部 internal。
- 构建：`dotnet build Junevy.Communication.slnx` 在 Debug 与 Release 下（`--no-incremental`）均为 0 错误。警告：两个配置各 2 条，去重后为 1 条（`Junevy.Communication.Test/App.xaml.cs(15,33)` 的 CS8618，WPF 调试工具，不是本期改动，也不属于 Modbus）。Modbus 为 0 条；基线为 31 条。
- 测试：`pwsh build/test-affected.ps1 -Base c29a45b -NoBuild -Repeat 3`，三遍结果一致：每遍 2727 通过、0 失败、4 跳过。跳过的 4 项是两个真实串口对用例（Melsec 与 Modbus 各一个，分别在 net8.0 与 net472 上），因为未设置 `JUNEVY_SERIAL_PAIR`。Modbus.Tests 每个框架 307 通过、1 跳过（共 308）。
- 扩展性结论（第四次验证）：Modbus 迁移到套件只改动了两项（S1 `IsReadOnly`、S2 `SendProtocolRequestAsync`）。Melsec、Siemens、Omron 的生产代码与测试没有改动，测试数不变。摩擦记录见第 25.2 节，其中 F-A1（声明式解析器的表达力）仍然存在。有效行：Modbus v3 为 1215，v2 为 3304；Plc 为 1285（+54）；Melsec 1247、Siemens 1271、Omron 692 不变。

### 25.4 基线与度量（执行者填写）

Task 0 实测（第 3 节口径；脚本 `build/metrics/*.py`，经 `runpy` 调用，未修改脚本）：

| 指标 | 基线 |
|---|---|
| 生产重复窗口（6 行），15 个目录 | 35（10 个集群） |
| 生产重复窗口，16 个目录（含 v2 Modbus） | 55（14 个集群）；差值 20 即 Modbus 相关（v2 Modbus 内部 10，共 2 个集群；其余与其他目录之间） |
| 超过 60 行的方法，15 个目录 | 0 |
| 超过 60 行的方法，16 个目录 | 8，全部在 v2 Modbus（最长 `ModbusPduValidator` 的方法 86 行） |
| 超过 300 行的文件，15 个目录 | 9（Channels 6、Core 1 `NamedRegistry.cs` 461 行、Tcp 2） |
| 超过 300 行的文件，v2 Modbus | 4（`ModbusTransportBase.cs` 552、`ModbusTcpClient.cs` 526、`ModbusRtuClient.cs` 420、`ModbusConnectionManager.cs` 371） |
| 生产程序集之间的 IVT（架构测试口径） | 2（Channels → Tcp、Channels → Udp） |
| 有效行（`effective_lines.py`） | Modbus v2 **3304**；Plc 1231；Melsec 1247；Siemens 1271；Omron 692 |
| 构建警告 | Debug 31、Release 31（Modbus 25 去重后，见 25.3） |
| 测试重复窗口（`Tests/` 下 445 个 `*.cs`，P4.6 口径） | 192（53 个集群；计划写 52，见 25.3 偏差）；Modbus.Tests 内部 55（12 个集群） |
| 第 22 节静态用例 | `[Fact]` 159；`[Theory]` 5（`[InlineData]` 15 行；4 个 Theory 的数据来自 MemberData，未展开） |
| 套件改动 | — |


Task 15 最终复测（同一口径；"目标"列来自第 3 节）：

| 指标 | 最终（Task 15） | 目标 | 结果 |
|---|---|---|---|
| 生产重复窗口，15 个目录 | 35（10 个集群） | 不增加 | 达成（Task 11 之后曾为 36，见 25.3 Task 15 偏差 1，已修正） |
| 生产重复窗口，15 个目录 + Modbus | 35（Modbus 增加 0） | Modbus 内部与其他程序集之间都为 0 | 达成；Modbus 内部（`Modbus` 目录单独扫描）为 0 |
| 超过 60 行的方法，15 个目录 / 含 Modbus | 0 / 0（v2 Modbus 为 8） | 0 | 达成 |
| 超过 300 行的文件，15 个目录 | 9（与基线相同） | 不增加 | 达成 |
| 超过 300 行的文件，Modbus | 0（v2 为 4） | 0 | 达成 |
| 生产程序集之间的 IVT | 2（Channels → Tcp、Channels → Udp） | 2 | 达成 |
| 有效行 | Modbus v3 **1215**（v2 3304）；Plc 1285（基线 1231，+54）；Melsec 1247；Siemens 1271；Omron 692 | 记录 | 已记录 |
| 构建警告（`--no-incremental`，Debug / Release） | 各 2 条，去重后 1 条（WPF 调试工具 `App.xaml.cs` 的 CS8618）；Modbus 0 条；0 错误 | Modbus 0 条；总数减少 | 达成（基线 31 条，Modbus 25 条） |
| 测试重复窗口，`Tests/` 下 451 个 `*.cs` | 164（42 行）（基线 192，P4.6 口径，文件数 445 → 451） | 不增加 | 达成（减少 28） |
| Modbus.Tests 内部重复窗口 | 2（一对：TCP 与 UDP 的 Theory 样板） | 0 | **未达成**（见 25.3 偏差 3） |
| Modbus.Tests 与其他测试项目之间 | 37（`ScriptedModbusChannel` ↔ `Plc.Tests` 的 `ScriptedClientChannel`）+ `DelayingStream` 的若干窗口 | 0 | **未达成**，属于 F-A2、F-A3（开放决策） |
| Modbus 静态用例 | `[Fact]` 153；`[Theory]` 24；`[InlineData]` 121；`[MemberData]` 1；`.cs` 文件 26 | 记录（第 22 节的 v2 计数不再适用） | 已记录 |
| 三遍 `test-affected`（`-Base c29a45b -NoBuild -Repeat 3`） | 每遍 2727 通过、0 失败、4 跳过（真实串口对，未设置 `JUNEVY_SERIAL_PAIR`）；三遍一致 | 0 失败 | 达成 |
| Modbus.Tests（每个框架） | 307 通过、1 跳过（真实串口对），共 308 | — | 已记录 |
| 公开类型（反射，net8.0 Debug） | 9（与 P7-D3 逐条列表一致；P7-D3 原写的"10"是计数错误，已改正） | 9 | 达成 |
| 套件改动 | 只有 S1（`IsReadOnly`）与 S2（`SendProtocolRequestAsync`） | 只有 S1、S2 | 达成 |
