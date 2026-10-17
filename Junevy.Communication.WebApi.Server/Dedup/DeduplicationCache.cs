using System.Security.Cryptography;
using Junevy.Communication.WebApi.Server.Pipeline;

namespace Junevy.Communication.WebApi.Server.Dedup;

/// <summary>
/// 重复下发的去重缓存（内部，P32-D11）。键为（幂等键、方法、路径）。已完成的条目在窗口内重放第一次的结果；
/// 处理中的条目保持到处理器真正结束，超时之后也不提前释放。条目超过上限时淘汰最旧的已完成条目，处理中的条目永不淘汰。
/// 时间源由调用方注入（测试可以推进时间）。线程安全。
/// </summary>
internal sealed class DeduplicationCache
{
    private readonly TimeSpan window;
    private readonly int maxEntries;
    private readonly TimeProvider timeProvider;
    private readonly Dictionary<DedupKey, DedupEntry> entries = new Dictionary<DedupKey, DedupEntry>();
    private readonly object gate = new object();
    private long sequence;

    /// <summary>创建缓存。</summary>
    /// <param name="windowMilliseconds">去重窗口（毫秒）。</param>
    /// <param name="maxEntries">条目上限。</param>
    /// <param name="timeProvider">时间源。</param>
    public DeduplicationCache(int windowMilliseconds, int maxEntries, TimeProvider timeProvider)
    {
        window = TimeSpan.FromMilliseconds(windowMilliseconds);
        this.maxEntries = maxEntries;
        this.timeProvider = timeProvider;
    }

    /// <summary>当前条目数（含处理中与窗口内已完成的）。</summary>
    public int EntryCount
    {
        get
        {
            lock (gate)
                return entries.Count;
        }
    }

    /// <summary>处理中的条目数。</summary>
    public int InFlightCount
    {
        get
        {
            lock (gate)
                return entries.Values.Count(entry => entry.Result == null);
        }
    }

    /// <summary>
    /// 查询或登记一次请求。同键正文不同时返回 <see cref="DedupState.BodyMismatch"/>；同键仍在处理时返回 <see cref="DedupState.InProgress"/>；
    /// 同键已完成且在窗口内时返回 <see cref="DedupState.Replay"/>；否则登记为处理中并返回 <see cref="DedupState.Proceed"/>。
    /// </summary>
    /// <param name="key">幂等键。</param>
    /// <param name="method">HTTP 方法。</param>
    /// <param name="path">请求路径。</param>
    /// <param name="body">请求正文（用于比较）。</param>
    /// <returns>判定结果。</returns>
    public DedupDecision Begin(string key, string method, string path, byte[] body)
    {
        var identity = new DedupKey(key, method, path);
        byte[] bodyHash = SHA256.HashData(body);
        lock (gate)
        {
            PurgeExpired();
            if (entries.TryGetValue(identity, out DedupEntry? existing))
            {
                if (!CryptographicOperations.FixedTimeEquals(existing.BodyHash, bodyHash))
                    return DedupDecision.Of(DedupState.BodyMismatch);
                if (existing.Result == null)
                    return DedupDecision.Of(DedupState.InProgress);

                return DedupDecision.Replay(existing.Result);
            }

            if (entries.Count >= maxEntries && !EvictOldestCompleted())
                return DedupDecision.Of(DedupState.Full);

            var entry = new DedupEntry(bodyHash, ++sequence);
            entries[identity] = entry;
            return DedupDecision.Proceed(new DedupTicket(this, identity, entry));
        }
    }

    internal void Complete(DedupKey identity, DedupEntry entry, ServerResult result)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(identity, out DedupEntry? current) || !ReferenceEquals(current, entry))
                return;

            entry.Result = result;
            entry.CompletedAt = timeProvider.GetUtcNow();
        }
    }

    internal void Abandon(DedupKey identity, DedupEntry entry)
    {
        lock (gate)
        {
            if (entries.TryGetValue(identity, out DedupEntry? current) && ReferenceEquals(current, entry))
                entries.Remove(identity);
        }
    }

    // 窗口之外的已完成条目不再重放：查询前清理，让过期的键可以重新执行。
    private void PurgeExpired()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (KeyValuePair<DedupKey, DedupEntry> pair in entries.ToArray())
        {
            if (pair.Value.Result != null && pair.Value.CompletedAt + window <= now)
                entries.Remove(pair.Key);
        }
    }

    private bool EvictOldestCompleted()
    {
        KeyValuePair<DedupKey, DedupEntry>? oldest = null;
        foreach (KeyValuePair<DedupKey, DedupEntry> pair in entries)
        {
            if (pair.Value.Result != null && (oldest == null || pair.Value.Sequence < oldest.Value.Value.Sequence))
                oldest = pair;
        }

        if (oldest == null)
            return false;

        entries.Remove(oldest.Value.Key);
        return true;
    }

    /// <summary>条目的身份：幂等键、方法与路径。</summary>
    /// <param name="Key">幂等键。</param>
    /// <param name="Method">HTTP 方法。</param>
    /// <param name="Path">请求路径。</param>
    internal readonly record struct DedupKey(string Key, string Method, string Path);

    /// <summary>条目：正文哈希、插入顺序、完成后的结果与完成时间。</summary>
    internal sealed class DedupEntry
    {
        /// <summary>创建条目。</summary>
        /// <param name="bodyHash">请求正文的 SHA-256。</param>
        /// <param name="sequence">插入顺序。</param>
        public DedupEntry(byte[] bodyHash, long sequence)
        {
            BodyHash = bodyHash;
            Sequence = sequence;
        }

        /// <summary>请求正文的 SHA-256。</summary>
        public byte[] BodyHash { get; }

        /// <summary>插入顺序（用于淘汰最旧的条目）。</summary>
        public long Sequence { get; }

        /// <summary>完成后的结果；处理中时为 null。</summary>
        public ServerResult? Result { get; set; }

        /// <summary>完成的时间（窗口从这里开始计算）。</summary>
        public DateTimeOffset CompletedAt { get; set; }
    }
}
