using System.Text;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.Logging.Abstractions;
using static Junevy.Communication.Tcp.Tests.TcpTestHelpers;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// TCP 创建器对普通 <see cref="ChannelComponents"/> 的复制：通过真实的回环连接验证分帧编解码与关联键确实生效。
/// 需要真实套接字，因此放在 SocketTiming 集合中。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class ChannelFactoryComponentsTests
{
    [Fact(Timeout = 30000)]
    public async Task TcpCreator_CopiesPlainChannelComponents()
    {
        using LoopbackServer server = LoopbackServer.Start((index, client, token) => EchoLinesAsync(client.GetStream(), token));
        var codec = new CountingCodecFactory(FrameCodecFactory.Create(LineFraming()));
        var keys = new FirstByteKeyExtractor();

        // 普通基类实例（不是 TcpChannelComponents）：只提供分帧编解码与 Keyed 关联。配置中的分帧为 Raw。
        var components = new ChannelComponents { FrameCodec = codec, Correlation = CorrelationMode.Keyed, KeyExtractor = keys };

        await using IClientChannel channel = new TcpClientChannelCreator().Create("tcp", CreateConfig(server.Port), components, NullLoggerFactory.Instance);
        CommResult connected = await channel.ConnectAsync();
        Assert.True(connected.IsSuccess, connected.ErrorMessage);

        CommResult<byte[]> reply = await channel.RequestAsync(Ascii("A-ping"));

        // 只有复制过去的行分帧生效时，应答才会去掉换行并交付为 "A-ping"；按配置的 Raw 分帧交付的会是 "A-ping\n"。
        Assert.True(reply.IsSuccess, reply.ErrorMessage);
        Assert.Equal("A-ping", Encoding.ASCII.GetString(reply.Data!));
        Assert.True(codec.DecoderCount > 0);
        Assert.True(keys.RequestKeyCalls > 0);
    }

    // 记录分帧器创建次数的编解码工厂，委托给内部工厂。
    private sealed class CountingCodecFactory : IFrameCodecFactory
    {
        private readonly IFrameCodecFactory inner;
        private int decoderCount;

        public CountingCodecFactory(IFrameCodecFactory inner)
        {
            this.inner = inner;
        }

        public int DecoderCount => Volatile.Read(ref decoderCount);

        public IFrameDecoder CreateDecoder()
        {
            Interlocked.Increment(ref decoderCount);
            return inner.CreateDecoder();
        }

        public IFrameEncoder CreateEncoder() => inner.CreateEncoder();
    }

    // 以首字节作为关联键：请求与应答的首字节相同即匹配。
    private sealed class FirstByteKeyExtractor : IFrameKeyExtractor
    {
        private int requestKeyCalls;

        public int RequestKeyCalls => Volatile.Read(ref requestKeyCalls);

        public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key)
        {
            Interlocked.Increment(ref requestKeyCalls);
            key = request.Length > 0 ? request[0] : 0;
            return request.Length > 0;
        }

        public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key)
        {
            key = frame.Length > 0 ? frame[0] : 0;
            return frame.Length > 0;
        }
    }
}
