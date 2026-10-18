using Junevy.Communication.WebApi;

namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// 内置认证使用的凭据（内部）：构造服务器时从环境变量读取。类型不重写 <c>ToString</c>，避免凭据被意外输出。
/// </summary>
internal sealed class ServerCredentials
{
    /// <summary>不认证时的凭据。</summary>
    public static readonly ServerCredentials None = new ServerCredentials(WebApiAuthScheme.None, null, null, null, "X-API-Key", null);

    /// <summary>创建凭据快照。</summary>
    /// <param name="scheme">认证方式。</param>
    /// <param name="userName">Basic 的用户名。</param>
    /// <param name="password">Basic 的密码。</param>
    /// <param name="token">Bearer 的令牌。</param>
    /// <param name="apiKeyHeader">ApiKey 的请求头名称。</param>
    /// <param name="apiKey">ApiKey 的密钥。</param>
    public ServerCredentials(WebApiAuthScheme scheme, string? userName, string? password, string? token, string apiKeyHeader, string? apiKey)
    {
        Scheme = scheme;
        UserName = userName;
        Password = password;
        Token = token;
        ApiKeyHeader = apiKeyHeader;
        ApiKey = apiKey;
    }

    /// <summary>认证方式。</summary>
    public WebApiAuthScheme Scheme { get; }

    /// <summary>Basic 的用户名。</summary>
    public string? UserName { get; }

    /// <summary>Basic 的密码。</summary>
    public string? Password { get; }

    /// <summary>Bearer 的令牌。</summary>
    public string? Token { get; }

    /// <summary>ApiKey 的请求头名称。</summary>
    public string ApiKeyHeader { get; }

    /// <summary>ApiKey 的密钥。</summary>
    public string? ApiKey { get; }
}
