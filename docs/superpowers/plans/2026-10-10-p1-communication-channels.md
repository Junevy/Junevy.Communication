# 计划：P1 通讯通道族（Core / Channels / Tcp / Udp / Serial）

> 来源：设计文档 `docs/superpowers/specs/2026-10-10-communication-core-design.md`（下称"设计文档"），第 3、4–11、13、18、19 节。
> 范围：Core（只含 P1 有使用者的工具）、Channels、Tcp（含可选 TLS）、Udp、Serial、测试套件 Testing。
> 版本：新包统一为 `1.0.0-preview.1`（D1）。Modbus 不做任何改动（设计文档 Q4）。
> 里程碑：M1 = Task 1–10（Core + Channels + TCP 客户端/服务端）；M2 = Task 11（TLS）；M3 = Task 12（串口）；M4 = Task 13（UDP）；收尾 = Task 14–16。
> 后续：P2（Plc 套件 + MELSEC）依赖本计划合入 master。

## 0. 已固定的设计决策（审阅时逐条确认；不同意的条目在此表修改后再执行）

| 编号 | 决策 | 影响的 Task |
|---|---|---|
| D1 | 新包版本 `1.0.0-preview.1`。P2 用 MELSEC 验证扩展性之后才发布 `1.0.0`，因为验证中发现的通道层缺口可能需要改公开 API。不执行 `nuget push` | 1、16 |
| D2 | 新项目的公共设置放在 `build/Junevy.Communication.Common.props` 和 `build/Junevy.Communication.Tests.props`，各 csproj 显式 `Import`；**不新增** `Directory.Build.props`，避免影响 Modbus 项目 | 1 |
| D3 | 新库项目开启 `TreatWarningsAsErrors`，生成 XML 文档且**不抑制** CS1591（公开成员必须有注释）。Modbus 的 CS1591 抑制是历史遗留，新代码不沿用 | 1 起全部 |
| D4 | 新测试项目双目标 `net8.0;net472`。通道层在 net472 上有大量降级分支（流不响应取消令牌、KeepAlive 设置方式、`SslStream` 认证、`SerialPort`），必须在 net472 上实际运行测试，而不是像 Modbus 那样只写一次性控制台探针 | 1 起全部 |
| D5 | 新代码**校验**配置，非法时抛 `ArgumentException`，**不改写**调用者的配置对象（Modbus 的 `Normalize` 回填默认值是知识库记录的已知陷阱，新代码不沿用） | 3、8、9、12、13 |
| D6 | P1 只实现在 P1 内有使用者的 Core 工具：`CommResult` / `CommErrorKind`、退避策略、`TimeoutScope`、`HexFormatter`、`NamedRegistry<T>`。`ByteTransform` / `WordOrder` 推迟到 P2（Plc 套件是第一个使用者），`RetryLoop` 推迟到第一个需要它的阶段，敏感信息脱敏推迟到 P3（WebApi） | 2 |
| D7 | `StreamChannel` 采用**填充循环 + 解析循环**两个循环：填充循环从 `Stream` 读入一个内部 `Pipe`，解析循环从该 `Pipe` 读出并分帧。**不使用** `PipeReader.Create(stream)`：net472 的 `NetworkStream` 和 `SerialPort.BaseStream` 不响应取消令牌，`StreamPipeReader.CancelPendingRead()` 打断不了挂起的流读取，静默分帧与半帧计时会失效；内部 `Pipe` 的读端则在两个目标上都可靠地响应 `CancelPendingRead()` | 6 |
| D8 | 握手期间（`IConnectionInitializer` 执行中）未被认领的帧进入**积压缓冲**（上限 64 帧），之后注册的 `ReceiveAsync` 先扫描积压；握手结束后积压按顺序转入派发队列。解决"对端一连上就发帧、初始化器还没来得及注册等待"的竞态（HSMS 被动端等）。积压溢出按 ProtocolViolation 处理 | 5、7、9 |
| D9 | `Keyed` 模式的迟到应答：超时的键在 `LateReplyWindow` 内记录，期间到达的同键应答记 Warning 后**丢弃**，不作为主动上报派发（避免宿主误当成新消息）。`Matcher` 模式无法识别迟到应答，按未认领帧派发。修订设计文档 5.3 节原文"派发并告警" | 5 |
| D10 | 用户取消**正在写出**的帧会断开连接（可能已写出半帧，连接不可信）；取消发生在等待发送锁期间则只返回 `Cancelled`。`Sequential` 模式下用户取消**等待应答**，按请求超时同样处理（迟到应答风险相同） | 6 |
| D11 | 配置补充：`TcpClientChannelConfig.LateReplyWindow`（`ResetOnRequestTimeout = false` 时生效）；`SerialChannelConfig.OpenTimeout`（默认 2000，`SerialPort.Open` 可能被驱动阻塞）；`UdpChannelConfig.MulticastLoopback`（默认 false）。同步修订设计文档 | 8、12、13、14 |
| D12 | `StreamClientChannel` 作为**公开抽象基类**，是新增字节流传输（命名管道、蓝牙串口等）的扩展点；`DatagramChannel` 保持 internal | 7 |
| D13 | UDP 不调用 `Socket.Connect`，定向模式的来源过滤在代码中完成，两种模式走同一条路径 | 13 |
| D14 | 内置心跳探测的 `ExpectedReply` 采用**精确匹配**（整帧逐字节相等） | 7 |
| D15 | 测试套件**复制**Modbus 测试工程中的 `ScriptedTcpServer` / `SilentTcpServer` 并泛化；Modbus 测试工程不改（Q4） | 4 |
| D16 | `Dispose()` 同步等待 `DisposeAsync()` 完成（库内部全部 `ConfigureAwait(false)`，不会因同步上下文死锁），最长等待 `DisconnectTimeout + 1000` 毫秒。边界（Task 5 验收确定）：派发队列在时限内未排空时，返回时只保证不再派发新的帧，不等待仍在执行的事件处理器；在 `FrameReceived` / `StateChanged` 处理器内调用 `DisconnectAsync` / `DisposeAsync` 不会死锁（派发上下文内只发出停止信号，不等待派发循环） | 5、7、9、13 |
| D17 | `ChannelFactory` 只管理客户端通道（`IClientChannel`）；`TcpServer` 由宿主直接创建和持有 | 10 |
| D18 | `Delimiter` 分帧跳过空帧（连续分隔符之间的空内容不交付）；`Delimiter` 编码器默认在发送时追加 `Delimiters[0]`（`FramingOptions.AppendDelimiterOnSend = true`）；其余模式的编码器原样发送 | 3 |

## 1. 执行规则（每个 Task 都适用）

1. 分支：从 master 新建 `feat/channels-p1`，每个 Task 一个 commit，提交信息见各 Task。
2. 步骤顺序固定：先写测试并确认失败（新代码阶段"失败"包括无法编译）→ 写生产代码 → 测试通过 → 清理冗余 → 更新文档 → 提交。
3. 每个 Task 完成后依次运行，全部成功才能提交：
   ```bash
   dotnet build Junevy.Communication.slnx -c Debug
   dotnet build Junevy.Communication.slnx -c Release
   dotnet test Junevy.Communication.slnx -c Debug
   ```
   `dotnet test` 会对新测试项目的 net8.0 与 net472 两个目标各运行一次，Modbus 测试照常运行且必须保持全绿。
4. 代码约束：
   - 只提供异步 API（设计文档 Q2）。
   - 库内部每个 `await` 都加 `ConfigureAwait(false)`。
   - net472 禁用：`Task.WaitAsync`、`ArgumentNullException.ThrowIfNull`、`Index` / `Range` 语法（`^1`、`a..b`）、`init` 访问器与 `record`、`ArrayBufferWriter<T>`。需要新 API 时用 `#if NET8_0_OR_GREATER`，并在注释中写明 net472 的降级行为。
   - 可空性特性（`NotNullWhen`、`MaybeNullWhen`、`DoesNotReturn`）在 net472 上由 `build/Polyfills/NullableAttributes.cs` 提供。
   - 新文件使用文件作用域命名空间（与 `ModbusTcpClient.cs` 一致）；分组注释沿用 `// ————— xxx —————`；错误消息与日志用英文，XML 注释用中文。
   - TX/RX 十六进制日志只在 `logger.IsEnabled(LogLevel.Debug)` 时生成（避免 Modbus "日志热路径 `ToHex()` 提前求值"的问题）。
5. 套接字测试：
   - 放进各测试项目的 `SocketTiming` 集合（同集合串行，与纯内存测试并行）。
   - 除了专门测超时的用例，`ConnectTimeout` 一律设为 10000。
   - 耗时断言用区间：下限取期望值的 80%，上限取期望值 + 2000 ms。
   - 测试 props 中设置 `TestTfmsInParallel=false`（.NET 9 SDK 起支持），避免两个目标的测试进程同时抢占端口和线程池。若本机 SDK 不识别该属性，改为分别以 `-f net8.0`、`-f net472` 顺序运行。
6. CHANGELOG：在 `CHANGELOG.md` 顶部新增一节 `## [Unreleased] — 通讯通道族 P1（Core / Channels / Tcp / Udp / Serial 1.0.0-preview.1）`，每个 Task 的条目写入该节，格式 `- YYYY-MM-DD [包名] 内容`，日期填执行当天。
7. 知识库：各 Task 不改 `Junevy.Communication.Wiki/`，统一在 Task 15 回写；**知识库不提交**，按 AGENTS.md 运行 `git status --short -- Junevy.Communication.Wiki` 与 `git diff --stat -- Junevy.Communication.Wiki` 并汇报。
8. 不修改 `Junevy.Communication.Modbus/` 与 `Junevy.Communication.Modbus.Tests/` 下的任何文件。
9. 本计划之外的问题记入文末"未纳入本计划的条目"，执行者遇到时只记录，不修改。

## 2. Task 0：记录基线

1. 在 master 上连续运行 3 次 `dotnet test Junevy.Communication.slnx -c Debug`，记录每次的失败 / 通过 / 总数和失败用例全名。
2. 写入分支第一个 commit 的提交信息正文：`git commit --allow-empty -m "chore: record test baseline"`。
3. 判定规则沿用 `2026-10-07-p1-critical-fixes.md` Task 0：已知偶发用例 `TcpClient_ExceptionResponse_IsTerminal_NoRetry` 单独重跑通过即不阻塞。

## 3. Task 1：项目骨架

### 3.1 新增文件（固定）

```
build/Junevy.Communication.Common.props
build/Junevy.Communication.Tests.props
build/Polyfills/NullableAttributes.cs
Junevy.Communication.Core/Junevy.Communication.Core.csproj
Junevy.Communication.Channels/Junevy.Communication.Channels.csproj
Junevy.Communication.Tcp/Junevy.Communication.Tcp.csproj
Junevy.Communication.Udp/Junevy.Communication.Udp.csproj
Junevy.Communication.Serial/Junevy.Communication.Serial.csproj
Junevy.Communication.Testing/Junevy.Communication.Testing.csproj
Junevy.Communication.Core.Tests/        Junevy.Communication.Channels.Tests/
Junevy.Communication.Tcp.Tests/         Junevy.Communication.Udp.Tests/
Junevy.Communication.Serial.Tests/
```

`build/Junevy.Communication.Common.props`：

```xml
<Project>
  <PropertyGroup>
    <TargetFrameworks>net472;net8.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Version>1.0.0-preview.1</Version>
    <Authors>Junevy</Authors>
    <Company>Junevy</Company>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <RepositoryUrl>https://github.com/Junevy/Junevy.Communication</RepositoryUrl>
    <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
    <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
  </PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net472'">
    <Compile Include="$(MSBuildThisFileDirectory)Polyfills\*.cs" LinkBase="Polyfills" />
  </ItemGroup>
</Project>
```

`build/Junevy.Communication.Tests.props`：`TargetFrameworks` 为 `net8.0;net472`，`IsPackable=false`，`IsTestProject=true`，`TestTfmsInParallel=false`；包引用与 Modbus 测试项目版本一致（`Microsoft.NET.Test.Sdk 17.8.0`、`xunit 2.5.3`、`xunit.runner.visualstudio 2.5.3`、`coverlet.collector 6.0.0`、`Microsoft.Extensions.DependencyInjection 8.0.0`）；全局 `Using Include="Xunit"`。

### 3.2 包依赖（固定）

| 项目 | 依赖 |
|---|---|
| Core | `Microsoft.Extensions.Logging.Abstractions 8.0.0`、`Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0`、`Microsoft.Bcl.AsyncInterfaces 8.0.0`、`System.Memory 4.5.5` |
| Channels | Core + `System.IO.Pipelines 8.0.0` + `System.Threading.Channels 8.0.0` |
| Tcp、Udp | Channels |
| Serial | Channels + `System.IO.Ports 8.0.0` |
| Testing | Channels（`IsPackable=false`，使用 Common.props 但不生成包） |
| 各测试项目 | 对应库 + Testing |

库项目用 `<InternalsVisibleTo Include="对应测试项目名" />` 只向自己的测试项目开放内部成员。

### 3.3 步骤

1. 创建上述文件；把全部新项目加入 `Junevy.Communication.slnx`。
2. 每个测试项目写一个冒烟测试 `ProjectSmokeTests.Runtime_MatchesTargetFramework`：net472 目标下断言 `RuntimeInformation.FrameworkDescription` 以 `.NET Framework` 开头，net8.0 目标下断言以 `.NET 8` 开头。它用来证明 net472 测试确实在 .NET Framework 上执行（本机为 4.8.1）。
3. 运行第 1 节的三条命令。

### 3.4 提交

`build: scaffold channel family projects and shared props`

## 4. Task 2：Core 工具

### 4.1 目标结构（固定）

```
Junevy.Communication.Core/
  Results/CommErrorKind.cs      Results/CommResult.cs
  Resilience/IBackoffPolicy.cs  Resilience/FixedIntervalBackoff.cs  Resilience/ExponentialBackoff.cs
  Utils/TimeoutScope.cs
  Diagnostics/HexFormatter.cs
  Registry/NamedRegistry.cs
```

```csharp
public enum CommErrorKind   // 数值与设计文档第 4 节一致：0–7 与 ModbusErrorKind 对齐，8–11 新增
{ None = 0, Unspecified = 1, InvalidRequest = 2, ConnectionClosed = 3, Timeout = 4, ProtocolViolation = 5,
  RemoteError = 6, Cancelled = 7, NotConnected = 8, AuthenticationFailed = 9, ResourceExhausted = 10, NotSupported = 11 }

public sealed class CommResult
{
    public bool IsSuccess { get; }
    public CommErrorKind ErrorKind { get; }
    public string? ErrorMessage { get; }
    public long? ProtocolErrorCode { get; }
    public Exception? Exception { get; }
    public static CommResult Success();                       // 返回缓存的单例
    public static CommResult Fail(string message, CommErrorKind kind, long? protocolErrorCode = null, Exception? exception = null);
    public CommResult<T> As<T>();                             // 把失败转为另一种类型的失败；对成功结果调用抛 InvalidOperationException
    public override string ToString();                        // "Success" 或 "Timeout: message (code 0xC051)"
}

public sealed class CommResult<T>
{
    public bool IsSuccess { get; }
    public T? Data { get; }
    public CommErrorKind ErrorKind { get; }
    public string? ErrorMessage { get; }
    public long? ProtocolErrorCode { get; }
    public Exception? Exception { get; }
    public static CommResult<T> Success(T data);
    public static CommResult<T> Fail(string message, CommErrorKind kind, long? protocolErrorCode = null, Exception? exception = null);
    public CommResult<TOther> As<TOther>();
    public CommResult ToResult();
    public override string ToString();
}
```

规则：`Fail(..., None)` 抛 `ArgumentException`；`message` 为 null 抛 `ArgumentNullException`；全部属性只读。

```csharp
public interface IBackoffPolicy
{
    /// <summary>attempt 从 1 开始；返回下一次尝试前的等待毫秒数；返回 null 表示放弃。线程安全。</summary>
    int? GetDelay(int attempt);
}

public sealed class FixedIntervalBackoff : IBackoffPolicy
{
    public FixedIntervalBackoff(int interval, int maxAttempts = 0);   // maxAttempts 0 = 无限；interval < 0 抛 ArgumentOutOfRangeException
}

public sealed class ExponentialBackoff : IBackoffPolicy
{
    public ExponentialBackoff(int initialInterval, int maxInterval, double multiplier = 2.0, double jitter = 0.2,
                              int maxAttempts = 0, Random? random = null);
}
```

`ExponentialBackoff` 的固定算法：`baseDelay = min(maxInterval, initialInterval × multiplier^(attempt−1))`；`delay = baseDelay × (1 + jitter × (2r − 1))`，r 为 [0,1) 随机数；结果取整并夹在 `[0, maxInterval]`。`Random` 访问加锁（net472 的 `Random` 不是线程安全的）；测试注入固定种子。

```csharp
/// <summary>链接"用户令牌 + 超时"的作用域；区分超时与用户取消；超时或取消时调用 onAbort 中止不响应令牌的 I/O。</summary>
public sealed class TimeoutScope : IDisposable
{
    /// <param name="timeout">毫秒；≤ 0 表示不限时。</param>
    /// <param name="onAbort">超时或用户取消时调用一次（例如销毁 socket）；可为 null。</param>
    public static TimeoutScope Start(int timeout, CancellationToken userToken, Action? onAbort = null);
    public CancellationToken Token { get; }
    public bool IsTimedOut { get; }          // 超时已触发，且用户令牌未触发
    public bool IsUserCancelled { get; }
}

public static class HexFormatter
{
    /// <summary>"AA-BB-CC"；超过 maxBytes 时截断并追加 "...(+N bytes)"。</summary>
    public static string ToHex(ReadOnlySpan<byte> data, int maxBytes = 256);
}
```

`NamedRegistry<T> where T : class`：把 `ModbusConnectionManager` 泛化，规则逐条保留——

```csharp
public sealed class NamedRegistry<T> : IDisposable, IAsyncDisposable where T : class
{
    public NamedRegistry(ILogger? logger = null);
    public int Count { get; }                          // 直接实例数（不含别名）
    public IEnumerable<string> Keys { get; }           // 含别名
    public bool TryGet(string key, [NotNullWhen(true)] out T? value);
    public TResult GetRequired<TResult>(string key) where TResult : class, T;   // 不存在抛 KeyNotFoundException，类型不符抛 InvalidCastException
    public bool TryAdd(string key, T value);
    public T GetOrAdd(string key, Func<string, T> factory);   // TryGetValue/TryAdd 自旋；竞态输家实例被释放
    public bool TryRemove(string key);                 // 移除直接键：级联删除所有指向它的别名并释放实例；同一实例被多个直接键引用时，只在最后一个引用移除时释放；移除别名：只删别名
    public bool RegisterAlias(string aliasKey, string existingKey);   // 目标不存在 / 别名等于目标 / 别名已存在 → false
    public bool RemoveAlias(string aliasKey);          // 对直接键返回 false
}
```

释放规则：实例实现 `IAsyncDisposable` 时优先 `DisposeAsync`，否则 `IDisposable`；都不实现则不释放。结构变更（移除、建别名）在内部锁下执行；别名解析带环检测。

### 4.2 测试（项目 `Junevy.Communication.Core.Tests`）

| 类 | 测试名 | 断言 |
|---|---|---|
| `CommResultTests` | `Success_IsCachedAndHasNoneKind` | 两次 `Success()` 引用相等；`ErrorKind == None` |
| | `Fail_WithNone_Throws` | `ArgumentException` |
| | `Fail_NullMessage_Throws` | `ArgumentNullException` |
| | `As_PropagatesFailureFields` | Kind、消息、协议码、异常全部保留 |
| | `As_OnSuccess_Throws` | `InvalidOperationException` |
| | `Properties_HaveNoPublicSetters` | 反射检查两个类型的公开属性无公开 setter |
| | `ErrorKindValues_MatchModbusErrorKind` | 0–7 的数值与名称逐一等于设计文档表格（`RemoteError` 对应 Modbus 的 `ModbusException`） |
| `BackoffTests` | `Fixed_ReturnsIntervalUntilMaxAttempts` | `maxAttempts=3` 时第 1–3 次返回间隔，第 4 次返回 null |
| | `Exponential_GrowsAndCaps` | 固定种子、jitter=0：500、1000、2000、4000、5000、5000（max=5000） |
| | `Exponential_JitterWithinRange` | jitter=0.2，1000 次取值都在 `[0.8×base, 1.2×base]` |
| | `Exponential_ConcurrentCalls_DoNotThrow` | 16 线程各调用 10000 次无异常 |
| `TimeoutScopeTests` | `Timeout_SetsIsTimedOutAndCallsAbortOnce` | 100 ms 超时：`IsTimedOut == true`、`onAbort` 恰好调用 1 次 |
| | `UserCancel_SetsIsUserCancelled` | `IsUserCancelled == true`、`IsTimedOut == false`、`onAbort` 调用 1 次 |
| | `NonPositiveTimeout_NeverTimesOut` | timeout=0，500 ms 内 `Token` 未触发 |
| | `Dispose_BeforeTimeout_DoesNotCallAbort` | 先 Dispose，等 300 ms，`onAbort` 未调用 |
| `HexFormatterTests` | `Format_Truncates` | 300 字节、`maxBytes=4` → `"00-01-02-03...(+296 bytes)"` |
| `NamedRegistryTests` | 把 Modbus 的 `ConnectionManagerAliasTests` 全部用例移植为泛型版本（实例用测试替身 `DisposableProbe` 记录释放次数），另加 | |
| | `GetOrAdd_Concurrent_LoserIsDisposed` | 32 线程同键 `GetOrAdd`：只有 1 个实例存活，其余 31 个各被释放 1 次 |
| | `PrefersDisposeAsync` | 同时实现两种接口时只调用 `DisposeAsync` |

### 4.3 提交

`feat(core): add result model, backoff policies, timeout scope and named registry`

## 5. Task 3：Channels 抽象、配置与分帧器

### 5.1 目标结构（固定）

```
Junevy.Communication.Channels/
  Abstractions/  IConnectable.cs  IByteChannel.cs  IClientChannel.cs  IFrameDecoder.cs  IFlushableFrameDecoder.cs
                 IFrameEncoder.cs  IFrameCodecFactory.cs  IResponseMatcher.cs  IFrameKeyExtractor.cs
                 IHealthProbe.cs  IConnectionInitializer.cs  IChannelConfig.cs  IChannelCreator.cs
  Models/        ConnectionState.cs  DisconnectReason.cs  ConnectionStateChangedEventArgs.cs
                 FrameReceivedEventArgs.cs  ConnectionStatistics.cs  RequestOptions.cs
  Options/       FramingOptions.cs  HeartbeatOptions.cs  ReconnectOptions.cs  ChannelComponents.cs
                 Enums.cs（FramingMode, LengthFieldEncoding, CorrelationMode, QueueFullMode, ReconnectMode, PartialFrameAction）
  Framing/       RawFrameDecoder.cs  DelimiterFrameDecoder.cs  FixedLengthFrameDecoder.cs  LengthFieldFrameDecoder.cs
                 StartEndFrameDecoder.cs  IdleGapFrameDecoder.cs  DelimiterFrameEncoder.cs  PassthroughFrameEncoder.cs
                 FrameCodecFactory.cs  FrameDecodeException.cs  ByteSequenceParser.cs
```

接口签名与设计文档 5.1–5.6 节一致，以下为补充的固定内容：

```csharp
public sealed class FramingOptions
{
    public FramingMode Mode { get; set; } = FramingMode.Raw;
    public int MaxFrameLength { get; set; } = 65536;
    public string[]? Delimiters { get; set; }                 // Delimiter 模式必填
    public bool KeepDelimiter { get; set; }
    public bool AppendDelimiterOnSend { get; set; } = true;   // D18
    public int FrameLength { get; set; }                      // FixedLength
    public int LengthFieldOffset { get; set; }                // LengthField
    public int LengthFieldSize { get; set; } = 2;
    public LengthFieldEncoding LengthFieldEncoding { get; set; } = LengthFieldEncoding.BinaryBigEndian;
    public int LengthAdjustment { get; set; }
    public int InitialBytesToStrip { get; set; }
    public string? StartMarker { get; set; }                  // StartEnd
    public string? EndMarker { get; set; }
    public bool KeepMarkers { get; set; } = true;
    public int GapTimeout { get; set; } = 20;                 // IdleGap
}

public sealed class ConnectionStatistics      // 线程安全；属性读取 Interlocked 值；递增方法为 internal
{
    public long BytesSent { get; }        public long BytesReceived { get; }
    public long FramesSent { get; }       public long FramesReceived { get; }
    public long FramesDropped { get; }    public long ProtocolErrors { get; }
    public long ReconnectCount { get; }   public int ConsecutiveHeartbeatFailures { get; }
    public DateTimeOffset? LastSentAt { get; }  public DateTimeOffset? LastReceivedAt { get; }
    public DateTimeOffset? ConnectedSince { get; }
}

public static class ByteSequenceParser
{
    /// <summary>"hex:0D 0A" / "hex:0D-0A" → 十六进制字节；否则按文本解析，支持 \r \n \t \0 \\ \xHH 转义，文本按 UTF-8 编码。空字符串或非法 hex 抛 FormatException。</summary>
    public static byte[] Parse(string text);
}
```

`FrameCodecFactory.Create(FramingOptions options)` 返回 `IFrameCodecFactory`，在创建时校验参数（D5），错误示例：`Delimiter` 模式未给分隔符、`FrameLength ≤ 0`、二进制 `LengthFieldSize ∉ {1,2,4}`、ASCII `LengthFieldSize ∉ [1,8]`、`InitialBytesToStrip > LengthFieldOffset + LengthFieldSize + 修正后的最小帧长`、`GapTimeout ≤ 0`、`MaxFrameLength ≤ 0`。

### 5.2 分帧器的固定算法

所有分帧器必须正确处理**多段** `ReadOnlySequence<byte>`（字节分散在多个段中）。

| 分帧器 | 算法 |
|---|---|
| Raw | 缓冲非空时交出全部字节；超过 `MaxFrameLength` 时按 `MaxFrameLength` 切块交出 |
| Delimiter | 在缓冲中找所有分隔符的最早出现位置（位置相同取最长的分隔符）；交出其前的内容（`KeepDelimiter` 时含分隔符）；空内容跳过（D18）；找不到分隔符且缓冲长度超过 `MaxFrameLength + 最长分隔符长度` 时抛 `FrameDecodeException` |
| FixedLength | 缓冲 ≥ `FrameLength` 时交出 `FrameLength` 字节 |
| LengthField | 缓冲 < offset + size 时返回 false；读长度值（二进制按大小端读无符号整数；ASCII 把 size 个字符按十六进制或十进制解析，含非法字符时抛 `FrameDecodeException`）；`total = offset + size + length + adjustment`；`total < offset + size` 或 `total > MaxFrameLength` 时抛异常；缓冲 ≥ total 时交出 `[InitialBytesToStrip, total)` |
| StartEnd | 丢弃起始符之前的字节（重新同步）；从起始符之后找结束符；找到则交出（`KeepMarkers` 决定是否含标记）；未找到且从起始符算起超过 `MaxFrameLength` 时抛异常 |
| IdleGap | `TryDecode` 只在缓冲 > `MaxFrameLength` 时抛异常（`MaxFrameLength` 为允许的最大帧长，含边界），其余返回 false；`TryFlush` 交出全部缓冲；`FlushTimeout = GapTimeout` |

### 5.3 测试（项目 `Junevy.Communication.Channels.Tests`，类 `FramingTests`）

测试辅助 `SequenceFactory.Segmented(byte[] data, int segmentSize)` 构造多段序列；`DecoderHarness.Feed(decoder, byte[] stream, int chunkSize)` 模拟逐块到达并收集所有帧。

| 测试名 | 断言 |
|---|---|
| `{每种分帧器}_SingleFrame` | 1 帧输入交出 1 帧，内容正确 |
| `{每种分帧器}_100FramesCoalesced` | 100 帧拼成一块输入，交出 100 帧且顺序正确 |
| `{每种分帧器}_ByteByByte` | `chunkSize=1` 逐字节喂入，结果与整块相同 |
| `{每种分帧器}_SegmentedSequence` | 段大小 1、3、7 时结果一致 |
| `Delimiter_MultipleDelimiters_EarliestWins` | 分隔符 `\r\n` 与 `\n`，输入 `a\nb\r\nc\n` → `a`、`b`、`c` |
| `Delimiter_EmptyFramesSkipped` | `\r\n\r\nx\r\n` → 只交出 `x` |
| `Delimiter_TooLong_Throws` | 无分隔符的超长输入抛 `FrameDecodeException` |
| `LengthField_ProtocolTable` | 设计文档 5.2 节表格中的 6 行（Modbus TCP、S7、MC 3E 二进制、MC 3E ASCII、MC 4E、HSMS）各用一帧真实格式的样例验证 |
| `LengthField_AsciiInvalidChar_Throws` | 长度字段含 `G` 抛异常 |
| `LengthField_TotalExceedsMax_Throws` | — |
| `StartEnd_GarbageBeforeStart_Resyncs` | `xx\x02AB\x03` → `\x02AB\x03` |
| `IdleGap_TryFlush_ReturnsBuffer` | — |
| `CodecFactory_InvalidOptions_Throw` | 5.1 列出的每一种非法组合各一条 |
| `DelimiterEncoder_AppendsFirstDelimiter` | `AppendDelimiterOnSend=true` 时 `T1` → `T1\r\n`；false 时原样 |
| `ByteSequenceParser_Cases` | `hex:0D0A`、`hex:0D-0A`、`\r\n`、`\x02`、`\\`、非法 hex 抛 `FormatException` |

### 5.4 提交

`feat(channels): add channel abstractions, options and frame decoders`

## 6. Task 4：测试套件（P1 部分）

### 6.1 目标结构（固定）

```
Junevy.Communication.Testing/
  DuplexStreamPair.cs        两个内部 Pipe 组成的双工流对
  ScriptedTcpServer.cs       由 Modbus 测试工程复制并泛化（D15）
  SilentTcpServer.cs         同上
  ScriptedUdpPeer.cs
  DeviceSimulator.cs
  TimingAssert.cs
  TestLogger.cs              收集日志条目的 ILogger / ILoggerFactory 实现（断言 Warning 是否被记录）
```

```csharp
public sealed class DuplexStreamPair : IDisposable
{
    /// <param name="pauseWriterThreshold">某一端未被读取的字节超过此值时，对端写入挂起（用于模拟"对端不读取"导致的发送超时）。</param>
    public static DuplexStreamPair Create(long pauseWriterThreshold = 1024 * 1024);
    public Stream A { get; }
    public Stream B { get; }
    /// <summary>模拟链路中断：两端后续的读写抛 IOException，挂起的读写立即以 IOException 结束。</summary>
    public void Abort();
}

public sealed class ScriptedUdpPeer : IDisposable
{
    public static ScriptedUdpPeer Start(Func<int, UdpReceiveResult, Task<byte[]?>> handler);   // 参数：数据报序号、数据报；返回 null 表示不回复
    public IPEndPoint EndPoint { get; }
    public int ReceivedCount { get; }
    public Task SendToAsync(IPEndPoint remote, byte[] payload);
}

public sealed class DeviceSimulator
{
    public DeviceSimulator(IFrameCodecFactory codec, Func<byte[], byte[]?> handler);   // handler 返回 null 表示不回复
    public Task RunAsync(Stream stream, CancellationToken cancellationToken);          // 在任意流上应答（DuplexStreamPair 或 TCP 连接）
    public DeviceSimulatorTcpHost HostTcp();   // 用 TcpListener 承载，与被测的 TcpServer 无关；返回的宿主含 Port、AcceptedConnectionCount、DisposeAsync（第 20 节勘误）
}

public static class TimingAssert
{
    public static Task<TimeSpan> WithinAsync(TimeSpan min, TimeSpan max, Func<Task> action);
}
```

`ScriptedTcpServer` 在 Modbus 版本基础上增加：`ReceivedBytes`、`ClosedByPeerCount`，以及 `CloseConnection(int index)`（主动断开第 index 个连接，用于重连测试）。

### 6.2 测试（`Junevy.Communication.Channels.Tests`，类 `TestingKitTests`）

| 测试名 | 断言 |
|---|---|
| `DuplexStream_RoundTrip` | A 写 1 MB，B 读到相同内容 |
| `DuplexStream_Abort_FailsPendingRead` | B 挂起读时 `Abort()`，读在 1 s 内抛 `IOException` |
| `DuplexStream_PauseThreshold_BlocksWriter` | 阈值 4096、B 不读：A 写 64 KB 的任务 500 ms 内未完成；B 开始读后完成 |
| `DeviceSimulator_EchoesOverDuplex` | 分隔符帧回显 |
| `ScriptedUdpPeer_RepliesAndCounts` | — |

### 6.3 提交

`test: add shared testing kit for channel family`

## 7. Task 5：帧路由（FrameRouter / PendingRequestTable）

### 7.1 目标结构（固定，全部 internal）

```csharp
internal sealed class PendingRequestTable
{
    public PendingRequestTable(CorrelationMode mode, IFrameKeyExtractor? keyExtractor, int lateReplyWindow, ILogger logger);

    /// <summary>注册等待者。payload 为空表示 ReceiveAsync。expectedRemote 用于 UDP 非定向模式的来源校验。
    /// Keyed 模式提取不到请求键、或该键已在途时，返回的等待者立即以 InvalidRequest 完成。</summary>
    public PendingRequest Register(ReadOnlyMemory<byte> payload, IResponseMatcher? matcher, int timeout,
                                   EndPoint? expectedRemote, CancellationToken cancellationToken);

    /// <summary>由解析循环调用：按"在途请求 → ReceiveAsync 等待者（FIFO）"的顺序尝试认领；返回 false 表示未认领。</summary>
    public bool TryComplete(byte[] frame, EndPoint? remote);

    /// <summary>判断未认领的帧是否属于迟到应答（Sequential 处于丢弃窗口内，或 Keyed 命中近期超时键）；是则记 Warning 并返回 true（调用方丢弃）。</summary>
    public bool IsLateReply(byte[] frame);

    /// <summary>连接关闭：所有在途等待者以给定失败完成。</summary>
    public void FailAll(CommResult failure);

    /// <summary>握手积压（D8）：BeginHandshake 后未认领帧进入积压；Register 先扫描积压；EndHandshake 返回剩余积压供转入派发队列。</summary>
    public void BeginHandshake();
    public IReadOnlyList<(byte[] Frame, EndPoint? Remote)> EndHandshake();
}

internal sealed class PendingRequest : IDisposable
{
    public Task<CommResult<byte[]>> Completion { get; }   // TaskCompletionSource 使用 RunContinuationsAsynchronously
    public void StartTimer();                              // 请求帧写出完成后调用；ReceiveAsync 在注册时即开始计时
}

internal sealed class FrameRouter : IAsyncDisposable
{
    public FrameRouter(PendingRequestTable table, int queueCapacity, QueueFullMode fullMode,
                       Func<FrameReceivedEventArgs, Task> raise, ConnectionStatistics statistics, ILogger logger);
    public void Start();                                                   // 启动派发循环
    public ValueTask RouteAsync(byte[] frame, EndPoint? remote, CancellationToken cancellationToken);   // Wait 模式在队列满时挂起
    public ValueTask StopAsync(int drainTimeout);   // 在 drainTimeout 内派发完队列中已收到的帧，超时才丢弃剩余帧（Task 5 验收修正）
}
```

### 7.2 固定规则

1. 路由顺序：在途请求认领 → 迟到应答判定（是则丢弃）→ 接收等待者认领 → 握手期间进积压，否则进派发队列。迟到判定必须先于接收等待者，否则默认匹配任意帧的 `ReceiveAsync` 会拿走超时请求的迟到应答（Task 5 验收修正）。
2. `Sequential`：同一时刻至多一个请求型等待者（由调用方的请求锁保证，表内再做断言）；请求未指定 Matcher 时认领任意帧。
3. `Matcher`：多个请求型等待者按注册顺序依次判定；未指定 Matcher 的等同"任意帧"。
4. `Keyed`：按键字典匹配；`TryGetResponseKey` 返回 false 的帧视为未认领。
5. UDP 非定向模式（`expectedRemote` 非 null）：来源地址不等于 `expectedRemote` 的帧不认领。
6. 迟到应答（D9）：`Sequential` 在请求超时（或被取消）后的 `lateReplyWindow` 毫秒内丢弃未认领帧；`Keyed` 记录超时键（最多 256 个，过期即删除），期间到达的同键帧丢弃；两者都记 Warning 并递增 `FramesDropped`。
7. 派发：派发循环逐个调用 `raise`；`raise` 内部对事件的每个订阅者单独 try/catch，异常只记 Error 日志。
8. 队列满：`Wait` 挂起 `RouteAsync`；`DropOldest` / `DropNewest` 丢弃并递增 `FramesDropped`，每 100 次丢弃记一次 Warning。
9. 握手积压超过 64 帧：`RouteAsync` 抛 `FrameDecodeException`（由调用方按 ProtocolViolation 处理）。

### 7.3 测试（类 `FrameRouterTests`，全部内存，不用 socket）

| 测试名 | 断言 |
|---|---|
| `Sequential_FirstFrameCompletesRequest` | — |
| `Sequential_WithMatcher_NonMatchingGoesToEvent` | — |
| `Matcher_OutOfOrderReplies_EachMatched` | 3 个在途请求，应答逆序到达，各自拿到正确应答 |
| `Keyed_ConcurrentRequests_MatchedByKey` | 50 个在途请求随机顺序应答，全部正确 |
| `Keyed_MissingRequestKey_FailsInvalidRequest` | — |
| `Keyed_DuplicateInFlightKey_FailsInvalidRequest` | — |
| `ReceiveWaiter_ClaimsAfterRequest` | 同时有请求与 ReceiveAsync 等待者时，第一帧给请求，第二帧给等待者 |
| `Timeout_CompletesWithTimeout` | 200 ms 超时：返回 `Timeout`，耗时落在区间内 |
| `TimerStartsOnlyAfterStartTimer` | 注册 300 ms 后才调用 `StartTimer`，超时从调用时起算 |
| `Sequential_LateReplyWithinWindow_Dropped` | 超时后窗口内到达的帧不触发事件，`FramesDropped == 1`，记 Warning |
| `Keyed_LateReplyForTimedOutKey_Dropped` | — |
| `Matcher_LateReply_RaisedAsUnsolicited` | — |
| `UnconnectedUdp_SourceMismatch_NotClaimed` | — |
| `FailAll_CompletesAllWaiters` | 全部以 `ConnectionClosed` 完成 |
| `Handshake_BacklogClaimedByLaterReceive` | `BeginHandshake` 后先到帧、后注册 ReceiveAsync，等待者拿到该帧 |
| `Handshake_EndReturnsRemainingInOrder` | — |
| `Handshake_BacklogOverflow_Throws` | 第 65 帧抛 `FrameDecodeException` |
| `HandlerException_DoesNotStopDispatch` | 第一个订阅者抛异常，第二个订阅者与后续帧照常收到 |
| `QueueFull_Wait_BlocksRoute` | 容量 2、处理器阻塞：第 3 次 `RouteAsync` 挂起，处理器放行后完成 |
| `QueueFull_DropOldest_CountsDrops` | — |
| `SlowHandler_DoesNotDelayReply` | 处理器阻塞 2 s 期间，请求的应答在 100 ms 内完成 |

### 7.4 提交

`feat(channels): add frame router with request correlation`

## 8. Task 6：StreamChannel

### 8.1 目标结构（固定，internal）

```csharp
internal sealed class StreamChannel : IAsyncDisposable
{
    public StreamChannel(Stream stream, StreamChannelSettings settings, IFrameDecoder decoder, IFrameEncoder encoder,
                         PendingRequestTable table, FrameRouter router, ConnectionStatistics statistics, ILogger logger,
                         Action<DisconnectReason, Exception?> onFault, Action abortTransport);
    public void Start();
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
    public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options, EndPoint? expectedRemote, CancellationToken cancellationToken);
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options, CancellationToken cancellationToken);
    public Task StopAsync(int drainTimeout);
}

internal sealed class StreamChannelSettings
{
    public int SendTimeout, RequestTimeout, LateReplyWindow, PartialFrameTimeout, ReceiveBufferSize = 4096;
    public CorrelationMode Correlation;  public bool ResetOnRequestTimeout;  public PartialFrameAction PartialFrameAction;
}
```

另新增 internal `PooledBufferWriter : IBufferWriter<byte>`（基于 `ArrayPool<byte>`，替代 net472 没有的 `ArrayBufferWriter<T>`）。

### 8.2 固定算法（D7）

**填充循环**：
1. `Memory<byte> memory = pipe.Writer.GetMemory(ReceiveBufferSize)`。
2. 读取：net8 用 `stream.ReadAsync(memory, stopToken)`；net472 用 `MemoryMarshal.TryGetArray` 取得数组段后调用 `stream.ReadAsync(array, offset, count, stopToken)`（net472 的流可能忽略令牌，停止时依靠 `abortTransport` 销毁底层对象打断读取）。
3. 读到 0 字节或读取出错：**不直接报告**，只记录原因（0 字节或 IOException → RemoteClosed，其他 → Error）与异常，然后正常完成 Pipe 写端；正在停止时不记录。由解析循环在处理完全部已收到的数据之后再报告（Task 6 验收修正：设备"回完应答立即断开"时，请求必须拿到应答而不是 ConnectionClosed）。
4. `writer.Advance(n)`；`await writer.FlushAsync()`。解析循环跟不上时 `FlushAsync` 会挂起，形成背压（TCP 由此触发流控）。
5. `Pipe` 选项：`pauseWriterThreshold = 1 MiB`、`resumeWriterThreshold = 512 KiB`、`useSynchronizationContext = false`。

**解析循环**：
1. `ReadResult result = await pipe.Reader.ReadAsync()`；`buffer = result.Buffer`。
2. 循环 `decoder.TryDecode(ref buffer, out frame)`：每帧 `frame.ToArray()`，统计 `FramesReceived` / `BytesReceived` / `LastReceivedAt`，Debug 日志，`await router.RouteAsync(frame)`。
3. `pipe.Reader.AdvanceTo(buffer.Start, buffer.End)`。
4. 若剩余未成帧字节 > 0，且分帧器是 `IFlushableFrameDecoder` 或 `PartialFrameTimeout > 0`，启动计时器（时长为 `FlushTimeout` 或 `PartialFrameTimeout`），到期调用 `pipe.Reader.CancelPendingRead()`；有新数据到达时重置计时器。
5. 读到 `result.IsCanceled` 且计时器已触发：`IFlushableFrameDecoder` 调用 `TryFlush` 交帧；否则按半帧超时处理——`PartialFrameAction.Disconnect` 调用 `onFault(PartialFrameTimeout)`，`Discard` 丢弃全部剩余字节并递增 `ProtocolErrors`。
6. `FrameDecodeException`：`Disconnect` 调用 `onFault(ProtocolViolation)`；`Discard` 丢弃缓冲、递增 `ProtocolErrors` 后继续。
7. 读到 `result.IsCompleted`：先分帧并路由缓冲中的全部完整帧；`IFlushableFrameDecoder` 再 `TryFlush` 交出残留；不可刷新的残留半帧丢弃（记 Warning、递增 `ProtocolErrors`）；最后按填充循环记录的原因调用 `onFault`（未记录原因说明是停止导致的完成，不报告）。

**发送**：
1. `await sendLock.WaitAsync(userToken)`；在此处被取消返回 `Cancelled`，不影响连接。
2. `PooledBufferWriter` 编码；`TimeoutScope.Start(SendTimeout, userToken, abortTransport)`；`WriteAsync` + `FlushAsync`（net472 用数组重载）。
3. 成功：统计 `FramesSent` / `BytesSent` / `LastSentAt`，Debug 日志。
4. 失败：超时返回 `Timeout`；用户取消返回 `Cancelled`；I/O 异常返回 `ConnectionClosed`。三种情况都调用 `onFault(SendFailed)`（D10）。

**请求**：
1. 负载为空时立即返回 `InvalidRequest`（空负载在 `PendingRequestTable` 中表示 `ReceiveAsync`，Task 5 验收确定）。
2. `Sequential` 先 `await requestLock.WaitAsync(userToken)`（被取消返回 `Cancelled`）；`Matcher` / `Keyed` 不加请求锁。
3. `table.Register(...)` → `SendAsync` → 发送失败则释放等待者并返回发送失败 → `StartTimer()` → 等待 `Completion`。
4. 超时，或用户取消等待：`Sequential` 且 `ResetOnRequestTimeout` 时调用 `onFault(RequestTimeout)`；否则进入迟到应答窗口，并且**在窗口期内继续持有请求锁**再释放——否则下一个请求会在窗口期内发出并认领上一个请求的迟到应答（Task 5 验收确定）。用户取消时，等待窗口的这段时间不受用户令牌约束，但连接停止时立即结束。
5. `finally` 释放请求锁。

**停止**：设置停止标志 → `abortTransport()` → `pipe.Writer.Complete()` → 在 `drainTimeout` 内等待两个循环退出 → `table.FailAll(ConnectionClosed)` → `router.StopAsync(drainTimeout)`（已收到但尚未派发的帧在时限内送达）。`onFault` 对同一个 `StreamChannel` 实例至多调用一次（Interlocked 标志）。

### 8.3 测试（类 `StreamChannelTests`，使用 `DuplexStreamPair`）

| 测试名 | 断言 |
|---|---|
| `Send_FrameArrivesAtPeer` | — |
| `Receive_UnsolicitedRaisesEvent` | — |
| `ConcurrentSends_FramesNotInterleaved` | 100 个并发 `SendAsync`（分隔符帧），对端解出 100 个完整帧 |
| `Request_Sequential_RoundTrip` | 对端 `DeviceSimulator` 回显 |
| `Request_And_Unsolicited_Interleaved` | 对端在应答前先发 1 帧主动上报（请求带 Matcher）：请求拿到应答，事件收到上报 |
| `Request_Timeout_ResetCallsOnFault` | `ResetOnRequestTimeout=true`：返回 `Timeout`，`onFault(RequestTimeout)` 调用 1 次 |
| `Request_Timeout_NoReset_LateReplyDropped` | — |
| `IdleGap_FlushAfterGap` | 写 5 字节后静默：在 `GapTimeout`（50 ms）到 `GapTimeout + 300` ms 之间交出 1 帧 |
| `IdleGap_ContinuousBytes_NotSplit` | 每 10 ms 写 1 字节共 10 字节，GapTimeout=50：交出 1 帧 10 字节 |
| `PartialFrame_Disconnect` | LengthField 帧只写一半，`PartialFrameTimeout=200`：`onFault(PartialFrameTimeout)` |
| `PartialFrame_Discard_ContinuesReceiving` | Discard：丢弃后再写完整帧能正常交出；`ProtocolErrors == 1` |
| `DecodeError_Disconnect` | 超长帧：`onFault(ProtocolViolation)` |
| `RemoteClose_FaultsAndFailsPending` | 在途请求时 `Abort()`：请求返回 `ConnectionClosed`，`onFault(RemoteClosed 或 Error)` |
| `SendTimeout_PeerNotReading` | 阈值 4096、对端不读、`SendTimeout=300`：返回 `Timeout`，耗时落在区间内，`onFault(SendFailed)`，`abortTransport` 被调用 |
| `CancelWhileWaitingSendLock_DoesNotFault` | — |
| `CancelDuringWrite_Faults` | D10 |
| `Backpressure_WaitMode_ProducerBlocks` | 处理器阻塞、队列容量 4、对端持续写：对端写入最终挂起（证明背压传到流）；放行后全部帧按顺序到达，`FramesDropped == 0` |
| `Stop_FailsPendingAndCompletesLoops` | `StopAsync` 在 `drainTimeout` 内返回 |

### 8.4 提交

`feat(channels): add stream channel with fill/parse loops`

## 9. Task 7：生命周期（ConnectionSupervisor、HeartbeatMonitor、StreamClientChannel）

### 9.1 目标结构（固定）

```csharp
internal interface IConnectionDriver
{
    Task<CommResult> OpenAsync(CancellationToken cancellationToken);        // 打开链路 + 握手；用户取消抛 OperationCanceledException
    Task CloseAsync(DisconnectReason reason, int drainTimeout);
}

internal sealed class ConnectionSupervisor : IAsyncDisposable
{
    public ConnectionSupervisor(string name, IConnectionDriver driver, IBackoffPolicy? reconnectPolicy, bool reconnectOnInitialFailure,
                                ConnectionStatistics statistics, ILogger logger);
    public ConnectionState State { get; }
    public long Generation { get; }
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;
    public Task<CommResult> ConnectAsync(CancellationToken cancellationToken);
    public Task DisconnectAsync(int drainTimeout, CancellationToken cancellationToken);
    public Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken);
    public void ReportConnectionLost(long generation, DisconnectReason reason, Exception? exception);
}

internal sealed class HeartbeatMonitor : IAsyncDisposable
{
    public HeartbeatMonitor(IHealthProbe? probe, HeartbeatOptions options, int idleTimeout, ConnectionStatistics statistics,
                            Action<DisconnectReason> onDead, ILogger logger);
    public void Start();
    public ValueTask StopAsync();
}

internal sealed class PayloadHeartbeatProbe : IHealthProbe   // 由 HeartbeatOptions.Payload / ExpectedReply 构造（D14）
{
    public PayloadHeartbeatProbe(IByteChannel channel, byte[] payload, byte[]? expectedReply, int timeout);
}

/// <summary>字节流客户端通道的公开基类（D12）。TcpClientChannel、SerialChannel 继承它；新增字节流传输也继承它。</summary>
public abstract class StreamClientChannel : IClientChannel
{
    protected StreamClientChannel(string name, ClientChannelSettings settings, ChannelComponents? components, ILogger logger);
    protected abstract Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken);  // 含该传输自己的连接超时；用户取消抛 OperationCanceledException
    protected abstract void AbortTransport();                                                        // 立即中止；可在任意线程重复调用
    protected virtual Task OnClosingAsync(CancellationToken cancellationToken) => Task.CompletedTask; // 优雅关闭前的动作（TCP：Shutdown(Send)）
    protected virtual PartialFrameAction PartialFrameAction => PartialFrameAction.Disconnect;
    protected abstract string DescribeEndpoint();                                                    // 日志用，如 "192.168.1.10:5000"、"COM3"
}

/// <summary>派生类把自己的 Config 映射到这里；全部为已校验的值。</summary>
public sealed class ClientChannelSettings
{
    public int HandshakeTimeout { get; set; }   public int SendTimeout { get; set; }   public int RequestTimeout { get; set; }
    public int LateReplyWindow { get; set; }    public int IdleTimeout { get; set; }   public int PartialFrameTimeout { get; set; }
    public int DisconnectTimeout { get; set; }  public int ReceiveBufferSize { get; set; } = 4096;
    public FramingOptions Framing { get; set; } = new();
    public CorrelationMode Correlation { get; set; }
    public bool ResetOnRequestTimeout { get; set; }
    public HeartbeatOptions Heartbeat { get; set; } = new();
    public ReconnectOptions Reconnect { get; set; } = new();
    public int ReceiveQueueCapacity { get; set; } = 1024;
    public QueueFullMode QueueFullMode { get; set; }
}
```

`ReconnectOptions` → `IBackoffPolicy` 的映射放在 Channels 的 internal `BackoffPolicyFactory`：`Enabled = false` 返回 null；`FixedInterval` 映射为 `FixedIntervalBackoff(Interval, MaxAttempts)`；`ExponentialBackoff` 映射为 `ExponentialBackoff(Interval, MaxInterval, 2.0, 0.2, MaxAttempts)`。`ChannelComponents.ReconnectPolicy` 非 null 时优先，但仍受 `Reconnect.Enabled` 控制。

### 9.2 Supervisor 固定算法

- `ConnectAsync`：已释放则抛 `ObjectDisposedException` → 获取生命周期锁（用户令牌，取消抛 OCE）→ 已 Connected 直接返回 `Success` → 处于 Reconnecting 时先取消并等待重连循环退出 → 状态改为 Connecting → `driver.OpenAsync`。成功：`Generation++`，状态改为 Connected，记录 `ConnectedSince`。用户取消：状态改为 Disconnected 后重新抛出。失败：重连已启用且 `OnInitialFailure` 时状态改为 Reconnecting 并启动重连循环，否则改为 Disconnected。返回失败结果。
- `DisconnectAsync`：已释放则直接返回 → 设置 `userDisconnected` → 取消并等待重连循环 → 获取锁 → 若 Connected：状态改为 Disconnecting，`driver.CloseAsync(UserRequested, drainTimeout)` → 状态改为 Disconnected（`UserRequested`）。下一次 `ConnectAsync` 清除 `userDisconnected`。
- `ReportConnectionLost`：代次不等于当前代次，或状态不是 Connected，则忽略；否则在后台执行：获取锁 → 再次校验 → `driver.CloseAsync(reason, 0)` → 重连已启用且非用户断开时状态改为 Reconnecting（带原因与异常）并启动循环，否则改为 Disconnected。
- 重连循环（后台，独立 CTS）：`attempt++` → `delay = policy.GetDelay(attempt)`；null 时获取锁、状态改为 Disconnected（`ReconnectExhausted`，`ReconnectAttempt = attempt − 1`）并退出 → `Task.Delay(delay, cts)` → 获取锁 → 已取消则退出 → `driver.OpenAsync(cts)`。成功：`Generation++`，`ReconnectCount++`，状态改为 Connected（`ReconnectAttempt = attempt`）。失败：记 Information 日志后进入下一轮。
- 状态事件：写入无界 `Channel<ConnectionStateChangedEventArgs>`，由单个派发任务**按顺序**、在**锁外**触发；每个订阅者单独 try/catch。
- `WaitForConnectedAsync`：当前已 Connected 立即返回 true；否则登记 `TaskCompletionSource`，在进入 Connected 时完成，超时返回 false。
- `DisposeAsync`：幂等 → 取消重连 → `driver.CloseAsync(Disposed, 0)` → 状态改为 Disposed → 等待事件派发任务结束。

### 9.3 StreamClientChannel 的固定流程

- `OpenAsync`（驱动实现）：`OpenStreamAsync` → 创建 `PendingRequestTable`、`FrameRouter`、`StreamChannel` 并启动 → `table.BeginHandshake()` → 若有 `IConnectionInitializer`，在 `TimeoutScope(HandshakeTimeout)` 内执行，传入的是绑定到这条新连接的内部 `IByteChannel` 视图（不检查 State，因为此时尚未进入 Connected）→ `EndHandshake()` 返回的积压转入派发队列 → 启动 `HeartbeatMonitor`（`Heartbeat.Enabled` 或 `IdleTimeout > 0` 时）。握手失败或超时则停止该连接并返回失败。
- `StreamChannel` 的 `onFault` 与 `HeartbeatMonitor` 的 `onDead` 都转为 `supervisor.ReportConnectionLost(打开时记录的代次, reason, ex)`。
- 公开的 `SendAsync` / `RequestAsync` / `ReceiveAsync`：State 不是 Connected 时立即返回 `NotConnected`；已释放时抛 `ObjectDisposedException`。
- 构造时校验：`Heartbeat.Enabled` 且既没有 `ChannelComponents.HealthProbe` 也没有 `Heartbeat.Payload` 时抛 `ArgumentException`（"Heartbeat.Payload is required when no IHealthProbe is supplied."）。
- `Dispose()` 同步等待 `DisposeAsync()`（D16）。

### 9.4 测试

`Junevy.Communication.Channels.Tests`：

- `SupervisorTests`：使用 `FakeConnectionDriver`，可以按脚本返回成功、失败、延迟，并记录调用。

| 测试名 | 断言 |
|---|---|
| `Connect_Success_TransitionsAndRaisesInOrder` | 事件序列 Disconnected→Connecting→Connected |
| `Connect_Failure_NoReconnect_ReturnsToDisconnected` | — |
| `Connect_Failure_OnInitialFailure_StartsReconnect` | 驱动第 3 次成功：最终 Connected，`OpenAsync` 共调用 3 次 |
| `Connect_UserCancel_Throws_AndDisconnected` | — |
| `Lost_WithReconnect_ReconnectsWithInterval` | 固定间隔 200 ms、前两次失败：Connected 出现在 600 ms 区间内 |
| `Lost_StaleGeneration_Ignored` | 用旧代次报告：状态保持 Connected |
| `Lost_Twice_HandledOnce` | 同一代次并发报告 10 次：`CloseAsync` 只调用 1 次 |
| `Reconnect_Exhausted_Disconnected` | `MaxAttempts=2`：原因 `ReconnectExhausted`，`ReconnectAttempt == 2` |
| `Disconnect_DuringReconnect_StopsLoop` | 之后 1 s 内 `OpenAsync` 不再被调用 |
| `Disconnect_ThenNoAutoReconnect` | 断开后报告丢失（旧代次）不触发重连 |
| `Connect_DuringReconnect_SupersedesLoop` | — |
| `StateChangedHandler_CallsDisconnect_NoDeadlock` | 在 Connected 事件处理器里调用 `DisconnectAsync`，2 s 内完成 |
| `WaitForConnected_TrueAndFalse` | — |
| `ConcurrentConnectDisconnectDispose_NoExceptions` | 50 轮随机并发调用，无未处理异常，最终状态为 Disposed |

- `HeartbeatTests`：

| 测试名 | 断言 |
|---|---|
| `ProbeFailsMaxTimes_ReportsHeartbeatFailed` | Interval=100、MaxFailures=3 |
| `ProbeSuccess_ResetsCounter` | 失败、失败、成功、失败、失败：不触发 |
| `OnlyWhenIdle_SkipsWhenTraffic` | 期间持续有收发时探测不被调用 |
| `IdleTimeout_ReportsIdle` | IdleTimeout=300：在区间内报告 `IdleTimeout` |
| `PayloadProbe_ExpectedReply_ExactMatch` | 收到不同内容判失败，收到相同内容判成功 |

- `StreamClientChannelTests`：派生一个测试类 `DuplexClientChannel : StreamClientChannel`，`OpenStreamAsync` 返回 `DuplexStreamPair.A`。

| 测试名 | 断言 |
|---|---|
| `SendWhenDisconnected_ReturnsNotConnectedImmediately` | < 50 ms |
| `Initializer_RunsBeforeConnected` | 初始化器执行期间 State 为 Connecting，事件中没有提前出现 Connected |
| `Initializer_PeerSendsFirst_BacklogClaimed` | 对端连上立即发帧，初始化器随后 `ReceiveAsync` 拿到该帧（D8） |
| `Initializer_Timeout_ConnectFails` | `HandshakeTimeout=300`：返回 `Timeout`，流被中止 |
| `Initializer_RerunsOnReconnect` | 重连后初始化器再次执行 |
| `HeartbeatWithoutPayloadOrProbe_Throws` | 构造时抛 `ArgumentException` |
| `Dispose_DuringInFlightRequest_ReturnsConnectionClosed` | 循环 20 次：请求返回 `ConnectionClosed`，无异常 |
| `AfterDispose_ConnectAsyncThrows` | `ObjectDisposedException` |

### 9.5 提交

`feat(channels): add connection supervisor, heartbeat and stream client channel base`

## 10. Task 8：TcpClientChannel

### 10.1 目标结构（固定）

```
Junevy.Communication.Tcp/
  Client/ITcpClientChannel.cs  Client/TcpClientChannel.cs  Client/TcpClientChannelConfig.cs  Client/TcpConnector.cs (internal)
  Options/TcpSocketOptions.cs  Options/TcpKeepAliveOptions.cs  Options/TcpChannelComponents.cs
```

公开 API 与设计文档 7.1 节一致；另按 D11 增加 `TcpClientChannelConfig.LateReplyWindow`（默认 -1，表示等于 `RequestTimeout`）。`TcpClientTlsOptions` 属性在本 Task 先加入配置类，`Enabled = true` 时 `ConnectAsync` 返回 `NotSupported`，Task 11 实现。

```csharp
public sealed class TcpSocketOptions
{
    public bool NoDelay { get; set; } = true;
    public int ReceiveBufferSize { get; set; }        // 0 = 系统默认
    public int SendBufferSize { get; set; }
    public int LingerTime { get; set; } = -1;         // -1 = 系统默认；0 = 关闭时立即发送 RST；> 0 = 秒
    public TcpKeepAliveOptions KeepAlive { get; set; } = new();
}

public sealed class TcpKeepAliveOptions
{
    public bool Enabled { get; set; } = true;
    public int Time { get; set; } = 30000;            // 空闲多久后开始探测（毫秒）
    public int Interval { get; set; } = 5000;         // 探测间隔（毫秒）
    public int RetryCount { get; set; } = 3;          // 只在 net8.0 生效；net472 使用系统默认（Windows 为 10 次）
}
```

### 10.2 固定算法（TcpConnector）

1. 地址：`Host` 能解析为 `IPAddress` 时直接使用；否则 DNS 解析（net8 用 `Dns.GetHostAddressesAsync(host, ct)`；net472 用 `Dns.GetHostAddressesAsync(host)` 与超时任务竞速），IPv4 地址排在 IPv6 之前。
2. 在 `ConnectTimeout` 总时限内依次尝试各地址：`new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)` → 应用选项 → 设置了 `LocalAddress` / `LocalPort` 时 `Bind` → 连接。net8 用 `ConnectAsync(endPoint, ct)`；net472 用 `FromAsync(BeginConnect/EndConnect)` + `Task.WhenAny` 超时竞速，超时时销毁 socket，并用 `ContinueWith(OnlyOnFaulted)` 观察被中止任务的异常（照搬 `ModbusTcpClient.OpenConnectionAsync` 的做法）。
3. KeepAlive：net8 设置 `SocketOptionName.KeepAlive`、`TcpKeepAliveTime`、`TcpKeepAliveInterval`（秒，向上取整）、`TcpKeepAliveRetryCount`；net472 用 `IOControl(IOControlCode.KeepAliveValues, 12 字节 {onoff, time ms, interval ms}, null)`。
4. 返回 `new NetworkStream(socket, ownsSocket: true)`。
5. `AbortTransport` 销毁 socket；`OnClosingAsync` 执行 `socket.Shutdown(SocketShutdown.Send)`（忽略异常）。
6. 错误归类：超时 → `Timeout`；连接被拒绝、主机不可达、DNS 失败 → `ConnectionClosed`（消息包含 `SocketError`）。

配置校验（D5）：`Port ∉ [1, 65535]`、`Host` 为空、各超时 < 0（`ConnectTimeout`、`SendTimeout`、`RequestTimeout` 必须 > 0）、`LocalPort ∉ [0, 65535]`、`LocalAddress` 不能解析为 IP 时抛 `ArgumentException`。

### 10.3 测试（项目 `Junevy.Communication.Tcp.Tests`，`SocketTiming` 集合）

| 测试名 | 断言 |
|---|---|
| `Connect_Success` | State 为 Connected；`RemoteEndPoint` 正确 |
| `Connect_Refused_ReturnsConnectionClosed` | 连接已关闭的端口 |
| `Connect_Timeout_ReturnsTimeoutWithinBudget` | 不可路由地址 `10.255.255.1`、`ConnectTimeout=500` |
| `Connect_InvalidHost_ReturnsConnectionClosed` | `nonexistent.invalid` |
| `Config_Invalid_Throws` | 10.2 列出的每种非法配置各一条 |
| `Config_NotMutated` | 构造前后配置对象的所有属性值不变（D5） |
| `LocalBinding_UsesLocalPort` | 服务端看到的来源端口等于 `LocalPort` |
| `Framing_Delimiter_EndToEnd` / `Framing_LengthField_EndToEnd` | 经真实 TCP 收发 |
| `ServerClosesConnection_Reconnects` | 固定间隔 300 ms：`ScriptedTcpServer.CloseConnection(0)` 后依次出现 Reconnecting、Connected，间隔落在区间内；服务端 `AcceptedConnectionCount == 2` |
| `SilentServer_HeartbeatFails_Reconnecting` | Interval=200、Timeout=200、MaxFailures=2，`ExpectedReply` 非空 |
| `IdleTimeout_Disconnects` | — |
| `RequestTimeout_Sequential_ResetsConnection` | 重连开启：超时后服务端看到第 2 个连接，下一次请求成功 |
| `Keyed_ConcurrentRequests_OverRealTcp` | 20 个并发请求，服务端乱序应答，全部匹配 |
| `SendTimeout_ServerNotReading` | 双方缓冲设为 4096、服务端不读、发 10 MB、`SendTimeout=500`：返回 `Timeout`，随后 State 离开 Connected |
| `Disconnect_NoAutoReconnect_SendFailsFast` | 重连开启时 `DisconnectAsync` 后 2 s 内服务端不再收到新连接；`SendAsync` 立即返回 `NotConnected` |
| `KeepAlive_AppliedWithoutError` | net8 读回 `TcpKeepAliveTime`；net472 只断言连接成功 |
| `Dispose_DuringRequest_Loop20` | 每次 3 s 内返回 `ConnectionClosed`，服务端 `ClosedByPeerCount` 递增 |
| `Tls_EnabledBeforeTask11_ReturnsNotSupported` | Task 11 完成时删除此测试 |

### 10.4 提交

`feat(tcp): add TCP client channel`

## 11. Task 9：TcpServer / TcpSession

### 11.1 目标结构（固定）

```
Junevy.Communication.Tcp/Server/
  ITcpServer.cs  TcpServer.cs  TcpServerConfig.cs  ITcpSession.cs  TcpSession.cs (internal)
  ServerState.cs  ServerStateChangedEventArgs.cs  TcpSessionEventArgs.cs  TcpSessionClosedEventArgs.cs
  TcpSessionFrameEventArgs.cs  IConnectionFilter.cs
```

公开 API 与设计文档 7.2 节一致。构造函数：`TcpServer(TcpServerConfig config, ILogger<TcpServer>? logger = null, TcpChannelComponents? components = null)`；`components` 的分帧、关联、初始化器、心跳探测作用于每个会话。`IConnectionFilter { bool Accept(IPEndPoint remote); }` 通过 `components` 的 Tcp 专属属性 `ConnectionFilter` 注入。

### 11.2 固定算法

- `StartAsync`：校验配置 → `Socket` 绑定 `ListenAddress:Port`（Windows 上 `ExclusiveAddressUse = true`）→ `Listen(Backlog)` → 启动接受循环 → State 为 Running。绑定失败返回 `Fail(ResourceExhausted 或 ConnectionClosed)`（端口占用为 `ResourceExhausted`），State 保持 Stopped。
- 接受循环：对每个接入的 socket 依次检查 `MaxSessions` → 白名单 → `IConnectionFilter`，不通过则立即关闭并记 Warning → 创建会话（自增 ID）→ 启动其 `StreamChannel` → `BeginHandshake` → 在 `SessionHandshakeTimeout` 内执行（TLS 由 Task 11 加入）初始化器 → `EndHandshake` → 加入会话表 → 触发 `SessionConnected` → 启动会话心跳。握手失败则关闭，不触发事件。
- 接受循环出现非停止原因的异常：State 改为 Faulted；`RestartOnFault.Enabled` 时按策略重新绑定监听，成功后回到 Running。
- 会话的 `onFault` / `onDead`：从会话表移除 → 停止 StreamChannel → 触发 `SessionClosed`（带原因）。
- 会话未认领帧：先触发会话自己的 `FrameReceived`，再触发服务端的 `FrameReceived`。
- `BroadcastAsync`：对快照中的所有会话并行 `SendAsync`，返回成功数。
- `StopAsync`：State 改为 Stopping → 关闭监听 socket → 在 `StopTimeout` 内并行关闭所有会话（原因 `UserRequested`）→ 等待接受循环退出 → State 改为 Stopped。
- internal 测试钩子 `SimulateListenerFault()`：销毁监听 socket，触发 Faulted 路径。

### 11.3 测试（`Junevy.Communication.Tcp.Tests`）

| 测试名 | 断言 |
|---|---|
| `Start_PortInUse_ReturnsResourceExhausted` | — |
| `Start_Stop_Restart` | — |
| `AcceptsMultipleClients_RaisesSessionConnected` | 5 个客户端 |
| `MaxSessions_RejectsExtra` | `MaxSessions=2`：第 3 个客户端 1 s 内被对端关闭，`SessionConnected` 只触发 2 次 |
| `Whitelist_RejectsOthers` | 白名单只含 `127.0.0.2`：来自 `127.0.0.1` 的连接被拒 |
| `SessionHandshake_Timeout_ClosesWithoutEvent` | 初始化器等待一个永远不来的帧 |
| `SessionHandshake_ClientSendsImmediately_BacklogClaimed` | D8 |
| `SessionClosed_RemoteClose_Reason` | 原因 `RemoteClosed` |
| `SessionIdleTimeout_Closes` | — |
| `SessionHeartbeat_Fails_Closes` | — |
| `Broadcast_ToTenSessions` | 返回 10，每个客户端收到 1 帧 |
| `ServerToSession_Request` | 服务端对会话 `RequestAsync`，客户端回复，服务端拿到应答 |
| `FrameReceived_SessionThenServer` | 两级事件都触发，会话级在前 |
| `Stop_WithActiveSessions_WithinStopTimeout` | 10 个会话：`StopAsync` 在 `StopTimeout` 内返回，客户端全部被关闭 |
| `ListenerFault_RestartOnFault_Recovers` | `SimulateListenerFault()` → Faulted → Running，之后能接受新连接 |

### 11.4 提交

`feat(tcp): add TCP server and sessions`

## 12. Task 10：ChannelFactory 与依赖注入（M1 收尾）

### 12.1 目标结构（固定）

```csharp
public interface IChannelCreator
{
    Type ConfigType { get; }
    IClientChannel Create(string name, IChannelConfig config, ChannelComponents? components, ILoggerFactory loggerFactory);   // 内部校验配置（D5）
}

public sealed class ChannelFactory : IDisposable, IAsyncDisposable    // 基于 NamedRegistry<IClientChannel>（D17）
{
    public ChannelFactory(IEnumerable<IChannelCreator> creators, ILoggerFactory? loggerFactory = null);
    public IClientChannel GetOrAdd(string name, IChannelConfig config, ChannelComponents? components = null);   // 按配置精确类型选择创建器，找不到抛 NotSupportedException
    public bool TryAdd(string name, IChannelConfig config, out IClientChannel? channel, ChannelComponents? components = null);
    public bool TryGet(string name, [NotNullWhen(true)] out IClientChannel? channel);
    public T GetRequired<T>(string name) where T : class, IClientChannel;
    public bool TryRemove(string name);
    public bool RegisterAlias(string aliasName, string existingName);
    public bool RemoveAlias(string aliasName);
    public IEnumerable<string> Keys { get; }
    public int Count { get; }
}

public sealed class ChannelFactoryBuilder     // 无 DI 容器的宿主
{
    public static ChannelFactoryBuilder Create();
    public ChannelFactoryBuilder WithLoggerFactory(ILoggerFactory loggerFactory);
    public ChannelFactoryBuilder WithCreator(IChannelCreator creator);
    public ChannelFactory Build();
}

// DI
services.AddChannels();        // TryAddSingleton<ChannelFactory>
services.AddTcpChannels();     // TryAddEnumerable(Singleton<IChannelCreator, TcpClientChannelCreator>)
```

`TcpClientChannelCreator`（公开，位于 Tcp 包）。Udp、Serial 的创建器与 DI 扩展在 Task 12、13 中加入。

### 12.2 测试

| 类 | 测试名 | 断言 |
|---|---|---|
| `ChannelFactoryTests` | `GetOrAdd_CreatesByConfigType` | — |
| | `UnknownConfigType_ThrowsNotSupported` | — |
| | `Alias_SharesInstance` | — |
| | `TryRemove_DisposesChannel` | — |
| | `Builder_And_DI_ProduceEquivalentFactories` | 两种方式都能创建 TCP 通道 |

### 12.3 M1 验收

1. 运行第 1 节三条命令。
2. scratchpad 新建两个控制台项目（`net8.0` 与 `net472`，`ProjectReference` 指向 Tcp），依次执行并打印结果：
   1. 启动 `TcpServer`（分隔符分帧，回显），客户端开启重连，收发 100 帧。
   2. `StopAsync` 服务端 2 s 后再次 `StartAsync`：客户端自动重连，再收发 10 帧。
   3. 客户端开启心跳，服务端改为不回心跳：客户端在预期时间内进入 Reconnecting。
3. 输出粘贴到合并请求描述。

### 12.4 提交

`feat(channels): add channel factory and DI registration`

## 13. Task 11：TLS（M2）

### 13.1 目标结构（固定）

```
Junevy.Communication.Tcp/Tls/
  TcpClientTlsOptions.cs  TcpServerTlsOptions.cs  CertificateSource.cs
  CertificateLoader.cs (internal)  TlsStreamFactory.cs (internal)
```

选项类与设计文档 7.3 节一致。`TcpChannelComponents` 增加 `X509Certificate2? ClientCertificate`、`X509Certificate2? ServerCertificate`、`RemoteCertificateValidationCallback? RemoteCertificateValidation`（优先级高于选项中的同类设置）。

### 13.2 固定算法

- `CertificateLoader`：有 `Thumbprint` 时先清洗（去掉所有非十六进制字符——从证书管理器复制的指纹常带不可见字符 U+200E），再从 `StoreLocation` / `StoreName` 按指纹查找（忽略大小写），找不到或不带私钥抛 `ArgumentException`；否则从 `PfxPath` 加载，密码读环境变量 `PfxPasswordEnvironmentVariable`（未设置该属性时视为无密码；设置了但环境变量不存在则抛 `ArgumentException`），使用 `X509KeyStorageFlags.DefaultKeySet`（Task 11 派发时修订：不用 `MachineKeySet`，导入机器密钥库通常需要管理员权限）。
- 握手时限：公开基类 `StreamClientChannel` 新增 `protected virtual Task<CommResult<Stream>> SecureStreamAsync(Stream, CancellationToken)`，在 `HandshakeTimeout` 计时窗口内、初始化器之前执行，两者共享同一时限（Task 11 派发时确定，满足"TLS 握手 + 初始化器总时限"）；服务端会话的 TLS 与初始化器同样共享 `SessionHandshakeTimeout`。
- 客户端：`TcpClientChannel` 覆写 `SecureStreamAsync`，把 `NetworkStream` 包装为 `SslStream`。net8 用 `AuthenticateAsClientAsync(SslClientAuthenticationOptions, ct)`；net472 用 `AuthenticateAsClientAsync(targetHost, clientCertificates, protocols, checkRevocation)`，超时由 `TimeoutScope` 的 `onAbort` 销毁 socket 打断。认证时间计入 `HandshakeTimeout`。
- 证书校验：有自定义回调时以回调为准；否则 `sslPolicyErrors == None` 才通过；`AllowUntrustedServerCertificate = true` 时任何错误都通过，但每次连接记一条 Warning（含错误类型与证书指纹）。
- 服务端：`AuthenticateAsServerAsync(serverCertificate, clientCertificateRequired, protocols, checkRevocation)`，计入 `SessionHandshakeTimeout`。
- 错误归类：`AuthenticationException` → `AuthenticationFailed`；超时 → `Timeout`；其他 I/O 异常 → `ConnectionClosed`。
- `IsTlsActive` 反映当前连接是否已完成 TLS。

### 13.3 测试

测试套件新增 `TestCertificates.CreateSelfSigned(string subject = "CN=localhost")`：RSA 2048、SAN 为 `localhost` 与 `127.0.0.1`、EKU 为 serverAuth + clientAuth，**导出为 PFX 再以 `Exportable` 重新导入**（Windows SChannel 不接受临时密钥，否则服务端认证报 "No credentials are available in the security package"）。

| 测试名 | 断言 |
|---|---|
| `Tls_EchoRoundTrip` | 客户端用自定义回调信任测试证书 |
| `Tls_UntrustedCertificate_AuthenticationFailed` | 默认校验 |
| `Tls_AllowUntrusted_SucceedsAndLogsWarning` | `TestLogger` 中有 Warning |
| `Tls_PinnedThumbprint_Callback` | 指纹匹配通过、不匹配失败 |
| `Tls_MutualRequired_NoClientCert_Fails` | 双端都报失败 |
| `Tls_MutualRequired_WithClientCert_Succeeds` | — |
| `Tls_HandshakeTimeout_AgainstSilentServer` | `HandshakeTimeout=500` 对 `SilentTcpServer`：返回 `Timeout`，耗时落在区间内 |
| `Tls_ReconnectRedoesHandshake` | — |
| `TlsServer_SessionHandshakeTimeout` | 客户端只建 TCP 不发 ClientHello：会话被关闭，不触发 `SessionConnected` |
| `CertificateSource_MissingEnvVar_Throws` | — |

### 13.4 提交

`feat(tcp): add optional TLS for client and server`

## 14. Task 12：串口（M3）

### 14.1 目标结构（固定）

```
Junevy.Communication.Serial/
  ISerialChannel.cs  SerialChannel.cs  SerialChannelConfig.cs  SerialChannelCreator.cs
  Internal/ISerialPortHandle.cs  Internal/SerialPortHandle.cs  Internal/ISerialPortHandleFactory.cs
  DependencyInjection/SerialServiceCollectionExtensions.cs      // AddSerialChannels()
```

公开 API 与设计文档第 8 节一致，按 D11 增加 `OpenTimeout`（默认 2000）。`SerialChannel : StreamClientChannel`，`PartialFrameAction` 覆写为 `Discard`。

```csharp
internal interface ISerialPortHandle : IDisposable
{
    Stream BaseStream { get; }
    bool IsOpen { get; }
    void Open();
    void DiscardInBuffer();
}

internal interface ISerialPortHandleFactory { ISerialPortHandle Create(SerialChannelConfig config); }
```

公开构造函数使用真实的 `SerialPortHandle`；internal 构造函数接收 `ISerialPortHandleFactory`，供测试注入基于 `DuplexStreamPair` 的假端口。

### 14.2 固定算法

- `OpenStreamAsync`：`Task.Run(() => handle.Open())` 与 `OpenTimeout` 竞速；超时返回 `Timeout`，并在 `Open` 最终完成时释放端口。打开后设置 `ReadTimeout = InfiniteTimeout`、`WriteTimeout = SendTimeout`（作为发送超时的后备）→ `DiscardInBuffer()` 清掉残留数据 → 返回 `BaseStream`。
- 打开失败（端口不存在、被占用、拒绝访问）返回 `ConnectionClosed`，消息包含端口名与原始异常消息。
- `AbortTransport`：关闭并释放端口（忽略异常）。
- **热拔插风险项**（设计文档第 8 节）：
  1. 本 Task 先用真实 USB 转串口做热拔插复现：打开状态下拔出，观察 net472 与 net8 进程是否崩溃。
  2. 若复现崩溃，按顺序尝试并记录结果：a) 打开后对 `BaseStream` 调用 `GC.SuppressFinalize`，关闭前 `GC.ReRegisterForFinalize`（社区已知方案）；b) 关闭端口前先停止填充循环，避免在端口失效后再发起读取。
  3. 把复现结论与采用的防护写进 CHANGELOG 和 Skill；若两种方案都无效，在 README 与 Skill 中写明限制，不阻塞本 Task。

### 14.3 测试（项目 `Junevy.Communication.Serial.Tests`）

使用假端口：

| 测试名 | 断言 |
|---|---|
| `Open_ConnectsAndDiscardsInBuffer` | `DiscardInBuffer` 被调用 |
| `Open_Throws_ReturnsConnectionClosed` | 假端口 `Open` 抛 `UnauthorizedAccessException` |
| `Open_Hangs_ReturnsTimeout` | 假端口 `Open` 阻塞 5 s、`OpenTimeout=300` |
| `IdleGap_DefaultFraming_EndToEnd` | — |
| `PartialFrame_Discard_Continues` | LengthField 分帧 + `PartialFrameTimeout` |
| `ReadIOException_ReconnectsByReopening` | 假端口读取抛 `IOException`：Reconnecting → Connected，工厂 `Create` 被调用 2 次 |
| `SharedChannel_ViaAlias_SequentialAcrossLogicalDevices` | 两个别名并发 `RequestAsync`：请求串行，各自拿到对应应答 |
| `QueueFull_DefaultDropOldest` | — |
| `Config_Invalid_Throws` | 波特率 ≤ 0、端口名为空、`DataBits ∉ [5,8]` 等 |

真实端口（`[SerialPairFact]`，只在设置了环境变量 `JUNEVY_SERIAL_PAIR=COMx,COMy` 时执行，否则跳过）：

| 测试名 | 断言 |
|---|---|
| `RealPair_RoundTrip_9600_And_115200` | — |
| `RealPair_IdleGap_FramesIntact` | 连续 100 帧，每帧之间间隔 50 ms，全部完整 |

### 14.4 提交

`feat(serial): add serial channel`

## 15. Task 13：UDP（M4）

### 15.1 目标结构（固定）

```
Junevy.Communication.Udp/
  IUdpChannel.cs  UdpChannel.cs  UdpChannelConfig.cs  UdpChannelCreator.cs
  DependencyInjection/UdpServiceCollectionExtensions.cs        // AddUdpChannels()
Junevy.Communication.Channels/Pipeline/DatagramChannel.cs (internal)
```

公开 API 与设计文档第 9 节一致，按 D11 增加 `MulticastLoopback`（默认 false）。`UdpChannel` 直接组合 `ConnectionSupervisor` + `DatagramChannel` + `HeartbeatMonitor`，不继承 `StreamClientChannel`。

### 15.2 固定算法

- 打开：`new UdpClient(AddressFamily)` → 设置 `ReuseAddress` → `Bind(LocalAddress:LocalPort)` → `EnableBroadcast` → 加入组播组（同时设置 `MulticastLoopback` 与 TTL）→ Windows 上执行 `Client.IOControl(unchecked((int)0x9800000C), new byte[4], null)` 关闭 `SIO_UDP_CONNRESET`（net472 恒执行；net8 仅在 `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)` 时执行）→ 定向模式解析 `RemoteHost`。不调用 `Connect`（D13）。
- 接收循环：net8 用 `ReceiveAsync(ct)`，net472 用 `ReceiveAsync()`（停止时释放 `UdpClient` 打断）。长度超过 `MaxDatagramSize` 的数据报丢弃并递增 `ProtocolErrors`；定向模式丢弃来源不等于远端的数据报并递增 `FramesDropped`；其余交给 `FrameRouter`（携带来源地址）。
- 发送：定向模式的 `SendAsync` 发往远端；非定向模式调用 `SendAsync` 返回 `InvalidRequest`（"Remote endpoint is required; use SendToAsync."）。发送受 `SendTimeout` 约束。
- `RequestAsync` / `RequestToAsync`：注册时传入 `expectedRemote`（非定向模式为目标地址，定向模式为远端）；超时后按 `RequestRetryCount` 原样重发，每次重发都重新计时，`Sequential` 模式在全部重发期间持有请求锁；某次重发期间收到之前请求的应答，同样视为成功。
- 接收循环异常（非停止原因）报告 `Error`，由 Supervisor 按重连策略重新绑定。

### 15.3 测试（项目 `Junevy.Communication.Udp.Tests`）

| 测试名 | 断言 |
|---|---|
| `Directed_Echo` | — |
| `Directed_FiltersOtherSources` | 另一个 peer 发来的数据报不触发事件，`FramesDropped == 1` |
| `Unconnected_RequestTo_TwoPeers` | 分别请求两个 peer，各自拿到正确应答 |
| `Unconnected_SendAsync_ReturnsInvalidRequest` | — |
| `RequestRetry_PeerIgnoresFirst_Succeeds` | `RequestRetryCount=2`：peer 收到 2 个数据报，请求成功 |
| `RequestRetry_Exhausted_Timeout` | 总耗时约 (重发次数 + 1) × `RequestTimeout` |
| `SendToClosedPort_ReceiveLoopSurvives` | 先向无人监听的端口发送，再与正常 peer 收发，仍然成功 |
| `OversizeDatagram_Dropped` | `MaxDatagramSize=100`，peer 发 200 字节 |
| `Multicast_Loopback` | `239.255.0.1`、`MulticastLoopback=true`；加入组播失败时跳过（条件执行） |
| `Heartbeat_SilentPeer_Reconnecting` | — |
| `Config_Invalid_Throws` | — |

### 15.4 提交

`feat(udp): add UDP channel`

## 16. Task 14：README、Skill、CHANGELOG、设计文档同步

1. 根 `readme.md`（英文）新增 "Communication Channels (preview)" 小节：包列表与依赖关系、TCP 客户端/服务端/TLS/UDP/串口各一段最小示例、"preview：P2 之前公开 API 可能调整"的说明。
2. 每个新包新增 `README.md`（英文）：用途、安装、示例、超时表摘录、平台说明（net472 降级行为）。
3. 新增 Skill `Skills/using-junevy-channels/SKILL.md`。frontmatter 的 description 写明触发场景（项目引用 Junevy.Communication.Tcp / Udp / Serial，或需要 TCP/UDP/串口通讯、心跳、重连、分帧）。正文必须包含：
   - 包选择与 DI 注册；
   - 状态机与 `DisconnectAsync` 语义；
   - 重连默认关闭；断开期间发送立即返回 `NotConnected`；
   - 事件在线程池线程上触发（WPF 需切回 Dispatcher）；
   - 分帧选择表；FTDI 延迟与 `GapTimeout`；Windows UDP 10054；
   - TLS 证书来源与"不允许明文密码"；
   - 与 Modbus 懒重连的差异；
   - 热拔插结论（Task 12）。
4. `CHANGELOG.md`：检查本计划新增节的条目完整、日期正确。
5. 设计文档同步：D7、D9、D11 已在编写本计划时写入设计文档（5.2、5.3、6.2、第 7–10 节）；实施中其他偏离设计的地方逐条写入对应章节，并在第 20 节审阅记录后追加"实施修订"表。

提交：`docs: add channel family docs, skill and design sync`

## 17. Task 15：知识库回写（不提交）

在 `Junevy.Communication.Wiki/zh/content/` 下：

| 操作 | 笔记 | 内容 |
|---|---|---|
| 新增 | `架构设计/通讯库总体架构（协议族）.md` | 协议族划分、包依赖规则、新协议归类指引 |
| 新增 | `架构设计/字节通道族内部结构.md` | Supervisor、StreamChannel 双循环（D7）、FrameRouter、线程与背压 |
| 新增 | `行为契约/通道重连心跳与超时.md` | 行为契约表、超时总表、迟到应答、握手积压、Dispose 契约 |
| 新增 | `API参考/通道与传输（TCP UDP 串口 TLS）.md` | 公开 API 与配置项 |
| 更新 | `变更与决策/设计决策记录.md` | 新增：协议族架构、后台主动重连与 Modbus 懒重连并存的理由、Q2（新模块只异步）、Q3（依赖规则放宽）、R1（Core / Channels 拆分）、D5（校验不回填）、D1（preview 版本） |
| 更新 | `Agent协作/开发约定与验收基线.md` | "同步 + 异步双轨"限定为 Modbus；依赖规则；新项目 props、0 警告、测试双目标 |
| 更新 | `项目概述/技术栈与仓库结构.md` | 新项目与包 |
| 更新 | `测试/测试工程与验收方式.md` | 新测试项目、测试套件、串口真机测试的环境变量 |
| 更新 | `Junevy.Communication.Wiki/知识库首页.md` | 分类索引挂入新增笔记，更新版本与基线 commit |

完成后运行并在汇报中贴出结果：

```bash
git status --short -- Junevy.Communication.Wiki
git diff --stat -- Junevy.Communication.Wiki
```

## 18. Task 16：收尾与验收

1. 全量回归：连续运行 3 次 `dotnet test Junevy.Communication.slnx -c Debug`，3 次结果一致（已知偶发用例按 Task 0 规则处理）；Modbus 测试数量与 Task 0 基线一致。
2. 0 警告：`dotnet build Junevy.Communication.slnx -c Release` 成功（新项目开启了 `TreatWarningsAsErrors`）。
3. 依赖规则检查：`dotnet pack -c Release` 后用 `unzip -p *.nupkg *.nuspec` 查看各包依赖：
   - Core 的依赖中**不出现** `System.IO.Pipelines` 和 `System.Threading.Channels`（R1）；
   - Serial 依赖 `System.IO.Ports`；
   - Tcp、Udp 只依赖 Channels。
4. 扩展性预演（scratchpad 控制台项目，只使用公开 API）：实现一个虚构的"行协议"客户端，包含：
   - `LengthField` 分帧 + `Keyed` 关联（自定义 `IFrameKeyExtractor`）；
   - `IConnectionInitializer`（登录报文）+ 自定义 `IHealthProbe`；
   - 对一个 `DeviceSimulator` 跑通连接、请求、断线重连、重新登录。

   记录写了多少行、是否需要修改库代码。需要修改库代码说明扩展点有缺口，必须在本计划内补齐后重新验收。
5. 把第 3、4 步的输出和 M1 验收脚本的输出粘贴到合并请求描述。
6. 提交：`chore: finalize channel family P1`。

## 19. 未纳入本计划的条目（只记录，不修改）

- `CHANGELOG.md` 中 Modbus 2.0 的小节标题仍是 `[Unreleased] — 分支 refactor/modbus-p3-architecture`，而 2.0.0 已合入 master。
- Modbus 测试工程只测 net8.0（新项目已改为双目标，Modbus 是否跟进留待 Modbus v3）。
- USB 串口重新插入后 COM 号变化的自动识别（按 VID/PID 查找端口）。
- UDP 上的 DTLS。
- **待办（用户确认，2026-10-10）**：USB 转串口热拔插的人工验证。用户当前没有串口硬件，有硬件后按 Task 12 执行报告中的检查清单进行（Task 14 写入 Skill），结论写入 CHANGELOG。
- WPF 调试页（设计文档 E3：暂不做）。
- `ByteTransform` / `WordOrder`（P2）、`RetryLoop`（按需）、敏感信息脱敏（P3）。
- `NamedRegistry<T>` 继承自 `ModbusConnectionManager` 的两处既有行为（Task 2 验收记录）：`TryAdd` / `GetOrAdd` 不加锁，与 `Dispose` 并发时，清空之后插入的实例不会被释放；`RemoveAlias` 删除别名链的中间一环时，其下游别名会悬空（`TryGet` 返回 false 并记 Error）。

## 20. 验收记录（执行过程中追加）

- Task 2：net472 测试需要 `build/Junevy.Communication.Tests.app.config` 中的 System.Memory 绑定重定向。原因是 SDK 10 自带的 .NET Framework 测试宿主（`sdk/10.0.401/TestHostNetFramework`）使用 System.Memory 4.0.5.0，而库依赖解析为 4.5.5（程序集 4.0.1.2）。重定向只作用于测试宿主，使测试运行在用户实际得到的 4.0.1.2 上。Task 15 写入知识库《测试工程与验收方式》。
- Task 2：`NamedRegistry.TryRemove` 改为出锁后释放实例（提交 718261d、93ff6e1）。慢释放与跨线程回调两个回归测试均已确认在旧实现上失败。
- Task 3：命名空间由审阅者确定——Channels 的 `Abstractions/`、`Models/`、`Options/` 公开类型统一放在 `Junevy.Communication.Channels`，`Framing/` 放在 `Junevy.Communication.Channels.Framing`；内置分帧器与编码器为 internal，经 `FrameCodecFactory` 获取。
- Task 3：设计缺陷修正——`IByteChannel` 删除 `IsConnected`（与 `IConnectable` 重复声明，导致经 `IClientChannel` 访问时报 CS0229），`ITcpSession` 自行声明；`IdleGap` 的 `TryDecode` 改为超过 `MaxFrameLength` 才抛出（原写"达到即抛"，恰好等于上限的帧无法交出）。设计文档 5.1、7.2 与本计划 5.2 已同步。
- Task 3：`MaxFrameLength` 的计量口径——`Delimiter` 按分隔符之前的内容计量（`KeepDelimiter=true` 时交付的帧最多为上限加分隔符长度），其余分帧器按线路上的整帧计量。该上限用于防止错位数据撑爆内存，差几个字节不影响这一作用，保持现状；Task 14 写入 README 与 Skill。
- Task 5：路由顺序改为"在途请求 → 迟到判定 → 接收等待者"；`FrameRouter.StopAsync(drainTimeout)` 停止时排空已收到的帧；`Keyed` 模式下 `RequestAsync` 忽略 `RequestOptions.Matcher`（按关联键匹配），`ReceiveAsync` 在所有模式下都使用 Matcher（HSMS 被动端靠它等待 Select.req）；`FrameRouter.StopAsync` 重复调用时等待同一次停止完成；空负载请求由 `StreamChannel` 以 `InvalidRequest` 拒绝；`Sequential` 超时且不重建连接时，在迟到窗口期内继续持有请求锁（写入 8.2）。`QueueFullMode.DropNewest` 映射为 `BoundedChannelFullMode.DropWrite`（`DropNewest` 会移除已入队的最新帧）。
- Task 6：对端关闭（EOF）或读取出错时，先处理完已收到的数据再报告故障（8.2 填充第 3 条、解析第 7 条）；测试套件新增 `NonCancellableStream`，用于在测试中模拟 net472 不响应取消令牌的流，验证 D7。`StreamChannel` 报告故障后不会自行停止——Task 7 的 Supervisor 必须在收到 `onFault` 后停止该通道，且不得在 `onFault` 回调中同步等待停止完成。
- Task 7：9.3 的顺序修正为先 `BeginHandshake()` 再启动 `StreamChannel`（否则解析循环可能在握手积压开启前派发掉已到达的帧）；Supervisor 记住"正在打开的连接"期间报告的故障，连接建立后立即按丢失处理；握手期间发生故障则本次打开失败；`DisconnectAsync` 在持有生命周期锁后再次停止重连循环；Reconnecting 状态下断开直接转为 Disconnected（UserRequested）；心跳探测返回 `NotConnected` 不计为失败；状态事件派发任务在首次状态变化时才启动；在状态事件派发上下文中 `DisposeAsync` 仍执行并等待关闭完成，只是不等待状态派发任务本身。
- Task 8：设计缺陷修正——心跳探测经 `RequestAsync` 发出，TCP 默认 `ResetOnRequestTimeout = true` 时第一次探测超时就断开连接，`MaxFailures` 失效（自定义 `IHealthProbe` 同样受影响）。改为 `HeartbeatMonitor` 在探测期间建立探测上下文，其中的请求超时不重建连接、按迟到窗口处理，失败由心跳计数（已知限制：探测上下文随探测代码的异步调用链流动，探测代码若调用其他通道的请求，那些请求同样不会触发重建；协议探测只访问本通道，可接受）。`TcpConnector` 返回 `Socket`，由通道包装为 `NetworkStream`（通道需要 socket 做中止、Shutdown 与端点查询）。
- Task 8：测试环境——Windows 回环会吸收单次超大写入（对不读取的对端，第一次写 10 MB 立即完成，第二次才阻塞），"对端不读"类测试需连续写入直到超时；`Connect_Refused` 在本机约 2 秒返回（系统行为）；`Connect_InvalidHost` 依赖 DNS 返回 NXDOMAIN，`Connect_Timeout` 依赖 10.255.255.1 不即时返回不可达。Task 9 沿用，Task 15 写入知识库《测试工程与验收方式》。
- Task 9：会话建立顺序调整为"入会话表 → 触发 SessionConnected 并等其派发完成 → EndHandshake 放出积压 → 启动心跳"，保证积压帧派发时会话已在表中、且 SessionConnected 先于该会话的任何 FrameReceived；代价是 SessionConnected 处理器阻塞会推迟该会话的积压。Channels 对第一方传输包开放 internal（`InternalsVisibleTo Junevy.Communication.Tcp`，Udp 同理），所有包统一版本发布；第三方扩展只能使用公开基类 `StreamClientChannel`。服务端自定义心跳改为 `TcpChannelComponents.SessionHealthProbeFactory` 按会话创建（共享的 `HealthProbe` 在服务端抛 `ArgumentException`；工厂只在启用心跳时、会话启动心跳时调用；工厂抛异常或返回 null 时该会话以 `Error` 关闭）；`TcpServerConfig` 增加 `ResetOnRequestTimeout`、`LateReplyWindow`。套接字选项的校验与应用抽到客户端、服务端共用的 internal `TcpSocketConfigurator`。
- Task 10：DI 扩展命名空间沿用 Modbus 约定（`Junevy.Communication.Channels.DependencyInjection`、`Junevy.Communication.Tcp.DependencyInjection`）；工厂按配置精确类型匹配创建器，同一类型注册多个创建器时抛 `ArgumentException`（与 ModbusFactory 不同）。心跳检测耗时语义：探测按 Interval 起始到起始调度，第 MaxFailures 次失败约在 `(MaxFailures − 1) × Interval + Timeout` 之后判定——Task 14 写入 README 与 Skill。Task 14 顺带修正 `TcpClientChannel` 的 XML 注释（`Config` 返回调用方对象，运行行为只用构造时快照）。
- M1 验收（2026-10-10，e3c1c66）：net8.0 与 net472 控制台三个场景全部通过——100 帧回显；服务端同端口重启后客户端 32–46 ms 内重连并继续收发；心跳连续 3 次失败后以 HeartbeatFailed 进入重连（约 1.6 s）。本机连接被拒绝约 2 s 才返回（系统行为）；验收端口应避开 Windows 动态端口范围 49152–65535。
- Task 11：`TcpChannelComponents` 增加 `ClientCertificate`、`ServerCertificate`、`RemoteCertificateValidation`（服务端用它校验客户端证书）。配置来源的客户端证书每次连接时加载、连接结束释放（重连可取到轮换后的证书）；服务端证书在构造 `TcpServer` 时加载、释放服务端时释放。构造期结构错误抛 `ArgumentException`（D5），连接期证书加载失败（找不到、无私钥、环境变量缺失、PFX 损坏）归类为 `InvalidRequest`。`AllowUntrustedServerCertificate` 开启时每次连接都记 Warning（设置了自定义校验回调时以回调为准，不记）。已知限制：断开时不发送 TLS close_notify（只对套接字 `Shutdown(Send)`）。测试证书两个目标统一用 `CertificateRequest` 生成（net472 4.7.2 起即有，执行者曾误判为不存在而改用 CAPI P/Invoke，验收时纠正）。
- Task 12：串口固定 `ResetOnRequestTimeout = false`（无法换连接，走迟到窗口）；`SerialChannelConfig` 增加 `HandshakeTimeout`（默认 5000）与 `DisconnectTimeout`（默认 1000）——执行者曾推断为"不限时"，验收时纠正（初始化器挂起不应只能靠取消令牌结束；断开时须排空已收到的帧）。UDP 同样增加这两项（Task 13）。缓冲区大小必须为偶数（`SerialPort` 规定）；`ReadBufferSize` 只设置驱动缓冲，不用作通道读取块大小。Serial 只用公开基类，未开放 Channels 的 internal。**待人工验证**：USB 转串口热拔插（检查清单见 Task 12 执行报告，Task 14 写入 Skill）；真实端口测试需设置 `JUNEVY_SERIAL_PAIR` 后运行。
- Task 12 验收确定的语义（所有客户端通道通用）：`DisconnectAsync` 是优雅断开，在 `DisconnectTimeout` 内排空已收到但未派发的帧；`Dispose` / `DisposeAsync` 是立即释放，不排空（通常发生在程序退出，此时再向事件处理器派发帧容易访问已释放的对象）。D16 的"最长等待 DisconnectTimeout + 1000"仍是同步 `Dispose()` 的等待上限。Task 14 写入 README 与 Skill。
- Task 13：`DatagramChannel` 与具体传输无关（内部 `IDatagramTransport`），数据报驱动放在 Channels（`InternalsVisibleTo Junevy.Communication.Udp`）；`ConnectionAttempt` 从 `StreamConnectionDriver` 搬出供两种驱动共用；`PendingRequest.MarkSent()` 支持逐次重发计时。UDP 帧事件在定向模式也携带来源地址；超过 `MaxDatagramSize` 的数据报丢弃（无法检测截断）；`ChannelComponents.FrameCodec` 对 UDP 非法；定向模式 `RequestToAsync` 发往其他地址返回 `InvalidRequest`，`SendToAsync` 允许；空负载 `SendAsync` 发送零长度数据报；非定向模式启用内置心跳必须提供 `HealthProbe`。验收修正：心跳探测期间未发出任何数据（等待请求锁）不计失败；UDP 与串口内置心跳必须配置 `ExpectedReply`；串口打开在 `OpenTimeout` 内重试 `UnauthorizedAccessException`（虚拟串口驱动关闭端口后释放有延迟，实测最长 16 ms）；TCP 连接超时测试改为先探测环境（路由变化后 10.255.255.1 立即返回不可达），Modbus 同类测试受 Q4 约束不改，记为环境依赖。
- 串口真机测试（COM20/COM21 虚拟对，审阅者运行）：单独运行两个目标均通过；同一测试内换波特率立即重开时偶发"拒绝访问"，已由上述打开重试修正；修正后（7e51d84）连续 3 轮、每轮两个目标、两个测试共 12 次全部通过。
- M4 验收（2026-10-10，7e51d84）：全量测试 Core 43、Channels 228、Tcp 115、Udp 32、Serial 57（+2 真机测试在未设置环境变量时跳过）均在 net8.0 与 net472 上通过，Modbus 405 通过；新项目 0 警告。
- Task 14：根 readme 新增通道族小节，5 个新包各有 README（打包进 nupkg），新增 Skill `using-junevy-channels`，文档示例 42 段全部编译验证；设计文档追加"20.1 实施修订（P1）"表。验收时补齐两项：设计 5.1 的文本扩展 `SendTextAsync` / `RequestTextAsync` / `ReceiveTextAsync`（计划漏列，默认 UTF-8，不追加分隔符）；公开基类配置 `ClientChannelSettings` 的超时默认值与 TCP 客户端对齐（5000 / 2000 / 2000 / 1000 / -1，原为 0 = 不限时，`ResetOnRequestTimeout` 基类默认保持 false）。
- Task 16 最终验收（2026-10-10，dc1e6e5）：连续 3 次全量回归结果一致、0 失败（Core 43、Channels 235、Tcp 116、Udp 32、Serial 57 + 2 跳过，均为 net8.0 与 net472；Modbus 405）；六个新库 Release 构建 0 警告；打包依赖全部符合规则（Core 不依赖 Pipelines / Channels，Tcp 与 Udp 只依赖 Channels，Serial 依赖 Channels + System.IO.Ports，均含 README，版本 1.0.0-preview.1）；串口真机测试（COM20/COM21）在最终代码上两个目标通过。**扩展性预演通过**：只用公开 API、不改库代码，实现带 LengthField 分帧、Keyed 关联、登录初始化器、自定义心跳的虚构行协议（协议代码 205 行），两个目标跑通并发请求匹配、断线重连并重新登录、心跳失败重连。
- 扩展性预演发现（P2 的输入，未处理）：文档缺口——LengthField 等模式的编码器原样发送（长度字段由协议写入）、Keyed 键提取器收到的字节、客户端自定义 `IHealthProbe` 的写法、`ITcpSession.CloseAsync` 与 `TcpServer.Sessions` / `TryGetSession`、直接构造时的 `ILogger` 注入、服务端会话初始化器、Testing 无 README、LengthField 参数默认值、`TcpServer` 可重启、`CommResult<T>.ToResult()`。API 问题——客户端自定义探测在通道构造前无法拿到通道引用（建议增加与 `SessionHealthProbeFactory` 对称的 `Func<IByteChannel, IHealthProbe>` 工厂）；Testing 是否对外可用。依赖卫生：net8.0 依赖组中的 `System.Memory`、`Microsoft.Bcl.AsyncInterfaces`、`System.IO.Pipelines`、`System.Threading.Channels` 在 net8.0 上可省略（按目标框架条件化引用）。
- Task 4：计划 6.1 勘误——`HostTcpAsync(..., out int port)` 不合法（async 方法不能有 out 参数），改为 `DeviceSimulator.HostTcp()` 返回 `DeviceSimulatorTcpHost`（含 `Port`、`AcceptedConnectionCount`、`DisposeAsync`）。
- 收尾补丁（2026-10-10，分支 `feat/channels-p1`，任务 A–E）：
  - 任务 A（统一的心跳探测工厂）：新增 `ChannelComponents.HealthProbeFactory`。客户端（TCP、UDP、串口）仅在启用心跳时，于第一次成功打开、启动心跳之前以通道自身调用一次，结果跨重连复用；工厂抛出或返回 null 使本次打开失败（`Unspecified`）。服务端每个会话启动心跳时调用一次。删除 `TcpChannelComponents.SessionHealthProbeFactory`。`HealthProbe` 与工厂互斥（构造即 `ArgumentException`）。测试先红后绿：Channels 三项、Tcp 迁移与新增项、Udp 一项、Serial 一项在修改前均确认失败（编译失败或运行失败，原因与预期一致）。变异检查三项均被测试捕获：工厂每次重连都调用（Channels 与 Tcp 的复用测试失败）、客户端互斥校验缺失（Channels、Tcp、Udp 的互斥测试失败）、工厂返回 null 未检查（`HealthProbeFactory_Throws_FailsOpen` 失败），之后均已还原。提交 `1dcc645`；补充测试（创建器复制、服务端工厂失败只关闭该会话）提交 `3bdf75a`。
  - 任务 B（文档缺口）：分帧编码规则与 `LengthField` 默认值、`Keyed` 键提取器收到的字节、客户端探测工厂、日志注入（直接构造、`WithLoggerFactory`、DI）、`ITcpSession.CloseAsync` 与 `Sessions` / `SessionCount` / `TryGetSession`、服务端会话初始化器与 64 帧积压、`TcpServer` 停止后可再次启动、`CommResult<T>.ToResult()`、UDP 无定向心跳。新增或修改的示例在临时控制台项目中编译，net8.0 与 net472 均 0 错误、0 警告，且与文档逐字一致。提交 `ffb4ff3`。
  - 任务 C（按目标框架精简依赖）：Core 的 `System.Memory`、`Microsoft.Bcl.AsyncInterfaces` 与 Channels 的 `System.Threading.Channels` 改为只在 net472 引用；`System.IO.Pipelines` 两个目标都保留。`dotnet pack -c Release` 后：Core 的 net8.0 依赖组只剩两个 `Microsoft.Extensions.*.Abstractions`；Channels 的 net8.0 依赖组为 Core + `System.IO.Pipelines`；两个包的 net472 依赖组不变。提交 `474d03f`。
  - 任务 D（文档同步）：设计文档 3.1、5.4、5.6、7.2、9、11.3 与 20.1 第 39–41 项，提交 `76f4fc7`。知识库的 API 参考、常见陷阱、行为契约、架构与技术栈笔记、设计决策记录已改写，**未提交**（见汇报）。
  - 任务 E（扩展性预演复跑）：预演项目的 `Bind` 变通改为 `HealthProbeFactory`，协议代码 205 行 → 199 行，库代码未改。net8.0 与 net472 各运行一次，场景全部通过：并发请求 20/20 匹配；断线后重连并重新登录，之后 5/5 匹配；心跳失败进入 Reconnecting（`HeartbeatFailed`）。
  - 验收：`dotnet build` Debug 与 Release、`dotnet test` Debug 全部成功。测试数：Core 43、Channels 239、Tcp 122、Udp 34、Serial 59（+2 跳过，未设置 `JUNEVY_SERIAL_PAIR`，未打开真实串口）、Modbus 405（net8.0）；新项目 0 警告（剩余警告全部来自既有的 Modbus、Modbus.Tests、Junevy.Communication.Test 与 wpftmp）。Channels、Tcp、Udp、Serial 在两个目标上各连续运行 3 次，结果一致。
  - 未解决（记录，未修改）：`TcpServer.TryGetSession` 的 `out ITcpSession?` 参数缺少可空特性，调用方需要自行判空（示例中使用 `is not null`）；设计文档没有 4.9 节（`ChannelComponents` 位于 5.6 节，已同步）。
  - 审阅者验收（2026-10-10）：
    - 重跑 `dotnet build` Debug 与 Release、`dotnet test` Debug，结果与上面的测试数一致，0 失败；审读任务 A 的代码（客户端驱动解析工厂、服务端按会话调用、互斥校验与 Payload / ExpectedReply 要求的放宽）与知识库 7 处改动，均与代码一致。
    - 处理"未解决"的第一项：`TryGetSession` 增加 `[NotNullWhen(true)]`，README 与 Skill 示例去掉 `is not null`，新增测试 `TryGetSession_FindsConnectedSession_AndForgetsClosedOne`（此前没有测试调用该方法）。net472 构建因此暴露 CS0436（Tcp 经 `InternalsVisibleTo` 同时看到自己与 Channels 的内部补丁特性），`Common.props` 仅在 net472 上抑制。变异检查：去掉实现上的注解后两个目标都报 CS8767，说明 net472 上补丁特性同样生效。设计 20.1 第 42 项。
    - 第二项（"4.9 节"）是审阅者派发任务时的笔误，5.6 节正确，无需处理。
    - 复跑全量时 `HeartbeatProbe_BlockedByRequestLock_IsNotCountedAsFailure`（net472）偶发失败一次，之后 41 次未复现。分析：线程池续延延迟达到 100 ms 的探测超时并持续数秒时，获得请求锁的探测来不及写出即被判为通道忙（按设计不计失败），判定不了死亡；人为限制线程池的实验中逻辑照常推进。只加宽测试余量（探测超时 300 ms、检查点 1200 ms、时限 15 s），变异检查确认测试仍能发现"通道忙计为失败"的回归。
