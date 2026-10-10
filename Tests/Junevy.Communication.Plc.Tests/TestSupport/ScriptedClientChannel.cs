using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 脚本化的 <see cref="IClientChannel"/> 测试替身：记录每个请求，按脚本或响应函数返回结果，可以模拟断开与重连。
/// 不建立任何套接字；请求与应答都在内存中。
/// </summary>
internal sealed class ScriptedClientChannel : IClientChannel
{
    private static readonly ConnectionStatistics StatisticsInstance = CreateStatistics();

    private readonly object sync = new object();
    private readonly Queue<ScriptedReply> script = new Queue<ScriptedReply>();
    private readonly List<byte[]> requests = new List<byte[]>();
    private readonly List<int> waitTimeouts = new List<int>();
    private EventHandler<ConnectionStateChangedEventArgs>? stateChanged;
    private bool connected = true;
    private int disposeCount;

    /// <summary>未设置脚本时，按此函数生成应答（例如测试设备）。返回 null 时该请求失败（<c>Unspecified</c>）。</summary>
    public Func<byte[], CommResult<byte[]>?>? Responder { get; set; }

    /// <summary>
    /// <see cref="WaitForConnectedAsync"/> 被调用时是否恢复连接（模拟链路在 RetryInterval 内恢复）。
    /// </summary>
    public bool ReconnectOnWait { get; set; }

    /// <summary>发送给通道的请求副本，按发送顺序排列。</summary>
    public IReadOnlyList<byte[]> Requests
    {
        get
        {
            lock (sync)
                return requests.ToArray();
        }
    }

    /// <summary>传给 <see cref="WaitForConnectedAsync"/> 的超时值，按调用顺序排列。</summary>
    public IReadOnlyList<int> WaitTimeouts
    {
        get
        {
            lock (sync)
                return waitTimeouts.ToArray();
        }
    }

    /// <summary>Dispose 被调用的次数。</summary>
    public int DisposeCount => Volatile.Read(ref disposeCount);

    /// <summary>是否有状态变化订阅者（用于验证事件转发）。</summary>
    public bool HasStateChangedSubscribers
    {
        get
        {
            lock (sync)
                return stateChanged != null;
        }
    }

    /// <summary>设置连接状态（测试用）。</summary>
    public bool Connected
    {
        get
        {
            lock (sync)
                return connected;
        }
        set
        {
            lock (sync)
                connected = value;
        }
    }

    /// <inheritdoc />
    public string Name => "scripted";

    /// <inheritdoc />
    public ConnectionState State => IsConnected ? ConnectionState.Connected : ConnectionState.Disconnected;

    /// <inheritdoc />
    public bool IsConnected => Connected;

    /// <inheritdoc />
    public ConnectionStatistics Statistics => StatisticsInstance;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged
    {
        add
        {
            lock (sync)
                stateChanged += value;
        }
        remove
        {
            lock (sync)
                stateChanged -= value;
        }
    }

    /// <summary>
    /// 按脚本返回下一个应答。
    /// </summary>
    /// <param name="reply">应答结果。</param>
    public void Enqueue(CommResult<byte[]> reply)
    {
        lock (sync)
            script.Enqueue(new ScriptedReply(reply, disconnectAfterReply: false));
    }

    /// <summary>
    /// 按脚本返回下一个应答，并在返回后断开连接（模拟超时或断线时链路随之丢失）。
    /// </summary>
    /// <param name="reply">应答结果。</param>
    public void EnqueueWithDisconnect(CommResult<byte[]> reply)
    {
        lock (sync)
            script.Enqueue(new ScriptedReply(reply, disconnectAfterReply: true));
    }

    /// <inheritdoc />
    public Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Connected = true;
        return Task.FromResult(CommResult.Success());
    }

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        Connected = false;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            waitTimeouts.Add(timeout);
            if (ReconnectOnWait)
                connected = true;

            return Task.FromResult(connected);
        }
    }

    /// <inheritdoc />
    public async Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                       CancellationToken cancellationToken = default)
    {
        // 让出线程：并发的操作在这里有机会交错（如果套件没有操作锁，测试就会失败）。
        await Task.Yield();

        lock (sync)
        {
            if (cancellationToken.IsCancellationRequested)
                return CommResult<byte[]>.Fail("The request was cancelled.", CommErrorKind.Cancelled);
            if (!connected)
                return CommResult<byte[]>.Fail("The channel is not connected.", CommErrorKind.NotConnected);

            byte[] request = payload.ToArray();
            requests.Add(request);
            return NextReply(request);
        }
    }

    /// <inheritdoc />
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The scripted channel only supports requests.");

    /// <inheritdoc />
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The scripted channel only supports requests.");

    /// <inheritdoc />
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Increment(ref disposeCount);
        Connected = false;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    // 调用方持有 sync。脚本优先；脚本耗尽后使用响应函数。
    private CommResult<byte[]> NextReply(byte[] request)
    {
        if (script.Count == 0)
            return Responder?.Invoke(request) ?? CommResult<byte[]>.Fail("No scripted reply.", CommErrorKind.Unspecified);

        ScriptedReply next = script.Dequeue();
        if (next.DisconnectAfterReply)
            connected = false;

        return next.Reply;
    }

    private static ConnectionStatistics CreateStatistics()
    {
        // ConnectionStatistics 的构造函数是 internal，测试替身通过反射创建一个全零的统计对象。
        // 见计划 §20.2（Plc 套件测试）：Channels 提供公开构造函数后移除此反射。
        return (ConnectionStatistics)Activator.CreateInstance(typeof(ConnectionStatistics), nonPublic: true)!;
    }

    private sealed class ScriptedReply
    {
        public ScriptedReply(CommResult<byte[]> reply, bool disconnectAfterReply)
        {
            Reply = reply;
            DisconnectAfterReply = disconnectAfterReply;
        }

        public CommResult<byte[]> Reply { get; }

        public bool DisconnectAfterReply { get; }
    }
}
