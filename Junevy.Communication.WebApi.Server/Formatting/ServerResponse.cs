namespace Junevy.Communication.WebApi.Server.Formatting;

/// <summary>
/// 写给线上的响应（内部）：状态码、内容类型、正文与额外响应头。正文为 null 时不写正文。
/// </summary>
internal sealed class ServerResponse
{
    /// <summary>创建响应。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="contentType">内容类型；没有正文时为 null。</param>
    /// <param name="body">正文字节；没有正文时为 null。</param>
    /// <param name="headers">额外的响应头（副本，名称不区分大小写）。</param>
    public ServerResponse(int statusCode, string? contentType, byte[]? body, IDictionary<string, string> headers)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
        Headers = headers;
    }

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>内容类型；没有正文时为 null。</summary>
    public string? ContentType { get; }

    /// <summary>正文字节；没有正文时为 null。</summary>
    public byte[]? Body { get; }

    /// <summary>额外的响应头。</summary>
    public IDictionary<string, string> Headers { get; }
}
