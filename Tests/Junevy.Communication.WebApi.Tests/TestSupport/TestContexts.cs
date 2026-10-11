namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 拦截器测试用的上下文工厂：给所有参数提供合理的默认值，测试只写自己关心的那几个。
/// </summary>
internal static class TestContexts
{
    public const string DefaultUri = "http://mes.local/api/report";

    public static WebApiSendingContext Sending(string method = "POST", string uri = DefaultUri, IDictionary<string, string>? headers = null,
                                               byte[]? body = null, string? contentType = "application/json", string? idempotencyKey = null,
                                               IDictionary<string, object?>? items = null, int attempt = 1, long callId = 1)
        => new WebApiSendingContext("mes", callId, attempt, method, new Uri(uri), headers ?? new Dictionary<string, string>(),
                                    body ?? Array.Empty<byte>(), contentType, idempotencyKey, items ?? new Dictionary<string, object?>());

    public static WebApiReceivedContext Received(int statusCode = 200, byte[]? body = null, string? contentType = "application/json",
                                                 IReadOnlyDictionary<string, string>? responseHeaders = null,
                                                 IDictionary<string, object?>? items = null, int attempt = 1, long callId = 1)
        => new WebApiReceivedContext("mes", callId, attempt, "POST", new Uri(DefaultUri), statusCode,
                                     responseHeaders ?? new Dictionary<string, string>(), body ?? Array.Empty<byte>(), contentType,
                                     items ?? new Dictionary<string, object?>());
}
