using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 测试用的字节流客户端通道：每次打开都新建一个内存双工流对（<see cref="DuplexStreamPair"/>），返回 A 端，B 端交给测试脚本。
/// <see cref="AbortTransport"/> 中止当前连接所用的双工流，因此可以在不使用套接字的情况下测试重连与释放。
/// </summary>
internal sealed class DuplexClientChannel : StreamClientChannel
{
    private readonly object sync = new object();
    private readonly List<DuplexStreamPair> pairs = new List<DuplexStreamPair>();
    private readonly Func<DuplexStreamPair, Task>? onOpened;
    private readonly Func<Stream, CancellationToken, Task<CommResult<Stream>>>? onSecure;
    private int openCount;
    private int abortCount;
    private DuplexStreamPair? currentPair;

    /// <summary>
    /// 创建测试通道。
    /// </summary>
    /// <param name="settings">运行参数。</param>
    /// <param name="components">代码级覆盖；可为 null。</param>
    /// <param name="onOpened">每次打开时在返回流之前调用（可用于让对端先发送数据）；可为 null。</param>
    /// <param name="logger">日志记录器；为 null 时使用测试日志器。</param>
    /// <param name="onSecure">替代 <c>SecureStreamAsync</c> 的实现（模拟 TLS 包装）；为 null 时使用基类的默认实现。</param>
    public DuplexClientChannel(ClientChannelSettings settings, ChannelComponents? components = null,
                               Func<DuplexStreamPair, Task>? onOpened = null, ILogger? logger = null,
                               Func<Stream, CancellationToken, Task<CommResult<Stream>>>? onSecure = null)
        : base("duplex-test", settings, components, logger ?? new TestLogger())
    {
        this.onOpened = onOpened;
        this.onSecure = onSecure;
    }

    /// <summary>OpenStreamAsync 的调用次数（含失败的打开）。</summary>
    public int OpenCount => Volatile.Read(ref openCount);

    /// <summary>AbortTransport 的调用次数。</summary>
    public int AbortCount => Volatile.Read(ref abortCount);

    /// <summary>最近一次打开创建的双工流对；尚未打开时为 null。</summary>
    public DuplexStreamPair? LatestPair
    {
        get
        {
            lock (sync)
                return pairs.Count == 0 ? null : pairs[pairs.Count - 1];
        }
    }

    /// <inheritdoc />
    protected override async Task<CommResult<Stream>> OpenStreamAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref openCount);

        DuplexStreamPair pair = DuplexStreamPair.Create();
        lock (sync)
        {
            pairs.Add(pair);
            currentPair = pair;
        }

        if (onOpened != null)
            await onOpened(pair).ConfigureAwait(false);

        return CommResult<Stream>.Success(pair.A);
    }

    /// <inheritdoc />
    protected override Task<CommResult<Stream>> SecureStreamAsync(Stream stream, CancellationToken cancellationToken)
        => onSecure != null
            ? onSecure(stream, cancellationToken)
            : base.SecureStreamAsync(stream, cancellationToken);

    /// <inheritdoc />
    protected override void AbortTransport()
    {
        Interlocked.Increment(ref abortCount);
        DuplexStreamPair? pair;
        lock (sync)
        {
            pair = currentPair;
        }

        pair?.Abort();
    }

    /// <inheritdoc />
    protected override string DescribeEndpoint() => "duplex";
}
