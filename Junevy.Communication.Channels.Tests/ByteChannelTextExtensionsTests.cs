using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 文本扩展方法（<see cref="ByteChannelTextExtensions"/>）的测试：默认 UTF-8、分隔符只追加一次、失败结果原样透传、null 参数校验。
/// 大部分用例使用记录型替身通道；涉及分帧的用例使用真实的字节流客户端通道与内存双工流。
/// </summary>
public sealed class ByteChannelTextExtensionsTests
{
    [Fact(Timeout = 20000)]
    public async Task SendText_UsesUtf8ByDefault()
    {
        var channel = new RecordingTextChannel();
        const string text = "héllo€";

        CommResult sent = await channel.SendTextAsync(text);

        Assert.True(sent.IsSuccess, sent.ToString());
        byte[] payload = Assert.Single(channel.Sent);
        Assert.Equal(Encoding.UTF8.GetBytes(text), payload);
    }

    [Fact(Timeout = 20000)]
    public async Task SendText_WithDelimiterFraming_AppendsDelimiterOnce()
    {
        await using DuplexClientChannel channel = NewDelimitedChannel();
        Assert.True((await channel.ConnectAsync()).IsSuccess);
        DuplexStreamPair pair = channel.LatestPair!;

        CommResult sent = await channel.SendTextAsync("OK");

        Assert.True(sent.IsSuccess, sent.ToString());
        Assert.Equal("OK\r\n", Encoding.ASCII.GetString(await ReadExactlyAsync(pair.B, 4)));
        Assert.False(await HasMoreBytesAsync(pair.B, 300), "The delimiter must be appended exactly once.");
    }

    [Fact(Timeout = 20000)]
    public async Task RequestText_DecodesReply()
    {
        await using DuplexClientChannel channel = NewDelimitedChannel();
        Assert.True((await channel.ConnectAsync()).IsSuccess);
        DuplexStreamPair pair = channel.LatestPair!;

        // 对端读到一帧请求（应为 "PING" 加一次分隔符），回复 "PONG" 加分隔符。
        Task peer = Task.Run(async () =>
        {
            byte[] request = await ReadUntilAsync(pair.B, "\r\n");
            Assert.Equal("PING\r\n", Encoding.ASCII.GetString(request));
            byte[] reply = Encoding.ASCII.GetBytes("PONG\r\n");
            await pair.B.WriteAsync(reply, 0, reply.Length);
            await pair.B.FlushAsync();
        });

        CommResult<string> result = await channel.RequestTextAsync("PING");

        await peer;
        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("PONG", result.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task RequestText_PropagatesFailureKind()
    {
        var channel = new RecordingTextChannel();
        var inner = new InvalidOperationException("inner cause");
        channel.NextReply = CommResult<byte[]>.Fail("The peer answered with an unexpected frame.", CommErrorKind.ProtocolViolation, 0x51, inner);

        CommResult<string> result = await channel.RequestTextAsync("PING");

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Equal("The peer answered with an unexpected frame.", result.ErrorMessage);
        Assert.Equal(0x51L, result.ProtocolErrorCode);
        Assert.Same(inner, result.Exception);
    }

    [Fact(Timeout = 20000)]
    public async Task RequestText_OnRealChannel_ReportsNotConnectedAndTimeout()
    {
        await using DuplexClientChannel channel = NewDelimitedChannel(requestTimeout: 200);

        CommResult<string> notConnected = await channel.RequestTextAsync("PING");
        Assert.Equal(CommErrorKind.NotConnected, notConnected.ErrorKind);

        Assert.True((await channel.ConnectAsync()).IsSuccess);
        CommResult<string> timedOut = await channel.RequestTextAsync("PING");   // 对端不回复。
        Assert.Equal(CommErrorKind.Timeout, timedOut.ErrorKind);
        Assert.False(string.IsNullOrEmpty(timedOut.ErrorMessage));
    }

    [Fact(Timeout = 20000)]
    public async Task ReceiveText_UsesGivenEncoding()
    {
        var channel = new RecordingTextChannel { NextReply = CommResult<byte[]>.Success(Encoding.Unicode.GetBytes("hi")) };

        CommResult<string> withEncoding = await channel.ReceiveTextAsync(encoding: Encoding.Unicode);
        Assert.True(withEncoding.IsSuccess, withEncoding.ToString());
        Assert.Equal("hi", withEncoding.Data);

        // 默认编码为 UTF-8：同样的字节解码结果不同。
        CommResult<string> withDefault = await channel.ReceiveTextAsync();
        Assert.True(withDefault.IsSuccess, withDefault.ToString());
        Assert.NotEqual("hi", withDefault.Data);
    }

    [Fact(Timeout = 20000)]
    public async Task NullArguments_Throw()
    {
        IByteChannel? missing = null;
        var channel = new RecordingTextChannel();

        await Assert.ThrowsAsync<ArgumentNullException>(() => missing!.SendTextAsync("x"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => missing!.RequestTextAsync("x"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => missing!.ReceiveTextAsync());
        await Assert.ThrowsAsync<ArgumentNullException>(() => channel.SendTextAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => channel.RequestTextAsync(null!));
    }

    private static DuplexClientChannel NewDelimitedChannel(int requestTimeout = 2000)
        => new DuplexClientChannel(new ClientChannelSettings
        {
            HandshakeTimeout = 2000,
            SendTimeout = 2000,
            RequestTimeout = requestTimeout,
            LateReplyWindow = 0,
            DisconnectTimeout = 0,
            Framing = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = new[] { "\r\n" } },
        });

    // 读取恰好 count 个字节（net472 没有 Stream.ReadExactly）。
    private static async Task<byte[]> ReadExactlyAsync(Stream stream, int count)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer, offset, count - offset);
            if (read == 0)
                throw new EndOfStreamException("The stream ended before the expected bytes arrived.");

            offset += read;
        }

        return buffer;
    }

    // 逐字节读取，直到末尾出现 terminator；返回包含 terminator 的全部字节。
    private static async Task<byte[]> ReadUntilAsync(Stream stream, string terminator)
    {
        byte[] marker = Encoding.ASCII.GetBytes(terminator);
        var collected = new List<byte>();
        var single = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(single, 0, 1);
            if (read == 0)
                throw new EndOfStreamException("The stream ended before the terminator arrived.");

            collected.Add(single[0]);
            if (collected.Count >= marker.Length && collected.Skip(collected.Count - marker.Length).SequenceEqual(marker))
                return collected.ToArray();
        }
    }

    // 在 milliseconds 内是否还有字节到达；超时返回 false（挂起的读取留给测试结束时释放流）。
    private static async Task<bool> HasMoreBytesAsync(Stream stream, int milliseconds)
    {
        var single = new byte[1];
        Task<int> read = stream.ReadAsync(single, 0, 1);
        Task finished = await Task.WhenAny(read, Task.Delay(milliseconds));
        if (!ReferenceEquals(finished, read))
            return false;

        return await read > 0;
    }

    /// <summary>记录发送与请求的负载，并返回预设回复的替身通道。</summary>
    private sealed class RecordingTextChannel : IByteChannel
    {
        private readonly List<byte[]> sent = new List<byte[]>();

        /// <summary>按顺序记录的负载（发送与请求）。</summary>
        public IReadOnlyList<byte[]> Sent => sent;

        /// <summary>请求与接收返回的结果。</summary>
        public CommResult<byte[]> NextReply { get; set; } = CommResult<byte[]>.Success(Array.Empty<byte>());

        /// <inheritdoc />
        public event EventHandler<FrameReceivedEventArgs>? FrameReceived
        {
            add
            {
            }

            remove
            {
            }
        }

        /// <inheritdoc />
        public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            sent.Add(payload.ToArray());
            return Task.FromResult(CommResult.Success());
        }

        /// <inheritdoc />
        public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null,
                                                     CancellationToken cancellationToken = default)
        {
            sent.Add(payload.ToArray());
            return Task.FromResult(NextReply);
        }

        /// <inheritdoc />
        public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(NextReply);
    }
}
