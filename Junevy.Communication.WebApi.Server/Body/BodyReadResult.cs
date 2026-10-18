namespace Junevy.Communication.WebApi.Server.Body;

/// <summary>
/// 请求正文的读取结果（内部）。成功时携带解码后的值；失败时携带应返回的状态码与英文消息，异常只供日志。
/// </summary>
internal sealed class BodyReadResult
{
    private BodyReadResult(bool isSuccess, object? value, int statusCode, string? message, Exception? exception)
    {
        IsSuccess = isSuccess;
        Value = value;
        StatusCode = statusCode;
        Message = message;
        Exception = exception;
    }

    /// <summary>是否读取成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>成功时解码后的值（可为 null）；失败时为 null。</summary>
    public object? Value { get; }

    /// <summary>失败时应返回的 HTTP 状态码（400 或 415）；成功时为 200。</summary>
    public int StatusCode { get; }

    /// <summary>失败时的英文消息；成功时为 null。</summary>
    public string? Message { get; }

    /// <summary>失败的底层异常，只供日志；没有时为 null。</summary>
    public Exception? Exception { get; }

    /// <summary>读取成功。</summary>
    /// <param name="value">解码后的值。</param>
    /// <returns>成功结果。</returns>
    public static BodyReadResult Success(object? value) => new BodyReadResult(true, value, 200, null, null);

    /// <summary>读取失败。</summary>
    /// <param name="statusCode">应返回的状态码。</param>
    /// <param name="message">英文消息。</param>
    /// <param name="exception">底层异常；没有时为 null。</param>
    /// <returns>失败结果。</returns>
    public static BodyReadResult Fail(int statusCode, string message, Exception? exception = null)
        => new BodyReadResult(false, null, statusCode, message, exception);
}
