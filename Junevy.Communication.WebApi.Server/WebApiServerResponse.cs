namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 处理器的返回值（P32-D6）：成功携带数据，业务失败携带业务码与消息。只能通过 <see cref="Ok"/> 与 <see cref="Fail"/> 构造。
/// </summary>
/// <typeparam name="T">成功时携带的数据类型。</typeparam>
public sealed class WebApiServerResponse<T>
{
    private WebApiServerResponse(bool isSuccess, T? data, int? statusCode, string? code, string? message)
    {
        IsSuccess = isSuccess;
        Data = data;
        StatusCode = statusCode;
        Code = code;
        Message = message;
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>是否成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>成功时的数据；业务失败时为默认值。</summary>
    public T? Data { get; }

    /// <summary>显式指定的 HTTP 状态码；null 表示按 P32-D6 的默认值。</summary>
    public int? StatusCode { get; }

    /// <summary>业务码；成功时为 null。</summary>
    public string? Code { get; }

    /// <summary>消息；成功时为 null。</summary>
    public string? Message { get; }

    /// <summary>额外的响应头（名称不区分大小写）。</summary>
    public IDictionary<string, string> Headers { get; }

    /// <summary>
    /// 成功结果。
    /// </summary>
    /// <param name="data">数据；null 时不用包络写出为 204，用包络写出为 <c>data: null</c>。</param>
    /// <param name="statusCode">显式状态码（2xx，例如 201 或 202）；null 表示 200。</param>
    /// <returns>成功结果。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> 不是 2xx。</exception>
    public static WebApiServerResponse<T> Ok(T? data, int? statusCode = null)
    {
        if (statusCode is { } status && (status < 200 || status > 299))
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "A success response must use a 2xx status code.");

        return new WebApiServerResponse<T>(true, data, statusCode, null, null);
    }

    /// <summary>
    /// 业务失败结果。HTTP 状态码默认按配置（包络启用时为 <c>BusinessFailureStatusCode</c>，否则 400）。
    /// </summary>
    /// <param name="code">业务码（英文，不能为空）。</param>
    /// <param name="message">消息（英文）。</param>
    /// <param name="statusCode">显式状态码（200 至 599）；null 表示按配置的默认值。</param>
    /// <returns>业务失败结果。</returns>
    /// <exception cref="ArgumentException"><paramref name="code"/> 为 null 或空。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="statusCode"/> 不在 200 至 599 之间。</exception>
    public static WebApiServerResponse<T> Fail(string code, string message, int? statusCode = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        if (statusCode is { } status && (status < 200 || status > 599))
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "A business failure must use a status code from 200 to 599.");

        return new WebApiServerResponse<T>(false, default, statusCode, code, message);
    }
}
