using System.Security.Cryptography;
using System.Text;
using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Auth;

/// <summary>
/// 内置的凭据校验（内部，P32-D9）：Basic、Bearer、ApiKey。期望的凭据来自配置快照，比较使用 <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>。
/// 失败的原因只进入 <see cref="WebApiAuthenticationResult.FailureMessage"/>（用于日志），响应体不回显任何期望值。
/// </summary>
internal sealed class CredentialAuthenticator : IWebApiRequestAuthenticator
{
    private const string AuthorizationHeader = "Authorization";
    private const string BasicScheme = "Basic";
    private const string BearerScheme = "Bearer";

    private readonly ServerCredentials credentials;

    /// <summary>创建校验器。</summary>
    /// <param name="credentials">已读取的期望凭据。</param>
    public CredentialAuthenticator(ServerCredentials credentials)
    {
        this.credentials = credentials;
    }

    /// <inheritdoc />
    public Task<WebApiAuthenticationResult> AuthenticateAsync(WebApiServerContext context, CancellationToken cancellationToken)
    {
        WebApiAuthenticationResult result = credentials.Scheme switch
        {
            WebApiAuthScheme.Basic => CheckBasic(context),
            WebApiAuthScheme.Bearer => CheckBearer(context),
            WebApiAuthScheme.ApiKey => CheckApiKey(context),
            _ => WebApiAuthenticationResult.Fail("No authentication scheme is configured."),
        };
        return Task.FromResult(result);
    }

    private WebApiAuthenticationResult CheckBasic(WebApiServerContext context)
    {
        if (!TryGetAuthorization(context, BasicScheme, out string parameter))
            return WebApiAuthenticationResult.Fail("The Authorization header is missing or is not Basic.");

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
        }
        catch (FormatException)
        {
            return WebApiAuthenticationResult.Fail("The Basic credentials are not valid Base64.");
        }

        int separator = decoded.IndexOf(':');
        if (separator < 0)
            return WebApiAuthenticationResult.Fail("The Basic credentials have no ':' separator.");

        // 两个比较都执行，不因第一个失败而短路，避免用时差判断哪一项错误。
        bool userMatches = SameText(decoded[..separator], credentials.UserName!);
        bool passwordMatches = SameText(decoded[(separator + 1)..], credentials.Password!);
        return userMatches & passwordMatches
            ? WebApiAuthenticationResult.Success(credentials.UserName)
            : WebApiAuthenticationResult.Fail("The Basic credentials do not match.");
    }

    private WebApiAuthenticationResult CheckBearer(WebApiServerContext context)
    {
        if (!TryGetAuthorization(context, BearerScheme, out string token))
            return WebApiAuthenticationResult.Fail("The Authorization header is missing or is not Bearer.");

        return SameText(token, credentials.Token!)
            ? WebApiAuthenticationResult.Success()
            : WebApiAuthenticationResult.Fail("The Bearer token does not match.");
    }

    private WebApiAuthenticationResult CheckApiKey(WebApiServerContext context)
    {
        if (!context.Headers.TryGetValue(credentials.ApiKeyHeader, out string? presented))
            return WebApiAuthenticationResult.Fail("The API key header is missing.");

        return SameText(presented, credentials.ApiKey!)
            ? WebApiAuthenticationResult.Success()
            : WebApiAuthenticationResult.Fail("The API key does not match.");
    }

    // 方案名不区分大小写（RFC 7235 第 2.1 节）；参数为方案名之后的第一个空格之后的部分。
    private static bool TryGetAuthorization(WebApiServerContext context, string scheme, out string parameter)
    {
        parameter = string.Empty;
        if (!context.Headers.TryGetValue(AuthorizationHeader, out string? value))
            return false;

        int space = value.IndexOf(' ');
        if (space < 0 || !value[..space].Equals(scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        parameter = value[(space + 1)..].Trim();
        return true;
    }

    private static bool SameText(string presented, string expected)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
}
