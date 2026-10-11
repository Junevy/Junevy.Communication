using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi;

/// <summary>
/// 拦截器通过上下文的 <c>Fail</c> 记录的失败意图（内部）：上下文只记录，由 Http 命名空间的尝试循环读取并据此结束调用。
/// </summary>
internal sealed class CallFailure
{
    private CallFailure(string message, CommErrorKind kind, Exception? exception)
    {
        Message = message;
        Kind = kind;
        Exception = exception;
    }

    /// <summary>失败信息（英文）。</summary>
    public string Message { get; }

    /// <summary>失败分类，不会是 <see cref="CommErrorKind.None"/>。</summary>
    public CommErrorKind Kind { get; }

    /// <summary>导致失败的异常（拦截器抛出异常时）；仅供诊断，没有时为 null。</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// 校验并创建失败意图。
    /// </summary>
    /// <param name="message">失败信息；不能为 null。</param>
    /// <param name="kind">失败分类；不允许为 <see cref="CommErrorKind.None"/>。</param>
    /// <param name="exception">导致失败的异常；没有时为 null。</param>
    /// <returns>失败意图。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="kind"/> 为 <see cref="CommErrorKind.None"/>。</exception>
    public static CallFailure Create(string message, CommErrorKind kind, Exception? exception = null)
    {
        if (message == null)
            throw new ArgumentNullException(nameof(message));
        if (kind == CommErrorKind.None)
            throw new ArgumentException("A failure cannot carry CommErrorKind.None.", nameof(kind));

        return new CallFailure(message, kind, exception);
    }
}
