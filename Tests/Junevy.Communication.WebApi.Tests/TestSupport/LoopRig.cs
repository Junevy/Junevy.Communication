using System.Collections.Concurrent;
using System.Net.Http;
using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 尝试循环的测试装置：用 <see cref="StubHttpHandler"/> 搭出"配置快照 + 拦截器链 + 事件 + 可用性记录 + 尝试循环"，
/// 并把三个请求事件按触发顺序记录下来。Task 6 与 Task 7 的测试共用，不开套接字。
/// </summary>
internal sealed class LoopRig : IDisposable
{
    public static readonly Uri BaseAddress = new Uri("http://mes.local/api/");

    private readonly HttpClient client;
    private readonly ConcurrentQueue<string> timeline = new ConcurrentQueue<string>();

    public LoopRig(Action<TestEndpointConfig>? configure = null, IEnumerable<IWebApiInterceptor>? interceptors = null,
                   IBackoffPolicy? backoff = null, IWebApiInterceptor? authInterceptor = null)
    {
        var config = new TestEndpointConfig { Retry = new WebApiRetryOptions { RetryDelay = 0 } };
        configure?.Invoke(config);
        Settings = EndpointConfigRules.CreateSettings(config);
        client = new HttpClient(Handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        Events = new EndpointEvents(this, Settings, Logger);
        Events.RequestSending += (sender, args) => Record(args, "Sending(" + args.Attempt + ")", Sending);
        Events.ResponseReceived += (sender, args) => Record(args, $"Response({args.Attempt},{args.StatusCode?.ToString() ?? "-"},retry={args.WillRetry})", Responses);
        Events.RequestCompleted += (sender, args) => Record(args, $"Completed({args.Attempts},{args.IsSuccess})", Completed);
        var identity = new EndpointIdentity("mes", BaseAddress);
        var chain = InterceptorChain.Create(authInterceptor, interceptors ?? Array.Empty<IWebApiInterceptor>(), Logger);
        Loop = new AttemptLoop(identity, Settings, new HttpExchange(client, Settings.MaxResponseBodySize), chain, backoff, Events, Availability, Logger);
    }

    public StubHttpHandler Handler { get; } = new StubHttpHandler();

    public TestLogger Logger { get; } = new TestLogger();

    public RecordingAvailability Availability { get; } = new RecordingAvailability();

    public EndpointSettings Settings { get; }

    public EndpointEvents Events { get; }

    public AttemptLoop Loop { get; }

    public ConcurrentQueue<WebApiSendingEventArgs> Sending { get; } = new ConcurrentQueue<WebApiSendingEventArgs>();

    public ConcurrentQueue<WebApiResponseEventArgs> Responses { get; } = new ConcurrentQueue<WebApiResponseEventArgs>();

    public ConcurrentQueue<WebApiCompletedEventArgs> Completed { get; } = new ConcurrentQueue<WebApiCompletedEventArgs>();

    /// <summary>三个请求事件按触发顺序的记录，例如 <c>Sending(1)</c>、<c>Response(1,503,retry=True)</c>、<c>Completed(2,True)</c>。</summary>
    public IReadOnlyList<string> Timeline => timeline.ToArray();

    public Task<WebApiResult<string>> SendAsync(string method = "GET", string path = "orders", string? idempotencyKey = null, byte[]? body = null,
                                                string? contentType = null, IDictionary<string, string>? headers = null, int? timeout = null,
                                                CancellationToken cancellationToken = default)
    {
        HttpRequestSnapshot snapshot = HttpRequestSnapshot.Create(Settings, method, path, headers, contentType, body ?? Array.Empty<byte>(), idempotencyKey);
        return Loop.ExecuteAsync(snapshot, timeout, TestInterpreter.Text, cancellationToken);
    }

    public void Dispose() => client.Dispose();

    private void Record<T>(T args, string entry, ConcurrentQueue<T> queue)
    {
        timeline.Enqueue(entry);
        queue.Enqueue(args);
    }
}

/// <summary>记录尝试结果的可用性报告（Task 7 的 <c>AvailabilityTracker</c> 取代它的位置）。</summary>
internal sealed class RecordingAvailability : IAvailabilityReporter
{
    private readonly ConcurrentQueue<(CommErrorKind Kind, int? StatusCode)> reports = new ConcurrentQueue<(CommErrorKind, int?)>();

    public IReadOnlyList<(CommErrorKind Kind, int? StatusCode)> Reports => reports.ToArray();

    public void ReportAttempt(CommErrorKind transportKind, int? statusCode, Exception? exception) => reports.Enqueue((transportKind, statusCode));
}

/// <summary>
/// 测试用的协议层解释器：2xx 返回响应正文文本；其余按 <see cref="HttpStatusClassifier"/> 归类，响应头 <c>X-Remote-Code</c> 作为业务码。
/// Http 命名空间不认识 REST / SOAP，协议层只传入这样一个委托。
/// </summary>
internal static class TestInterpreter
{
    public static WebApiResult<string> Text(HttpExchangeResponse response)
    {
        string text = System.Text.Encoding.UTF8.GetString(response.Body);
        CommErrorKind kind = HttpStatusClassifier.Classify(response.StatusCode);
        if (kind == CommErrorKind.None)
            return WebApiResult<string>.Success(text, response.StatusCode);

        response.Headers.TryGetValue("X-Remote-Code", out string? remoteCode);
        return WebApiResult<string>.Fail($"HTTP {response.StatusCode}.", kind, response.StatusCode, remoteCode);
    }
}
