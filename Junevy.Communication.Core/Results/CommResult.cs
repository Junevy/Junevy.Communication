namespace Junevy.Communication.Core.Results;

/// <summary>
/// 不携带数据的操作结果（不可变）。只能通过 <see cref="Success"/> 与 <see cref="Fail"/> 工厂方法构造。
/// 决策应只看 <see cref="ErrorKind"/>，不要匹配 <see cref="ErrorMessage"/> 字符串。
/// </summary>
public sealed class CommResult
{
    private static readonly CommResult SuccessResult = new CommResult(true, CommErrorKind.None, null, null, null);

    private CommResult(bool isSuccess, CommErrorKind errorKind, string? errorMessage, long? protocolErrorCode, Exception? exception)
    {
        IsSuccess = isSuccess;
        ErrorKind = errorKind;
        ErrorMessage = errorMessage;
        ProtocolErrorCode = protocolErrorCode;
        Exception = exception;
    }

    /// <summary>是否成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>失败的机器可读分类（成功时为 <see cref="CommErrorKind.None"/>）。</summary>
    public CommErrorKind ErrorKind { get; }

    /// <summary>错误信息（英文）；成功时为 null。</summary>
    public string? ErrorMessage { get; }

    /// <summary>协议原始错误码（例如 MC 结束码 0xC051）；没有协议码时为 null。</summary>
    public long? ProtocolErrorCode { get; }

    /// <summary>底层异常，仅供诊断，不参与决策。</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// 成功结果（返回缓存的单例）。
    /// </summary>
    /// <returns>成功结果。</returns>
    public static CommResult Success() => SuccessResult;

    /// <summary>
    /// 失败结果。
    /// </summary>
    /// <param name="message">错误信息（英文）；不能为 null。</param>
    /// <param name="kind">错误分类；不允许为 <see cref="CommErrorKind.None"/>。</param>
    /// <param name="protocolErrorCode">协议原始错误码；没有时为 null。</param>
    /// <param name="exception">底层异常；没有时为 null。</param>
    /// <returns>失败结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> 为 <see cref="CommErrorKind.None"/>。</exception>
    public static CommResult Fail(string message, CommErrorKind kind, long? protocolErrorCode = null, Exception? exception = null)
    {
        ValidateFailure(message, kind);
        return new CommResult(false, kind, message, protocolErrorCode, exception);
    }

    /// <summary>
    /// 把失败转换为携带数据类型 <typeparamref name="T"/> 的失败结果，保留分类、消息、协议码与异常。
    /// </summary>
    /// <typeparam name="T">目标数据类型。</typeparam>
    /// <returns>失败结果。</returns>
    /// <exception cref="InvalidOperationException">对成功结果调用。</exception>
    public CommResult<T> As<T>()
    {
        if (IsSuccess)
            throw new InvalidOperationException("A successful result cannot be converted to a failure of another type.");

        return CommResult<T>.Fail(ErrorMessage!, ErrorKind, ProtocolErrorCode, Exception);
    }

    /// <summary>
    /// 返回可读文本：成功为 "Success"；失败为 "Kind: message"，有协议码时追加 " (code 0x...)"。
    /// </summary>
    /// <returns>可读文本。</returns>
    public override string ToString()
        => IsSuccess ? "Success" : FormatFailure(ErrorKind, ErrorMessage, ProtocolErrorCode);

    internal static string FormatFailure(CommErrorKind kind, string? message, long? protocolErrorCode)
    {
        string text = $"{kind}: {message}";
        return protocolErrorCode.HasValue
            ? $"{text} (code 0x{protocolErrorCode.Value:X})"
            : text;
    }

    internal static void ValidateFailure(string message, CommErrorKind kind)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));
        if (kind == CommErrorKind.None)
            throw new ArgumentException("A failed result cannot carry CommErrorKind.None.", nameof(kind));
    }
}

/// <summary>
/// 携带数据的操作结果（不可变）。只能通过 <c>Success</c> 与 <c>Fail</c> 工厂方法构造。
/// </summary>
/// <typeparam name="T">成功时携带的数据类型。</typeparam>
public sealed class CommResult<T>
{
    private CommResult(bool isSuccess, T? data, CommErrorKind errorKind, string? errorMessage, long? protocolErrorCode, Exception? exception)
    {
        IsSuccess = isSuccess;
        Data = data;
        ErrorKind = errorKind;
        ErrorMessage = errorMessage;
        ProtocolErrorCode = protocolErrorCode;
        Exception = exception;
    }

    /// <summary>是否成功。</summary>
    public bool IsSuccess { get; }

    /// <summary>成功时的数据；失败时为默认值。</summary>
    public T? Data { get; }

    /// <summary>失败的机器可读分类（成功时为 <see cref="CommErrorKind.None"/>）。</summary>
    public CommErrorKind ErrorKind { get; }

    /// <summary>错误信息（英文）；成功时为 null。</summary>
    public string? ErrorMessage { get; }

    /// <summary>协议原始错误码（例如 MC 结束码 0xC051）；没有协议码时为 null。</summary>
    public long? ProtocolErrorCode { get; }

    /// <summary>底层异常，仅供诊断，不参与决策。</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// 成功结果。
    /// </summary>
    /// <param name="data">成功时的数据。</param>
    /// <returns>成功结果。</returns>
    public static CommResult<T> Success(T data)
        => new CommResult<T>(true, data, CommErrorKind.None, null, null, null);

    /// <summary>
    /// 失败结果。
    /// </summary>
    /// <param name="message">错误信息（英文）；不能为 null。</param>
    /// <param name="kind">错误分类；不允许为 <see cref="CommErrorKind.None"/>。</param>
    /// <param name="protocolErrorCode">协议原始错误码；没有时为 null。</param>
    /// <param name="exception">底层异常；没有时为 null。</param>
    /// <returns>失败结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> 为 <see cref="CommErrorKind.None"/>。</exception>
    public static CommResult<T> Fail(string message, CommErrorKind kind, long? protocolErrorCode = null, Exception? exception = null)
    {
        CommResult.ValidateFailure(message, kind);
        return new CommResult<T>(false, default, kind, message, protocolErrorCode, exception);
    }

    /// <summary>
    /// 把失败转换为携带数据类型 <typeparamref name="TOther"/> 的失败结果，保留分类、消息、协议码与异常。
    /// </summary>
    /// <typeparam name="TOther">目标数据类型。</typeparam>
    /// <returns>失败结果。</returns>
    /// <exception cref="InvalidOperationException">对成功结果调用。</exception>
    public CommResult<TOther> As<TOther>()
    {
        if (IsSuccess)
            throw new InvalidOperationException("A successful result cannot be converted to a failure of another type.");

        return CommResult<TOther>.Fail(ErrorMessage!, ErrorKind, ProtocolErrorCode, Exception);
    }

    /// <summary>
    /// 转换为不携带数据的结果，分类、消息、协议码与异常保持不变。
    /// </summary>
    /// <returns>不携带数据的结果。</returns>
    public CommResult ToResult()
        => IsSuccess
            ? CommResult.Success()
            : CommResult.Fail(ErrorMessage!, ErrorKind, ProtocolErrorCode, Exception);

    /// <summary>
    /// 返回可读文本：成功为 "Success"；失败为 "Kind: message"，有协议码时追加 " (code 0x...)"。
    /// </summary>
    /// <returns>可读文本。</returns>
    public override string ToString()
        => IsSuccess ? "Success" : CommResult.FormatFailure(ErrorKind, ErrorMessage, ProtocolErrorCode);
}
