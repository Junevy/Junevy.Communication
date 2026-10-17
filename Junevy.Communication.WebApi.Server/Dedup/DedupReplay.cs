using Junevy.Communication.WebApi.Server.Formatting;
using Junevy.Communication.WebApi.Server.Pipeline;

namespace Junevy.Communication.WebApi.Server.Dedup;

/// <summary>
/// 重放缓存的结果（内部，P32-D11）：状态码、内容类型与正文与第一次相同，只加重放标记头；处理器没有执行。
/// </summary>
internal static class DedupReplay
{
    /// <summary>重放标记的响应头名称。</summary>
    public const string HeaderName = "Idempotent-Replayed";

    /// <summary>由缓存的结果构造重放的结果。</summary>
    /// <param name="cached">第一次的结果（必须有响应）。</param>
    /// <returns>重放的结果。</returns>
    public static ServerResult Create(ServerResult cached)
    {
        var headers = new Dictionary<string, string>(cached.Response!.Headers, StringComparer.OrdinalIgnoreCase)
        {
            [HeaderName] = "true",
        };
        var response = new ServerResponse(cached.StatusCode, cached.Response.ContentType, cached.Response.Body, headers);
        return new ServerResult(cached.StatusCode, response, WebApiServerOutcome.Replayed, cached.Code, cached.Message);
    }
}
