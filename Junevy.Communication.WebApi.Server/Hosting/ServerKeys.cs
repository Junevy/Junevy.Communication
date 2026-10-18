namespace Junevy.Communication.WebApi.Server.Hosting;

/// <summary>
/// 中间件之间在 <c>HttpContext.Items</c> 中传递数据用的键（内部）。
/// </summary>
internal static class ServerKeys
{
    /// <summary>本请求的编号（<see cref="long"/>）。</summary>
    public const string RequestId = "Junevy.Communication.WebApi.Server.RequestId";

    /// <summary>管线产生的结果（<c>ServerResult</c>）。</summary>
    public const string Result = "Junevy.Communication.WebApi.Server.Result";

    /// <summary>并发限流器拒绝了本请求。</summary>
    public const string LimiterRejected = "Junevy.Communication.WebApi.Server.LimiterRejected";
}
