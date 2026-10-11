namespace Junevy.Communication.WebApi.Auth;

/// <summary>
/// 由配置创建内置认证拦截器（内部，P3-D4）：配置只保存环境变量名，凭据在构造客户端时读取。
/// 变量不存在或为空时抛出 <see cref="ArgumentException"/>，消息只包含<b>变量名</b>，绝不包含任何凭据值。
/// 配置的结构（需要哪些变量名）已由 <c>EndpointConfigRules</c> 校验。
/// </summary>
internal static class AuthInterceptorFactory
{
    private const string AuthSection = nameof(WebApiEndpointConfig.Auth);

    /// <summary>
    /// 按配置创建认证拦截器。
    /// </summary>
    /// <param name="options">认证配置（已通过结构校验）。</param>
    /// <returns>认证拦截器；<see cref="WebApiAuthScheme.None"/> 时为 null。</returns>
    /// <exception cref="ArgumentException">配置引用的环境变量不存在或为空。</exception>
    public static IWebApiInterceptor? FromOptions(WebApiAuthOptions options)
    {
        switch (options.Scheme)
        {
            case WebApiAuthScheme.Basic:
                return WebApiAuthentication.Basic(
                    ReadVariable(options.UserNameVariable, nameof(WebApiAuthOptions.UserNameVariable)),
                    ReadVariable(options.PasswordVariable, nameof(WebApiAuthOptions.PasswordVariable)));
            case WebApiAuthScheme.Bearer:
                return WebApiAuthentication.Bearer(ReadVariable(options.TokenVariable, nameof(WebApiAuthOptions.TokenVariable)));
            case WebApiAuthScheme.ApiKey:
                return WebApiAuthentication.ApiKey(options.ApiKeyHeader, ReadVariable(options.ApiKeyVariable, nameof(WebApiAuthOptions.ApiKeyVariable)));
            default:
                return null;
        }
    }

    private static string ReadVariable(string? variableName, string property)
    {
        string? value = Environment.GetEnvironmentVariable(variableName!);
        if (string.IsNullOrEmpty(value))
        {
            string field = AuthSection + "." + property;
            throw new ArgumentException($"The environment variable '{variableName}' named by {field} is not set or is empty.", field);
        }

        return value!;
    }
}
