namespace Junevy.Communication.WebApi;

/// <summary>
/// 内置认证的配置（P3-D4）。只保存<b>环境变量名</b>，凭据本身在构造客户端时从环境变量读取，不会出现在配置文件或日志里；
/// 需要在代码中注入凭据时使用 <c>WebApiAuthentication</c> 的静态工厂。
/// </summary>
public sealed class WebApiAuthOptions
{
    /// <summary>认证方式，默认 <see cref="WebApiAuthScheme.None"/>。</summary>
    public WebApiAuthScheme Scheme { get; set; }

    /// <summary>Basic：保存用户名的环境变量名。</summary>
    public string? UserNameVariable { get; set; }

    /// <summary>Basic：保存密码的环境变量名。</summary>
    public string? PasswordVariable { get; set; }

    /// <summary>Bearer：保存令牌的环境变量名。</summary>
    public string? TokenVariable { get; set; }

    /// <summary>ApiKey：携带密钥的请求头名，默认 <c>X-API-Key</c>。</summary>
    public string ApiKeyHeader { get; set; } = "X-API-Key";

    /// <summary>ApiKey：保存密钥的环境变量名。</summary>
    public string? ApiKeyVariable { get; set; }
}
