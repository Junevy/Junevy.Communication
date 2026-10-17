namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// 监听范围的判定（内部，P32-D9）：监听所有网卡（主机为 <c>*</c>、<c>+</c>、<c>0.0.0.0</c> 或 <c>[::]</c>）却没有配置认证时，启动时需要记一条 Warning。
/// 本类只做判定，不写日志；日志由启动流程负责。
/// </summary>
internal static class ServerExposureRules
{
    private const string SchemeSeparator = "://";

    /// <summary>
    /// 是否存在监听所有网卡的地址，并且没有配置任何认证。
    /// </summary>
    /// <param name="urls">监听地址（已通过校验）。</param>
    /// <param name="authenticated">是否配置了认证（内置方式或自定义认证器）。</param>
    /// <returns>需要警告返回 true。</returns>
    public static bool IsUnprotectedWildcard(IEnumerable<string> urls, bool authenticated)
    {
        if (authenticated)
            return false;

        return urls.Any(IsWildcardHost);
    }

    private static bool IsWildcardHost(string url)
    {
        int schemeEnd = url.IndexOf(SchemeSeparator, StringComparison.Ordinal);
        if (schemeEnd < 0)
            return false;

        string authority = url[(schemeEnd + SchemeSeparator.Length)..];
        int portSeparator = authority.LastIndexOf(':');
        string host = portSeparator < 0 ? authority : authority[..portSeparator];
        return host is "*" or "+" or "0.0.0.0" or "[::]";
    }
}
