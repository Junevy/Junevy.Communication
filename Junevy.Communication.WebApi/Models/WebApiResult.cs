using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi;

/// <summary>
/// WebApi 调用的结果（不可变）。只能通过 <see cref="Success"/> 与 <see cref="Fail"/> 工厂方法构造。
/// 与 <see cref="CommResult{T}"/> 的区别是携带 HTTP 状态码与对端业务码；决策应只看 <see cref="ErrorKind"/>，不要匹配 <see cref="ErrorMessage"/> 字符串。
/// </summary>
/// <typeparam name="T">成功时携带的数据类型。</typeparam>
public sealed class WebApiResult<T>
{
    private WebApiResult(bool isSuccess, T? data, int? statusCode, string? remoteCode, string? errorMessage, CommErrorKind errorKind, Exception? exception)
    {
        IsSuccess = isSuccess;
        Data = data;
        StatusCode = statusCode;
        RemoteCode = remoteCode;
        ErrorMessage = errorMessage;
        ErrorKind = errorKind;
        Exception = exception;
    }

    /// <summary>是否成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>成功时的数据；失败时为默认值。</summary>
    public T? Data { get; }

    /// <summary>HTTP 状态码；没有收到 HTTP 响应时为 null。</summary>
    public int? StatusCode { get; }

    /// <summary>对端给出的业务码：业务包络的 code，或 SOAP Fault 的 faultcode / Code；没有时为 null。</summary>
    public string? RemoteCode { get; }

    /// <summary>错误信息（英文）；成功时为 null。</summary>
    public string? ErrorMessage { get; }

    /// <summary>失败的机器可读分类（成功时为 <see cref="CommErrorKind.None"/>）。</summary>
    public CommErrorKind ErrorKind { get; }

    /// <summary>底层异常，仅供诊断，不参与决策。</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// 成功结果。
    /// </summary>
    /// <param name="data">成功时的数据；没有响应正文时为默认值。</param>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <returns>成功结果。</returns>
    public static WebApiResult<T> Success(T? data, int? statusCode)
        => new WebApiResult<T>(true, data, statusCode, null, null, CommErrorKind.None, null);

    /// <summary>
    /// 失败结果。
    /// </summary>
    /// <param name="message">错误信息（英文）；不能为 null。</param>
    /// <param name="kind">错误分类；不允许为 <see cref="CommErrorKind.None"/>。</param>
    /// <param name="statusCode">HTTP 状态码；没有收到响应时为 null。</param>
    /// <param name="remoteCode">对端给出的业务码；没有时为 null。</param>
    /// <param name="exception">底层异常；没有时为 null。</param>
    /// <returns>失败结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> 为 <see cref="CommErrorKind.None"/>。</exception>
    public static WebApiResult<T> Fail(string message, CommErrorKind kind, int? statusCode = null, string? remoteCode = null, Exception? exception = null)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));
        if (kind == CommErrorKind.None)
            throw new ArgumentException("A failed result cannot carry CommErrorKind.None.", nameof(kind));

        return new WebApiResult<T>(false, default, statusCode, remoteCode, message, kind, exception);
    }

    /// <summary>
    /// 把失败转换为携带数据类型 <typeparamref name="TOther"/> 的失败结果，保留分类、消息、状态码、业务码与异常。
    /// </summary>
    /// <typeparam name="TOther">目标数据类型。</typeparam>
    /// <returns>失败结果。</returns>
    /// <exception cref="InvalidOperationException">对成功结果调用。</exception>
    public WebApiResult<TOther> As<TOther>()
    {
        if (IsSuccess)
            throw new InvalidOperationException("A successful result cannot be converted to a failure of another type.");

        return WebApiResult<TOther>.Fail(ErrorMessage!, ErrorKind, StatusCode, RemoteCode, Exception);
    }

    /// <summary>
    /// 返回可读文本：成功为 "Success"，失败为 "Kind: message"；有状态码时追加 "HTTP 503"，有业务码时追加 "remote code X"，
    /// 两者放在同一对括号内，例如 "RemoteError: rejected (HTTP 500, remote code E42)"。
    /// </summary>
    /// <returns>可读文本。</returns>
    public override string ToString()
    {
        string text = IsSuccess ? "Success" : $"{ErrorKind}: {ErrorMessage}";

        var details = new List<string>(2);
        if (StatusCode.HasValue)
            details.Add($"HTTP {StatusCode.Value}");
        if (RemoteCode != null)
            details.Add($"remote code {RemoteCode}");

        return details.Count == 0 ? text : $"{text} ({string.Join(", ", details)})";
    }
}
