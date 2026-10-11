using System.Net;
using System.Net.Http;
using System.Text;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 按脚本应答的 HTTP 处理器（不开套接字）：每个请求消费脚本中的下一项——返回响应、抛出异常或一直等到取消——并把收到的请求记录下来。
/// Task 5–7 的测试共用它。脚本用完后再收到请求会抛出 <see cref="InvalidOperationException"/>，避免测试悄悄多发请求。
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly object sync = new object();
    private readonly Queue<Func<StubRequest, CancellationToken, Task<HttpResponseMessage>>> script
        = new Queue<Func<StubRequest, CancellationToken, Task<HttpResponseMessage>>>();
    private readonly List<StubRequest> requests = new List<StubRequest>();

    /// <summary>处理器是否已被释放（验证"注入的处理器不由客户端释放"）。</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>已收到的请求（快照，按到达顺序）。</summary>
    public IReadOnlyList<StubRequest> Requests
    {
        get
        {
            lock (sync)
                return requests.ToArray();
        }
    }

    /// <summary>脚本中是否还有未消费的项。</summary>
    public int PendingScriptCount
    {
        get
        {
            lock (sync)
                return script.Count;
        }
    }

    /// <summary>追加一个文本响应。</summary>
    public StubHttpHandler Enqueue(int statusCode, string body = "", string? contentType = "application/json", IDictionary<string, string>? headers = null)
        => EnqueueBytes(statusCode, Encoding.UTF8.GetBytes(body), contentType, headers);

    /// <summary>追加一个字节响应（正文带 Content-Length）。</summary>
    public StubHttpHandler EnqueueBytes(int statusCode, byte[] body, string? contentType = "application/json", IDictionary<string, string>? headers = null)
        => EnqueueResponse(() =>
        {
            var content = new ByteArrayContent(body);
            return Build(statusCode, content, contentType, headers);
        });

    /// <summary>追加一个没有 Content-Length 的响应（模拟分块传输）。</summary>
    public StubHttpHandler EnqueueChunked(int statusCode, byte[] body, string? contentType = "application/json")
        => EnqueueResponse(() => Build(statusCode, new UnknownLengthContent(body), contentType, null));

    /// <summary>追加一个自定义构造的响应。</summary>
    public StubHttpHandler EnqueueResponse(Func<HttpResponseMessage> factory)
        => EnqueueAsync((request, token) => Task.FromResult(factory()));

    /// <summary>追加一个抛出异常的项。</summary>
    public StubHttpHandler EnqueueException(Exception exception)
        => EnqueueAsync((request, token) => throw exception);

    /// <summary>追加一个一直等到取消的项（取消时抛出 <see cref="TaskCanceledException"/>）。</summary>
    public StubHttpHandler EnqueueHang()
        => EnqueueAsync(async (request, token) =>
        {
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            throw new InvalidOperationException("Unreachable.");
        });

    /// <summary>追加一个由测试控制的异步项。</summary>
    public StubHttpHandler EnqueueAsync(Func<StubRequest, CancellationToken, Task<HttpResponseMessage>> step)
    {
        lock (sync)
            script.Enqueue(step);
        return this;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        StubRequest recorded = await StubRequest.CaptureAsync(request).ConfigureAwait(false);
        Func<StubRequest, CancellationToken, Task<HttpResponseMessage>> step;
        lock (sync)
        {
            requests.Add(recorded);
            if (script.Count == 0)
                throw new InvalidOperationException("The stub handler has no scripted response left.");
            step = script.Dequeue();
        }

        return await step(recorded, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }

    private static HttpResponseMessage Build(int statusCode, HttpContent content, string? contentType, IDictionary<string, string>? headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)statusCode) { Content = content };
        if (contentType != null)
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        if (headers != null)
        {
            foreach (KeyValuePair<string, string> header in headers)
                response.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return response;
    }
}

/// <summary><see cref="StubHttpHandler"/> 收到的一个请求（内容已读完，可在请求释放后检查）。</summary>
internal sealed class StubRequest
{
    private StubRequest(string method, Uri uri, IReadOnlyDictionary<string, string> headers, IReadOnlyDictionary<string, string> contentHeaders,
                        bool hasContent, byte[] body, bool? expectContinue)
    {
        Method = method;
        Uri = uri;
        Headers = headers;
        ContentHeaders = contentHeaders;
        HasContent = hasContent;
        Body = body;
        ExpectContinue = expectContinue;
    }

    /// <summary>HTTP 方法。</summary>
    public string Method { get; }

    /// <summary>请求 URI。</summary>
    public Uri Uri { get; }

    /// <summary>请求头（不含内容头）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>内容头（例如 Content-Type）；没有内容时为空。</summary>
    public IReadOnlyDictionary<string, string> ContentHeaders { get; }

    /// <summary>请求是否带内容对象。</summary>
    public bool HasContent { get; }

    /// <summary>请求正文。</summary>
    public byte[] Body { get; }

    /// <summary><c>Expect: 100-continue</c> 的设置（false 表示显式关闭）。</summary>
    public bool? ExpectContinue { get; }

    /// <summary>读取请求的全部内容。</summary>
    public static async Task<StubRequest> CaptureAsync(HttpRequestMessage request)
    {
        byte[] body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return new StubRequest(request.Method.Method, request.RequestUri!, Flatten(request.Headers),
                               request.Content == null ? new Dictionary<string, string>() : Flatten(request.Content.Headers),
                               request.Content != null, body, request.Headers.ExpectContinue);
    }

    private static IReadOnlyDictionary<string, string> Flatten(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
        => headers.ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);
}

/// <summary>长度未知的内容：不提供 Content-Length，用来模拟分块传输。</summary>
internal sealed class UnknownLengthContent : HttpContent
{
    private readonly byte[] data;

    public UnknownLengthContent(byte[] data)
    {
        this.data = data;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => stream.WriteAsync(data, 0, data.Length);

    protected override bool TryComputeLength(out long length)
    {
        length = -1;
        return false;
    }
}
