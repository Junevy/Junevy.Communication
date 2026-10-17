using Junevy.Communication.WebApi;

namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// 内置认证配置的校验与凭据读取（内部，P32-D9）。配置只保存环境变量名，凭据在创建服务器时读取，不出现在配置文件或日志里。
/// 变量名缺失、变量不存在或为空时抛出 <see cref="ArgumentException"/>，消息只含变量名与字段名，绝不含凭据值。
/// </summary>
internal static class ServerCredentialRules
{
    private const string Section = "config.Auth";

    /// <summary>
    /// 按认证方式校验配置并读取凭据。
    /// </summary>
    /// <param name="auth">认证配置。</param>
    /// <returns>凭据快照；认证方式为 None 时为 <see cref="ServerCredentials.None"/>。</returns>
    /// <exception cref="ArgumentException">配置为 null，缺少变量名，变量不存在或为空，或请求头名称非法。</exception>
    public static ServerCredentials Create(WebApiAuthOptions? auth)
    {
        if (auth == null)
            throw new ArgumentException("config.Auth must not be null.", Section);

        switch (auth.Scheme)
        {
            case WebApiAuthScheme.None:
                return ServerCredentials.None;
            case WebApiAuthScheme.Basic:
                return new ServerCredentials(
                    WebApiAuthScheme.Basic,
                    ReadVariable(auth.UserNameVariable, Section + "." + nameof(WebApiAuthOptions.UserNameVariable)),
                    ReadVariable(auth.PasswordVariable, Section + "." + nameof(WebApiAuthOptions.PasswordVariable)),
                    null,
                    auth.ApiKeyHeader,
                    null);
            case WebApiAuthScheme.Bearer:
                return new ServerCredentials(
                    WebApiAuthScheme.Bearer,
                    null,
                    null,
                    ReadVariable(auth.TokenVariable, Section + "." + nameof(WebApiAuthOptions.TokenVariable)),
                    auth.ApiKeyHeader,
                    null);
            case WebApiAuthScheme.ApiKey:
                RequireHeaderName(auth.ApiKeyHeader);
                return new ServerCredentials(
                    WebApiAuthScheme.ApiKey,
                    null,
                    null,
                    null,
                    auth.ApiKeyHeader,
                    ReadVariable(auth.ApiKeyVariable, Section + "." + nameof(WebApiAuthOptions.ApiKeyVariable)));
            default:
                throw new ArgumentException($"config.Auth.Scheme '{auth.Scheme}' is not supported.", Section + ".Scheme");
        }
    }

    /// <summary>
    /// 读取环境变量的值。变量名为空、变量不存在或为空时抛出异常，消息只含变量名与字段名。
    /// </summary>
    /// <param name="variableName">环境变量名。</param>
    /// <param name="field">配置字段的完整名称，用于错误消息，例如 <c>config.Auth.TokenVariable</c>。</param>
    /// <returns>变量的值（非空）。</returns>
    /// <exception cref="ArgumentException">变量名为空，或变量不存在或为空。</exception>
    public static string ReadVariable(string? variableName, string field)
    {
        if (string.IsNullOrEmpty(variableName))
            throw new ArgumentException($"{field} must name an environment variable.", field);

        string? value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(value))
            throw new ArgumentException($"The environment variable '{variableName}' named by {field} is not set or is empty.", field);

        return value;
    }

    private static void RequireHeaderName(string headerName)
    {
        if (!HttpSyntax.IsToken(headerName))
        {
            string field = Section + "." + nameof(WebApiAuthOptions.ApiKeyHeader);
            throw new ArgumentException($"{field} must be a valid HTTP header name, not '{headerName}'.", field);
        }
    }
}
