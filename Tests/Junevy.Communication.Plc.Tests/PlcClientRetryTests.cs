using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 逐包重试规则（P2-D7）：每条规则至少一个用例。
/// </summary>
public sealed class PlcClientRetryTests
{
    [Fact(Timeout = 20000)]
    public async Task RemoteError_IsNotRetried_EvenWhenRetriesAreConfigured()
    {
        var channel = new ScriptedClientChannel { ReconnectOnWait = true };
        channel.Enqueue(CommResult<byte[]>.Fail("MC end code 0xC051.", CommErrorKind.RemoteError, 0xC051));
        var client = new FakePlcClient(channel, new PlcClientOptions { RetryCount = 2 });

        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(0xC051, result.ProtocolErrorCode);
        Assert.Single(channel.Requests);
        Assert.Empty(channel.WaitTimeouts);
    }

    [Fact(Timeout = 20000)]
    public async Task Timeout_WithRetryCountZero_IsNotRetried()
    {
        var channel = new ScriptedClientChannel { ReconnectOnWait = true };
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var client = new FakePlcClient(channel);

        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.Single(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task Timeout_LinkStillUp_RetriesAndSucceeds()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0x1234);
        var channel = new ScriptedClientChannel { ReconnectOnWait = true, Responder = request => CommResult<byte[]>.Success(device.Respond(request)) };
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var client = new FakePlcClient(channel, new PlcClientOptions { RetryCount = 2, RetryInterval = 250 });

        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(new byte[] { 0x34, 0x12 }, result.Data);
        Assert.Equal(2, channel.Requests.Count);
        Assert.Equal(new[] { 250 }, channel.WaitTimeouts);
    }

    [Fact(Timeout = 20000)]
    public async Task Timeout_LinkDoesNotRecoverWithinRetryInterval_ReturnsTheRealTimeout()
    {
        var channel = new ScriptedClientChannel { ReconnectOnWait = false };
        channel.EnqueueWithDisconnect(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var client = new FakePlcClient(channel, new PlcClientOptions { RetryCount = 2 });

        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.Equal("The request timed out.", result.ErrorMessage);
        Assert.Single(channel.Requests);
        Assert.Single(channel.WaitTimeouts);
    }

    [Fact(Timeout = 20000)]
    public async Task ProtocolViolation_IsRetried_WhenTheLinkStaysUp()
    {
        var device = new FakeDevice();
        device.SetWord('D', 100, 0x0001);
        var channel = new ScriptedClientChannel { Responder = request => CommResult<byte[]>.Success(device.Respond(request)) };
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00 }));
        var client = new FakePlcClient(channel, new PlcClientOptions { RetryCount = 1 });

        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, channel.Requests.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task RepeatedTimeouts_StopAfterRetryCount_ReturningTheLastFailure()
    {
        var channel = new ScriptedClientChannel { ReconnectOnWait = true };
        for (int i = 0; i < 3; i++)
            channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var client = new FakePlcClient(channel, new PlcClientOptions { RetryCount = 2 });

        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.Equal(3, channel.Requests.Count);
        Assert.Equal(2, channel.WaitTimeouts.Count);
    }

    [Fact(Timeout = 20000)]
    public async Task RetriedPacket_DoesNotRepeatThePacketsBeforeIt()
    {
        var device = new FakeDevice();
        var channel = new ScriptedClientChannel { ReconnectOnWait = true, Responder = request => CommResult<byte[]>.Success(device.Respond(request)) };
        channel.Enqueue(CommResult<byte[]>.Success(new byte[] { 0x00, 1, 0, 2, 0, 3, 0, 4, 0 }));
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var client = new FakePlcClient(channel, new PlcClientOptions { RetryCount = 1 });

        CommResult<byte[]> result = await client.ReadAsync("D100", 5);

        Assert.True(result.IsSuccess);
        // 第一包成功，只有第二包（D104，1 个字）被重试：请求总数为 3。
        Assert.Equal(3, channel.Requests.Count);
        Assert.Equal(104, FakeFrame.ReadHeader(channel.Requests[2]).Number);
    }
}
