namespace Junevy.Communication.WebApi;

/// <summary>
/// 内置认证方式（P3-D4）。认证以拦截器实现；登录换 token、签名等需求由宿主编写自己的拦截器。
/// </summary>
public enum WebApiAuthScheme
{
    /// <summary>不认证。</summary>
    None = 0,

    /// <summary>HTTP Basic（RFC 7617，用户名与密码按 UTF-8 编码）。</summary>
    Basic = 1,

    /// <summary>HTTP Bearer 令牌。</summary>
    Bearer = 2,

    /// <summary>自定义请求头中的 API Key。</summary>
    ApiKey = 3,
}
