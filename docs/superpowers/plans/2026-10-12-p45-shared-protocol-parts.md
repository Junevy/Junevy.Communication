# P4.5 实施计划：协议共用件抽取与 `CommResult` 可空性标注

- 日期：2026-10-12；分支：`feat/plc-s7-fins-p4`（继续在 P4 分支上做，基线提交 `d306543`，即 P4 验收修正之后）。
- 执行者：另一个智能体。审阅者：在用户通知后验收，重点同 P4：代码质量、clean code、架构设计；功能完整性与缺陷检查。
- 来源：P4 验收（P4 计划第 20.1 节"审阅者对需要决定的处置"）后，用户 2026-10-12 的决定：
  - 同级协议包之间的结构性重复，**现在就在本分支抽成共用件**，并允许 MELSEC 换上共用件；
  - `CommResult<T>` 的可空性标注一并做；
  - S7 不支持 `MX0.0` 写法，保持现状（已写入知识库与 Siemens README）；
  - 测试基建留到 P5（知识库 `测试/P5 测试基建整理（待办）.md`），钩子测试门面不做。
- 依据：P4 计划 `docs/superpowers/plans/2026-10-12-p4-s7-fins.md` 第 20.2 节摩擦记录 F3、F4、F8、F9、F11，以及第 1、2、3 节的执行规则、质量标准与度量。
- **执行者从第 0、1、2 节读起。**本计划的规则与 P4 计划冲突时，以本计划为准。

本期是**行为不变的重构**加一处 Core 的小改动：三个协议的线上字节、错误分类与消息、公开 API 都不变（例外见 P45-D4 与 P45-D5，已逐条列出）。目标是把 P4 新增的 21 个重复窗口去掉 20 个，让第四个协议不必再写第四份。

---

## 0. 已固定的设计决策

| # | 决策 | 理由 |
|---|---|---|
| P45-D1 | 范围：<br>① Core 新增大端字段工具 `BigEndianFields`，S7 与 FINS 改用它，删除 `S7Fields`、`FinsFields`（F8）；<br>② Core 新增序号计数器 `SequenceCounter`，三个协议改用它，删除 `McSerialNumbers`、`S7PduReferences`、`FinsSessionIds`（F3）；<br>③ Plc 新增组件接线 `PlcComponentWiring`，三个协议的 profile 改用它，删除各自的 `RequireUnset` / `ThrowAlreadySet` 与逐项赋值（F4、F9）；<br>④ `CommResult` 与 `CommResult<T>` 加可空性标注，并清理因此变得多余的 `!`（F11）。<br>不做：`S7Framing` / `FinsFraming`（各 29 行的 `FramingOptions` 声明，剩 1 个重复窗口，接受）；MELSEC 的 `McFieldCodec`（小端二进制 + ASCII 两种编码，形状不同，不属于 F8）；`WebApiResult<T>` 的同类标注 | 用户决定；每一项都是"第三个协议出现同一逻辑"，收益可测 |
| P45-D2 | `BigEndianFields` 的公开 API 固定为第 5 节的代码块：`UInt8` / `UInt16` / `UInt24` / `UInt32` 与原样字节，写入到 `IBufferWriter<byte>`，从 `ref ReadOnlySpan<byte>` 头部读取。**读取在字节不足时返回 false，且不移动 `data`、`value` 为默认值**；不抛异常（解码器的结构错误走结果，P4 Q7）。参数错误（`output` 为 null、`UInt24` 超出 0–0xFFFFFF、长度为负）抛 `ArgumentNullException` / `ArgumentOutOfRangeException` | 这是 S7 与 FINS 两份实现的并集；Core 现有的 `ByteTransform` 面向数据载荷与字序、长度不足时抛异常，`ByteSequenceReader` 面向 `ReadOnlySequence`，都不适合报文头逐字段编解码 |
| P45-D3 | `SequenceCounter` 的公开 API 固定为第 7 节的代码块：线程安全，`NextUInt16()` / `NextByte()` 取下一个值并按宽度回绕，第一次调用返回 1（与现有三份实现一致）。一个实例只按一种宽度使用。每个协议**只有一个进程级实例**，放在写该字段的协议层类型上：MELSEC 放在 `McFrameExchange`（唯一使用者，`private static readonly`）；S7 放在 `S7Message`（写 PDU 引用号的报文头编码器，`internal static readonly PduReferences`，供钩子、握手、探测共用）；FINS 放在 `FinsHead`（写 SID 的头编码器，`internal static readonly SessionIds`，供钩子、探测共用） | 删掉三份同骨架的计数逻辑；实例的位置跟着"谁写这个字段"走，不再为一个字段单独建类 |
| P45-D4 | `PlcComponentWiring` 的公开 API 固定为第 8 节的代码块。语义：<br>• `components` 为 null 抛 `ArgumentNullException`；`owner` 为 null 或空白抛 `ArgumentException`；`healthProbeFactory` 为 null 抛 `ArgumentNullException`。<br>• **协议总是拥有**的组件：`FrameCodec`、`Correlation`、`KeyExtractor`、`HealthProbe`、`HealthProbeFactory`。调用方已设置任一项时抛 `ArgumentException`，`ParamName` 为 `"components"`，消息为 `ChannelComponents.{属性} is already set; {owner} sets it and does not override it.`。<br>• `Initializer` **只在协议提供了初始化器时**才视为协议拥有：`initializer` 参数非 null 且调用方已设置 → 抛同样的异常；参数为 null → 不检查、不修改（MELSEC 不设初始化器，调用方自己的初始化器原样保留，与现状一致）。<br>• 检查顺序：`FrameCodec`、`Correlation`、`KeyExtractor`、`Initializer`、`HealthProbe`、`HealthProbeFactory`；全部通过后才赋值（不会留下半配置的组件）。<br>• 赋值：`FrameCodec = frameCodec`（可为 null，MELSEC 数据报不设分帧）、`Correlation = correlation`、`KeyExtractor = keyExtractor`（可为 null）、`Initializer`（仅参数非 null 时）、`HealthProbeFactory = healthProbeFactory`；`ReconnectPolicy` 不碰。返回同一实例。<br>• 交付方式与介质的规则（S7、FINS 只接受字节流；MELSEC 的帧类型与介质）**留在各协议的 profile**，在调用 `Apply` 之前检查。<br>MELSEC 的变化只有两处：已设置组件时的消息去掉了 "for MELSEC" 几个字；"组件为 null 且帧类型非法"这类双重错误先报帧类型。两者都写进 CHANGELOG 的"变更" | F4 + F9：三份守卫与四步接线同形；`Initializer` 的条件规则保证 MELSEC 公开的 `McProtocolProfile.Configure` 行为不变 |
| P45-D5 | `CommResult` 可空性（F11）：<br>• `CommResult.IsSuccess` 加 `[MemberNotNullWhen(false, nameof(ErrorMessage))]`；`CommResult<T>.IsSuccess` 加 `[MemberNotNullWhen(true, nameof(Data))]` 与 `[MemberNotNullWhen(false, nameof(ErrorMessage))]`。<br>• net472 没有 `MemberNotNullWhenAttribute`：在 `build/Polyfills/NullableAttributes.cs` 补一个 internal 定义（与现有三个同法）。编译器按特性全名识别，所以标在公开成员上时，引用方的流分析同样生效——顺带改正该文件头注释里"不进入公开 API"的说法。<br>• 让标注成立：`CommResult<T>.Success(T data)` 在 `data` 为 null（引用类型）时抛 `ArgumentNullException`。约定 `T` 不是可空引用类型（仓库中没有 `CommResult<X?>` 的用法，Task 5 先复核）。这是 Core 的行为变化，写进 CHANGELOG 的"破坏性变更"。<br>• 清理：生产代码中因标注而多余的 `.Data!` 与 `.ErrorMessage!` 一律去掉（基线计数：`.Data!` 共 41 处——Channels 5、Tcp 7、Udp 1、Plc 13、Melsec 5、Siemens 5、Omron 5；`ErrorMessage!` 3 处，在 Core）。编译器证明不了的保留，并在汇报中逐处列出原因 | 调用方在 `if (result.IsSuccess)` 分支里可直接用 `Data`；`TreatWarningsAsErrors` 使"去掉 `!` 仍能编译"成为标注生效的证据 |
| P45-D6 | MELSEC 允许改动的生产文件**只有**：`McProtocolProfile.cs`（改用 `PlcComponentWiring`）、`Protocol/McFrameExchange.cs`（改用 `SequenceCounter`）、删除 `Protocol/McSerialNumbers.cs`，以及 Task 5 的 `!` 清理。`McProtocolProfile.Configure` 的公开签名与 XML 注释中的异常列表保持准确。**MELSEC 的测试一个都不改，并保持全绿** | 用户放宽了 P4 的 Q28；行为不变由 MELSEC 的既有测试证明 |
| P45-D7 | Channels、Tcp、Udp 的生产代码**只允许** Task 5 的 `!` 清理（纯语法，不改任何行为）。Serial、WebApi、Modbus 不改 | 控制改动面 |
| P45-D8 | S7、FINS 的测试断言不改：本期是行为不变的重构（Q17）。允许修改的测试代码只有：`TestSupport/` 下的替身改用 `BigEndianFields`（它们现在经 IVT 使用 `S7Fields` / `FinsFields`）；新增的 Core 与 Plc 测试文件 | 断言不变才能证明行为不变 |
| P45-D9 | S7 地址不接受 `MX` / `IX` / `QX`（P4-D16 不变，用户 2026-10-12 决定）。审阅者已写入知识库《S7 协议》的注意事项与 Siemens README 的地址一节，本期不再改动 | 记录决定，避免执行者顺手"修" |

---

## 1. 执行规则（每个 Task 都适用）

1. 每个 Task 一个提交，在 `feat/plc-s7-fins-p4` 上提交。不 push、不 amend、不 rebase、不切换分支。**只 `git add` 本 Task 的文件。**知识库只在 Task 6 回写，**不提交**；汇报时附上 `git status --short -- Junevy.Communication.Wiki` 与 `git diff --stat -- Junevy.Communication.Wiki` 的输出。工作区里知识库已有的未提交改动（P4 的笔记与审阅者的修正）不要还原。
2. 步骤顺序固定：**先写测试并确认失败**（新代码阶段无法编译也算失败）→ 写生产代码 → 测试通过 → 按第 2 节自查并清理 → 提交。纯迁移的 Task（2、3 的迁移部分、4 的迁移部分）没有新测试，以"既有测试不改且全绿"为证据，并在汇报中写明。
3. 每个 Task 完成后依次运行，全部成功才能提交：
   ```bash
   dotnet build Junevy.Communication.slnx -c Debug
   dotnet build Junevy.Communication.slnx -c Release
   dotnet test Junevy.Communication.slnx -c Debug
   ```
   每个测试项目在 net8.0 与 net472 上各运行一次（Modbus 只有 net8.0）。测试宿主因内存紧张崩溃时，先运行 `dotnet build-server shutdown` 再重跑。Channels 的 `HeartbeatTests.OnlyWhenIdle_SkipsWhenTraffic` 是已知的偶发计时失败（另有任务修复）：它单独失败时单独重跑该项目确认，并在汇报中注明，不要为它改代码。
4. 代码约束沿用 P4 计划第 1 节第 4 条：异步 API、`ConfigureAwait(false)`、文件作用域命名空间、英文消息与中文 XML 注释、常量日志模板；net472 禁用清单（不用默认接口成员、`Enumerable.Append`、`SequenceReader<T>`、`Task.WaitAsync`、`ArgumentNullException.ThrowIfNull`、Index/Range、**init 与 record**）。
5. CHANGELOG：在 `CHANGELOG.md` 顶部新增一节 `## [Unreleased] — P4.5（Core / Plc 共用件；CommResult 可空性标注）`，下设 `### 新增（Added）`、`### 变更（Changed）`、`### 破坏性变更（Breaking）`、`### 移除（Removed）`。条目格式为 `- YYYY-MM-DD [包名] 内容`。每个 Task 在提交前写好自己的条目。
6. 不修改：`Junevy.Communication.Modbus/`、`Tests/Junevy.Communication.Modbus.Tests/`、`Skills/using-junevy-modbus/`；WebApi、Serial 的全部代码；P45-D6、P45-D7 之外的 Melsec、Channels、Tcp、Udp 代码；MELSEC、S7、FINS 测试的断言（P45-D8）。
7. 新增公开类型只能是第 5、7、8 节代码块中的三个，成员不多不少。
8. 本计划之外的问题记入第 13 节，只记录不修改。偏离计划的地方（包括发现计划写错）记入第 14.2 节。
9. 每个 Task 的汇报写进第 14.2 节：新增、修改、删除的文件；测试先红后绿的证据（或"纯迁移、既有测试不改"的说明）；第 2 节逐条自查；第 3 节度量；三条命令的结果（每个项目在两个目标上的通过 / 失败 / 跳过数）。
10. 串口：本期不需要。万一要运行串口用例，只允许 COM20 / COM21，**绝不打开 COM3 / COM4**。

---

## 2. 代码质量标准（审阅者逐条验收，不符合即打回）

P4 计划第 2 节列出的 Q1–Q27 全部适用（文件 ≤ 300 行、方法 ≤ 60 行、不重复、依赖方向、可见性最小、无魔法数、无死代码、错误走结果、入口校验、测试确定、不新增生产程序集之间的 IVT、公开 API 只增加计划列出的成员、抽取后删除旧代码、协议层纯净、会话快照线程安全等）。另外：

- **Q28′ MELSEC 行为不变**（取代 P4 的 Q28）：MELSEC 的测试不修改并保持全绿；MELSEC 生产代码只在 P45-D6 列出的位置改动。
- **Q29 不留转发层**：`S7Fields`、`FinsFields`、`S7PduReferences`、`FinsSessionIds`、`McSerialNumbers` 整个删除，调用点直接使用 Core 的类型；不允许保留"只是转调 Core"的薄包装。三个 profile 中不再有 `RequireUnset` / `ThrowAlreadySet`。
- **Q30 Core 与 Plc 的新类型协议无关**：`BigEndianFields`、`SequenceCounter` 的代码与 XML 注释里不出现 S7、FINS、MELSEC 的术语（可以在 README 里举例）；`PlcComponentWiring` 的注释只谈"协议"，不点名具体协议。
- **Q31 清理 `!` 不改行为**：Task 5 在 Channels、Tcp、Udp、Melsec 等文件里只删除 `!`，不做任何其他编辑（包括格式化与重命名）；`git diff -w` 中每一处改动都应只是去掉一个 `!`（`CommResult.cs`、`NullableAttributes.cs` 与 Task 5 新增的守卫除外）。
- **Q32 迁移后线上字节不变**：S7 与 FINS 的黄金向量测试、MELSEC 的帧测试全部原样通过；这是 Task 2、3、4 的主要证据。

---

## 3. 度量（Task 0 记录基线，Task 7 复测）

范围与命令同 P4 计划第 3 节（Core、Channels、Tcp、Udp、Serial、Plc、Melsec、WebApi、Siemens、Omron 的 `*.cs`，用 `git ls-files` 取文件，排除 `obj/`、`bin/`）。`dupscan.py` 的"重复窗口"按**各集群窗口数之和**读。

| 指标 | 基线（`d306543`，审阅者实测） | P4.5 目标 |
|---|---|---|
| 重复窗口（6 行） | 54（其中 P4 新增 21：`FinsFields ↔ S7Fields` 9、`FinsProtocolProfile ↔ S7ProtocolProfile` 8、`McProtocolProfile ↔ FinsProtocolProfile ↔ S7ProtocolProfile` 3、`FinsFraming ↔ S7Framing` 1） | **≤ 34**：前三个集群消失，只允许保留 `FinsFraming ↔ S7Framing` 的 1 个；新增窗口为 0 |
| 超过 60 行的方法 | 0 | 0 |
| 超过 300 行的文件 | 9（全部在 Core / Channels / Tcp） | 9 |
| 生产程序集之间的 IVT | 2 | 2 |
| 有效行 | Core 979、Plc 1189、Melsec 1275、Siemens 1367、Omron 772 | 记录增减：Core、Plc 增，Melsec、Siemens、Omron 减 |
| 测试数（两个目标） | Core 116、Channels 278、Tcp 129、Udp 38、Serial 63 + 2 跳过、Plc 245、Melsec 184 + 1 跳过、Architecture 11、WebApi 732（net472 733）、Modbus 405、Siemens 316、Omron 259 | Core、Plc 增加；其余不变（S7、FINS 若因删类型而删测试，必须在汇报中逐条说明，原则上不应发生） |

---

## 4. Task 0：基线

1. 运行三条命令，确认第 3 节的测试数；按第 3 节记录度量，写入第 14.3 节。
2. 复核 P45-D5 的前提：`grep -rnE "CommResult<[^>]+\?>"` 在生产与测试代码中无结果；`grep -rn "Success(null\|Success(default"` 列出所有可能给 `CommResult<T>.Success` 传 null 的调用点。
3. `git commit -m "chore: record the P4.5 baseline"`（只含本计划文件）。

---

## 5. Task 1：Core 的 `BigEndianFields`（P45-D2）

**公开 API（固定）**

```csharp
namespace Junevy.Communication.Core.Buffers;

public static class BigEndianFields
{
    public const int UInt8Bytes = 1;
    public const int UInt16Bytes = 2;
    public const int UInt24Bytes = 3;
    public const int UInt32Bytes = 4;

    public static void WriteUInt8(IBufferWriter<byte> output, byte value);
    public static void WriteUInt16(IBufferWriter<byte> output, ushort value);
    public static void WriteUInt24(IBufferWriter<byte> output, int value);     // 0–0xFFFFFF，否则 ArgumentOutOfRangeException
    public static void WriteUInt32(IBufferWriter<byte> output, uint value);
    public static void WriteBytes(IBufferWriter<byte> output, ReadOnlySpan<byte> value);

    public static bool TryTakeUInt8(ref ReadOnlySpan<byte> data, out byte value);
    public static bool TryTakeUInt16(ref ReadOnlySpan<byte> data, out ushort value);
    public static bool TryTakeUInt24(ref ReadOnlySpan<byte> data, out int value);
    public static bool TryTakeUInt32(ref ReadOnlySpan<byte> data, out uint value);
    public static bool TryTakeBytes(ref ReadOnlySpan<byte> data, int length, out ReadOnlySpan<byte> value);  // length < 0 抛 ArgumentOutOfRangeException
}
```

1. 文件 `Junevy.Communication.Core/Buffers/BigEndianFields.cs`。多字节写入与读取可用 `BinaryPrimitives`（net472 由 `System.Memory` 提供），`UInt24` 手写移位。XML 注释说明"字节不足返回 false、不移动 `data`"的约定。
2. 测试 `Tests/Junevy.Communication.Core.Tests/BigEndianFieldsTests.cs`：
   - 每个宽度的写入字节（例如 `0x1234` → `12 34`，`0x123456` → `12 34 56`，`0x12345678` → `12 34 56 78`）与读回往返；
   - 读取恰好够长、多出字节（只取前面的、`data` 剩余正确）、少一个字节（返回 false、`data` 不变、`value` 为 0 或空）；
   - `WriteUInt24` 的 −1 与 0x1000000 抛异常、0 与 0xFFFFFF 通过；`TryTakeBytes` 的长度 0（成功、空）与 −1（抛）；`output` 为 null 抛 `ArgumentNullException`；
   - 连续写入多个字段后整体比较（模拟一个报文头）。
3. Core README 的 "Buffers" 一节加一小段 `BigEndianFields` 用法（英文，示例不点名具体协议，可以说"a protocol header"）。CHANGELOG "新增"。
4. 提交：`feat(core): add big-endian field helpers`

---

## 6. Task 2：S7 与 FINS 改用 `BigEndianFields`（F8）

1. Siemens 的 `Protocol/` 与 Omron 的 `Protocol/` 中所有 `S7Fields.*` / `FinsFields.*` 改为 `BigEndianFields.*`（`WriteRaw` → `WriteBytes`，`TryTakeRaw` → `TryTakeBytes`，宽度常量同名）。删除 `Junevy.Communication.Siemens/Protocol/S7Fields.cs` 与 `Junevy.Communication.Omron/Protocol/FinsFields.cs`。
2. 测试替身 `Tests/Junevy.Communication.Siemens.Tests/TestSupport/S7DeviceSimulator.cs` 与 `Tests/Junevy.Communication.Omron.Tests/TestSupport/FinsDeviceSimulator.cs` 同样改用 `BigEndianFields`（P45-D8 允许）。
3. 注意：`S7Fields.WriteUInt24` 原本不校验范围，`BigEndianFields.WriteUInt24` 会校验。调用点（`S7VarItem.WriteParameters` 的地址字段）的值已由 `S7Client.ValidateRange` 保证不超过 0xFFFFFF——确认这一点并在汇报中写明，不要在调用点再加一层校验。
4. 证据：S7 与 FINS 的全部测试不改且全绿（含黄金向量），`dupscan` 中 `FinsFields ↔ S7Fields` 集群消失。
5. CHANGELOG "变更"（[Siemens]、[Omron] 内部改用 Core 的字段工具，线上字节不变）。
6. 提交：`refactor(siemens, omron): use the Core big-endian field helpers`

---

## 7. Task 3：Core 的 `SequenceCounter` 与三个协议（P45-D3，F3）

**公开 API（固定）**

```csharp
namespace Junevy.Communication.Core.Utils;

public sealed class SequenceCounter
{
    public ushort NextUInt16();   // 低 16 位，线程安全，第一次调用返回 1，65535 之后回到 0
    public byte NextByte();       // 低 8 位，线程安全，第一次调用返回 1，255 之后回到 0
}
```

1. 文件 `Junevy.Communication.Core/Utils/SequenceCounter.cs`：一个 `int` 字段 + `Interlocked.Increment`，`unchecked` 截断（与现有三份实现同一算法，`int` 溢出后截断结果仍正确）。XML 注释写明"一个实例只按一种宽度使用"。
2. 测试 `Tests/Junevy.Communication.Core.Tests/SequenceCounterTests.cs`：第一次为 1；连续调用递增；`NextByte` 第 256 次回到 0、`NextUInt16` 第 65536 次回到 0；并发调用（例如 8 个任务各取 1000 次 `NextUInt16`）得到的 8000 个值互不相同。
3. 迁移（P45-D3 的位置）：
   - MELSEC：`McFrameExchange` 新增 `private static readonly SequenceCounter SerialNumbers = new SequenceCounter();`，`McSerialNumbers.Next()` 改为 `SerialNumbers.NextUInt16()`；删除 `Protocol/McSerialNumbers.cs`。
   - S7：`S7Message` 新增 `internal static readonly SequenceCounter PduReferences`；`S7Client.Hooks`、`S7Handshake`、`S7ReadProbe` 改用 `S7Message.PduReferences.NextUInt16()`；删除 `Protocol/S7PduReferences.cs`。
   - FINS：`FinsHead` 新增 `internal static readonly SequenceCounter SessionIds`；`FinsClient.Hooks`、`FinsReadProbe` 改用 `FinsHead.SessionIds.NextByte()`；删除 `Protocol/FinsSessionIds.cs`。
   - 字段的 XML 注释保留原来的决策出处（P2-D11、P4-D17：客户端请求与心跳探测共用、进程级）。
4. Core README 的 "Timeout Scope" 之后加一小节 "Sequence Counter"。CHANGELOG "新增"（[Core]）与"移除"（[Melsec]、[Siemens]、[Omron] 的内部计数器类）。
5. 提交：`feat(core): add a sequence counter and use it in the PLC protocols`

---

## 8. Task 4：Plc 的 `PlcComponentWiring` 与三个 profile（P45-D4，F4、F9）

**公开 API（固定）**

```csharp
namespace Junevy.Communication.Plc;

public static class PlcComponentWiring
{
    public static TComponents Apply<TComponents>(
        TComponents components,
        string owner,
        IFrameCodecFactory? frameCodec,
        CorrelationMode correlation,
        IFrameKeyExtractor? keyExtractor,
        IConnectionInitializer? initializer,
        Func<IByteChannel, IHealthProbe> healthProbeFactory)
        where TComponents : ChannelComponents;
}
```

1. 文件 `Junevy.Communication.Plc/Factory/PlcComponentWiring.cs`（与 `PlcChannelBinding` 同目录）。语义按 P45-D4，逐条落实；XML 注释写清"总是拥有的五项"与"`Initializer` 的条件规则"，以及"交付方式与介质的规则由协议在调用前检查"。
2. 测试 `Tests/Junevy.Communication.Plc.Tests/PlcComponentWiringTests.cs`：
   - 三个 null / 空白参数的异常；
   - 五项总是拥有的组件逐项预设 → `ArgumentException`，`ParamName == "components"`，消息以 `ChannelComponents.{属性} is already set; {owner}` 开头；
   - `Initializer` 已预设、参数为 null → 不抛，预设值保留；已预设、参数非 null → 抛；
   - 守卫失败时组件没有被部分赋值（例如 `HealthProbeFactory` 已预设时，`FrameCodec` 仍为 null）；
   - 全部赋值正确、`frameCodec` / `keyExtractor` 为 null 时对应属性为 null、预设的 `ReconnectPolicy` 保留、返回同一实例。
3. 迁移（调用点一律使用**命名参数**，便于核对）：
   - `S7ProtocolProfile.Configure`：先检查交付方式（消息不变），再 `PlcComponentWiring.Apply(components, owner: nameof(S7ProtocolProfile), frameCodec: S7Framing.Create(), correlation: CorrelationMode.Sequential, keyExtractor: null, initializer: new S7Handshake(settings, session), healthProbeFactory: channel => new S7ReadProbe(channel))`。原有的 P4-D8、P4-D15 注释保留在调用点旁。删除 `RequireUnset`、`ThrowAlreadySet`。
   - `FinsProtocolProfile.Configure`：同上。
   - `McProtocolProfile.Configure`（internal 重载）：先 `RequireFrame`，再 `Apply(..., owner: nameof(McProtocolProfile), frameCodec: delivery == ChannelDelivery.Stream ? McStreamFraming.Create(settings, FramingOptions.DefaultMaxFrameLength) : null, correlation: ..., keyExtractor: ..., initializer: null, healthProbeFactory: channel => new McLoopbackProbe(channel, settings))`。删除 `RequireComponents`、`RequireUnset`、`ThrowAlreadySet`、`ApplyCorrelation`（其中关联规则的注释移到调用点）。公开重载的签名与 XML 注释保持准确（异常列表按新顺序核对）。
   - 动手前先 `grep` MELSEC 测试中针对 `McProtocolProfile` 的用例，确认没有用例同时触发"组件为 null 或已预设"与"帧类型非法"两种错误。若有，**停下汇报**（不要改测试，也不要为保持顺序而拆分 `Apply`）。
4. 证据：S7、FINS、MELSEC 的 profile 测试与端到端测试不改且全绿；新测试先红后绿；`dupscan` 中两个 profile 集群消失。
5. Plc README 的 "Writing a Protocol" 一节加 `PlcComponentWiring` 的用法与规则（英文）。CHANGELOG "新增"（[Plc]）、"变更"（[Melsec] 消息文本与双重错误的报告顺序，见 P45-D4）。
6. 提交：`feat(plc): add component wiring for protocol profiles`

---

## 9. Task 5：`CommResult` 可空性标注（P45-D5，F11）

1. `build/Polyfills/NullableAttributes.cs` 增加 `MemberNotNullWhenAttribute`（`AttributeTargets.Method | AttributeTargets.Property`，`AllowMultiple = true`，`Inherited = false`；两个构造函数：`(bool returnValue, string member)` 与 `(bool returnValue, params string[] members)`；属性 `ReturnValue`、`Members`），并改正文件头注释（标在公开成员上的特性对引用方的流分析同样生效）。
2. `CommResult.cs`：按 P45-D5 加三个特性；`CommResult<T>.Success` 加 null 守卫，XML 注释的 `<param>` 与 `<exception>` 同步。
3. 先写测试（加进已有的 `Tests/Junevy.Communication.Core.Tests/CommResultTests.cs`）：`CommResult<string>.Success(null!)` 抛 `ArgumentNullException`；`CommResult<int>.Success(0)` 与 `CommResult<string>.Success("")` 成功。确认失败后再写守卫。
4. 清理 `!`（Q31）：在 P45-D5 列出的生产项目中去掉 `.Data!` 与 `.ErrorMessage!`，以 Debug 与 Release 构建通过（`TreatWarningsAsErrors`）为准；编译器证明不了的（例如跨越方法调用的属性链）保留原样，汇报中逐处列出文件、行号与原因。`Junevy.Communication.Modbus` 不在范围内。
5. 文档：Core README 的 "Results and Error Kinds" 一节说明"成功分支内 `Data` 非空、失败分支内 `ErrorMessage` 非空"以及 `Success` 的 null 守卫。CHANGELOG "新增"（[Core] 可空性标注）、"破坏性变更"（[Core] `Success(null)` 抛异常）、"变更"（各包去掉多余的 `!`，无行为变化）。
6. 提交：`feat(core): annotate CommResult nullability`

---

## 10. Task 6：文档与知识库

1. Skill `Skills/using-junevy-plc/SKILL.md`：协议作者指南改为使用 `PlcComponentWiring`、`BigEndianFields`、`SequenceCounter`；删掉"三份计数器 / 三份守卫"的摩擦说明（约第 449–451 行附近）与 `Data ?? default` 的变通解释（改为说明标注已生效）。示例代码与 README 一样，在临时项目里经 net8.0 与 net472 编译验证（验证材料不入库）。
2. 知识库（只写不提交）：
   - `Agent协作/新增协议指南.md`：把 F3、F4、F8、F9、F11 的条目改为"已由 P4.5 解决，新协议直接用……"，并写明第四个协议的接线方式；
   - `架构设计/Plc 地址型设备协议套件.md`：加 `PlcComponentWiring` 一节；
   - `协议细节/S7 协议.md`、`协议细节/FINS 协议.md`：更新指向已删除文件（`S7Fields.cs`、`S7PduReferences.cs`、`FinsFields.cs`、`FinsSessionIds.cs`）的链接；
   - 用 `Grep` 搜整个知识库的 `McSerialNumbers`、`S7Fields`、`FinsFields`、`RequireUnset`、`Data!`，逐处更新；涉及 `CommResult` 的 API 参考笔记补可空性说明；
   - `知识库首页.md` 的版本与时效一行补 P4.5。
3. 设计文档新增第 20.6 节"实施修订（P4.5）"：表格列出本期的四项变化、原因（指向本计划的决策号）与对第 12 节的影响。
4. P4 计划第 20.2 节：在 F3、F4、F8、F9、F11 末尾各加一句"已在 P4.5 解决（本计划 Task N）"。
5. 提交：`docs: document the shared protocol parts and CommResult nullability`

---

## 11. Task 7：最终度量

1. 按第 3 节复测，写入第 14.3 节；对照目标逐项判定。重复窗口逐集群列出，确认只剩 `FinsFraming ↔ S7Framing` 一个跨协议窗口。
2. 记录有效行增减，并给出一句结论："新增一个协议"现在少写哪些代码（对照 P4 计划第 20.2 节"扩展性结论"的第三部分）。
3. 三条命令连续运行三次，结果一致。
4. 提交：`chore: record the P4.5 final metrics`

---

## 12. 审阅者的验收清单（执行者据此自查）

**代码**

- 第 2 节 Q1–Q27、Q28′、Q29–Q32 逐条检查；第 3 节度量达标。
- 新增公开类型只有三个，成员与第 5、7、8 节逐条一致；架构测试通过，IVT 仍为 2。
- 被删除的五个类型与三份守卫没有残留（`grep` 为空），没有转发层。
- MELSEC、Channels、Tcp、Udp 的 diff 只在 P45-D6、P45-D7 允许的位置；`git diff -w` 抽查 `!` 清理只删了 `!`。
- README、Skill 的示例能编译。

**功能**（审阅者复跑三条命令三遍，并做变异检查：临时改坏下列规则，确认有测试失败）

- `BigEndianFields`：`WriteUInt16` / `WriteUInt24` / `WriteUInt32` 的字节顺序（S7、FINS 黄金向量应失败）；读取失败时移动了 `data`；`WriteUInt24` 不校验范围。
- `SequenceCounter`：`NextByte` 取错字节；起始值不是 1。
- `PlcComponentWiring`：漏掉任一项守卫；`Initializer` 无条件检查（MELSEC 场景的测试应失败）；不赋值 `Initializer`（S7 / FINS 握手相关测试应失败）；先赋值后检查（部分赋值测试应失败）。
- `CommResult`：去掉 `MemberNotNullWhen`（生产构建应失败）；去掉 `Success` 的 null 守卫（Core 测试应失败）。
- MELSEC、S7、FINS 的全部测试不修改并保持全绿。

---

## 13. 未纳入本计划的条目

- 测试基建（P4 计划 F6、F7、F10，Task 7 偏差 4、Task 8 偏差 6）：P5，见知识库 `测试/P5 测试基建整理（待办）.md`。
- 钩子测试门面（F5）：用户决定不做。
- F1（S7 地址需要自写解析器）、F2（位/字节换算 3 行）：不处理。
- `S7Framing` / `FinsFraming` 的 1 个重复窗口：接受。
- `WebApiResult<T>` 的同类可空性标注；Modbus 的 `ModbusResult`。
- Channels `HeartbeatTests.OnlyWhenIdle_SkipsWhenTraffic` 偶发失败：另有任务。

---

## 14. 验收记录

### 14.1 各 Task 验收（审阅者填写）

| Task | 结论 | 说明 |
|---|---|---|

### 14.2 执行汇报与偏差（执行者填写）

### 14.3 基线与度量（执行者填写）
