using System.Diagnostics;
using System.Net;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 请求-应答关联表（设计文档 5.3 节，计划 7.2 节的规则 1–6、9）。
/// 维护在途请求（<see cref="CorrelationMode"/> 决定它们如何认领帧）、接收等待者（FIFO）、迟到应答窗口与握手积压（D8）。
/// 所有结构变更在内部锁下完成；完成等待者（<see cref="PendingRequest.TryFinish"/>）一律在锁之外进行。
/// </summary>
internal sealed class PendingRequestTable
{
    /// <summary>握手积压上限（D8）。</summary>
    public const int MaxHandshakeBacklog = 64;

    /// <summary>Keyed 模式近期超时键的上限（D9）。</summary>
    public const int MaxLateKeys = 256;

    private readonly object gate = new object();
    private readonly CorrelationMode mode;
    private readonly IFrameKeyExtractor? keyExtractor;
    private readonly int lateReplyWindow;
    private readonly ILogger logger;

    // Sequential / Matcher：在途请求，按注册顺序排列。
    private readonly List<PendingRequest> inFlight = new List<PendingRequest>();

    // Keyed：在途请求，按关联键索引。
    private readonly Dictionary<long, PendingRequest> keyed = new Dictionary<long, PendingRequest>();

    // ReceiveAsync 等待者，FIFO。
    private readonly List<PendingRequest> receivers = new List<PendingRequest>();

    // 握手期间未被认领的帧（D8）。
    private readonly List<(byte[] Frame, EndPoint? Remote)> backlog = new List<(byte[] Frame, EndPoint? Remote)>();

    // Keyed：近期超时键 → 过期时间戳（Stopwatch 刻度）。
    private readonly Dictionary<long, long> lateKeys = new Dictionary<long, long>();

    private bool handshaking;

    // Sequential：迟到应答窗口的截止时间戳（Stopwatch 刻度）。
    private long sequentialLateUntil;

    /// <summary>
    /// 创建关联表。
    /// </summary>
    /// <param name="mode">关联模式。</param>
    /// <param name="keyExtractor">关联键提取器；<see cref="CorrelationMode.Keyed"/> 模式必须提供。</param>
    /// <param name="lateReplyWindow">迟到应答窗口（毫秒）；-1 表示等于各请求自身的超时。</param>
    /// <param name="logger">日志记录器。</param>
    /// <exception cref="ArgumentException">Keyed 模式缺少键提取器，或模式未定义。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lateReplyWindow"/> 小于 -1。</exception>
    public PendingRequestTable(CorrelationMode mode, IFrameKeyExtractor? keyExtractor, int lateReplyWindow, ILogger logger)
    {
        if (!Enum.IsDefined(typeof(CorrelationMode), mode))
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown correlation mode.");
        if (mode == CorrelationMode.Keyed && keyExtractor == null)
            throw new ArgumentException("Keyed correlation requires a key extractor.", nameof(keyExtractor));
        if (lateReplyWindow < -1)
            throw new ArgumentOutOfRangeException(nameof(lateReplyWindow), lateReplyWindow, "The late reply window must be -1 or a non-negative value.");
        if (logger == null)
            throw new ArgumentNullException(nameof(logger));

        this.mode = mode;
        this.keyExtractor = keyExtractor;
        this.lateReplyWindow = lateReplyWindow;
        this.logger = logger;
    }

    /// <summary>
    /// 注册等待者。<paramref name="payload"/> 为空表示 <c>ReceiveAsync</c>：立即开始计时，并先扫描握手积压。
    /// Keyed 模式提取不到请求键、或该键已在途时，返回的等待者立即以 <see cref="CommErrorKind.InvalidRequest"/> 完成（不加入表中）。
    /// Sequential 模式下同一时刻只允许一个请求型等待者，违反时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <param name="payload">请求负载；为空表示接收等待者。</param>
    /// <param name="matcher">应答判定器；为 null 时认领任意帧。Keyed 模式的请求忽略此项（按关联键匹配）。</param>
    /// <param name="timeout">超时毫秒数；≤ 0 表示不限时。</param>
    /// <param name="expectedRemote">UDP 非定向模式的期望来源；为 null 时不校验。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>等待者；调用方负责 <see cref="PendingRequest.Dispose"/>。</returns>
    public PendingRequest Register(ReadOnlyMemory<byte> payload, IResponseMatcher? matcher, int timeout,
                                   EndPoint? expectedRemote, CancellationToken cancellationToken)
    {
        bool isReceive = payload.IsEmpty;
        // Keyed 请求按关联键匹配，忽略 Matcher（RequestOptions.Matcher 的约定）；接收等待者没有键，在所有模式下都使用 Matcher。
        IResponseMatcher? effectiveMatcher = mode == CorrelationMode.Keyed && !isReceive ? null : matcher;
        var waiter = new PendingRequest(this, payload, effectiveMatcher, timeout,
                                        ResolveLateWindow(timeout), expectedRemote, isReceive);

        if (cancellationToken.IsCancellationRequested)
        {
            waiter.TryFinish(Cancelled());
            return waiter;
        }

        CommResult<byte[]>? rejected = null;
        byte[]? recovered = null;
        lock (gate)
        {
            if (isReceive)
            {
                int index = FindBacklogIndex(waiter);
                if (index >= 0)
                {
                    recovered = backlog[index].Frame;
                    backlog.RemoveAt(index);
                }
                else
                {
                    receivers.Add(waiter);
                }
            }
            else if (mode == CorrelationMode.Keyed)
            {
                if (!keyExtractor!.TryGetRequestKey(payload.Span, out long key))
                {
                    rejected = CommResult<byte[]>.Fail("The request does not contain a correlation key.", CommErrorKind.InvalidRequest);
                }
                else if (keyed.ContainsKey(key))
                {
                    rejected = CommResult<byte[]>.Fail($"A request with correlation key {key} is already in flight.", CommErrorKind.InvalidRequest);
                }
                else
                {
                    keyed.Add(key, waiter);
                    waiter.SetKey(key);
                }
            }
            else
            {
                if (mode == CorrelationMode.Sequential && inFlight.Count > 0)
                    throw new InvalidOperationException("Sequential correlation allows only one in-flight request; the caller must hold the request lock.");

                inFlight.Add(waiter);
            }
        }

        // 以下步骤都在结构锁之外：完成回调与取消注册可能同步执行。
        if (rejected != null)
        {
            waiter.TryFinish(rejected);
            return waiter;
        }

        if (recovered != null)
        {
            waiter.TryFinish(CommResult<byte[]>.Success(recovered));
            return waiter;
        }

        if (isReceive)
            waiter.StartTimer();

        waiter.ArmCancellation(cancellationToken);
        return waiter;
    }

    /// <summary>
    /// 由解析循环调用，判定一个入站帧的去向（计划 7.2 规则 1，经修订）：
    /// ① 在途请求认领；② 迟到应答判定（Sequential 窗口内未匹配的帧，或 Keyed 命中近期超时键）；③ 接收等待者（FIFO）；
    /// ④ 都没认领则进入握手积压（握手期间）或交给调用方派发。
    /// 迟到应答先于接收等待者判定，因此无 Matcher 的 ReceiveAsync 等待者不会拿走超时请求的迟到应答。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 四步在<strong>同一把锁下</strong>完成，缺一不可：接收等待者的注册（<see cref="Register"/>）也取同一把锁，
    /// 因此这一帧要么被当时已在等的等待者认领，要么进积压等后来注册的等待者来取。
    /// 分两步判定（先认领、再决定去向）会留下一个窗口：等待者恰好注册在这两步之间时，
    /// 它看到空的积压而把自己挂进等待者列表，随后这一帧才被放进积压——双方互不可见，
    /// 帧一直躺到握手结束。握手期逐帧消费的协议（例如 SECS 被动端等 Select.req）会丢掉整条消息。
    /// </para>
    /// <para>
    /// 认领在调用线程上同步完成，不经过派发队列；<paramref name="claimed"/> 的完成由调用方在锁外触发。
    /// </para>
    /// </remarks>
    /// <param name="frame">入站帧。</param>
    /// <param name="remote">来源地址；无来源概念的传输为 null。</param>
    /// <param name="claimed">被认领的等待者；<see cref="FrameRoute.Claimed"/> 时非 null，由调用方完成它。</param>
    /// <returns>
    /// <see cref="FrameRoute.Claimed"/>：已被在途请求或接收等待者认领；
    /// <see cref="FrameRoute.LateReply"/>：迟到应答，调用方丢弃并计入 FramesDropped；
    /// <see cref="FrameRoute.Backlog"/>：进入握手积压；
    /// <see cref="FrameRoute.BacklogFull"/>：握手积压已满（D8），调用方应断开；
    /// <see cref="FrameRoute.Dispatch"/>：交给调用方派发。
    /// </returns>
    internal FrameRoute Route(byte[] frame, EndPoint? remote, out PendingRequest? claimed)
    {
        lock (gate)
        {
            claimed = TakeRequest(frame, remote);
            if (claimed == null)
            {
                if (IsLateUnclaimed(frame, Stopwatch.GetTimestamp()))
                    return FrameRoute.LateReply;

                claimed = TakeReceiver(frame, remote);
                if (claimed == null)
                {
                    if (!handshaking)
                        return FrameRoute.Dispatch;

                    if (backlog.Count >= MaxHandshakeBacklog)
                        return FrameRoute.BacklogFull;

                    backlog.Add((frame, remote));
                    return FrameRoute.Backlog;
                }
            }

            return FrameRoute.Claimed;
        }
    }

    // 调用方必须持有 gate。判断未认领的帧是否属于迟到应答：Sequential 处于丢弃窗口内，或 Keyed 命中近期超时键。
    // Matcher 模式无法识别迟到应答，总是返回 false。
    private bool IsLateUnclaimed(byte[] frame, long now)
    {
        switch (mode)
        {
            case CorrelationMode.Sequential:
                return now < sequentialLateUntil;

            case CorrelationMode.Keyed:
                return IsLateKey(frame, now);

            default:
                return false;
        }
    }

    /// <summary>
    /// 连接关闭：所有在途等待者以给定失败完成，并清空握手积压与迟到应答记录。
    /// </summary>
    /// <param name="failure">失败结果；不能为成功。</param>
    /// <exception cref="ArgumentException"><paramref name="failure"/> 为成功结果。</exception>
    public void FailAll(CommResult failure)
    {
        if (failure == null)
            throw new ArgumentNullException(nameof(failure));
        if (failure.IsSuccess)
            throw new ArgumentException("FailAll requires a failed result.", nameof(failure));

        CommResult<byte[]> typed = failure.As<byte[]>();
        var all = new List<PendingRequest>();
        lock (gate)
        {
            all.AddRange(inFlight);
            all.AddRange(keyed.Values);
            all.AddRange(receivers);
            inFlight.Clear();
            keyed.Clear();
            receivers.Clear();
            backlog.Clear();
            lateKeys.Clear();
            handshaking = false;
            sequentialLateUntil = 0;
        }

        foreach (PendingRequest waiter in all)
            waiter.TryFinish(typed);
    }

    /// <summary>
    /// 开始握手：之后未被认领的帧进入积压（上限 <see cref="MaxHandshakeBacklog"/>），而不是派发队列。
    /// </summary>
    public void BeginHandshake()
    {
        lock (gate)
        {
            handshaking = true;
        }
    }

    /// <summary>
    /// 结束握手：返回剩余积压（按到达顺序），由调用方转入派发队列。
    /// </summary>
    /// <returns>剩余积压的帧与来源，按到达顺序。</returns>
    public IReadOnlyList<(byte[] Frame, EndPoint? Remote)> EndHandshake()
    {
        lock (gate)
        {
            handshaking = false;
            (byte[] Frame, EndPoint? Remote)[] remaining = backlog.ToArray();
            backlog.Clear();
            return remaining;
        }
    }

    /// <summary>等待者被释放（<see cref="PendingRequest.Dispose"/>）：从表中移除，不记录迟到应答。</summary>
    internal void ReleaseWaiter(PendingRequest waiter)
    {
        lock (gate)
        {
            Detach(waiter);
        }
    }

    /// <summary>计时器到期：以 <see cref="CommErrorKind.Timeout"/> 完成，并按模式记录迟到应答窗口。</summary>
    internal void OnTimeout(PendingRequest waiter) => OnTimeout(waiter, $"No reply was received within {waiter.TimeoutMs} ms.");

    /// <summary>以指定消息超时结束等待（由自行计时的调用方使用，例如 UDP 的全部重发超时）：完成为 <see cref="CommErrorKind.Timeout"/>，并记录迟到应答窗口。</summary>
    internal void OnTimeout(PendingRequest waiter, string message) => Expire(waiter, CommResult<byte[]>.Fail(message, CommErrorKind.Timeout));

    /// <summary>用户取消等待：以 <see cref="CommErrorKind.Cancelled"/> 完成，并按模式记录迟到应答窗口（D10）。</summary>
    internal void OnUserCancel(PendingRequest waiter)
    {
        Expire(waiter, Cancelled());
    }

    private static CommResult<byte[]> Cancelled()
        => CommResult<byte[]>.Fail("The wait was cancelled by the caller.", CommErrorKind.Cancelled);

    private void Expire(PendingRequest waiter, CommResult<byte[]> result)
    {
        lock (gate)
        {
            if (!Detach(waiter))
                return;

            RecordLateWindow(waiter, Stopwatch.GetTimestamp());
        }

        waiter.TryFinish(result);
    }

    // 调用方必须持有 gate。
    private PendingRequest? TakeRequest(byte[] frame, EndPoint? remote)
    {
        if (mode == CorrelationMode.Keyed)
        {
            if (!keyExtractor!.TryGetResponseKey(frame, out long key) || !keyed.TryGetValue(key, out PendingRequest? candidate))
                return null;

            if (!SourceMatches(candidate, remote))
                return null;

            keyed.Remove(key);
            return candidate;
        }

        for (int i = 0; i < inFlight.Count; i++)
        {
            PendingRequest candidate = inFlight[i];
            if (!SourceMatches(candidate, remote) || !MatchesRequest(candidate, frame))
                continue;

            inFlight.RemoveAt(i);
            return candidate;
        }

        return null;
    }

    // 调用方必须持有 gate。
    private PendingRequest? TakeReceiver(byte[] frame, EndPoint? remote)
    {
        for (int i = 0; i < receivers.Count; i++)
        {
            PendingRequest candidate = receivers[i];
            if (!SourceMatches(candidate, remote) || !MatchesReceive(candidate, frame))
                continue;

            receivers.RemoveAt(i);
            return candidate;
        }

        return null;
    }

    // 调用方必须持有 gate。
    private int FindBacklogIndex(PendingRequest waiter)
    {
        for (int i = 0; i < backlog.Count; i++)
        {
            if (SourceMatches(waiter, backlog[i].Remote) && MatchesReceive(waiter, backlog[i].Frame))
                return i;
        }

        return -1;
    }

    // 调用方必须持有 gate。
    private bool Detach(PendingRequest waiter)
    {
        if (waiter.IsReceive)
            return receivers.Remove(waiter);

        if (mode == CorrelationMode.Keyed)
            return waiter.HasKey && keyed.TryGetValue(waiter.Key, out PendingRequest? current)
                && ReferenceEquals(current, waiter) && keyed.Remove(waiter.Key);

        return inFlight.Remove(waiter);
    }

    // 调用方必须持有 gate。
    private void RecordLateWindow(PendingRequest waiter, long now)
    {
        if (waiter.IsReceive || !waiter.Started || waiter.LateReplyWindowMs <= 0)
            return;

        long until = now + MsToTicks(waiter.LateReplyWindowMs);
        if (mode == CorrelationMode.Sequential)
        {
            sequentialLateUntil = Math.Max(sequentialLateUntil, until);
        }
        else if (mode == CorrelationMode.Keyed && waiter.HasKey)
        {
            RememberLateKey(waiter.Key, until, now);
        }
    }

    // 调用方必须持有 gate。
    private void RememberLateKey(long key, long until, long now)
    {
        PruneLateKeys(now);
        if (!lateKeys.ContainsKey(key) && lateKeys.Count >= MaxLateKeys)
        {
            long earliestKey = 0;
            long earliestUntil = long.MaxValue;
            foreach (KeyValuePair<long, long> entry in lateKeys)
            {
                if (entry.Value < earliestUntil)
                {
                    earliestUntil = entry.Value;
                    earliestKey = entry.Key;
                }
            }

            lateKeys.Remove(earliestKey);
        }

        lateKeys[key] = until;
    }

    // 调用方必须持有 gate。
    private bool IsLateKey(byte[] frame, long now)
    {
        PruneLateKeys(now);
        return keyExtractor!.TryGetResponseKey(frame, out long key) && lateKeys.ContainsKey(key);
    }

    // 调用方必须持有 gate。
    private void PruneLateKeys(long now)
    {
        if (lateKeys.Count == 0)
            return;

        var expired = new List<long>();
        foreach (KeyValuePair<long, long> entry in lateKeys)
        {
            if (entry.Value <= now)
                expired.Add(entry.Key);
        }

        foreach (long key in expired)
            lateKeys.Remove(key);
    }

    private int ResolveLateWindow(int timeout)
    {
        if (lateReplyWindow >= 0)
            return lateReplyWindow;

        return timeout > 0 ? timeout : 0;
    }

    private static long MsToTicks(int milliseconds)
        => (long)(milliseconds * (Stopwatch.Frequency / 1000.0));

    private static bool SourceMatches(PendingRequest waiter, EndPoint? remote)
        => waiter.ExpectedRemote == null || waiter.ExpectedRemote.Equals(remote);

    private bool MatchesRequest(PendingRequest waiter, byte[] frame)
    {
        if (waiter.Matcher == null)
            return true;

        return SafeIsMatch(waiter.Matcher, waiter.Payload.Span, frame);
    }

    private bool MatchesReceive(PendingRequest waiter, byte[] frame)
    {
        if (waiter.Matcher == null)
            return true;

        return SafeIsMatch(waiter.Matcher, ReadOnlySpan<byte>.Empty, frame);
    }

    // 用户提供的判定器抛出的异常不能破坏解析循环：记 Error 并视为不匹配。
    private bool SafeIsMatch(IResponseMatcher matcher, ReadOnlySpan<byte> request, byte[] frame)
    {
        try
        {
            return matcher.IsMatch(request, frame);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The response matcher threw an exception; the frame is treated as not matching.");
            return false;
        }
    }
}

/// <summary>入站帧经关联表判定后的去向（<see cref="PendingRequestTable.Route"/>）。</summary>
internal enum FrameRoute
{
    /// <summary>已被在途请求或接收等待者认领。</summary>
    Claimed,

    /// <summary>迟到应答，应丢弃并计入 FramesDropped。</summary>
    LateReply,

    /// <summary>进入握手积压，等接收等待者来取（见 <see cref="PendingRequestTable.Register"/>）。</summary>
    Backlog,

    /// <summary>握手积压已满（D8）。</summary>
    BacklogFull,

    /// <summary>进入派发队列。</summary>
    Dispatch,
}
