namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// 配置里 URL 的共用解析（内部）：REST 的 BaseUrl、SOAP 的 Url 与 HealthCheckUrl 都要求"绝对的 http / https 地址"，规则只在这里写一次。
/// </summary>
internal static class HttpUrl
{
    /// <summary>
    /// 解析绝对的 http / https URL（前后空白被忽略）。
    /// </summary>
    /// <param name="text">配置里的文本。</param>
    /// <param name="uri">解析成功时的 URI。</param>
    /// <returns>是否是绝对的 http / https URL。</returns>
    public static bool TryParseAbsolute(string? text, out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(text)
            && Uri.TryCreate(text!.Trim(), UriKind.Absolute, out Uri? parsed)
            && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
        {
            uri = parsed;
            return true;
        }

        uri = null!;
        return false;
    }
}
