using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 客户端生命周期：释放、选项校验与快照、连接成员的转发、协议钩子异常的转换。
/// </summary>
public sealed class PlcClientLifecycleTests
{
    [Fact(Timeout = 20000)]
    public void Dispose_ReleasesTheChannelOnce()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel);

        client.Dispose();
        client.Dispose();

        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact(Timeout = 20000)]
    public async Task DisposeAsync_ReleasesTheChannel()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel);

        await client.DisposeAsync();

        Assert.Equal(1, channel.DisposeCount);
    }

    [Fact(Timeout = 20000)]
    public async Task AfterDispose_OperationsReturnNotConnectedWithoutSending()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel);
        client.Dispose();

        CommResult<byte[]> read = await client.ReadAsync("D100", 1);
        CommResult<bool[]> bits = await client.ReadBitsAsync("M0", 1);

        Assert.Equal(CommErrorKind.NotConnected, read.ErrorKind);
        Assert.Equal("The PLC client has been disposed.", read.ErrorMessage);
        Assert.Equal(CommErrorKind.NotConnected, bits.ErrorKind);
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public void Constructor_NullArguments_ThrowArgumentNullException()
    {
        var channel = new ScriptedClientChannel();
        var options = new PlcClientOptions();
        var format = new PlcDataFormat(WordOrder.DCBA);
        var logger = new TestLogger();

        Assert.Throws<ArgumentNullException>(() => new RawClient(null!, options, format, logger));
        Assert.Throws<ArgumentNullException>(() => new RawClient(channel, null!, format, logger));
        Assert.Throws<ArgumentNullException>(() => new RawClient(channel, options, null!, logger));
        Assert.Throws<ArgumentNullException>(() => new RawClient(channel, options, format, null!));
    }

    [Theory(Timeout = 20000)]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void Constructor_NegativeRetrySettings_ThrowArgumentOutOfRangeException(int retryCount, int retryInterval)
    {
        var options = new PlcClientOptions { RetryCount = retryCount, RetryInterval = retryInterval };

        Assert.Throws<ArgumentOutOfRangeException>(() => new FakePlcClient(new ScriptedClientChannel(), options));
    }

    [Fact(Timeout = 20000)]
    public async Task Options_AreSnapshottedAtConstruction()
    {
        var options = new PlcClientOptions { RetryCount = 0 };
        var channel = new ScriptedClientChannel { ReconnectOnWait = true };
        channel.Enqueue(CommResult<byte[]>.Fail("The request timed out.", CommErrorKind.Timeout));
        var client = new FakePlcClient(channel, options);

        options.RetryCount = 5;
        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        Assert.Single(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public void DataFormat_IsTheFormatPassedToTheClient()
    {
        var format = new PlcDataFormat(WordOrder.ABCD);
        var client = new FakePlcClient(new ScriptedClientChannel(), dataFormat: format);

        Assert.Same(format, client.DataFormat);
    }

    [Fact(Timeout = 20000)]
    public void ConnectionMembers_AreForwardedToTheChannel()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel);

        Assert.Equal("scripted", client.Name);
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.True(client.IsConnected);
        Assert.Same(channel.Statistics, client.Statistics);
    }

    [Fact(Timeout = 20000)]
    public void StateChanged_SubscriptionIsForwardedToTheChannel()
    {
        var channel = new ScriptedClientChannel();
        var client = new FakePlcClient(channel);
        EventHandler<ConnectionStateChangedEventArgs> handler = (sender, args) => { };

        client.StateChanged += handler;
        Assert.True(channel.HasStateChangedSubscribers);

        client.StateChanged -= handler;
        Assert.False(channel.HasStateChangedSubscribers);
    }

    [Fact(Timeout = 20000)]
    public async Task ConnectDisconnectAndWait_AreForwardedToTheChannel()
    {
        var channel = new ScriptedClientChannel { Connected = false };
        var client = new FakePlcClient(channel);

        CommResult connect = await client.ConnectAsync();
        Assert.True(connect.IsSuccess);
        Assert.True(channel.Connected);

        await client.DisconnectAsync();
        Assert.False(channel.Connected);

        Assert.False(await client.WaitForConnectedAsync(0));
        Assert.Equal(new[] { 0 }, channel.WaitTimeouts);
    }

    [Fact(Timeout = 20000)]
    public async Task HookException_BecomesUnspecifiedFailure_AndIsLogged()
    {
        var channel = new ScriptedClientChannel();
        var logger = new TestLogger();
        var client = new ThrowingClient(channel, logger);

        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.Equal("The operation failed unexpectedly during ReadAsync.", result.ErrorMessage);
        Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.Single(logger.GetEntries(LogLevel.Error));
        Assert.Empty(channel.Requests);
    }

    [Fact(Timeout = 20000)]
    public async Task HookReturningNoPoints_IsAProtocolError_AndBecomesUnspecified()
    {
        var channel = new ScriptedClientChannel();
        var logger = new TestLogger();
        var client = new ZeroPointClient(channel, logger);

        CommResult<byte[]> result = await client.ReadAsync("D100", 1);

        Assert.Equal(CommErrorKind.Unspecified, result.ErrorKind);
        Assert.IsType<InvalidOperationException>(result.Exception);
        Assert.Empty(channel.Requests);
    }

    // 直接调用基类构造函数（不替换 null），用于参数校验测试。
    private sealed class RawClient : FakePlcClient
    {
        public RawClient(IClientChannel channel, PlcClientOptions options, PlcDataFormat dataFormat, ILogger logger)
            : base(FakePlcClient.DefaultMaxWords, FakePlcClient.DefaultMaxBits, channel, options, dataFormat, logger)
        {
        }
    }

    // 解析地址时的协议实现抛出异常（外部代码的故障）。
    private sealed class ThrowingClient : FakePlcClient
    {
        public ThrowingClient(IClientChannel channel, ILogger logger)
            : base(channel, logger: logger)
        {
        }

        protected internal override int GetMaxReadPoints(TestAddress address, PlcAccess access)
            => throw new InvalidOperationException("The test protocol failed.");
    }

    // 协议返回 0 点的单包上限：这是协议实现错误。
    private sealed class ZeroPointClient : FakePlcClient
    {
        public ZeroPointClient(IClientChannel channel, ILogger logger)
            : base(channel, logger: logger)
        {
        }

        protected internal override int GetMaxReadPoints(TestAddress address, PlcAccess access) => 0;
    }
}
