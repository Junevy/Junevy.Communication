# WebApi 客户端与钩子（REST / SOAP）

<cite>
**本文引用的文件**
- [Junevy.Communication.WebApi/README.md](file://Junevy.Communication.WebApi/README.md)
- [Junevy.Communication.WebApi/Rest/WebApiClient.cs](file://Junevy.Communication.WebApi/Rest/WebApiClient.cs)
- [Junevy.Communication.WebApi/Soap/SoapClient.cs](file://Junevy.Communication.WebApi/Soap/SoapClient.cs)
- [Junevy.Communication.WebApi/Http/EndpointCore.cs](file://Junevy.Communication.WebApi/Http/EndpointCore.cs)
- [Junevy.Communication.WebApi/Http/AttemptLoop.cs](file://Junevy.Communication.WebApi/Http/AttemptLoop.cs)
- [Junevy.Communication.WebApi/Http/AttemptRunner.cs](file://Junevy.Communication.WebApi/Http/AttemptRunner.cs)
- [Junevy.Communication.WebApi/Http/InterceptorChain.cs](file://Junevy.Communication.WebApi/Http/InterceptorChain.cs)
- [Junevy.Communication.WebApi/Rest/RestRequestBuilder.cs](file://Junevy.Communication.WebApi/Rest/RestRequestBuilder.cs)
- [Junevy.Communication.WebApi/Rest/RestResponseDecoder.cs](file://Junevy.Communication.WebApi/Rest/RestResponseDecoder.cs)
- [Junevy.Communication.WebApi/Soap/SoapRequestBuilder.cs](file://Junevy.Communication.WebApi/Soap/SoapRequestBuilder.cs)
- [Junevy.Communication.WebApi/Soap/SoapMessage.cs](file://Junevy.Communication.WebApi/Soap/SoapMessage.cs)
- [Junevy.Communication.WebApi/Soap/XmlBodyCodec.cs](file://Junevy.Communication.WebApi/Soap/XmlBodyCodec.cs)
- [Junevy.Communication.WebApi/Factory/WebApiClientFactory.cs](file://Junevy.Communication.WebApi/Factory/WebApiClientFactory.cs)
- [Skills/using-junevy-webapi/SKILL.md](file://Skills/using-junevy-webapi/SKILL.md)
- [docs/superpowers/plans/2026-10-11-p31-webapi-client.md](file://docs/superpowers/plans/2026-10-11-p31-webapi-client.md)
- [docs/superpowers/specs/2026-10-10-communication-core-design.md](file://docs/superpowers/specs/2026-10-10-communication-core-design.md)
</cite>

## 定位

`Junevy.Communication.WebApi`（net472 + net8.0，版本 `1.0.0-preview.1`，只依赖 Core）是**主动上报 MES** 的 REST 与 SOAP 客户端。HTTP 服务端（MES 下发给设备）是 3.2，不在本包。设计修订对照见设计文档第 20.4 节。

## 结构：单程序集，按命名空间分层

| 命名空间 | 内容 | 允许依赖 |
|---|---|---|
| `…WebApi`（根） | 公共契约：`WebApiResult<T>`、`WebApiAvailability`、`IWebApiEndpoint`、配置与嵌套选项、`WebApiComponents`、`IWebApiSerializer`、拦截器接口与上下文、事件参数 | 无 |
| `….Serialization` | `SystemTextJsonWebApiSerializer`；internal 的业务包络读写器 | 根 |
| `….Auth` | `WebApiAuthentication`；internal 的认证拦截器与"从配置创建" | 根 |
| `….Http` | 全部 internal：`EndpointCore`、请求快照、尝试循环、重试规则、可用性、健康检查、处理器工厂、日志与脱敏 | 根、Auth |
| `….Rest` / `….Soap` | 外观（各不超过 200 行）、配置校验、请求构造与响应解码 | Rest：根、Serialization、Http；Soap：根、Http；二者互不引用 |
| `….Factory` / `….DependencyInjection` | `WebApiClientFactory`（派生 `NamedFactory<IWebApiEndpoint>`）、`AddWebApiClients()` | 根、Rest、Soap / 根、Factory |

依赖方向由 `Tests/Junevy.Communication.Architecture.Tests/WebApiLayeringTests.cs` 用 NetArchTest.Rules 强制（方法体里的引用也检查）。REST 与 SOAP 外观只做两件事：构造请求快照、提供"响应 → `WebApiResult<T>`"的委托；超时、重试、拦截器、事件、日志、可用性、健康检查、释放都只在 `EndpointCore` 与 `AttemptLoop` 里实现一次。

## 一次调用的流程

```
外观校验请求 ──非法──> core.Reject（RequestCompleted, Attempts=0, InvalidRequest）
   └─> EndpointCore.ExecuteAsync（Task.Run 进入线程池；已释放则返回 NotConnected，无事件）
        └─> AttemptLoop：分配 CallId，循环：
              AttemptRunner（一次尝试，同一个 TimeoutScope）
                ① 从快照重建 WebApiSendingContext（头与正文是副本）
                ② 拦截器 OnSendingAsync（配置认证第一，其余按注册顺序）；Fail/异常/改了来源 → 不发送，不重试
                ③ 触发 RequestSending
                ④ HttpExchange（不自动重定向，读完响应体，上限 MaxResponseBodySize）
                ⑤ 拦截器 OnReceivedAsync（逆序）；可替换正文、Fail、RequestReplay
                ⑥ 协议层委托解释响应
              重试判定（RetryPlanner）→ 触发 ResponseReceived（WillRetry/RetryDelay）→ 报告可用性 → 等待退避
        └─> RequestCompleted（每次调用恰好一次）
```

## 钩子契约

- **事件旁观，拦截器干预。** 事件：`RequestSending`、`ResponseReceived`、`RequestCompleted`、`AvailabilityChanged`；在调用自身的执行流上同步触发（`EventSubscribers.InvokeEach` 逐订阅者隔离异常）；`SendAsync` 返回时 `RequestCompleted` 的处理器已执行完。`AvailabilityChanged` 经单消费者的有序队列（`OrderedEventQueue`）触发。
- **没有发出的尝试没有事件**：拦截器 `Fail`/抛异常/改了 scheme-host-port，或 `OnSendingAsync` 期间超时，都不触发 `RequestSending` 与 `ResponseReceived`，也不报告给可用性跟踪。
- **释放**：在途尝试的 `ResponseReceived.ErrorKind`、调用结果、`RequestCompleted` 都是 `ConnectionClosed`（`AttemptLoop.MapDisposal` 一处改判，改判后不再重试；可用性读传输层结果 `Cancelled`，不计失败）；释放后的调用返回 `NotConnected`，无事件。
- **拦截器上下文只记录意图**（`Fail`、`RequestReplay`、`MarkSensitiveHeader`），由尝试循环读取。每次尝试从快照重建，修改不累积；`Items` 是同一次调用所有尝试共享的。重放每次调用最多 1 次，不计入 `RetryCount`，不受方法规则限制；`Fail` 优先于重放。
- 事件参数全部有公开构造函数并保存全部参数；请求事件里的 URI 查询参数与敏感头已脱敏（`***`），真正发送的请求不变。

## REST 要点

- `Path` 相对于 BaseUrl（规范化为以 `/` 结尾）：开头的 `/` 去掉后拼接，`http://host/api` + `/orders` = `http://host/api/orders`；绝对地址、`//host/x`、UNC、第一段含冒号（`orders:1`）一律 `InvalidRequest`（`RestPaths`）。
- 正文：`byte[]`/`string` 必须指定 `ContentType`（字符串按 charset 编码）；其他对象经 `IWebApiSerializer`（默认 `System.Text.Json`，camelCase、读取不区分大小写、中文不转义）。头合并顺序：默认头 → 请求头 → 幂等头。
- 响应：非 2xx 按状态码归类（401/403 认证失败，429 资源耗尽，其余对端错误，`HTTP 400 Bad Request.` 格式）；业务包络（`ResponseEnvelopeOptions`）用 `JsonDocument` 定位 code/msg/data，路径区分大小写，业务失败 = `RemoteError` + `RemoteCode` 且不重试；`byte[]`/`string` 返回原始正文（空正文是 `[]`/`""`）；其他类型空正文：无包络成功（`default`），有包络仅 204 成功、其他 2xx 为 `ProtocolViolation`；反序列化失败 `ProtocolViolation`。未知 charset（net8.0 无 GBK）是 `ProtocolViolation`，宿主自行注册 `CodePagesEncodingProvider`。

## SOAP 要点

- 信封 UTF-8 无 BOM；1.1 `text/xml; charset=utf-8` + 带引号的 `SOAPAction`（空 action 是 `""`），1.2 `application/soap+xml; charset=utf-8; action="…"`（空 action 省略参数）。action 为 null、含引号或换行 → `InvalidRequest`。
- 强类型正文只用 `new XmlSerializer(Type)` / `new XmlSerializer(Type, string)`（其他重载每次生成新程序集，泄漏），按类型缓存；序列化给显式命名空间表（空前缀绑定正文根元素的命名空间），`xsi:type="xsd:int"` 这类 QName 值属性的前缀声明保持有效，net8.0 与 net472 输出一致。
- 响应：`DtdProcessing.Prohibit` + `XmlResolver = null`；Fault 先于状态码解析、`RemoteError`、`RemoteCode` = faultcode（1.2：Code/Value，有 Subcode 时消息附 ` (subcode: 值)`；缺失时为空串，保证永不重试）；信封版本按响应根元素的命名空间判定。
- `SoapClientConfig.HealthCheckUrl` 必须与 `Url` 同源；`WebApiComponents.Serializer` 对 SOAP 无效，设置即构造时抛 `ArgumentException`。

## 其他约定

- 配置：`WebApiEndpointConfig`（共有字段）← `WebApiClientConfig` / `SoapClientConfig`；构造时校验（`EndpointConfigRules` + `RestConfigRules`/`SoapConfigRules`）并拍快照（`EndpointSettings` 提供 `IsRedactedHeader`/`IsRedactedQueryParameter`，大小写规则只在快照里定一次）。
- 凭据：`WebApiAuthOptions` 只存环境变量名，构造时读取，缺失抛 `ArgumentException` 且只含变量名；代码注入用 `WebApiAuthentication.Basic/Bearer/ApiKey`（都是拦截器，写入的头标记为敏感）。
- HttpClient：每个客户端一个；net8.0 `SocketsHttpHandler`（连接 5 分钟），net472 `HttpClientHandler` + 本端点的 `ServicePoint`（`ConnectionLimit`、`ConnectionLeaseTimeout`），绝不修改 `ServicePointManager` 静态属性；注入的 `HttpHandler` 不由客户端释放。条件编译只在 `HttpHandlerFactory.cs`。
- 工厂：`WebApiClientFactory.GetOrAdd` 的 REST 与 SOAP 重载共用一个名称空间；类型不符 → `InvalidOperationException`（消息含现有实例的类型）；名称、别名、移除即释放由 `NamedFactory` 提供。
- 测试：`StubHttpHandler`（不开套接字）驱动绝大多数测试；`RawHttpServer`（`TcpListener`）+ `SocketTiming` 集合做真实套接字端到端；`ReadmeSampleTests` 运行 README 里的两个示例拦截器并检查 README 与 `ReadmeSamples.cs` 逐字一致。

相关：[[WebApi/重试与可用性行为契约]]、[[Agent协作/开发约定与验收基线]]。
