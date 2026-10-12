using System.Collections.Concurrent;
using System.Net;
using Junevy.Communication.Testing;
using MQTTnet;
using MQTTnet.Packets;
using MQTTnet.Protocol;
using MQTTnet.Server;
using SdkProtocolVersion = MQTTnet.Formatter.MqttProtocolVersion;

namespace Junevy.Communication.Mqtt.Tests.TestSupport;

/// <summary>
/// 进程内 MQTT Broker 的封装（计划第 7 节第 3 条、P6-C12）：起停、可选 TLS、凭据校验、持久会话、
/// 从 Broker 侧注入消息、强制断开客户端、记录收到的报文、延迟 CONNECT 的应答、拒绝发布。
/// 全部走 MQTTnet.Server 的公开 API，用例不需要直接接触 SDK 的事件类型。
/// </summary>
public sealed class TestBroker : IAsyncDisposable
{
private readonly MqttServer server;
    private readonly ConcurrentQueue<ObservedConnect> connects = new();
    private readonly ConcurrentQueue<SdkProtocolVersion> protocolVersions = new();
    private readonly ConcurrentQueue<ObservedSubscription> subscriptions = new();
    private readonly ConcurrentQueue<string> clientIds = new();
    private readonly TestCertificate? certificate;
    private readonly bool ownsCertificate;
    private TimeSpan connectDelay = TimeSpan.Zero;
    private MqttPubAckReasonCode? publishRejection;
    private bool disposed;

    private TestBroker(MqttServerOptions options, int port, TestCertificate? certificate, bool ownsCertificate)
    {
        Port = port;
        this.certificate = certificate;
        this.ownsCertificate = ownsCertificate;

        server = new MqttServerFactory().CreateMqttServer(options);
        server.ValidatingConnectionAsync += OnValidatingConnectionAsync;
        server.ClientConnectedAsync += OnClientConnectedAsync;
        server.ClientSubscribedTopicAsync += OnClientSubscribedTopicAsync;
        server.InterceptingPublishAsync += OnInterceptingPublishAsync;
    }

    /// <summary>Broker 监听的端口。</summary>
    public int Port { get; }

    /// <summary>期望的用户名；为 null 时不做凭据校验。</summary>
    public string? ExpectedUserName { get; private set; }

    /// <summary>期望的密码。</summary>
    public string? ExpectedPassword { get; private set; }

    /// <summary>Broker 收到的 CONNECT，按到达顺序。凭据校验发生在握手之前，因此这里包含后来被拒绝的那些。</summary>
    public IReadOnlyList<ObservedConnect> Connects => connects.ToArray();

    /// <summary>Broker 处理过的 SUBSCRIBE，按到达顺序。</summary>
    public IReadOnlyList<ObservedSubscription> Subscriptions => subscriptions.ToArray();

    /// <summary>完成过 CONNECT 的客户端标识，按到达顺序。</summary>
    public IReadOnlyList<string> ConnectedClientIds => clientIds.ToArray();

    /// <summary>成功连接的客户端使用的协议版本，按到达顺序。CONNECT 报文本身不带版本，只有握手层知道。</summary>
    public IReadOnlyList<SdkProtocolVersion> ConnectedProtocolVersions => protocolVersions.ToArray();

    /// <summary>当前仍连在 Broker 上的客户端标识。</summary>
    /// <returns>在线客户端的标识。</returns>
    public async Task<IReadOnlyList<string>> ActiveClientIdsAsync() =>
        (await server.GetClientsAsync()).Select(client => client.Id).ToArray();

/// <summary>起一个普通 Broker：回环地址 + 随机端口。</summary>
    /// <param name="persistentSessions">是否保留会话（客户端用 CleanSession = false 时需要）。</param>
    /// <returns>已启动的 Broker。</returns>
    public static async Task<TestBroker> StartAsync(bool persistentSessions = false)
    {
        int port = FreePort();
        MqttServerOptions options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(port)
            .WithPersistentSessions(persistentSessions)
            .Build();

        var broker = new TestBroker(options, port, certificate: null, ownsCertificate: false);
        await broker.server.StartAsync();
        return broker;
    }

    /// <summary>起一个使用自签名证书的 TLS Broker。</summary>
    /// <param name="persistentSessions">是否保留会话。</param>
    /// <returns>已启动的 Broker。</returns>
    public static async Task<TestBroker> StartTlsAsync(bool persistentSessions = false)
    {
        int port = FreePort();
        TestCertificate certificate = TestCertificates.CreateSelfSigned();
        MqttServerOptions options = new MqttServerOptionsBuilder()
            .WithoutDefaultEndpoint()
            .WithEncryptedEndpoint()
            .WithEncryptedEndpointBoundIPAddress(IPAddress.Loopback)
            .WithEncryptedEndpointPort(port)
            .WithEncryptionCertificate(certificate.Certificate)
            .WithPersistentSessions(persistentSessions)
            .Build();

        var broker = new TestBroker(options, port, certificate, ownsCertificate: true);
        await broker.server.StartAsync();
        return broker;
    }

    // 先向操作系统要一个空闲端口再停掉监听器：MQTTnet 的服务端不会把实际绑定的端口写回选项对象。
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>要求 CONNECT 带上给定的凭据；不符时以 <c>BadUserNameOrPassword</c> 拒绝。</summary>
    /// <param name="userName">期望的用户名。</param>
    /// <param name="password">期望的密码。</param>
    /// <returns>同一个 Broker。</returns>
    public TestBroker WithCredentials(string userName, string password)
    {
        ExpectedUserName = userName;
        ExpectedPassword = password;
        return this;
    }

    /// <summary>把 CONNECT 的应答延迟一段时间，用于验证"连接进行中调用 DisconnectAsync"。</summary>
    /// <param name="delay">延迟时长。</param>
    /// <returns>同一个 Broker。</returns>
    public TestBroker DelayConnectAck(TimeSpan delay)
    {
        connectDelay = delay;
        return this;
    }

    /// <summary>拒绝下一次发布：PUBACK 带上失败原因码（仅 MQTT 5 有效）。</summary>
    /// <param name="reasonCode">要返回的原因码；仅下一次生效。</param>
    /// <returns>同一个 Broker。</returns>
    public TestBroker RejectNextPublish(MqttPubAckReasonCode reasonCode)
    {
        publishRejection = reasonCode;
        return this;
    }

    /// <summary>从 Broker 侧向主题注入一条消息。</summary>
    /// <param name="topic">主题。</param>
    /// <param name="payload">正文。</param>
    /// <param name="retain">是否为保留消息。</param>
    /// <param name="qos">QoS。</param>
    /// <returns>注入完成。</returns>
    public Task InjectAsync(
        string topic,
        string payload,
        bool retain = false,
        MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtMostOnce)
    {
        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .WithQualityOfServiceLevel(qos)
            .Build();

        return server.InjectApplicationMessage(
            new InjectedMqttApplicationMessage(message),
            CancellationToken.None);
    }

    /// <summary>强制断开某个客户端。Broker 侧发起的断开，按 MQTT 的规定会发布该客户端的遗嘱。</summary>
    /// <param name="clientId">客户端标识。</param>
    /// <param name="reasonCode">DISCONNECT 的原因码；默认 <c>DisconnectWithWillMessage</c>（4）。</param>
    /// <returns>断开完成。</returns>
    public Task DisconnectClientAsync(
        string clientId,
        MqttDisconnectReasonCode reasonCode = MqttDisconnectReasonCode.DisconnectWithWillMessage) =>
        server.DisconnectClientAsync(
            clientId,
            new MqttServerClientDisconnectOptionsBuilder().WithReasonCode(reasonCode).Build());

    /// <summary>停止 Broker。</summary>
    /// <returns>停止完成。</returns>
    public async Task StopAsync()
    {
        if (server.IsStarted)
        {
            await server.StopAsync(new MqttServerStopOptions());
        }
    }

    /// <summary>停止 Broker 并释放证书。</summary>
    /// <returns>释放完成。</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopAsync();
        if (ownsCertificate)
        {
            certificate!.Dispose();
        }
    }

private async Task OnValidatingConnectionAsync(ValidatingConnectionEventArgs args)
    {
        // 这里就是服务端看到的 CONNECT：凭据校验发生在 CONNACK 之前，因此先记录再判定。
        // 遗嘱的字段不在这个事件上暴露，遗嘱改由"订阅方收到遗嘱"那条端到端用例验证（计划第 8 节）。
        connects.Enqueue(new ObservedConnect(
            args.ClientId,
            args.UserName,
            args.Password,
            args.CleanSession,
            args.KeepAlivePeriod,
            args.SessionExpiryInterval,
            args.WillDelayInterval));

        // 延迟 CONNECT 的应答：让"连接进行中"这个窗口在测试里可复现。
        if (connectDelay > TimeSpan.Zero)
        {
            await Task.Delay(connectDelay);
        }

        if (ExpectedUserName is not null && (args.UserName != ExpectedUserName || args.Password != ExpectedPassword))
        {
            args.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            args.ReasonString = "The user name or the password is not correct.";
        }
    }

    private Task OnClientConnectedAsync(ClientConnectedEventArgs args)
    {
        clientIds.Enqueue(args.ClientId);
        protocolVersions.Enqueue(args.ProtocolVersion);
        return Task.CompletedTask;
    }

    private Task OnClientSubscribedTopicAsync(ClientSubscribedTopicEventArgs args)
    {
        subscriptions.Enqueue(new ObservedSubscription(args.ClientId, args.TopicFilter.Topic, (int)args.TopicFilter.QualityOfServiceLevel));
        return Task.CompletedTask;
    }

    private Task OnInterceptingPublishAsync(InterceptingPublishEventArgs args)
    {
        MqttPubAckReasonCode? rejection = publishRejection;
        if (rejection is not null)
        {
            publishRejection = null;
            args.ProcessPublish = false;
            args.Response.ReasonCode = rejection.Value;
            args.Response.ReasonString = "Rejected by the test broker.";
        }

        return Task.CompletedTask;
    }
}
/// <summary>Broker 看到的一次 CONNECT。CONNECT 报文的字段在这里被摊平成 SDK 无关的类型。</summary>
/// <param name="ClientId">客户端标识。</param>
/// <param name="UserName">用户名。</param>
/// <param name="Password">密码。</param>
/// <param name="CleanSession">Clean Session / Clean Start。</param>
/// <param name="KeepAliveSeconds">保活周期（秒）。</param>
/// <param name="SessionExpiryInterval">会话过期（秒）。</param>
/// <param name="WillDelayInterval">遗嘱延迟（秒）。</param>
public sealed record ObservedConnect(
    string ClientId,
    string? UserName,
    string? Password,
    bool? CleanSession,
    ushort? KeepAliveSeconds,
    uint SessionExpiryInterval,
    uint WillDelayInterval);

/// <summary>Broker 处理过的一次 SUBSCRIBE。</summary>
/// <param name="ClientId">客户端标识。</param>
/// <param name="Topic">主题过滤器。</param>
/// <param name="Qos">请求的 QoS。</param>
public sealed record ObservedSubscription(string ClientId, string Topic, int Qos);
