using System.Net.Http;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Http;
using Junevy.Communication.WebApi.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// REST 客户端（设计 15.3）：把请求发给 MES 并解释响应，用于主动上报。本类只构造请求快照并提供解释响应的委托；
/// 超时、重试、拦截器、事件、日志、可用性、健康检查与释放都在共享的端点核心里（与 SOAP 客户端共用）。
/// 运行期失败都体现在 <see cref="WebApiResult{T}"/> 里，不抛出异常。线程安全，可以并发调用；一个实例对应一个长期存活的 HttpClient，请复用而不是每次新建。
/// </summary>
public sealed class WebApiClient : IWebApiClient
{
    private readonly EndpointCore core;
    private readonly RestRequestBuilder requestBuilder;
    private readonly RestResponseDecoder decoder;

    /// <summary>创建客户端，名称为 <see cref="WebApiClientConfig.BaseUrl"/> 的 authority（主机与非默认端口）。</summary>
    /// <param name="config">配置；构造时校验并复制，之后修改它不生效。</param>
    /// <param name="logger">日志记录器；null 表示不记录日志。</param>
    /// <param name="components">代码级组件（拦截器、序列化器、退避策略、HTTP 处理器）；可为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException">配置非法，或认证配置引用的环境变量不存在 / 为空，或拦截器列表含 null。</exception>
    public WebApiClient(WebApiClientConfig config, ILogger<WebApiClient>? logger = null, WebApiComponents? components = null)
        : this(config, null, logger, components)
    {
    }

    /// <summary>创建客户端。</summary>
    /// <param name="name">端点名称（日志与事件参数中使用）；不能为空。</param>
    /// <param name="config">配置；构造时校验并复制，之后修改它不生效。</param>
    /// <param name="logger">日志记录器；null 表示不记录日志。</param>
    /// <param name="components">代码级组件（拦截器、序列化器、退避策略、HTTP 处理器）；可为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 或 <paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentException">名称为空，配置非法，或认证配置引用的环境变量不存在 / 为空，或拦截器列表含 null。</exception>
    public WebApiClient(string name, WebApiClientConfig config, ILogger<WebApiClient>? logger = null, WebApiComponents? components = null)
        : this(config, EndpointArguments.RequireName(name), logger, components)
    {
    }

    private WebApiClient(WebApiClientConfig config, string? name, ILogger<WebApiClient>? logger, WebApiComponents? components)
    {
        RestSettings settings = RestConfigRules.Create(config);
        IWebApiSerializer serializer = components?.Serializer ?? new SystemTextJsonWebApiSerializer();
        requestBuilder = new RestRequestBuilder(settings.Endpoint, serializer);
        decoder = new RestResponseDecoder(serializer, settings.Envelope);
        Config = config;
        core = new EndpointCore(this, name, settings.BaseAddress, settings.HealthCheckUri, settings.Endpoint, components, logger ?? NullLogger<WebApiClient>.Instance);
    }

    /// <inheritdoc />
    public WebApiClientConfig Config { get; }

    /// <inheritdoc />
    public string Name => core.Name;

    /// <inheritdoc />
    public WebApiAvailability Availability => core.Availability;

    /// <inheritdoc />
    public event EventHandler<WebApiAvailabilityChangedEventArgs>? AvailabilityChanged
    {
        add => core.Events.AvailabilityChanged += value;
        remove => core.Events.AvailabilityChanged -= value;
    }

    /// <inheritdoc />
    public event EventHandler<WebApiSendingEventArgs>? RequestSending
    {
        add => core.Events.RequestSending += value;
        remove => core.Events.RequestSending -= value;
    }

    /// <inheritdoc />
    public event EventHandler<WebApiResponseEventArgs>? ResponseReceived
    {
        add => core.Events.ResponseReceived += value;
        remove => core.Events.ResponseReceived -= value;
    }

    /// <inheritdoc />
    public event EventHandler<WebApiCompletedEventArgs>? RequestCompleted
    {
        add => core.Events.RequestCompleted += value;
        remove => core.Events.RequestCompleted -= value;
    }

    /// <inheritdoc />
    public Task<WebApiResult<TResponse>> SendAsync<TResponse>(WebApiRequest request, CancellationToken cancellationToken = default)
    {
        if (!requestBuilder.TryBuild(request, out HttpRequestSnapshot? snapshot, out string? error))
            return Task.FromResult(core.Reject<TResponse>(request?.Method?.Method ?? HttpMethod.Get.Method, error!, CommErrorKind.InvalidRequest));

        return core.ExecuteAsync(snapshot!, request.Timeout, decoder.Decode<TResponse>, cancellationToken);
    }

    /// <summary>
    /// 同步释放：停止健康检查，取消在途请求（返回 <c>ConnectionClosed</c>），之后的调用返回 <c>NotConnected</c>。注入的 HTTP 处理器不会被释放。幂等。
    /// </summary>
    public void Dispose() => core.Dispose();

    /// <summary>异步释放，语义同 <see cref="Dispose"/>，并等待可用性事件派发完（最多 2000 毫秒）。</summary>
    /// <returns>释放完成的任务。</returns>
    public ValueTask DisposeAsync() => core.DisposeAsync();
}
