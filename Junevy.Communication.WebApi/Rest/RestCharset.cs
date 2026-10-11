using System.Text;

namespace Junevy.Communication.WebApi.Rest;

/// <summary>
/// 从内容类型的 <c>charset</c> 参数解析字符串编码（内部）：请求的字符串正文与响应的字符串结果共用，缺省 UTF-8。
/// </summary>
internal static class RestCharset
{
    private const string CharsetPrefix = "charset=";

    /// <summary>
    /// 解析编码。net8.0 默认不带 GBK 等代码页编码，宿主需要时自行注册 <c>CodePagesEncodingProvider</c>；这里不修改任何全局设置。
    /// </summary>
    /// <param name="contentType">内容类型，例如 <c>text/plain; charset=gbk</c>；可为 null。</param>
    /// <param name="encoding">解析出的编码；没有 charset 参数时为 UTF-8（无 BOM）。</param>
    /// <param name="charset">内容类型里写的 charset 名称；没有时为 null。</param>
    /// <returns>编码是否可用。</returns>
    public static bool TryResolve(string? contentType, out Encoding encoding, out string? charset)
    {
        encoding = new UTF8Encoding(false);
        charset = FindCharset(contentType);
        if (charset == null)
            return true;

        try
        {
            encoding = Encoding.GetEncoding(charset);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string? FindCharset(string? contentType)
    {
        if (contentType == null)
            return null;

        foreach (string part in contentType.Split(';').Skip(1))
        {
            string parameter = part.Trim();
            if (parameter.StartsWith(CharsetPrefix, StringComparison.OrdinalIgnoreCase))
                return parameter.Substring(CharsetPrefix.Length).Trim('"', ' ');
        }

        return null;
    }
}
