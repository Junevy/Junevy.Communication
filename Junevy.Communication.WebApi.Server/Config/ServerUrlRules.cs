using System.Text.RegularExpressions;

namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// 监听地址的校验（内部，P32-D13）：Kestrel 的写法，每项是 http 或 https 的绝对地址，必须写端口（0 至 65535），不允许重复。
/// </summary>
internal static class ServerUrlRules
{
    private const string UrlsField = "config.Urls";
    private const int MaxPort = 65535;

    // 主机可以是通配符（*、+）、IPv6 字面量（方括号）或普通主机名；路径只允许结尾的一个 "/"。
    private static readonly Regex AddressPattern = new Regex(
        @"^(?<scheme>https?)://(?<host>\[[0-9A-Fa-f:.]+\]|\*|\+|[A-Za-z0-9._-]+):(?<port>[0-9]{1,5})/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 校验监听地址并返回副本。
    /// </summary>
    /// <param name="urls">监听地址。</param>
    /// <returns>校验通过的地址副本。</returns>
    /// <exception cref="ArgumentException">地址为空、格式不对、端口越界或重复。</exception>
    public static string[] Require(string[]? urls)
    {
        if (urls == null || urls.Length == 0)
            throw new ArgumentException("config.Urls must contain at least one address.", UrlsField);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string url in urls)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("config.Urls must not contain an empty address.", UrlsField);

            Match match = AddressPattern.Match(url);
            if (!match.Success)
                throw new ArgumentException($"config.Urls address '{url}' must be an absolute http or https address with an explicit port, for example http://*:8080.", UrlsField);
            if (int.Parse(match.Groups["port"].Value, System.Globalization.CultureInfo.InvariantCulture) > MaxPort)
                throw new ArgumentException($"config.Urls address '{url}' has a port outside 0 to {MaxPort}.", UrlsField);
            if (!seen.Add(Normalize(url)))
                throw new ArgumentException($"config.Urls contains the address '{url}' more than once.", UrlsField);
        }

        return (string[])urls.Clone();
    }

    /// <summary>是否含有 https 地址。</summary>
    /// <param name="urls">已通过校验的地址。</param>
    /// <returns>含有 https 地址返回 true。</returns>
    public static bool HasHttps(IEnumerable<string> urls)
        => urls.Any(url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    // 重复判断忽略大小写与结尾的 "/"：http://*:8080 与 HTTP://*:8080/ 是同一个地址。
    private static string Normalize(string url) => url.TrimEnd('/').ToLowerInvariant();
}
