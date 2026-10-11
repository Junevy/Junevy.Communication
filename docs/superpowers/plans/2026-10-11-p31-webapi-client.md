# P3.1 实施计划：WebApi 客户端（REST + SOAP 主动上报 MES）

- 日期：2026-10-11；分支：`feat/webapi-p3`（已从 master `60eb98b` 建好）
- 执行者：Sonnet（high）；审阅者：Opus，逐 Task 验收。
- 依据：设计文档 `docs/superpowers/specs/2026-10-10-communication-core-design.md` 第 15、16、18、22 节；用户 2026-10-11 的决定（第 0 节 P3-D1 至 D4）。
- P3 的目标：MES 能向通讯库下发（3.2，HTTP 服务端），通讯库也能主动上报 MES（3.1，本计划）。第三方应用可以按阶段挂接自己的逻辑：**事件**用于旁观（日志、界面、统计、补传触发），**拦截器**用于干预（签名、换 token、改请求头）。
- 3.2 在 3.1 验收后另写计划。第 17 节列出 3.1 必须为 3.2 准备好的部分。
- 验收重点：功能完整、分层清晰、**代码质量**（第 2 节逐条检查）。

---

## 0. 已固定的设计决策（不同意的条目先在此表修改再执行）

| # | 决策 | 理由 |
|---|---|---|
| P3-D1 | 拆成两期：3.1 客户端（REST + SOAP，主动上报），3.2 HTTP 服务端（接收 MES 下发） | 用户 2026-10-11 决定；沿依赖边界切分，3.2 只复用 3.1 的公共契约 |
| P3-D2 | 新库 `Junevy.Communication.WebApi`（net472;net8.0，**只依赖 Core**）。**单程序集，按命名空间分层**，依赖方向由架构测试强制（第 3 节）。测试项目 `Tests/Junevy.Communication.WebApi.Tests` | 用户决定；拆成多个程序集会迫使内部管线公开（Q16 禁止新增 InternalsVisibleTo） |
| P3-D3 | 阶段钩子 = **事件（旁观）+ 拦截器（干预）**。事件：`RequestSending`、`ResponseReceived`、`RequestCompleted`、`AvailabilityChanged`。拦截器：每次发送尝试前（`OnSendingAsync`）与收到响应后（`OnReceivedAsync`）调用，可修改请求、中止调用、请求重放一次（规则见 Task 4、Task 6） | 用户 2026-10-11 需求；事件不能 await、不能修改请求，签名与 token 刷新必须在每次尝试前执行 |
| P3-D4 | 认证：内置 Basic / Bearer / ApiKey，**以拦截器实现**（不再设计 `IAuthenticationProvider`）。配置 `WebApiAuthOptions` 只保存环境变量名，构造时读取；代码注入用 `WebApiAuthentication` 的静态工厂。登录换 token、签名等由宿主写拦截器 | 用户决定；一种机制覆盖认证与签名，修订设计 15.2 |
| P3-D5 | 离线缓存：本期**不公开** `IOutboxStore` / `OutboxItem`。预留的是让以后能非破坏性加入离线缓存的前提：① `WebApiRequest.IdempotencyKey`；② 管线基于完整、可重放的内部请求快照（`HttpRequestSnapshot`）发送；③ 拦截器在每次尝试前执行（补发时会重新签名）；④ `AvailabilityChanged` 事件（补传的触发信号） | 没有调用方的公开接口是空 API（Q8），一旦公开就不能再改；接口应该和它的使用者一起设计。修订设计 15.6 的"只有接口" |
| P3-D6 | 命名空间与依赖方向（第 3 节表格）：根命名空间放公共契约；`Serialization`、`Auth` 只依赖根；`Http`（全部 internal）依赖根与 `Auth`；`Rest` 依赖根、`Serialization`、`Http`；`Soap` 依赖根与 `Http`；`Rest` 与 `Soap` 互不引用；`Factory`、`DependencyInjection` 在最上层 | 用户要求"分好层"；REST 与 SOAP 只负责编码请求与解析响应，其余全部共用 |
| P3-D7 | REST 与 SOAP 共用一个内部核心 `EndpointCore`（`Http` 命名空间）：配置快照、HttpClient、拦截器链、尝试循环、重试、超时、日志、可用性、健康检查、事件、释放。`WebApiClient` 与 `SoapClient` 是薄外观，只构造请求快照并解释响应 | Q2；P2.5 的 `ClientOptionsCore` 先例 |
| P3-D8 | 请求事件在调用自身的执行流上**同步触发**；公开的 `SendAsync` / `InvokeAsync` 用 `Task.Run(..., CancellationToken.None)` 进入管线，所以拦截器与事件总在线程池线程上运行（设计第 22 节）。每个订阅者单独捕获异常并记 Error 日志（Core `EventSubscribers.InvokeEach`）。`SendAsync` 返回时，`RequestCompleted` 的处理器已经执行完毕。`AvailabilityChanged` 经单消费者队列按顺序触发（与 `ConnectionSupervisor` 的状态事件相同） | 事件确定、可测；并发请求与健康检查同时改变可用性时，状态事件不会乱序 |
| P3-D9 | 每次调用恰好触发一次 `RequestCompleted`（包括校验失败、拦截器中止；此时 `Attempts = 0` 或实际次数）。释放之后的调用返回 `NotConnected`，不触发事件 | 宿主可以只订阅一个事件就完整记录每次上报 |
| P3-D10 | 重试（`WebApiRetryOptions`）：`RetryCount` 默认 0，`RetryDelay` 默认 1000 ms（`FixedIntervalBackoff`；`WebApiComponents.RetryBackoff` 可覆盖）。可重试的结果：网络层失败（`ConnectionClosed`）、尝试超时、HTTP 408 / 429 / 500 / 502 / 503 / 504，且没有 `RemoteCode`（SOAP Fault 与业务包络失败**永不重试**）。方法规则：GET / HEAD / OPTIONS 自动可重试；POST / PUT / PATCH / DELETE 只有设置了 `IdempotencyKey` 或 `RetryUnsafeMethods = true` 时才重试。429 / 503 带 `Retry-After` 时取 max(退避, Retry-After)，超过 30000 ms 则不再重试 | 设计 15.3；MES 的 PUT / DELETE 未必幂等，过站与产量重复计数的代价远大于少一次重试，因此比 RFC 9110 更保守 |
| P3-D11 | 超时按**每次尝试**计算：覆盖 `OnSendingAsync` 拦截器、HTTP 收发（含读完响应体）与 `OnReceivedAsync` 拦截器。`HttpClient.Timeout = Infinite`，用 Core 的 `TimeoutScope` 区分超时与用户取消。`WebApiRequest.Timeout` / `SoapCallOptions.Timeout` 覆盖配置值。不设整体超时 | 拦截器取 token 也可能卡住，必须受超时约束；与通道族"按请求超时"一致 |
| P3-D12 | HttpClient：每个客户端实例一个长期存活的 HttpClient。net8 用 `SocketsHttpHandler`（`PooledConnectionLifetime` 5 分钟、`MaxConnectionsPerServer`、`AllowAutoRedirect = false`、GZip / Deflate 解压）；net472 用 `HttpClientHandler`（同样的重定向与解压设置）+ 该端点的 `ServicePoint`（`ConnectionLeaseTimeout`、`ConnectionLimit`）。每个请求设 `ExpectContinue = false`。`WebApiComponents.HttpHandler` 可以注入处理器（代理、客户端证书、测试），注入的处理器**不由客户端释放**。不修改任何进程级静态设置（`ServicePointManager` 的静态属性禁止修改） | 设计 15.3；net472 默认每主机只有 2 个连接、POST 默认带 100-continue（每次多等 350 ms）；自动重定向会把 POST 静默改成 GET 并丢掉正文 |
| P3-D13 | 结果类型 `WebApiResult<T>`（设计 15.1，增加 `Exception` 诊断属性），公开工厂方法。错误归类沿用设计 15.7，补充：3xx → `RemoteError`（不自动重定向）；响应体超过 `MaxResponseBodySize` → `ProtocolViolation`；拦截器抛异常 → `Unspecified`（记 Error，不重试）；拦截器调用 `Fail` → 指定的分类；请求参数非法 → `InvalidRequest`；释放后调用 → `NotConnected`（消息 `The WebApi client has been disposed.`）；释放时仍在途的请求 → `ConnectionClosed` | P2-D13、P2-D14 先例：运行期不抛异常 |
| P3-D14 | Core 新增公开静态类 `Junevy.Communication.Core.Diagnostics.EventSubscribers`（`InvokeEach`），由 Channels 的 `LifecycleSupport.InvokeEach` 移入；Channels 的 `LifecycleSupport.InvokeEach` 与 `FrameRouter.RaiseToSubscribers` 删除并改用它（行为不变；FrameReceived 订阅者异常的日志文本统一为通用文本，记入 CHANGELOG 变更） | WebApi 只依赖 Core，需要同一个逐订阅者派发；顺带消除 Channels 内已有的重复 |
| P3-D15 | JSON：`System.Text.Json`（net472 引用 NuGet 包 8.0.5，net8 内置）。默认选项：属性名 camelCase、读取时不区分大小写、`JavaScriptEncoder.Create(UnicodeRanges.All)`（中文不转义成 `\uXXXX`）。`IWebApiSerializer` 可替换（设计 Q8）；业务包络的字段定位始终用 `JsonDocument`，与替换的序列化器无关 | 设计 21.1 Q3、Q8；部分 MES 处理不了转义的中文，日志也无法阅读 |
| P3-D16 | 配置继承：`WebApiEndpointConfig`（抽象基类，REST 与 SOAP 共有的字段）← `WebApiClientConfig`（REST）/ `SoapClientConfig`（SOAP）。构造时校验一次并拍快照，之后修改配置对象不生效（与通道族一致） | Q2；设计第 22 节命名规则 |
| P3-D17 | 安全：`WebApiRequest.Path` 必须是相对路径，绝对 URL → `InvalidRequest`（认证头不能被带到别的主机）；拦截器修改 `RequestUri` 时 scheme 与 authority 必须不变，否则调用以 `InvalidRequest` 失败；解析 SOAP / XML 响应时 `DtdProcessing.Prohibit`、`XmlResolver = null`；日志对 `RedactedHeaders`、拦截器标记为敏感的头、`RedactedQueryParameters` 只输出 `***` | 凭据不出站、防 XXE、日志脱敏（设计 15.3、第 22 节） |
| P3-D18 | 架构测试：`Tests/Junevy.Communication.Architecture.Tests` 引用 **NetArchTest.Rules 1.3.2**（仅测试项目，MIT），检查 WebApi 内部命名空间的依赖方向（含方法体中的引用）；分层表新增 WebApi 行（只允许引用 Core） | 命名空间分层需要检查方法体，反射做不到；第三方包只出现在测试项目，不违反设计 Q3 |

---

## 1. 执行规则（每个 Task 都适用）

1. 每个 Task 一个 commit（提交信息见各 Task），在 `feat/webapi-p3` 上提交。不 push、不 amend、不 rebase、不切换分支。**只 `git add` 本 Task 的文件。**
2. 步骤顺序固定：**先写测试并确认失败**（新代码阶段无法编译也算失败）→ 写生产代码 → 测试通过 → 按第 2 节自查并清理 → 更新文档 → 提交。Task 1 的 Channels 部分是"行为不变"，改用第 4 条。
3. 每个 Task 完成后依次运行，全部成功才能提交：
   ```bash
   dotnet build Junevy.Communication.slnx -c Debug
   dotnet build Junevy.Communication.slnx -c Release
   dotnet test Junevy.Communication.slnx -c Debug
   ```
   每个测试项目在 net8.0 与 net472 上各运行一次（Modbus 只有 net8.0）。内存紧张导致测试宿主崩溃（"Releasing the double mapped memory failed"、启动超时 90 秒）时，先运行 `dotnet build-server shutdown` 再重跑；**不要**把这类崩溃当作测试失败去改代码，也不要当作通过。
4. **行为不变的证据**：`git diff --stat <本 Task 开始时的 HEAD> -- Tests` 中已有文件的改动为 0（可以新增测试文件）；测试数量只增不减；三条命令全绿。
5. 代码约束沿用 P1、P2、P2.5：只异步 API、库内每个 `await` 都 `ConfigureAwait(false)`、文件作用域命名空间、英文错误消息与日志、中文 XML 注释、日志模板为常量（CA2254）、net472 禁用清单（无 `SequenceReader<T>`、无 `Enumerable.Append`、无默认接口成员）。条件编译只用 `#if NET8_0_OR_GREATER`，并且集中在一个内部文件里（Task 5 的 `HttpHandlerFactory`）。
6. CHANGELOG：在 `CHANGELOG.md` 顶部新增一节 `## [Unreleased] — WebApi P3.1（WebApi 1.0.0-preview.1；Core 新增 EventSubscribers）`，下设 `### 新增（Added）`、`### 变更（Changed）`，每个 Task 的条目写入对应小节，格式 `- YYYY-MM-DD [包名] 内容`。
7. 不修改：`Junevy.Communication.Modbus/`、`Tests/Junevy.Communication.Modbus.Tests/`、`Skills/using-junevy-modbus/`。Channels 的生产代码只允许 Task 1 修改；Tcp、Udp、Serial、Plc、Melsec 的生产代码本期不改。
8. 知识库：只在 Task 12 回写，**不提交**；汇报时附 `git status --short -- Junevy.Communication.Wiki` 与 `git diff --stat -- Junevy.Communication.Wiki`。
9. 本计划之外的问题记入第 18 节，只记录不修改。执行中偏离计划的地方（包括发现计划写错）记入第 19.2 节并在汇报中说明；需要审阅者决定的停下来汇报，不要自行改设计。
10. 每个 Task 的汇报包含：新增 / 修改 / 删除文件；测试先红后绿的证据；第 2 节逐条自查（"符合 / 不符合 + 说明"）；本 Task 涉及的第 4 节度量；三条命令的结果（每个项目两个目标的通过 / 失败 / 跳过数）。
11. 本期没有串口测试。万一需要运行全部测试中的串口用例：只允许 COM20 / COM21（`JUNEVY_SERIAL_PAIR=COM20,COM21`），**绝不打开 COM3 / COM4**。
12. 套接字测试（Task 10）放进已有的 `SocketTiming` 集合约定（同一集合内串行），耗时断言用区间，不用 `Thread.Sleep`。

---

## 2. 代码质量标准（审阅者逐条验收，不符合即打回）

P2 计划第 2 节 Q1–Q15 与 P2.5 计划第 2 节 Q16–Q20 全部适用（文件 ≤ 300 行、方法 ≤ 60 行、不重复、依赖方向、可见性最小化、无魔法数、命名、注释、无死代码、异步、错误处理、资源、入口校验、测试覆盖、测试确定、测试干净、不新增生产 → 生产 IVT、公开 API 只增加列出的成员、抽取后删除旧代码）。补充：

- Q21 **分层**：每个类型所在的命名空间与第 3 节表格一致；不允许为了绕过分层把类型放进根命名空间。
- Q22 **REST 与 SOAP 不重复**：两者共有的行为（超时、重试、拦截器、事件、日志、可用性、释放、配置校验）只在 `Http` 与根命名空间实现一次；`WebApiClient.cs` 与 `SoapClient.cs` 各自不超过 200 行。
- Q23 **钩子契约可测**：第 6 节的事件顺序、拦截器顺序与重放规则逐条有测试。
- Q24 **不碰进程级状态**：不修改 `ServicePointManager` 的静态属性、不设置全局 `JsonSerializerOptions`、不注册全局证书回调。

---

## 3. 命名空间与依赖方向（架构测试强制，P3-D6）

| 命名空间 | 内容 | 允许依赖（WebApi 内部） |
|---|---|---|
| `Junevy.Communication.WebApi` | 公共契约：`WebApiResult<T>`、`WebApiAvailability`、`IWebApiEndpoint`、`WebApiEndpointConfig` 与嵌套选项、`ResponseEnvelopeOptions`、`WebApiComponents`、`IWebApiSerializer`、拦截器接口与上下文、事件参数 | 无 |
| `….WebApi.Serialization` | `SystemTextJsonWebApiSerializer`；internal 业务包络读写器 | 根 |
| `….WebApi.Auth` | `WebApiAuthentication`；internal 的 Basic / Bearer / ApiKey 拦截器与"从配置创建"的逻辑 | 根 |
| `….WebApi.Http` | 全部 internal：`EndpointCore`、请求快照、尝试循环、状态码归类、重试规则、日志脱敏、可用性、健康检查、处理器工厂 | 根、`Auth` |
| `….WebApi.Rest` | `IWebApiClient`、`WebApiClient`、`WebApiClientConfig`、`WebApiRequest`、`WebApiClientExtensions`；internal 响应解码 | 根、`Serialization`、`Http` |
| `….WebApi.Soap` | `ISoapClient`、`SoapClient`、`SoapClientConfig`、`SoapVersion`、`SoapCallOptions`；internal 信封读写与 XmlSerializer 缓存 | 根、`Http` |
| `….WebApi.Factory` | `WebApiClientFactory`、`WebApiClientFactoryBuilder` | 根、`Rest`、`Soap` |
| `….WebApi.DependencyInjection` | `WebApiServiceCollectionExtensions` | 根、`Factory` |
| `….WebApi.Server`（3.2） | — | 3.2 计划确定（预期：根、`Serialization`、`Auth`） |

架构测试对每一行断言"该命名空间（精确匹配，不含子命名空间）的类型不依赖表中未列出的 WebApi 命名空间"。编译器生成的嵌套类型（闭包、异步状态机）属于其外层类型的命名空间，自然参与检查。

---

## 4. 度量（Task 0 记录基线，Task 13 复测）

生产代码范围在 P2.5 的基础上加入 `Junevy.Communication.WebApi`（排除 `obj/`、`bin/`）。

| 指标 | 命令 | 基线 | P3.1 目标 |
|---|---|---|---|
| 重复窗口（6 行） | `python build/metrics/dupscan.py 6 <文件>` | Task 0 实测（P2.5 结束时 24） | 不增加；WebApi 内部只允许两个外观的公开转发样板（实测 9 个窗口：`Name`、`Availability` 与四个事件的 add / remove；里程碑 3 审阅裁决：公开类无法共享这些转发，不为度量调整代码顺序），WebApi 与其他程序集之间为 0 |
| 超过 60 行的方法 | `python build/metrics/methodlen.py 61 <文件>` | 0 | 0 |
| 超过 300 行的文件 | `wc -l` | 9 | 不增加（WebApi 的文件全部 ≤ 300 行） |
| 生产 → 生产 IVT | 架构测试 | 2 | 2 |
| WebApi 有效行 | `python build/metrics/effective_lines.py Junevy.Communication.WebApi` | — | 记录（供 3.2 对比） |

---

## 5. Task 0：基线

1. 运行 `dotnet test Junevy.Communication.slnx -c Debug`，记录每个项目两个目标的通过 / 失败 / 跳过数（预期：Core 112、Channels 278、Tcp 129、Udp 38、Serial 63 + 2 跳过、Plc 152、Melsec 184 + 1 跳过、Architecture 2，均为两个目标；Modbus 405）。
2. 按第 4 节记录度量基线。
3. 写入第 19.3 节，`git commit -m "chore: record the P3.1 baseline"`（包含本计划文件的基线记录）。

---

## 6. Task 1：Core 事件派发辅助（P3-D14）

**公开 API（固定）**

```csharp
namespace Junevy.Communication.Core.Diagnostics;

/// 逐个调用事件订阅者：单个订阅者抛出的异常只记 Error 日志，其余订阅者照常收到事件。
public static class EventSubscribers
{
    public static void InvokeEach<TArgs>(EventHandler<TArgs>? handler, object sender, TArgs args, ILogger logger, string eventName)
        where TArgs : EventArgs;
}
```

1. 先写 `Tests/Junevy.Communication.Core.Tests/EventSubscribersTests.cs`：null 处理器无操作；三个订阅者中第二个抛异常，第一、三个都收到，且记录一条 Error（用 Testing 的 `TestLogger` 或测试内的记录器）；`eventName` 出现在日志的结构化参数中。
2. 实现 Core 版本（日志模板沿用 `LifecycleSupport.InvokeEach` 的现有文本）。
3. Channels：删除 `LifecycleSupport.InvokeEach` 与 `FrameRouter.RaiseToSubscribers`，全部调用点（Channels、Tcp、Udp 中的 10 处，用 `grep -rn "InvokeEach\|RaiseToSubscribers"` 核对）改用 `EventSubscribers.InvokeEach`，FrameReceived 的 `eventName` 传 `"FrameReceived"`。Tcp、Udp 只改调用点这一行，属于本条允许的例外。
4. 这一步是"行为不变"：已有测试文件不改（第 1 节第 4 条）。如果有已有测试断言了旧的 FrameReceived 日志文本，停下来汇报。
5. CHANGELOG：新增（Core `EventSubscribers`）；变更（Channels 改用 Core 的派发辅助，FrameReceived 订阅者异常的日志文本统一）。
6. 提交：`refactor(core): move per-subscriber event dispatch into Core`

---

## 7. Task 2：项目骨架、结果类型与架构测试

**新增**
- `Junevy.Communication.WebApi/Junevy.Communication.WebApi.csproj`：Import `..\build\Junevy.Communication.Common.props`；`ProjectReference` Core；net472 条件下 `PackageReference System.Text.Json 8.0.5` 与 `<Reference Include="System.Net.Http" />`；`InternalsVisibleTo` 只给 `Junevy.Communication.WebApi.Tests`；`README.md` 打包（Task 12 写正文，本 Task 只放标题与一句定位）。
- `Tests/Junevy.Communication.WebApi.Tests/…csproj`：Import `Tests.props`，引用 WebApi 与 `Junevy.Communication.Testing`。
- `Junevy.Communication.slnx` 加入两个项目。
- net472 测试宿主：System.Text.Json 8.0.5 会带来 `System.Runtime.CompilerServices.Unsafe`、`System.Text.Encodings.Web`、`System.Buffers` 等程序集版本冲突。运行时出现 `FileLoadException` 时，在 `build/Junevy.Communication.Tests.app.config` 中补绑定重定向（只加实际需要的条目，并在汇报里列出）。

**公开 API（固定）**

```csharp
namespace Junevy.Communication.WebApi;

public enum WebApiAvailability { Unknown = 0, Online = 1, Offline = 2 }

public sealed class WebApiResult<T>
{
    public bool IsSuccess { get; }
    public T? Data { get; }
    public int? StatusCode { get; }          // 没有收到 HTTP 响应时为 null
    public string? RemoteCode { get; }       // 业务包络的 code，或 SOAP Fault 的 faultcode / Code
    public string? ErrorMessage { get; }     // 英文；成功时为 null
    public CommErrorKind ErrorKind { get; }
    public Exception? Exception { get; }     // 仅供诊断
    public static WebApiResult<T> Success(T? data, int? statusCode);
    public static WebApiResult<T> Fail(string message, CommErrorKind kind, int? statusCode = null, string? remoteCode = null, Exception? exception = null);
    public WebApiResult<TOther> As<TOther>();   // 失败转换；对成功结果调用抛 InvalidOperationException
    public override string ToString();          // 与 CommResult 相同的格式，另附 "HTTP 503" / "remote code X"
}
```

`Fail` 的参数校验与 `CommResult.Fail` 一致（message 为 null 抛 `ArgumentNullException`，kind 为 `None` 抛 `ArgumentException`）。

**架构测试**（P3-D18）
- 分层表加一行：WebApi 只允许引用 Core；`ProductionAssemblies` 自动包含它。
- 新增 `WebApiLayeringTests`：按第 3 节表格逐行断言（NetArchTest.Rules；命名空间用正则精确匹配）。本 Task 只有根命名空间有类型，其余行在后续 Task 加入类型后自动生效。
- **证明测试有效**：临时在根命名空间的某个类型里引用一个 `Http` 命名空间的类型，确认测试失败，再撤销；在汇报中贴出失败输出（审阅者会复做）。

测试：`WebApiResultTests`（工厂、校验、`As`、`ToString` 的各种组合）。

提交：`feat(webapi): add the WebApi project, result type and layering tests`

---

## 8. Task 3：共享配置与校验

**公开 API（固定）**（根命名空间）

```csharp
public abstract class WebApiEndpointConfig
{
    public int Timeout { get; set; } = 10000;                    // 每次尝试（P3-D11）；> 0
    public WebApiAuthOptions Auth { get; set; } = new();
    public Dictionary<string, string> DefaultHeaders { get; set; } = new();
    public WebApiRetryOptions Retry { get; set; } = new();
    public int HealthCheckInterval { get; set; } = 30000;        // > 0；只在配置了健康检查地址时生效
    public int OfflineThreshold { get; set; } = 3;               // >= 1
    public WebApiLogOptions Logging { get; set; } = new();
    public int MaxResponseBodySize { get; set; } = 4194304;      // > 0，字节
    public int MaxConnectionsPerServer { get; set; } = 10;       // >= 1
    public string? IdempotencyHeaderName { get; set; } = "Idempotency-Key";   // null 或空表示不发送
}

public sealed class WebApiRetryOptions
{
    public int RetryCount { get; set; }                          // >= 0，默认 0
    public int RetryDelay { get; set; } = 1000;                  // >= 0
    public bool RetryUnsafeMethods { get; set; }                 // 默认 false（P3-D10）
}

public sealed class WebApiLogOptions
{
    public bool LogBodies { get; set; }
    public int MaxLoggedBodyLength { get; set; } = 2048;         // >= 0
    public string[] RedactedHeaders { get; set; } = { "Authorization", "Proxy-Authorization" };
    public string[] RedactedQueryParameters { get; set; } = { "access_token", "token", "api_key", "apikey" };
}

public enum WebApiAuthScheme { None = 0, Basic = 1, Bearer = 2, ApiKey = 3 }

public sealed class WebApiAuthOptions
{
    public WebApiAuthScheme Scheme { get; set; }
    public string? UserNameVariable { get; set; }                // Basic
    public string? PasswordVariable { get; set; }                // Basic
    public string? TokenVariable { get; set; }                   // Bearer
    public string ApiKeyHeader { get; set; } = "X-API-Key";      // ApiKey
    public string? ApiKeyVariable { get; set; }                  // ApiKey
}

public sealed class ResponseEnvelopeOptions
{
    public bool Enabled { get; set; }
    public string CodePath { get; set; } = "code";               // 点分路径，例如 "result.code"
    public string MessagePath { get; set; } = "msg";
    public string DataPath { get; set; } = "data";
    public string[] SuccessCodes { get; set; } = { "0" };        // 按 JSON 原文比较：数字 0 与字符串 "0" 都是 "0"
}
```

1. internal `EndpointConfigRules`（`Http` 命名空间）：校验 `WebApiEndpointConfig` 的全部字段并生成**快照**（internal 不可变类型 `EndpointSettings`）。错误抛 `ArgumentException`，消息指出字段名（P1 D5）。`DefaultHeaders` 的键不能为空；头名重复（不区分大小写）抛异常。
2. 认证配置的校验在 Task 4（读取环境变量），这里只校验结构（例如 Basic 必须两个变量名都有）。
3. 测试：每个非法字段一条（`[Theory]`），合法默认值通过；快照与原配置隔离（构造后修改配置不影响快照）。

提交：`feat(webapi): add the shared endpoint configuration and its validation`

---

## 9. Task 4：钩子契约与内置认证（P3-D3、P3-D4）

**公开 API（固定）**（根命名空间，`WebApiAuthentication` 在 `Auth`）

```csharp
public interface IWebApiInterceptor
{
    Task OnSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken);
    Task OnReceivedAsync(WebApiReceivedContext context, CancellationToken cancellationToken);
}

/// 便捷基类：两个方法默认什么也不做（net472 没有默认接口成员）。
public abstract class WebApiInterceptor : IWebApiInterceptor { public virtual Task OnSendingAsync(...); public virtual Task OnReceivedAsync(...); }

public sealed class WebApiSendingContext
{
    public WebApiSendingContext(string endpointName, long callId, int attempt, string method, Uri requestUri,
                                IDictionary<string, string> headers, byte[] body, string? contentType,
                                string? idempotencyKey, IDictionary<string, object?> items);
    public string EndpointName { get; }
    public long CallId { get; }                      // 同一客户端内单调递增，从 1 开始
    public int Attempt { get; }                      // 从 1 开始；重试与重放都加 1
    public string Method { get; }
    public Uri RequestUri { get; set; }              // 可改（例如追加签名参数），scheme 与 authority 不能变（P3-D17）
    public IDictionary<string, string> Headers { get; }   // 不区分大小写，可改
    public byte[] Body { get; set; }                 // 可替换（例如加密）；不能为 null
    public string? ContentType { get; set; }
    public string? IdempotencyKey { get; }
    public IDictionary<string, object?> Items { get; }    // 本次调用内所有拦截器、所有尝试共享
    public void MarkSensitiveHeader(string name);    // 日志与事件参数中该头只输出 ***
    public void Fail(string message, CommErrorKind kind);   // 中止本次调用，不发送、不重试
}

public sealed class WebApiReceivedContext
{
    public WebApiReceivedContext(string endpointName, long callId, int attempt, string method, Uri requestUri, int statusCode,
                                 IReadOnlyDictionary<string, string> responseHeaders, byte[] body, string? contentType,
                                 IDictionary<string, object?> items);
    // EndpointName、CallId、Attempt、Method、RequestUri、Items 同上（只读）
    public int StatusCode { get; }
    public IReadOnlyDictionary<string, string> ResponseHeaders { get; }   // 同名多值用 ", " 连接
    public byte[] Body { get; set; }                 // 可替换（例如解密）
    public string? ContentType { get; }
    public void RequestReplay();                     // 立即再发一次（重新执行 OnSendingAsync），每次调用最多 1 次
    public void Fail(string message, CommErrorKind kind);   // 把本次结果改判为失败，不重试
}

public sealed class WebApiComponents
{
    public IList<IWebApiInterceptor> Interceptors { get; } = new List<IWebApiInterceptor>();
    public IWebApiSerializer? Serializer { get; set; }        // 仅 REST；SOAP 客户端设置了它就在构造时抛 ArgumentException
    public IBackoffPolicy? RetryBackoff { get; set; }         // 覆盖 RetryDelay
    public HttpMessageHandler? HttpHandler { get; set; }      // 客户端不释放它（P3-D12）
}

public interface IWebApiEndpoint : IDisposable, IAsyncDisposable
{
    string Name { get; }
    WebApiAvailability Availability { get; }
    event EventHandler<WebApiAvailabilityChangedEventArgs>? AvailabilityChanged;
    event EventHandler<WebApiSendingEventArgs>? RequestSending;
    event EventHandler<WebApiResponseEventArgs>? ResponseReceived;
    event EventHandler<WebApiCompletedEventArgs>? RequestCompleted;
}

public abstract class WebApiCallEventArgs : EventArgs
{
    // protected 构造函数；属性：EndpointName、CallId、Method、RequestUri（查询参数已脱敏）
}
public sealed class WebApiSendingEventArgs : WebApiCallEventArgs
{
    // + Attempt、Headers（IReadOnlyDictionary，已脱敏）、Body（ReadOnlyMemory<byte>，实际发送的字节）、ContentType
}
public sealed class WebApiResponseEventArgs : WebApiCallEventArgs
{
    // + Attempt、StatusCode（int?，网络层失败为 null）、ErrorKind（本次尝试的归类）、Body（ReadOnlyMemory<byte>，拦截器处理后的字节；无响应时为空）、
    //   Elapsed（TimeSpan）、WillRetry（bool）、RetryDelay（int 毫秒，不重试为 0）、Exception
}
public sealed class WebApiCompletedEventArgs : WebApiCallEventArgs
{
    // + Attempts（int，没有发出任何尝试为 0）、IsSuccess、StatusCode、RemoteCode、ErrorKind、ErrorMessage、Elapsed（整个调用）
}
public sealed class WebApiAvailabilityChangedEventArgs : EventArgs
{
    // EndpointName、PreviousAvailability、CurrentAvailability、Reason（CommErrorKind：导致 Offline 的最后一次失败分类，其余为 None）、Exception
}
```

所有事件参数都有公开构造函数并保存全部参数（P2.5 先例：自定义实现也能引发事件）。

```csharp
namespace Junevy.Communication.WebApi.Auth;

public static class WebApiAuthentication
{
    public static IWebApiInterceptor Basic(string userName, string password);
    public static IWebApiInterceptor Bearer(string token);
    public static IWebApiInterceptor Bearer(Func<CancellationToken, Task<string>> tokenProvider);   // 每次尝试前调用
    public static IWebApiInterceptor ApiKey(string headerName, string apiKey);
}
```

1. internal 实现：三个认证拦截器（Basic 按 RFC 7617 UTF-8 编码；都会对自己写入的头调用 `MarkSensitiveHeader`）；`AuthInterceptorFactory.FromOptions(WebApiAuthOptions)`：读取环境变量，变量不存在或为空抛 `ArgumentException`（消息含**变量名**，绝不含值）；`Scheme = None` 返回 null。
2. 上下文的 `Fail` / `RequestReplay` / `MarkSensitiveHeader` 只记录意图，由 Task 6 的尝试循环读取（上下文内用 internal 属性暴露给 `Http`）。`RequestUri` 的 setter 不做同源检查（检查在 Task 6 统一做，返回 `InvalidRequest`，不抛异常）。
3. 测试：Basic 用 RFC 7617 的示例（`Aladdin` / `open sesame` → `QWxhZGRpbjpvcGVuIHNlc2FtZQ==`）；中文用户名按 UTF-8 编码；Bearer 动态提供者每次尝试调用一次；ApiKey 头名与值；环境变量缺失抛异常且消息不含值（测试用唯一的变量名，结束时清除）；参数为 null 或空抛 `ArgumentException`；事件参数构造函数保存全部参数。

提交：`feat(webapi): add the interceptor and event contracts and built-in authentication`

---

## 10. Task 5：HTTP 交换（单次尝试）

全部 internal，`Http` 命名空间。

1. `HttpRequestSnapshot`（不可变）：Method、相对 URI（含已编码的查询）、Headers、ContentType、Body、IdempotencyKey。离线缓存以后从它重放（P3-D5）。
2. `HttpHandlerFactory`（唯一含条件编译的文件）：按 P3-D12 创建处理器；net472 按 BaseUrl / Url 设置该端点的 `ServicePoint`。
3. `HttpExchange`：把一次尝试的请求（拦截器处理后的方法、URI、头、正文）转成 `HttpRequestMessage`。请求头先 `request.Headers.TryAddWithoutValidation`，失败（属于内容头，例如 `Content-Type`）再加到 `content.Headers`；`ExpectContinue = false`。发送用 `HttpCompletionOption.ResponseHeadersRead`，读取响应体时检查 `MaxResponseBodySize`（先看 `Content-Length`，再在读取过程中计数）。结果是"收到响应（状态码、响应头、正文、ContentType）"或"网络层失败（分类 + 异常）"。
4. 归类（`HttpStatusClassifier`，设计 15.7 + P3-D13）：DNS 失败、连接被拒、连接中断（`HttpRequestException`、`IOException`、`SocketException`）→ `ConnectionClosed`；`TimeoutScope.IsTimedOut` → `Timeout`；用户取消 → `Cancelled`；401 / 403 → `AuthenticationFailed`；429 → `ResourceExhausted`；其他非 2xx（含 3xx）→ `RemoteError`；超出响应体上限 → `ProtocolViolation`。
5. 日志（`WebApiLogFormatter`）：每次尝试 Debug 记录方法、脱敏后的 URI、状态码、耗时；`LogBodies = true` 时 Debug 记录请求与响应正文（UTF-8 解码，超过 `MaxLoggedBodyLength` 截断并注明原长度）；脱敏规则 P3-D17。
6. 测试（`StubHttpHandler` 放在 `TestSupport/`：按脚本返回响应、抛异常或延迟，记录收到的请求）：第 15.7 节归类表逐行；超时与用户取消区分；内容头路由（`Content-Type` 出现在内容头，自定义头在请求头）；`ExpectContinue` 为 false；响应体上限（有 `Content-Length` 与分块两种）；脱敏（头、敏感标记、查询参数）与截断。

提交：`feat(webapi): add the single-attempt HTTP exchange, classification and logging`

---

## 11. Task 6：尝试循环（拦截器、事件、重试）

`Http` 命名空间的 `AttemptLoop`（或等价名称）。每次调用的流程固定如下，测试逐条覆盖（Q23）：

1. 调用开始：分配 `CallId`，创建本次调用的 `Items`，开始计时。
2. 每次尝试：
   1. **从快照重建** `WebApiSendingContext`（上一次尝试中拦截器的修改不累积，避免重复签名）；`Attempt` 加 1。
   2. 在本次尝试的 `TimeoutScope` 内**按注册顺序**执行 `OnSendingAsync`：配置生成的认证拦截器排第一，然后是 `WebApiComponents.Interceptors`。任何拦截器调用了 `Fail` → 调用以该结果结束；抛异常 → `Unspecified`（记 Error）；修改后的 `RequestUri` 不同源 → `InvalidRequest`。这三种情况都不发送、不重试，也不触发本次尝试的 `RequestSending`。
   3. 触发 `RequestSending`。
   4. HTTP 交换（Task 5）。
   5. 收到响应时**按注册的逆序**执行 `OnReceivedAsync`（洋葱模型，与 `DelegatingHandler` 一致）；可替换正文、`Fail`、`RequestReplay`。网络层失败时不调用 `OnReceivedAsync`。
   6. 协议层解释响应（REST / SOAP 传入的委托，得到 `WebApiResult<T>`）。
   7. 重试判定（P3-D10）：拦截器请求了重放且本调用尚未重放过 → 立即重发，不计入 `RetryCount`，不受方法规则限制（拦截器对此负责，例如 401 换 token 后重发）；否则按可重试结果、方法规则、剩余次数、退避与 `Retry-After` 决定。
   8. 触发 `ResponseReceived`（带 `WillRetry` 与 `RetryDelay`），把本次尝试的结果报告给可用性跟踪（Task 7）。
   9. 需要重试时等待退避（用户取消 → `Cancelled`，不再发送）。
3. 调用结束：触发一次 `RequestCompleted`（P3-D9），返回结果。最终失败为 `ConnectionClosed`、`Timeout` 或 5xx 时记 Warning，其余失败记 Debug。

典型序列（第一次 503 后重试成功）：`RequestSending(1)` → `ResponseReceived(1, 503, WillRetry = true)` → `RequestSending(2)` → `ResponseReceived(2, 200, WillRetry = false)` → `RequestCompleted(Attempts = 2, IsSuccess = true)`。

测试（全部用 `StubHttpHandler`，不开套接字）：
- 重试规则表：GET 遇 503 重试；POST 遇 503 不重试；POST 带 `IdempotencyKey` 重试且每次都带 `Idempotency-Key` 头；`RetryUnsafeMethods = true` 时 POST 重试；4xx（除 408 / 429）不重试；带 `RemoteCode` 的 500 不重试；`RetryCount` 用尽返回最后一次的真实结果；`Retry-After`（秒数与 HTTP 日期两种格式）被采用，超过 30000 ms 不重试；退避期间取消 → `Cancelled`。
- 拦截器：执行顺序（发送正序、接收逆序）；每次尝试从快照重建（计数型签名拦截器在第 2 次尝试看到的是原始头）；`Items` 跨尝试保留；`Fail` 中止；抛异常 → `Unspecified` 且不触发 `RequestSending`；修改 URI 改了 host → `InvalidRequest`；`RequestReplay` 只生效一次，第二次请求被忽略；重放让 POST 也重发；`OnSendingAsync` 卡住 → 本次尝试 `Timeout`。
- 事件：上面的典型序列；网络层失败的 `ResponseReceived`（`StatusCode` 为 null）；每次调用恰好一个 `RequestCompleted`（包括校验失败 `Attempts = 0`）；订阅者抛异常不影响结果与其他订阅者；事件参数中的敏感头为 `***`；`SendAsync` 返回时 `RequestCompleted` 已执行（同步可观察）。

提交：`feat(webapi): add the attempt loop with interceptors, events and retries`

---

## 12. Task 7：可用性、健康检查与共享核心

1. `AvailabilityTracker`（P3-D8、P3-D13）：初始 `Unknown`。计为"失败"的尝试结果：`ConnectionClosed`、尝试超时、HTTP 502 / 503 / 504；收到其他任何响应 → `Online` 并清零；连续 `OfflineThreshold` 次失败 → `Offline`。用户取消、`InvalidRequest`、拦截器失败不影响可用性。状态变化经单消费者队列按顺序触发 `AvailabilityChanged`，并记 Information 日志。
2. `HealthCheckLoop`：配置了健康检查地址时（REST `HealthCheckPath`、SOAP `HealthCheckUrl`），构造后立即开始，每 `HealthCheckInterval` 发一次 GET。探测走拦截器链与超时，**不重试、不触发请求事件**，结果只报告给可用性跟踪。释放时停止。
3. `EndpointCore`（P3-D7）：持有设置快照、HttpClient（注入的处理器不释放）、拦截器链、尝试循环、可用性、健康检查、事件委托（外观类的 `event` 用 `add` / `remove` 转发到这里，`sender` 为外观对象）。释放：幂等；停止健康检查；取消在途请求（返回 `ConnectionClosed`）；`DisposeAsync` 等待可用性事件队列派发完，最多 2000 ms（具名常量），剩余事件丢弃并记 Warning；之后的调用返回 `NotConnected` 且不触发事件。
4. 测试：可用性转换表逐行（含 `OfflineThreshold = 1` 与 3）；并发请求下事件顺序与最终状态一致；健康检查让 `Unknown` 变为 `Online` / `Offline`，且不触发请求事件；释放的各条规则（在途请求、之后的调用、注入处理器未被释放、重复释放）。等待事件用 `TaskCompletionSource`，**不要**再复制一份 `WaitUntilAsync`（第 18 节已记录它的重复）。

提交：`feat(webapi): add availability tracking, health checks and the shared endpoint core`

---

## 13. Task 8：REST 客户端

**公开 API（固定）**（`Rest` 命名空间；序列化在 `Serialization`）

```csharp
public interface IWebApiClient : IWebApiEndpoint
{
    WebApiClientConfig Config { get; }
    Task<WebApiResult<TResponse>> SendAsync<TResponse>(WebApiRequest request, CancellationToken cancellationToken = default);
}

public sealed class WebApiClient : IWebApiClient
{
    public WebApiClient(WebApiClientConfig config, ILogger<WebApiClient>? logger = null, WebApiComponents? components = null);   // 名称默认为 BaseUrl 的 authority
    public WebApiClient(string name, WebApiClientConfig config, ILogger<WebApiClient>? logger = null, WebApiComponents? components = null);
}

public class WebApiClientConfig : WebApiEndpointConfig
{
    public string BaseUrl { get; set; } = "";                    // 绝对 http / https URL
    public ResponseEnvelopeOptions Envelope { get; set; } = new();
    public string? HealthCheckPath { get; set; }                 // 相对路径
}

public sealed class WebApiRequest
{
    public HttpMethod Method { get; set; } = HttpMethod.Get;
    public string Path { get; set; } = "";                       // 相对路径（P3-D17）
    public IDictionary<string, string?>? Query { get; set; }     // 值为 null 的项不发送
    public object? Body { get; set; }                            // byte[] 与 string 原样发送；其他对象经序列化器
    public string? ContentType { get; set; }                     // byte[] / string 正文必须指定；对象正文默认序列化器的类型
    public IDictionary<string, string>? Headers { get; set; }    // 覆盖同名的 DefaultHeaders
    public int? Timeout { get; set; }
    public string? IdempotencyKey { get; set; }
}

public static class WebApiClientExtensions
{
    public static Task<WebApiResult<TResponse>> GetAsync<TResponse>(this IWebApiClient client, string path, IDictionary<string, string?>? query = null, CancellationToken cancellationToken = default);
    public static Task<WebApiResult<TResponse>> PostAsync<TResponse>(this IWebApiClient client, string path, object? body, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    public static Task<WebApiResult<TResponse>> PutAsync<TResponse>(this IWebApiClient client, string path, object? body, string? idempotencyKey = null, CancellationToken cancellationToken = default);
    public static Task<WebApiResult<TResponse>> DeleteAsync<TResponse>(this IWebApiClient client, string path, CancellationToken cancellationToken = default);
}

namespace Junevy.Communication.WebApi;   // 契约在根命名空间（里程碑 2 审阅裁决：WebApiComponents.Serializer 引用它）
public interface IWebApiSerializer
{
    string ContentType { get; }                                  // 例如 "application/json; charset=utf-8"
    byte[] Serialize(object value, Type type);
    object? Deserialize(byte[] data, Type type);                 // 失败抛异常，由调用方归类为 ProtocolViolation
}
namespace Junevy.Communication.WebApi.Serialization;
public sealed class SystemTextJsonWebApiSerializer : IWebApiSerializer
{
    public SystemTextJsonWebApiSerializer();                     // P3-D15 的默认选项
    public SystemTextJsonWebApiSerializer(JsonSerializerOptions options);
}
```

1. 请求：校验（`request` 为 null、`Path` 为绝对 URL、`byte[]` / `string` 正文没有 `ContentType`、`Timeout <= 0` → `InvalidRequest`，触发 `RequestCompleted(Attempts = 0)`）；拼接 BaseUrl 与 Path（BaseUrl 末尾有无 `/` 都正确）；查询参数用 `Uri.EscapeDataString`；头合并顺序：DefaultHeaders → request.Headers → `Idempotency-Key`。
2. 响应解码（internal `RestResponseDecoder`）：
   - 非 2xx：分类按状态码；启用了包络且正文可解析时，`RemoteCode` 与消息取自包络，因此按 P3-D10 不重试（MES 给出了业务码，说明请求已到达业务层）；否则消息为 `HTTP 400 Bad Request.` 这类固定格式。
   - 2xx：启用包络时先用 `JsonDocument` 定位 code / msg / data；包络无法解析或缺少 code → `ProtocolViolation`；code 不在 `SuccessCodes` → `RemoteError` + `RemoteCode`；成功则把 data 的原始 JSON 交给序列化器。
   - `TResponse` 为 `byte[]` / `string`：返回原始正文（string 按响应 charset，缺省 UTF-8），不应用包络。
   - 正文为空（204 或长度 0）：成功，`Data = default`。
   - 反序列化失败 → `ProtocolViolation`（带状态码与异常）。
3. 测试：设计第 18 节 REST 部分全部（状态码归类表、业务包络、重试规则已在 Task 6）；包络：数字 code 与字符串 code、嵌套路径、自定义成功码、非 2xx 带包络、包络缺字段；中文不转义（序列化结果包含原文"工单"）；查询编码（空格、中文、`&`）；绝对 Path 被拒；头合并与覆盖；空正文；原始类型；替换序列化器；名称默认值。

提交：`feat(webapi): add the REST client`

---

## 14. Task 9：SOAP 客户端

**公开 API（固定）**（`Soap` 命名空间）

```csharp
public interface ISoapClient : IWebApiEndpoint
{
    SoapClientConfig Config { get; }
    Task<WebApiResult<TResponse>> InvokeAsync<TRequest, TResponse>(string action, TRequest body, SoapCallOptions? options = null, CancellationToken cancellationToken = default);
    Task<WebApiResult<XElement>> InvokeRawAsync(string action, XElement body, SoapCallOptions? options = null, CancellationToken cancellationToken = default);
}

public sealed class SoapClient : ISoapClient
{
    public SoapClient(SoapClientConfig config, ILogger<SoapClient>? logger = null, WebApiComponents? components = null);   // 名称默认为 Url 的 authority
    public SoapClient(string name, SoapClientConfig config, ILogger<SoapClient>? logger = null, WebApiComponents? components = null);
}

public class SoapClientConfig : WebApiEndpointConfig
{
    public string Url { get; set; } = "";
    public SoapVersion Version { get; set; } = SoapVersion.Soap11;
    public string? DefaultNamespace { get; set; }
    public string? HealthCheckUrl { get; set; }                  // 绝对 URL，通常为 "...?wsdl"
}

public enum SoapVersion { Soap11 = 0, Soap12 = 1 }

public sealed class SoapCallOptions
{
    public int? Timeout { get; set; }
    public IDictionary<string, string>? Headers { get; set; }    // HTTP 头
    public IList<XElement>? SoapHeaders { get; set; }            // 写入 soap:Header 的元素
    public string? IdempotencyKey { get; set; }                  // 设置后允许重试（SOAP 总是 POST）
}
```

1. 请求（internal `SoapEnvelopeWriter`）：UTF-8 无 BOM；1.1 命名空间 `http://schemas.xmlsoap.org/soap/envelope/`，`Content-Type: text/xml; charset=utf-8`，`SOAPAction: "<action>"`（带引号；action 为空串时发 `""`）；1.2 命名空间 `http://www.w3.org/2003/05/soap-envelope`，`Content-Type: application/soap+xml; charset=utf-8; action="<action>"`（action 为空串时省略 action 参数）。`action` 为 null → `InvalidRequest`。强类型正文用 `XmlSerializer`，**只用** `new XmlSerializer(Type)` 或 `new XmlSerializer(Type, string)` 两个构造函数（其他重载每次都生成新程序集，会泄漏），并按（类型, 命名空间）缓存。
2. 响应（internal `SoapResponseReader`）：`XmlReaderSettings { DtdProcessing = Prohibit, XmlResolver = null }`。顺序：解析信封 → 如果 Body 的第一个子元素是 Fault，无论状态码都返回 `RemoteError`（1.1：`RemoteCode` = faultcode 原文，消息 = faultstring；1.2：`RemoteCode` = Code/Value 原文，消息 = 第一个 Reason/Text，有 Subcode 时在消息后附 ` (subcode: <值>)`），带状态码 → 否则状态码非 2xx 按 Task 5 归类 → 否则取 Body 的第一个子元素反序列化（失败 → `ProtocolViolation`）。正文不是 XML：非 2xx 按状态码归类，2xx → `ProtocolViolation`。
3. 测试：黄金信封（1.1、1.2 各一份完整请求，按字节比较；SOAPAction 与 Content-Type 的各种 action 值）；1.1 Fault（`soap:Server`）、1.2 Fault（W3C SOAP 1.2 Primer 的 `env:Sender` + `m:MessageTimeout` 示例结构）；Fault 随 500 返回且不重试（即使是 GET 也不会出现，因为 SOAP 总是 POST；用 `IdempotencyKey` 打开重试后验证 Fault 仍不重试）；带 DTD 的响应被拒（`ProtocolViolation`）；`InvokeRawAsync` 往返；`SoapHeaders` 写入；`DefaultNamespace`；设置了 `WebApiComponents.Serializer` 的构造抛 `ArgumentException`。

提交：`feat(webapi): add the SOAP client`

---

## 15. Task 10：真实套接字端到端

`TestSupport/RawHttpServer`：基于 `TcpListener`（`127.0.0.1:0`），读到 `\r\n\r\n` 再按 `Content-Length` 读正文，按脚本写回原始 HTTP/1.1 响应；支持"接受连接但不回应"与"写一半响应后断开"。**不使用 HttpListener**（非管理员需要 URL ACL，3.2 再处理）。全部放进 `SocketTiming` 集合。

用例：REST 成功往返（JSON 中文）；SOAP 1.1 / 1.2 成功往返；连接被拒（关闭的端口）→ `ConnectionClosed`；接受但不回应 → `Timeout`，耗时在 [Timeout, Timeout + 1500 ms] 区间；响应写一半断开 → `ConnectionClosed`；第一次断开、第二次成功的 GET 重试；健康检查经真实连接把可用性从 `Unknown` 变为 `Online`，关闭服务器后变为 `Offline`；在途请求期间 Dispose（循环 20 次，每次都返回 `ConnectionClosed` 且无未观察的任务异常）。

提交：`test(webapi): add end-to-end tests over real sockets`

---

## 16. Task 11：工厂与依赖注入

**公开 API（固定）**

```csharp
namespace Junevy.Communication.WebApi.Factory;

public sealed class WebApiClientFactory : NamedFactory<IWebApiEndpoint>
{
    public WebApiClientFactory(ILoggerFactory? loggerFactory = null);
    public IWebApiClient GetOrAdd(string name, WebApiClientConfig config, WebApiComponents? components = null);
    public ISoapClient GetOrAdd(string name, SoapClientConfig config, WebApiComponents? components = null);
}

public sealed class WebApiClientFactoryBuilder
{
    public static WebApiClientFactoryBuilder Create();
    public WebApiClientFactoryBuilder WithLoggerFactory(ILoggerFactory loggerFactory);
    public WebApiClientFactory Build();
}

namespace Junevy.Communication.WebApi.DependencyInjection;
public static class WebApiServiceCollectionExtensions
{
    public static IServiceCollection AddWebApiClients(this IServiceCollection services);   // TryAddSingleton，幂等
}
```

- 同名已存在但类型不符（例如已注册 SOAP，又按 REST 获取）→ `InvalidOperationException`，消息说明已有实例的类型。
- 名称、别名、移除即释放、工厂释放后的行为全部由 `NamedFactory` 提供，不重写。
- 测试：同名返回同一实例；类型不符抛异常；移除时释放客户端（健康检查停止）；DI 注册幂等；与 `PlcClientFactory` 的测试风格一致。

提交：`feat(webapi): add the client factory and DI registration`

---

## 17. Task 12：文档与知识库；以及 3.1 为 3.2 准备的部分

文档：
1. `Junevy.Communication.WebApi/README.md`：定位；REST 与 SOAP 快速上手；**阶段钩子**一节（事件序列图、拦截器顺序、签名与 401 换 token 两个示例拦截器）；重试与幂等（为什么默认不重试 POST）；可用性；日志脱敏；net472 注意事项（`ServicePoint` 设置作用于同一主机的其他代码）；凭据只能来自环境变量或代码注入。
2. 根 `readme.md`：包列表加入 WebApi，链接到包 README。
3. Skill：新建 `Skills/using-junevy-webapi/SKILL.md`（格式参照 `Skills/using-junevy-plc/SKILL.md`），覆盖创建客户端、拦截器、事件、重试规则、常见坑。
4. 设计文档：新增第 20.4 节"实施修订（P3.1）"，列出 P3-D4、D5、D6、D10、D11、D12、D15、D16 对第 15 节的修订。
5. CHANGELOG 收尾。
6. 知识库（不提交）：新增 `zh/content/WebApi/` 下的笔记（客户端与钩子、重试与可用性行为契约），挂进 `知识库首页.md`；在 `Agent协作/开发约定与验收基线.md` 的分层规则中加入 WebApi 命名空间分层与 NetArchTest 测试。

**3.1 必须为 3.2 准备好（审阅者在本 Task 核对）**
- `ResponseEnvelopeOptions` 在根命名空间；`Serialization` 中的包络读写器同时具备"读"（3.1 用）与"写"的能力边界清楚——**"写"本期不实现**（没有使用者），只确保读写器的结构不需要改动就能加入写入方法。
- 事件参数的基类 `WebApiCallEventArgs` 只服务客户端；3.2 的服务端事件另设基类，不复用客户端的 `CallId` 语义。
- 第 3 节表格中 `Server` 行的依赖由 3.2 计划确定。

3.2 已知待决问题（3.2 计划开始时向用户确认）：
1. 监听实现：`HttpListener`（非管理员需 `netsh http add urlacl`，HTTPS 需 `netsh http add sslcert`）还是其他方案。
2. 设计 15.5 引用的 `ServerState`、`ServerStateChangedEventArgs`（在 Tcp）与 `ReconnectOptions`（在 Channels）WebApi 都不能引用：WebApi 自建同名类型，还是把通用部分移入 Core。
3. 服务端钩子：中间件（干预）+ 事件（收到请求、拒绝、处理器异常、响应已发出）的具体形态。
4. 服务端认证：`IRequestAuthenticator` 与内置 Basic / Bearer / ApiKey 校验。

提交：`docs: document the WebApi client`

---

## 18. 未纳入本计划的条目

- 离线缓存（`IOutboxStore`）的实现与公开接口（P3-D5）。
- `WaitUntilAsync` 在 5 个测试项目中重复（P2.5 第 20 节已记录）；本期新测试不得再复制一份。
- 表单（`application/x-www-form-urlencoded`）与 multipart 正文：宿主可自行序列化为 `string` / `byte[]` 并指定 `ContentType`。
- WS-Security 等 SOAP 安全扩展：宿主用 `SoapCallOptions.SoapHeaders` 或拦截器实现。

---

## 19. 验收记录

### 19.1 各 Task 验收（审阅者填写）

| Task | 结论 | 说明 |
|---|---|---|
| 0 | 通过 | 基线与预期一致；审阅者复跑三条命令全绿，度量相同（重复 24、超长方法 0、超 300 行文件 9、IVT 2） |
| 1 | 通过 | 19.2 第 1 条：`FrameRouterTests` 的调用点是计划第 6 节第 3 条漏列（计划错误，不是执行偏差），接受这处两行改动；断言未变。第 2 条（5 个文件各 +1 行 using）接受：调用点所需，审阅者逐个核对过 using 仍被使用 |
| 2 | 通过 | 审阅者复做故意违规：`Soap` 方法体内引用 `Rest` 的类型 → 只有 Soap 行失败。19.2 第 3 条：两行参数检查的重复保留，不为它新增 Core 公开 API；第 4 条 `ToString` 格式接受 |
| 3 | 通过，附一项修改 | 第 6 条补充的校验全部保留。修改（在 Task 5 开始时做）：`EndpointSettings` 不再暴露 `RedactedHeaders` / `RedactedQueryParameters` 列表，改为 `IsRedactedHeader(string)` / `IsRedactedQueryParameter(string)` 方法，内部用 `StringComparer.OrdinalIgnoreCase` 的 `HashSet<string>`，大小写规则只在快照里定一次（Q12） |
| 4 | 通过 | 19.2 第 1 条是计划自相矛盾（审阅者的错误）：裁决 `IWebApiSerializer` 移到根命名空间，`SystemTextJsonWebApiSerializer` 留在 `Serialization`；第 3 节表格与 Task 8 的 API 已改。`WebApiComponents.Serializer` 在 Task 8 加入。第 2、3 条接受 |
| 5 | 通过 | 第 5、6 条接受。Q24 有测试证据（`ServicePointManager` 静态值前后不变） |
| 6 | 通过，附一项修改 | 审阅者做了两次变异：方法规则改成恒真 → 8 个测试失败；每次尝试不再从快照重建 → `EachAttempt_StartsFromTheSnapshot_SoModificationsDoNotAccumulate` 失败。第 7、8、9、12 条接受（第 7 条写进 Task 12 README 的事件一节：被中止或在发送前拦截器阶段超时的尝试不触发 `RequestSending` / `ResponseReceived`）。第 10 条追加提交接受。**修改（第 11 条）**：端点释放导致的在途尝试，`ResponseReceived.ErrorKind` 也必须是 `ConnectionClosed`，与最终结果一致；在 `AttemptLoop` 里用一个辅助方法同时映射尝试结果与最终结果，可用性仍不计入 |
| 7 | 通过，附一项修改 | 第 13 条接受：`IWebApiEndpoint.AvailabilityChanged` 的 XML 注释与 README 写明"订阅后先读一次 `Availability`"。第 14–17 条接受。**修改**：`OrderedEventQueue.RunAsync` 中 `consumer(item)` 与 `dispatching` 标记改为 try/finally（防御：消费者以后若抛出，派发循环不能就此停止） |
| 修订（755404a） | 通过 | 释放一致性与队列防御已按裁决完成；try/finally 无直接测试接受（`AsyncLocal` 标志在测试线程不可见） |
| 8 | 通过，附一项修改 | 第 3、5、6、7 条接受（第 5 条写进 README：GBK 等代码页需宿主注册 `CodePagesEncodingProvider`）。**修改（第 4 条）**：① 启用包络时，2xx 且正文为空：204 → 成功、`Data = default`；其他 2xx → `ProtocolViolation`（无法判断业务成败，不能当作成功）；② `TResponse` 为 `byte[]` / `string` 时，空正文返回 `Array.Empty<byte>()` / `""`，不是 null（原始类型就是原样的正文） |
| 9 | 通过，附一项缺陷修复 | 第 8、9 条接受。**缺陷（第 10 条）**：`XmlBodyCodec` 删除全部命名空间声明后，`xsi:type="xsd:int"` 这类 QName 属性值失去前缀绑定。审阅者复现：`object` 类型的成员写入信封后反序列化报 `Namespace prefix 'xsd' is not defined`。修复见下一里程碑。**修改（第 11 条）**：不为度量调整代码顺序，`SoapClient` 的事件声明恢复与 `WebApiClient` 相同的顺序，接受 4 个样板窗口（第 4 节目标已改） |
| 10 | 通过 | 审阅者复跑三遍全绿（每遍 19 个测试程序集），套接字用例稳定。第 12 条接受 |
| 11 | 通过 | 第 13 条接受 |
| 修订 A（75581cd） | 通过 | 空正文规则按裁决实现，8 个新用例先红后绿；事件声明恢复同一顺序，重复窗口 33（新增 9 个全部是外观转发样板，见第 4 节） |
| 修订 B（6aecd44） | 通过 | 审阅者的复现用例（`object` 成员）已成为回归测试，两个目标通过；黄金信封未改动且两个目标字节一致 |
| 12 | 通过，附审阅者修订 | README 覆盖要求的全部主题，示例拦截器由 `ReadmeSampleTests` 逐字核对；Skill、根 readme、设计文档 20.4、CHANGELOG、知识库（未提交）齐全。审阅者修订提交 `7f76c62`：① 重试的状态码规则只适用于按状态码归类的结果，正文超限的 503 不再重试（原代码与 README 不一致，新增用例先红后绿）；② 删除 `XmlBodyCodec.GetSerializer` 及两个只测它的用例（Q8：只为测试存在的生产代码） |
| 最终验收 | 通过 | 审阅者在 `74e7a05` 复跑三遍：19 个测试程序集、两个目标全绿，Release 0 警告，重复窗口 33（8 组，WebApi 与其他程序集之间 0），超过 60 行的方法 0，超过 300 行的文件 9，IVT 2。`7f76c62` 之后 WebApi.Tests 为 732 / 733（net8.0 / net472），架构测试 11，构建两种配置成功 |

### 19.2 执行中的偏差与发现（执行者填写：现象 / 处理 / 是否需要审阅者决定）

里程碑 1（Task 0–3，执行日 2026-10-11）：

1. **Task 1：已有测试调用了被删除的内部方法（需要审阅者决定）。** 第 6 节第 3 条没有列出 `Tests/Junevy.Communication.Channels.Tests/FrameRouterTests.cs`（`HandlerException_DoesNotStopDispatch`，第 412 行）对 `FrameRouter.RaiseToSubscribers` 的直接调用；按计划删除该方法后这个已有测试无法编译（Q17 要求此时停下汇报）。处理：为了不让里程碑停在 Task 1，只改了这一处调用点（`FrameRouter.RaiseToSubscribers(handlers, this, args, logger)` → `EventSubscribers.InvokeEach(handlers, this, args, logger, "FrameReceived")`，外加一行 `using`），断言与测试意图不变。证据：`git diff --stat b31dd27 -- Tests` 在 Task 1 提交后为 FrameRouterTests.cs 2 行增、1 行减，加 1 个新增文件；没有测试被删除或改名。审阅者若不接受，撤销方式是还原这两行并保留 `RaiseToSubscribers` 为一行转发（与 Q20 冲突），请裁决。
2. **Task 1：Q18「只减不增」。** 新增 `using Junevy.Communication.Core.Diagnostics;` 使 ConnectionSupervisor（690→691）、StreamClientChannel、TcpServer、TcpSession、UdpChannel 各增加 1 行（均为调用点所需，Tcp / Udp 属第 6 节第 3 条的例外）；LifecycleSupport 减 30 行、FrameRouter 减 26 行。
3. **Task 2：`WebApiResult<T>.Fail` 的校验与 `CommResult.ValidateFailure` 重复（4 行）。** 后者在 Core 内是 `internal`，WebApi 不能引用（Q16 禁止新增 IVT，第 2 节 Q19 禁止新增未列出的公开成员）。处理：在 `WebApiResult<T>` 中写了等价的两个检查。如果审阅者愿意把它公开（`CommResult.ValidateFailure`），可以删除这份重复。
4. **Task 2：`ToString()` 的格式。** 计划只写「与 CommResult 相同的格式，另附 HTTP 503 / remote code X」。实现：失败为 `Kind: message`，成功为 `Success`，有状态码 / 业务码时追加 ` (HTTP 500, remote code E42)`（同一对括号，逗号分隔），成功结果带状态码时为 `Success (HTTP 200)`。
5. **Task 2：NetArchTest 的命名空间匹配。** `HaveDependencyOnAny` 按命名空间**前缀**匹配（依赖名末尾加 `.` 反而匹配不到）。禁止集合因此取「分层表中除自身与允许项以外的命名空间」，不带 `.`；方法体内的引用已用故意违规验证会被检出（见汇报）。
6. **Task 3：计划未写明、实现中补充的校验。** `DefaultHeaders` 的值为 null 抛异常；`IdempotencyHeaderName` 只有空白（非 null、非空串）抛异常；`Auth.Scheme` 取未定义枚举值抛 `ArgumentOutOfRangeException`；`RedactedHeaders` / `RedactedQueryParameters` 为 null 或含空白项抛异常。嵌套选项为 null 抛 `ArgumentException`（参数名如 `Retry`）。
7. **Task 3：`ResponseEnvelopeOptions` 的校验不在本 Task。** 它只属于 REST 配置（`WebApiClientConfig.Envelope`），路径与成功码的规则随 Task 8 的 REST 配置校验一起写；本 Task 只提供类型与默认值测试。
8. **net472 绑定重定向：** `System.Text.Json` 8.0.5 已写入 csproj，但本里程碑没有代码使用它，net472 测试宿主未出现 `FileLoadException`，`build/Junevy.Communication.Tests.app.config` 未修改。Task 8 开始使用后再评估。

里程碑 2（Task 4–7，执行日 2026-10-11）：

1. **Task 4：`WebApiComponents.Serializer` 暂未添加（需要审阅者在 Task 8 前决定）。** 计划第 9 节把 `Serializer`（类型 `IWebApiSerializer`）放在根命名空间的 `WebApiComponents` 上，而 `IWebApiSerializer` 按第 13 节在 `Serialization` 命名空间，第 3 节又规定根命名空间不得依赖任何 WebApi 内部命名空间，两条无法同时成立（`WebApiLayeringTests` 会失败）。本里程碑的 `WebApiComponents` 只有 `Interceptors`、`RetryBackoff`、`HttpHandler`。建议方案：把 `IWebApiSerializer` 放到根命名空间（`SystemTextJsonWebApiSerializer` 仍在 `Serialization`，依赖根）；请裁决。
2. **Task 4：三个内置认证拦截器共用一个内部类 `HeaderAuthInterceptor`。** Basic、Bearer、ApiKey 只在头名和取值方式上不同（Q2），因此是一个类加三个工厂方法；行为与计划一致（头被标记为敏感，Bearer 提供者每次发送尝试前调用）。`Basic` 的用户名含冒号抛 `ArgumentException`（RFC 7617），所有字符串参数为 null 抛 `ArgumentNullException`、为空抛 `ArgumentException`。
3. **Task 4：net472 的 `System.Net.Http`。** 隐式 using 在 net472 上不含 `System.Net.Http`，用到 `HttpMessageHandler` 等类型的文件显式 `using System.Net.Http;`；`WebApi.Tests.csproj` 在 net472 下增加 `<Reference Include="System.Net.Http" />`。`build/Junevy.Communication.Tests.app.config` 仍未修改，`System.Text.Json` 仍未被使用。
4. **Task 5：按审阅者决定改了 `EndpointSettings`。** `RedactedHeaders` / `RedactedQueryParameters` 列表换成 `IsRedactedHeader(name)` / `IsRedactedQueryParameter(name)`（内部为 `OrdinalIgnoreCase` 的 `HashSet`），快照隔离测试同步更新。
5. **Task 5：响应没有内容对象时按空正文处理。** net472 上自定义处理器返回的 `new HttpResponseMessage(...)` 的 `Content` 为 null（net8.0 为空内容），`HttpResponseReader` 兼容两者；有对应测试。
6. **Task 5：`WebApiRedactor` 与 `WebApiLogFormatter` 分成两个类。** 计划只写了 `WebApiLogFormatter`；脱敏（头、URI）供事件参数与日志共用，格式化日志只消费已脱敏的事件参数，所以拆开（Q1）。
7. **Task 6：`ResponseReceived` 与可用性报告只针对"已发出"的尝试。** 拦截器 `Fail` / 抛异常、URI 改了来源、`OnSendingAsync` 期间超时的尝试没有发出请求：不触发 `RequestSending`，也不触发 `ResponseReceived`，不报告给可用性跟踪（计划第 11 节第 2 步第 2 条已规定前者，后两者是对该条的延伸）。`OnSendingAsync` 期间的超时按"尝试超时"可重试，重试时没有对应的 `ResponseReceived`。
8. **Task 6：`RequestCompleted.Attempts` 是实际发出的尝试次数**（被拦截器中止的尝试不计），而上下文里的 `Attempt` 序号每次尝试（含被中止的）都加 1。
9. **Task 6：重放优先于重试规则，但被忽略的第二次重放请求会继续走重试规则**（例如重放后又收到 503，仍可按 `RetryCount` 重试）。
10. **Task 6：重复扫描后追加提交 `1083a94`。** Task 6 提交（`a94f1bf`）后度量显示重复窗口 24→26：`InterceptorChain` 的两个阶段各抄了一份 try/catch 循环。按"不 amend"的规则另起一个提交，两个阶段共用 `RunAsync`，度量回到 24，行为不变。
11. **Task 6：`AttemptLoop.ExecuteAsync` 增加可选参数 `closingToken`**（端点正在释放的信号）。它触发而调用方没有取消时，最终结果由 `Cancelled` 改为 `ConnectionClosed`（P3-D13）；该在途尝试的 `ResponseReceived.ErrorKind` 仍是 `Cancelled`，`RequestCompleted` 与返回值是 `ConnectionClosed`。
12. **Task 6：无法解析的相对 URI**（`Uri.TryCreate` 失败）以 `InvalidRequest` 结束，走与 `Reject` 相同的路径（`Attempts = 0`）。REST / SOAP 外观仍应在 Task 8 / 9 里先校验路径。
13. **Task 7：健康检查在构造时就开始，用户来不及在构造返回前订阅 `AvailabilityChanged`。** 第一次探测引起的变化可能先于订阅发生；`Availability` 属性始终是真实状态（Task 12 的 README 需要写明"订阅后读一次 `Availability`"）。测试里用闸门卡住第一次探测来规避。
14. **Task 7：没有引入 `System.Threading.Channels`。** `OrderedEventQueue<T>` 用 `ConcurrentQueue` 加 `SemaphoreSlim` 实现，WebApi.csproj 未新增包引用。
15. **Task 7：`EndpointCore` 不释放 `closing` 令牌源与有序队列的信号量。** 两者都没有计时器或等待句柄，并且释放后仍可能被在途调用读取（释放它们反而会产生 `ObjectDisposedException` 竞争），已在代码里注明原因；`HealthCheckLoop` 的停止令牌源在循环结束后释放。
16. **Task 7：同步 `Dispose()` 沿用 `SupervisedChannelCore` 的先例**（`DisposeAsync().AsTask().Wait(limit)`，上限 `DisposeDrainMilliseconds + 1000`，超时记 Warning），不使用 `GetAwaiter().GetResult()`。
17. **测试耗时：** `Dispose_WithAStuckSubscriber_GivesUpAfterTheDrainLimitAndLogsAWarning` 需要真实等待约 2 秒（排空上限是具名常量，没有为测试做可注入的时钟）。

里程碑 3（Task 8–11，执行日 2026-10-11）：

1. **审阅修改第 5、6 条已在提交 `755404a` 完成。** 释放造成的取消由 `AttemptLoop.MapDisposal` 一个辅助方法改判为 `ConnectionClosed`，用在每次尝试的结果（`ResponseReceived` 之前）和"等待重试时被取消"的路径上，改判后不再重试；可用性跟踪读传输层结果（`Cancelled`），仍不计入。`OrderedEventQueue` 的消费回调用 try/finally 复位派发标志；该标志是 `AsyncLocal`，只在消费循环自己的执行流里可见，测试线程观察不到，所以没有对应的直接测试（防御性代码）。
2. **Task 8：`System.Text.Json` 首次使用，net472 测试宿主没有出现 `FileLoadException`，`build/Junevy.Communication.Tests.app.config` 未修改。**
3. **Task 8：路径语义。** `WebApiRequest.Path` 与 `HealthCheckPath` 去掉开头的 `/` 后以 BaseUrl（规范化为以 `/` 结尾）为基准拼接，因此 `http://host/api` + `/orders` = `http://host/api/orders`，不会替换基地址的路径。第一段带冒号的相对路径（`orders:1`）与 `scheme:x` 无法区分，一并按绝对引用拒绝（需要时写成 `./orders:1`）。
4. **Task 8：空正文按计划原文处理为"成功，Data = default"，对 `byte[]` / `string` 也是 null（不是空数组 / 空串）；启用包络时，2xx 的空正文同样是成功。** 若希望 2xx 空正文在启用包络时是协议违规，需要审阅者裁决。
5. **Task 8：未知 charset（net8.0 默认没有 GBK 等代码页）的字符串响应是 `ProtocolViolation`，不静默回退到 UTF-8；** 宿主需要时自行注册 `CodePagesEncodingProvider`，库不修改全局设置。请求的字符串正文也按 `ContentType` 的 charset 编码。
6. **Task 8：包络路径按名称精确匹配（区分大小写）；`ResponseEnvelopeOptions` 只在 `Enabled` 时校验路径与成功码。** 非 2xx 且包络可读时，`RemoteCode` 与消息取自包络但状态码分类不变（401 仍是 `AuthenticationFailed`）。
7. **Task 8：共享部分放在 Http 一处。** `EndpointArguments`（名称与调用超时规则）、`EndpointCore` 的名称可空（默认基地址的 authority）。**Task 9 追加** `HttpUrl`（绝对 http / https URL）、`UriOrigin`（同源比较，拦截器改地址的检查改用它）、`HttpStatusClassifier.Describe`（`HTTP 400 Bad Request.` 格式，REST 解码改用它）。
8. **Task 9：`HealthCheckUrl` 必须与 `Url` 同源（计划未写，新增）。** 探测也经过认证拦截器，不同主机会把凭据带出去（P3-D17 的同一理由）。
9. **Task 9：Fault 没有代码时 `RemoteCode` 为空串而不是 null，** 这样永远满足"带 `RemoteCode` 不重试"。信封版本按响应根元素自己的命名空间判定，不依赖配置的版本（1.1 客户端也能读懂 1.2 的 Fault）。
10. **Task 9：强类型正文序列化后去掉所有命名空间声明，由写信封的 `XmlWriter` 统一生成。** 原因：net472 的 `XmlSerializer` 给有命名空间的元素选 `q1` 之类的前缀，net8.0 用默认命名空间，黄金信封在两个目标上才能字节一致。`XmlSerializer` 只用两个会缓存生成程序集的构造函数，按类型缓存（每个客户端一份，默认命名空间对一个客户端固定，所以键里不含命名空间）。
11. **Task 9：`SoapClient` 的事件声明顺序与 `WebApiClient` 不同。** `add`/`remove` 转发是公开类型无法共享的样板（公开类不能继承内部基类，新增公开基类不在固定的公开 API 里）；相同顺序会让重复扫描多出 4 个窗口（24→28），换顺序后回到 24。
12. **Task 10：未观察任务异常的检查只统计异常文本含 `Junevy.Communication.WebApi` 的项，** 避免同一进程里其他测试的任务异常误伤；`RawHttpServer.Dispose` 同步等待内部循环最多 5 秒。
13. **Task 11：工厂在创建前先检查释放与 config 是否为 null，** 名称为 null / 空由 `NamedFactory` 抛 `ArgumentException`；配置非法时抛出且不注册任何东西。类型不符的 `InvalidOperationException` 消息格式：`The name 'x' is already registered with a SoapClient, which is not a IWebApiClient.`

最终里程碑（修订 A / B 与 Task 12，执行日 2026-10-12）：

1. **修订 A（`75581cd`）：空正文规则按审阅裁决收紧，取代里程碑 3 第 4 条。** `byte[]` / `string` 的空正文是空数组 / 空串（有没有包络都一样，空正文时不检查 charset）；其他类型：没有包络时成功（`default`），启用包络时 204 成功、其他 2xx 为 `ProtocolViolation`（无 `RemoteCode`，不重试）。旧代码上 8 个新用例失败。**事件顺序**按裁决恢复为与 `WebApiClient` 相同（`IWebApiEndpoint` 的声明顺序），取代里程碑 3 第 11 条；重复窗口因此为 33（8 组，其中 `WebApiClient` ↔ `SoapClient` 9 个窗口，是公开类无法共享的转发样板），已在 19.3 记录。
2. **修订 B（`6aecd44`）：`XmlBodyCodec` 不再去掉命名空间声明，取代里程碑 3 第 10 条。** 去掉全部声明会让 `object` 成员的 `xsi:type="xsd:int"` 失去前缀绑定，反序列化抛 `InvalidOperationException`（先以 `ObjectTypedMember_SurvivesTheEnvelopeRoundTrip` 在两个目标上复现）。现在序列化时给 `XmlSerializer` 一张显式的命名空间表（空前缀绑定到 `XmlReflectionImporter` 得到的正文根元素命名空间，与序列化器一起按类型缓存）；`XmlSerializer` 仍只用两个会缓存的构造函数。net8.0 与 net472 输出逐字相同，已有的黄金信封未改动，在两个目标上字节一致；`xsi` / `xsd` 在用到的地方就近声明（`p3:type="q1:int"`、`xmlns:q1` / `xmlns:p3` 均就地绑定）。
3. **Task 12：`ReadmeSampleTests` 读取仓库里的源文件。** 它从测试输出目录向上找到 `Junevy.Communication.slnx`，读 `Junevy.Communication.WebApi/README.md` 与 `Tests/…/ReadmeSamples.cs`，逐字比较两个示例拦截器（忽略换行风格与行尾空白）；找不到仓库根时测试失败而不是跳过。示例里的注释是英文（README 面向英文读者），其余测试注释仍是中文。
4. **Task 12：知识库已写入但按约定不提交；** 新增 `zh/content/WebApi/` 下两篇，更新首页与"开发约定与验收基线"。

### 19.3 基线与度量

#### Task 0 基线（2026-10-11，`7c6f0ec`，Debug，`dotnet test Junevy.Communication.slnx -c Debug`）

测试（通过 / 失败 / 跳过；net8.0 与 net472 相同，Modbus 只有 net8.0）：

| 项目 | net8.0 | net472 |
|---|---|---|
| Core.Tests | 112 / 0 / 0 | 112 / 0 / 0 |
| Channels.Tests | 278 / 0 / 0 | 278 / 0 / 0 |
| Tcp.Tests | 129 / 0 / 0 | 129 / 0 / 0 |
| Udp.Tests | 38 / 0 / 0 | 38 / 0 / 0 |
| Serial.Tests | 63 / 0 / 2 | 63 / 0 / 2 |
| Plc.Tests | 152 / 0 / 0 | 152 / 0 / 0 |
| Melsec.Tests | 184 / 0 / 1 | 184 / 0 / 1 |
| Architecture.Tests | 2 / 0 / 0 | 2 / 0 / 0 |
| Modbus.Tests | 405 / 0 / 0 | — |

度量（生产范围：Core、Channels、Tcp、Udp、Serial、Plc、Melsec，共 205 个 `.cs`）：

| 指标 | 基线 |
|---|---|
| 重复窗口（6 行） | 24（7 组：Serial↔TcpClient 7、Datagram↔StreamChannel 6、DatagramDriver↔StreamDriver 5、StreamClientChannel↔UdpChannel 3，其余三组各 1） |
| 超过 60 行的方法 | 0 |
| 超过 300 行的文件 | 9（NamedRegistry 461、ConnectionSupervisor 690、StreamConnectionDriver 314、DatagramChannel 352、FrameRouter 394、PendingRequestTable 537、StreamChannel 621、TcpClientChannel 372、TcpServer 448） |
| 生产 → 生产 IVT | 2（架构测试） |
| 有效行 | Core 957、Channels 4811、Tcp 2416、Udp 560、Serial 386、Plc 968、Melsec 1275 |

#### 里程碑 2 结束时（Task 7，2026-10-11）

| 指标 | 值 |
|---|---|
| 重复窗口（6 行，含 WebApi） | 24（7 组，与基线相同；WebApi 内部与跨程序集均为 0） |
| 超过 60 行的方法 | 0 |
| 超过 300 行的文件 | 9（不增加；WebApi 最大文件 `AttemptLoop.cs` 175 行） |
| 生产 → 生产 IVT | 2（架构测试通过） |
| WebApi 有效行 | 1677（Task 7 结束，里程碑 3–4 还会增加 REST / SOAP / 工厂） |
| WebApi.Tests | net8.0 378、net472 379（通过 / 0 失败 / 0 跳过） |

#### 里程碑 3 结束时（Task 11，2026-10-11）

| 指标 | 值 |
|---|---|
| 重复窗口（6 行，含 WebApi） | 24（7 组，与基线相同；WebApi 内部与跨程序集均为 0） |
| 超过 60 行的方法 | 0 |
| 超过 300 行的文件 | 9（不增加；WebApi 最大文件 `AttemptLoop.cs` 180 行） |
| 生产 → 生产 IVT | 2（架构测试通过） |
| `WebApiClient.cs` / `SoapClient.cs` | 108 / 121 行（Q22 要求各 ≤ 200） |
| WebApi 有效行 | 2713 |
| WebApi.Tests | net8.0 709、net472 710（通过 / 0 失败 / 0 跳过） |

#### 最终度量（P3.1 结束，2026-10-12，`dotnet test Junevy.Communication.slnx -c Debug`，通过 / 失败 / 跳过）

| 项目 | net8.0 | net472 | 基线（Task 0） |
|---|---|---|---|
| Core.Tests | 116 / 0 / 0 | 116 / 0 / 0 | 112 |
| Channels.Tests | 278 / 0 / 0 | 278 / 0 / 0 | 278 |
| Tcp.Tests | 129 / 0 / 0 | 129 / 0 / 0 | 129 |
| Udp.Tests | 38 / 0 / 0 | 38 / 0 / 0 | 38 |
| Serial.Tests | 63 / 0 / 2 | 63 / 0 / 2 | 63 + 2 跳过 |
| Plc.Tests | 152 / 0 / 0 | 152 / 0 / 0 | 152 |
| Melsec.Tests | 184 / 0 / 1 | 184 / 0 / 1 | 184 + 1 跳过 |
| Architecture.Tests | 11 / 0 / 0 | 11 / 0 / 0 | 2 |
| WebApi.Tests（新增） | 733 / 0 / 0 | 734 / 0 / 0 | — |
| Modbus.Tests | 405 / 0 / 0 | — | 405 |

第 4 节度量：

| 指标 | 基线 | P3.1 目标 | 最终 |
|---|---|---|---|
| 重复窗口（6 行） | 24（7 组） | 不增加；WebApi 内部与跨程序集为 0 | **33（8 组）**：新增的 9 个窗口全部是 `WebApiClient.cs` ↔ `SoapClient.cs` 的事件 `add` / `remove` 转发样板（公开类不能继承内部基类，新增公开基类不在固定的公开 API 里），审阅者已接受；其余 7 组与基线相同，WebApi 内部与对其他程序集为 0 |
| 超过 60 行的方法 | 0 | 0 | 0 |
| 超过 300 行的文件 | 9 | 不增加；WebApi 全部 ≤ 300 | 9；WebApi 84 个文件，最大 `AttemptLoop.cs` 180 行 |
| 生产 → 生产 IVT | 2 | 2 | 2（架构测试通过） |
| WebApi 有效行 | — | 记录 | 2735 |
| `WebApiClient.cs` / `SoapClient.cs` | — | 各 ≤ 200 | 108 / 121 行 |

