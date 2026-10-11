using Junevy.Communication.Core.Results;
using Junevy.Communication.Core.Utils;
using Junevy.Communication.Serial.Internal;

namespace Junevy.Communication.Serial;

// 打开路径（P25 Task 12，补审第 6 节）：OpenStreamAsync 的重试循环与拒绝访问后的等待。
// 与 SerialChannel.cs 同属一个类（SerialChannel 不在任何 IVT 名单内，因此只能在自身内部拆分）。
public sealed partial class SerialChannel
{
    /// <summary>
    /// 打开端口（受 <c>OpenTimeout</c> 约束）。拒绝访问（<see cref="UnauthorizedAccessException"/>）可能是瞬时状态（驱动释放端口是异步的），
    /// 在 <c>OpenTimeout</c> 总时限内每 20 ms 重试一次；时限用尽仍被拒绝访问则返回 <c>ConnectionClosed</c>，消息说明已经重试。
    /// 其他打开失败（端口不存在、被占用）立即返回 <c>ConnectionClosed</c>，消息包含端口名与原始异常消息；
    /// 超时返回 <c>Timeout</c>，端口在驱动最终返回时释放；用户取消抛出 <see cref="OperationCanceledException"/>。
    /// 成功时清空接收缓冲（<c>DiscardInBuffer</c>）后返回数据流。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时为已打开端口的数据流。</returns>
    protected override async Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken)
    {
        using TimeoutScope window = TimeoutScope.Start(portSettings.OpenTimeout, cancellationToken);
        int attempts = 0;
        UnauthorizedAccessException? denied = null;
        while (true)
        {
            // 拒绝访问之后时限用尽：报告 ConnectionClosed 并说明已经重试（而不是超时）。
            if (denied != null && window.Token.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return OpenFailedAfterRetries(denied, attempts);
            }

            attempts++;
            ISerialPortHandle handle;
            try
            {
                handle = handleFactory.Create(portSettings);
            }
            catch (Exception ex)
            {
                return OpenFailed(ex);
            }

            Task opening = Task.Run(handle.Open);
            bool completed = await WaitForOpenAsync(opening, window.Token).ConfigureAwait(false);
            Exception? failure = completed && opening.IsFaulted ? opening.Exception!.GetBaseException() : null;

            // 拒绝访问即使在时限到达之后才返回，也按拒绝访问处理（已经重试过），而不是超时。其余情况下时限到达即超时。
            bool timedOut = !completed || (window.Token.IsCancellationRequested && failure is not UnauthorizedAccessException);
            if (timedOut)
            {
                // 驱动可能在之后才返回：由后台在它完成时释放端口，不让端口没有所有者（计划 14.2）。
                ReleaseWhenOpened(opening, handle);
                cancellationToken.ThrowIfCancellationRequested();

                // 之前的尝试已被拒绝访问，时限到达时本次尝试仍未返回：时限用尽仍被拒绝访问，报告 ConnectionClosed（已重试）。
                return !completed && denied != null ? OpenFailedAfterRetries(denied, attempts) : OpenTimedOut();
            }

            if (failure == null)
                return ActivateHandle(handle);

            DisposeQuietly(handle);
            if (failure is not UnauthorizedAccessException access)
                return OpenFailed(failure);

            // 拒绝访问可能是瞬时状态：驱动释放端口是异步的，刚关闭的端口可能在短时间内仍被占用。在 OpenTimeout 总时限内重试。
            denied = access;
            await WaitBeforeRetryAsync(window.Token, cancellationToken).ConfigureAwait(false);
        }
    }

    // 拒绝访问后的重试间隔：用户取消立即结束（抛出）；时限用尽则返回，由循环开头报告。
    private static async Task WaitBeforeRetryAsync(CancellationToken windowToken, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(OpenRetryIntervalMilliseconds, windowToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
