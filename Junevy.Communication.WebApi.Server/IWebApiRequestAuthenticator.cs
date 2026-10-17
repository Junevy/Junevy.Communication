namespace Junevy.Communication.WebApi.Server;

/// <summary>
/// 自定义请求认证器（P32-D9）：设置在 <see cref="WebApiServerComponents.Authenticator"/> 上后取代内置的认证配置。
/// 认证发生在读取正文之后、拦截器之前；实现必须线程安全。
/// </summary>
public interface IWebApiRequestAuthenticator
{
    /// <summary>认证一次请求。</summary>
    /// <param name="context">请求上下文（正文已经读取）。</param>
    /// <param name="cancellationToken">请求的取消令牌。</param>
    /// <returns>认证结果；认证器抛出异常时该请求返回 500。</returns>
    Task<WebApiAuthenticationResult> AuthenticateAsync(WebApiServerContext context, CancellationToken cancellationToken);
}
