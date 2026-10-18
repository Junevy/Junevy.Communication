namespace Junevy.Communication.WebApi.Server.Dedup;

/// <summary>
/// 去重键中的请求目标（内部，P32-D11）：路径加上按名称排序的查询参数。查询参数不同的请求是不同的命令，不能互相重放。
/// </summary>
internal static class DedupTarget
{
    /// <summary>生成请求目标。</summary>
    /// <param name="path">请求路径。</param>
    /// <param name="query">查询参数（同名取第一个）。</param>
    /// <returns>没有查询参数时为路径本身；否则为 <c>path?a=1&amp;b=2</c>（参数按名称的序数顺序排列）。</returns>
    public static string Of(string path, IReadOnlyDictionary<string, string> query)
        => query.Count == 0
            ? path
            : path + "?" + string.Join("&", query.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
}
