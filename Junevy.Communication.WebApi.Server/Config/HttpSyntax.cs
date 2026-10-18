namespace Junevy.Communication.WebApi.Server.Config;

/// <summary>
/// HTTP 语法的小工具（内部）：请求头名称（RFC 9110 的 token）与 JSON 媒体类型。
/// </summary>
internal static class HttpSyntax
{
    private const string TokenSymbols = "!#$%&'*+-.^_`|~";

    /// <summary>是否为合法的 HTTP token（可以用作请求头名称）。</summary>
    /// <param name="value">待检查的文本。</param>
    /// <returns>合法返回 true。</returns>
    public static bool IsToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && TokenSymbols.IndexOf(c) < 0)
                return false;
        }

        return true;
    }

    /// <summary>是否为 JSON 媒体类型：<c>application/json</c>，或 <c>*/*+json</c>（参数部分忽略）。</summary>
    /// <param name="contentType">内容类型，可以带参数，例如 <c>application/json; charset=utf-8</c>。</param>
    /// <returns>是 JSON 返回 true。</returns>
    public static bool IsJsonMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        string mediaType = contentType.Split(';')[0].Trim();
        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            return true;

        int slash = mediaType.IndexOf('/');
        return slash > 0 && mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
