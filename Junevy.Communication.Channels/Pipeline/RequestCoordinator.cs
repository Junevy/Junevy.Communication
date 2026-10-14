using System.Net;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 字节流与数据报通道共用的请求编排（P25 Task 10，补审第 1 节）：Sequential 请求锁、关闭检查、注册等待者、
/// 交换（由通道以委托传入）、超时或取消之后持锁过迟到窗口、finally 释放；接收等待者也在这里。
/// 两种通道的差异只在交换步骤（字节流"写出 + 计时 + 等应答"，数据报"按重试次数重发"）与字节流超时后的连接重置。
/// </summary>
internal sealed class RequestCoordinator
{
    private readonly ChannelStopSequence lifecycle;
    private readonly PendingRequestTable table;
    private readonly SemaphoreSlim requestLock = new SemaphoreSlim(1, 1);
    private readonly bool sequential;
    private readonly int defaultTimeout;
    private readonly int lateReplyWindowSetting;
    private readonly Func<bool>? resetOnExpiry;

    /// <summary>
    /// 创建请求编排。
    /// </summary>
    /// <param name="lifecycle">停止与故障状态（关闭检查与停止令牌）。</param>
    /// <param name="table">关联表。</param>
    /// <param name="sequential">是否为 Sequential 关联（启用请求锁与迟到窗口）。</param>
    /// <param name="defaultTimeout">请求未指定超时时使用的默认超时（毫秒）。</param>
    /// <param name="lateReplyWindowSetting">迟到应答窗口的配置值（-1 表示等于本次请求超时）。</param>
    /// <param name="resetOnExpiry">Sequential 请求超时或取消后的重置钩子：返回 true 表示已重置连接，不再持锁等待窗口；不需要时为 null。</param>
    public RequestCoordinator(ChannelStopSequence lifecycle, PendingRequestTable table, bool sequential, int defaultTimeout,
                              int lateReplyWindowSetting, Func<bool>? resetOnExpiry)
    {
        this.lifecycle = lifecycle;
        this.table = table;
        this.sequential = sequential;
        this.defaultTimeout = defaultTimeout;
        this.lateReplyWindowSetting = lateReplyWindowSetting;
        this.resetOnExpiry = resetOnExpiry;
    }

    /// <summary>
    /// 执行一次请求（计划 8.2 请求流程）。调用方已校验负载与目标。
    /// </summary>
    /// <param name="payload">请求负载（非空）。</param>
    /// <param name="options">超时与匹配器；为 null 时使用默认值。</param>
    /// <param name="expectedRemote">期望的应答来源；字节流为 null。</param>
    /// <param name="exchange">交换步骤：写出（或重发）请求并等待应答。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>应答或失败结果。</returns>
    public async Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options, EndPoint? expectedRemote,
                                                       RequestExchange exchange, CancellationToken cancellationToken)
    {
        if (lifecycle.IsClosed)
            return lifecycle.ClosedResult<byte[]>();

        int timeout = options?.Timeout ?? defaultTimeout;
        bool holdsRequestLock = false;
        PendingRequest? waiter = null;
        try
        {
            if (sequential)
            {
                if (!await WaitForLockAsync(requestLock, cancellationToken).ConfigureAwait(false))
                    return CommResult<byte[]>.Fail("The request was cancelled while waiting for the request lock.", CommErrorKind.Cancelled);

                holdsRequestLock = true;
            }

            if (lifecycle.IsClosed)
                return lifecycle.ClosedResult<byte[]>();

            waiter = table.Register(payload, options?.Matcher, timeout, expectedRemote, cancellationToken);
            if (waiter.Completion.IsCompleted)
                return await waiter.Completion.ConfigureAwait(false);

            // 登记之后再检查：登记与报告故障（FailAll）并发时，等待者可能错过 FailAll，此时由这里返回。
            if (lifecycle.IsClosed)
                return lifecycle.ClosedResult<byte[]>();

            ExchangeOutcome outcome = await exchange(waiter, timeout, cancellationToken).ConfigureAwait(false);
            if (outcome.AwaitedReply && sequential && (outcome.Result.ErrorKind == CommErrorKind.Timeout || outcome.Result.ErrorKind == CommErrorKind.Cancelled))
                await HoldRequestLockAfterExpiryAsync(timeout).ConfigureAwait(false);

            return outcome.Result;
        }
        finally
        {
            waiter?.Dispose();
            if (holdsRequestLock)
                requestLock.Release();
        }
    }

    /// <summary>
    /// 不发送，只等待下一个匹配的入站帧（计划 8.2 接收流程）。接收等待者在注册时开始计时，options 为 null 时超时取 RequestTimeout。
    /// </summary>
    /// <param name="options">超时与匹配器；为 null 时使用配置的 RequestTimeout 与默认匹配。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>匹配的帧或失败结果。</returns>
    public async Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options, CancellationToken cancellationToken)
    {
        if (lifecycle.IsClosed)
            return lifecycle.ClosedResult<byte[]>();

        int timeout = options?.Timeout ?? defaultTimeout;
        PendingRequest waiter = table.Register(ReadOnlyMemory<byte>.Empty, options?.Matcher, timeout, null, cancellationToken);
        try
        {
            // 与 RequestAsync 相同：登记之后再检查，避免错过 FailAll。
            if (lifecycle.IsClosed)
                return lifecycle.ClosedResult<byte[]>();

            return await waiter.Completion.ConfigureAwait(false);
        }
        finally
        {
            waiter.Dispose();
        }
    }

    /// <summary>等待发送锁或请求锁；取消返回 false（不影响连接）。</summary>
    /// <param name="gate">要等待的信号量。</param>
    /// <param name="token">取消令牌。</param>
    /// <returns>获得锁为 true；取消为 false。</returns>
    public static async Task<bool> WaitForLockAsync(SemaphoreSlim gate, CancellationToken token)
    {
        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // 计划 8.2 请求第 4 条：Sequential 超时或取消等待后，重建连接，或在迟到窗口期内继续持有请求锁。
    // 重置钩子（字节流）返回 true 时已重建连接，不再等待窗口。
    private async Task HoldRequestLockAfterExpiryAsync(int timeout)
    {
        if (resetOnExpiry != null && resetOnExpiry())
            return;

        int window = ResolveLateReplyWindow(timeout);
        if (window <= 0)
            return;

        try
        {
            // 这段等待不受用户令牌约束；通道停止时立即结束。
            await Task.Delay(window, lifecycle.StopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 通道正在停止：窗口提前结束。
        }
    }

    // 与 PendingRequestTable 的窗口规则一致：-1 表示等于本次请求超时，超时无限时窗口为 0。
    private int ResolveLateReplyWindow(int timeout)
    {
        if (lateReplyWindowSetting >= 0)
            return lateReplyWindowSetting;

        return timeout > 0 ? timeout : 0;
    }

    /// <summary>一次请求的交换步骤：写出（或重发）并等待应答。</summary>
    /// <param name="waiter">已登记的等待者。</param>
    /// <param name="timeout">本次请求的超时（毫秒）。</param>
    /// <param name="token">用户取消令牌。</param>
    /// <returns>交换结果。</returns>
    public delegate Task<ExchangeOutcome> RequestExchange(PendingRequest waiter, int timeout, CancellationToken token);

    /// <summary>一次请求的等待结果。AwaitedReply 为 false 表示在发送阶段失败，不进入迟到窗口。</summary>
    public readonly struct ExchangeOutcome
    {
        private ExchangeOutcome(CommResult<byte[]> result, bool awaitedReply)
        {
            Result = result;
            AwaitedReply = awaitedReply;
        }

        /// <summary>请求的结果（应答或失败）。</summary>
        public CommResult<byte[]> Result { get; }

        /// <summary>是否已进入等待应答（发送成功）。</summary>
        public bool AwaitedReply { get; }

        /// <summary>发送阶段失败。</summary>
        /// <param name="failure">失败结果。</param>
        public static ExchangeOutcome SendFailure(CommResult<byte[]> failure) => new ExchangeOutcome(failure, awaitedReply: false);

        /// <summary>等待应答之后得到的结果。</summary>
        /// <param name="result">应答或超时结果。</param>
        public static ExchangeOutcome AfterWait(CommResult<byte[]> result) => new ExchangeOutcome(result, awaitedReply: true);
    }
}
