using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 可以把下一个应答挂起的测试通道：请求已经记录（并且已经到达通道），应答要等到 <see cref="Release"/> 才返回。
/// 用来在不阻塞线程池的前提下，让一个操作在操作锁内保持"在途"，再观察另一个操作是否被排队。
/// </summary>
internal sealed class GatedClientChannel : IClientChannel
{
    private readonly ScriptedClientChannel inner = new ScriptedClientChannel();
    private readonly object sync = new object();
    private TaskCompletionSource<bool>? gate;

    /// <summary>底层脚本通道，用于读取请求并排队应答。</summary>
    public ScriptedClientChannel Inner => inner;

    /// <summary>挂起下一个请求的应答，直到调用 <see cref="Release"/>。</summary>
    public void HoldNextReply()
    {
        lock (sync)
            gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>放开被挂起的应答。</summary>
    public void Release()
    {
        TaskCompletionSource<bool>? held;
        lock (sync)
        {
            held = gate;
            gate = null;
        }

        held?.TrySetResult(true);
    }

    /// <inheritdoc />
    public string Name => inner.Name;

    /// <inheritdoc />
    public ConnectionState State => inner.State;

    /// <inheritdoc />
    public bool IsConnected => inner.IsConnected;

    /// <inheritdoc />
    public ConnectionStatistics Statistics => inner.Statistics;

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged
    {
        add => inner.StateChanged += value;
        remove => inner.StateChanged -= value;
    }

    /// <inheritdoc />
    public Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => inner.DisconnectAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken = default)
        => inner.WaitForConnectedAsync(timeout, cancellationToken);

    /// <inheritdoc />
    public async Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                       CancellationToken cancellationToken = default)
    {
        CommResult<byte[]> reply = await inner.RequestAsync(payload, options, cancellationToken);

        TaskCompletionSource<bool>? held;
        lock (sync)
            held = gate;

        if (held is not null)
            await held.Task;

        return reply;
    }

    /// <inheritdoc />
    public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => inner.SendAsync(payload, cancellationToken);

    /// <inheritdoc />
    public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
        => inner.ReceiveAsync(options, cancellationToken);

    /// <inheritdoc />
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public void Dispose() => inner.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
