namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 认证结果（不可变）。只能通过 <see cref="Success"/> 与 <see cref="Fail"/> 工厂方法构造。
/// </summary>
public sealed class WebApiAuthenticationResult
{
    private WebApiAuthenticationResult(bool isAuthenticated, string? userName, string? failureMessage)
    {
        IsAuthenticated = isAuthenticated;
        UserName = userName;
        FailureMessage = failureMessage;
    }

    /// <summary>是否认证通过。</summary>
    public bool IsAuthenticated { get; }

    /// <summary>认证通过时的身份名称；没有时为 null。</summary>
    public string? UserName { get; }

    /// <summary>认证失败的原因（只写日志，不返回给调用方）；成功时为 null。</summary>
    public string? FailureMessage { get; }

    /// <summary>认证通过。</summary>
    /// <param name="userName">身份名称；没有时为 null。</param>
    /// <returns>认证通过的结果。</returns>
    public static WebApiAuthenticationResult Success(string? userName = null) => new WebApiAuthenticationResult(true, userName, null);

    /// <summary>认证失败。</summary>
    /// <param name="message">失败原因（只写日志，不返回给调用方）；不能为 null。</param>
    /// <returns>认证失败的结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    public static WebApiAuthenticationResult Fail(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new WebApiAuthenticationResult(false, null, message);
    }
}
