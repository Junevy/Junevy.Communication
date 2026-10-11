namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 一次 HTTP 交换收到的响应（内部，不可变）：状态码、响应头、已读完的正文与内容类型。协议层（REST / SOAP）据此解释结果。
/// </summary>
internal sealed class HttpExchangeResponse
{
    /// <summary>创建响应。</summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="reasonPhrase">原因短语；没有时为 null。</param>
    /// <param name="headers">响应头（同名多值用 ", " 连接，头名不区分大小写）。</param>
    /// <param name="body">响应正文。</param>
    /// <param name="contentType">响应的内容类型；没有时为 null。</param>
    public HttpExchangeResponse(int statusCode, string? reasonPhrase, IReadOnlyDictionary<string, string> headers, byte[] body, string? contentType)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = headers;
        Body = body;
        ContentType = contentType;
    }

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>原因短语；没有时为 null。</summary>
    public string? ReasonPhrase { get; }

    /// <summary>响应头（同名多值用 ", " 连接，头名不区分大小写）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>响应正文。</summary>
    public byte[] Body { get; }

    /// <summary>响应的内容类型；没有时为 null。</summary>
    public string? ContentType { get; }

    /// <summary>返回一个仅正文不同的副本（拦截器替换了正文之后交给协议层）。</summary>
    /// <param name="body">新的正文。</param>
    /// <returns>副本。</returns>
    public HttpExchangeResponse WithBody(byte[] body)
        => new HttpExchangeResponse(StatusCode, ReasonPhrase, Headers, body, ContentType);
}
