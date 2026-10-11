using System.Collections.Concurrent;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 共享端点核心的测试装置：用注入的 <see cref="StubHttpHandler"/> 构造 <see cref="EndpointCore"/>，记录可用性变化与请求事件。
/// 等待可用性变化用信号量（带超时），不轮询、不睡眠。
/// </summary>
internal sealed class CoreRig : IAsyncDisposable
{
    private static readonly TimeSpan ChangeWait = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim changeSignal = new SemaphoreSlim(0);

    /// <param name="configure">修改配置。</param>
    /// <param name="healthCheckUrl">健康检查地址；null 表示不做健康检查。</param>
    /// <param name="script">在核心创建之前编排处理器的脚本（健康检查在构造后立即开始，脚本必须先就绪）。</param>
    /// <param name="configureComponents">修改代码级组件（拦截器等）。</param>
    public CoreRig(Action<TestEndpointConfig>? configure = null, string? healthCheckUrl = null, Action<StubHttpHandler>? script = null,
                   Action<WebApiComponents>? configureComponents = null)
    {
        var config = new TestEndpointConfig { Retry = new WebApiRetryOptions { RetryDelay = 0 } };
        configure?.Invoke(config);
        var components = new WebApiComponents { HttpHandler = Handler };
        configureComponents?.Invoke(components);
        Settings = EndpointConfigRules.CreateSettings(config);
        HealthCheckUri = healthCheckUrl == null ? null : new Uri(healthCheckUrl);
        script?.Invoke(Handler);

        Core = new EndpointCore(this, "mes", LoopRig.BaseAddress, HealthCheckUri, Settings, components, Logger);
        Core.Events.AvailabilityChanged += (sender, args) =>
        {
            Changes.Enqueue(args);
            changeSignal.Release();
        };
        Core.Events.RequestCompleted += (sender, args) => Completed.Enqueue(args);
        Core.Events.RequestSending += (sender, args) => Sending.Enqueue(args);
        Core.Events.ResponseReceived += (sender, args) => Responses.Enqueue(args);
    }

    public StubHttpHandler Handler { get; } = new StubHttpHandler();

    public TestLogger Logger { get; } = new TestLogger();

    public EndpointSettings Settings { get; }

    public Uri? HealthCheckUri { get; }

    public EndpointCore Core { get; }

    public ConcurrentQueue<WebApiAvailabilityChangedEventArgs> Changes { get; } = new ConcurrentQueue<WebApiAvailabilityChangedEventArgs>();

    public ConcurrentQueue<WebApiCompletedEventArgs> Completed { get; } = new ConcurrentQueue<WebApiCompletedEventArgs>();

    public ConcurrentQueue<WebApiResponseEventArgs> Responses { get; } = new ConcurrentQueue<WebApiResponseEventArgs>();

    public ConcurrentQueue<WebApiSendingEventArgs> Sending { get; } = new ConcurrentQueue<WebApiSendingEventArgs>();

    /// <summary>等到至少又发生了一次可用性变化事件（每次调用消费一次）。</summary>
    public async Task WaitForChangeAsync()
    {
        if (!await changeSignal.WaitAsync(ChangeWait))
            throw new TimeoutException("No availability change was raised in time.");
    }

    public Task<WebApiResult<string>> SendAsync(string method = "GET", string path = "orders", string? idempotencyKey = null,
                                                CancellationToken cancellationToken = default)
    {
        HttpRequestSnapshot snapshot = HttpRequestSnapshot.Create(Settings, method, path, null, null, Array.Empty<byte>(), idempotencyKey);
        return Core.ExecuteAsync(snapshot, null, TestInterpreter.Text, cancellationToken);
    }

    public ValueTask DisposeAsync() => Core.DisposeAsync();
}
