using System.Collections.Concurrent;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Rest;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// REST 客户端的测试装置：用注入的 <see cref="StubHttpHandler"/> 构造 <see cref="WebApiClient"/>，记录三个请求事件。
/// 客户端构造后健康检查（若配置）立即开始，所以脚本通过 <c>script</c> 参数在构造之前编排好。
/// </summary>
internal sealed class RestRig : IAsyncDisposable
{
    public const string BaseUrl = "http://mes.local/api";

    public RestRig(Action<WebApiClientConfig>? configure = null, Action<WebApiComponents>? configureComponents = null,
                   Action<StubHttpHandler>? script = null, string? name = "mes")
    {
        var config = new WebApiClientConfig { BaseUrl = BaseUrl, Retry = new WebApiRetryOptions { RetryDelay = 0 } };
        configure?.Invoke(config);
        var components = new WebApiComponents { HttpHandler = Handler };
        configureComponents?.Invoke(components);
        script?.Invoke(Handler);

        var logger = new TypedLogger<WebApiClient>(Logger);
        Client = name == null ? new WebApiClient(config, logger, components) : new WebApiClient(name, config, logger, components);
        Client.RequestSending += (sender, args) => Sending.Enqueue(args);
        Client.ResponseReceived += (sender, args) => Responses.Enqueue(args);
        Client.RequestCompleted += (sender, args) => Completed.Enqueue(args);
    }

    public StubHttpHandler Handler { get; } = new StubHttpHandler();

    public TestLogger Logger { get; } = new TestLogger();

    public WebApiClient Client { get; }

    public ConcurrentQueue<WebApiSendingEventArgs> Sending { get; } = new ConcurrentQueue<WebApiSendingEventArgs>();

    public ConcurrentQueue<WebApiResponseEventArgs> Responses { get; } = new ConcurrentQueue<WebApiResponseEventArgs>();

    public ConcurrentQueue<WebApiCompletedEventArgs> Completed { get; } = new ConcurrentQueue<WebApiCompletedEventArgs>();

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
