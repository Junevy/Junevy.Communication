using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Lifecycle;

/// <summary>
/// 客户端通道的监督外观（P25 Task 12，补审第 3 节）：持有监督器与统计，提供字节流与数据报客户端共用的成员——名称、状态、连接判断、
/// 统计、连接 / 断开 / 等待连接的转发、同步与异步释放（同步释放最长等待 <c>DisconnectTimeout + 1000</c> 毫秒，D16），以及释放之后的检查。
/// 两个公开通道各自只保留收发与传输差异，共用成员一行转发。
/// </summary>
/// <typeparam name="TChannel">传输的字节通道类型（<see cref="Pipeline.StreamChannel"/> 或 <see cref="Pipeline.DatagramChannel"/>）。</typeparam>
internal sealed class SupervisedChannelCore<TChannel>
    where TChannel : class
{
    private readonly object channel;
    private readonly string name;
    private readonly ClientOptionsCore options;
    private readonly ConnectionSupervisor supervisor;
    private readonly ConnectionStatistics statistics;
    private readonly ILogger logger;
    private readonly Func<TChannel?> currentChannel;
    private int disposed;

    /// <summary>
    /// 创建监督外观。
    /// </summary>
    /// <param name="channel">公开的通道实例；释放之后的异常类型名取自它（与各通道原有的行为一致）。</param>
    /// <param name="name">通道名称。</param>
    /// <param name="options">已校验的客户端运行参数。</param>
    /// <param name="supervisor">已绑定驱动的监督器。</param>
    /// <param name="currentChannel">当前连接的字节通道；没有连接时为 null。</param>
    /// <param name="statistics">连接统计。</param>
    /// <param name="logger">日志记录器。</param>
    public SupervisedChannelCore(object channel, string name, ClientOptionsCore options, ConnectionSupervisor supervisor,
                                 Func<TChannel?> currentChannel, ConnectionStatistics statistics, ILogger logger)
    {
        this.channel = channel;
        this.name = name;
        this.options = options;
        this.supervisor = supervisor;
        this.currentChannel = currentChannel;
        this.statistics = statistics;
        this.logger = logger;
    }

    /// <summary>通道名称。</summary>
    public string Name => name;

    /// <summary>当前连接状态。</summary>
    public ConnectionState State => supervisor.State;

    /// <summary>是否已连接。</summary>
    public bool IsConnected => State == ConnectionState.Connected;

    /// <summary>连接统计。</summary>
    public ConnectionStatistics Statistics => statistics;

    /// <summary>是否已释放。</summary>
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>连接；释放之后抛出 <see cref="ObjectDisposedException"/>。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>连接结果。</returns>
    public async Task<CommResult> ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await supervisor.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>断开；释放之后直接返回。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>断开完成的任务。</returns>
    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
            return Task.CompletedTask;

        return supervisor.DisconnectAsync(options.DisconnectTimeout, cancellationToken);
    }

    /// <summary>等待连接建立；释放之后抛出 <see cref="ObjectDisposedException"/>。</summary>
    /// <param name="timeout">超时（毫秒）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>是否在超时内连接成功。</returns>
    public async Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return await supervisor.WaitForConnectedAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 只有 Connected 时才返回字节通道；连接正在关闭或重建时即使有旧的通道对象也返回 null。
    /// </summary>
    /// <returns>当前的字节通道，或 null。</returns>
    public TChannel? ConnectedChannel() => State == ConnectionState.Connected ? currentChannel() : null;

    /// <summary>释放之后抛出 <see cref="ObjectDisposedException"/>（类型名取自公开通道）。</summary>
    public void ThrowIfDisposed()
    {
        if (IsDisposed)
            throw new ObjectDisposedException(channel.GetType().Name);
    }

    /// <summary>
    /// 同步释放：等待 <see cref="DisposeAsync"/> 完成，最长 <c>DisconnectTimeout + 1000</c> 毫秒（D16）。
    /// 超时后返回，释放在后台继续。在事件处理器内调用时不会死锁。
    /// </summary>
    public void Dispose()
    {
        int limit = options.DisconnectTimeout + 1000;
        Task disposing = DisposeAsync().AsTask();
        if (!disposing.Wait(limit))
            logger.LogWarning("Disposing the channel {Name} did not finish within {Timeout} ms; the release continues in the background.", name, limit);
    }

    /// <summary>
    /// 异步释放：关闭连接、停止重连与心跳、结束状态事件派发。幂等。
    /// 在状态事件处理器内调用时只发出停止信号，不等待状态事件派发结束，因此不会死锁。
    /// </summary>
    /// <returns>释放完成的任务。</returns>
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref disposed, 1);
        return supervisor.DisposeAsync();
    }
}
