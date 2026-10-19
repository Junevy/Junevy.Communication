using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Workspace;
using Junevy.Communication.Tcp;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>TCP 客户端页（计划第 7.13 节）：连接参数、分帧、关联、心跳、重连、超时、套接字与 TLS。</summary>
public sealed partial class TcpClientSessionViewModel : ChannelSessionViewModel
{
    public TcpClientSessionViewModel(ProtocolDescriptor descriptor, SessionServices dependencies)
        : base(descriptor, dependencies, new ChannelSettingsEditor(ChannelTransport.Tcp))
    {
    }

    [ObservableProperty]
    private string _host = "127.0.0.1";

    [ObservableProperty]
    private int _port = 5000;

    /// <summary>本地地址，留空由系统选择。</summary>
    [ObservableProperty]
    private string _localAddress = string.Empty;

    /// <summary>本地端口，0 表示由系统选择。</summary>
    [ObservableProperty]
    private int _localPort;

    public override string Target => $"{Host}:{Port}";

    protected override IClientChannel CreateChannel() => new TcpClientChannel(
        BuildConfig(),
        new Logger<TcpClientChannel>(SessionLoggerFactory),
        Settings.ToComponents<TcpChannelComponents>());

    public override SessionSettings CaptureSettings() => new TcpClientSettings(Host, Port, LocalAddress, LocalPort, Settings.Capture());

    public override void ApplySettings(SessionSettings settings)
    {
        if (settings is not TcpClientSettings saved)
        {
            throw new ArgumentException("The settings do not belong to a TCP client page.", nameof(settings));
        }

        Host = saved.Host;
        Port = saved.Port;
        LocalAddress = saved.LocalAddress;
        LocalPort = saved.LocalPort;
        Settings.Apply(saved.Channel);
    }

    private TcpClientChannelConfig BuildConfig()
    {
        ChannelSettingsEditor s = Settings;
        return new TcpClientChannelConfig
        {
            Host = Host,
            Port = Port,
            LocalAddress = LocalAddress.Length == 0 ? null : LocalAddress,
            LocalPort = LocalPort,
            ConnectTimeout = s.Timeouts.ConnectTimeout,
            HandshakeTimeout = s.Timeouts.HandshakeTimeout,
            SendTimeout = s.Timeouts.SendTimeout,
            RequestTimeout = s.Timeouts.RequestTimeout,
            IdleTimeout = s.Timeouts.IdleTimeout,
            PartialFrameTimeout = s.Timeouts.PartialFrameTimeout,
            DisconnectTimeout = s.Timeouts.DisconnectTimeout,
            ResetOnRequestTimeout = s.Timeouts.ResetOnRequestTimeout,
            LateReplyWindow = s.Timeouts.LateReplyWindow,
            Framing = s.Framing.ToOptions(),
            Correlation = s.Correlation.Mode,
            Heartbeat = s.Heartbeat.ToOptions(),
            Reconnect = s.Reconnect.ToOptions(),
            Socket = s.Socket.ToOptions(),
            Tls = s.Tls.ToOptions(),
        };
    }
}
