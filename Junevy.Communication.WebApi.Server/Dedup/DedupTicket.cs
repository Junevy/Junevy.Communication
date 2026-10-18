using Junevy.Communication.WebApi.Server.Pipeline;

namespace Junevy.Communication.WebApi.Server.Dedup;

/// <summary>
/// 去重登记的凭据（内部）：处理器真正结束时调用 <see cref="Complete"/> 缓存结果；异常或不缓存的结果调用 <see cref="Abandon"/> 撤销登记。
/// </summary>
internal sealed class DedupTicket
{
    private readonly DeduplicationCache cache;
    private readonly DeduplicationCache.DedupKey identity;
    private readonly DeduplicationCache.DedupEntry entry;

    /// <summary>创建凭据（由缓存创建）。</summary>
    /// <param name="cache">所属的缓存。</param>
    /// <param name="identity">条目的身份。</param>
    /// <param name="entry">条目。</param>
    internal DedupTicket(DeduplicationCache cache, DeduplicationCache.DedupKey identity, DeduplicationCache.DedupEntry entry)
    {
        this.cache = cache;
        this.identity = identity;
        this.entry = entry;
    }

    /// <summary>缓存处理器的最终结果，之后同键请求在窗口内重放它。</summary>
    /// <param name="result">处理器的结果（成功或业务失败）。</param>
    public void Complete(ServerResult result) => cache.Complete(identity, entry, result);

    /// <summary>撤销登记：之后同键请求会重新执行处理器。</summary>
    public void Abandon() => cache.Abandon(identity, entry);
}
