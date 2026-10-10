using Junevy.Communication.Channels.Lifecycle;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 生命周期测试用的连接驱动替身：打开结果由脚本决定（按第几次打开），并记录打开与关闭的调用。
/// </summary>
internal sealed class FakeConnectionDriver : IConnectionDriver
{
    private readonly object sync = new object();
    private readonly Func<int, CancellationToken, Task<CommResult>> open;
    private readonly List<DisconnectReason> closeReasons = new List<DisconnectReason>();
    private int openCalls;
    private int closeCalls;

    /// <summary>
    /// 创建驱动。
    /// </summary>
    /// <param name="open">按打开序号（从 1 开始）与取消令牌返回结果；为 null 时每次都成功。</param>
    public FakeConnectionDriver(Func<int, CancellationToken, Task<CommResult>>? open = null)
    {
        this.open = open ?? ((attempt, token) => Task.FromResult(CommResult.Success()));
    }

    /// <summary>OpenAsync 的调用次数。</summary>
    public int OpenCalls => Volatile.Read(ref openCalls);

    /// <summary>CloseAsync 的调用次数。</summary>
    public int CloseCalls => Volatile.Read(ref closeCalls);

    /// <summary>CloseAsync 收到的原因（按调用顺序）。</summary>
    public IReadOnlyList<DisconnectReason> CloseReasons
    {
        get
        {
            lock (sync)
                return closeReasons.ToList();
        }
    }

    /// <inheritdoc />
    public Task<CommResult> OpenAsync(CancellationToken cancellationToken)
    {
        int attempt = Interlocked.Increment(ref openCalls);
        return open(attempt, cancellationToken);
    }

    /// <inheritdoc />
    public Task CloseAsync(DisconnectReason reason, int drainTimeout)
    {
        Interlocked.Increment(ref closeCalls);
        lock (sync)
            closeReasons.Add(reason);

        return Task.CompletedTask;
    }

    /// <summary>成功结果的快捷方法。</summary>
    public static Task<CommResult> Succeed() => Task.FromResult(CommResult.Success());

    /// <summary>失败结果的快捷方法（连接被关闭）。</summary>
    public static Task<CommResult> Refuse() => Task.FromResult(CommResult.Fail("The scripted open failed.", CommErrorKind.ConnectionClosed));
}

/// <summary>
/// 一条状态事件的记录（派发时刻的时间戳用于测量区间）。
/// </summary>
internal sealed class StateEntry
{
    public StateEntry(ConnectionStateChangedEventArgs args, long timestamp)
    {
        Previous = args.PreviousState;
        Current = args.CurrentState;
        Reason = args.Reason;
        ReconnectAttempt = args.ReconnectAttempt;
        Timestamp = timestamp;
    }

    /// <summary>变化前的状态。</summary>
    public ConnectionState Previous { get; }

    /// <summary>变化后的状态。</summary>
    public ConnectionState Current { get; }

    /// <summary>原因。</summary>
    public DisconnectReason Reason { get; }

    /// <summary>重连尝试序号。</summary>
    public int ReconnectAttempt { get; }

    /// <summary>事件派发时的 <c>Stopwatch</c> 时间戳。</summary>
    public long Timestamp { get; }
}

/// <summary>
/// 记录监督器或通道的状态事件（按派发顺序）。
/// </summary>
internal sealed class StateRecorder
{
    private readonly object sync = new object();
    private readonly List<StateEntry> entries = new List<StateEntry>();

    /// <summary>订阅监督器的状态事件。</summary>
    /// <param name="supervisor">监督器。</param>
    public void Attach(ConnectionSupervisor supervisor)
    {
        supervisor.StateChanged += OnStateChanged;
    }

    /// <summary>已记录的全部事件（快照）。</summary>
    public IReadOnlyList<StateEntry> Entries
    {
        get
        {
            lock (sync)
                return entries.ToList();
        }
    }

    /// <summary>已记录的状态转换（From, To）。</summary>
    public List<(ConnectionState From, ConnectionState To)> Transitions()
        => Entries.Select(entry => (entry.Previous, entry.Current)).ToList();

    /// <summary>最近一条事件；没有时为 null。</summary>
    public StateEntry? Last
    {
        get
        {
            IReadOnlyList<StateEntry> snapshot = Entries;
            return snapshot.Count == 0 ? null : snapshot[snapshot.Count - 1];
        }
    }

    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        lock (sync)
            entries.Add(new StateEntry(args, System.Diagnostics.Stopwatch.GetTimestamp()));
    }
}

/// <summary>
/// 记录心跳监视器的死亡报告。
/// </summary>
internal sealed class DeathRecorder
{
    private readonly object sync = new object();
    private readonly List<DisconnectReason> reasons = new List<DisconnectReason>();

    /// <summary>作为死亡回调传入监视器。</summary>
    public void OnDead(DisconnectReason reason)
    {
        lock (sync)
            reasons.Add(reason);
    }

    /// <summary>报告次数。</summary>
    public int Count
    {
        get
        {
            lock (sync)
                return reasons.Count;
        }
    }

    /// <summary>报告的原因（按报告顺序）。</summary>
    public IReadOnlyList<DisconnectReason> Reasons
    {
        get
        {
            lock (sync)
                return reasons.ToList();
        }
    }
}

/// <summary>
/// 按脚本返回结果的心跳探测替身（第 n 次探测返回 <c>script(n)</c>）。
/// </summary>
internal sealed class ScriptedProbe : IHealthProbe
{
    private readonly Func<int, Task<CommResult>> script;
    private int calls;

    /// <summary>创建探测。</summary>
    /// <param name="script">按探测序号（从 1 开始）返回结果。</param>
    public ScriptedProbe(Func<int, Task<CommResult>> script)
    {
        this.script = script;
    }

    /// <summary>探测次数。</summary>
    public int Calls => Volatile.Read(ref calls);

    /// <inheritdoc />
    public Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
        => script(Interlocked.Increment(ref calls));
}

/// <summary>
/// 记录请求与发送、并返回预设结果的字节通道替身（供 <see cref="PayloadHeartbeatProbe"/> 测试使用）。
/// </summary>
internal sealed class FakeByteChannel : IByteChannel
{
    private readonly object sync = new object();
    private readonly List<byte[]> requests = new List<byte[]>();
    private int sendCount;

    /// <summary>下一次请求返回的结果。</summary>
    public CommResult<byte[]> NextReply { get; set; } = CommResult<byte[]>.Success(Array.Empty<byte>());

    /// <summary>收到的请求负载（按顺序）。</summary>
    public IReadOnlyList<byte[]> Requests
    {
        get
        {
            lock (sync)
                return requests.ToList();
        }
    }

    /// <summary>发送次数。</summary>
    public int SendCount => Volatile.Read(ref sendCount);

    /// <summary>最近一次请求的超时选项（毫秒）；没有请求时为 null。</summary>
    public int? LastRequestTimeout { get; private set; }

    /// <inheritdoc />
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref sendCount);
        return Task.FromResult(CommResult.Success());
    }

    /// <inheritdoc />
    public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                 CancellationToken cancellationToken = default)
    {
        lock (sync)
        {
            requests.Add(payload.ToArray());
            LastRequestTimeout = options?.Timeout;
        }

        return Task.FromResult(NextReply);
    }

    /// <inheritdoc />
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(CommResult<byte[]>.Fail("The fake channel does not receive.", CommErrorKind.NotSupported));

    /// <summary>本替身不派发事件。</summary>
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived
    {
        add
        {
        }

        remove
        {
        }
    }
}

/// <summary>
/// 委托实现的握手钩子（测试用）。
/// </summary>
internal sealed class DelegateInitializer : IConnectionInitializer
{
    private readonly Func<IByteChannel, CancellationToken, Task<CommResult>> run;

    /// <summary>创建初始化器。</summary>
    /// <param name="run">握手逻辑。</param>
    public DelegateInitializer(Func<IByteChannel, CancellationToken, Task<CommResult>> run)
    {
        this.run = run;
    }

    /// <inheritdoc />
    public Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
        => run(channel, cancellationToken);
}
