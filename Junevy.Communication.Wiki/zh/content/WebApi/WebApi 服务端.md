# WebApi 服务端（P3.2）

<cite>
**本文引用的文件**
- [docs/superpowers/plans/2026-10-17-p32-webapi-server.md](file://docs/superpowers/plans/2026-10-17-p32-webapi-server.md)
- [docs/superpowers/specs/2026-10-10-communication-core-design.md](file://docs/superpowers/specs/2026-10-10-communication-core-design.md)（第 20.10 节）
- [Junevy.Communication.WebApi.Server/Server/WebApiServer.cs](file://Junevy.Communication.WebApi.Server/Server/WebApiServer.cs)
- [Junevy.Communication.WebApi.Server/Pipeline/RequestPipeline.cs](file://Junevy.Communication.WebApi.Server/Pipeline/RequestPipeline.cs)
- [Junevy.Communication.WebApi.Server/Hosting/ServerEntryMiddleware.cs](file://Junevy.Communication.WebApi.Server/Hosting/ServerEntryMiddleware.cs)
- [Junevy.Communication.WebApi.Server/Dedup/DeduplicationCache.cs](file://Junevy.Communication.WebApi.Server/Dedup/DeduplicationCache.cs)
- [Junevy.Communication.WebApi.Server/Lifecycle/ServerLifecycle.cs](file://Junevy.Communication.WebApi.Server/Lifecycle/ServerLifecycle.cs)
- [Junevy.Communication.WebApi.Server/README.md](file://Junevy.Communication.WebApi.Server/README.md)
</cite>

包 `Junevy.Communication.WebApi.Server`（1.0.0-preview.1，**仅 net8.0**）：MES 通过 HTTP 与 JSON 调用宿主注册的路由。服务端嵌入 Kestrel，部署不需要 `netsh`。设计修订见设计文档第 20.10 节；开工前的问题清单见 [[WebApi/P3.2 HTTP 服务端（待办）]]。

## 结构与依赖

- 依赖：Core、`Junevy.Communication.WebApi`（配置、包络选项、序列化器接口），以及 `Microsoft.AspNetCore.App`（共享框架）。
- 工厂：`WebApiServerFactory` 按名称管理服务器。有 DI 时用 `AddWebApiServers()`；没有 DI 容器的宿主用 `WebApiServerFactoryBuilder`（`Create()`、`WithLoggerFactory`、`Build()`，与客户端的 `WebApiClientFactoryBuilder` 对称；2026-10-17 作为计划 D-14 的增补加入）。
- 命名空间：根命名空间放公开 API 与不接触 ASP.NET 的逻辑；`Pipeline` 是管线、路由与拦截器运行；`Formatting`、`Body`、`Auth`、`Dedup`、`Config`（校验与快照）、`Lifecycle`（状态机与路由表）；`DependencyInjection`；`Hosting` 是唯一引用 `Microsoft.AspNetCore.*` 的命名空间，由 `SdkAdapterArchitectureTests` 守住。

## 一次请求的管线顺序

1. 入口中间件：分配请求编号，触发 `RequestReceived`，计入在途数。
2. 并发限流（`RateLimiter` 的并发限流器，队列长度 0）：超限立即 503 与 `Retry-After: 1`。
3. 路由（ASP.NET Core 模板，字面段优先）：无匹配 404，方法不符 405 并带 `Allow`。登记时只差大小写或首尾 `/` 的模板算重复（抛 `ArgumentException`），否则每次请求都会因歧义得到 500（2026-10-18 审阅 R4）。
4. 管线，全部在同一个请求超时之下：读取正文（超时 408，超限 413，畸形 400，对端断开不写响应）→ 认证（401，带 `WWW-Authenticate`；认证器拿到原始正文）→ 解码（空正文 400，非 JSON 415，`byte[]` 原样，`string` 按 charset，其余交给序列化器，失败 400）→ 请求拦截器（`Reject` 截断后续）→ 去重（重放、409、422 或 503）→ 处理器（超时 504，异常 500）→ 响应拦截器（逆序，只通知已完成请求阶段的拦截器；504 或断开之后迟到的结果不经过它们）。认证在解码之前是 2026-10-18 审阅（A）改的：未认证的调用方只得到 401，序列化器不处理未认证的输入。
   - 处理器不理令牌而在 504 或断开之后继续运行时，它已经让出并发许可；`KestrelHost.AbandonedCount` 统计仍在运行的数量，每条 Warning 写出这个数（审阅 B）。处理器自己抛出的 `OperationCanceledException`（令牌没有取消）按 500 处理。
5. 出口：统一写出响应，然后触发 `RequestCompleted`。每个请求恰好一次（Q26）。
6. 日志（P32-D15）：Debug 记进入管线的请求（方法、路径、按 `RedactedQueryParameters` 脱敏的查询串、按 `RedactedHeaders` 脱敏的请求头）与结束（方法、路由模板、状态码、结果、耗时）；`LogBodies` 时记请求与响应正文并按 `MaxLoggedBodyLength` 截断。实现在 `Logging/RequestLog`；2026-10-18 之前这组配置没有任何效果（审阅 R3）。

## 响应格式

- 未启用包络：成功写数据本身，数据为 null 时 204；失败与拒绝写 RFC 9457 Problem Details（`application/problem+json`，业务码在扩展字段 `code`，默认 400）。
- 启用包络（`ResponseEnvelopeOptions`，路径为点分路径，三条路径互不为前缀）：成功 `{ code: SuccessCodes[0], msg: "", data }`；业务失败 `{ code, msg }`，HTTP 为 `BusinessFailureStatusCode`（默认 200）；框架拒绝使用同一结构，`code` 为状态码文本。
- 显式状态码永远优先。数据由序列化器输出的 JSON 原样嵌入（`Utf8JsonWriter.WriteRawValue`），不再解析。

## 去重（P32-D11）

- 默认关闭。键是幂等头的值、方法与请求目标（路径加按名称排序的查询串）的组合；正文用 SHA-256 比较。
- 窗口内（默认 10 分钟，从完成时刻起算）已完成的同键请求：重放第一次的状态码、内容类型与正文，加 `Idempotent-Replayed: true`，处理器不执行。
- 同键仍在处理：409。同键正文不同：422。缓存满且全部是处理中的条目：503。
- 只缓存处理器给出的响应（成功与业务失败），存的是响应头的快照：响应拦截器之后加的头属于那一次请求，不会被重放（审阅 R5）。框架拒绝、正文错误与异常不缓存，允许重试。
- 处理器超时（504）之后仍在运行：该键保持"处理中"，直到处理器真正结束，才缓存它的最终结果。
- 上限满时淘汰最旧的已完成条目；处理中的条目永不淘汰。时间源可注入（`WebApiServerComponents.TimeProvider`）。

## 生命周期（P32-D4、D12）

- 状态：`Stopped → Starting → Running → Stopping → Stopped`。状态只在同一把异步锁之下变化，`StateChanged` 按变化顺序触发，订阅者的异常只记录。
- `StartAsync` 返回 `CommResult`，不因绑定、证书或路由失败而抛异常：`ResourceExhausted`（端口占用：`IOException` 内层为 `AddressInUseException`）；`AuthenticationFailed`（Windows 排除或保留端口：`SocketException`，`SocketErrorCode == AccessDenied`，10013）；`InvalidRequest`（重复启动、没有路由、模板非法、证书加载失败）；`Cancelled`；其余为 `Unspecified`。失败后不留下应用。
- 每次启动都新建 Kestrel 应用，因为 ASP.NET Core 应用停止后不能再启动。
- `StopAsync` 等在途请求最长 `StopTimeout`（默认 5000 ms），之后中止连接并取消处理器的令牌。调研 2.5：期限到时返回，在途客户端得到 `IOException`，处理器的 `RequestAborted` 被触发。
- 释放：`Dispose`/`DisposeAsync` 可重复调用；释放后 `StartAsync` 与 `Map*` 抛 `ObjectDisposedException`，`StopAsync` 无操作。释放标记在生命周期锁内设置与检查：等在锁外的 `StartAsync` 抛 `ObjectDisposedException`，不会在释放之后再建起应用（审阅 R1，修正前释放后的服务器仍在监听且停不下来）。释放不释放仍可能被在途请求使用的锁（P6.1 R3、P6.2 R10）。
- `Urls` 读启动成功时拍下的地址快照，停止期间也能读（审阅 R2：修正前读到正在释放的应用会抛 `ObjectDisposedException`）。UI 线程上用 `DisposeAsync`：同步 `Dispose` 会阻塞到停止完成，`StateChanged` 订阅者若 `Dispatcher.Invoke` 回 UI 线程就会死锁。

## 嵌入式 Kestrel 的调研结论（计划第 19.2 节）

- `WebApplication.CreateEmptyBuilder` 加 `UseKestrelCore` 与 `AddRoutingCore`，在控制台与 WPF 进程中都能启动、处理请求、停止。
- 不设地址时 Kestrel 回落到 `http://localhost:5000`，所以实现必须总是显式设置地址。
- `ASPNETCORE_URLS`、工作目录下的 `appsettings.json` 与 `ASPNETCORE_ENVIRONMENT` 都不影响服务器（调研 2.1）。
- 正文超限抛出 `Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException`（StatusCode 413）。它派生自 `Microsoft.AspNetCore.Http.BadHttpRequestException`，后者是 `IOException`，所以只能先翻译再交给管线，否则会被误判为对端断开。
- HTTPS：`EphemeralKeySet` 导入的证书握手失败；`Exportable` 可用，测试与 .pfx 文件加载都使用它。
- 框架的 405 端点会写入 `Allow`；HttpClient 把 `Allow` 放在 `Content.Headers` 中，测试读取时要看那里。

## 部署与运行时

- 目标机需要 **ASP.NET Core 8 运行时**（"ASP.NET Core Runtime 8"）。只装 .NET Desktop Runtime 不够，启动时会找不到 `Microsoft.AspNetCore.App`。自包含发布会带上运行时。
- 不需要 `netsh`、URL ACL 或证书绑定。防火墙需要放行端口。
- 宿主本身是 ASP.NET Core 应用时，直接使用 Minimal API，不必使用本包。

## 测试

- 测试项目 `Tests/Junevy.Communication.WebApi.Server.Tests`（net8.0）：真实 Kestrel 监听 `127.0.0.1:0`，套接字用例放在 `SocketTiming` 集合中串行运行。
- `ReadmeSampleTests` 编译并运行 `ReadmeSamples.cs`，并检查 README 中的代码块与它逐字一致。
- 与 3.1 客户端的往返：`ClientRoundTripTests`、`ResponseRoundTripTests`。

## 未验证与不做

- 未与真实 MES 联调。
- 不做：SOAP 服务端、流式正文、multipart 上传、CORS、OpenAPI 文档、HTTP/2 的专门配置，以及挂到宿主已有应用上的扩展方法（列入计划第 18 节）。
