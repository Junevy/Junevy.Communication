using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 逐包重试规则（P2-D7）在字节路径上的等价用例：规则与字访问完全相同，只是点与偏移按字节计。
/// </summary>
public sealed class PlcClientByteRetryTests
{
    [Fact(Timeout = 20000)]
    public async Task Timeout_LinkStillUp_RetriesAndSucceeds()
    {
        var device = new FakeDevice();
        device.SetByte('B', 100, 0x11);
        device.SetByte('B', 101, 0x22);
        var channel = new ScriptedClientChannel
        {
            ReconnectOnWait = true,
            Responder = request => CommResult<byte[]>.Success(device.Respond(request))
        };
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var client = new ByteFakePlcClient(channel, new PlcClientOptions { RetryCount = 2, RetryInterval = 250 });

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x11, 0x22 }, result.Data);
        Assert.Equal(2, channel.Requests.Count);
        Assert.Equal(new[] { 250 }, channel.WaitTimeouts);
    }

    [Fact(Timeout = 20000)]
    public async Task RemoteError_IsNotRetried_EvenWhenRetriesAreConfigured()
    {
        var channel = new ScriptedClientChannel { ReconnectOnWait = true };
        channel.Enqueue(CommResult<byte[]>.Fail("The device returned status 0xC1.", CommErrorKind.RemoteError, 0xC1));
        var client = new ByteFakePlcClient(channel, new PlcClientOptions { RetryCount = 2 });

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", 1);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(0xC1, result.ProtocolErrorCode);
        Assert.Single(channel.Requests);
        Assert.Empty(channel.WaitTimeouts);
    }

    [Fact(Timeout = 20000)]
    public async Task RetriedPacket_DoesNotRepeatThePacketsBeforeIt()
    {
        var device = new FakeDevice();
        var channel = new ScriptedClientChannel
        {
            ReconnectOnWait = true,
            Responder = request => CommResult<byte[]>.Success(device.Respond(request))
        };
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { FakeFrame.StatusOk, 1, 2, 3, 4, 5, 6 }));
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var client = new ByteFakePlcClient(channel, new PlcClientOptions { RetryCount = 1 });

        CommResult<byte[]> result = await client.ReadBytesAsync("B100", 7);

        Assert.True(result.IsSuccess);
        // 第一包（6 个字节）成功，只有第二包（B106，1 个字节）被重试：请求总数为 3。
        Assert.Equal(3, channel.Requests.Count);
        Assert.Equal(106, FakeFrame.ReadHeader(channel.Requests[2]).Number);
        Assert.Equal(1, FakeFrame.ReadHeader(channel.Requests[2]).Count);
    }

    [Fact(Timeout = 20000)]
    public async Task WriteBytesAsync_Timeout_LinkDoesNotRecoverWithinRetryInterval_ReturnsTheRealTimeout()
    {
        var channel = new ScriptedClientChannel { ReconnectOnWait = false };
        channel.EnqueueWithDisconnect(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var client = new ByteFakePlcClient(channel, new PlcClientOptions { RetryCount = 2 });

        CommResult result = await client.WriteBytesAsync("B100", new byte[] { 1, 2 });

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.Equal("The request timed out.", result.ErrorMessage);
        Assert.Single(channel.Requests);
        Assert.Single(channel.WaitTimeouts);
    }
}
