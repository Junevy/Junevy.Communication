using System.Text;
using Junevy.Communication.WebApi.Http;

namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// 把 <see cref="WebApiRequest"/> 转成可重放的请求快照（内部，计划 Task 8 第 1 条）：校验、拼接路径与查询、序列化正文、合并头。
/// 参数非法时不抛出异常，而是返回英文原因，由外观交给 <c>Reject</c> 以 <c>InvalidRequest</c> 结束调用。
/// </summary>
internal sealed class RestRequestBuilder
{
    private readonly EndpointSettings endpoint;
    private readonly IWebApiSerializer serializer;

    /// <summary>创建构造器。</summary>
    /// <param name="endpoint">共有字段的配置快照（默认头与幂等头名）。</param>
    /// <param name="serializer">对象正文的序列化器。</param>
    public RestRequestBuilder(EndpointSettings endpoint, IWebApiSerializer serializer)
    {
        this.endpoint = endpoint;
        this.serializer = serializer;
    }

    /// <summary>
    /// 构造请求快照。
    /// </summary>
    /// <param name="request">请求；可为 null（视为非法）。</param>
    /// <param name="snapshot">成功时的快照；失败为 null。</param>
    /// <param name="error">失败时的英文原因；成功为 null。</param>
    /// <returns>是否成功。</returns>
    public bool TryBuild(WebApiRequest? request, out HttpRequestSnapshot? snapshot, out string? error)
    {
        snapshot = null;
        error = Validate(request);
        if (error != null)
            return false;

        WebApiRequest valid = request!;
        error = BuildRelativeUri(valid, out string relativeUri);
        if (error != null || !TryBuildBody(valid, out byte[] body, out string? contentType, out error))
            return false;

        snapshot = HttpRequestSnapshot.Create(endpoint, valid.Method.Method, relativeUri, valid.Headers, contentType, body, valid.IdempotencyKey);
        return true;
    }

    private static string? Validate(WebApiRequest? request)
    {
        if (request == null)
            return "The request must not be null.";
        if (request.Method == null)
            return "The request method must not be null.";
        if (request.Path == null)
            return "The request path must not be null.";
        if (RestPaths.IsAbsoluteReference(request.Path))
            return $"The request path '{request.Path}' must be relative to the base address.";
        if ((request.Body is byte[] || request.Body is string) && string.IsNullOrWhiteSpace(request.ContentType))
            return "A byte[] or string body requires an explicit ContentType.";

        return EndpointArguments.TimeoutError(request.Timeout);
    }

    private static string? BuildRelativeUri(WebApiRequest request, out string relativeUri)
    {
        var query = new StringBuilder();
        foreach (KeyValuePair<string, string?> item in request.Query ?? Enumerable.Empty<KeyValuePair<string, string?>>())
        {
            if (string.IsNullOrEmpty(item.Key))
            {
                relativeUri = string.Empty;
                return "A query parameter name must not be empty.";
            }

            if (item.Value == null)
                continue;

            query.Append(query.Length == 0 ? string.Empty : "&").Append(Uri.EscapeDataString(item.Key)).Append('=').Append(Uri.EscapeDataString(item.Value));
        }

        string path = RestPaths.ToRelative(request.Path);
        string separator = path.IndexOf('?') >= 0 ? "&" : "?";
        relativeUri = query.Length == 0 ? path : path + separator + query;
        return null;
    }

    private bool TryBuildBody(WebApiRequest request, out byte[] body, out string? contentType, out string? error)
    {
        body = Array.Empty<byte>();
        contentType = request.ContentType;
        error = null;
        switch (request.Body)
        {
            case null:
                return true;
            case byte[] bytes:
                body = bytes;
                return true;
            case string text:
                return TryEncodeText(text, request.ContentType, out body, out error);
            default:
                return TrySerialize(request, out body, out contentType, out error);
        }
    }

    private static bool TryEncodeText(string text, string? contentType, out byte[] body, out string? error)
    {
        body = Array.Empty<byte>();
        error = null;
        if (!RestCharset.TryResolve(contentType, out Encoding encoding, out string? charset))
        {
            error = $"The charset '{charset}' of the request content type is not supported.";
            return false;
        }

        body = encoding.GetBytes(text);
        return true;
    }

    // 序列化器的异常属于调用方给了它处理不了的对象：边界处转换为原因文本，调用以 InvalidRequest 结束。
    private bool TrySerialize(WebApiRequest request, out byte[] body, out string? contentType, out string? error)
    {
        body = Array.Empty<byte>();
        contentType = request.ContentType ?? serializer.ContentType;
        error = null;
        try
        {
            body = serializer.Serialize(request.Body!, request.Body!.GetType());
            return true;
        }
        catch (Exception ex)
        {
            error = $"The request body could not be serialized: {ex.Message}";
            return false;
        }
    }
}
