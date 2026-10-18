using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 脚本化的 <see cref="IClientChannel"/> 测试替身：记录每个请求，按脚本或响应函数返回结果，可以模拟断开与重连。
/// 不建立任何套接字。与 Plc.Tests 的 ScriptedClientChannel 同形（那个类型不跨程序集共享）。
/// </summary>
internal sealed class ScriptedModbusChannel : IClientChannel
{
    private readonly ConnectionStatisticsRecorder statistics = new ConnectionStatisticsRecorder();
    private readonly object sync = new object();
    private readonly Queue<CommResult<byte[]>> script = new Queue<CommResult<byte[]>>();
    private readonly List<byte[]> requests = new List<byte[]>();
    private EventHandler<ConnectionStateChangedEventArgs>? stateChanged;
    private bool connected = true;
    private int disposeCount;

    /// <summary>未设置脚本时，按此函数生成应答。返回 null 时该请求失败（<c>Unspecified</c>）。</summary>
    public Func<byte[], CommResult<byte[]>?>? Responder { get; set; }

    /// <summary><see cref="WaitForConnectedAsync"/> 被调用时是否恢复连接（模拟链路在 RetryInterval 内恢复）。</summary>
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

    /// <summary>最后一个请求。</summary>
    public byte[]? LastRequest
    {
        get
        {
            lock (sync)
                return requests.Count == 0 ? null : requests[requests.Count - 1];
        }
    }

    /// <summary>Dispose 被调用的次数。</summary>
    public int DisposeCount => Volatile.Read(ref disposeCount);

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

    /// <summary>按脚本返回下一个应答。</summary>
    /// <param name="reply">应答结果。</param>
    public void Enqueue(CommResult<byte[]> reply)
    {
        lock (sync)
            script.Enqueue(reply);
    }

    /// <inheritdoc />
    public string Name => "scripted-modbus";

    /// <inheritdoc />
    public ConnectionState State => IsConnected ? ConnectionState.Connected : ConnectionState.Disconnected;

    /// <inheritdoc />
    public bool IsConnected => Connected;

    /// <inheritdoc />
    public ConnectionStatistics Statistics => statistics.Statistics;

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
            if (ReconnectOnWait)
                connected = true;

            return Task.FromResult(connected);
        }
    }

    /// <inheritdoc />
    public async Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                       CancellationToken cancellationToken = default)
    {
        // 让出线程：并发的操作在这里有机会交错（如果客户端没有操作锁，测试就会失败）。
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
        if (script.Count > 0)
            return script.Dequeue();

        return Responder?.Invoke(request) ?? CommResult<byte[]>.Fail("No scripted reply.", CommErrorKind.Unspecified);
    }
}
