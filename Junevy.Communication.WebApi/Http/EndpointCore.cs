using System.Net.Http;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Auth;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// REST 与 SOAP 共用的端点核心（内部，P3-D7）：配置快照、HttpClient、拦截器链、尝试循环、重试、超时、日志、可用性、健康检查、事件与释放。
/// <c>WebApiClient</c> 与 <c>SoapClient</c> 是薄外观：它们只构造请求快照并提供解释响应的委托，外观的 <c>event</c> 用 <c>add</c> / <c>remove</c> 转发到 <see cref="Events"/>。
/// 公开调用在线程池上进入管线（P3-D8），所以拦截器与请求事件总在线程池线程上运行，不会在调用方的同步上下文里执行。
/// </summary>
internal sealed class EndpointCore : IAsyncDisposable, IDisposable
{
    /// <summary>释放时等待可用性事件派发完的最长毫秒数；超过则丢弃剩余事件并记 Warning。</summary>
    internal const int DisposeDrainMilliseconds = 2000;

    private const int DisposeGraceMilliseconds = 1000;
    private const string DisposedMessage = "The WebApi client has been disposed.";

    private readonly string name;
    private readonly ILogger logger;
    private readonly HttpMessageHandler? ownedHandler;
    private readonly HttpClient client;
    private readonly AttemptLoop loop;
    private readonly AvailabilityTracker tracker;
    private readonly OrderedEventQueue<WebApiAvailabilityChangedEventArgs> availabilityQueue;
    private readonly HealthCheckLoop? healthCheck;

    // 没有计时器与等待句柄，且释放之后仍会被在途调用读取令牌，所以不 Dispose。
    private readonly CancellationTokenSource closing = new CancellationTokenSource();
    private readonly CancellationToken closingToken;
    private int disposed;

    /// <summary>
    /// 组装核心。会抛出异常的步骤（拦截器校验、读取认证环境变量）在创建任何资源之前完成。
    /// </summary>
    /// <param name="sender">事件的发送者（外观对象）。</param>
    /// <param name="name">端点名称。</param>
    /// <param name="baseAddress">绝对基地址；相对 URI 以它为基准。</param>
    /// <param name="healthCheckUri">健康检查的绝对地址；不做健康检查为 null。</param>
    /// <param name="settings">已校验的配置快照。</param>
    /// <param name="components">代码级组件；可为 null。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentException">拦截器列表含 null，或认证配置引用的环境变量不存在 / 为空。</exception>
    public EndpointCore(object sender, string name, Uri baseAddress, Uri? healthCheckUri, EndpointSettings settings, WebApiComponents? components, ILogger logger)
    {
        IReadOnlyList<IWebApiInterceptor> userInterceptors = CopyInterceptors(components);
        IWebApiInterceptor? authentication = AuthInterceptorFactory.FromOptions(settings.Auth);

        this.name = name;
        this.logger = logger;
        closingToken = closing.Token;
        HttpMessageHandler handler = components?.HttpHandler ?? (ownedHandler = HttpHandlerFactory.Create(settings, baseAddress));
        client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };

        var identity = new EndpointIdentity(name, baseAddress);
        var exchange = new HttpExchange(client, settings.MaxResponseBodySize);
        var chain = InterceptorChain.Create(authentication, userInterceptors, logger);
        Events = new EndpointEvents(sender, settings, logger);
        availabilityQueue = new OrderedEventQueue<WebApiAvailabilityChangedEventArgs>(Events.RaiseAvailabilityChanged);
        tracker = new AvailabilityTracker(name, settings.OfflineThreshold, availabilityQueue.Enqueue, logger);
        loop = new AttemptLoop(identity, settings, exchange, chain, components?.RetryBackoff, Events, tracker, logger);

        if (healthCheckUri != null)
        {
            var silentEvents = new EndpointEvents(sender, settings, logger);
            var runner = new AttemptRunner(identity, exchange, chain, silentEvents, new WebApiRedactor(settings));
            healthCheck = HealthCheckLoop.Start(runner, healthCheckUri, settings, tracker, logger);
        }
    }

    /// <summary>外观的事件转发到这里。</summary>
    public EndpointEvents Events { get; }

    /// <summary>对端当前的可用性。</summary>
    public WebApiAvailability Availability => tracker.Availability;

    private bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>
    /// 执行一次调用。释放之后返回 <see cref="CommErrorKind.NotConnected"/> 且不触发事件；释放时仍在途的调用以 <see cref="CommErrorKind.ConnectionClosed"/> 结束。
    /// </summary>
    /// <typeparam name="T">结果的数据类型。</typeparam>
    /// <param name="snapshot">请求快照。</param>
    /// <param name="timeout">本次调用每次尝试的超时（毫秒），覆盖配置值；null 使用配置值。</param>
    /// <param name="interpret">协议层解释响应的委托。</param>
    /// <param name="cancellationToken">调用方的取消令牌。</param>
    /// <returns>调用的最终结果。</returns>
    public Task<WebApiResult<T>> ExecuteAsync<T>(HttpRequestSnapshot snapshot, int? timeout, ResponseInterpreter<T> interpret, CancellationToken cancellationToken)
    {
        if (IsDisposed)
            return Task.FromResult(WebApiResult<T>.Fail(DisposedMessage, CommErrorKind.NotConnected));

        return Task.Run(() => loop.ExecuteAsync(snapshot, timeout, interpret, cancellationToken, closingToken), CancellationToken.None);
    }

    /// <summary>
    /// 以失败结束一个参数非法的调用（<c>RequestCompleted</c> 的 <c>Attempts = 0</c>）；释放之后返回 <see cref="CommErrorKind.NotConnected"/>。
    /// </summary>
    /// <typeparam name="T">结果的数据类型。</typeparam>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="message">失败信息（英文）。</param>
    /// <param name="kind">失败分类。</param>
    /// <returns>失败结果。</returns>
    public WebApiResult<T> Reject<T>(string method, string message, CommErrorKind kind)
        => IsDisposed ? WebApiResult<T>.Fail(DisposedMessage, CommErrorKind.NotConnected) : loop.Reject<T>(method, message, kind);

    /// <summary>
    /// 同步释放：等待 <see cref="DisposeAsync"/> 完成，最长 <see cref="DisposeDrainMilliseconds"/> 加 1000 毫秒的宽限；超时后返回，释放在后台继续。
    /// 在可用性事件处理器内调用时不会等待事件派发，因此不会死锁。
    /// </summary>
    public void Dispose()
    {
        int limit = DisposeDrainMilliseconds + DisposeGraceMilliseconds;
        if (!DisposeAsync().AsTask().Wait(limit))
            logger.LogWarning("Disposing the endpoint {Endpoint} did not finish within {Timeout} ms; the release continues in the background.", name, limit);
    }

    /// <summary>
    /// 异步释放：幂等。停止健康检查，取消在途调用（返回 <see cref="CommErrorKind.ConnectionClosed"/>），等待可用性事件派发完（最多 <see cref="DisposeDrainMilliseconds"/>，
    /// 剩余的丢弃并记 Warning），然后释放自己创建的 HttpClient 与处理器。注入的处理器不释放（P3-D12）。
    /// </summary>
    /// <returns>释放完成的任务。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        closing.Cancel();
        if (healthCheck != null)
            await healthCheck.StopAsync().ConfigureAwait(false);

        int dropped = await availabilityQueue.CompleteAsync(DisposeDrainMilliseconds).ConfigureAwait(false);
        if (dropped > 0)
            logger.LogWarning("{Dropped} pending availability event(s) of {Endpoint} were discarded because the subscribers did not finish within {Timeout} ms.",
                              dropped, name, DisposeDrainMilliseconds);

        client.Dispose();
        ownedHandler?.Dispose();
    }

    private static IReadOnlyList<IWebApiInterceptor> CopyInterceptors(WebApiComponents? components)
    {
        if (components == null)
            return Array.Empty<IWebApiInterceptor>();

        IWebApiInterceptor[] copy = components.Interceptors.ToArray();
        if (copy.Any(interceptor => interceptor == null))
            throw new ArgumentException("WebApiComponents.Interceptors must not contain null.", nameof(components));

        return copy;
    }
}
