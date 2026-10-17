using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Server.Config;
using Junevy.Communication.WebApi.Server.Formatting;
using Junevy.Communication.WebApi.Server.Lifecycle;
using Junevy.Communication.WebApi.Server.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// HTTP 服务端的外观（P32-D1 至 D13）：校验配置、登记路由，并把启动、停止与释放交给生命周期。
/// 路由的处理器在线程池上并发执行，线程安全由宿主负责。
/// </summary>
public sealed class WebApiServer : IWebApiServer
{
    private const string LogCategory = "Junevy.Communication.WebApi.Server";

    private readonly ServerLifecycle lifecycle;
    private readonly ServerEvents events;
    private readonly RouteTable routes = new RouteTable();
    private readonly ResponseFormatter formatter;

    /// <summary>
    /// 创建服务器。配置在这里校验并拍快照；非法配置抛出 <see cref="ArgumentException"/>，消息指出字段名与变量名（不含凭据值）。
    /// </summary>
    /// <param name="name">服务器名称，不能为空。</param>
    /// <param name="config">服务器配置。</param>
    /// <param name="components">代码级组件；为 null 时使用默认值。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时不输出日志。</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> 为空，或配置非法。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> 为 null。</exception>
    public WebApiServer(string name, WebApiServerConfig config, WebApiServerComponents? components = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;

        WebApiServerSettings settings = WebApiServerConfigRules.Create(config, components ?? new WebApiServerComponents());
        ILoggerFactory factory = loggerFactory ?? NullLoggerFactory.Instance;
        ILogger logger = factory.CreateLogger(LogCategory);
        formatter = new ResponseFormatter(settings);
        events = new ServerEvents(logger);
        lifecycle = new ServerLifecycle(name, settings, events, factory, logger);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public WebApiServerState State => lifecycle.State;

    /// <inheritdoc />
    public IReadOnlyList<string> Urls => lifecycle.Urls;

    /// <inheritdoc />
    public event EventHandler<WebApiServerStateChangedEventArgs>? StateChanged
    {
        add => lifecycle.StateChanged += value;
        remove => lifecycle.StateChanged -= value;
    }

    /// <inheritdoc />
    public event EventHandler<WebApiServerRequestEventArgs>? RequestReceived
    {
        add => events.RequestReceived += value;
        remove => events.RequestReceived -= value;
    }

    /// <inheritdoc />
    public event EventHandler<WebApiServerCompletedEventArgs>? RequestCompleted
    {
        add => events.RequestCompleted += value;
        remove => events.RequestCompleted -= value;
    }

    /// <inheritdoc />
    public Task<CommResult> StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return lifecycle.StartAsync(routes.Snapshot(), cancellationToken);
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default) => lifecycle.StopAsync(cancellationToken);

    /// <inheritdoc />
    public IWebApiServer MapGet<TResponse>(string template, Func<WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return AddRoute("GET", template, new TypedRoute<TResponse>(null, (_, context, token) => handler(context, token), formatter));
    }

    /// <inheritdoc />
    public IWebApiServer MapDelete<TResponse>(string template, Func<WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return AddRoute("DELETE", template, new TypedRoute<TResponse>(null, (_, context, token) => handler(context, token), formatter));
    }

    /// <inheritdoc />
    public IWebApiServer MapPost<TRequest, TResponse>(string template, Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return AddRoute("POST", template, BodyRoute(handler));
    }

    /// <inheritdoc />
    public IWebApiServer MapPut<TRequest, TResponse>(string template, Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return AddRoute("PUT", template, BodyRoute(handler));
    }

    /// <inheritdoc />
    public void Dispose() => lifecycle.DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => lifecycle.DisposeAsync();

    private TypedRoute<TResponse> BodyRoute<TRequest, TResponse>(Func<TRequest, WebApiServerContext, CancellationToken, Task<WebApiServerResponse<TResponse>>> handler)
        => new TypedRoute<TResponse>(
            typeof(TRequest),
            (request, context, token) => handler((TRequest)request!, context, token),
            formatter);

    private IWebApiServer AddRoute(string method, string template, IRoute route)
    {
        ThrowIfDisposed();
        if (lifecycle.StartRequested)
            throw new InvalidOperationException("Routes must be registered before the server is started.");
        ArgumentException.ThrowIfNullOrEmpty(template);

        routes.Add(new RouteDefinition(method, template, route));
        return this;
    }

    private void ThrowIfDisposed()
    {
        if (lifecycle.IsDisposed)
            throw new ObjectDisposedException(nameof(WebApiServer));
    }
}
