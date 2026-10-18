# P3.2 实施计划：WebApi HTTP 服务端（接收 MES 下发，Kestrel）

- 日期：2026-10-17；分支：`feat/webapi-p32`（执行者从 master `837f7d0` 建）。
- 执行者：另一个智能体。审阅者：在用户通知后验收，重点：代码质量、clean code、架构设计；功能完整性与缺陷检查。
- 依据：
  - 设计文档 `docs/superpowers/specs/2026-10-10-communication-core-design.md` 第 15.5 节（HTTP 服务端）、第 15.7、16、22 节、第 20.4 节（P3.1 实施修订）；
  - P3.1 计划 `docs/superpowers/plans/2026-10-11-p31-webapi-client.md`（P3-D1–D18 中与服务端相关的条目、第 17 节"3.2 已知待决问题"）；
  - 用户 2026-10-17 的决定（第 0 节 P32-D1、D3、D9、D11）；
  - P6.2 验收的教训（P6.2 计划第 19.1、19.4 节）：被摘出去的后台工作、服务器侧可观测的计数、释放不能释放仍在用的同步原语、代码不混进 `docs:` 提交。
- P3 的目标：MES 能向通讯库下发（**3.2，本计划**），通讯库也能主动上报 MES（3.1，已完成）。宿主注册路由与处理器，MES 用 HTTP + JSON 调用；第三方逻辑按阶段挂接：**事件**旁观，**拦截器**干预（与 3.1 的客户端一致）。
- **3.1 的客户端不改**：服务端是新的独立包，单向引用 `Junevy.Communication.WebApi` 的公共约定；客户端包仍是 net472;net8.0、只依赖 Core。
- 验收重点：功能完整、分层清晰、**代码质量**（第 2 节逐条检查）。

---

## 0. 已固定的设计决策（不同意的条目先在此表修改再执行）

| # | 决策 | 理由 |
|---|---|---|
| P32-D1 | **范围**：只做 REST / JSON 服务端。**不做 SOAP 服务端**（不解析 SOAP 信封、不按 `SOAPAction` 派发、不生成 WSDL），以后需要时另起计划 | 用户 2026-10-17 决定；现代 MES 的下发多为 HTTP + JSON |
| P32-D2 | **新包** `Junevy.Communication.WebApi.Server`（**仅 net8.0**），项目引用 `Junevy.Communication.WebApi`（复用 `ResponseEnvelopeOptions`、`WebApiAuthOptions`、`IWebApiSerializer` / `SystemTextJsonWebApiSerializer`、`WebApiLogOptions`）与 Core，另加 `<FrameworkReference Include="Microsoft.AspNetCore.App" />`。命名空间：`Junevy.Communication.WebApi.Server`（公开类型与不接触 ASP.NET Core 的内部逻辑）、`.Server.DependencyInjection`、`.Server.Hosting`（**只有这里引用 `Microsoft.AspNetCore.*`，全部 internal**，与 Mqtt / OpcUa 的 `Sdk` 命名空间同一规则）。测试项目 `Tests/Junevy.Communication.WebApi.Server.Tests`（net8.0）。版本 `1.0.0-preview.1`。**不新增生产 → 生产的 `InternalsVisibleTo`**：WebApi 里的 internal 类型（包络读取、凭据读取、事件队列、脱敏）服务端用不到就不用，用得到的在新包里按服务端的需要另写（服务端只"写"包络，客户端只"读"，不算重复） | 用户 2026-10-17 选择 Kestrel 并放弃服务端的 net472；拆包是为了不把 ASP.NET Core 运行时强加给只用客户端的人；Q16 |
| P32-D3 | **服务器：Kestrel**，以**嵌入**方式运行：每次 `StartAsync` 用 `WebApplication.CreateEmptyBuilder(...)` 建一个只含 Kestrel（`UseKestrelCore`）与路由（`AddRoutingCore`）的应用，**不读宿主进程的 `appsettings.json`、环境变量与命令行**（`ASPNETCORE_URLS` 之类不影响本库），不加任何默认日志提供程序。ASP.NET Core 自身的日志转给调用方的 `ILoggerFactory`，只转 Warning 及以上（与 Mqtt 桥接 SDK 日志的做法一致）。宿主本身就是 ASP.NET Core 应用时，README 建议直接用 Minimal API | 用户 2026-10-17 决定；Kestrel 免去 http.sys 的 URL ACL 与 `netsh` 证书绑定，是微软持续维护的默认服务器；`HttpListener` 官方不推荐用于新开发。使用方 AutomationSystem 是 net8.0-windows |
| P32-D4 | **状态**：`WebApiServerState { Stopped, Starting, Running, Stopping }`，自建在本包（不引用 Tcp 的 `ServerState`）。**不做故障自动重启**：Kestrel 的接收循环自己处理套接字错误，不存在 `HttpListener` 那种"监听意外停掉"的情形；设计 15.5 的 `RestartOnFault` 删除 | YAGNI；关闭 P3.1 待决问题 2 |
| P32-D5 | **路由**：`MapGet` / `MapDelete`（无正文）、`MapPost` / `MapPut`（JSON 正文），模板语法即 **ASP.NET Core 路由模板**（`/orders/{id}`、`{id:int}` 等约束照用），字面段优先由 ASP.NET Core 的路由匹配保证。同一方法下重复模板抛 `ArgumentException`（启动时由路由系统报出的冲突也要转成启动失败结果，不让异常逃出）；`StartAsync` 之后注册抛 `InvalidOperationException`。**404 / 405 也用本库的响应格式**（P32-D6），405 带 `Allow` 头 | 业界通用的模板语法，不自己写路由 |
| P32-D6 | **响应格式**：<br>• **启用业务包络**（`Envelope.Enabled`，复用 `ResponseEnvelopeOptions`）时，成功写成 `{ code: SuccessCodes[0], msg: "", data: … }`；处理器返回的业务失败写成 `{ code, msg }`，HTTP 状态码默认 **200**（`BusinessFailureStatusCode` 可改）；框架拒绝（400 / 401 / 404 / 405 / 408 / 409 / 413 / 415 / 422 / 500 / 503 / 504）用对应的 HTTP 状态码，正文同样是包络，`code` 为状态码文本；路径含 `.` 时建嵌套对象；<br>• **不启用包络**时，成功写出 `data` 本身（`data` 为 null 时 204）；业务失败与框架拒绝都写成 RFC 9457 Problem Details（用 ASP.NET Core 的 `ProblemDetails` 类型，`application/problem+json`，业务码放扩展字段 `code`），业务失败默认 400；<br>• 处理器可显式指定状态码（例如 201、202），显式值优先；<br>• 500 只返回通用消息，异常详情只写日志（设计 15.5） | 很多 MES 期望"无论成败都 HTTP 200、结果看 code"，与客户端的包络约定对称；不用包络时用业界标准的 Problem Details |
| P32-D7 | **请求正文**：`MapPost` / `MapPut` 的 `TRequest` 用 `IWebApiSerializer` 反序列化（`WebApiServerComponents.Serializer`，默认 `SystemTextJsonWebApiSerializer`，与客户端同样的选项；**不用 Minimal API 的参数绑定**，以保证序列化器可替换且与客户端一致）。`TRequest` 为 `byte[]` 时给原始正文，为 `string` 时按请求的 charset（缺省 UTF-8）解码。有正文但 `Content-Type` 不是 JSON（`application/json` 或 `*/*+json`）→ 415；正文为空 → 400；JSON 无法反序列化 → 400（详情只写日志）；超过 `MaxRequestBodySize` → 413（交给 Kestrel 的 `MaxRequestBodySize` 限制，读取时它抛出的异常转成本库格式的 413）。启用包络时**请求**不解包 | 请求包络各家 MES 不统一，由宿主的 `TRequest` 自己建模 |
| P32-D8 | **并发与超时**：<br>• 每个请求在线程池上处理，线程安全由宿主负责（设计 15.5）；<br>• 同时处理的请求超过 `MaxConcurrentRequests`（默认 16）立即 503 + `Retry-After: 1`，**不排队**（用 ASP.NET Core 的 `RateLimiter` 中间件 + 并发限流器，队列长度 0，拒绝时写本库格式）；<br>• `RequestTimeout`（默认 30000 ms）覆盖"读正文 + 拦截器 + 处理器"，**由本库自己计时**（`TimeoutScope` + `Task.WaitAsync`），不依赖 ASP.NET Core 的 `RequestTimeouts` 中间件——后者只取消令牌，处理器不理令牌时不会按时回 504。读正文超时 → 408，其余超时 → 504；**处理器不理令牌而继续运行时，响应已经发出，处理器之后的结果丢弃并记 Warning**（P6.2 R1 的同类）；慢速攻击交给 Kestrel 的 `RequestHeadersTimeout` 与 `MinRequestBodyDataRate`（保持默认）；<br>• 处理器抛异常 → 500，记 Error；<br>• 对端已断开（`RequestAborted` 触发或写入失败）→ 记 Debug，不抛异常，`RequestCompleted` 的结果为 `ClientDisconnected` | 设计 15.5；能用框架的部分用框架，框架做不到的（不理令牌的处理器）自己做 |
| P32-D9 | **认证：默认不校验**（用户 2026-10-17 决定）。可选内置 Basic / Bearer / ApiKey，**配置形状复用客户端的 `WebApiAuthOptions`**（`Scheme` + 环境变量名，读出的是"期望的凭据"，构造时读取，缺失抛 `ArgumentException`，消息只含变量名；读取逻辑在本包里按服务端需要实现，见 P32-D2）；或由 `WebApiServerComponents.Authenticator`（`IWebApiRequestAuthenticator`）自定义，设置后取代配置。比较凭据用 `CryptographicOperations.FixedTimeEquals`。失败 → 401，Basic / Bearer 带 `WWW-Authenticate`。**不接 ASP.NET Core 的认证方案体系**（Basic 不是内置方案，三种方案的校验各十几行，接方案体系反而更重）。**监听所有网卡（主机为 `*`、`+`、`0.0.0.0` 或 `[::]`）却没有配置认证时，`StartAsync` 记一条 Warning** | 关闭 P3.1 待决问题 4；多数 MES 在隔离内网运行，但要让裸奔"看得见" |
| P32-D10 | **钩子 = 事件（旁观）+ 拦截器（干预）**，与客户端同一套思路：<br>• 事件：`RequestReceived`（路由之前）、`RequestCompleted`（**每个请求恰好一次**，含被拒绝、超时、对端断开、去重重放）、`StateChanged`（状态只在生命周期锁内变化，按变化顺序触发）。订阅者异常逐个捕获记 Error（`EventSubscribers.InvokeEach`）；<br>• 拦截器 `IWebApiServerInterceptor`：`OnRequestAsync`（认证通过之后、去重与处理器之前，可调用 `Reject` 直接给出响应）与 `OnRespondingAsync`（响应写出之前，可加响应头）。请求阶段按注册顺序，响应阶段按逆序；拦截器抛异常 → 500，记 Error；<br>• 服务端事件参数自成一套，不复用客户端的 `WebApiCallEventArgs`（P3.1 第 17 节的约定）。**不把 ASP.NET Core 的中间件、`HttpContext` 暴露给宿主**：拦截器拿到的是本库的上下文类型，以后换实现不破坏公开 API | 关闭 P3.1 待决问题 3；签名校验、IP 白名单、审计都靠拦截器 |
| P32-D11 | **重复下发去重：内置、可选、默认关闭**（用户 2026-10-17 决定）。开启后按请求头（默认 `Idempotency-Key`）+ 方法 + 路径去重：<br>• 窗口内（默认 10 分钟）同键已完成的请求 → 直接重放第一次的响应（状态码、`Content-Type`、正文），加响应头 `Idempotent-Replayed: true`，处理器不执行；<br>• 同键请求还在处理中 → 409；<br>• 同键但正文不同（SHA-256 比较）→ 422；<br>• **只缓存处理器给出的响应**（成功与业务失败）；框架拒绝与 500 不缓存，可以重试；<br>• **处理器超时（504）之后仍在运行时，该键保持"处理中"直到处理器真正结束，然后缓存它的最终结果**——否则 MES 在 504 后重发会让同一条命令执行两次；<br>• 条目上限 `MaxEntries`（默认 1000），满了先淘汰最旧的；没有该请求头的请求不参与去重；时钟用 `TimeProvider`（net8 内置）注入，测试可推进时间 | 用户决定；MES 超时重发是现场最常见的重复执行来源 |
| P32-D12 | **生命周期**：<br>• 构造时校验配置并拍快照（与客户端、通道族一致）；<br>• `StartAsync` 返回 `CommResult`（与 `TcpServer.StartAsync` 一致，修订设计 15.5 的 `WebApiResult<bool>`）：已启动 → `InvalidRequest`；没有注册任何路由 → `InvalidRequest`；端口被占用 → `ResourceExhausted`；无权绑定（Windows 上端口落在系统保留的排除范围内、Linux 上 1024 以下端口）→ `AuthenticationFailed`，消息说明原因；HTTPS 证书加载失败 → `InvalidRequest`；其他 → `Unspecified`；任何失败都不留下半启动的应用；<br>• 每次 `StartAsync` 新建一个应用（ASP.NET Core 应用停止后不能再启动），停止后可以再启动；<br>• `StopAsync`：停止接收，等在途请求最多 `StopTimeout`（默认 5000 ms），之后中止剩余连接、取消处理器令牌；可重复调用；<br>• `Dispose` / `DisposeAsync` = 停止 + 释放，可重复调用；释放后 `StartAsync` 与 `Map*` 抛 `ObjectDisposedException`、`StopAsync` 无操作；**释放不释放仍可能被在途请求使用的同步原语**（P6.1 R3、P6.2 R10） | P6-C8 的释放规则 |
| P32-D13 | **地址与 HTTPS**：`Urls` 用 Kestrel 的写法（`http://*:8080`、`http://localhost:8080`、`https://0.0.0.0:8443`），默认 `{ "http://*:8080" }`；端口可写 0（由系统分配，主要给测试用），启动后 `IWebApiServer.Urls` 返回实际绑定的地址。HTTPS 在程序里配置证书：`Https.CertificatePath`（.pfx）+ `Https.CertificatePasswordVariable`（环境变量名），或 `WebApiServerComponents.Certificate`（`X509Certificate2`，设置后优先）。**没有任何 `netsh` 步骤**；README 的部署一节只剩防火墙放行与"ASP.NET Core 运行时"两条 | Kestrel 的标准做法 |
| P32-D14 | **工厂与依赖注入**：`WebApiServerFactory : NamedFactory<IWebApiServer>`（`GetOrAdd(name, config, components?)`）与 `WebApiServerServiceCollectionExtensions.AddWebApiServers()`（可重复调用，与 `AddWebApiClients()` 互不影响），都在新包里 | 与所有已有包一致（`NamedFactory` 先例） |
| P32-D15 | **日志**：状态变化 Information；每个请求 Debug（方法、路由模板、状态码、耗时）；框架拒绝 Warning（401、413、503 等）；处理器异常 Error。正文只在 `Logging.LogBodies` 开启时记录，`RedactedHeaders`、`RedactedQueryParameters` 只输出 `***`（复用客户端的 `WebApiLogOptions` 形状） | 设计第 22 节；P3-D17 |
| P32-D16 | **可测性**：管线拆成可单元测试的内部类型（响应写入、正文读取、认证校验、去重缓存、结果分类），它们不接触 ASP.NET Core，只有 `Hosting` 里的类型接触 `HttpContext`；集成用例用真实 Kestrel 监听 `http://127.0.0.1:0`（不需要管理员，不弹防火墙）。**内部暴露在途请求数与去重条目数**（经新包对自己测试项目的 `InternalsVisibleTo`）供用例断言（P6.2 的教训） | Kestrel 绑定回环地址不需要任何授权 |

---

## 1. 执行规则（每个 Task 都适用）

1. 每个 Task 一个 commit（提交信息见各 Task），在 `feat/webapi-p32` 上提交。不 push、不 amend、不 rebase、不切换分支。**只 `git add` 本 Task 的文件；生产代码不混进 `docs:` / `chore:` 提交**（P6.2 的流程问题）。
2. 步骤顺序固定：**先写测试并确认失败** → 写生产代码 → 测试通过 → 按第 2 节自查并清理 → 更新文档 → 提交。
3. 每个 Task 完成后依次运行，全部成功才能提交：
   ```powershell
   dotnet build Junevy.Communication.slnx -c Debug
   dotnet build Junevy.Communication.slnx -c Release
   pwsh build/test-affected.ps1 -Base <起点提交> -NoBuild
   ```
   起点提交是 Task 0 开始时的 HEAD（记入第 19.3 节）。本期改动只在新包，通常只选中 `WebApi.Server.Tests` 与 `Architecture.Tests`；**`WebApi.Tests` 不会被选中，因为客户端包不改**——如果脚本选中了它，说明动了客户端，停下检查。
4. 代码约束沿用 P3.1：只异步 API、库内每个 `await` 都 `ConfigureAwait(false)`、文件作用域命名空间、英文错误消息与日志、中文 XML 注释、日志模板为常量。新包只有 net8.0，不需要条件编译。
5. CHANGELOG：顶部新增一节 `## [Unreleased] — WebApi P3.2（WebApi.Server 1.0.0-preview.1：HTTP 服务端）`，下设 `### 新增（Added）`、`### 变更（Changed）`，条目格式 `- YYYY-MM-DD [WebApi.Server] 内容`。
6. 不修改：`Junevy.Communication.WebApi/` 的生产代码与 `Tests/Junevy.Communication.WebApi.Tests/`（3.1 客户端不改）、Core 与其他所有生产程序集、`Junevy.Communication.Modbus/`、`Tests/Junevy.Communication.Modbus.Tests/`、`Skills/using-junevy-modbus/`、共享测试库 `Tests/Junevy.Communication.Testing`（改它会让测试选择扩大到接近全量）。架构测试项目只做第 3 节的登记。
7. 知识库：只在 Task 8 回写，**不提交**；汇报时附 `git status --short -- Junevy.Communication.Wiki` 与 `git diff --stat -- Junevy.Communication.Wiki`。
8. 本计划之外的问题记入第 18 节，只记录不修改。偏离计划的地方记入第 19.2 节并在汇报中说明；**影响第 6–13 节公开 API 的偏离先停下汇报**。
9. 每个 Task 的汇报包含：新增 / 修改 / 删除文件；测试先红后绿的证据；第 2 节逐条自查；本 Task 涉及的度量；三条命令的结果（通过 / 失败 / 跳过数）。
10. 套接字用例放进测试项目自己的 `SocketTiming` 集合（集合内串行），只监听 `http://127.0.0.1:0` / `https://127.0.0.1:0`（**不监听所有网卡**），耗时断言用区间，不用 `Thread.Sleep`。
11. 本期没有串口测试。万一需要运行串口用例：只允许 COM20 / COM21，**绝不打开 COM3 / COM4**。

---

## 2. 代码质量标准（审阅者逐条验收，不符合即打回）

P2 计划 Q1–Q15、P2.5 计划 Q16–Q20、P3.1 计划 Q21–Q24 全部适用（文件 ≤ 300 行、方法 ≤ 60 行、不重复、依赖方向、可见性最小化、无魔法数、命名、注释、无死代码、异步、错误处理、资源、入口校验、测试覆盖、测试确定、测试干净、不新增生产 → 生产 IVT、公开 API 只增加列出的成员、抽取后删除旧代码、分层、不碰进程级状态）。补充：

- Q25 **管线单一来源**：限流、正文、认证、拦截器、去重、超时、响应格式、事件各自只在一个内部类型里实现；`WebApiServer.cs` 是薄外观，不超过 200 行；`HttpContext` 只出现在 `Hosting` 命名空间里。
- Q26 **每个请求恰好一个 `RequestCompleted`**：第 16 节的每条路径都有断言"恰好一次"的用例。
- Q27 **被摘出去的工作有用例**：超时后处理器继续运行、停止时处理器继续运行、去重键在 504 之后仍是"处理中"，三条各有用例，并断言没有未观察的任务异常（Q40）。
- Q28 **服务器侧可观测**：限流、停止、去重的用例用内部的在途请求数与去重条目数断言，而不只看 HTTP 响应。
- Q29 **不受宿主进程环境影响**：设置 `ASPNETCORE_URLS`、`ASPNETCORE_ENVIRONMENT` 与工作目录下放一份 `appsettings.json` 都不改变服务器的行为（有用例）。
- Q30 **ASP.NET Core 关在 `Hosting` 里**：公开成员的签名里不出现 `Microsoft.AspNetCore.*` 类型，`Hosting` 之外的类型（含方法体）不引用它（由已有的 `SdkAdapterArchitectureTests` 守住，见第 3 节）。

---

## 3. 程序集分层与架构测试

- `ArchitectureTests.LayerTable` 在 `#if NET8_0_OR_GREATER` 里加一行：`Junevy.Communication.WebApi.Server` 只允许引用 Core 与 WebApi；`Architecture.Tests.csproj` 对新包的项目引用加 net8.0 条件（与 Mqtt、OpcUa 相同）。
- `SdkAdapterArchitectureTests.Adapters` 加一行（`Junevy.Communication.WebApi.Server`、前缀 `Microsoft.AspNetCore`、命名空间 `Junevy.Communication.WebApi.Server.Hosting`），`ArchitectureTests.SdkPrefixes` 同步登记：已有守卫随即检查"公开 API 不出现 ASP.NET Core 类型、只有 `Hosting` 引用它"（Q30）。不新写守卫。
- `WebApiLayeringTests` 里"`Server`（3.2）的依赖由 3.2 计划确定，届时在此加一行"的注释改为"服务端是独立包 `Junevy.Communication.WebApi.Server`，见 P3.2 计划"，分层表不变（客户端包里不会出现 `Server` 命名空间）。
- 负控：临时让新包的某个公开方法暴露 `HttpContext`、再临时在根命名空间的一个类型里引用 `HttpContext`，确认守卫各报一次错，再改回。若已有守卫的实现对"前缀 `Microsoft.AspNetCore`、来自共享框架而不是 NuGet 包"判断不了，记入第 19.2 节并停下汇报。

---

## 4. 度量（Task 0 记录基线，Task 9 复测）

| 指标 | 命令 | 基线（P6.2 结束时） | P3.2 目标 |
|---|---|---|---|
| 生产重复窗口（6 行） | `python build/metrics/dupscan.py 6 <生产目录的 *.cs，加入新包>` | 34（9 个集群） | 不增加；新包内部、新包与其他程序集之间都为 0 |
| 测试重复窗口 | 同上，`Tests` 下全部 `*.cs` | 184（47 个集群） | 新增窗口只在新测试项目内部，且不超过 10 个 |
| 超过 60 行的方法 | `python build/metrics/methodlen.py 61 <文件>` | 生产 0 | 生产 0 |
| 超过 300 行的文件 | `[IO.File]::ReadAllLines(p).Count`（不要用 `Measure-Object -Line`） | 生产 9 | 不增加；新包的文件全部 ≤ 300 行 |
| 生产 → 生产 IVT | 架构测试 | 2 | 2 |
| 有效行 | `python build/metrics/effective_lines.py <目录>` | WebApi 2735 | WebApi 不变；记录新包有效行 |

---

## 5. Task 0：基线与嵌入式 Kestrel 调研

1. 从 master `837f7d0` 建分支 `feat/webapi-p32`，记下起点提交；运行两条构建命令，再只跑本期会被选中的测试项目作为基线（`Tests/Junevy.Communication.Architecture.Tests`，两个目标框架，**不跑全量**），记录通过 / 失败 / 跳过数；按第 4 节记录度量基线。
2. **调研**：在仓库之外的临时目录建 net8.0 工程（不提交），以**普通用户**身份确认下列事项，写入第 19.2 节（写明用到的 API 与观测到的现象）：
   1. `WebApplication.CreateEmptyBuilder` + `UseKestrelCore` + `AddRoutingCore` 在一个**非 Web 的宿主进程**（控制台，以及一个 net8.0-windows 的 WPF 空工程）里启动、处理请求、停止；设置 `ASPNETCORE_URLS` 与放一份 `appsettings.json` 时确认不受影响；
   2. 引用了 `FrameworkReference Microsoft.AspNetCore.App` 的类库被 WPF 工程引用时，构建与运行是否正常、`*.runtimeconfig.json` 是否带上 `Microsoft.AspNetCore.App`；与 Core 的 `Microsoft.Extensions.*.Abstractions` 8.0.0 是否有 NU1605 或版本冲突；
   3. 端口写 0 时怎样取到实际绑定的地址（`IServerAddressesFeature`）；
   4. 端口被占用、无权绑定（Windows 上用 `netsh int ipv4 show excludedportrange protocol=tcp` 找一个保留端口试，找不到就只记录）时 `StartAsync` 抛出的异常类型；
   5. `StopAsync` 的优雅停止：在途请求怎样等待、超时后怎样中止，处理器的 `RequestAborted` 何时触发；停止后同一端口能否立刻被新应用绑定；
   6. 超过 `MaxRequestBodySize` 时读取正文抛出的异常类型；没有命中路由与方法不匹配时框架默认的状态码（404 / 405），以及怎样改写成本库格式；
   7. `RateLimiter` 中间件的并发限流器（队列 0）拒绝时的回调与状态码设置；
   8. ASP.NET Core 自身日志怎样只把 Warning 及以上转给调用方的 `ILoggerFactory`；
   9. 用 `CertificateRequest` 生成自签名证书、Kestrel 在 `https://127.0.0.1:0` 上用它监听，`HttpClient`（自定义证书校验回调）能否访问。
3. **停止条件**：第 2.1 或 2.2 条做不到（嵌入式 Kestrel 在 WPF 进程里跑不起来，或与 Core 的依赖冲突无法消解）时停下汇报，等审阅者决定。
4. 提交：`chore: record the P3.2 baseline and Kestrel survey`（只含本计划文件）。

---

## 里程碑 1：骨架、模型与纯逻辑

### 6. Task 1：项目骨架、公开模型、选项与校验

**公开 API（固定）**

```csharp
namespace Junevy.Communication.WebApi.Server;

public enum WebApiServerState { Stopped, Starting, Running, Stopping }

public sealed class WebApiServerStateChangedEventArgs : EventArgs
{
    public WebApiServerStateChangedEventArgs(string serverName, WebApiServerState previous, WebApiServerState current, string? reason, Exception? exception);
    public string ServerName { get; }
    public WebApiServerState Previous { get; }
    public WebApiServerState Current { get; }
    public string? Reason { get; }
    public Exception? Exception { get; }          // 只作诊断
}

public sealed class WebApiServerConfig
{
    public string[] Urls { get; set; } = { "http://*:8080" };     // Kestrel 的地址写法；端口可为 0
    public WebApiServerHttpsOptions Https { get; set; } = new();   // 只有 https 地址时才需要
    public WebApiAuthOptions Auth { get; set; } = new();           // 默认 None（P32-D9）
    public int MaxConcurrentRequests { get; set; } = 16;
    public int RequestTimeout { get; set; } = 30000;               // 毫秒
    public long MaxRequestBodySize { get; set; } = 1048576;        // 字节
    public int StopTimeout { get; set; } = 5000;                   // 毫秒
    public ResponseEnvelopeOptions Envelope { get; set; } = new();
    public int BusinessFailureStatusCode { get; set; } = 200;      // 只在启用包络时生效
    public WebApiServerDeduplicationOptions Deduplication { get; set; } = new();
    public WebApiLogOptions Logging { get; set; } = new();
}

public sealed class WebApiServerHttpsOptions
{
    public string? CertificatePath { get; set; }                    // .pfx
    public string? CertificatePasswordVariable { get; set; }        // 环境变量名；证书无密码时为 null
}

public sealed class WebApiServerDeduplicationOptions
{
    public bool Enabled { get; set; }                               // 默认关闭（P32-D11）
    public string HeaderName { get; set; } = "Idempotency-Key";
    public int Window { get; set; } = 600000;                       // 毫秒
    public int MaxEntries { get; set; } = 1000;
}

public sealed class WebApiServerComponents
{
    public IList<IWebApiServerInterceptor> Interceptors { get; } = new List<IWebApiServerInterceptor>();
    public IWebApiSerializer? Serializer { get; set; }              // null：SystemTextJsonWebApiSerializer
    public IWebApiRequestAuthenticator? Authenticator { get; set; } // 设置后取代 Auth 配置
    public X509Certificate2? Certificate { get; set; }              // 设置后取代 Https 配置；不由服务器释放
    public TimeProvider? TimeProvider { get; set; }                 // null：TimeProvider.System（去重窗口用）
}
```

1. 新建 `Junevy.Communication.WebApi.Server`（net8.0，`FrameworkReference Microsoft.AspNetCore.App`，项目引用 WebApi 与 Core，英文 README 打包，`InternalsVisibleTo` 只给自己的测试项目）与 `Tests/Junevy.Communication.WebApi.Server.Tests`（net8.0，引用共享测试库）；加入 slnx。
2. 校验（构造 `WebApiServer` 时调用，`ArgumentException` 的 `ParamName` 为 `config.<属性>`，消息写明允许范围）：
   - `Urls` 非空，每项是 `http` / `https` 的绝对地址、端口 0–65535、不重复；有 https 地址时必须有证书来源（`Components.Certificate` 或 `Https.CertificatePath`）；
   - `MaxConcurrentRequests`、`RequestTimeout`、`MaxRequestBodySize`、`StopTimeout` > 0；`BusinessFailureStatusCode` 在 200–599；
   - 启用包络时三条路径非空、`SuccessCodes` 至少一项，且序列化器的 `ContentType` 必须是 JSON；
   - 去重：`HeaderName` 是合法的头名称，`Window`、`MaxEntries` > 0；
   - `Auth` 按客户端同样的规则读取环境变量（变量缺失抛 `ArgumentException`，消息只含变量名与 `config.Auth.<属性>`）；`Https.CertificatePasswordVariable` 同理。
3. 测试：每条校验一条用例；默认值逐项断言；快照（构造后改配置对象不生效）。
4. 架构测试：第 3 节的登记与负控。
5. 提交：`feat(webapi-server): add the server package skeleton, model and options`

### 7. Task 2：响应格式与请求正文（P32-D6、D7）

**公开 API（固定）**

```csharp
namespace Junevy.Communication.WebApi.Server;

public sealed class WebApiServerResponse<T>
{
    public static WebApiServerResponse<T> Ok(T? data, int? statusCode = null);                       // 成功；statusCode 例如 201、202
    public static WebApiServerResponse<T> Fail(string code, string message, int? statusCode = null); // 业务失败
    public bool IsSuccess { get; }
    public T? Data { get; }
    public int? StatusCode { get; }                     // null：按 P32-D6 的默认值
    public string? Code { get; }                        // 业务码；成功时为 null
    public string? Message { get; }
    public IDictionary<string, string> Headers { get; } // 额外响应头
}
```

1. 内部 `ResponseFormatter`（包络 / 非包络 / Problem Details / 204 / 显式状态码 / 额外头；包络的 `data` 以序列化器的输出原样嵌入，用 `Utf8JsonWriter.WriteRawValue`）与 `RequestBodyReader`（`Content-Type`、charset、反序列化、`byte[]` / `string` 原样、空正文）。两者都不接触 `HttpContext`，输入输出是字节与头。
2. 测试（纯单元）：P32-D6 每种组合一条（包络 × 成功 / 业务失败 / 框架拒绝；非包络 × 成功 / null 数据 / 业务失败 / 框架拒绝；显式状态码；嵌套路径）；P32-D7 每条规则一条；**往返用例：服务端写出的包络交给 3.1 客户端解析（`WebApiClient` + 启用包络，经一个返回固定字节的 `HttpMessageHandler`），客户端拿到同样的 `data`、业务失败拿到 `RemoteError` 与业务码**——两端约定一致的证据。
3. 提交：`feat(webapi-server): format envelope and problem responses and read request bodies`

---

## 里程碑 2：管线

### 8. Task 3：路由与请求管线（P32-D5、D8）

**公开 API（固定）**

```csharp
namespace Junevy.Communication.WebApi.Server;

public sealed class WebApiServerContext                // 一次请求，处理器与拦截器共用
{
    public string ServerName { get; }
    public long RequestId { get; }                     // 本服务器内递增
    public string Method { get; }
    public string Path { get; }                        // 例如 "/orders/42"
    public string? RouteTemplate { get; }              // 命中的模板；未命中为 null
    public IReadOnlyDictionary<string, string> RouteValues { get; }
    public IReadOnlyDictionary<string, string> Query { get; }    // 同名参数取第一个
    public IReadOnlyDictionary<string, string> Headers { get; }  // 名称不区分大小写
    public string? ContentType { get; }
    public byte[] Body { get; }                        // 原始正文
    public IPEndPoint? RemoteEndPoint { get; }
    public string? UserName { get; }                   // 认证通过时的身份；未认证为 null
    public IDictionary<string, object?> Items { get; } // 拦截器与处理器之间传值
}

public enum WebApiServerOutcome
{
    Succeeded,           // 处理器成功
    BusinessFailure,     // 处理器返回 Fail
    Rejected,            // 框架或拦截器拒绝（400 / 401 / 404 / 405 / 408 / 409 / 413 / 415 / 422 / 503）
    HandlerFailed,       // 处理器或拦截器抛异常（500）
    TimedOut,            // 504
    Replayed,            // 去重重放
    ClientDisconnected,  // 响应没能写出
}

public sealed class WebApiServerRequestEventArgs : EventArgs           // RequestReceived
{
    public string ServerName { get; }
    public long RequestId { get; }
    public string Method { get; }
    public string Path { get; }
    public IPEndPoint? RemoteEndPoint { get; }
}

public sealed class WebApiServerCompletedEventArgs : EventArgs         // RequestCompleted
{
    public string ServerName { get; }
    public long RequestId { get; }
    public string Method { get; }
    public string Path { get; }
    public string? RouteTemplate { get; }
    public int StatusCode { get; }
    public WebApiServerOutcome Outcome { get; }
    public string? Code { get; }                 // 业务码，或拒绝时的状态码文本
    public string? Message { get; }
    public Exception? Exception { get; }         // 只作诊断
    public TimeSpan Elapsed { get; }
}
```

1. 内部 `RequestPipeline`：固定顺序 `RequestReceived` → 限流 → 路由 → 正文 → 认证（Task 4 前放行）→ 请求拦截器（Task 4 前为空）→ 去重（Task 5 前放行）→ 处理器（带超时）→ 响应拦截器 → 写响应 → `RequestCompleted`。404 / 405 / 413 / 503 等框架层面的拒绝也经过同一个写响应与 `RequestCompleted` 出口。
2. 本 Task 的集成用例用一个最小的内部启动帮助类在 `http://127.0.0.1:0` 上跑真实 Kestrel（Task 6 再换成公开的 `WebApiServer`）：
   - 四种方法各一条往返；模板参数与约束；404；405 + `Allow`；
   - 每种 `Outcome` 一条，断言状态码、正文与 `RequestCompleted` **恰好一次**；
   - 限流：第 `MaxConcurrentRequests + 1` 个并发请求立即 503 + `Retry-After`，内部在途计数回到 0；
   - 超时：读正文超时 408；处理器超时 504；**处理器不理令牌、超时后才完成：响应仍是 504，之后记一条 Warning，`RequestCompleted` 不再触发第二次**；
   - 处理器异常 500，响应正文不含异常消息，日志含异常；
   - 正文超限 413；对端断开 → `ClientDisconnected`，不抛出；
   - 事件订阅者抛异常不影响响应与其他订阅者；
   - 强制 GC 后没有未观察的任务异常（Q40）。
3. 提交：`feat(webapi-server): route requests through the server pipeline`

### 9. Task 4：认证与拦截器（P32-D9、D10）

**公开 API（固定）**

```csharp
namespace Junevy.Communication.WebApi.Server;

public interface IWebApiRequestAuthenticator
{
    Task<WebApiAuthenticationResult> AuthenticateAsync(WebApiServerContext context, CancellationToken cancellationToken);
}

public sealed class WebApiAuthenticationResult
{
    public static WebApiAuthenticationResult Success(string? userName = null);
    public static WebApiAuthenticationResult Fail(string message);
    public bool IsAuthenticated { get; }
    public string? UserName { get; }
    public string? FailureMessage { get; }      // 只写日志，不回给调用方
}

public interface IWebApiServerInterceptor
{
    Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken);
    Task OnRespondingAsync(WebApiServerRespondingContext context, CancellationToken cancellationToken);
}

public abstract class WebApiServerInterceptor : IWebApiServerInterceptor   // 两个方法都是空实现
{
    public virtual Task OnRequestAsync(WebApiServerRequestingContext context, CancellationToken cancellationToken);
    public virtual Task OnRespondingAsync(WebApiServerRespondingContext context, CancellationToken cancellationToken);
}

public sealed class WebApiServerRequestingContext
{
    public WebApiServerContext Request { get; }
    public void Reject(int statusCode, string code, string message);        // 4xx / 5xx；之后的拦截器与处理器不再执行
}

public sealed class WebApiServerRespondingContext
{
    public WebApiServerContext Request { get; }
    public int StatusCode { get; }
    public WebApiServerOutcome Outcome { get; }
    public IDictionary<string, string> Headers { get; }                     // 可追加响应头
}
```

1. 内置 Basic / Bearer / ApiKey 校验器（由 `WebApiAuthOptions` 创建，`FixedTimeEquals` 比较），失败 401 + `WWW-Authenticate`（Basic：`Basic realm="<ServerName>"`；Bearer：`Bearer`）；自定义认证器取代配置；认证器抛异常 → 500。
2. 拦截器顺序与 `Reject`、异常处理按 P32-D10。
3. 测试：三种内置方案的成功与失败（缺头、格式错、凭据错）；凭据错时的响应不泄漏期望值；自定义认证器优先；`UserName` 传到处理器；拦截器请求阶段按注册顺序、响应阶段按逆序；`Reject` 截断后续拦截器与处理器且 `Outcome = Rejected`；拦截器抛异常 → 500；"监听所有网卡却没配认证"的判定函数单元测试（Warning 本身在 Task 6 断言）。
4. 提交：`feat(webapi-server): authenticate requests and add server interceptors`

### 10. Task 5：重复下发去重（P32-D11）

1. 内部 `DeduplicationCache`：键（请求头值 + 方法 + 路径）、正文摘要、状态（处理中 / 已完成 + 缓存的响应）、窗口过期（`TimeProvider`）、上限淘汰。
2. 测试：
   - 关闭时同键请求都执行处理器；
   - 开启后：已完成 → 重放（状态码、`Content-Type`、正文一致，带 `Idempotent-Replayed: true`，处理器只执行一次，`Outcome = Replayed`）；处理中 → 409；同键不同正文 → 422；没有该头 → 不参与；
   - 框架拒绝与 500 不缓存（之后同键请求照常执行）；
   - **处理器超时（504）之后仍在运行：同键请求 409，直到处理器结束；结束后同键请求重放它的最终结果**；
   - 用 `FakeTimeProvider`（或测试内的 `TimeProvider` 子类）推进时间：过期后重新执行；超过 `MaxEntries` 淘汰最旧的；内部条目数断言（Q28）。
3. 提交：`feat(webapi-server): deduplicate repeated requests by idempotency key`

---

## 里程碑 3：外观、宿主与依赖注入

### 11. Task 6：服务端外观与生命周期（P32-D3、D4、D12、D13）

**公开 API（固定）**

```csharp
namespace Junevy.Communication.WebApi.Server;

public interface IWebApiServer : IDisposable, IAsyncDisposable
{
    string Name { get; }
    WebApiServerState State { get; }
    IReadOnlyList<string> Urls { get; }          // 启动前为配置值；启动后为实际绑定的地址（端口 0 已解析）
    event EventHandler<WebApiServerStateChangedEventArgs>? StateChanged;
    event EventHandler<WebApiServerRequestEventArgs>? RequestReceived;
    event EventHandler<WebApiServerCompletedEventArgs>? RequestCompleted;

    Task<CommResult> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);

    IWebApiServer MapGet<TResponse>(string template, Func<WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler);
    IWebApiServer MapDelete<TResponse>(string template, Func<WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler);
    IWebApiServer MapPost<TRequest, TResponse>(string template, Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler);
    IWebApiServer MapPut<TRequest, TResponse>(string template, Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler);
}

public sealed class WebApiServer : IWebApiServer
{
    public WebApiServer(string name, WebApiServerConfig config, WebApiServerComponents? components = null, ILoggerFactory? loggerFactory = null);
    // IWebApiServer 的全部成员
}
```

1. 内部 `KestrelHost`：按 P32-D3 建应用、绑定地址与证书、挂中间件（限流、路由、管线）、启动失败分类、优雅停止；状态机只在生命周期锁内变化。
2. 测试（真实 Kestrel，`http://127.0.0.1:0`）：
   - `Urls` 在启动后返回实际端口；
   - 生命周期：`Stopped → Starting → Running → Stopping → Stopped`；重复启动 `InvalidRequest`；没有路由 `InvalidRequest`；端口被 `TcpListener` 占用 → `ResourceExhausted`；启动失败后状态回到 `Stopped`、没有残留的应用；停止后可再启动（同一端口）；`StopTimeout` 内等在途请求完成，超过则中止，处理器令牌被取消；**停止时仍在运行的处理器不让停止或释放抛异常、不留未观察异常**；释放规则（P32-D12）；
   - HTTPS：测试内用 `CertificateRequest` 生成自签名证书，经 `Components.Certificate` 在 `https://127.0.0.1:0` 上监听，`HttpClient` 用自定义校验回调访问成功；
   - 不受宿主进程环境影响（Q29）：设置 `ASPNETCORE_URLS`、在工作目录放 `appsettings.json` 后，服务器仍只监听配置的地址；
   - ASP.NET Core 自身的 Warning 日志转到调用方的 `ILoggerFactory`，Information 及以下不转；
   - 监听 `http://*:0` 且没配认证时记一条 Warning（只断言日志，用例结束立即停止）；
   - **与 3.1 客户端的真实往返**：本包服务器 + 3.1 的 `WebApiClient`（启用包络）：成功、业务失败（客户端拿到 `RemoteError` 与业务码）、401（客户端拿到 `AuthenticationFailed`）、去重开启时客户端带 `IdempotencyKey` 重试只执行一次处理器。
3. 提交：`feat(webapi-server): host the server on embedded Kestrel`

### 12. Task 7：工厂与依赖注入（P32-D14）

**公开 API（固定）**

```csharp
namespace Junevy.Communication.WebApi.Server;

public sealed class WebApiServerFactory : NamedFactory<IWebApiServer>
{
    public WebApiServerFactory(ILoggerFactory? loggerFactory = null);
    public IWebApiServer GetOrAdd(string name, WebApiServerConfig config, WebApiServerComponents? components = null);
}

namespace Junevy.Communication.WebApi.Server.DependencyInjection;

public static class WebApiServerServiceCollectionExtensions
{
    public static IServiceCollection AddWebApiServers(this IServiceCollection services);   // 可重复调用；不影响 AddWebApiClients
}
```

1. 测试同 `WebApiClientFactoryTests` 与 `WebApiDependencyInjectionTests` 的形状（同名同实例、不同名、null / 非法配置、释放与重复释放、释放后调用、取快照、appsettings 形状的 JSON 绑定、可重复注册、容器日志工厂透传）；`AddWebApiClients()` 与 `AddWebApiServers()` 同时调用互不影响。
2. 提交：`feat(webapi-server): add the server factory and DI registration`

---

## 里程碑 4：文档与验收

### 13. Task 8：README、Skill、CHANGELOG、设计文档与知识库

1. `Junevy.Communication.WebApi.Server/README.md`（英文，打包）：
   - **开头写明**：只支持 .NET 8；目标机器需要 ASP.NET Core 8 运行时（框架依赖部署时装 "ASP.NET Core Runtime 8"，只装 Desktop Runtime 不够；自包含发布会一并打包）；
   - 快速上手（注册路由、启动、停止）、路由模板、响应格式（包络 / Problem Details 两个示例）、请求正文规则、并发与超时、认证、拦截器与事件、去重（何时开、MES 要带什么头）、生命周期、状态码表（第 16 节）、HTTPS、**部署**（防火墙放行；不需要 `netsh`；宿主本身是 ASP.NET Core 应用时直接用 Minimal API）、未验证清单（未与真实 MES 联调）；
   - 示例经编译验证（参照 `WebApi.Tests` 的 `ReadmeSampleTests` 做法，先做负控）。
2. Skill `Skills/using-junevy-webapi/SKILL.md` 加服务端一节；常见错误包括：
   - 现场只装了 .NET Desktop Runtime，启动时报找不到 `Microsoft.AspNetCore.App`；
   - 处理器里做耗时的设备动作却不理取消令牌（504 之后仍在跑）；
   - 期望"无论成败都 HTTP 200"却没开包络；
   - 开了去重但 MES 不带幂等键（去重不生效）；
   - 把 500 的详细信息当作给 MES 的提示（只在日志里）；
   - 监听 `http://*:port` 却忘了防火墙放行。
3. CHANGELOG 收尾；根 `readme.md` 的包表加一行。
4. 设计文档新增第 20.10 节"实施修订（P3.2）"：服务端改用 Kestrel、独立成仅 net8 的包（推翻 15.5 的"不用 Kestrel"，写明原因：用户 2026-10-17 决定、使用方是 net8、部署免 `netsh`、`HttpListener` 不推荐用于新开发）、`StartAsync` 返回 `CommResult`、状态自建且不做故障重启、认证复用 `WebApiAuthOptions` 的形状、去重、Problem Details、不做 SOAP 服务端；第 3.1 节的包表与第 19 节标明 P3 完成。
5. 知识库（不提交）：新笔记 `WebApi/WebApi 服务端.md`（管线顺序、响应格式、去重、生命周期、部署与运行时要求），挂进首页；`项目概述/技术栈与仓库结构.md` 加新包与测试项目；`架构设计/通讯库总体架构（协议族）.md` 的分期表 P3 行；`项目概述/待办与路线图.md` 里 P3.2 一行改为完成。
6. 提交：`docs(webapi-server): document the HTTP server`

### 14. Task 9：最终度量

1. 按第 4 节复测，写入第 19.3 节。
2. 两条构建命令各运行一次；`pwsh build/test-affected.ps1 -Base <起点提交> -NoBuild -Repeat 3`，三遍结果一致。
3. 写结论：新包的规模（自己写的部分 vs 交给 ASP.NET Core 的部分）；嵌入式 Kestrel 在 WPF 进程里的注意事项；给以后 SOAP 服务端的建议。
4. 提交：`chore: record the P3.2 final metrics`

---

## 16. 状态码与 `Outcome` 对照表

| 情况 | HTTP | `Outcome` |
|---|---|---|
| 处理器成功 | 200（`data` 为 null 且不用包络时 204；处理器显式值优先） | `Succeeded` |
| 处理器返回业务失败 | 包络：`BusinessFailureStatusCode`（默认 200）；不用包络：400；处理器显式值优先 | `BusinessFailure` |
| 正文非法、正文为空、JSON 无法反序列化 | 400 | `Rejected` |
| 认证失败 | 401 | `Rejected` |
| 路径不存在 | 404 | `Rejected` |
| 方法不允许 | 405 + `Allow` | `Rejected` |
| 读正文超时 | 408 | `Rejected` |
| 去重：同键请求仍在处理 | 409 | `Rejected` |
| 正文超过上限 | 413 | `Rejected` |
| `Content-Type` 不是 JSON | 415 | `Rejected` |
| 去重：同键不同正文 | 422 | `Rejected` |
| 拦截器 `Reject` | 拦截器给的状态码 | `Rejected` |
| 处理器或拦截器抛异常 | 500（通用消息） | `HandlerFailed` |
| 并发超限 | 503 + `Retry-After: 1` | `Rejected` |
| 处理器超时 | 504 | `TimedOut` |
| 去重重放 | 第一次的状态码 + `Idempotent-Replayed: true` | `Replayed` |
| 响应没能写出 | — | `ClientDisconnected` |

## 17. 审阅者的验收清单（执行者据此自查）

**代码**：第 2 节逐条；第 4 节度量达标；架构测试的登记与新守卫通过；公开 API 与第 6–12 节逐条一致；`Junevy.Communication.WebApi/` 与 `Tests/Junevy.Communication.WebApi.Tests/` 零改动；调研记录与偏差记录完整。

**功能**：审阅者复跑三条命令（测试命令加 `-Repeat 3`），并做变异检查——临时改坏下列规则，确认有测试失败：
- 并发超限时排队而不是 503；
- 处理器超时后不发 504、等处理器结束；
- 超时后处理器的迟到结果覆盖已发出的响应，或触发第二次 `RequestCompleted`；
- 404 / 405 用框架默认的空正文而不是本库格式；
- 405 不带 `Allow`；
- 500 的正文带上异常消息；
- 认证不用固定时间比较（用例无法区分时改为检查比较帮助类被调用）；
- 拦截器响应阶段不按逆序；
- 去重缓存 500 的响应；
- 去重在 504 之后立即释放键；
- 构建应用时读取了宿主的环境变量（Q29 的用例必须失败）；
- 停止后不重建应用、直接再启动旧的；
- 释放时释放了在途请求还在用的同步原语；
- 包络写出的结构与客户端的读取不一致（往返用例必须失败）。

---

## 18. 未纳入本计划的条目

- SOAP 服务端（用户 2026-10-17 决定不做）。
- 挂到宿主已有的 ASP.NET Core 应用上（`app.MapWebApiServer(...)` 之类的扩展）：内部按"中间件 + 路由端点"组织，以后加只是公开一个扩展方法。
- 每个路由单独的匿名 / 认证设置、内置健康检查端点（宿主可以注册 `MapGet("/health", …)`，认证由拦截器按路径放行）。
- OpenAPI / Swagger 文档生成。
- CORS（MES 是服务器到服务器调用，不经浏览器）。
- 流式请求与响应、文件上传（multipart）、超过 `MaxRequestBodySize` 的大正文。
- HTTP/2、HTTP/3 的专门配置（Kestrel 默认行为即可）。
- 离线缓存（P3-D5，仍只保留前提）。

---

## 19. 验收记录

### 19.1 各 Task 验收（审阅者填写）

| Task | 结论 | 说明 |
|---|---|---|
| 0 | 通过 | 调研 9 项记录完整；起点 `837f7d0`；度量基线可复现 |
| 1 | 打回 | R3（`Logging` 配置无效） |
| 2 | 通过（小修） | R14（D-1 写了 `instance`，实现没有写出） |
| 3 | 有条件通过 | R10（D-7、D-8 没有用例）；R12（`Allow` 的复制冗余）；A（先解码后认证，待用户决定）、B（被摘出的处理器不计入并发上限，待用户决定） |
| 4 | 有条件通过 | R7（504 之后迟到的结果仍触发响应拦截器）；R10（固定时间比较没有用例）；R13（两个相同的拒绝记录类型） |
| 5 | 有条件通过 | R5（重放带上第一次请求的拦截器响应头；缓存与响应阶段共用同一个字典） |
| 6 | 打回 | R1（释放与启动竞争）、R2（停止期间读取 `Urls` 抛异常）、R4（只差大小写或斜杠的模板被接受）、R6（映射失败不释放应用）；R10（Q29 用例拦不住读取宿主环境、释放规则没有用例）；R11（死代码） |
| 7 | 通过 | 含 D-14 的 `WebApiServerFactoryBuilder`，与客户端对称 |
| 8 | 有条件通过 | 文档随 R3、R4、R14 修正；知识库未提交 |
| 9 | 通过 | 度量沿用执行者的记录（未复测）；审阅者复跑三条命令：Debug 构建 0 错误 0 警告，`test-affected -Repeat 3` 三遍均为 273 通过 |

**审阅记录（2026-10-18）**

复跑：`dotnet build Junevy.Communication.slnx -c Debug` 0 错误 0 警告；`pwsh build/test-affected.ps1 -Base 837f7d0 -NoBuild -Repeat 3` 三遍均为 273 通过（架构 net8.0 15、net472 12；服务端 246），0 失败，0 跳过。各 Task 只含本 Task 的文件，生产代码没有混进 `docs:` / `chore:` 提交；`WebApi/` 与 `Tests/Junevy.Communication.WebApi.Tests/` 相对 `837f7d0` 零改动。

变异检查（第 17 节，脚本在仓库之外，改完即恢复）：

| 变异 | 结果 | 拦住它的用例 |
|---|---|---|
| 并发超限排队而不是 503 | 拦住 | `ConcurrencyLimit_…`（等到 HttpClient 超时才失败，约 1 分钟） |
| 超时后不发 504、等处理器结束 | 拦住 | `HandlerIgnoringTheToken_Returns504_…`、`TimeoutWhileRunning_…` |
| 超时后第二次 `RequestCompleted` | 拦住 | `HandlerPastTheTimeout_…`、`HandlerIgnoringTheToken_…` |
| 404 / 405 用框架的空正文 | 拦住 | `UnknownPath_IsAProblemDetailsWithStatus404` |
| 405 不复制 `Allow` | **存活** | 框架已经把 `Allow` 写进响应头，本库的复制是冗余的（R12）；用例本身没有问题 |
| 500 的正文带上异常消息 | 拦住 | `HandlerException_IsA500WithoutDetails_…` |
| 认证不用固定时间比较 | **存活** | 没有用例（R10） |
| 响应阶段不按逆序 | 拦住 | `RequestInterceptors_RunInRegistrationOrder_…` |
| 去重缓存 500 | 拦住 | `HandlerFailure_IsNotCached_…` |
| 去重在 504 时立即释放键 | 拦住 | `TimeoutWhileRunning_KeepsTheKeyInProgress_…` |
| 改用 `CreateBuilder`（读取宿主环境） | **存活** | Q29 用例只检查 `Urls`，而显式的 `UseUrls` 本来就覆盖 `ASPNETCORE_URLS` 与 `appsettings` 的 `Urls`（R10） |
| 停止后再启动旧的应用 | 拦住 | `StopThenStart_ServesRequestsAgain` |
| 释放时释放生命周期锁 | **存活** | 没有用例（R10；与 R1 一起补） |
| 包络的 `data` 写到客户端不读的路径 | 拦住 | `ResponseRoundTripTests`、`ClientRoundTripTests` |
| 去掉对端断开路径上的 `AbandonedWork.Observe`（D-8） | **存活** | 没有用例；被摘出的任务只会以"取消"结束（非取消异常都已转成 500），所以这里只剩那条 Warning，没有用例断言它（R10） |
| 畸形正文不翻译、按对端断开处理（D-7） | **存活** | 没有畸形分块正文 → 400 的用例（R10） |
| 删除认证阶段 | 拦住 | 只有 `ClientRoundTripTests.Unauthorized_…` 一条；`AuthenticationTests` 只测校验器本身 |

另用临时探针（已删除）复现了 R1、R2、R4、R5。

**必须修（阻塞合并）**

- **R1 释放与启动竞争，释放后服务器仍在运行**（`ServerLifecycle`）：`DisposeAsync` 先在锁内停止、出锁后才置 `disposed`；`StartCoreAsync` 不检查 `disposed`。复现：运行中调用 `DisposeAsync()` 不等待、紧接着 `StartAsync()`，两者完成后 `State = Running`，`GET` 返回 200，而 `StopAsync` 在释放后是空操作，端口一直占到进程退出。修法：`disposed` 在锁内设置，`StartCoreAsync` 在锁内检查（返回失败或抛 `ObjectDisposedException`，与公开契约一致），并补用例。
- **R2 停止期间读取 `Urls` 抛 `ObjectDisposedException`**（`ServerLifecycle.Urls` → `KestrelHost.Addresses`）：`StopCoreAsync` 先 `await running.DisposeAsync()`，再把 `host` 置空；这段时间里 `Addresses` 访问已释放的 `ServiceProvider`。探针第 0 轮即复现。WPF 绑定或状态轮询读 `Urls` 就会碰上。修法：启动成功后把实际地址拍成快照（`KestrelHost` 或生命周期保存一份数组），`Urls` 只读快照；顺带免去每次读取都查服务容器。
- **R3 `Logging` 配置完全无效**（P32-D15 未实现）：`WebApiServerConfig.Logging` 只被校验、复制进 `settings.Logging`，没有任何代码读取；`LogBodies`、`MaxLoggedBodyLength`、`RedactedHeaders`、`RedactedQueryParameters` 设什么都没有效果，公开 API 名不副实。另外每个请求的 Debug 日志写的是路径而不是计划要求的路由模板。修法：按 P32-D15 实现（请求日志带脱敏后的头与查询参数，`LogBodies` 时带截断的正文），并补用例。
- **R4 只差大小写或斜杠的模板被当成不同路由**（`RouteTable.Add` 用区分大小写的字符串比较）：`MapGet("/orders")` 与 `MapGet("/Orders")`（或 `"orders"`、`"/orders/"`）都被接受，启动成功，之后每个 `GET /orders` 都因 ASP.NET Core 的 `AmbiguousMatchException` 返回 500。P32-D5 要求重复模板抛 `ArgumentException`，README 也写了"同一方法与模板不能注册两次"。修法：比较前规范化（去掉首尾 `/`，字面段不区分大小写，与 ASP.NET Core 的匹配规则一致）。

**应修**

- **R5 去重重放带上第一次请求的拦截器响应头，且缓存与响应阶段共用同一个字典**：`ticket.Complete(result)` 在响应拦截器之前，缓存的就是随后被拦截器修改的那个 `ServerResponse.Headers`。探针：拦截器按请求编号加头，重放的响应同时带 `X-Stamp-1` 与 `X-Stamp-2`。第二个请求在第一个请求的响应阶段期间复制该字典时，是对 `Dictionary` 的并发读写。修法：`Complete` 时缓存头的副本（或缓存不可变的快照）。
- **R6 映射路由失败时不释放已建好的应用**（`KestrelHost.Create`）：`Build()` 之后 `RouteEndpointBinder.Map` 抛异常（例如模板 `"/orders/{"`），`WebApplication` 没有人释放，与"启动失败不留下任何应用"不符。修法：`Create` 内捕获后 `DisposeAsync` 再抛出。
- **R7 504 之后迟到的处理器结果仍会触发响应拦截器**（`RunStagesAsync` → `ApplyResponseStageAsync`）：响应早已以 504 发出，拦截器却收到 `Outcome = Succeeded` 的 `OnRespondingAsync`（令牌已取消）；做审计的拦截器会记下一条没有发出的响应。修法：超时或对端断开之后跳过响应阶段。
- **R8 处理器自己抛出的 `OperationCanceledException`**（例如处理器内部的 HttpClient 超时）返回 500，但跳过了响应拦截器，与其它 500 不一致（低）。
- **R9 去重键不含查询串**：同键、同路径、同正文而查询参数不同的请求会被重放第一次的结果（低；至少写进 README）。
- **R10 测试缺口**：固定时间比较（计划写明"用例无法区分时改为检查比较帮助类被调用"）；Q29 应改用 `CreateBuilder` 才会读取、显式设置又覆盖不了的配置，例如 `appsettings.json` 里的 `Kestrel:Endpoints`；释放规则（与 R1 一起补）；D-7（畸形分块正文 → 400）；D-8（对端断开后处理器仍在运行 → Warning）；Q27 的"去重键在 504 之后仍是处理中"用例没有 `UnobservedTaskWatch`；Q28 的停止用例没有用服务器侧在途数断言。

**清理（clean code）**

- **R11 死代码**：`ServerLifecycle.Host`（注释写"用于测试断言在途数"，但没有任何引用）、`RouteTable.Count`。
- **R12** `ServerEntryMiddleware.ResolveResult` 复制 `Allow` 是冗余的（框架已经写入响应头）；删掉，或保留并注释原因。
- **R13** `WebApiServerRequestingContext.Rejection` 与 `InterceptorRefusal` 是同形的两个记录，`InterceptorRun` 只是互相转换，合并成一个；`RequestPipeline.Deduplicate` 与认证处手写 `"409"`、`"422"`、`"503"`、`"401"`，其余路径用 `statusCode.ToString(...)`，统一走同一个帮助方法；`ResponseFormatter` 在 `WebApiServer` 与 `KestrelHost` 各建一个；日志类别常量在两个文件里重复；`RequestPipeline` 有多余空行；`WebApiServerCompletedEventArgs.cs` 的 `using System.Net;` 未使用；`ServerLifecycle` 里写全名的 `Junevy.Communication.WebApi.WebApiAuthScheme`、`System.Security.Cryptography.CryptographicException`。
- **R14 文档**：D-1 说写出 `instance`，`ProblemWriter` 没有写；`WebApiServerOutcome.TimedOut` 的注释"504，或读取正文超时的 408 之外的超时"表述不清（408 属于 `Rejected`）。

**待用户决定（设计）**

- **A 先认证、后反序列化**：现在的顺序是读正文 → 解码 JSON（400 / 415）→ 认证（401）。未认证的调用方能拿到 400 / 415，序列化器也在认证之前处理不可信的输入。建议改为读正文 → 认证 → 解码 → 拦截器（认证只需要原始字节，签名校验也不受影响）。这会改变第 8 节 Task 3 的阶段顺序。
- **B 被摘出去的处理器不计入 `MaxConcurrentRequests`**：504 之后限流许可就释放了，不理令牌的处理器继续在后台运行。设备卡住、MES 每 30 秒重发一次时，后台任务会一直累积，直到线程池耗尽（开启去重且 MES 带幂等键时不会，同键会得到 409）。建议至少记录被摘出的处理器数（内部计数与 Warning）；是否把它们计入并发上限由用户决定。

**说明（不要求修改）**：同步 `Dispose()` 是 sync-over-async，与 `MqttClient`、`OpcUaClient` 一致；WPF 里在 UI 线程调用 `Dispose()`、同时 `StateChanged` 订阅者用 `Dispatcher.Invoke` 会死锁，建议 README 写明在 UI 线程上使用 `DisposeAsync`。

**修正与复验（2026-10-18，用户要求审阅者直接修；A、B 按审阅者的建议执行）**

| 项 | 修正 | 用例 |
|---|---|---|
| R1 | 释放标记在生命周期锁内设置与检查；等在锁外的 `StartAsync` 抛 `ObjectDisposedException` | `WebApiServerConcurrencyTests.StartWaitingBehindDispose_…` |
| R2 | `KestrelHost.Addresses` 在启动成功时拍快照；`Urls` 只读快照；停止时先清引用再释放应用 | `Urls_CanBeReadWhileTheServerStops` |
| R3 | 新增 `Logging/RequestLog`：Debug 记方法、路径、脱敏的查询串与请求头；结束日志用路由模板；`LogBodies` 时记两个方向的正文并截断 | `RequestLogTests`（3 条） |
| R4 | `RouteTable` 忽略大小写与首尾 `/` 判重；删除未用的 `Count` | `TemplateEquivalentForRouting_IsADuplicate_…` |
| R5 | `DeduplicationCache.Complete` 缓存 `DedupReplay.Capture` 的快照 | `CachedResult_IsASnapshot_…` |
| R6 | `KestrelHost.Create` 映射失败时释放已建立的应用再抛出 | 无法从外部观察，按代码审阅 |
| R7 | 令牌已取消时跳过响应阶段（结果仍按超时 / 断开处理） | `LateResultAfterA504_IsNotGivenToTheResponseInterceptors` |
| R8 | 只有令牌确实取消时才把 `OperationCanceledException` 当取消；否则 500 并经过响应阶段 | `HandlerThrowingItsOwnCancellation_IsA500_…` |
| R9 | 去重键加入按名称排序的查询串（`DedupTarget`） | `QueryParameters_ArePartOfTheTarget_…`、`SameKey_WithDifferentQueryParameters_…` |
| R10 | 比较函数可注入（内部构造函数），用例确认每次比较都经过它；`ConstantTimeText` 先取 SHA-256 再固定时间比较；Q29 改用 `Kestrel:Endpoints`；补 D-7、D-8、释放规则、Q27（`UnobservedTaskWatch`）、Q28（停止后在途数回到 0） | `EveryScheme_DecidesThroughTheComparer_…`、`MalformedChunkedBody_IsA400_…`、`ClientDisconnecting_…`、`StopWaitingBehindDispose_…`、`StopPastItsDeadline_…` |
| R11 | 删除 `ServerLifecycle.Host`、`RouteTable.Count` | — |
| R12 | 405 不再复制 `Allow`（注释说明框架已写入） | 新变异"写出时移除 `Allow`"被 `EntryAndRoutingTests` 拦住 |
| R13 | 删除 `InterceptorRefusal`（直接用 `Rejection`）；结果构造抽成 `Pipeline/ServerResults`（拒绝、503、500、504、断开，管线与入口共用），状态码文本只在一处生成；`HostServices` 让格式化器、事件与日志记录器只建一份；删除多余的 `using` 与全名 | — |
| R14 | `WebApiServerOutcome.TimedOut` 注释改正；D-1 的 `instance` 见下 | — |
| A | 认证移到解码之前 | `UnauthenticatedRequest_Gets401_BeforeItsBodyIsDecoded`（两条） |
| B | `AbandonedWork` 改为实例，统计仍在运行的被摘出处理器（`KestrelHost.AbandonedCount`），Warning 写出数量；仍不计入并发上限 | `ClientDisconnecting_…`、`LateResultAfterA504_…` |

关于 D-1：`ProblemWriter` 不写 `instance`（RFC 9457 中可选；格式化器不知道请求路径），D-1 原文"写出 `instance`"为误记，以本条为准。

复验：
- 第 17 节存活的变异与每项修正各一个变异，共 16 个，全部被拦住（含 M7、M11、M13、M15、M16）。
- `dotnet build Junevy.Communication.slnx -c Debug` 与 `-c Release`：0 错误、31 警告（全部既有，来自 Modbus 与 WPF 示例）。
- `pwsh build/test-affected.ps1 -Base 837f7d0 -NoBuild -Repeat 3`：三遍均为 298 通过（架构 net8.0 15、net472 12；服务端 271），0 失败，0 跳过；未选中 `WebApi.Tests`。
- 度量：生产重复窗口 34（9），与基线一致，新包为 0；测试重复窗口 190（51），不变；超过 60 行的方法 0；新包最大文件 `ServerLifecycle.cs` 285 行、`RequestPipeline.cs` 253 行；有效行 `WebApi.Server` 2488、`WebApi.Server.Tests` 3528，客户端 `WebApi` 仍为 2735。

**最终结论：通过**（Task 0–9）。

### 19.2 调研记录与执行偏差（执行者填写：现象 / 处理 / 是否需要审阅者决定）

**调研环境**：仓库之外的临时工程（未提交）：`Survey.Host`（net8.0，`FrameworkReference Microsoft.AspNetCore.App`，`Microsoft.Extensions.DependencyInjection.Abstractions` / `Logging.Abstractions` 8.0.0）、`Survey.Console`（net8.0 控制台）、`Survey.Wpf`（net8.0-windows WPF 空工程，引用 `Survey.Host`）。以普通用户 COWAIN（非管理员）运行；SDK 9.0.316 与 10.0.401，共享运行时 ASP.NET Core 8.0.31。控制台与 WPF 两份运行结果一致。

**调研结果（第 5 节第 2 条的 9 项）**

| 项 | 用到的 API | 现象 | 结论 / 处理 |
|---|---|---|---|
| 2.1 嵌入启动 | `WebApplication.CreateEmptyBuilder(new WebApplicationOptions())`、`WebHost.UseKestrelCore()`、`Services.AddRoutingCore()`、`UseRouting()`、`MapGet` | 控制台与 WPF 都能启动，`GET /ping` 返回 200 `pong`，`StopAsync` 后 `DisposeAsync` 正常 | 通过 |
| 2.1 配置隔离 | 同上；设 `ASPNETCORE_URLS=http://127.0.0.1:5999`、`ASPNETCORE_ENVIRONMENT=Development`，工作目录放 `appsettings.json`（`Urls` 为 5998） | 不设 `UseUrls` 时绑定 `http://localhost:5000`（Kestrel 的回退值），环境名 `Production`：三处配置都未生效。显式 `UseUrls("http://127.0.0.1:0")` 时按显式值绑定 | 通过。实现必须总是显式设置地址（`UseUrls` 或 `Listen`），否则回落到 5000 |
| 2.2 类库带 FrameworkReference | `Survey.Host` 的 `FrameworkReference Microsoft.AspNetCore.App` 被 WPF 工程引用 | 构建 0 警告 0 错误，无 NU1605；`Survey.Wpf.runtimeconfig.json` 的 frameworks 含 `Microsoft.AspNetCore.App 8.0.0`；`Microsoft.Extensions.*.Abstractions 8.0.0` 无冲突 | 通过 |
| 2.3 端口 0 | `Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()` | `StartAsync` 之后返回实际端口（如 `http://127.0.0.1:54094`） | 通过 |
| 2.4 端口被占用 | `TcpListener` 占用端口后 `StartAsync` | `System.IO.IOException`（"Failed to bind ... address already in use"），内层 `Microsoft.AspNetCore.Connections.AddressInUseException` | 映射 `ResourceExhausted`（计划） |
| 2.4 无权绑定 | Windows 排除端口 5357（`netsh int ipv4 show excludedportrange protocol=tcp` 唯一的排除段） | `System.Net.Sockets.SocketException`，`SocketErrorCode=AccessDenied`，`NativeErrorCode=10013`，**未被包装** | 映射 `AuthenticationFailed`（计划）。Linux 上 1024 以下端口本机无法验证，由实现按 `SocketErrorCode.AccessDenied` 判断，Linux 的 `AccessDenied` 同样适用 |
| 2.5 优雅停止（有期限） | `app.StopAsync(cts.Token)`，`cts` 为 1 s；在途 `GET /slow`（处理器 `Task.Delay(3 s, RequestAborted)`） | `StopAsync` 不抛异常，1010 ms 返回；在途客户端得到 `IOException`（远程主机关闭连接）；处理器的 `RequestAborted` 触发 | 与计划一致：到期后中止连接并取消处理器令牌 |
| 2.5 优雅停止（无期限） | `app.StopAsync()` | 等在途请求完成后返回（3005 ms），客户端收到 200 `done`，`RequestAborted` 未触发 | 与计划一致 |
| 2.5 停止后立即复用端口 | 停止后用同一端口新建应用 | 立即绑定成功 | 通过 |
| 2.6 正文超限 | `Limits.MaxRequestBodySize = 1024`；处理器 `CopyToAsync` 读取 4096 字节（`Content-Length` 与 chunked 两种） | 读取时抛 `Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException`，`StatusCode=413`；两种请求都一样 | 本库捕获该类型并写 413。`Microsoft.AspNetCore.Http.BadHttpRequestException` 与它同名，两个命名空间同时引用会 CS0104，代码中要写全名或只引用 Kestrel 的 |
| 2.6 404 / 405 | `UseRouting()` 之后加中间件：`await next()` 之后，若未 `HasStarted`、没有本库的处理标记，改写正文 | 404：`GetEndpoint()` 为 null，正文空；405：端点 DisplayName 为 `405 HTTP Method Not Supported`，框架写入 `Allow: GET, PUT`；改写后状态码保持，正文为 `{"code":"404"}` / `{"code":"405"}` | 通过。`Allow` 在 HttpClient 中位于 `Content.Headers`（HttpClient 把 Allow 归为内容头），测试要读 `Content.Headers` |
| 2.7 限流 | `AddRateLimiter`，`GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(…GetConcurrencyLimiter("all", … new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0, QueueProcessingOrder = … }))`，`RejectionStatusCode = 503`，`OnRejected` 设 `Retry-After` | 并发的第二个请求 1 ms 内得到 503，`OnRejected` 被调用，第一个请求 200，释放后再请求 200 | 通过。`ConcurrencyLimiterOptions` 没有三参数构造函数，只能用属性初始化 |
| 2.8 日志转发 | `Logging.ClearProviders()`、`SetMinimumLevel(Warning)`、`AddProvider(转发到调用方 ILoggerFactory 的 provider)` | 调用方只收到 Warning（Information 与 "Now listening" 都被过滤）；类别名是宿主程序集名 | 通过 |
| 2.9 HTTPS | `CertificateRequest` 自签名（SAN 为 127.0.0.1，EKU serverAuth）；`Listen(IPAddress.Loopback, 0, l => l.UseHttps(cert))`；客户端回调比对指纹 | **`X509KeyStorageFlags.EphemeralKeySet` 导入的证书：TLS 握手失败（服务端收到 EOF）**；`Exportable` 与 `UserKeySet` 都成功 | 测试辅助使用 `Exportable` 导入（不在用户密钥容器留下密钥）。客户端回调收到 `RemoteCertificateChainErrors`，符合自签名的预期 |

**执行偏差**

- **D-1（实现细节，不改公开 API）**：P32-D6 写的是"用 ASP.NET Core 的 `ProblemDetails` 类型"，但 `ProblemDetails` 位于共享框架的 `Microsoft.AspNetCore.Mvc.Core`，而 P32-D16 与 Q30 要求 `Hosting` 之外不引用 `Microsoft.AspNetCore.*`。处理：`ResponseFormatter` 直接写 RFC 9457 的 JSON（`type`、`title`、`status`、`detail`、`instance`，业务码放扩展字段 `code`，`application/problem+json`），线上格式不变。是否需要审阅者决定：否。
- **D-2（测试辅助）**：HTTPS 测试证书按 2.9 的结论使用 `Exportable` 导入，而不是直接用 `CreateSelfSigned` 的返回值或 `EphemeralKeySet`。是否需要审阅者决定：否。
- **D-3（Task 1，是否需要审阅者决定：是；审阅者决定：接受，2026-10-17）**：`SdkAdapterArchitectureTests.Adapters` 增加"允许引用的库"列，守卫 1 从"只引用 Core"改为"只引用各自的允许列表"（服务端允许 WebApi，其余适配包仍只允许 Core）。没有新写守卫，只改了登记表的语义，因为服务端同时依赖 WebApi 的公共约定（P32-D2）。
- **D-4（Task 1，否）**：计划写"启用包络时"才检查序列化器的内容类型必须是 JSON。实现为**不论是否启用包络都检查**：服务端的请求正文与成功数据都经过它。比计划严格，不影响公开 API。
- **D-5（Task 1，否）**：`WebApiServerComponents` 引用的 `IWebApiServerInterceptor`、`IWebApiRequestAuthenticator`、`WebApiServerContext`、`WebApiServerOutcome` 等类型在 Task 1 即写入（组件需要它们才能编译）。内容与计划第 6 节、第 9 节的公开 API 一致，只是提前。
- **D-6（Task 1、Task 2，否）**：计划要求先写测试并确认失败。实际上公开类型要先存在，测试才能编译，所以测试与实现同时写入。红灯证据：Task 1 首次编译时，XML 注释里的 `cref` 指向未定义的类型，报 CS1574，已改为纯文字；Task 1 的去重窗口用例首次运行失败（参数名为 `config.Window`，应为 `config.Deduplication.Window`），已修正。Task 2 的测试首次运行全部通过，没有单独做红灯验证。
- **D-7（Task 3，否）**：Kestrel 在 .NET 8 中抛出的 `BadHttpRequestException` 本身是 `IOException`（派生自 `Microsoft.AspNetCore.Http.BadHttpRequestException`）。若直接交给管线，会被当成"对端断开"。Hosting 层先翻译：413 → `RequestBodyTooLargeException`，其余畸形正文 → `RequestBodyMalformedException`（管线据此返回 400）。
- **D-8（Task 3，否）**：计划只写了"超时之后仍在运行的处理器"。实际上客户端断开时，仍在运行的处理器之后的异常同样需要观察；Task 6 的测试发现这一点，已修正（`AbandonedWork.Observe` 在两条路径上都调用）。
- **D-9（Task 4，否）**：响应拦截器只通知请求阶段已经完成（或拒绝）的拦截器，并按逆序执行，与 ASP.NET Core 中间件的前后处理一致。计划没有明确这一点。
- **D-10（Task 4，否）**：响应阶段只对读取了正文的请求执行。正文阶段的 408、413 等拒绝没有构成完整的请求上下文，因此不经过响应拦截器。计划没有明确这一点。
- **D-11（Task 5，否）**：缓存全是处理中的条目时返回 503（计划只写"满了先淘汰最旧的"）。处理中的条目永不淘汰，所以需要一个出路；503 与 `Retry-After` 与其它并发限制一致。
- **D-12（Task 6，否）**：同步的 `Dispose()` 使用 `DisposeAsync().AsTask().GetAwaiter().GetResult()`。依据是内部所有等待都使用 `ConfigureAwait(false)`，不会因同步上下文死锁；测试覆盖了释放与重复释放。
- **D-13（Task 6，否）**：HTTPS 还需要 `WebHost.UseKestrelHttpsConfiguration()`，否则 Kestrel 报 "Call UseKestrelHttpsConfiguration()"。调研 2.9 没有覆盖这一点，测试中发现，已在有证书时调用。
- **D-14（Task 7，是否需要审阅者决定：是；审阅者决定：需要，已实现，2026-10-17）**：客户端有 `WebApiClientFactoryBuilder`（给没有 DI 容器的宿主用）。计划 P32-D14 只列了 `WebApiServerFactory` 与 `AddWebApiServers()`，因此服务端原先没有对应的构建器。已增加 `WebApiServerFactoryBuilder`（`Create()`、`WithLoggerFactory`、`Build()`，与客户端对称），测试见 `WebApiServerFactoryBuilderTests.cs`。这是计划之外的增补，计划第 6–13 节的公开 API 不变。
- **D-15（Task 8，否）**：README 由模板生成，代码块从 `ReadmeSamples.cs` 逐字复制；`ReadmeSampleTests` 检查一致并运行示例。负控（把 README 中一个字符改为 202）使检查失败，恢复后通过。
- **D-16（Task 9，否）**：`RequestPipeline.cs` 第一次为 315 行（超过 300 行的上限），把重放结果的构造拆到 `Dedup/DedupReplay.cs`，被摘出去的处理器的观察拆到 `Pipeline/AbandonedWork.cs`，现为 294 行。不改公开 API。
- **D-17（Task 8，否）**：设计文档新增第 20.10 节（14 项修订）；第 15.5 节加了指向 20.10 的说明。知识库中的 `技术栈与仓库结构` 笔记的项目数字在本期之前已经过时（它停留在 P5 之前的计数），本期只补充了服务端的包与测试项目两行，未修正原有的计数。

**审阅者决定**：D-3 已接受；D-14 需要，已实现（均为 2026-10-17）。

### 19.3 基线与度量（执行者填写）

- 起点提交：`837f7d0`（master）；分支 `feat/webapi-p32` 从它创建，HEAD 为 `837f7d0`。工作区另有用户未提交的知识库改动（`Junevy.Communication.Wiki/` 下两份修改与一份未跟踪笔记），本计划不触碰，提交时只 `git add` 本计划文件。
- 构建：`dotnet build Junevy.Communication.slnx -c Debug` 0 错误、31 警告；`-c Release` 0 错误、31 警告。警告全部既有（Modbus 的 CS0168 / CS1574，WPF 示例的 CS8618），与本期无关。
- 架构测试基线（`Tests/Junevy.Communication.Architecture.Tests`，两个目标）：net8.0 15 通过 / 0 失败 / 0 跳过；net472 12 通过 / 0 失败 / 0 跳过。
- `pwsh build/test-affected.ps1 -Base 837f7d0 -NoBuild`：没有受影响的测试项目（工作区改动只在 docs 与知识库，不进入任何程序集），退出码 0。
- 第 4 节度量，逐项复现 P6.2 的基线：

| 指标 | 结果 | 与 P6.2 基线 |
|---|---|---|
| 生产重复窗口（6 行） | 34（9 个集群） | 一致。注意：生产范围必须是 13 个目录（Core、Channels、Tcp、Udp、Serial、Plc、Melsec、Siemens、Omron、Secs、WebApi、Mqtt、OpcUa，排除 Modbus）；`build/metrics/README.md` 列出的 7 个目录只得到 24（7 个集群）。Task 9 复测沿用 13 个目录 + 新包 |
| 测试重复窗口 | 184（47 个集群） | 一致 |
| 超过 60 行的方法（生产） | 0 | 一致 |
| 超过 300 行的文件（生产） | 9：NamedRegistry 461、ConnectionSupervisor 691、StreamConnectionDriver 314、DatagramChannel 352、FrameRouter 373、PendingRequestTable 524、StreamChannel 621、TcpClientChannel 372、TcpServer 449 | 一致 |
| 生产 → 生产 IVT | 2（Channels → Tcp、Channels → Udp） | 一致 |
| 有效行（WebApi） | 2735 | 一致 |

- 本期结束时需要复测的是 13 个目录加上 `Junevy.Communication.WebApi.Server` 的生产文件；测试范围加上 `Tests/Junevy.Communication.WebApi.Server.Tests`。

### 19.3.2 最终度量（Task 9）

| 指标 | 基线（P6.2） | 最终 | 结论 |
|---|---|---|---|
| 生产重复窗口（6 行，14 个目录含新包） | 34（9 个集群） | 34（9） | 不增加；新包内部为 0，与其他程序集之间为 0 |
| 测试重复窗口 | 184（47） | 189（50） | 新增 5 个窗口、3 个集群，在新测试项目内部，不超过 10 |
| 超过 60 行的方法（生产） | 0 | 0 | 达标 |
| 超过 300 行的文件（生产） | 9 | 9 | 不增加；新包全部 ≤ 300 行（最大 `RequestPipeline.cs` 294 行） |
| 生产 → 生产 IVT | 2 | 2 | 不增加；新包只对自己的测试项目开放 |
| 有效行 | WebApi 2735 | WebApi 2735（不变）；WebApi.Server 2322；WebApi.Server.Tests 3071 | 客户端零改动 |

- 构建：`dotnet build Junevy.Communication.slnx -c Debug` 0 错误、31 警告；`-c Release` 0 错误、31 警告。警告全部是既有的（Modbus、WPF 示例），新包与测试项目没有警告。
- `pwsh build/test-affected.ps1 -Base 837f7d0 -NoBuild -Repeat 3`：选中 `Junevy.Communication.Architecture.Tests` 与 `Junevy.Communication.WebApi.Server.Tests`，**没有选中 `WebApi.Tests`**（客户端未改）。三遍结果一致：每遍 268 通过（架构测试 net8.0 15、net472 12；服务端 241），0 失败，0 跳过。
- `WebApi/` 与 `Tests/Junevy.Communication.WebApi.Tests/` 相对 `837f7d0` 的差异为空。
- `Junevy.Communication.WebApi.Server` 中 `Microsoft.AspNetCore` 与 `HttpContext` 只出现在 `Hosting` 命名空间的代码里；其余出现的只是注释。
- **D-14 增补后的复测（2026-10-17，审阅者决定需要）**：生产重复窗口仍为 34（9），新文件为 0；测试重复窗口（`Tests/` 下全部 `*.cs`）由 189（50）变为 190（51），相对 P6.2 基线 184（47）增加 6，仍在 10 之内；超过 60 行的方法仍为 0；有效行：`WebApi.Server` 2339（原 2322）、`WebApi.Server.Tests` 3129（原 3071）；客户端 `WebApi` 仍为 2735。`dotnet build Junevy.Communication.slnx -c Debug`：0 错误。`pwsh build/test-affected.ps1 -Base 837f7d0 -NoBuild -Repeat 3`：三遍均为 273 通过（架构 net8.0 15、net472 12；服务端 246），0 失败，0 跳过。

**规模（Task 9 第 3 条）**：自己写的生产代码 2322 有效行，其中 `Hosting` 374 行（嵌入式 Kestrel 的宿主、入口与出口中间件、请求与响应的翻译、启动失败的分类、证书加载）。交给 ASP.NET Core 的部分包括：HTTP 解析与连接、Kestrel 的读写与背压、路由匹配（模板与字面段优先）、并发限流器、Kestrel 的正文上限与慢速攻击防护、框架日志的级别过滤。

**嵌入式 Kestrel 在 WPF 进程中的注意事项**（调研 2.2）：
- 类库引用 `FrameworkReference Microsoft.AspNetCore.App` 后，WPF 工程的 `runtimeconfig.json` 会自动带上 `Microsoft.AspNetCore.App`，构建没有 NU1605 或版本冲突。
- 目标机需要 ASP.NET Core 8 运行时；只装 Desktop Runtime 不够。
- 必须显式设置监听地址，否则 Kestrel 回落到 `localhost:5000`。
- Windows 上 Kestrel 不接受仅临时存在的密钥作为 HTTPS 证书（握手失败），证书要以 `Exportable` 导入。

**给以后 SOAP 服务端的建议**：复用 `Pipeline` 的阶段顺序与 `ResponseFormatter` 的出口（SOAP Fault 作为另一种格式写在同一个出口）；`RawRequest` 已与 HTTP 实现无关，SOAP 只需要新的正文解析与响应格式化，拦截器与事件可以原样使用。

**未验证与未决**：未与真实 MES 联调（README 的"未验证"一节已写明）；技术栈笔记的项目计数在本期之前已过时。
