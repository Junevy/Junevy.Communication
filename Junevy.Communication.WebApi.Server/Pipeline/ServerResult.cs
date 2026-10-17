using Junevy.Communication.WebApi.Server.Formatting;

namespace Junevy.Communication.WebApi.Server.Pipeline;

/// <summary>
/// 一次请求的处理结果（内部）：要写出的响应（没有时为 null，例如对端已断开），以及事件需要的分类与诊断信息。
/// </summary>
internal sealed class ServerResult
{
    /// <summary>创建结果。</summary>
    /// <param name="statusCode">将要写出的状态码；没有响应时为 0。</param>
    /// <param name="response">要写出的响应；没有时为 null。</param>
    /// <param name="outcome">结果分类。</param>
    /// <param name="code">业务码或状态码文本；没有时为 null。</param>
    /// <param name="message">英文消息；没有时为 null。</param>
    /// <param name="exception">底层异常，只供诊断；没有时为 null。</param>
    public ServerResult(int statusCode, ServerResponse? response, WebApiServerOutcome outcome, string? code = null, string? message = null, Exception? exception = null)
    {
        StatusCode = statusCode;
        Response = response;
        Outcome = outcome;
        Code = code;
        Message = message;
        Exception = exception;
    }

    /// <summary>将要写出的状态码；没有响应时为 0。</summary>
    public int StatusCode { get; }

    /// <summary>要写出的响应；没有时为 null。</summary>
    public ServerResponse? Response { get; }

    /// <summary>结果分类。</summary>
    public WebApiServerOutcome Outcome { get; }

    /// <summary>业务码或状态码文本。</summary>
    public string? Code { get; }

    /// <summary>英文消息。</summary>
    public string? Message { get; }

    /// <summary>底层异常。</summary>
    public Exception? Exception { get; }
}
