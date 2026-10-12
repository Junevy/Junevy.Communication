using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Mqtt.Tests.TestSupport;
using Junevy.Communication.Testing;
using SdkProtocolVersion = MQTTnet.Formatter.MqttProtocolVersion;

namespace Junevy.Communication.Mqtt.Tests;

[Collection(SocketTimingCollection.Name)]
public sealed class MqttConnectTests
{
    [Theory]
    [InlineData(MqttProtocolVersion.V311)]
    [InlineData(MqttProtocolVersion.V500)]
    public async Task Connect_BrokerSeesEveryConfiguredOption(MqttProtocolVersion version)
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, "connect-1", version);
        options.UserName = "user";
        options.Password = "secret";
        options.CleanSession = false;
        options.KeepAliveSeconds = 30;
        using var client = MqttClient.Create(options);

        await client.ConnectAndAssertAsync();

        Assert.Equal(MqttConnectionState.Connected, client.State);
        await Polling.WaitUntilAsync(() => broker.ConnectedClientIds.Count == 1);

        ObservedConnect connect = Assert.Single(broker.Connects);
        Assert.Equal("connect-1", connect.ClientId);
        Assert.False(connect.CleanSession);
        Assert.Equal((ushort)30, connect.KeepAliveSeconds);
        Assert.Equal("user", connect.UserName);
        Assert.Equal("secret", connect.Password);
        Assert.Equal(
            version == MqttProtocolVersion.V500 ? SdkProtocolVersion.V500 : SdkProtocolVersion.V311,
            broker.ConnectedProtocolVersions.Single());
    }

    [Fact]
    public async Task Connect_WalksDisconnectedToConnectingToConnected()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        var states = new List<MqttConnectionState>();
        client.StateChanged += (_, args) =>
        {
            lock (states)
            {
                states.Add(args.Current);
            }
        };

        await client.ConnectAndAssertAsync();

        Assert.Equal(new[] { MqttConnectionState.Connecting, MqttConnectionState.Connected }, states);
    }

    [Theory]
    [InlineData(MqttProtocolVersion.V311)]
    [InlineData(MqttProtocolVersion.V500)]
    public async Task Connect_BadCredentials_MapToAuthenticationFailedWithTheReasonCode(MqttProtocolVersion version)
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).WithCredentials("right", "right");
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port, version: version);
        options.UserName = "wrong";
        options.Password = "wrong";
        using var client = MqttClient.Create(options);

        CommResult result = await client.ConnectAsync();

        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal(134, result.ProtocolErrorCode);
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Connect_BadCredentials_DoesNotStartTheReconnectLoop()
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).WithCredentials("right", "right");
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port);
        options.UserName = "wrong";
        options.Password = "wrong";
        options.AutoReconnect = true;
        options.ReconnectInterval = 60;
        options.MaxReconnectInterval = 60;
        using var client = MqttClient.Create(options);

        Assert.False((await client.ConnectAsync()).IsSuccess);

        // 首次连接失败之后，三个重连间隔之内 Broker 不应再收到 CONNECT（P6-C11）。
        await Task.Delay(3 * 60);
        Assert.Single(broker.Connects);
    }

    [Fact]
    public async Task Connect_NoListenerOnThePort_ReportsConnectionClosed()
    {
        int port;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var client = MqttClient.Create(MqttTestOptions.Valid(port));

        CommResult result = await client.ConnectAsync();

        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Equal(MqttConnectionState.Disconnected, client.State);
    }

    [Fact]
    public async Task Connect_ServerAcceptsButNeverAnswers_ReportsTimeoutWithinTheConfiguredWindow()
    {
        using SilentTcpServer silent = SilentTcpServer.Start();
        MqttClientOptions options = MqttTestOptions.Valid(silent.Port);
        options.Timeout = 1000;
        using var client = MqttClient.Create(options);

        Stopwatch timer = Stopwatch.StartNew();
        CommResult result = await client.ConnectAsync();
        timer.Stop();

        Assert.Equal(CommErrorKind.Timeout, result.ErrorKind);
        // 下限取期望值的 80%，上限加 2000 ms（计划第 1 节第 7 条）。
        Assert.InRange(timer.ElapsedMilliseconds, 800, 1000 + 2000);
    }

    [Fact]
    public async Task Connect_CallerCancels_ReportsCancelled()
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).DelayConnectAck(TimeSpan.FromSeconds(5));
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        using var cancellation = new CancellationTokenSource(200);

        CommResult result = await client.ConnectAsync(cancellation.Token);

        Assert.Equal(CommErrorKind.Cancelled, result.ErrorKind);
    }

    [Fact]
    public async Task Connect_WhenAlreadyConnected_ReturnsSuccessWithoutConnectingAgain()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));
        await client.ConnectAndAssertAsync();

        CommResult second = await client.ConnectAsync();

        Assert.True(second.IsSuccess);
        Assert.Single(broker.ConnectedClientIds);
    }

    [Fact]
    public async Task Connect_WhileAnotherAttemptIsInProgress_ReportsInvalidRequest()
    {
        await using TestBroker broker = (await TestBroker.StartAsync()).DelayConnectAck(TimeSpan.FromMilliseconds(600));
        using var client = MqttClient.Create(MqttTestOptions.Valid(broker.Port));

        Task<CommResult> first = client.ConnectAsync();
        await Polling.WaitUntilAsync(() => client.State == MqttConnectionState.Connecting);
        CommResult second = await client.ConnectAsync();

        Assert.Equal(CommErrorKind.InvalidRequest, second.ErrorKind);
        Assert.Equal("A connection attempt is already in progress.", second.ErrorMessage);
        Assert.True((await first).IsSuccess);
    }

    [Fact]
    public void Create_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>("options", () => MqttClient.Create(null!));
    }

    [Fact]
    public async Task Create_InvalidOptions_ThrowsWithThePropertyName()
    {
        await using TestBroker broker = await TestBroker.StartAsync();
        MqttClientOptions options = MqttTestOptions.Valid(broker.Port);
        options.Port = 0;

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => MqttClient.Create(options));

        Assert.Equal("options.Port", error.ParamName);
    }
}