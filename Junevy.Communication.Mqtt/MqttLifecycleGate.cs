namespace Junevy.Communication.Mqtt;

/// <summary>
/// 生命周期操作的异步互斥锁（P61-D10）。SDK 要求连接、断开不能与彼此并发，因此首次连接、断开、
/// 每一次重连尝试以及释放都走这一把锁；发布、订阅、退订不经过它。
/// </summary>
/// <remarks>
/// 信号量不释放：它从不访问 <see cref="SemaphoreSlim.AvailableWaitHandle"/>，没有需要释放的非托管资源；
/// 而客户端释放时仍可能有操作握着租约，释放了信号量，那次 <c>Release</c> 就会抛 <see cref="ObjectDisposedException"/>。
/// </remarks>
internal sealed class MqttLifecycleGate
{
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>等待进入临界区。</summary>
    /// <param name="cancellationToken">取消令牌；取消时不会进入临界区。</param>
    /// <returns>退出临界区时释放的句柄。</returns>
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
