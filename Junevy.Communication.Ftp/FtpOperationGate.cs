namespace Junevy.Communication.Ftp;

/// <summary>
/// 同一个客户端上操作的串行门（P63-D4）：FTP 的控制连接一次只能执行一条命令，因此操作排队执行，不是失败。
/// 连接、断开、每个操作与释放都经过它。
/// </summary>
/// <remarks>
/// 信号量从不释放：客户端释放时仍可能有操作握着租约，释放了信号量那次 <c>Release</c> 就会抛 <see cref="ObjectDisposedException"/>。
/// </remarks>
internal sealed class FtpOperationGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>排队等待进入串行区。</summary>
    /// <param name="cancellationToken">取消令牌；取消时不会进入串行区。</param>
    /// <returns>离开串行区时释放的租约。</returns>
    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    private sealed class Lease : IAsyncDisposable
    {
        private readonly SemaphoreSlim gate;
        private int released;

        public Lease(SemaphoreSlim gate) => this.gate = gate;

        public ValueTask DisposeAsync()
        {
            // 重复释放会让信号量计数越过上限，因此只认第一次。
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                gate.Release();
            }

            return default;
        }
    }
}
