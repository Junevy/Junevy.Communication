using System.Net.Http;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 脚本化的 HTTP 处理器（测试用，经 <c>WebApiComponents.HttpHandler</c> 注入 SOAP 页）：对每个请求调用脚本给出响应，
/// 并记录方法、地址、请求头与正文，便于断言 SOAPAction、Content-Type、信封与重试次数。不开启任何套接字。
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    private readonly List<CapturedRequest> _requests = [];
    private readonly object _gate = new();

    public ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    /// <summary>已收到的请求，按到达顺序。</summary>
    public IReadOnlyList<CapturedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>()))
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        lock (_gate)
        {
            _requests.Add(new CapturedRequest(request.Method.Method, request.RequestUri!, headers, body));
        }

        return _respond(request);
    }

    /// <summary>一次收到的请求。</summary>
    public sealed record CapturedRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string Body);
}
