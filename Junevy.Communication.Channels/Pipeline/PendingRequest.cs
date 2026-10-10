using System.Net;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 一个等待者：在途请求（<see cref="IByteChannel.RequestAsync"/>）或接收等待（<see cref="IByteChannel.ReceiveAsync"/>）。
/// 完成只能发生一次（认领、超时、取消、<see cref="PendingRequestTable.FailAll"/>、释放之间通过 <see cref="TryFinish"/> 串行化）；
/// 超时计时器与取消注册在完成或 <see cref="Dispose"/> 时释放。
/// </summary>
/// <remarks>
/// 锁顺序：<see cref="PendingRequestTable"/> 的结构锁 → 本类的 <c>sync</c>。本类从不在持有 <c>sync</c> 时进入结构锁，
/// 也不在持有任何锁时调用 <see cref="CancellationTokenRegistration.Dispose"/>（它会等待正在执行的取消回调，而回调需要结构锁）。
/// </remarks>
internal sealed class PendingRequest : IDisposable
{
    private readonly object sync = new object();
    private readonly PendingRequestTable owner;
    private readonly int timeout;
    private readonly TaskCompletionSource<CommResult<byte[]>> completion =
        new TaskCompletionSource<CommResult<byte[]>>(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool finished;
    private bool disposed;
    private Timer? timer;
    private CancellationTokenRegistration registration;
    private volatile bool started;

    /// <summary>
    /// 创建等待者（尚未注册到表中，计时器与取消注册都未启动）。
    /// </summary>
    /// <param name="owner">所属的路由表。</param>
    /// <param name="payload">请求负载；为空表示接收等待者。</param>
    /// <param name="matcher">应答判定器；可为 null。</param>
    /// <param name="timeout">超时毫秒数；≤ 0 表示不限时。</param>
    /// <param name="lateReplyWindowMs">已解析的迟到应答窗口毫秒数；0 表示不记录迟到应答。</param>
    /// <param name="expectedRemote">UDP 非定向模式的期望来源；为 null 时不校验来源。</param>
    /// <param name="isReceive">是否为 <see cref="IByteChannel.ReceiveAsync"/> 等待者。</param>
    internal PendingRequest(PendingRequestTable owner, ReadOnlyMemory<byte> payload, IResponseMatcher? matcher, int timeout,
                            int lateReplyWindowMs, EndPoint? expectedRemote, bool isReceive)
    {
        this.owner = owner;
        this.timeout = timeout;
        Payload = payload;
        Matcher = matcher;
        LateReplyWindowMs = lateReplyWindowMs;
        ExpectedRemote = expectedRemote;
        IsReceive = isReceive;
    }

    /// <summary>完成时的结果：应答帧、超时、取消或失败。只会完成一次。</summary>
    public Task<CommResult<byte[]>> Completion => completion.Task;

    /// <summary>请求负载（接收等待者为空）。</summary>
    internal ReadOnlyMemory<byte> Payload { get; }

    /// <summary>应答判定器；为 null 时认领任意帧（Keyed 模式不使用）。</summary>
    internal IResponseMatcher? Matcher { get; }

    /// <summary>UDP 非定向模式的期望来源；为 null 时不校验。</summary>
    internal EndPoint? ExpectedRemote { get; }

    /// <summary>是否为接收等待者（不发送，不产生迟到应答）。</summary>
    internal bool IsReceive { get; }

    /// <summary>已解析的迟到应答窗口毫秒数；0 表示不记录迟到应答。</summary>
    internal int LateReplyWindowMs { get; }

    /// <summary>超时毫秒数；≤ 0 表示不限时。</summary>
    internal int TimeoutMs => timeout;

    /// <summary>Keyed 模式下请求的关联键；仅当 <see cref="HasKey"/> 为 true 时有效。</summary>
    internal long Key { get; private set; }

    /// <summary>是否已提取到关联键（Keyed 模式的请求）。</summary>
    internal bool HasKey { get; private set; }

    /// <summary>计时已开始，即请求帧已写出（或接收等待者已注册）。</summary>
    internal bool Started => started;

    /// <summary>
    /// 开始计时。请求帧写出完成后调用；超时从调用时起算。若已完成则无效果。重复调用无效果。
    /// </summary>
    public void StartTimer()
    {
        lock (sync)
        {
            if (finished || started)
                return;

            started = true;
            if (timeout > 0)
                timer = new Timer(OnTimerElapsed, null, timeout, System.Threading.Timeout.Infinite);
        }
    }

    /// <summary>
    /// 尝试完成等待者。只有第一次调用返回 true；之后的调用（超时、取消、认领之间的竞态）返回 false 且不改变结果。
    /// 完成时释放计时器；回调在线程池上异步执行（<see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>）。
    /// </summary>
    /// <param name="result">完成结果。</param>
    /// <returns>本次调用完成了等待者返回 true。</returns>
    internal bool TryFinish(CommResult<byte[]> result)
    {
        lock (sync)
        {
            if (finished)
                return false;

            finished = true;
            timer?.Dispose();
            timer = null;
            completion.TrySetResult(result);
            return true;
        }
    }

    /// <summary>
    /// 记录 Keyed 模式提取到的请求键。
    /// </summary>
    /// <param name="key">关联键。</param>
    internal void SetKey(long key)
    {
        Key = key;
        HasKey = true;
    }

    /// <summary>
    /// 绑定用户取消令牌：令牌触发时以 <see cref="CommErrorKind.Cancelled"/> 完成。
    /// 必须在表中注册之后调用（令牌已取消时回调同步执行）；不能在持有路由表锁时调用。
    /// </summary>
    /// <param name="cancellationToken">用户取消令牌。</param>
    internal void ArmCancellation(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
            return;

        CancellationTokenRegistration newRegistration = cancellationToken.Register(OnCancellationRequested);
        bool releaseNow;
        lock (sync)
        {
            releaseNow = disposed || finished;
            if (!releaseNow)
                registration = newRegistration;
        }

        if (releaseNow)
            newRegistration.Dispose();
    }

    /// <summary>
    /// 释放等待者：从路由表中移除（不记录迟到应答）；若尚未完成，以 <see cref="CommErrorKind.Unspecified"/> 完成，避免等待方永远挂起；
    /// 释放计时器与取消注册。可重复调用。
    /// </summary>
    public void Dispose()
    {
        owner.ReleaseWaiter(this);
        TryFinish(CommResult<byte[]>.Fail("The pending request was released before a reply arrived.", CommErrorKind.Unspecified));

        CancellationTokenRegistration toDispose;
        lock (sync)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
            toDispose = registration;
            registration = default;
        }

        // 在所有锁之外释放：若取消回调正在执行，这里会等待它返回。
        toDispose.Dispose();
    }

    private void OnTimerElapsed(object? state)
    {
        owner.OnTimeout(this);
    }

    private void OnCancellationRequested()
    {
        owner.OnUserCancel(this);
    }
}
