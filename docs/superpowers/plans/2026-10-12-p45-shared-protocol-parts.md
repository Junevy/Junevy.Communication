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
| 0 | 通过 | 基线与第 3 节一致；P45-D5 的前提复核正确（`Success(null/default)` 的 5 处命中都是 `WebApiResult<T>`） |
| 1 | 通过 | 公开成员与第 5 节逐条一致；变异 ①–⑧ 全部被抓住 |
| 2 | 通过 | 只有机械替换；S7、FINS 测试只改了两个替身的调用点，断言未动；黄金向量全绿 |
| 3 | 通过（审阅者修正注释） | 算法与三份旧实现一致，变异 ⑨⑩ 被抓住。`SequenceCounter` 的注释、Core README、CHANGELOG 有两处不准确：说"混用两种宽度会让 8 位序号重复"（实际是两种宽度都跳号）；说"回绕不影响配对，因为应答按发送顺序匹配"（MELSEC 4E 按序列号匹配，理由不成立）。已改为"同一个号在它的应答返回之前不会再被取到"。README 示例在同一个实例上混用两种宽度，与紧接着的建议矛盾，已改为两个实例 |
| 4 | 通过（审阅者修正 CHANGELOG） | 语义逐条符合 P45-D4，变异 ⑪–⑯ 全部被抓住；MELSEC 测试未改且全绿。CHANGELOG 把三条 profile 改动放进了"移除"，已移到"变更"；S7、FINS 两条写着"is already set 的消息文本保持原样"，实际上 "for S7"、"for FINS" 同样被去掉，已改正；设计文档 20.6 节"错误消息都不变"的说法同步改正 |
| 5 | 通过 | Channels、Tcp、Udp 的 13 处改动逐行核对，只去掉了 `!`；生产代码已无 `.Data!`；去掉 `MemberNotNullWhen` 后 Plc 构建报 CS8600 / CS8602 / CS8604，去掉 null 守卫后 Core 测试失败 |
| 6 | 通过（审阅者修正示例） | Core README 的 `HeaderExample` 缩进错乱、`TryTake*` 的说明写成"值保持不变"（实际是取默认值），Plc README 的示例把 16 位序号写进 4 字节字段，均已改正 |
| 7 | 通过 | 审阅者复算：343 个文件、重复窗口 34（只剩 `FinsFraming ↔ S7Framing` 1 个跨协议窗口）、超长方法 0、超过 300 行的文件 9、有效行逐项一致；五个被删类型在代码中无残留 |

**复跑**：Debug / Release（`--no-incremental`）构建均为 0 错误（31 条既有警告）；`dotnet test -c Debug --no-build` 连续三次，每次 23 个测试宿主全部通过，合计 5270 通过、6 跳过、0 失败。

**变异检查**（18 条，跑完逐条恢复原文件）：① `WriteUInt16` 字节序（Core）② 同上（S7 向量，73 个失败）③ `WriteUInt24` 字节序（Core）④ 同上（S7 向量，22 个失败）⑤ `WriteUInt32` 字节序（Core）⑥ 同上（FINS 向量，48 个失败）⑦ 读取失败时移动了切片 ⑧ `WriteUInt24` 不校验范围 ⑨ `NextByte` 取错字节 ⑩ 起始值不是 1 ⑪ 漏掉 `KeyExtractor` 守卫（Plc）⑫ 同上（S7 profile）⑬ `Initializer` 无条件检查 ⑭ 不赋值 `Initializer`（Plc）⑮ 同上（FINS，32 个失败）⑯ 守卫未全部通过就赋值 ⑰ 去掉 `MemberNotNullWhen`（Plc 构建失败）⑱ 去掉 `Success` 的 null 守卫。**18 条全部被抓住**。

### 14.2 执行汇报与偏差（执行者填写）

#### Task 7：最终度量

本 Task 只测量与记录，**没有改动任何生产代码**（只在本计划文件与 `CHANGELOG.md` 追加记录）。度量、三条命令连跑三次的逐项结果、以及"新增一个协议现在少写什么"的结论见 **14.3 的"最终度量"**。

#### Task 6：文档与知识库

**新增**：无。
**修改（入库）**：`Skills/using-junevy-plc/SKILL.md`、`docs/superpowers/specs/2026-10-10-communication-core-design.md`（新增第 20.6 节）、`docs/superpowers/plans/2026-10-12-p4-s7-fins.md`（第 20.2 节的 F3 / F4 / F8 / F9 / F11 末尾各加一句）、`CHANGELOG.md`。
**修改（知识库，只写不提交）**：`知识库首页.md`、`zh/content/Agent协作/新增协议指南.md`、`zh/content/架构设计/Plc 地址型设备协议套件.md`、`zh/content/协议细节/S7 协议.md`、`zh/content/协议细节/FINS 协议.md`。

**第 6 节 Task 6 第 1 条（Skill）**：

- 协议作者指南的 "Transport profile and factories" 改为直接给 `PlcComponentWiring.Apply` 的完整调用（不是裸调用片段），并写明五项"总是拥有"、`Initializer` 的条件规则、`ReconnectPolicy` 不碰、`frameCodec: null` / `keyExtractor: null` 的含义，以及"交付方式与介质规则在调用**之前**检查"。
- 补两条共用件说明：头部字段走 `BigEndianFields`；配对字段的序号来自挂在编码器类型上的 `SequenceCounter`。
- "What you must write and what you do not" 表的两行更新：编码器那行注明不再需要自写大端字段工具，profile 那行注明 profile 本身只是一次 `Apply` 调用加自己的交付规则。
- 摩擦说明 F3 / F4 / F8 / F9 / F11 五条都改成"**Resolved in P4.5**"开头，并给出新协议该怎么写；F11 的 `result.Data ?? fallback` 变通解释改成"直接写 `result.Data`，不要加合并运算符或 null-forgiving"。
- 结尾那句"第四个协议会再写一遍五件事"改为：F3 / F4·F9 / F8 **不会**再来（已是共用件），只剩 F7 与 F10。
- **示例编译验证**（计划要求"在临时项目里经 net8.0 与 net472 编译验证，验证材料不入库"）：6 个 `csharp` 代码块**逐个**编译，全部 0 错误。

**验证过程中的一处自查（重要）**：第一版验证脚本**并没有真正编译**——临时工程的 `ProjectReference` 路径写错（`Junevy.Communication.Testing` 在 `Tests/` 下，不在仓库根），加上 `Microsoft.Extensions.Configuration.Json` 的版本不存在，restore 直接 NU1603 失败，而我每轮跑了两遍 build（第二遍是增量的空跑），于是"6/6 OK"全是假的。是靠**负控实验**（故意写一个 `int y = 1 + null;` 的片段）报出 6 个错误才发现的。修正路径与包版本、每轮只跑一次 `--no-incremental` 之后，负控稳定报错，真实片段才逐一验证：

| 代码块 | 结果 |
|---|---|
| 0 `McTcpExample` | OK |
| 1 `S7SkillExample` | OK |
| 2 `FinsSkillExample` | OK |
| 3 `McErrorHandlingExample` | OK |
| 4 `McDependencyInjectionExample` | OK（需要 `Microsoft.Extensions.Configuration.Abstractions`，已加进临时工程） |
| 5 新增的 `MyProtocolProfile` | **先失败**：用了 `ArgumentException` 却缺 `using System;`（CS0246）→ 补上后 OK |

Task 1 / 4 / 5 的 README 验证也用了同样的负控思路；其中 Task 1 的 `HeaderExample`（缺 `out` 赋值）、Task 4 的 `ProtocolWiringExample`（同样缺 `using System;`）、Task 5 的 `CommResult` 示例（用了未定义的 `Send` / `Consume` / `Log`）三处真实缺陷都是这样发现的。**Task 1 的那次负控当时没有做，结论是靠真实报错支撑的**；为免同样的疏漏，这次对每个验证脚本都先跑了负控。

**第 6 节 Task 6 第 2 条（知识库，只写不提交）**：

- `Agent协作/新增协议指南.md`：F3、F4+F9、F8、F11 四条改写为"已由 P4.5 解决，新协议直接用……"并给出各自的形状（`SequenceCounter` 挂在写字段的类型上、`PlcComponentWiring.Apply` 的七参与规则、`BigEndianFields` 的成员与"字节不足返回 false 且不移动切片"的约定、`IsSuccess` 的两条不变式）；收尾那条把 F3/F4·F9/F8 划归"已抽取"，F7、F10、F5 留给 P5。
- `架构设计/Plc 地址型设备协议套件.md`：新增 "P4.5 变更：三个协议共用的接线与字段件" 一节，四个子节（`PlcComponentWiring` / `BigEndianFields` / `SequenceCounter` / `CommResult` 可空性），含 profile 的行数变化（58 → 34、58 → 34、89 → 54）与 54 → 34 的度量；并注明 `WebApiResult<T>` 与 `ModbusResult` 未做同类标注。
- `协议细节/S7 协议.md`、`FINS 协议.md`：指向已删除文件的 `file://` 链接改为指向仍然存在的承载类型（`Protocol/S7Message.cs`、`Protocol/FinsHead.cs`），并说明计数器来自 Core 的 `SequenceCounter`。
- `知识库首页.md`：版本清单加一条"共用件 P4.5"；"版本与时效"段落加一条 P4.5 的基线与测试数。
- 用 `Grep` 扫过整个知识库的 `McSerialNumbers` / `S7Fields` / `FinsFields` / `RequireUnset` / `S7PduReferences` / `FinsSessionIds` / `Data ??`，剩余命中全部是**有意保留的历史叙述**（"取代了 `S7Fields` / `FinsFields`"、"原先三个 profile 各自的 `RequireUnset`"），没有指向已删除文件的死链接。
- **一处刻意不改**：`项目概述/快速开始指南.md:36` 的 `result.Data!` 属于 **Modbus** 示例（`modbus.ReadHoldingRegisters` 返回 `ModbusResult`）。`ModbusResult` 与 `WebApiResult<T>` 的同类标注被 P45-D1 明确排除在本期之外，删掉 `!` 会让这段示例编译不过，因此保留。

**第 6 节 Task 6 第 3、4 条**：设计文档新增第 20.6 节（四行表格 + 对第 12 节与 20.5 的影响说明）；P4 计划第 20.2 节的五条摩擦末尾各加一句"已在 P4.5 解决（本计划 Task N）"，F3 → Task 3、F4 → Task 4、F8 → Task 1、2、F9 → Task 4、F11 → Task 5。

**三条命令的结果**：本 Task 只改 Markdown，`dotnet build -c Debug`、`dotnet build -c Release`、`dotnet test -c Debug` 均与 Task 5 结束时相同（0 错误 / 0 错误 / 23 个项目目标组合 0 失败）。

#### Task 5：`CommResult` 可空性标注（P45-D5，F11）

**新增**：无新文件。
**修改**：`build/Polyfills/NullableAttributes.cs`（加 `MemberNotNullWhenAttribute`，改正文件头注释）、`Junevy.Communication.Core/Results/CommResult.cs`（三个特性 + `Success` 的 null 守卫 + 两处 XML 注释）、`Tests/Junevy.Communication.Core.Tests/CommResultTests.cs`（加 3 条用例）、**23 个生产文件**的纯 `!` 清理、`Junevy.Communication.Core/README.md`、`CHANGELOG.md`。

**测试先红后绿的证据**：先只往 `CommResultTests.cs` 加用例，`dotnet test --filter CommResultTests` → **12 个用例里 1 失败**（`Success_NullData_Throws` 报 `Assert.Throws` 未抛异常），两个目标都失败。加上 null 守卫后转绿；Core 全量 **160 通过 / 0 失败 / 0 跳过**（net8.0 与 net472 各一次），比 Task 4 后多 3 条（`Success_NullData_Throws`、`Success_DefaultValueTypes_Succeed`、`Success_EmptyReferenceData_Succeeds`）。

**第 2 节自查**：

- **Q31 清理 `!` 不改行为**：逐行核对过。`git diff -U0` 的 **41 个改动行**里，每一行的"旧行去掉 `.Data!` 的叹号"与"新行"**完全相等**（脚本核对结果 `non-'!'-removal diffs=0`）；另有 3 行在 `CommResult.cs` 里是 `ErrorMessage!`（前面没有点，因此不在同一批正则里，单独处理）。分布与计划基线一致：Core 3、Channels 5、Tcp 7、Udp 1、Plc 13、Melsec 5、Siemens 5、Omron 5 = **44 处**，与第 3 节 P45-D5 写的"41 + 3"逐个吻合。
- **标注确实生效的证据**：
  - 正向：`TreatWarningsAsErrors` 下 Debug 与 Release 均 **0 错误**——41 处 `.Data!` 去掉叹号后编译器不再报 CS8604，说明流分析认得标注。
  - 反向（变异检查）：临时把 `CommResult.cs` 里三行 `[MemberNotNullWhen…]` 删掉再构建 → **12 个 CS8604**（"可能为 null 的参数"）；恢复后回到 0 错误。这正是审阅者清单里"去掉 `MemberNotNullWhen`（生产构建应失败）"那一条的提前验证。
- **编译器证明不了的、需要保留的 `!`**：**没有**。计划第 6 节 Task 5 第 4 条要求的"逐处列出原因"因此是空列表。`PlcTransfer.cs:154` 的 `packet.Data!.Length` 与 `PlcClientExtensions.cs:178` 的 `bytes.Data!.AsSpan(…)` 都属"跨越属性链 / 传给 Span 转换"的情形，编译器同样能证明，因此一并去掉。
- **net472 侧**：`MemberNotNullWhenAttribute` 是 `internal sealed`、`AttributeUsage(Method | Property, AllowMultiple = true, Inherited = false)`、两个构造函数、`ReturnValue` 与 `Members` 属性，与 BCL 的定义一致；`[AttributeUsage]` 的 `Inherited = false` 与既有三个 polyfill 同一写法。文件头注释已改成正确说法（"编译器按特性全名识别，标在公开成员上时引用方的流分析同样生效"）。
- **行为变化只有一处**：`CommResult<T>.Success(null)` 现在抛 `ArgumentNullException`（`CommResult.Fail` 的 `message` 守卫早就有，同样写法）。已写进 CHANGELOG 的"破坏性变更"，`<param>` 与 `<exception>` 已同步。Task 0 复核过没有 `CommResult<X?>` 的用法，也没有任何 `CommResult<T>.Success(null/default)` 的调用点（那 5 处 `Success(default…)` 全是 `WebApiResult<T>`，不在本期范围）。
- **Q28′ MELSEC 行为不变**：Melsec 的 diff 只有 5 处 `!`（`McLoopbackProbe.cs` 1、`McFrameExchange.cs` 4），测试 **184 + 1 跳过** 与基线一致。
- **依赖方向 / 公开 API**：没有新增程序集引用；`MemberNotNullWhenAttribute` 是 `internal`，不进入公开 API（第 2 节的 Q11 只要求公开 API 不增加计划外的成员，这里是零增加）。
- **文件 ≤ 300 行 / 方法 ≤ 60 行**：`CommResult.cs` 182 → 196 行（增加的是注释与守卫），仍在限内；`methodlen.py 61` 无输出。

**第 3 节度量**：重复窗口仍是 **34**（本 Task 只做语法级清理，不改结构）；超过 60 行的方法 0；超过 300 行的文件 9；IVT 仍为 2。有效行变化极小（`CommResult.cs` +14 行注释与守卫，其余 22 个生产文件净 0）。

**三条命令的结果**：`dotnet build -c Debug` 0 错误；`dotnet build -c Release` 0 错误、31 个警告（同基线）；`dotnet test -c Debug` **23 个测试项目 / 目标组合全部通过，0 失败**——Core **160**、Plc 262、Channels 278、Tcp 129、Udp 38、Serial 63+2 跳过、Melsec 184+1 跳过、Architecture 11、WebApi 732 / 733、Modbus 405、Siemens 316、Omron 259。

**顺带发现并修正**：Core README 新增的示例初版用了未定义的 `Send` / `Consume` / `Log` 占位名，在临时项目里编译失败（CS0103）；已改成完全自足的代码并经 net8.0 与 net472 编译通过。

#### Task 4：Plc 的 `PlcComponentWiring` 与三个 profile（P45-D4，F4、F9）

**新增**：`Junevy.Communication.Plc/Factory/PlcComponentWiring.cs`（95 行）、`Tests/Junevy.Communication.Plc.Tests/PlcComponentWiringTests.cs`。
**修改**：`Siemens/S7ProtocolProfile.cs`、`Omron/FinsProtocolProfile.cs`、`Melsec/McProtocolProfile.cs`、`Junevy.Communication.Plc/README.md`（"Writing a Protocol" 加 "Wiring the Components" 与 "Shared Parts" 两小节）、`CHANGELOG.md`。
**删除**：无文件被删除，但三个 profile 里**总共 6 个私有方法**整体删除：`S7ProtocolProfile.RequireUnset` / `ThrowAlreadySet`、`FinsProtocolProfile.RequireUnset` / `ThrowAlreadySet`、`McProtocolProfile.RequireComponents` / `RequireUnset` / `ThrowAlreadySet` / `ApplyCorrelation`（后者连同它的注释一起拆进调用点）。

**动手前的第 3 条前提核查（计划要求"若有就停下汇报"）**：

- `McProtocolOptions.FrameType` 默认 `Frame3E`（`McClientOptions.cs:11`）。
- 逐个核对 MELSEC 的 12 个 profile 用例：**没有任何一个同时触发"组件为 null 或已预设"与"帧类型非法"**。
  - 5 个 `Configure_Stream_Rejects*SetByTheCaller` 与 `Configure_Datagram_RejectsACorrelationSetByTheCaller` 都用默认 `McClientOptions`（3E），Stream/Datagram + Network 都是 3E 合法组合 → 只有一种错误。
  - `Configure_WithNullComponents_IsAnArgumentNullException` 用默认选项 + Stream + Network → 3E 合法 → 只有一种错误。
  - `Configure_RejectsAFrameTheDeliveryOrMediumDoesNotCarry` 的 4 个组合用**全新**的 `ChannelComponents` → 只有一种错误。
  - `Configure_WithAnUndefinedDelivery/Medium` 仍断言 `ArgumentOutOfRangeException`，`RequireFrame` 提前执行后不变。
- S7 / FINS 的两个 profile 用例同样核对：交付方式的用例都用**全新**的 `ChannelComponents`（`S7ProtocolProfileTests.cs:52`、`FinsProtocolProfileTests.cs:53`），预设组件的用例都用 `ChannelDelivery.Stream`（合法）→ 也都没有双重错误，把交付方式检查提到前面不会改变任何断言。
- 结论：**不需要停下汇报**，继续执行。

**测试先红后绿的证据**：

- 先只写 `PlcComponentWiringTests.cs` → `dotnet build Tests/Junevy.Communication.Plc.Tests` 报 **CS0103「当前上下文中不存在名称 PlcComponentWiring」**（两个目标）。
- 写生产代码后：260 通过 / 2 失败。两条都是**我测试写错了**——`Apply_WithoutAFrameCodecAndKeyExtractor_LeavesBothNull` 用了默认带分帧的辅助方法（改为直接传 `frameCodec: null`）；`Apply_ChecksTheAlwaysOwnedPropertiesInTheDocumentedOrder` 用 `Assert.Equal` 比整条消息，而 `ArgumentException.Message` 会追加 " (Parameter 'components')"（改为 `Assert.StartsWith`，`ParamName` 另有断言）。
- 最终 **262 通过 / 0 失败 / 0 跳过**（net8.0 与 net472 各一次），比基线的 245 多 **17** 条：三个参数异常（`owner` 用 Theory 覆盖 null / 空串 / 空白三种）、五项总是拥有的组件逐项预设、`Initializer` 的条件规则（两条）、守卫失败时零部分赋值（逐项断言 6 个属性都还是原值）、全部赋值正确、`frameCodec` / `keyExtractor` 为 null 时对应属性为 null、`ReconnectPolicy` 保留、返回同一实例、守卫顺序。

三个 profile 的迁移**没有新测试**，以"既有测试一个都没改且全绿"为证据：Melsec **184 + 1 跳过**、Siemens **316**、Omron **259**（两个目标各自相同），与基线逐条一致。

**第 2 节自查**：

- **公开 API 与第 8 节逐条一致**：`public static class` + 唯一的 `Apply<TComponents>(...)`，七个参数的名字、顺序与可空标注逐条一致，**不多不少**。
- **Q29 不留转发层**：三份 `RequireUnset` 与三份 `ThrowAlreadySet` 整个删除，profile 里一次都不出现。`git grep` 确认生产代码里已无这些符号。
- **Q30 协议无关**：`PlcComponentWiring.cs` 的代码与 XML 注释里**没有** MELSEC、S7、FINS 的术语；注释只说"协议"、"协议包"。协议特有的内容（`nameof(McProtocolProfile)` 这类 owner 名、帧类型规则）都留在各自 profile 里。
- **P45-D4 的语义逐条落实**：三个入口校验；五项总是拥有；`Initializer` 只在 `initializer` 非 null 时检查与赋值；检查顺序 `FrameCodec → Correlation → KeyExtractor → Initializer → HealthProbe → HealthProbeFactory`；**全部通过后才赋值**；`ReconnectPolicy` 不碰；返回同一实例。
- **职责划分**：`RequireUnset` 这个私有辅助收 `object?` + 属性名 + owner，因此**三种调用点形态**（`CorrelationMode?` 的装箱、`IFrameCodecFactory`、委托）都能复用同一份逻辑，不会为了不同类型写六个重载。
- **MELSEC 的两处变化（P45-D4 明示，写进 CHANGELOG 的"变更"）**：① 组件已设置时的消息去掉了 "for MELSEC"；② 双重错误的报告顺序反转，`RequireFrame` 先于 `Apply`。
- **`McProtocolProfile` 的 XML 注释保持准确**（P45-D6）：公开重载的签名与 `<typeparamref>` / `<param>` / `<returns>` 未动；`<exception>` 的三条按新顺序重排为"未定义的枚举 → 帧类型与介质不匹配 → 组件已设置"，并把原来的"（以太网只接受 3E、4E，串口只接受 4C）；或数据报使用 4C"拆开表述，语义逐条核对无误。
- **文件 ≤ 300 行 / 方法 ≤ 60 行**：三个 profile 从 58 / 58 / 89 行降到 **34 / 34 / 54 行**；`PlcComponentWiring.cs` 95 行，`Apply` 方法 42 行（含两段注释与空行），`methodlen.py 61` 对全部生产文件无输出。
- **依赖方向**：`PlcComponentWiring` 在 Plc 包里，只引用 `Junevy.Communication.Channels`；Melsec / Siemens / Omron 本来就依赖 Plc，只是多了 `using Junevy.Communication.Plc;`。**没有新增程序集引用**，架构测试 11 个用例全绿，IVT 仍为 2。
- **net472 禁用清单**：用了 `string.IsNullOrWhiteSpace` 与 `string.Format`，未用禁用清单里的任何一项；没有默认接口成员、`init`、`record`。
- **不留死代码**：删除的 6 个私有方法逐个确认无其他调用点；`ApplyCorrelation` 里的关联规则注释搬到了 `McProtocolProfile` 的调用点旁。

**第 3 节度量**：

| 指标 | Task 3 后 | Task 4 后 | 目标 |
|---|---|---|---|
| 重复窗口（6 行） | 45 | **34** | ≤ 34 ✅ |
| 其中跨协议窗口 | `FinsProfile ↔ S7Profile` 8、`Mc ↔ Fins ↔ S7` 3、`FinsFraming ↔ S7Framing` 1 | **只剩 `FinsFraming ↔ S7Framing` 1** | 只允许保留 1 ✅ |
| 新增窗口 | — | **0** | 0 ✅ |
| 超过 60 行的方法 | 0 | 0 | 0 ✅ |
| 超过 300 行的文件 | 9 | 9 | 9 ✅ |
| 生产程序集之间的 IVT | 2 | 2 | 2 ✅ |
| 有效行 | Plc 1189、Melsec 1275、Siemens 1286、Omron 707 | **Plc 1231（+42）、Melsec 1247（−28）、Siemens 1271（−15）、Omron 692（−15）** | Core、Plc 增，其余减 ✅ |

**三条命令的结果**：`dotnet build -c Debug` 0 错误；`dotnet build -c Release` 0 错误、31 个警告（同基线）；`dotnet test -c Debug` **23 个测试项目 / 目标组合全部通过，0 失败**——Core 157、Plc **262**、Channels 278、Tcp 129、Udp 38、Serial 63+2 跳过、Melsec 184+1 跳过、Architecture 11、WebApi 732 / 733、Modbus 405、Siemens 316、Omron 259。

**顺带发现并修正**：Plc README 新增的两个示例里，`ProtocolWiringExample.Configure` 用了 `ArgumentException` 却漏了 `using System;`；在临时项目里经 net8.0 与 net472 编译验证时（CS0246）发现并补上。README 的 5 个示例现在**逐个**经两个目标编译通过（用带占位类型的临时工程，占位类型不进仓库）。

#### Task 3：Core 的 `SequenceCounter` 与三个协议（P45-D3，F3）

**新增**：`Junevy.Communication.Core/Utils/SequenceCounter.cs`（20 行）、`Tests/Junevy.Communication.Core.Tests/SequenceCounterTests.cs`。
**修改**：`Siemens/Protocol/S7Message.cs`、`Siemens/S7Client.Hooks.cs`、`Siemens/S7Handshake.cs`、`Siemens/S7ReadProbe.cs`、`Omron/Protocol/FinsHead.cs`、`Omron/FinsClient.Hooks.cs`、`Omron/FinsReadProbe.cs`、`Melsec/Protocol/McFrameExchange.cs`；`Junevy.Communication.Core/README.md`（"Timeout Scope" 之后加 "Sequence Counter" 一节）；`CHANGELOG.md`。
**删除**：`Siemens/Protocol/S7PduReferences.cs`、`Omron/Protocol/FinsSessionIds.cs`、`Melsec/Protocol/McSerialNumbers.cs`（三个都是 `internal`，各 13–14 行）。

**测试先红后绿的证据**：先只写测试 → `dotnet build Tests/Junevy.Communication.Core.Tests` 报 **CS0246「未能找到类型或命名空间名 "SequenceCounter"」**（两个目标）。写生产代码后 156 通过 / 1 失败：`NextByte_FromConcurrentCallers_NeverRepeatsAValue` 期望 256、实际 255——**是我测试写错了**（8 × 32 = 256 次取值，序列是 1…256，256 截断成 0，所以最大字节是 255，不是 256）。改为断言"互不相同 256 个 + 最大值 255 + 恰好一个 0"，反而把"并发下正好绕回一次"也测到了。最终 **157 通过 / 0 失败 / 0 跳过**（net8.0 与 net472 各一次），比 Task 1 后多 10 条。

三个协议的迁移**没有新测试**，以"既有测试一个都没改且全绿"为证据：Melsec **184 + 1 跳过**、Siemens **316**、Omron **259**（两个目标各自相同），与基线逐条一致。

**第 2 节自查**：

- **公开 API 与第 7 节逐条一致**：`public sealed class` + `NextUInt16()` + `NextByte()`，**不多不少**；`counter` 字段 `private`。
- **算法与三份旧实现完全一致**：三份旧代码都是 `unchecked((ushort)Interlocked.Increment(ref counter))`，`SequenceCounter` 逐字沿用，只多一个 `(byte)` 版本的截断。因此**序列取值一字不差**（第一个 1、递增、到界回绕），`int` 溢出的问题与原来一样由 unchecked 截断处理。`int` 溢出后 `unchecked` 的截断结果与从 0 重新计数一致，已在字段注释里写明。
- **Q30 协议无关**：`SequenceCounter.cs` 的代码与 XML 注释里没有 MELSEC、S7、FINS 的术语（注释只说"协议报文中用来把应答与请求配好的字段"）；具体协议的位置与决策出处留在各自协议包里。
- **P45-D3 的"实例位置"**：跟着"谁写这个字段"走——`McFrameExchange`（`private static readonly SerialNumbers`，唯一使用者）、`S7Message`（`internal static readonly PduReferences`，报文头编码器）、`FinsHead`（`internal static readonly SessionIds`，头编码器）。三个都是**进程级单例**，因此"客户端请求与心跳探测共用"的原有语义不变。
- **XML 注释保留原决策出处**：三个字段的注释都保留了 P2-D11 / P4-D17 的出处，以及"请求与心跳共用、进程级、回绕不改变配对"的理由（FINS 的 8 位回绕说明也一并保留）。
- **Q29 不留转发层**：三个旧类型整个删除，5 个调用点直接改调 `S7Message.PduReferences` / `FinsHead.SessionIds` / `SerialNumbers`，没有薄包装。
- **依赖方向**：三个协议包本来就引用 Core，只多了 `using Junevy.Communication.Core.Utils;`；没有新增程序集引用，架构测试 11 个用例全绿，IVT 仍为 2。
- **文件 ≤ 300 行 / 方法 ≤ 60 行**：`methodlen.py 61` 对全部生产文件仍无输出；`SequenceCounter.cs` 只有 20 行。
- **net472 禁用清单**：`Parallel.For` 是 net472 上就有的 API，未使用禁用清单里的任何一项。
- **测试确定**：除并发用例外全是确定性断言；并发用例本身也是确定的——计数器的契约就是"每个并发调用拿到不同的值"，`Distinct()` 的计数是确定的，不依赖调度顺序（8 个任务 × 1000 个 16 位值不会回绕；8 × 32 个 8 位值只绕一次，断言也按此写死）。

**第 3 节度量**：重复窗口仍是 **45**（Task 2 后未变）——三份旧计数器每个只有 3 行有效代码（其余是注释与空行），扫描器剥掉后不足 6 行，本来就不计入窗口；新文件 20 行、内部 0 窗口。超过 60 行的方法 0；超过 300 行的文件 9。有效行：Core 1088 → **1095（+7）**、Siemens 1290 → **1286（−4）**、Omron 711 → **707（−4）**、Melsec 1275 → **1271（−4）**。

**三条命令的结果**：`dotnet build -c Debug` 0 错误 0 警告（增量）；`dotnet build -c Release` 0 错误、31 个警告（同基线，全部来自 Modbus 测试与 WPF 临时工程）；`dotnet test -c Debug` **23 个测试项目 / 目标组合全部通过，0 失败**——Core 157、Plc 245、Channels 278、Tcp 129、Udp 38、Serial 63+2 跳过、Melsec 184+1 跳过、Architecture 11、WebApi 732 / 733、Modbus 405、Siemens 316、Omron 259。除 Core 外与 Task 2 完全一致。

#### Task 2：S7 与 FINS 改用 `BigEndianFields`（F8）

**新增**：无。
**修改**：`Siemens/Protocol/` 的 `CotpFrames.cs`、`S7Message.cs`、`S7VarItem.cs`、`S7SetupCommunication.cs`、`S7StringFormat.cs`、`S7WriteVar.cs`；`Omron/Protocol/` 的 `FinsTcpFrame.cs`、`FinsHead.cs`、`FinsMemoryArea.cs`、`FinsNodeAddress.cs`；两个测试替身 `Tests/Junevy.Communication.Siemens.Tests/TestSupport/S7DeviceSimulator.cs`、`Tests/Junevy.Communication.Omron.Tests/TestSupport/FinsDeviceSimulator.cs`（P45-D8 允许）；`CHANGELOG.md`。
**删除**：`Junevy.Communication.Siemens/Protocol/S7Fields.cs`（133 行）、`Junevy.Communication.Omron/Protocol/FinsFields.cs`（106 行）。

**测试先红后绿的证据**：**纯迁移，没有新测试**。证据是"既有测试断言一个都没改且全绿"——`git status` 在本 Task 涉及的测试项目里只有两个 `TestSupport/` 替身文件，`dotnet test` 的 Siemens **316 / 316**、Omron **259 / 259**（net8.0 与 net472 各一次）与基线逐条相同；S7 与 FINS 的逐字节黄金向量用例（`V-S7-*`、`V-F-*`）原样通过，这就是**线上字节不变**（Q32）的直接证据。

**第 2 节自查**：

- **Q29 不留转发层**：两个旧类型整个删除，91 处 `S7Fields.*` 与 40 处 `FinsFields.*` 直接改调 `BigEndianFields`，没有薄包装。改名映射只有两个：`WriteRaw` → `WriteBytes`、`TryTakeRaw` → `TryTakeBytes`；宽度常量四个同名，无需改动。
- **依赖方向**：两个协议包本来就引用 Core；新增的只是 `using Junevy.Communication.Core.Buffers;`（`S7VarItem.cs` 与 `FinsHead.cs` 原本没有，因为它们用的 `IBufferWriter<byte>` 在 `System.Buffers` 里）。**没有引入任何新的程序集引用**，架构测试 11 个用例照常全绿，IVT 仍为 2。
- **行为不变**：`S7Fields` 手写的 `value >> 8` / `value >> 16` 与 `FinsFields` 用的 `BinaryPrimitives` 产出**完全相同的字节**，`BigEndianFields` 统一走 `BinaryPrimitives`（2 / 4 字节）与手写移位（3 字节，`UInt24` 没有 `BinaryPrimitives` 版本）。黄金向量全绿证明这一点。
- **读取约定不变**：两个旧类型的 `TryTake*` 本来就是"字节不足返回 false、`value` 取默认值、不移动 `data`"，`BigEndianFields` 逐字沿用。
- **无死代码**：随两个文件一起删掉的常量、成员全部有对应的新类型成员；`git grep` 确认生产与测试代码里已无 `S7Fields` / `FinsFields`（剩下的命中只在历史计划文档里）。
- **文件 ≤ 300 行 / 方法 ≤ 60 行**：两个被删的文件本身是删除不新增；`methodlen.py 61` 对全部生产文件仍无输出。
- **Q30**：`BigEndianFields` 里不含协议术语（见 Task 1 报告）；本 Task 也没有把旧文件里的协议注释搬回协议代码里——只有 `S7VarItem.cs:86` 的地址字段补了两行注释，保留**原来**在 `S7Fields.WriteUInt24` 注释里的那条依据（见下面第 3 条）。

**计划第 6 节 Task 2 第 3 条要求的确认（`WriteUInt24` 的取值范围）**：

- 唯一调用点是 `S7VarItem.WriteParameters` 的地址字段：`byteAddress * BitsPerByte + bitIndex`。
- `byteAddress` 来自 `address.Number`，`S7AddressParser` 限 0–2097151；`bitIndex` 来自 `address.BitIndex ?? 0`，限 0–7（P4-D16）。
- `S7Client.Hooks.ValidateRange` 在每次请求前算出**最后一个被触及的字节号**（字：`Number + 2 × count − 1`；字节：`Number + count − 1`；位：`(8 × Number + BitIndex + count − 1) / 8`），并与 `address.Device.MaxNumber`（= `0xFFFFFF / 8` = 2097151）比较，超出即返回 `InvalidRequest`（`The access on '…' ends at byte N, beyond the address field limit 2097151.`）。
- 因此任何走到编码器的请求满足 `byteAddress × 8 + bitIndex ≤ 2097151 × 8 + 7 = 0xFFFFFF`——**恰好是 `UInt24` 的上限**，运行时不会触发新的越界校验。
- 已在调用点写上这两行注释（**不是**再加一层校验，符合第 6 节第 3 条），把这个"上限由谁保证"的依据从被删文件里保住。

**第 3 节度量**：

| 指标 | Task 1 后 | Task 2 后 |
|---|---|---|
| 重复窗口（6 行） | 54 | **45**（−9，`FinsFields ↔ S7Fields` 集群消失，**新增窗口 0**） |
| 其中 `BigEndianFields.cs` 文件内部 | 0 | **0** |
| 超过 60 行的方法 | 0 | 0 |
| 超过 300 行的文件 | 9 | 9 |
| 有效行 | Siemens 1367、Omron 772 | **Siemens 1290（−77）、Omron 711（−61）** |

**度量口径的一处修正**（记在 14.2，不是偏差）：第一次跑 `dupscan` 时我把 `BigEndianFields.cs` 手工追加进了 `git ls-files` 的结果，该文件因此被扫描两次，输出里出现一条假的 `70 windows … BigEndianFields.cs`。改用纯 `git ls-files`（344 个文件 = 345 − 2 删除 + 1 新增）后该集群消失。Task 1 报告里"`BigEndianFields` 未产生新的重复窗口"这句话当时**没有实测**，现已实测确认。

**三条命令的结果**：`dotnet build -c Debug` 0 错误；`dotnet build -c Release` 0 错误、31 个警告（全部来自 `Tests/Junevy.Communication.Modbus.Tests` 与 `Junevy.Communication.Test` WPF 临时工程，与基线相同）；`dotnet test -c Debug` **24 个测试项目 / 目标组合全部通过，0 失败**（Core 147、Plc 245、Channels 278、Tcp 129、Udp 38、Serial 63+2 跳过、Melsec 184+1 跳过、Architecture 11、WebApi 732/733、Modbus 405、Siemens 316、Omron 259），与 Task 1 完全一致。

#### Task 1：Core 的 `BigEndianFields`（P45-D2）

**新增**：`Junevy.Communication.Core/Buffers/BigEndianFields.cs`（165 行）、`Tests/Junevy.Communication.Core.Tests/BigEndianFieldsTests.cs`。
**修改**：`Junevy.Communication.Core/README.md`（"Buffers" 一节加一段用法与说明）、`CHANGELOG.md`（"新增"）。
**删除**：无。

**测试先红后绿的证据**：

- 先只写测试文件 → `dotnet build Tests/Junevy.Communication.Core.Tests` 报 **CS0103「当前上下文中不存在名称 BigEndianFields」**（两个目标都报），即新代码阶段无法编译（符合第 1 节第 2 条）。
- 写生产代码后再跑：先绿了，但 `WriteBytes_CopiesTheBytesVerbatim(value: [])` **失败**（`ArgumentOutOfRangeException: The initial capacity must be greater than zero.`）——这是测试辅助方法把 0 当成 `ByteBufferWriter` 的初始容量，改为 `Math.Max(1, minimumCapacity)` 后通过。
- 最终：`dotnet test Tests/Junevy.Communication.Core.Tests` → **147 通过 / 0 失败 / 0 跳过**（net8.0 与 net472 各一次），比基线的 116 多 31 条。

**第 2 节自查**：

- **文件 ≤ 300 行**：165 行。**方法 ≤ 60 行**：最长的是类级 XML 注释后的 `WriteUInt24`（8 行），`methodlen.py 61` 无输出。
- **不重复**：`WriteUInt8` 的"取 span、写、Advance"三步在四个写入方法里重复出现，但宽度、写入原语（`BinaryPrimitives` / 手写移位）与前置校验都不同；`WriteUInt16` / `WriteUInt32` 只有两行，抽辅助方法反而增加间接层。`TryTake*` 的"长度不足 → 取默认值 → 返回 false"是本类型的核心约定，五处各 5 行，形状相同但类型不同（`byte` / `ushort` / `int` / `uint` / `ReadOnlySpan<byte>`），合并需要装箱或委托，不值得。判断：可接受。
- **依赖方向**：只依赖 `System.Buffers` 与 `System.Buffers.Binary`，Core 不引用任何上层。**可见性最小化**：`public` 成员与第 5 节的固定清单逐条一致，**不多不少**（4 个宽度常量 + 5 个写入 + 5 个读取）。
- **无魔法数**：`0` / `0xFFFFFF` / `16` / `8` 提成 `UInt24Min` / `UInt24Max` 与 `UInt8Bytes` 等常量；`UInt24` 的移位量是位宽的固有值。
- **无死代码**：每个成员都有测试覆盖。
- **错误走结果**：字节不足返回 `false`（解码器据此走 `ProtocolViolation` 之类的结果），不抛异常；只有**编程错误**（null、越界、负长度）抛异常，符合第 2 节"入口校验"与 P45-D2。
- **测试确定**：全部是纯函数 + 字节数组比较，无时间、无随机、无 I/O。黄金向量 `0x1234` / `0x123456` / `0x12345678` 与 S7、FINS 的线上字节序一致（高位在前）。
- **不新增生产程序集之间的 IVT**：Core 内部使用，没有新增任何 `[assembly: InternalsVisibleTo]`。
- **公开 API 只增加计划列出的成员**：第 1 节第 7 条与第 5 节代码块逐条核对通过。
- **Q30 Core 与 Plc 的新类型协议无关**：`BigEndianFields.cs` 的代码与 XML 注释里**没有** S7、FINS、MELSEC 的术语（原 `S7Fields` 注释里的"S7 的多字节字段""S7 数据项的地址字段"、`FinsFields` 注释里的"FINS/TCP 的长度"等全部没有搬过来）；协议举例只出现在 README（"a protocol header"）。
- **抽取后删除旧代码**：本 Task 只做 Core 侧的新增，旧代码在 Task 2 删除（计划已如此安排）。
- **net472 禁用清单**：未用默认接口成员、`Enumerable.Append`、`SequenceReader<T>`、`Task.WaitAsync`、`ArgumentNullException.ThrowIfNull`、Index/Range、`init` 与 `record`。`BinaryPrimitives` 在 net472 由 `System.Memory` 提供（既有 `ByteTransform` 已经这么用）。
- **代码风格**：文件作用域命名空间；英文异常消息、中文 XML 注释；每条公开成员都有 XML 注释与 `<exception>`。

**第 3 节度量**：Core 有效行 979 → **1088**（+109，`effective_lines.py` 去掉空行与 `//` 注释后的口径；文件本身 165 行）；新增文件未产生新的重复窗口（`BigEndianFields` 的方法体与将被删除的 `S7Fields` / `FinsFields` 相似，Task 2 后自然消失）；超过 60 行的方法仍为 0。

**三条命令的结果**：`dotnet build -c Debug` 0 错误、`dotnet build -c Release` 0 错误、`dotnet test -c Debug` **24 个测试项目 / 目标组合全部通过，0 失败**（Core 147+147、Plc 245+245、Channels 278+278、Tcp 129+129、Udp 38+38、Serial 63+2 跳过 ×2、Melsec 184+1 跳过 ×2、Architecture 11+11、WebApi 732 / 733、Modbus 405、Siemens 316+316、Omron 259+259）。除 Core 外的项目计数与基线逐项一致。

**顺带发现并修正**：Core README 的 `HeaderExample.TryReadAddress` 初版在提前返回的分支没有给 `out address` 赋值，`dotnet build` 报 CS0177；已在临时项目里经 net8.0 与 net472 编译验证后改正（Task 6 的 Skill 示例也要走同样的验证）。

#### Task 0：基线

- **新增、修改、删除的文件**：无（只在本计划文件内记录基线）。
- **测试先红后绿的证据**：不适用（纯记录）。
- **第 2 节自查**：本 Task 不涉及生产代码。
- **第 3 节度量**：见 14.3，与第 3 节表格的基线列逐项一致。
- **三条命令的结果**：见 14.3 的测试表，与第 3 节表格的基线列逐项一致；无失败、无偶发失败（`HeartbeatTests.OnlyWhenIdle_SkipsWhenTraffic` 本次通过）。
- **P45-D5 前提复核（Task 0 第 2 条）**：
  - `grep -rnE "CommResult<[^>]+\?>"` 在生产与测试代码中**无结果**——`T` 没有可空引用类型的用法，`CommResult<T>.Success` 的 null 守卫不会与现有调用冲突。
  - `grep -rn "Success(null|Success(default"` 只有 **WebApi** 的 5 处（`Junevy.Communication.WebApi/Rest/RestResponseDecoder.cs:66,93`、`Soap/SoapResponseDecoder.cs:26,37` 与 `Tests/Junevy.Communication.WebApi.Tests/WebApiResultTests.cs:27`），命中的是 `WebApiResult<T>.Success`，**不是** `CommResult<T>.Success`，本期不涉及（P45-D1 明确把 `WebApiResult<T>` 的同类标注排除在外）。
  - 结论：`CommResult<T>.Success` 加 null 守卫是安全的，写入 CHANGELOG 的"破坏性变更"。

### 14.3 基线与度量（执行者填写）

#### 基线（Task 0 实测，`d306543` + 计划文件提交 `b8ed387`）

三条命令全部成功：`dotnet build -c Debug` 与 `-c Release` 均 **0 错误**（31 个警告，全部来自 `Tests/Junevy.Communication.Modbus.Tests` 的 CS8600/CS8625/xUnit1031 与 `Junevy.Communication.Test` WPF 临时工程的 CS8618，不在本期范围）；`dotnet test -c Debug` **全部通过**。

| 测试项目 | net8.0 | net472 |
|---|---|---|
| Core | 116 通过 | 116 通过 |
| Channels | 278 通过 | 278 通过 |
| Tcp | 129 通过 | 129 通过 |
| Udp | 38 通过 | 38 通过 |
| Serial | 63 通过 + 2 跳过 | 63 通过 + 2 跳过 |
| Plc | 245 通过 | 245 通过 |
| Melsec | 184 通过 + 1 跳过 | 184 通过 + 1 跳过 |
| Architecture | 11 通过 | 11 通过 |
| WebApi | 732 通过 | 733 通过 |
| Modbus | 405 通过 | （只有 net8.0） |
| Siemens | 316 通过 | 316 通过 |
| Omron | 259 通过 | 259 通过 |

与第 3 节表格的基线列逐项一致。

| 指标 | 实测 | 第 3 节基线 |
|---|---|---|
| 重复窗口（6 行） | 54 | 54 |
| 超过 60 行的方法 | 0 | 0 |
| 超过 300 行的文件 | 9（`Channels/Lifecycle/ConnectionSupervisor.cs` 691、`Channels/Pipeline/StreamChannel.cs` 621、`Channels/Pipeline/PendingRequestTable.cs` 537、`Core/Registry/NamedRegistry.cs` 461、`Tcp/Server/TcpServer.cs` 449、`Tcp/Client/TcpClientChannel.cs` 372、`Channels/Pipeline/FrameRouter.cs` 368、`Channels/Pipeline/DatagramChannel.cs` 352、`Channels/Lifecycle/StreamConnectionDriver.cs` 314） | 9 |
| 生产程序集之间的 IVT | 2（Architecture 11 个用例全绿） | 2 |
| 有效行 | Core 979、Plc 1189、Melsec 1275、Siemens 1367、Omron 772 | 一致 |

**重复窗口的逐集群分布（54 = 各集群之和）**：

| 窗口数 | 集群 | 处置 |
|---|---|---|
| 9 | `Omron/Protocol/FinsFields.cs ↔ Siemens/Protocol/S7Fields.cs` | Task 2 消除 |
| 9 | `WebApi/Rest/WebApiClient.cs ↔ WebApi/Soap/SoapClient.cs` | 范围外（P45-D1 只处理三个 PLC 协议） |
| 8 | `Omron/FinsProtocolProfile.cs ↔ Siemens/S7ProtocolProfile.cs` | Task 4 消除 |
| 7 | `Serial/SerialChannel.cs ↔ Tcp/Client/TcpClientChannel.cs` | 范围外（P45-D7：Channels、Tcp、Udp 生产代码只允许 Task 5 的 `!` 清理） |
| 6 | `Channels/Pipeline/DatagramChannel.cs ↔ Channels/Pipeline/StreamChannel.cs` | 范围外 |
| 5 | `Channels/Lifecycle/DatagramConnectionDriver.cs ↔ StreamConnectionDriver.cs` | 范围外 |
| 3 | `Channels/Lifecycle/StreamClientChannel.cs ↔ Udp/UdpChannel.cs` | 范围外 |
| 3 | `Melsec/McProtocolProfile.cs ↔ Omron/FinsProtocolProfile.cs ↔ Siemens/S7ProtocolProfile.cs` | Task 4 消除 |
| 1 | `Core/Registry/NamedRegistry.cs`（文件内部） | 范围外 |
| 1 | `Omron/Framing/FinsFraming.cs ↔ Siemens/Framing/S7Framing.cs` | **接受保留**（P45-D1） |
| 1 | `Tcp/Client/TcpClientChannelCreator.cs ↔ Udp/UdpChannelCreator.cs` | 范围外 |
| 1 | `Tcp/Client/TcpConnector.cs ↔ Udp/UdpDatagramTransport.cs` | 范围外 |

合计 54。Task 2 与 Task 4 合计消除 9 + 8 + 3 = **20**，目标 54 − 20 = **34**，与第 3 节的目标一致。

#### 最终度量（Task 7 复测）

提交 `dbc3622`（Task 6）之后、生产文件 343 个（基线 345 = 345 − 5 个已删除 + 3 个新增）。

| 指标 | 基线（`d306543`） | P4.5 目标 | 实测 | 判定 |
|---|---|---|---|---|
| 重复窗口（6 行） | 54 | ≤ 34，新增窗口 0 | **34** | ✅ |
| 超过 60 行的方法 | 0 | 0 | **0** | ✅ |
| 超过 300 行的文件 | 9 | 9 | **9**（清单与基线逐条相同） | ✅ |
| 生产程序集之间的 IVT | 2 | 2 | **2**（Architecture 11 用例全绿） | ✅ |
| 有效行：Core | 979 | 增 | **1103（+124）** | ✅ |
| 有效行：Plc | 1189 | 增 | **1231（+42）** | ✅ |
| 有效行：Melsec | 1275 | 减 | **1247（−28）** | ✅ |
| 有效行：Siemens | 1367 | 减 | **1271（−96）** | ✅ |
| 有效行：Omron | 772 | 减 | **692（−80）** | ✅ |

**重复窗口逐集群（34 = 各集群之和）**：

| 窗口数 | 集群 | 与基线相比 |
|---|---|---|
| 9 | `WebApi/Rest/WebApiClient.cs ↔ WebApi/Soap/SoapClient.cs` | 不变（范围外） |
| 7 | `Serial/SerialChannel.cs ↔ Tcp/Client/TcpClientChannel.cs` | 不变（P45-D7：只允许 `!` 清理） |
| 6 | `Channels/Pipeline/DatagramChannel.cs ↔ StreamChannel.cs` | 不变 |
| 5 | `Channels/Lifecycle/DatagramConnectionDriver.cs ↔ StreamConnectionDriver.cs` | 不变 |
| 3 | `Channels/Lifecycle/StreamClientChannel.cs ↔ Udp/UdpChannel.cs` | 不变 |
| 1 | `Core/Registry/NamedRegistry.cs`（文件内部） | 不变 |
| 1 | `Omron/Framing/FinsFraming.cs ↔ Siemens/Framing/S7Framing.cs` | 不变——**本期唯一保留的跨协议窗口**（P45-D1 接受） |
| 1 | `Tcp/Client/TcpClientChannelCreator.cs ↔ Udp/UdpChannelCreator.cs` | 不变 |
| 1 | `Tcp/Client/TcpConnector.cs ↔ Udp/UdpDatagramTransport.cs` | 不变 |
| — | `Omron/Protocol/FinsFields.cs ↔ Siemens/Protocol/S7Fields.cs` | **9 → 0**（Task 2） |
| — | `Omron/FinsProtocolProfile.cs ↔ Siemens/S7ProtocolProfile.cs` | **8 → 0**（Task 4） |
| — | `Melsec/McProtocolProfile.cs ↔ Omron/… ↔ Siemens/…` | **3 → 0**（Task 4） |

消除 20 个（9 + 8 + 3），**新增窗口 0**，因此 54 − 20 = **34**，正好达到第 3 节的目标。

**三条命令连续运行三次**（每次都跑完整的三条；`dotnet build -c Debug`、`-c Release`、`dotnet test -c Debug`）：

- 6 次构建全部 **0 错误**（Release 仍是 31 条既有警告，全部来自 `Tests/Junevy.Communication.Modbus.Tests` 的 CS8600/CS8625/xUnit1031 与 `Junevy.Communication.Test` WPF 临时工程的 CS8618，与基线相同）。
- 3 次测试各 **23 个项目 / 目标组合，0 失败**；按"项目 + 目标 + 失败 / 通过 / 跳过 / 总数"逐项比对，**三次完全一致**（`Compare-Object` 差异数 0）。

| 项目 | net8.0 | net472 |
|---|---|---|
| Core | **160** 通过 | **160** 通过 |
| Channels | 278 通过 | 278 通过 |
| Tcp | 129 通过 | 129 通过 |
| Udp | 38 通过 | 38 通过 |
| Serial | 63 通过 + 2 跳过 | 63 通过 + 2 跳过 |
| Plc | **262** 通过 | **262** 通过 |
| Melsec | 184 通过 + 1 跳过 | 184 通过 + 1 跳过 |
| Architecture | 11 通过 | 11 通过 |
| WebApi | 732 通过 | 733 通过 |
| Modbus | 405 通过 | （只有 net8.0） |
| Siemens | 316 通过 | 316 通过 |
| Omron | 259 通过 | 259 通过 |

对照基线：**只有 Core（116 → 160，+44）与 Plc（245 → 262，+17）增加**，其余 10 个项目一字未变。Core 的 +44 = `BigEndianFields` 31 + `SequenceCounter` 10 + `CommResult` 3；Plc 的 +17 全部来自 `PlcComponentWiringTests`。**S7、FINS、MELSEC 一条用例都没删、断言一个字都没改**（P45-D8 / Q28′），因此第 3 节"原则上不应发生"的删测试情形没有发生，无需逐条说明。

**"新增一个协议"现在少写什么**（对照 P4 计划第 20.2 节扩展性结论的第三部分）：

- **不用写**大端字段工具——`BigEndianFields` 的 14 个成员覆盖 1/2/3/4 字节的写与读，以及原样字节；原先 `S7Fields` 133 行 + `FinsFields` 106 行，计 239 行，现在是一行调用。
- **不用写**进程级序号计数器——`SequenceCounter` 两个方法；原先三份 13–14 行，计 40 行，现在是在报文头编码器上放一个字段 + 一行 `NextUInt16()` / `NextByte()`。
- **不用写**组件守卫与六项接线——`PlcComponentWiring.Apply` 一次调用；原先三份 `RequireUnset` / `ThrowAlreadySet` 与 MELSEC 额外的 `RequireComponents` / `ApplyCorrelation`，计 8 个方法。
- **仍然要写**：设备表与地址类型、协议钩子与帧编解码（外加黄金向量测试）、握手与会话快照、心跳探测、客户端工厂与 DI、以及测试侧的设备替身与端到端脚手架（F7、F10，转入 P5）。

净效果：三个协议包的有效行从 3414（1275 + 1367 + 772）降到 **3210（−204，−6.0%）**，而套件的"新增协议"能力没有缩水——省下的正是三份会无限复制的样板。
