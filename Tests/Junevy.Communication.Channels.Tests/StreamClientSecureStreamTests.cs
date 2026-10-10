using System.Diagnostics;
using Junevy.Communication.Core.Results;
using static Junevy.Communication.Channels.Tests.RouterTestHelpers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// <see cref="StreamClientChannel.SecureStreamAsync"/> 钩子（TLS 的扩展点）的测试：握手总时限由包装与初始化器共享、
/// 通道使用包装后的流、包装失败使打开失败。使用 <see cref="DuplexClientChannel"/>（内存双工流），不使用套接字。
/// </summary>
public sealed class StreamClientSecureStreamTests
{
    [Fact(Timeout = 20000)]
    public async Task SecureStream_SharesHandshakeTimeoutWithInitializer()
    {
        // 包装 300 ms + 初始化器 300 ms，握手时限 500 ms：两者合计超出时限，打开以 Timeout 失败（设计文档第 10 节）。
        var initializer = new DelegateInitializer(async (view, token) =>
        {
            await Task.Delay(300, token);
            return CommResult.Success();
        });
        var channel = new DuplexClientChannel(Settings(handshakeTimeout: 500), new ChannelComponents { Initializer = initializer },
                                              onSecure: async (stream, token) =>
                                              {
                                                  await Task.Delay(300, token);
                                                  return CommResult<Stream>.Success(stream);
                                              });
        await using var owned = channel;

        var stopwatch = Stopwatch.StartNew();
        CommResult result = await WithinAsync(channel.ConnectAsync(), 4000);
        stopwatch.Stop();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.InRange(stopwatch.Elapsed.TotalMilliseconds, 400d, 2300d);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.True(channel.AbortCount >= 1, "The transport must be aborted after a handshake timeout.");
    }

    [Fact(Timeout = 20000)]
    public async Task SecureStream_ReplacesStream()
    {
        CountingStream? wrapper = null;
        var channel = new DuplexClientChannel(Settings(), null, onSecure: (stream, token) =>
        {
            wrapper = new CountingStream(stream);
            return Task.FromResult(CommResult<Stream>.Success(wrapper));
        });
        await using var owned = channel;

        CommResult connected = await WithinAsync(channel.ConnectAsync(), 3000);
        Assert.True(connected.IsSuccess, connected.ToString());
        Assert.NotNull(wrapper);

        // 发送：对端收到的字节经过包装流写出。
        CommResult sent = await WithinAsync(channel.SendAsync(Ascii("PING")), 3000);
        Assert.True(sent.IsSuccess, sent.ToString());
        var received = new byte[4];
        await ReadExactlyAsync(channel.LatestPair!.B, received);
        Assert.Equal(Ascii("PING"), received);
        Assert.True(wrapper!.BytesWritten >= 4, $"Only {wrapper.BytesWritten} bytes went through the wrapper.");

        // 接收：对端写入的字节经包装流读入。先登记接收等待者，避免帧在无人认领时被派发丢弃。
        Task<CommResult<byte[]>> receiving = channel.ReceiveAsync(new RequestOptions { Timeout = 2000 });
        await channel.LatestPair.B.WriteAsync(Ascii("PONG"), 0, 4);
        CommResult<byte[]> reply = await WithinAsync(receiving, 3000);
        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(Ascii("PONG"), reply.Data);
        Assert.True(wrapper.BytesRead >= 4, $"Only {wrapper.BytesRead} bytes came through the wrapper.");
    }

    [Fact(Timeout = 20000)]
    public async Task SecureStream_Failure_FailsOpen()
    {
        var channel = new DuplexClientChannel(Settings(), null, onSecure: (stream, token) =>
            Task.FromResult(CommResult<Stream>.Fail("The secure wrapper failed in the test.", CommErrorKind.AuthenticationFailed)));
        await using var owned = channel;

        CommResult result = await WithinAsync(channel.ConnectAsync(), 3000);

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal(ConnectionState.Disconnected, channel.State);
        Assert.Equal(1, channel.OpenCount);
        Assert.True(channel.AbortCount >= 1, "The transport must be aborted when the secure step fails.");
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        int offset = 0;
        using var cts = new CancellationTokenSource(3000);
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cts.Token);
            if (read == 0)
                throw new EndOfStreamException("The peer closed the stream early.");

            offset += read;
        }
    }

    private static ClientChannelSettings Settings(int handshakeTimeout = 0)
        => new ClientChannelSettings
        {
            HandshakeTimeout = handshakeTimeout,
            SendTimeout = 2000,
            RequestTimeout = 2000,
            DisconnectTimeout = 0,
            Framing = new FramingOptions { Mode = FramingMode.Raw },
        };
}

/// <summary>
/// 透传包装流：统计经过它的读写字节数，用于证明通道使用的是包装后的流（测试用）。
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly Stream inner;
    private long bytesRead;
    private long bytesWritten;

    /// <summary>创建包装流。</summary>
    /// <param name="inner">被包装的流。</param>
    public CountingStream(Stream inner)
    {
        this.inner = inner;
    }

    /// <summary>经过包装流读入的字节数。</summary>
    public long BytesRead => Interlocked.Read(ref bytesRead);

    /// <summary>经过包装流写出的字节数。</summary>
    public long BytesWritten => Interlocked.Read(ref bytesWritten);

    /// <inheritdoc />
    public override bool CanRead => inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => inner.CanWrite;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);
        Interlocked.Add(ref bytesRead, read);
        return read;
    }

    /// <inheritdoc />
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        int read = await inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref bytesRead, read);
        return read;
    }

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
        Interlocked.Add(ref bytesWritten, count);
    }

    /// <inheritdoc />
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await inner.WriteAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref bytesWritten, count);
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();
}
