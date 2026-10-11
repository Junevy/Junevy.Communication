namespace Junevy.Communication.WebApi.Http;

/// <summary>
/// URI 的"来源"（scheme、主机、端口）比较（内部，P3-D17）：拦截器不能把请求改到别的主机，SOAP 的健康检查地址也必须与服务地址同源，
/// 两处都因为认证头会随请求发出，不能被带到别的主机。规则只在这里写一次。
/// </summary>
internal static class UriOrigin
{
    /// <summary>
    /// 两个 URI 是否同源。
    /// </summary>
    /// <param name="original">基准 URI（绝对）。</param>
    /// <param name="candidate">待比较的 URI；不是绝对 URI 时视为不同源。</param>
    /// <returns>scheme、主机与端口都相同（主机与 scheme 不区分大小写）时为 true。</returns>
    public static bool AreSame(Uri original, Uri candidate)
        => candidate.IsAbsoluteUri
           && Uri.Compare(original, candidate, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;
}
