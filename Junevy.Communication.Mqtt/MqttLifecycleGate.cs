namespace Junevy.Communication.Mqtt;

/// <summary>
/// 生命周期操作的异步互斥锁（P61-D10）。SDK 要求连接、断开不能与彼此并发，因此首次连接、断开、
/// 以及每一次重连尝试都走这一把锁；发布、订阅、退订不经过它。
/// </summary>
internal sealed class MqttLifecycleGate : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    /// <summary>等待进入临界区。</summary>
    /// <param name="cancellationToken">取消令牌；取消时不会进入临界区。</param>
    /// <returns>退出临界区时释放的句柄。</returns>
    public async ValueTask<IAsyncDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(gate);
    }

    /// <summary>释放锁本身。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
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