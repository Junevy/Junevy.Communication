# P2 实施计划：Plc 地址型设备协议套件 + MELSEC MC 协议

- 日期：2026-10-11；分支：`feat/plc-melsec-p2`（已从 master 建好）
- 依据：设计文档 `docs/superpowers/specs/2026-10-10-communication-core-design.md` 第 3、4、12、13 节；三菱官方手册 *MELSEC Communication Protocol Reference Manual* SH(NA)-080008-AB（2022 年 5 月）。手册全文已抽取为文本：`D:\AppTemp\claude\D--Desktop-doc-Github-Repository-Junevy-Communication\479e70ed-fbe3-4aeb-8ee8-516a219d8e85\scratchpad\mcref\sh080008.txt`（下文"手册 L1234"指该文件行号，"手册 p.88"指手册页码）。
- 本期目标有两个：交付可用的 Plc 套件与 MELSEC 客户端；**验证扩展性**——MELSEC 只写协议差异，不修改 Core / Channels / Plc（设计 12.1）。
- 验收重点：功能完整 + **代码质量**（第 2 节逐条检查）。

---

## 0. 已固定的设计决策（不同意的条目先在此表修改再执行）

| # | 决策 | 理由 |
|---|---|---|
| P2-D1 | 新增两个库 `Junevy.Communication.Plc`（依赖 Channels）、`Junevy.Communication.Melsec`（依赖 Plc + Tcp + Udp + Serial），两个测试项目 `Tests/Junevy.Communication.Plc.Tests`、`Tests/Junevy.Communication.Melsec.Tests`；目标框架、版本、公共设置沿用 `build/Junevy.Communication.Common.props`（1.0.0-preview.1，net472;net8.0） | 设计 3.1 |
| P2-D2 | MELSEC 范围（用户确认）：3E / 4E 帧 × 二进制 / ASCII，TCP 与 UDP；串口 4C 帧格式 5（二进制）与格式 4（ASCII），可选和校验；命令只做 0401 批量读、1401 批量写（字单位与位单位）、0619 回环测试；软元件指定只做 Q/L 兼容（子命令 0000 / 0001，1 字节软元件代码 + 3 字节编号） | 用户 2026-10-11 决定 |
| P2-D3 | Core 新增 `Buffers/`：`WordOrder`（ABCD / BADC / CDAB / DCBA）与 `ByteTransform`。P1 未实现，设计 3.2、4 节列为 Core 内容。`RetryLoop` 不加（Plc 是唯一使用者，重试写在 Plc 管线内；以后有第二个使用者再提到 Core） | 设计 4 节；YAGNI |
| P2-D4 | `PlcAddressUnit` 只有 `Bit`、`Word`。`Byte`（S7 用）留到 P4 随 S7 加入（新增枚举值是非破坏性的） | 本期没有字节地址的使用者，加了就是无人测试的分支 |
| P2-D5 | `ReadAsync` / `WriteAsync` 的原始字节是**协议规范化布局**：与编码方式（二进制 / ASCII）无关，由协议定义。MELSEC：每个字 2 字节小端，字按地址递增排列（ASCII 响应的十六进制文本由协议转换成同样的布局） | 类型化转换只需一种布局；手册 p.73、p.77 |
| P2-D6 | 每个 `PlcClientBase` 实例的所有公开操作经同一把**操作锁**串行执行（含拆包的全部分包与读-改-写）。4E 的序列号用于丢弃迟到 / 错配的应答（Keyed 关联），不用于同一客户端内并发 | PLC 侧不存在事务，拆包读本来就不是原子的；锁只保证本客户端内的顺序与读-改-写不被自己的其他写入打断，行为与 Modbus 主站一致、简单可预测 |
| P2-D7 | 重试**按单个请求**进行（拆包时只重试失败的那一包），规则沿用设计 12.5：开始时未连接 → 立即 `NotConnected`；`RemoteError` / `InvalidRequest` / `NotSupported` / `Cancelled` / `NotConnected` 不重试；`Timeout` / `ConnectionClosed` / `ProtocolViolation` 在 `RetryCount > 0` 且 `RetryInterval` 内 `WaitForConnectedAsync` 成功时重试，否则返回**真实**的失败 | 设计 12.5 |
| P2-D8 | 类型化写入用**显式类型名**：`WriteInt16Async`、`WriteInt32Async`、`WriteFloatAsync`……，不用 `WriteAsync(string, short)` 这类重载 | 字面量 `5` 会绑定到 `int` 写两个字，PLC 现场这是静默的数据破坏；显式名字没有这个陷阱（设计 12.3 示例为重载，此处修订） |
| P2-D9 | 本期不提供按调用覆盖数据格式。`IPlcClient.DataFormat` 由协议选项决定（MELSEC：`McClientOptions.DataFormat`，默认 DCBA + ASCII）；混合格式用 `ReadAsync` 取原始字节 + `ByteTransform` | 每个类型化方法再加一个格式参数会让重载翻倍；需要时可以非破坏性地加 |
| P2-D10 | 字中的位（`D100.5`、MELSEC 写作 `D100.5` 或 `D100.F`，位号为十六进制 0–F）：读由套件实现（读字取位）；写默认 `NotSupported`，`PlcClientOptions.AllowReadModifyWriteBits = true` 时在操作锁内读-改-写并每次记录 Warning | 设计 12.6 |
| P2-D11 | 4E 序列号来自进程级单调计数器（internal static，`Interlocked.Increment` 取低 16 位）。客户端请求与心跳探测共用，同一通道上不会撞号 | 探测由通道通过 `HealthProbeFactory` 创建，拿不到客户端实例；共享计数器是最简单的正确做法 |
| P2-D12 | 结束码 / 错误码**不做说明表**：失败结果为 `RemoteError`，`ProtocolErrorCode` 携带原始码，消息为 `MC end code 0xC051.` 这类固定格式 | 手册 p.44 明确说明错误码含义在各模块手册中，本手册没有；编造说明表会误导现场排查（设计 12.8 的 McErrorCodes.cs 取消） |
| P2-D13 | `PlcClientBase` 拥有通道：`Dispose` / `DisposeAsync` 释放通道。释放后的操作返回 `NotConnected`（消息 `The PLC client has been disposed.`） | 单一所有权，避免使用者忘记释放 |
| P2-D14 | 运行期操作不抛异常：非法地址、参数越界、空数据返回 `InvalidRequest`；用户取消返回 `Cancelled`。只有构造时的配置错误抛 `ArgumentException` 族（沿用 P1 D5） | 与 P1 通道一致 |
| P2-D15 | `McClientOptions.MonitoringTimer` 默认 `0x0004`（1 s），小于通道默认请求超时 2000 ms：PLC 处理超时会先返回结束码（`RemoteError`），而不是让客户端先超时 | 手册 p.43 建议本站 1–40（0.25–10 s） |
| P2-D16 | 已知不确定项（手册未给出示例，按图示推断，代码注释与 README 写明"未经真机验证"）：① 4C 格式 4 响应的和校验范围**包含 ETX**（手册 p.37 图示：格式 1–4 的范围从首个控制码之后到和校验码之前；格式 5 图示明确排除 DLE ETX）；② 4C 格式 5 异常响应的 2 字节错误码按小端解析（与其他 2 字节数值一致） | 诚实记录，留待真机验证 |

---

## 1. 执行规则（每个 Task 都适用）

1. 每个 Task 一个 commit（提交信息见各 Task），在 `feat/plc-melsec-p2` 上提交。不 push、不 amend、不 rebase、不切换分支。
2. 步骤顺序固定：**先写测试并确认失败**（新代码阶段无法编译也算失败）→ 写生产代码 → 测试通过 → **按第 2 节自查并清理** → 更新文档 → 提交。
3. 每个 Task 完成后依次运行，全部成功才能提交：
   ```bash
   dotnet build Junevy.Communication.slnx -c Debug
   dotnet build Junevy.Communication.slnx -c Release
   dotnet test Junevy.Communication.slnx -c Debug
   ```
   新测试项目在 net8.0 与 net472 上各运行一次；Modbus 405 个测试与 P1 的全部测试必须保持全绿。
4. P1 计划第 1 节第 4、5 条的代码约束与套接字测试规则全部沿用（只异步 API、`ConfigureAwait(false)`、net472 禁用清单、文件作用域命名空间、英文错误消息与日志、中文 XML 注释、Debug 才生成十六进制日志、`SocketTiming` 集合、耗时断言区间）。
5. CHANGELOG：在 `CHANGELOG.md` 顶部新增一节 `## [Unreleased] — PLC 套件 P2（Plc / Melsec 1.0.0-preview.1；Core 新增 Buffers）`，每个 Task 的条目写入该节，格式 `- YYYY-MM-DD [包名] 内容`。
6. 不修改：`Junevy.Communication.Modbus/`、`Tests/Junevy.Communication.Modbus.Tests/`、`Skills/using-junevy-modbus/`。Channels / Tcp / Udp / Serial 的**生产代码**本期不改；如果 MELSEC 需要它们没有的能力，**不要绕过**，按第 1 节第 8 条记录后停下来在汇报中说明。
7. 知识库：只在 Task 13 回写，**不提交**；汇报时附 `git status --short -- Junevy.Communication.Wiki` 与 `git diff --stat -- Junevy.Communication.Wiki`。
8. **扩展性摩擦记录**（本期核心产出之一）：从 Task 8 开始，每当 MELSEC 需要 Core / Channels / Plc 里没有的东西、或者不得不写变通代码，立即在本文件第 20.2 节追加一行（现象、变通或缺口、建议的通用能力）。Task 8 之后对 Core / Channels / Plc 的任何修改都必须对应其中一行。
9. 本计划之外的问题记入第 19 节，只记录不修改。
10. 每个 Task 的汇报包含：新增 / 修改文件、测试先红后绿的证据、第 2 节自查结果（逐条"符合 / 不符合 + 说明"）、三条命令的结果。

---

## 2. 代码质量标准（审阅者逐条验收，不符合即打回）

**结构**
- Q1 单一职责：一个类型只做一件事。文件不超过 300 行（含注释），方法不超过 60 行；超过就拆分，不要用 region 掩盖。
- Q2 不重复：相同逻辑只写一次。特别是：3E 与 4E 共用一套以太网帧代码（4E 只多序列号）；二进制与 ASCII 共用同一份"字段顺序"代码，差异只在字段写入 / 读取策略；8 种数值类型的类型化读写共用一个泛型辅助方法；4C 格式 4 与格式 5 共用访问路径与请求数据代码。审阅者会用相似度检查找重复块。
- Q3 依赖方向：Melsec → Plc → Channels → Core。Plc 不引用任何传输包；Melsec 内部的协议层（帧编解码）不依赖通道类型，只处理字节，便于单测。
- Q4 可见性最小化：只有第 0 节与各 Task "公开 API（固定）"列出的类型和成员是 `public`，其余一律 `internal`（或 `private`）。新增公开成员需要先在汇报中说明理由。

**可读性**
- Q5 没有魔法数：命令码、子命令、副头部、控制码、偏移量、上限全部用具名常量，常量旁注明手册页码。
- Q6 命名与周边一致：MELSEC 类型用 `Mc` 前缀，套件类型用 `Plc` 前缀；异步方法以 `Async` 结尾；参数与字段命名沿用 P1 风格。
- Q7 注释：公开成员必须有中文 XML 注释（Common.props 已把缺失注释当错误）；内部代码只在"为什么"不明显时写注释，不写复述代码的注释。注释密度与 P1 代码相当。
- Q8 没有死代码：没有注释掉的代码、未使用的参数 / 字段 / using / 私有方法、只为测试存在的生产代码分支、`TODO`（确需保留的待办写进第 19 节）。

**正确性习惯**
- Q9 异步：库内每个 `await` 都有 `ConfigureAwait(false)`；不出现 `.Result`、`.Wait()`、`GetAwaiter().GetResult()`、`async void`。
- Q10 错误处理：运行期失败一律走 `CommResult`（P2-D14）；不吞异常——捕获后要么转换成带原因的结果，要么记录日志；`catch (Exception)` 只出现在边界（钩子调用、工厂调用）并说明原因。
- Q11 资源：拥有的 `IDisposable` 都释放；`SemaphoreSlim`、`CancellationTokenSource` 有明确的生命周期。
- Q12 输入校验在入口集中做一次，内部代码不重复校验同一个条件。

**测试**
- Q13 每个公开行为至少一个测试；协议编解码必须用第 18 节的黄金向量（来自手册的字节），不能只用"自己编码再自己解码"的往返测试。
- Q14 测试确定：不使用 `Thread.Sleep`；等待用 `WaitUntilAsync` / 超时任务；耗时断言用区间。
- Q15 测试也要干净：测试辅助代码不重复（共用的放进 Testing 或测试项目的 `TestSupport/`），测试名说明场景与期望。

---

## 3. Task 0：记录基线

1. 运行 `dotnet test Junevy.Communication.slnx -c Debug` 3 次，记录每个项目的通过 / 失败 / 跳过数（预期：Core 43、Channels 239、Tcp 123、Udp 34、Serial 59 + 2 跳过，均为两个目标；Modbus 405）。
2. `git commit --allow-empty -m "chore: record P2 test baseline"`，正文写入上述结果。

---

## 4. Task 1：项目骨架

新增：
```
Junevy.Communication.Plc/Junevy.Communication.Plc.csproj       （Import Common.props；README 打包；引用 Channels；InternalsVisibleTo Plc.Tests）
Junevy.Communication.Plc/README.md                              （占位一行，Task 13 填写）
Junevy.Communication.Melsec/Junevy.Communication.Melsec.csproj （引用 Plc、Tcp、Udp、Serial；InternalsVisibleTo Melsec.Tests）
Junevy.Communication.Melsec/README.md
Tests/Junevy.Communication.Plc.Tests/Junevy.Communication.Plc.Tests.csproj      （Import Tests.props；引用 Plc、Testing）
Tests/Junevy.Communication.Melsec.Tests/Junevy.Communication.Melsec.Tests.csproj（引用 Melsec、Testing）
```
- csproj 照抄 `Junevy.Communication.Tcp.csproj` / `Tests/Junevy.Communication.Tcp.Tests/*.csproj` 的结构。
- `Junevy.Communication.slnx`：两个库加在根级，两个测试项目加进 `/Tests/` 解决方案文件夹。
- 每个测试项目放一个冒烟测试（引用一个公开类型），证明项目能编译运行。
- 提交：`feat(plc): add Plc and Melsec project skeletons`

---

## 5. Task 2：Core 字节序转换

公开 API（固定），命名空间 `Junevy.Communication.Core.Buffers`：
```csharp
public enum WordOrder { ABCD, BADC, CDAB, DCBA }

public static class ByteTransform
{
    public static short  ToInt16 (ReadOnlySpan<byte> source, WordOrder order);
    public static ushort ToUInt16(ReadOnlySpan<byte> source, WordOrder order);
    public static int    ToInt32 (ReadOnlySpan<byte> source, WordOrder order);
    public static uint   ToUInt32(ReadOnlySpan<byte> source, WordOrder order);
    public static long   ToInt64 (ReadOnlySpan<byte> source, WordOrder order);
    public static ulong  ToUInt64(ReadOnlySpan<byte> source, WordOrder order);
    public static float  ToSingle(ReadOnlySpan<byte> source, WordOrder order);
    public static double ToDouble(ReadOnlySpan<byte> source, WordOrder order);

    public static void WriteInt16 (Span<byte> destination, short  value, WordOrder order);
    // WriteUInt16 / WriteInt32 / WriteUInt32 / WriteInt64 / WriteUInt64 / WriteSingle / WriteDouble 同形
}
```
固定语义：把数值的大端字节 B0…Bn-1（B0 为最高位）按 16 位切成字 W0…Wk-1（W0 最高）：
- `ABCD`：W0…Wk-1，每字 [高, 低]（即大端）
- `BADC`：W0…Wk-1，每字 [低, 高]
- `CDAB`：Wk-1…W0，每字 [高, 低]
- `DCBA`：Wk-1…W0，每字 [低, 高]（即小端）
- 16 位数值：ABCD / CDAB 为大端，BADC / DCBA 为小端。
- 四种排列都是对合（再排列一次即还原），实现为**一个**私有重排函数，读写共用（Q2）。
- `source` / `destination` 长度不足时抛 `ArgumentException`。net472 上浮点与整数位模式互转用 `MemoryMarshal`（`BitConverter.SingleToInt32Bits` 在 net472 不存在）。

测试（`Core.Tests`，类 `ByteTransformTests`）：
- `0x12345678`：ABCD `12 34 56 78`、BADC `34 12 78 56`、CDAB `56 78 12 34`、DCBA `78 56 34 12`；64 位用 `0x0102030405060708` 同样四种；16 位 `0x1234`。
- 手册锚点：浮点 0.75 以 DCBA 写出为 `00 00 40 3F`（手册 p.77：D0 = 0000H、D1 = 3F40H，按字小端传输）。
- 每种类型 × 每种排列往返；长度不足抛异常。

提交：`feat(core): add WordOrder and ByteTransform`

---

## 6. Task 3：Plc 地址、软元件定义与声明式解析器

公开 API（固定），命名空间 `Junevy.Communication.Plc`：
```csharp
public enum PlcAddressUnit { Bit, Word }
public enum PlcAccess { Words, Bits }     // 一次请求以字为单位还是以位为单位访问

public interface IPlcDeviceDefinition
{
    string Symbol { get; }            // "D"、"ZR"、"SM"
    PlcAddressUnit Unit { get; }
    int Radix { get; }                // 10 或 16
    int MaxNumber { get; }            // 编号上限（含）
}

public abstract class PlcAddress
{
    protected PlcAddress(string text, IPlcDeviceDefinition device, int number, int? bitIndex);
    public string Text { get; }
    public IPlcDeviceDefinition Device { get; }
    public PlcAddressUnit Unit { get; }   // = Device.Unit
    public int Number { get; }
    public int? BitIndex { get; }         // 只出现在字软元件上，0–15
    public override string ToString();    // 返回 Text
}

public sealed class PlcAddressParser<TDevice, TAddress>
    where TDevice : IPlcDeviceDefinition
    where TAddress : PlcAddress
{
    public PlcAddressParser(IEnumerable<TDevice> devices, Func<TDevice, int, int?, string, TAddress> create, int bitIndexRadix = 10);
    public bool TryParse(string? text, [NotNullWhen(true)] out TAddress? address, [NotNullWhen(false)] out string? error);
}
```
解析规则（固定）：
1. 去掉首尾空白；空串 / null → 失败，消息 `The address is empty.`。
2. 软元件符号**大小写不敏感**、**最长前缀优先**（`ZR` 优先于 `Z`，`SM` / `SD` / `SB` / `SW` 优先于 `S`，`DX` / `DY` 优先于 `D`）。最长前缀匹配后剩余部分不是合法编号时**不回退**尝试更短的前缀（避免 `DX10` 被误解析成 `D` + 非法编号之外的东西）。
3. 编号按该软元件的 `Radix` 解析（十六进制允许 0–9、A–F，大小写不敏感），超过 `MaxNumber` → 失败。
4. 可选的 `.n` 位号：只允许出现在 `Word` 软元件上，按构造参数 `bitIndexRadix` 解析，范围 0–15；位软元件带位号 → 失败。
5. 失败消息为英文、包含原始文本，例如 `Unknown device in address 'Q100'.`、`'X1G' is not a valid hexadecimal device number.`。
6. 设备表中出现重复符号时构造函数抛 `ArgumentException`。

测试（`Plc.Tests`，类 `PlcAddressParserTests`，用测试内的小设备表）：上述每条规则至少一个用例，含最长前缀不回退、大小写、十六进制编号、位号进制 10 与 16、越界。

提交：`feat(plc): add device definitions and declarative address parser`

---

## 7. Task 4：PlcClientBase 与请求管线

公开 API（固定）：
```csharp
public sealed class PlcDataFormat
{
    public PlcDataFormat(WordOrder wordOrder, Encoding? stringEncoding = null);   // null → Encoding.ASCII
    public WordOrder WordOrder { get; }
    public Encoding StringEncoding { get; }
}

public class PlcClientOptions
{
    public int RetryCount { get; set; } = 0;                  // ≥ 0
    public int RetryInterval { get; set; } = 1000;            // ms，≥ 0：重试前等待链路恢复的上限
    public bool AllowReadModifyWriteBits { get; set; }        // P2-D10
}

public interface IPlcClient : IConnectable, IDisposable, IAsyncDisposable
{
    PlcDataFormat DataFormat { get; }
    Task<CommResult<byte[]>> ReadAsync(string address, int count, CancellationToken cancellationToken = default);
    Task<CommResult> WriteAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);
    Task<CommResult<bool[]>> ReadBitsAsync(string address, int count, CancellationToken cancellationToken = default);
    Task<CommResult> WriteBitsAsync(string address, ReadOnlyMemory<bool> values, CancellationToken cancellationToken = default);
}

public abstract class PlcClientBase<TAddress> : IPlcClient where TAddress : PlcAddress
{
    protected PlcClientBase(IClientChannel channel, PlcClientOptions options, PlcDataFormat dataFormat, ILogger logger);

    // IConnectable 成员全部转发给通道（Name、State、IsConnected、Statistics、StateChanged、Connect/Disconnect/WaitForConnected）
    public PlcDataFormat DataFormat { get; }
    protected IClientChannel Channel { get; }
    protected ILogger Logger { get; }

    // —— 协议必须实现（这就是"协议差异"的全部）——
    protected abstract bool TryParseAddress(string address, [NotNullWhen(true)] out TAddress? parsed, [NotNullWhen(false)] out string? error);
    protected abstract int GetMaxReadPoints(TAddress address, PlcAccess access);
    protected abstract int GetMaxWritePoints(TAddress address, PlcAccess access);
    protected abstract void BuildReadWordsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output);
    protected abstract CommResult<byte[]> ParseReadWordsResponse(int count, ReadOnlySpan<byte> frame);
    protected abstract void BuildWriteWordsRequest(TAddress address, int offset, ReadOnlySpan<byte> words, IBufferWriter<byte> output);
    protected abstract void BuildReadBitsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output);
    protected abstract CommResult<bool[]> ParseReadBitsResponse(int count, ReadOnlySpan<byte> frame);
    protected abstract void BuildWriteBitsRequest(TAddress address, int offset, ReadOnlySpan<bool> values, IBufferWriter<byte> output);
    protected abstract CommResult ParseWriteResponse(ReadOnlySpan<byte> frame);   // 字写与位写共用

    // —— 协议按需覆写 ——
    protected virtual CommResult ValidateRange(TAddress address, PlcAccess access, int count) => CommResult.Success();
}
```
固定语义：
- `offset`：相对于 `address.Number` 的偏移，单位与本次访问一致（`Words` 访问字软元件时按字；`Words` 访问位软元件时按 16 点一字；`Bits` 访问按点）。协议据此计算本包的起始编号，套件不构造地址对象。
- `ReadAsync(address, count)`：`count` 个字。地址可以是字软元件（不得带位号）或位软元件（以 16 点为一字访问）。返回 `count * 2` 字节（P2-D5 布局）。
- `WriteAsync(address, data)`：`data` 长度必须是正偶数。
- `ReadBitsAsync`：位软元件 → `Bits` 访问；字软元件（可带位号，未带视为 0）→ 套件读 `ceil((bit + count) / 16)` 个字，按 `DataFormat.WordOrder` 解出 16 位值，从低位起取位。
- `WriteBitsAsync`：位软元件 → `Bits` 访问；字软元件 → P2-D10。
- 每个公开操作：入口校验（地址解析、`count ≥ 1`、数据非空 / 偶数长度、`ValidateRange`）→ 释放检查 → 未连接立即 `NotConnected` → 进入操作锁 → 按 `GetMax*Points` 拆包，逐包 `Channel.RequestAsync`（用通道默认超时）→ 逐包重试（P2-D7）→ 任一包最终失败则整体返回该失败、不返回部分数据。
- 钩子抛出异常：捕获，返回 `Unspecified` 并记录 Error（Q10 的边界），不让异常逃出公开方法。`GetMax*Points` 返回值 < 1 视为协议实现错误，同样处理。
- Debug 日志：每个操作一行（地址、点数、包数、结果），只在 `IsEnabled(Debug)` 时格式化。
- 结构要求（Q1）：`PlcClientBase` 只放公开表面与钩子声明；拆包 / 重试 / 位提取 / 读-改-写放进 internal 的辅助类型（例如 `PlcRequestPipeline`、`PlcBitPacking`），各自可单测。

测试（`Plc.Tests`）：
- 在 `TestSupport/` 写一个最小测试协议 `FakePlcClient : PlcClientBase<FakeAddress>`（帧格式自定，几十行）与一个脚本化的 `IClientChannel` 测试替身 `ScriptedClientChannel`（记录请求、按脚本返回结果、可设置连接状态；**不要**为它写 socket）。
- 覆盖：未连接立即返回且不发请求；拆包边界（恰好上限、上限 + 1、偏移正确、包数正确）；任一包失败整体失败且不返回部分数据；重试规则逐条（`RemoteError` 不重试；`Timeout` 在 `RetryCount = 0` 时不重试；`RetryCount = 2` 且链路恢复时重试并成功；链路未在 `RetryInterval` 内恢复时返回真实的 `Timeout`）；字中位读取（跨字、位号 15）；读-改-写关闭时 `NotSupported`、开启时正确修改且记录 Warning；非法输入返回 `InvalidRequest`；取消返回 `Cancelled`；操作锁使两个并发读-改-写不交错；释放后返回 `NotConnected` 且通道被释放；钩子抛异常转换为 `Unspecified`。

提交：`feat(plc): add PlcClientBase with chunking, retry and bit access`

---

## 8. Task 5：类型化读写扩展

公开 API（固定），静态类 `PlcClientExtensions`（命名空间 `Junevy.Communication.Plc`）：
```csharp
// 每种类型 T ∈ { Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double }（C# 类型 short…double）：
Task<CommResult<T>>   ReadTAsync (this IPlcClient client, string address, CancellationToken cancellationToken = default);
Task<CommResult<T[]>> ReadTAsync (this IPlcClient client, string address, int count, CancellationToken cancellationToken = default);
Task<CommResult>      WriteTAsync(this IPlcClient client, string address, T value, CancellationToken cancellationToken = default);
Task<CommResult>      WriteTAsync(this IPlcClient client, string address, T[] values, CancellationToken cancellationToken = default);
// 例：ReadInt16Async、ReadFloatAsync、WriteDoubleAsync

Task<CommResult<string>> ReadStringAsync (this IPlcClient client, string address, int byteLength, CancellationToken cancellationToken = default);
Task<CommResult>         WriteStringAsync(this IPlcClient client, string address, string value, CancellationToken cancellationToken = default);
Task<CommResult<bool>>   ReadBoolAsync   (this IPlcClient client, string address, CancellationToken cancellationToken = default);
Task<CommResult>         WriteBoolAsync  (this IPlcClient client, string address, bool value, CancellationToken cancellationToken = default);
```
固定语义：16 位占 1 字、32 位 2 字、64 位 4 字，按 `client.DataFormat.WordOrder` 转换（`ByteTransform`）；字符串读取 `ceil(byteLength / 2)` 个字，用 `StringEncoding` 解码前 `byteLength` 字节并在第一个 `'\0'` 处截断；写字符串时编码后奇数长度补一个 `0x00`，空串返回 `InvalidRequest`；`ReadBoolAsync` / `WriteBoolAsync` 即 1 点的位读写。底层失败原样透传（`ErrorKind`、`ProtocolErrorCode` 不变）。

实现要求（Q2）：8 种数值类型共用**一个**私有泛型读辅助与**一个**写辅助（用私有委托承载 span 转换函数），公开方法只是一行转发。

测试：每种类型单值与数组 × DCBA 与 ABCD 的往返（用 `FakePlcClient` + 内存存储）；字符串奇数长度、截断、空串；失败透传。

提交：`feat(plc): add typed read and write extensions`

---

## 9. Task 6：工厂与依赖注入（Plc）

公开 API（固定）：
```csharp
public interface IPlcClientConfig { }
public interface IPlcClientCreator
{
    Type ConfigType { get; }
    IPlcClient Create(string name, IPlcClientConfig config, ILoggerFactory loggerFactory);
}
public sealed class PlcClientFactory : IDisposable, IAsyncDisposable
{
    public PlcClientFactory(IEnumerable<IPlcClientCreator> creators, ILoggerFactory? loggerFactory = null);
    public IEnumerable<string> Keys { get; }
    public int Count { get; }
    public IPlcClient GetOrAdd(string name, IPlcClientConfig config);
    public bool TryGet(string name, [NotNullWhen(true)] out IPlcClient? client);
    public T GetRequired<T>(string name) where T : class, IPlcClient;
    public bool TryRemove(string name);
    public bool RegisterAlias(string aliasName, string existingName);
    public bool RemoveAlias(string aliasName);
}
public sealed class PlcClientFactoryBuilder { Create(); WithLoggerFactory(...); WithCreator(...); Build(); }
// 命名空间 Junevy.Communication.Plc.DependencyInjection：
public static class PlcServiceCollectionExtensions { public static IServiceCollection AddPlcClients(this IServiceCollection services); }
```
- 行为与 `ChannelFactory` / `ChannelFactoryBuilder` / `AddChannels` 一致（按配置的精确运行时类型选择创建器；找不到抛 `NotSupportedException`；同一配置类型注册两个创建器抛 `ArgumentException`）。注册表一律用 Core 的 `NamedRegistry<IPlcClient>`，**不复制**注册表逻辑（Q2）。
- 测试：照 `Tests/Junevy.Communication.Channels.Tests/ChannelFactoryTests.cs` 的覆盖面写 `PlcClientFactoryTests`（用测试创建器）。

提交：`feat(plc): add PlcClientFactory and AddPlcClients`

---

## 10. Task 7：Testing 扩充

1. `PlcMemory`（public，线程安全，稀疏存储）：
   ```csharp
   public sealed class PlcMemory
   {
       public void SetWords(string device, int number, params ushort[] values);
       public ushort[] GetWords(string device, int number, int count);     // 未写过的为 0
       public void SetBits(string device, int number, params bool[] values);
       public bool[] GetBits(string device, int number, int count);
       public ushort[] GetBitsAsWords(string device, int number, int wordCount);   // 16 点一字，首点为 bit0
       public void SetBitsFromWords(string device, int number, params ushort[] words);
   }
   ```
2. `DeviceSimulator.HostUdp()` → `DeviceSimulatorUdpHost`（绑定 127.0.0.1 随机端口，每个数据报交给处理器，非 null 结果原路回复；公开 `Port`、`DisposeAsync`）。另加一个可选的回复延迟钩子，用于测试迟到应答（例如 `Func<byte[], int> replyDelay`）。
3. 把 `Tests/Junevy.Communication.Channels.Tests/TestSupport/DuplexClientChannel.cs` 移到 `Tests/Junevy.Communication.Testing/`，改为 `public`（只依赖公开的 `StreamClientChannel`），更新 Channels.Tests 的引用。
4. 把 `Tests/Junevy.Communication.Serial.Tests/TestSupport/SerialPairFactAttribute.cs` 及其依赖的环境变量帮助类移到 Testing（public），Testing 增加 `xunit.extensibility.core` 2.5.3 的包引用，更新 Serial.Tests 的引用。
5. 移动用 `git mv`，保留历史；移动后 Channels.Tests 与 Serial.Tests 的测试数不变。
6. 测试：`PlcMemory` 与 UDP 宿主各自的单测放在 `Plc.Tests`（`TestingKitTests`）。

提交：`test: extend the testing kit for PLC protocols`

---

## 11. Task 8：MELSEC 软元件表与地址

公开 API（固定），命名空间 `Junevy.Communication.Melsec`：
```csharp
public sealed class McDevice : IPlcDeviceDefinition
{
    public string Symbol { get; }  public PlcAddressUnit Unit { get; }  public int Radix { get; }  public int MaxNumber { get; }
    public string AsciiCode { get; }   // 2 个字符，如 "D*"、"ZR"、"SM"
    public byte BinaryCode { get; }    // 如 0xA8
}
public sealed class McAddress : PlcAddress
{
    public new McDevice Device { get; }
}
```
软元件表（固定，Q/L 兼容，手册 p.68 / L3561 起；`MaxNumber` 一律 `0xFFFFFF`，即 3 字节编号上限）：

| 符号 | 单位 | 进制 | ASCII | 二进制 | | 符号 | 单位 | 进制 | ASCII | 二进制 |
|---|---|---|---|---|---|---|---|---|---|---|
| SM | Bit | 10 | `SM` | 0x91 | | SD | Word | 10 | `SD` | 0xA9 |
| X | Bit | 16 | `X*` | 0x9C | | Y | Bit | 16 | `Y*` | 0x9D |
| M | Bit | 10 | `M*` | 0x90 | | L | Bit | 10 | `L*` | 0x92 |
| F | Bit | 10 | `F*` | 0x93 | | V | Bit | 10 | `V*` | 0x94 |
| B | Bit | 16 | `B*` | 0xA0 | | D | Word | 10 | `D*` | 0xA8 |
| W | Word | 16 | `W*` | 0xB4 | | TS | Bit | 10 | `TS` | 0xC1 |
| TC | Bit | 10 | `TC` | 0xC0 | | TN | Word | 10 | `TN` | 0xC2 |
| SS | Bit | 10 | `SS` | 0xC7 | | SC | Bit | 10 | `SC` | 0xC6 |
| SN | Word | 10 | `SN` | 0xC8 | | CS | Bit | 10 | `CS` | 0xC4 |
| CC | Bit | 10 | `CC` | 0xC3 | | CN | Word | 10 | `CN` | 0xC5 |
| SB | Bit | 16 | `SB` | 0xA1 | | SW | Word | 16 | `SW` | 0xB5 |
| S | Bit | 10 | `S*` | 0x98 | | DX | Bit | 16 | `DX` | 0xA2 |
| DY | Bit | 16 | `DY` | 0xA3 | | Z | Word | 10 | `Z*` | 0xCC |
| R | Word | 10 | `R*` | 0xAF | | ZR | Word | **16** | `ZR` | 0xB0 |

- 注意：`ZR` 是**十六进制**编号（手册 p.68 "Serial number access method … Hexadecimal ZR B0H"）；设计 12.4 示例写成 10 进制是错的，Task 13 修正设计文档。
- 不收录：iQ-R 专用的长计时器 / 长计数器 / LZ / RD，以及需要扩展指定（子命令 008x）的 `U\G`、链接直接软元件（P2-D2）。
- 位号进制为 16（`D100.F`），即 `new PlcAddressParser<McDevice, McAddress>(…, bitIndexRadix: 16)`。
- 表以**一份声明式列表**实现（每行一个 `McDevice`），不要写成 switch。
- ASCII 编码下十进制软元件编号上限为 999999（6 位，手册 p.67），由字段编码层提供（`McFieldCodec.MaxDeviceNumber`）；`McDevice.MaxNumber` 保持 0xFFFFFF（二进制与十六进制的上限）。

测试（`Melsec.Tests`，类 `McAddressTests`）：表中每个符号各解析一次（核对单位、进制、代码）；`ZR1F` 为 31；`DX10` 解析为 DX 而不是 D；`S10` 与 `SM10` 区分；`x1a` 大小写；`D100.F` 位号 15；`M10.1` 失败；`D-1`、`X1G`、`Q10` 失败。

提交：`feat(melsec): add the Q/L device table and address parsing`

---

## 12. Task 9：MC 协议层（3E / 4E × 二进制 / ASCII）

全部 `internal`，放在 `Junevy.Communication.Melsec/Protocol/`，只处理字节，不依赖通道（Q3）。

**结构要求（Q2 的具体化）**
- 字段编码策略：一个接口（或抽象类）+ 二进制、ASCII 两个实现，提供"写 1 字节值、写 2 字节值、写软元件（代码 + 编号，顺序由实现决定）、写字数据、写位数据"以及对应的读取。二进制：多字节数值小端，软元件为"编号 3 字节 + 代码 1 字节"；ASCII：数值为大写十六进制、高位在前，软元件为"代码 2 字符 + 编号 6 字符"，**编号按软元件进制书写**（M1234 写 `001234`，X1234 也写 `001234`，前者是十进制数字、后者是十六进制数字；手册 p.67）。
- 请求数据（命令 + 子命令 + 参数）只写一次：批量读（0401，子命令字 0000 / 位 0001）、批量写（1401，同上）、回环（0619，子命令 0000），全部通过字段编码策略输出。
- 以太网帧：3E 与 4E 是**同一个**编解码类型，4E 只多出"副头部 54 00 + 序列号 + 00 00"（响应 D4 00）。请求数据长度 = 监视定时器 + 请求数据的长度（ASCII 按字符数）。
- 位数据：二进制每点 4 位、首点在高半字节，奇数点时最后的低半字节为 0；ASCII 每点一个字符 `0` / `1`（手册 p.72）。字数据：二进制每字小端；ASCII 每字 4 位十六进制（手册 p.73）。
- 响应解码结果统一为一个内部类型（结束码 + 规范化数据），供 `McClient` 解释：结束码非 0 → `RemoteError`（P2-D12）；副头部错误、长度与实际不符、4E 序列号不符、数据长度与点数不符 → `ProtocolViolation`。访问路径的回显**不校验**（手册 p.44：错误信息中的访问路径可能与请求不同）。
- 常量（Q5）：副头部、命令、子命令、各字段偏移、上限都用具名常量并注明手册页码。

**上限（固定，手册 p.87、p.91、p.93、p.96）**：字访问 960 字（Q/L，所有传输与编码）；位访问：以太网二进制 7168 点、以太网 ASCII 3584 点。

测试（`Melsec.Tests`，类 `McEthernetFrameTests`）：第 18 节 V1–V6 逐字节编码与解码（双向），外加：错误结束码 C051 → `RemoteError` 且 `ProtocolErrorCode = 0xC051`；副头部错误、长度不符、序列号不符 → `ProtocolViolation`；奇数点位数据的半字节填充；ASCII 十进制软元件与十六进制软元件的编号书写。

提交：`feat(melsec): add 3E/4E binary and ASCII frame encoding`

---

## 13. Task 10：4C 帧（串口格式 4 / 格式 5）

全部 `internal`。

- 访问路径（固定，手册 p.45、L2322）：站号、网络号、PC 号、请求目标模块 I/O 号（2 字节）、请求目标模块站号、本站号。二进制 7 字节 `00 00 FF FF 03 00 00`（默认值），ASCII 14 字符 `0000FF03FF0000`。帧识别号：二进制 `F8`，ASCII `F8`（2 字符）。
- 格式 4（ASCII，手册 p.32）：请求 `ENQ` + 帧识别号 + 访问路径 + 请求数据 + [和校验 2 字符] + `CR LF`；响应三种：`STX … 数据 ETX [和校验] CR LF`（有数据）、`ACK 帧识别号 访问路径 CR LF`（无数据）、`NAK 帧识别号 访问路径 错误码(4 字符) CR LF`（异常）。和校验范围见 P2-D16 ①。
- 格式 5（二进制，手册 p.33、p.35）：`DLE STX` + 数据字节数（2 字节小端）+ `F8` + 访问路径 + 请求数据 + `DLE ETX` + [和校验 2 个 **ASCII** 字符]；响应在访问路径后为"响应 ID `FF FF` + 正常完成码 `00 00` + 数据"或"`FF FF` + 错误码（2 字节，P2-D16 ②）"。
  - 数据字节数 = 从帧识别号到 `DLE ETX` 之前的字节数，**不含**附加码。
  - 和校验 = 从"数据字节数"到请求数据末尾各字节之和的低 8 位，**不含**附加码，以 2 位大写十六进制 ASCII 发送（手册 p.37 示例：和为 205H → `30 35`）。
  - 附加码：数据字节数到请求 / 响应数据之间出现的 `0x10` 一律写成 `10 10`；解码时去掉。
- 和校验开关 `McClientOptions.SumCheck`（默认 true）必须与 PLC 侧设置一致；关闭时请求不带、响应也不带。
- 通道分帧（固定）：格式 4 用 `FrameCodecFactory.Create(new FramingOptions { Mode = Delimiter, Delimiters = new[] { "\r\n" }, KeepDelimiter = false, AppendDelimiterOnSend = true })`，帧编码不自己写 `CR LF`；格式 5 写一个 internal 的 `IFrameCodecFactory`：解码器从 `DLE STX` 开始，跳过成对的 `10 10`，找到未转义的 `DLE ETX` 后再取和校验 2 字节（开启时），把**原样**（仍含附加码）的整帧交出；编码器原样输出（附加码由帧编码层加）。缓冲开头不是 `DLE STX` 时抛 `FrameDecodeException`（交给通道按协议违规处理）；超过 `MaxFrameLength` 同样抛出。
- 上限：串口（C24）位访问 7904 点（两种格式），字访问 960 字。
- 和校验错误、帧识别号错误、响应 ID 错误 → `ProtocolViolation`；`NAK` / 格式 5 错误码 → `RemoteError` 且带 `ProtocolErrorCode`。

测试（类 `Mc4CFrameTests`）：第 18 节 V7–V10 逐字节双向；附加码在编号、点数、数据中各出现一次；和校验关闭时的帧；解码器的半帧（分多次到达）、帧前垃圾、`10 10` 恰好跨越两次到达的边界。

提交：`feat(melsec): add 4C serial frames (format 4 and 5)`

---

## 14. Task 11：McClient、协议配置与心跳

公开 API（固定）：
```csharp
public enum McFrameType { Frame3E, Frame4E, Frame4C }
public enum McEncoding { Binary, Ascii }      // 4C：Binary = 格式 5，Ascii = 格式 4

public sealed class McClientOptions : PlcClientOptions
{
    public McFrameType FrameType { get; set; } = McFrameType.Frame3E;
    public McEncoding Encoding { get; set; } = McEncoding.Binary;
    public byte NetworkNumber { get; set; } = 0x00;
    public byte PcNumber { get; set; } = 0xFF;
    public ushort ModuleIoNumber { get; set; } = 0x03FF;      // 请求目标模块 I/O 号
    public byte ModuleStationNumber { get; set; } = 0x00;     // 请求目标模块站号
    public ushort MonitoringTimer { get; set; } = 0x0004;     // 3E / 4E，单位 250 ms（P2-D15）
    public byte StationNumber { get; set; } = 0x00;           // 4C
    public byte SelfStationNumber { get; set; } = 0x00;       // 4C
    public bool SumCheck { get; set; } = true;                // 4C
    public PlcDataFormat? DataFormat { get; set; }            // null → new PlcDataFormat(WordOrder.DCBA)
}

public sealed class McClient : PlcClientBase<McAddress>
{
    public McClient(IClientChannel channel, McClientOptions options, ILogger<McClient>? logger = null);
    public static McClient CreateTcp(TcpClientChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory = null);
    public static McClient CreateUdp(UdpChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory = null);
    public static McClient CreateSerial(SerialChannelConfig transport, McClientOptions options, ILoggerFactory? loggerFactory = null);
}

public static class McProtocolProfile
{
    // 向调用方提供的组件实例填入 MELSEC 所需的分帧、关联、键提取器与心跳探测工厂，返回同一实例。
    // 组件中这些属性已被调用方设置时抛 ArgumentException（不静默覆盖）。
    public static TComponents ConfigureStream<TComponents>(TComponents components, McClientOptions options) where TComponents : ChannelComponents;   // TCP、串口、任意 StreamClientChannel
    public static TComponents ConfigureDatagram<TComponents>(TComponents components, McClientOptions options) where TComponents : ChannelComponents; // UDP
}
```
固定行为：
- 选项在构造时校验并取快照（D5），不改写调用方对象。`CreateTcp` / `CreateUdp` 只接受 3E / 4E，`CreateSerial` 只接受 4C，否则抛 `ArgumentException`。
- 流式分帧：3E 二进制 `LengthField(offset 7, size 2, BinaryLittleEndian)`；3E ASCII `(14, 4, AsciiHex)`；4E 二进制 `(11, 2, BinaryLittleEndian)`；4E ASCII `(22, 4, AsciiHex)`；4C 见 Task 10。数据报不设分帧。
- 关联：4E → `Keyed`（internal `IFrameKeyExtractor`：二进制取第 2–3 字节小端，ASCII 取第 4–7 字符十六进制）；其余 `Sequential`。
- 心跳：`HealthProbeFactory = channel => new McLoopbackProbe(channel, …)`，发送 0619，回环数据固定为 `"1234ABCD"`（手册 p.184：只允许 0–9、A–F），回显不一致 → `ProtocolViolation`。是否启用由通道配置 `Heartbeat.Enabled` 决定（启用时不需要 `Payload` / `ExpectedReply`）。
- `CreateTcp` 用 `ConfigureStream(new TcpChannelComponents(), options)`，因此 TLS 等仍由 `TcpClientChannelConfig` 配置。DI 创建器需要命名时用 internal 的带名重载，公开工厂方法不加名字参数。
- `McClient` 本身只做"选项 → 帧编解码选择 → 七个钩子的实现"，帧细节全部在 `Protocol/`。`GetMax*Points` 按 Task 9 / 10 的上限表返回。

测试（类 `McClientTests`，单元级，用 Plc.Tests 同款的脚本化通道或 `DuplexClientChannel`）：选项校验（错误的传输 / 帧组合抛异常、不改写调用方对象）；`ConfigureStream` 在组件已有设置时抛异常；三种帧 × 两种编码的分帧与关联配置正确；回环探测成功、回显不符、超时。

提交：`feat(melsec): add McClient, protocol profile and loopback heartbeat`

---

## 15. Task 12：MELSEC 模拟器适配与端到端测试

- `Melsec.Tests/TestSupport/McDeviceAdapter.cs`：把 MC 请求帧翻译成 `PlcMemory` 操作并生成响应帧（批量读写字 / 位、回环；可注入结束码以测试异常响应）。**允许**复用 `Protocol/` 的字段编码代码生成响应，前提是 Task 9 / 10 的黄金向量测试已经通过（黄金向量是唯一的正确性来源）。目标 200 行以内。
- 端到端（类 `McEndToEndTests`，TCP / UDP 测试放进 `SocketTiming` 集合）：
  - TCP：3E / 4E × 二进制 / ASCII，各跑一遍全部类型化读写（Int16…Double、字符串、位、字中位读取）。
  - UDP：同上四种组合。
  - 4C：`DuplexClientChannel` + `DeviceSimulator.RunAsync`，格式 4 / 5 × 和校验开 / 关；格式 5 用含大量 `0x10` 的数据。
  - 拆包：TCP 二进制读 2000 字 → 3 包，位读 8000 点 → 2 包，模拟器记录的包数与起始编号正确。
  - 异常：模拟器返回结束码 → `RemoteError` 带原始码；模拟器断开连接 → 通道重连后再次读取成功（`Reconnect.Enabled = true`）；静默的模拟器 + 启用心跳 → 状态进入 Reconnecting、原因 `HeartbeatFailed`。
  - 4E 迟到应答：UDP 模拟器让第一个请求的应答延迟到超时之后再发，下一个请求必须拿到**自己的**应答（序列号匹配），迟到应答被丢弃。
  - 真实串口（可选，`SerialPairFact`，设置 `JUNEVY_SERIAL_PAIR=COM20,COM21`）：`McClient.CreateSerial` 在一端，另一端用 `SerialPort` 打开并跑 `DeviceSimulator.RunAsync(port.BaseStream)`；格式 5 + 和校验。执行者**不运行**真实串口测试（未设置环境变量时自动跳过），由审阅者运行。

提交：`test(melsec): add the device adapter and end-to-end tests`

---

## 16. Task 13：MELSEC 的工厂接入与文档

1. DI（命名空间 `Junevy.Communication.Melsec.DependencyInjection`）：
   ```csharp
   public enum McTransport { Tcp, Udp, Serial }
   public sealed class McClientConfig : IPlcClientConfig
   {
       public McTransport Transport { get; set; } = McTransport.Tcp;
       public TcpClientChannelConfig? Tcp { get; set; }
       public UdpChannelConfig? Udp { get; set; }
       public SerialChannelConfig? Serial { get; set; }
       public McClientOptions Options { get; set; } = new McClientOptions();
   }
   public sealed class McClientCreator : IPlcClientCreator { … }   // 与 Transport 对应的配置为 null 时抛 ArgumentException
   public static class MelsecServiceCollectionExtensions { public static IServiceCollection AddMelsec(this IServiceCollection services); }  // 内部调用 AddPlcClients
   ```
   测试：`AddPlcClients().AddMelsec()` 后经工厂创建三种传输的客户端；配置缺失抛异常；`appsettings` 形状的 JSON 能绑定到 `McClientConfig`。
2. 文档：
   - `Junevy.Communication.Plc/README.md`、`Junevy.Communication.Melsec/README.md`（英文；示例在临时控制台项目中编译验证，net8.0 与 net472 均 0 错误 0 警告，与文档逐字一致）。MELSEC README 写明支持范围（P2-D2）、默认值、上限、`ZR` 十六进制、`D100.F` 位号、P2-D16 的不确定项。
   - 根 `readme.md` 新增 "PLC Protocols (preview)" 小节。
   - 新 Skill `Skills/using-junevy-plc/SKILL.md`：使用者部分（MELSEC 连接、类型化读写、错误处理、心跳、DI）+ **协议作者部分（新增协议指南）**：继承 `PlcClientBase` 要实现哪些钩子、如何用 `PlcAddressParser` 声明设备表、如何用 `ConfigureStream` 这类配置声明分帧与关联、如何用 `PlcMemory` + 适配器测试；把本期摩擦记录（第 20.2 节）里的坑写进去。
   - `CHANGELOG.md`：本期各 Task 的条目。
   - 设计文档：新增"20.2 实施修订（P2）"表，逐条列出与第 12、13 节原设计的差异（P2-D3、D4、D6、D8、D9、D12、钩子清单的最终形状、`ZR` 进制修正、`McProtocolProfile` 的形状等），并修改第 12 节正文使其与实现一致。
   - 知识库（不提交）：新增 `架构设计/Plc 地址型设备协议套件.md`、`协议细节/MELSEC MC 协议.md`、`Agent协作/新增协议指南.md`，挂进 `知识库首页.md`；更新 `架构设计/通讯库总体架构（协议族）.md` 与 `项目概述/技术栈与仓库结构.md`。
3. 提交：`feat(melsec): add DI registration and P2 documentation`（知识库不进提交）

---

## 17. Task 14：最终验收与扩展性度量

1. 连续 3 次全量 `dotnet test`，结果一致、0 失败；Release 构建新项目 0 警告；`dotnet pack -c Release` 后核对 nuspec：Plc 依赖 Channels（两个目标组），Melsec 依赖 Plc、Tcp、Udp、Serial。
2. 扩展性度量，写入第 20.3 节：
   - Melsec 生产代码按文件列出有效行数（去掉空行与注释），合计；其中协议层（`Protocol/`）、客户端、配置、DI 各多少。
   - Task 8 之后对 Core / Channels / Plc 的修改清单（期望为空；不为空时逐条对应第 20.2 节）。
   - `McClient` 实现的钩子数与各自行数。
   - 新协议作者"必须写"与"不用写"的对照（对应设计 12.8）。
3. 提交：`docs(plan): record P2 final verification and extensibility metrics`

---

## 18. 黄金向量（全部来自手册；组合帧由手册中的分段示例拼接，拼接规则见各 Task）

默认访问路径：3E / 4E 二进制 `00 FF FF 03 00`、ASCII `00FF03FF00`（手册 L2500）；4C 二进制 `00 00 FF FF 03 00 00`、ASCII `0000FF03FF0000`（手册 L2322）。监视定时器取手册示例值 `0x0010`（手册 p.43）。

| # | 场景 | 请求 | 响应 | 依据 |
|---|---|---|---|---|
| V1 | 3E 二进制，字单位读 TN100–102 | `50 00 00 FF FF 03 00 0C 00 10 00 01 04 00 00 64 00 00 C2 03 00` | `D0 00 00 FF FF 03 00 08 00 00 00 34 12 02 00 EF 1D`（TN100 = 1234H，TN101 = 2，TN102 = 1DEFH） | 手册 p.89 请求数据与响应数据 + p.42–43 帧头 |
| V2 | 3E ASCII，同 V1 | `5000` `00FF03FF00` `0018` `0010` `04010000TN0001000003` | `D000` `00FF03FF00` `0010` `0000` `123400021DEF` | 手册 p.89 + p.42–43（长度 = 4 + 20 字符 = 0x18） |
| V3 | 4E 二进制，序列号 1234H，位单位读 M100–107 | `54 00 34 12 00 00 00 FF FF 03 00 0C 00 10 00 01 04 01 00 64 00 00 90 08 00` | `D4 00 34 12 00 00 00 FF FF 03 00 06 00 00 00 00 01 00 11`（M100–107 = 0,0,0,1,0,0,1,1） | 手册 p.91 + p.42 序列号示例 |
| V4 | 4E ASCII，序列号 1234H，字单位写 D100–102 = 1995H、1202H、1130H | `5400` `1234` `0000` `00FF03FF00` `0024` `0010` `14010000D*0001000003199512021130` | `D400` `1234` `0000` `00FF03FF00` `0004` `0000` | 手册 p.94 + p.42（长度 = 4 + 32 = 0x24） |
| V5 | 3E 二进制，异常响应结束码 C051 | （请求同 V1） | `D0 00 00 FF FF 03 00 0B 00 51 C0 00 FF FF 03 00 01 04 00 00` | 手册 p.44 结束码与错误信息（访问路径 + 命令 + 子命令） |
| V6 | 3E 二进制，回环 "ABCDE" | `50 00 00 FF FF 03 00 0D 00 10 00 19 06 00 00 05 00 41 42 43 44 45` | `D0 00 00 FF FF 03 00 09 00 00 00 05 00 41 42 43 44 45` | 手册 p.185 |
| V7 | 4C 格式 5，和校验开，站号 5、网络号 7、PC 号 3、I/O 号 0004H、模块站号 1、本站号 0，位单位读 X40 起 5 点 | `10 02 12 00 F8 05 07 03 04 00 01 00 01 04 01 00 40 00 00 9C 05 00 10 03 30 35` | —（编码方向的官方示例） | 手册 p.37 和校验示例（逐字节） |
| V8 | 4C 格式 5，默认访问路径，字单位读 M16 起 2 字（编号 0x10 触发附加码） | `10 02 12 00 F8 00 00 FF FF 03 00 00 01 04 00 00 10 10 00 00 90 02 00 10 03 42 32` | 解码向量（按同一规则构造）：数据为字 0010H、1010H，数据字节数 16（0x10，本身也要加附加码）：`10 02 10 10 00 F8 00 00 FF FF 03 00 00 FF FF 00 00 10 10 00 10 10 10 10 10 03 33 37`（和 = 37H）。末尾 `10 10 10 10 10 03` 必须从左到右成对解析为两个数据 `10` 与结束符 | 手册 p.35 附加码规则与示例（"001010H"）、p.37 和校验；和 = 3B2H → `42 32` |
| V9 | 4C 格式 4，和校验开，默认访问路径，字单位读 TN100–102 | `05` `F80000FF03FF000004010000TN0001000003` `84` `0D 0A`（和校验 `84` 为 ASCII 两字符 `38 34`） | `02` `F80000FF03FF0000123400021DEF` `03` `08` `0D 0A`（和校验 `08` 含 ETX，P2-D16 ①） | 手册 p.32 格式 4、p.37 和校验规则、p.89 数据 |
| V10 | 4C 格式 4，异常 | — | `15` `F80000FF03FF0000` `C051` `0D 0A` → `RemoteError`，`ProtocolErrorCode = 0xC051` | 手册 p.32 |

审阅者已用脚本核对：手册 p.37 的两个和校验示例（1C 格式 1 → C0，4C 格式 5 → 05）按上述规则计算一致；V8、V9 的和校验由同一脚本算出。

---

## 19. 未纳入本计划的条目

- 随机读写（0403 / 1402）、多块批量读写（0406 / 1406）、远程 RUN / STOP、读 CPU 型号（用户决定本期不做）。
- iQ-R 扩展软元件指定（子命令 0002 / 0003）、扩展指定（008x）、1E 帧、1C / 2C / 3C 帧、4C 格式 1–3。
- 结束码说明表（P2-D12）。
- `PlcAddressUnit.Byte`（P4 随 S7）、按调用覆盖数据格式（P2-D9）、公开的复合操作锁 `ExclusiveAsync`（P2-D6）。
- P2-D16 的两项真机验证。
- （P2 里程碑 1 记录）完整解决方案的既有构建警告（31 个，均来自既有代码，本计划不处理）：Modbus/ModbusRtuClient.cs(397) CS0168；Modbus/Factory/ModbusFactoryBuilder.cs(32) CS1574（cref "LoggerFactory"）；Junevy.Communication.Test（WPF）的 MainWindowViewModel.cs(44) 与 App.xaml.cs(15) CS8618；Modbus.Tests 的 ModbusFactoryTests.cs、ExtensionRequestTests.cs、DisposeConcurrencyTests.cs 中的 xUnit1031、CS8600 与 CS8625。
- （P2 后处理）`PlcClientFactory` 与 P1 的 `ChannelFactory` 的外壳（创建器字典、释放检查、名称校验，约 83 行重复）：在 P2 之后的重构中统一到 Core。注册表本身已共用 `NamedRegistry`。
- （P2 后处理）`PlcRequestBuffer` 是代码库中第三个 `IBufferWriter<byte>` 实现（另两个：Channels 的内部 `PooledBufferWriter`、Testing 的 `ByteArrayBufferWriter`）：在 P2 之后的重构中合并为 Core 的公开缓冲写入器。
- （P2 Task 14 记录，P1 测试）`Tests/Junevy.Communication.Tcp.Tests` 的 `TcpClientChannelLifecycleTests.ServerClosesConnection_Reconnects` 偶发失败：`ScriptedTcpServer.CloseConnection(0)` 在服务端登记第 0 个连接之前执行（`ConnectAsync` 返回时服务端的 `AcceptAsync` 尚未完成），抛出 `ArgumentOutOfRangeException`。在 7 次全解决方案运行中出现 1 次（当次两个目标同时失败）；不含 Melsec 测试的对照运行通过，单独运行 Tcp.Tests 3 次通过，之后的完整运行也通过。未修改该测试与 `ScriptedTcpServer`（P1 代码）。建议：测试在关闭连接前等待 `AcceptedConnectionCount == 1`，或让 `CloseConnection` 等待登记。属于 P1 测试，P2 后处理。

---

## 20. 验收记录

### 20.1 各 Task 验收（审阅者填写）

- **里程碑 1（Task 0–6）**：审阅者重跑 Debug / Release 构建与全量测试（一致、0 失败），逐个审读新增生产文件，重复块扫描（5 行窗口）与方法长度扫描（≥ 40 行）均无命中，公开面与计划一致。打回一项：`IPlcProtocol` 接口与私有适配器 `ProtocolHooks` 只做转发（约 70 行），改为把钩子声明为 `protected internal`，流水线直接调用（`7d4513d`）。延后到 P2 之后的重构：`PlcClientFactory` 与 `ChannelFactory` 的外壳重复（约 83 行）、第三个 `IBufferWriter` 实现；记入第 19 节。`ConnectionStatistics` 构造函数为 internal 的缺口记为第 20.2 节第一条。
- **里程碑 2（F1、Task 7–10）**：同样重跑与扫描；黄金向量 V1–V10 在测试中逐字节出现（V9 为字符串拼接，和校验 84 / 08）。打回六项并在 `1a9a3e5` 修复：删除只供测试使用的默认访问路径；位数据改用 `bool`（`TryReadBits(…, Span<bool>)`、`McResponse.Bits`）；删除只断言常量值的测试；具名掩码并用 `UInt8Size` / `UInt16Size` 取代字面量；`McAddress` 去掉重复的设备字段；`SequenceFactory` 移入 Testing。决定：ASCII 十进制编号上限 999999 由字段编码层提供，`ValidateRange` 使用同一来源；Task 10 先写生产代码的流程偏差经审阅者独立审读后接受。
- **里程碑 3（Task 11–14）**：审阅者在 Haiku 的最终状态上连续 3 次全量测试（一致、0 失败），新项目 Release 0 警告；审读 `McClient`、`McProtocolProfile`、`McProtocolSettings`、`McFrameExchange`、分帧与 DI 代码，扫描无命中，公开面与计划一致。审阅者自行修复两项：
  - `McClient.ValidateRange` 以 `int` 计算 `16 × count`，大数量时回绕成负数而放行；改为 `long`，回归测试修复前两个目标均失败、修复后通过。
  - P1 测试 `TcpClientChannelLifecycleTests` 的竞态（`ConnectAsync` 后立即 `CloseConnection(0)`，服务端可能尚未登记连接）：关闭前等待 `AcceptedConnectionCount == 1`；修复后该类连续 5 次通过。Melsec 端到端测试同类调用之前已有一次完整读写往返，不受影响。
  - `McDeviceAdapter`（269 行，测试代码）超出 200 行目标：接受（在 Q1 的 300 行上限内）。
  - 真实串口（COM20 / COM21 虚拟对，审阅者运行）：`McSerialPairTests`（格式 5 + 和校验）与 Serial 的两个真实串口测试，两轮、两个目标全部通过。
  - 独立核对扩展性度量：自 Task 8 起 Core / Channels / Plc / Tcp / Udp / Serial 的生产代码没有任何修改（只新增 Plc 的 README）；整个 P2 期间 Channels / Tcp / Udp / Serial 未改动；Melsec 有效行 1304、Plc 有效行 1054。

### 20.2 扩展性摩擦记录（执行者从 Task 8 起填写；格式：现象 / 变通或缺口 / 建议）

- **Plc 套件测试（P2 里程碑 1 复核时补记）**
  - 现象：`ConnectionStatistics` 与 `ConnectionStateChangedEventArgs` 的构造函数是 internal，Channels 之外无法实现 `IClientChannel`（自定义通道或测试替身）而不用反射。
  - 变通或缺口：`ScriptedClientChannel` 用反射创建统计对象（见其 `CreateStatistics` 上的注释）；无法触发 `StateChanged`，因此只测试订阅转发。
  - 建议：评估公开构造函数（或统计记录方法）。属于 Channels 的公开 API 设计，P2 后处理。
- **MELSEC 软元件地址（Task 8）**
  - 现象：`PlcAddress.Device` 的类型是 `IPlcDeviceDefinition`，MELSEC 需要具体的 `McDevice`（`AsciiCode`、`BinaryCode`）。计划规定的 `McAddress.Device` 因此用 `new` 隐藏基类成员。
  - 变通或缺口：通过基类引用访问 `Device` 得到的是接口类型，取代码需要向下转换。`McAddress` 另存了一份 `McDevice` 字段，只为让新属性返回具体类型。
  - 建议：Plc 提供泛型的地址基类（例如 `PlcAddress<TDevice>`，`Device` 返回 `TDevice`），或让解析器的结果保留具体的设备类型。属于 Plc 的通用能力，P2 后评估。
- **MELSEC 帧编码（Task 9）**
  - 现象：`PlcClientBase` 的四个 `Build*Request` 钩子返回 `void`，编码阶段无法报告"协议不可表示"的参数：ASCII 十进制软元件编号超过 6 位（上限 999999，手册 p.67），以太网 ASCII 位访问超过 3584 点（手册 p.70）。计划的 `McDevice.MaxNumber`（0xFFFFFF）只对二进制与十六进制软元件成立。
  - 变通或缺口：编码器的 `RequireDeviceNumber` 抛出 `ArgumentOutOfRangeException`，作为内部守卫；运行期的拒绝由 Task 11 的 `ValidateRange` 返回 `InvalidRequest`，同一条件出现在两层。
  - 建议：协议的可表示性限制（编号位数、点数上限）由协议对象统一提供，`ValidateRange` 与编码器共用；或构建钩子改为返回 `CommResult`。属于 Plc 的通用能力，P2 后评估。
- **IBufferWriter 写入样板（Task 9）**
  - 现象：协议帧需要把整帧复制到调用方的 `IBufferWriter<byte>`，仓库没有公共的写入扩展（如 `Write(ReadOnlySpan<byte>)`）。Channels 的分帧编码器已有同样的 `GetSpan` / `Advance` 复制代码。
  - 变通或缺口：`McEthernetFrame` 内部写一份 `CopyTo`。Task 10 的 4C 帧也需要它，届时移到 Protocol 的共享位置，不写第二份。
  - 建议：Core 提供 `IBufferWriter<byte>` 的写入扩展，与第 19 节"第三个 IBufferWriter"条目一并在 P2 后处理。
- **4C 格式 5 的分帧上限与透传编码器（Task 10）**
  - 现象：`IFrameCodecFactory` 的自定义实现拿不到 `FramingOptions.MaxFrameLength`，格式 5 的分帧器必须自己保存帧长上限；透传编码器 `PassthroughFrameEncoder` 是 Channels 的 internal 类型，只能借用 `FrameCodecFactory.Create(new FramingOptions())` 的 Raw 模式取得。
  - 变通或缺口：`Mc4CBinaryFrameCodecFactory` 单独接收 `maxFrameLength`（与通道配置分开，Task 11 需保持一致）；编码器借用 Raw 模式工厂。
  - 建议：Channels 公开透传编码器，并让自定义工厂与 `FramingOptions` 共用同一个上限。属于 Channels 的通用能力，P2 后评估。
- **分段缓冲的测试辅助（Task 10）— resolved in milestone 2 review**
  - 现象：`Channels.Tests` 的 `SequenceFactory`（把字节切成多段 `ReadOnlySequence<byte>`）是测试项目内部类型；Melsec 的分帧器测试需要同样的分段构造，只能在 `McTestSupport` 再写一份（约 20 行）。
  - 变通或缺口：（已解决）`SequenceFactory` 由 `git mv` 移入 `Junevy.Communication.Testing` 并公开，增加 `FromSegments`；Channels.Tests 与 Melsec.Tests 共用同一实现，`McTestSupport` 中的副本已删除。Channels.Tests 数量仍为 239。
- **分帧器的逐字节扫描（Task 10）**
  - 现象：`IFrameDecoder.TryDecode` 接收 `ReadOnlySequence<byte>`。Channels 只提供分隔符与起止标记的扫描；格式 5 需要"跳过成对的 10H 后找未转义的 DLE ETX"，仓库里没有可复用的逐字节前进式读取器，`SequenceReader<T>` 在 Channels 中也未使用。
  - 变通或缺口：`Mc4CBinaryFrameDecoder` 把不超过 MaxFrameLength 的窗口复制为数组，再交给协议层的 `McDleFrame.Scan`；每次到达都会重新扫描已缓存的字节。
  - 建议：Channels 提供前进式的字节读取器（或基于 `SequenceReader<T>` 的扩展），分帧器不必复制窗口。属于 Channels 的通用能力，P2 后评估。
- **MELSEC 客户端的传输配置（Task 11）**
  - 现象：`ChannelComponents` 的设置覆盖传输配置的同名设置（`TcpClientChannelConfig.Framing`、`Correlation` 等），通道不报告这种覆盖。
  - 变通或缺口：`McClient.CreateTcp` / `CreateUdp` / `CreateSerial` 用默认实例比较，传输配置不是默认值时抛出 ArgumentException，不静默覆盖。
  - 建议：通道在组件覆盖配置时可见（或拒绝），让两者的优先级在类型上明确。属于 Channels 的通用能力，P2 后评估。
- **协议客户端与通道的对应（Task 11）**
  - 现象：`McClient(IClientChannel, McClientOptions)` 无法检查通道是否已由 `McProtocolProfile` 配置（分帧、关联与键提取器只能由调用方保证）。
  - 变通或缺口：构造函数的文档要求调用方使用 `McProtocolProfile`；三个工厂方法始终完成配置。
  - 建议：通道提供已配置的分帧与关联的只读视图，协议客户端构造时核对。属于 Channels 与 Plc 的通用能力，P2 后评估。

- **MELSEC 依赖注入（Task 13）**
  - 现象：`TcpClientChannelConfig` 的默认端口为 0；通道在构造时校验配置，因此 `PlcClientFactory.GetOrAdd` 抛出 `ArgumentOutOfRangeException`（不是 `ArgumentException`），创建器无法在调用通道前预检传输配置。
  - 变通或缺口：创建器照常让通道抛出，错误类型与 `ArgumentException` 约定不一致；测试与示例使用合法端口。
  - 建议：Channels 为配置提供公开的校验方法（例如 `Validate()`），创建器预检后抛出一致的 `ArgumentException`。属于 Channels 的通用能力，P2 后评估。

### 20.3 最终度量（Task 14）

以下数值在 Task 13 提交 `9346a1e` 上测得（Task 14 没有修改生产代码与测试）。"有效行"指去掉空行与注释行。

**验收（Task 14）**

- 连续三次全量 `dotnet test Junevy.Communication.slnx -c Debug --no-build`：三次结果一致，每次 2093 通过、6 跳过、0 失败（两个目标的全部测试程序集；Modbus 仅 net8.0）。
- Debug 与 Release 构建（`--no-incremental`）：0 错误；既有 31 条警告（Modbus 与 WPF 测试应用）不变；新库项目（Plc、Melsec、Plc.Tests、Melsec.Tests）0 警告。
- `dotnet pack -c Release`（Plc、Melsec）的 nuspec：Plc 依赖 Channels（net472 与 net8.0）；Melsec 依赖 Plc、Serial、Tcp、Udp（net472 与 net8.0）。
- 文档示例：README 的 6 个 C# 示例（Plc 2 个、Melsec 4 个）与根 `readme.md` 的 1 个示例，逐字抽取到临时控制台项目，net8.0 与 net472 均 0 错误、0 警告。

**Melsec 生产代码有效行数**：合计 **1304** 行（38 个文件）。协议层 `Protocol/`（19 个文件）784 行；分帧 `Framing/`（4 个文件）110 行；客户端 253 行（`McClient.cs`、`McClient.Hooks.cs`、`McProtocolProfile.cs`、`McLoopbackProbe.cs`、`McProtocolSettings.cs`）；配置与地址 101 行；DI `DependencyInjection/`（4 个文件）56 行。逐文件如下：

| 文件（相对 Junevy.Communication.Melsec/） | 有效行 | 分组 |
|---|---:|---|
| `DependencyInjection/McClientConfig.cs` | 13 | DI |
| `DependencyInjection/McClientCreator.cs` | 21 | DI |
| `DependencyInjection/McTransport.cs` | 7 | DI |
| `DependencyInjection/MelsecServiceCollectionExtensions.cs` | 15 | DI |
| `Framing/Mc4CBinaryFrameCodecFactory.cs` | 17 | 分帧 |
| `Framing/Mc4CBinaryFrameDecoder.cs` | 33 | 分帧 |
| `Framing/McSerialKeyExtractor.cs` | 22 | 分帧 |
| `Framing/McStreamFraming.cs` | 38 | 分帧 |
| `McAddress.cs` | 10 | 配置与地址 |
| `McClient.Hooks.cs` | 43 | 客户端 |
| `McClient.cs` | 90 | 客户端 |
| `McClientOptions.cs` | 16 | 配置与地址 |
| `McDevice.cs` | 20 | 配置与地址 |
| `McDeviceTable.cs` | 42 | 配置与地址 |
| `McEncoding.cs` | 6 | 配置与地址 |
| `McFrameType.cs` | 7 | 配置与地址 |
| `McLoopbackProbe.cs` | 23 | 客户端 |
| `McProtocolProfile.cs` | 55 | 客户端 |
| `McProtocolSettings.cs` | 42 | 客户端 |
| `Protocol/Mc4CAccessRoute.cs` | 29 | 协议层 |
| `Protocol/Mc4CFrame.cs` | 127 | 协议层 |
| `Protocol/McAccessRoute.cs` | 16 | 协议层 |
| `Protocol/McAsciiFieldCodec.cs` | 97 | 协议层 |
| `Protocol/McBinaryFieldCodec.cs` | 73 | 协议层 |
| `Protocol/McChecksum.cs` | 23 | 协议层 |
| `Protocol/McCommands.cs` | 14 | 协议层 |
| `Protocol/McDataKind.cs` | 8 | 协议层 |
| `Protocol/McDleFrame.cs` | 51 | 协议层 |
| `Protocol/McEthernetFrame.cs` | 74 | 协议层 |
| `Protocol/McFieldCodec.cs` | 56 | 协议层 |
| `Protocol/McFrameExchange.cs` | 74 | 协议层 |
| `Protocol/McFrameOutput.cs` | 10 | 协议层 |
| `Protocol/McLimits.cs` | 9 | 协议层 |
| `Protocol/McRequestBody.cs` | 40 | 协议层 |
| `Protocol/McResponse.cs` | 26 | 协议层 |
| `Protocol/McResponseData.cs` | 39 | 协议层 |
| `Protocol/McSerialCodes.cs` | 12 | 协议层 |
| `Protocol/McSerialNumbers.cs` | 6 | 协议层 |
| **合计** | **1304** | |

**Core / Channels / Plc 的修改（Task 8 之后，`d43e9f5..HEAD`）**：生产代码无修改。`git diff --stat d43e9f5 HEAD -- Junevy.Communication.Core Junevy.Communication.Channels Junevy.Communication.Plc` 只列出 `Junevy.Communication.Plc/README.md`（Task 13 新增的文档）。因此无需对应第 20.2 节的条目。

**McClient 的钩子**：11 个覆写，即 10 个抽象钩子加虚拟的 `ValidateRange`（`McClient.Hooks.cs`）。

| 钩子 | 有效行 |
|---|---:|
| `TryParseAddress` | 2 |
| `GetMaxReadPoints` | 1 |
| `GetMaxWritePoints` | 1 |
| `BuildReadWordsRequest` | 2 |
| `ParseReadWordsResponse` | 2 |
| `BuildWriteWordsRequest` | 2 |
| `BuildReadBitsRequest` | 2 |
| `ParseReadBitsResponse` | 2 |
| `BuildWriteBitsRequest` | 2 |
| `ParseWriteResponse` | 1 |
| `ValidateRange`（虚拟，覆写） | 12 |
| **合计** | **27** |

钩子之外，`McClient.Hooks.cs` 还有 16 行有效代码的辅助方法（`MaxPoints`、字访问的软元件头部等），因此该文件合计 43 行。

**新协议作者的"必须写"与"不用写"（设计 12.8）**

| 必须写（MELSEC 对应的文件） | 不用写（套件与通道族提供） |
|---|---|
| 设备表与地址类型（`McDeviceTable.cs`、`McAddress.cs`、`McDevice.cs`） | 连接、断线检测、重连、心跳调度、超时（Channels） |
| 十个钩子与 `ValidateRange`（`McClient.Hooks.cs`） | 请求串行化、重试、拆包、类型化读写、字节序转换（Plc） |
| 帧编解码、请求体、应答解码（`Protocol/`，784 行） | 日志、注册表、工厂的基础设施（Plc、Core） |
| 分帧与关联声明（`McProtocolProfile.cs`、`Framing/`，165 行） | TCP、UDP、串口的传输适配（Tcp、Udp、Serial） |
| 客户端工厂与守卫（`McClient.cs`） | 流分帧的字节级实现，如长度字段与分隔符（Channels） |
| 客户端选项（`McClientOptions.cs`） | 模拟服务器（Testing 的 `DeviceSimulator`、`ScriptedTcpServer`） |
| 创建器与 DI（`DependencyInjection/`，56 行） | 操作锁与按单包的重试规则（Plc） |
| 设备替身与端到端测试（`Junevy.Communication.Melsec.Tests`） | |

**偏差与待决事项**

- `McDeviceAdapter.cs` 为 269 行（有效行 216），超出计划 Task 12 的 200 行目标（设计 12.8 写的是约 100 行）。该文件保留回环、两种 4C 格式与 3E/4E 的应答，未拆分。待审阅者裁定。
- P2-D16 的两项（格式 4 的和校验范围、格式 5 的错误码字节序）仍需真机验证。
- 真实串口测试 `McSerialPairTests` 未运行（需设置 `JUNEVY_SERIAL_PAIR`，由审阅者运行）。
- 全解决方案运行中，`Tcp.Tests` 的 `ServerClosesConnection_Reconnects` 在 7 次运行中偶发失败 1 次，记录在第 19 节。
- 过程偏差：Task 10、Task 11 为生产先于测试（已在提交说明中披露，审阅者接受 Task 10 并要求不再重复）；Task 13 的 DI 生产代码也先于测试编写，随后移出生产文件复现了编译失败（红灯），再恢复并通过（绿灯）。Task 12 起按测试先行执行。
