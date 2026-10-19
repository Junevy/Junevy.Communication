using System.IO;
using System.IO.Ports;
using System.Text.Json;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

/// <summary>
/// 传输页的参数经过工作区（导出）文件往返后保持不变（计划 Task 5，DA-D11）：每一节都设为非默认值，
/// 读回后的页签重新导出得到相同的 JSON。
/// </summary>
public sealed class ChannelSettingsRoundTripTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "junevy-da-channel-settings", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task TcpPage_EverySection_SurvivesTheWorkspaceFile()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.TcpPage(dispatcher, 5123);
        page.Host = "192.168.1.20";
        page.LocalAddress = "127.0.0.1";
        page.LocalPort = 6001;
        ChannelSettingsEditor s = page.Settings;
        s.Framing.Mode = FramingMode.Delimiter;
        s.Framing.Delimiters = "hex:0D 0A\nhex:00";
        s.Framing.KeepDelimiter = true;
        s.Framing.AppendDelimiterOnSend = false;
        s.Framing.MaxFrameLength = 4096;
        s.Correlation.Mode = CorrelationMode.Keyed;
        s.Correlation.RequestKeyOffset = 2;
        s.Correlation.RequestKeyWidth = 4;
        s.Correlation.RequestKeyOrder = KeyByteOrder.LittleEndian;
        s.Correlation.ResponseKeyOffset = 3;
        s.Correlation.ResponseKeyWidth = 1;
        s.Heartbeat.Enabled = true;
        s.Heartbeat.Payload = "hex:00";
        s.Heartbeat.ExpectedReply = "hex:01";
        s.Heartbeat.Interval = 2000;
        s.Heartbeat.Timeout = 500;
        s.Heartbeat.MaxFailures = 4;
        s.Heartbeat.OnlyWhenIdle = true;
        s.Reconnect.Enabled = true;
        s.Reconnect.Mode = ReconnectMode.FixedInterval;
        s.Reconnect.Interval = 3000;
        s.Reconnect.MaxInterval = 9000;
        s.Reconnect.MaxAttempts = 5;
        s.Reconnect.OnInitialFailure = true;
        s.Timeouts.RequestTimeout = 1500;
        s.Timeouts.LateReplyWindow = 800;
        s.Socket.NoDelay = false;
        s.Socket.ReceiveBufferSize = 8192;
        s.Socket.KeepAliveEnabled = true;
        s.Socket.KeepAliveTime = 7000;
        s.Socket.KeepAliveRetryCount = 7;
        s.Tls.Enabled = true;
        s.Tls.TargetHost = "plc.local";
        s.Tls.ClientCertificate = ClientCertificateKind.Pfx;
        s.Tls.PfxPath = @"C:\certs\client.pfx";
        s.Tls.PfxPasswordVariable = "CLIENT_PFX_PASSWORD";
        s.Handshake.Enabled = true;
        s.Handshake.Send = "hex:02";
        s.Handshake.ExpectReply = true;
        s.Handshake.ExpectedReply = "hex:06";

        await using var restored = ChannelTestKit.TcpPage(dispatcher, 1);
        RoundTrip(page, restored);

        Assert.Equal(5123, restored.Port);
        Assert.Equal("192.168.1.20", restored.Host);
        Assert.Equal(KeyByteOrder.LittleEndian, restored.Settings.Correlation.RequestKeyOrder);
        Assert.Equal(7, restored.Settings.Socket.KeepAliveRetryCount);
        Assert.Equal("CLIENT_PFX_PASSWORD", restored.Settings.Tls.PfxPasswordVariable);
    }

    [Fact]
    public async Task UdpPage_EveryField_SurvivesTheWorkspaceFile()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.UdpPage(dispatcher);
        page.LocalAddress = "0.0.0.0";
        page.LocalPort = 0;
        page.Directed = false;
        page.RemoteHost = "10.0.0.5";
        page.RemotePort = 4001;
        page.TargetHost = "10.0.0.9";
        page.TargetPort = 4002;
        page.EnableBroadcast = true;
        page.MulticastGroups = "239.1.1.1, 239.1.1.2";
        page.MulticastTimeToLive = 4;
        page.MulticastLoopback = true;
        page.ReuseAddress = true;
        page.RequestRetryCount = 2;
        page.MaxDatagramSize = 2048;
        page.Settings.Timeouts.RequestTimeout = 1200;

        await using var restored = ChannelTestKit.UdpPage(dispatcher);
        RoundTrip(page, restored);

        Assert.False(restored.Directed);
        Assert.Equal("10.0.0.9:4002", restored.Target);
        Assert.Equal("239.1.1.1, 239.1.1.2", restored.MulticastGroups);
        Assert.Equal(2, restored.RequestRetryCount);
        Assert.Equal(2048, restored.MaxDatagramSize);
        Assert.Equal(1200, restored.Settings.Timeouts.RequestTimeout);
    }

    [Fact]
    public async Task SerialPage_EveryField_SurvivesTheWorkspaceFile()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.SerialPage(dispatcher);
        page.PortName = "COM20";
        page.BaudRate = 115200;
        page.DataBits = 7;
        page.Parity = Parity.Even;
        page.StopBits = StopBits.Two;
        page.Handshake = Handshake.RequestToSend;
        page.DtrEnable = true;
        page.RtsEnable = true;
        page.ReadBufferSize = 8192;
        page.WriteBufferSize = 4096;
        page.OpenTimeout = 3000;
        page.Settings.Framing.GapTimeout = 35;
        page.Settings.Heartbeat.Enabled = true;
        page.Settings.Heartbeat.Payload = "hex:00";
        page.Settings.Heartbeat.ExpectedReply = "hex:01";

        await using var restored = ChannelTestKit.SerialPage(dispatcher);
        RoundTrip(page, restored);

        Assert.Equal("COM20", restored.PortName);
        Assert.Equal(115200, restored.BaudRate);
        Assert.Equal(Parity.Even, restored.Parity);
        Assert.Equal(35, restored.Settings.Framing.GapTimeout);
        Assert.Equal("hex:01", restored.Settings.Heartbeat.ExpectedReply);
    }

    [Fact]
    public async Task TcpServerPage_EverySection_SurvivesTheWorkspaceFile()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var page = ChannelTestKit.TcpServerPage(dispatcher, 5300);
        page.Settings.ListenAddress = "127.0.0.1";
        page.Settings.Backlog = 16;
        page.Settings.MaxSessions = 4;
        page.Settings.AllowedRemoteAddresses = "127.0.0.1, 10.0.0.1";
        page.Settings.StopTimeout = 1500;
        page.Settings.Framing.Mode = FramingMode.Delimiter;
        page.Settings.Framing.Delimiters = "hex:0D 0A";
        page.Settings.Heartbeat.Enabled = true;
        page.Settings.Heartbeat.Payload = "hex:00";
        page.Settings.Heartbeat.ExpectedReply = "hex:01";
        page.Settings.Heartbeat.Interval = 2000;
        page.Settings.Heartbeat.Timeout = 500;
        page.Settings.Heartbeat.MaxFailures = 4;
        page.Settings.Timeouts.HandshakeTimeout = 4000;
        page.Settings.Timeouts.IdleTimeout = 60000;
        page.Settings.Timeouts.PartialFrameTimeout = 900;
        page.Settings.Timeouts.SendTimeout = 1200;
        page.Settings.Timeouts.RequestTimeout = 1600;
        page.Settings.Timeouts.ResetOnRequestTimeout = false;
        page.Settings.Timeouts.LateReplyWindow = 500;
        page.Settings.Socket.KeepAliveEnabled = true;
        page.Settings.Handshake.Enabled = true;
        page.Settings.Handshake.Send = "hex:02";
        page.Settings.Handshake.ExpectReply = true;
        page.Settings.Handshake.ExpectedReply = "hex:06";
        page.Settings.Tls.Enabled = true;
        page.Settings.Tls.Certificate = ServerCertificateKind.Pfx;
        page.Settings.Tls.PfxPath = @"C:\certs\server.pfx";
        page.Settings.Tls.PfxPasswordVariable = "SERVER_PFX_PASSWORD";
        page.Settings.Tls.ClientCertificateRequired = true;
        page.Settings.Tls.CheckCertificateRevocation = false;
        page.AutoReplies.Add(new AutoReplyRule("hex:AA", "hex:BB", Echo: false));
        page.AutoReplies.Add(new AutoReplyRule(string.Empty, string.Empty, Echo: true));

        await using var restored = ChannelTestKit.TcpServerPage(dispatcher, 1);
        RoundTrip(page, restored);

        Assert.Equal(5300, restored.Settings.Port);
        Assert.Equal(4, restored.Settings.MaxSessions);
        Assert.Equal(ServerCertificateKind.Pfx, restored.Settings.Tls.Certificate);
        Assert.Equal(page.AutoReplies, restored.AutoReplies);
    }

    /// <summary>导出 <paramref name="source"/> 的参数到文件，读回后应用到 <paramref name="target"/>，并比较两边重新导出的 JSON。</summary>
    private void RoundTrip(SessionViewModel source, SessionViewModel target)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, $"{source.Kind}.json");

        WorkspaceService.WriteFile(path, WorkspaceService.ToSingleSessionDocument(source));
        WorkspaceSession saved = WorkspaceService.ReadFile(path).Sessions.Single();
        target.ApplySettings(saved.Settings!);

        Assert.Equal(Export(source), Export(target));
    }

    private static string Export(SessionViewModel session) =>
        JsonSerializer.Serialize<SessionSettings>(session.CaptureSettings(), WorkspaceJson.Options);
}
