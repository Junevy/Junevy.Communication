using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Auth;

/// <summary>
/// 服务器使用的认证：校验器与 <c>WWW-Authenticate</c> 的挑战（内部）。挑战只对内置方案给出；自定义认证器没有挑战。
/// </summary>
/// <param name="Authenticator">校验器。</param>
/// <param name="Challenge">认证失败时写入 <c>WWW-Authenticate</c> 的值；没有时为 null。</param>
internal sealed record ServerAuthenticator(IWebApiRequestAuthenticator Authenticator, string? Challenge)
{
    /// <summary>
    /// 按配置决定使用哪种认证：自定义认证器优先；认证方式为 None 时不认证（返回 null）。
    /// </summary>
    /// <param name="settings">已校验的配置。</param>
    /// <param name="serverName">服务器名称，用于 Basic 的 realm。</param>
    /// <returns>认证；不认证时为 null。</returns>
    public static ServerAuthenticator? Create(WebApiServerSettings settings, string serverName)
    {
        if (settings.CustomAuthenticator != null)
            return new ServerAuthenticator(settings.CustomAuthenticator, null);

        ServerCredentials credentials = settings.Credentials;
        return credentials.Scheme switch
        {
            WebApiAuthScheme.Basic => new ServerAuthenticator(new CredentialAuthenticator(credentials), $"Basic realm=\"{serverName}\""),
            WebApiAuthScheme.Bearer => new ServerAuthenticator(new CredentialAuthenticator(credentials), "Bearer"),
            WebApiAuthScheme.ApiKey => new ServerAuthenticator(new CredentialAuthenticator(credentials), null),
            _ => null,
        };
    }
}
