using System.Net;
using System.Threading.Channels;
using Junevy.Communication.Channels.Pipeline;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 内存数据报传输（测试用）：入站数据报由测试注入，出站数据报被记录；发送时可以通过 <see cref="OnSend"/> 注入失败或同步回复。
/// </summary>
internal sealed class FakeDatagramTransport : IDatagramTransport
{
    private readonly Channel<DatagramReceipt> inbound = Channel.CreateUnbounded<DatagramReceipt>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object sync = new object();
    private readonly List<SentDatagram> sent = new List<SentDatagram>();
    private int abortCount;

    /// <summary>创建内存传输；<paramref name="directedRemote"/> 为 null 时等同于非定向模式。</summary>
    public FakeDatagramTransport(EndPoint? directedRemote)
    {
        DirectedRemote = directedRemote;
    }

    /// <inheritdoc />
    public EndPoint? LocalEndPoint => new IPEndPoint(IPAddress.Loopback, 5000);

    /// <inheritdoc />
    public EndPoint? DirectedRemote { get; }

    /// <summary>发送时调用的钩子：可抛出异常模拟发送失败，或同步注入应答。</summary>
    public Func<SentDatagram, Task>? OnSend { get; set; }

    /// <summary>中止调用次数。</summary>
    public int AbortCount => Volatile.Read(ref abortCount);

    /// <summary>已发送的数据报（按发送顺序）。</summary>
    public IReadOnlyList<SentDatagram> Sent
    {
        get
        {
            lock (sync)
            {
                return sent.ToArray();
            }
        }
    }

    /// <summary>注入一个入站数据报（来源为 <paramref name="remote"/>）。</summary>
    public void Deliver(byte[] data, EndPoint remote) => inbound.Writer.TryWrite(new DatagramReceipt(data, remote));

    /// <summary>让接收操作以异常结束（模拟接收循环出错）。</summary>
    public void FailReceive(Exception exception) => inbound.Writer.TryComplete(exception);

    /// <inheritdoc />
    public async Task<DatagramReceipt> ReceiveAsync(CancellationToken cancellationToken)
        => await inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task SendToAsync(ReadOnlyMemory<byte> payload, EndPoint destination, CancellationToken cancellationToken)
    {
        var datagram = new SentDatagram(payload.ToArray(), destination, DateTimeOffset.UtcNow);
        lock (sync)
        {
            sent.Add(datagram);
        }

        if (OnSend != null)
            await OnSend(datagram).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Abort()
    {
        Interlocked.Increment(ref abortCount);
        inbound.Writer.TryComplete(new ObjectDisposedException("The fake datagram transport was aborted."));
    }
}

/// <summary>一次出站数据报的记录。</summary>
internal sealed class SentDatagram
{
    public SentDatagram(byte[] payload, EndPoint destination, DateTimeOffset sentAt)
    {
        Payload = payload;
        Destination = destination;
        SentAt = sentAt;
    }

    /// <summary>负载副本。</summary>
    public byte[] Payload { get; }

    /// <summary>目标地址。</summary>
    public EndPoint Destination { get; }

    /// <summary>发出的时刻。</summary>
    public DateTimeOffset SentAt { get; }
}
