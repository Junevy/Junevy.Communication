using System.Collections.Concurrent;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Soap;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// SOAP 客户端的测试装置：用注入的 <see cref="StubHttpHandler"/> 构造 <see cref="SoapClient"/>，记录三个请求事件。
/// 健康检查（若配置）在构造后立即开始，所以脚本通过 <c>script</c> 参数在构造之前编排好。
/// </summary>
internal sealed class SoapRig : IAsyncDisposable
{
    public const string Url = "http://mes.local/service.asmx";

    public SoapRig(Action<SoapClientConfig>? configure = null, Action<WebApiComponents>? configureComponents = null,
                   Action<StubHttpHandler>? script = null, string? name = "mes")
    {
        var config = new SoapClientConfig { Url = Url, Retry = new WebApiRetryOptions { RetryDelay = 0 } };
        configure?.Invoke(config);
        var components = new WebApiComponents { HttpHandler = Handler };
        configureComponents?.Invoke(components);
        script?.Invoke(Handler);

        var logger = new TypedLogger<SoapClient>(Logger);
        Client = name == null ? new SoapClient(config, logger, components) : new SoapClient(name, config, logger, components);
        Client.RequestSending += (sender, args) => Sending.Enqueue(args);
        Client.ResponseReceived += (sender, args) => Responses.Enqueue(args);
        Client.RequestCompleted += (sender, args) => Completed.Enqueue(args);
    }

    public StubHttpHandler Handler { get; } = new StubHttpHandler();

    public TestLogger Logger { get; } = new TestLogger();

    public SoapClient Client { get; }

    public ConcurrentQueue<WebApiSendingEventArgs> Sending { get; } = new ConcurrentQueue<WebApiSendingEventArgs>();

    public ConcurrentQueue<WebApiResponseEventArgs> Responses { get; } = new ConcurrentQueue<WebApiResponseEventArgs>();

    public ConcurrentQueue<WebApiCompletedEventArgs> Completed { get; } = new ConcurrentQueue<WebApiCompletedEventArgs>();

    /// <summary>一个 SOAP 1.1 成功响应信封，Body 的内容由调用方给出。</summary>
    public static string Soap11Response(string bodyXml)
        => $"<?xml version=\"1.0\" encoding=\"utf-8\"?><soap:Envelope xmlns:soap=\"{SoapTestNamespaces.Soap11}\"><soap:Body>{bodyXml}</soap:Body></soap:Envelope>";

    public ValueTask DisposeAsync() => Client.DisposeAsync();
}
