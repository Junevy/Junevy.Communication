using System.Text;

namespace Junevy.Communication.WebApi.Auth;

/// <summary>
/// 内置认证拦截器的静态工厂（P3-D4）：凭据由代码注入。把返回的拦截器加入 <see cref="WebApiComponents.Interceptors"/> 即可使用；
/// 需要登录换 token、签名等，请直接实现 <see cref="IWebApiInterceptor"/>。
/// 返回的拦截器会把自己写入的头标记为敏感，日志与事件参数中只显示 <c>***</c>。
/// </summary>
public static class WebApiAuthentication
{
    private const string AuthorizationHeader = "Authorization";

    /// <summary>
    /// HTTP Basic 认证（RFC 7617，用户名与密码按 UTF-8 编码）。
    /// </summary>
    /// <param name="userName">用户名；不能为空，也不能包含冒号。</param>
    /// <param name="password">密码；不能为空。</param>
    /// <returns>认证拦截器。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    /// <exception cref="ArgumentException">任一参数为空，或 <paramref name="userName"/> 包含冒号。</exception>
    public static IWebApiInterceptor Basic(string userName, string password)
    {
        RequireText(userName, nameof(userName));
        RequireText(password, nameof(password));
        if (userName.IndexOf(':') >= 0)
            throw new ArgumentException("The user name must not contain ':' (RFC 7617).", nameof(userName));

        string value = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userName + ":" + password));
        return Fixed(AuthorizationHeader, value);
    }

    /// <summary>
    /// HTTP Bearer 认证，令牌固定不变。
    /// </summary>
    /// <param name="token">令牌；不能为空。</param>
    /// <returns>认证拦截器。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="token"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="token"/> 为空。</exception>
    public static IWebApiInterceptor Bearer(string token)
    {
        RequireText(token, nameof(token));
        return Fixed(AuthorizationHeader, "Bearer " + token);
    }

    /// <summary>
    /// HTTP Bearer 认证，令牌由提供者在<b>每次发送尝试前</b>取得（重试与重放也会重新取），适合会过期的令牌。
    /// 提供者返回 null 或空串时，本次调用以 <c>Unspecified</c> 失败。
    /// </summary>
    /// <param name="tokenProvider">令牌提供者；不能为 null。</param>
    /// <returns>认证拦截器。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="tokenProvider"/> 为 null。</exception>
    public static IWebApiInterceptor Bearer(Func<CancellationToken, Task<string>> tokenProvider)
    {
        if (tokenProvider == null)
            throw new ArgumentNullException(nameof(tokenProvider));

        return new HeaderAuthInterceptor(AuthorizationHeader, async token =>
        {
            string value = await tokenProvider(token).ConfigureAwait(false);
            return string.IsNullOrEmpty(value) ? string.Empty : "Bearer " + value;
        });
    }

    /// <summary>
    /// 自定义请求头中的 API Key。
    /// </summary>
    /// <param name="headerName">携带密钥的请求头名；不能为空。</param>
    /// <param name="apiKey">密钥；不能为空。</param>
    /// <returns>认证拦截器。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    /// <exception cref="ArgumentException">任一参数为空。</exception>
    public static IWebApiInterceptor ApiKey(string headerName, string apiKey)
    {
        RequireText(headerName, nameof(headerName));
        RequireText(apiKey, nameof(apiKey));
        return Fixed(headerName, apiKey);
    }

    private static IWebApiInterceptor Fixed(string headerName, string value)
    {
        Task<string> completed = Task.FromResult(value);
        return new HeaderAuthInterceptor(headerName, _ => completed);
    }

    private static void RequireText(string value, string paramName)
    {
        if (value == null)
            throw new ArgumentNullException(paramName);
        if (value.Length == 0)
            throw new ArgumentException("The value must not be empty.", paramName);
    }
}
