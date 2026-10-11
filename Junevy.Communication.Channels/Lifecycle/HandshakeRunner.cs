using Junevy.Communication.Channels.Pipeline;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 握手时限内的初始化器步骤（P25 Task 11，补审第 1、2 节）：字节流与数据报客户端的打开，以及 TCP 服务端会话的握手共用。
/// 步骤在握手时限（<see cref="TimeoutScope"/>）内运行。时限耗尽、用户取消或放弃令牌触发后不再等待步骤（它可能忽略取消令牌），
/// 但观察它的异常。日志文本与放弃时的结果由调用方提供（客户端与服务端的文本不同），语义与拆分前逐字相同。
/// </summary>
internal sealed class HandshakeRunner
{
    private readonly IConnectionInitializer? initializer;
    private readonly CancellationToken abandonToken;
    private readonly Func<CommResult> abandonedResult;
    private readonly Action<Exception?> reportFault;

    /// <summary>创建握手步骤。</summary>
    /// <param name="initializer">初始化器；为 null 时步骤直接成功。</param>
    /// <param name="abandonToken">放弃令牌（服务端会话的拆除令牌）；客户端使用 <see cref="CancellationToken.None"/>。</param>
    /// <param name="abandonedResult">时限耗尽、取消或放弃时返回的结果。</param>
    /// <param name="reportFault">初始化器抛出异常时记录日志。</param>
    public HandshakeRunner(IConnectionInitializer? initializer, CancellationToken abandonToken, Func<CommResult> abandonedResult,
                           Action<Exception?> reportFault)
    {
        this.initializer = initializer;
        this.abandonToken = abandonToken;
        this.abandonedResult = abandonedResult ?? throw new ArgumentNullException(nameof(abandonedResult));
        this.reportFault = reportFault ?? throw new ArgumentNullException(nameof(reportFault));
    }

    /// <summary>
    /// 客户端握手：初始化器 → 结束握手（积压转入派发队列）→ 确认握手期间没有发生故障。
    /// </summary>
    /// <param name="view">握手期间的字节通道视图。</param>
    /// <param name="router">连接的帧路由。</param>
    /// <param name="attempt">连接尝试（记录握手期间的故障）。</param>
    /// <param name="window">握手时限。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>握手结果。</returns>
    public async Task<CommResult> CompleteClientHandshakeAsync(IByteChannel view, FrameRouter router, ConnectionAttempt attempt,
                                                               TimeoutScope window, CancellationToken cancellationToken)
    {
        CommResult initialized = await RunInitializerAsync(view, window, cancellationToken).ConfigureAwait(false);
        if (!initialized.IsSuccess)
            return initialized;

        await router.EndHandshakeAsync(cancellationToken).ConfigureAwait(false);

        // 握手期间的故障先记在尝试上（监督器此时仍处于 Connecting），这里转换为打开失败，避免把已断开的连接报告为 Connected。
        if (attempt.Faulted)
            return CommResult.Fail("The connection was lost during the handshake.", CommErrorKind.ConnectionClosed);

        return CommResult.Success();
    }

    /// <summary>
    /// 在握手时限内执行初始化器；没有初始化器时直接成功。
    /// </summary>
    /// <param name="view">交给初始化器的字节通道视图。</param>
    /// <param name="window">握手时限。</param>
    /// <param name="cancellationToken">用户取消令牌。</param>
    /// <returns>初始化器的结果，或时限、取消、放弃时的结果。</returns>
    public async Task<CommResult> RunInitializerAsync(IByteChannel view, TimeoutScope window, CancellationToken cancellationToken)
    {
        if (initializer == null)
            return CommResult.Success();

        Task<CommResult> work = StartInitializer(initializer, view, window.Token);
        if (!await WaitForStepAsync(work, window.Token, abandonToken).ConfigureAwait(false))
        {
            ObserveAbandoned(work);
            cancellationToken.ThrowIfCancellationRequested();
            return abandonedResult();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (work.IsCanceled || window.IsTimedOut)
            return abandonedResult();

        if (work.IsFaulted)
        {
            Exception? cause = work.Exception?.InnerException ?? work.Exception;
            reportFault(cause);
            return CommResult.Fail("The connection initializer threw an exception.", CommErrorKind.Unspecified, null, cause);
        }

        return work.Result;
    }

    /// <summary>
    /// 等待一个握手步骤完成，或握手时限耗尽、用户取消、放弃令牌触发。返回 true 表示步骤已完成（成功、失败或取消均可）。
    /// </summary>
    /// <param name="work">步骤任务。</param>
    /// <param name="windowToken">握手时限的令牌。</param>
    /// <param name="abandonToken">放弃令牌；不需要时为 <see cref="CancellationToken.None"/>。</param>
    /// <returns>步骤已完成时为 true；被放弃时为 false。</returns>
    public static async Task<bool> WaitForStepAsync(Task work, CancellationToken windowToken, CancellationToken abandonToken)
    {
        var interrupted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (windowToken.Register(() => interrupted.TrySetResult(true)))
        using (abandonToken.Register(() => interrupted.TrySetResult(true)))
        {
            Task finished = await Task.WhenAny(work, interrupted.Task).ConfigureAwait(false);
            return ReferenceEquals(finished, work);
        }
    }

    /// <summary>
    /// 观察被放弃的步骤的异常，避免未观察的任务异常。
    /// </summary>
    /// <param name="work">被放弃的步骤任务。</param>
    public static void ObserveAbandoned(Task work)
    {
        work.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    // 同步抛出的异常转换为已完成的故障任务，统一由 RunInitializerAsync 处理。
    private static Task<CommResult> StartInitializer(IConnectionInitializer initializer, IByteChannel view, CancellationToken token)
    {
        try
        {
            return initializer.InitializeAsync(view, token);
        }
        catch (Exception ex)
        {
            return Task.FromException<CommResult>(ex);
        }
    }
}
