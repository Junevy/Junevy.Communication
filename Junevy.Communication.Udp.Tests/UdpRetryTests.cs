using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Udp.Tests;

/// <summary>请求超时后的原样重发（计划 15.2）：每次重发重新计时，全部超时后才进入迟到窗口。</summary>
[Collection(SocketTimingCollection.Name)]
public sealed class UdpRetryTests
{
    [Fact(Timeout = 30000)]
    public async Task RequestRetry_PeerIgnoresFirst_Succeeds()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) =>
            Task.FromResult<byte[]?>(index == 0 ? null : datagram.Buffer));
        var config = UdpTestConfig.Directed(peer, requestTimeout: 500);
        config.RequestRetryCount = 2;
        await using var channel = new UdpChannel(config);
        Assert.True((await channel.ConnectAsync()).IsSuccess);

        CommResult<byte[]> reply = await channel.RequestAsync(new byte[] { 5 });

        Assert.True(reply.IsSuccess, reply.ToString());
        Assert.Equal(new byte[] { 5 }, reply.Data);
        Assert.Equal(2, peer.ReceivedCount);
    }

    [Fact(Timeout = 30000)]
    public async Task RequestRetry_Exhausted_Timeout()
    {
        using ScriptedUdpPeer peer = ScriptedUdpPeer.Start((index, datagram) => Task.FromResult<byte[]?>(null));
        var config = UdpTestConfig.Directed(peer, requestTimeout: 300);
        config.RequestRetryCount = 2;
        await using var channel = new UdpChannel(config);
        Assert.True((await channel.ConnectAsync()).IsSuccess);
        CommResult<byte[]>? reply = null;

        // 期望约 (2 + 1) × 300 ms；调用方的返回时间还包含迟到窗口（默认等于 RequestTimeout，与 StreamChannel 的非重建路径一致），区间上限留出余量。
        await TimingAssert.WithinAsync(TimeSpan.FromMilliseconds(720), TimeSpan.FromMilliseconds(900 + 2000), async () =>
        {
            reply = await channel.RequestAsync(new byte[] { 1 });
        });

        Assert.NotNull(reply);
        Assert.Equal(CommErrorKind.Timeout, reply!.ErrorKind);
        Assert.Equal(3, peer.ReceivedCount);
    }
}
