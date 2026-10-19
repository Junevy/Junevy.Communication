using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>
/// 把通道参数编辑器映射为库的通道配置（计划第 5.5 节）。传输页签与 PLC 页签共用。
/// <c>protocolOwnsChannel</c> 为真时，分帧、关联与握手不写入配置：由协议包决定（PLC 页签）。
/// </summary>
internal static class ChannelConfigs
{
    public static TcpClientChannelConfig TcpClient(ChannelSettingsEditor s, string host, int port, string? localAddress, int localPort, bool protocolOwnsChannel = false) =>
        new()
        {
            Host = host,
            Port = port,
            LocalAddress = string.IsNullOrEmpty(localAddress) ? null : localAddress,
            LocalPort = localPort,
            ConnectTimeout = s.Timeouts.ConnectTimeout,
            HandshakeTimeout = s.Timeouts.HandshakeTimeout,
            SendTimeout = s.Timeouts.SendTimeout,
            RequestTimeout = s.Timeouts.RequestTimeout,
            IdleTimeout = s.Timeouts.IdleTimeout,
            PartialFrameTimeout = s.Timeouts.PartialFrameTimeout,
            DisconnectTimeout = s.Timeouts.DisconnectTimeout,
            ResetOnRequestTimeout = s.Timeouts.ResetOnRequestTimeout,
            LateReplyWindow = s.Timeouts.LateReplyWindow,
            Framing = protocolOwnsChannel ? new FramingOptions() : s.Framing.ToOptions(),
            Correlation = protocolOwnsChannel ? CorrelationMode.Sequential : s.Correlation.Mode,
            Heartbeat = s.Heartbeat.ToOptions(),
            Reconnect = s.Reconnect.ToOptions(),
            Socket = s.Socket.ToOptions(),
            Tls = s.Tls.ToOptions(),
        };

    public static UdpChannelConfig Udp(
        ChannelSettingsEditor s, string localAddress, int localPort, string? remoteHost, int remotePort,
        bool enableBroadcast, string[]? multicastGroups, int multicastTimeToLive, bool multicastLoopback, bool reuseAddress,
        int requestRetryCount, int maxDatagramSize, bool protocolOwnsChannel = false) =>
        new()
        {
            LocalAddress = localAddress,
            LocalPort = localPort,
            RemoteHost = remoteHost,
            RemotePort = remoteHost is null ? 0 : remotePort,
            EnableBroadcast = enableBroadcast,
            MulticastGroups = multicastGroups is { Length: > 0 } ? multicastGroups : null,
            MulticastTimeToLive = multicastTimeToLive,
            MulticastLoopback = multicastLoopback,
            ReuseAddress = reuseAddress,
            MaxDatagramSize = maxDatagramSize,
            HandshakeTimeout = s.Timeouts.HandshakeTimeout,
            DisconnectTimeout = s.Timeouts.DisconnectTimeout,
            SendTimeout = s.Timeouts.SendTimeout,
            RequestTimeout = s.Timeouts.RequestTimeout,
            RequestRetryCount = requestRetryCount,
            IdleTimeout = s.Timeouts.IdleTimeout,
            LateReplyWindow = s.Timeouts.LateReplyWindow,
            Correlation = protocolOwnsChannel ? CorrelationMode.Sequential : s.Correlation.Mode,
            Heartbeat = s.Heartbeat.ToOptions(),
            Reconnect = s.Reconnect.ToOptions(),
        };

    public static SerialChannelConfig Serial(
        ChannelSettingsEditor s, string portName, int baudRate, int dataBits, System.IO.Ports.Parity parity, System.IO.Ports.StopBits stopBits,
        System.IO.Ports.Handshake handshake, bool dtrEnable, bool rtsEnable, int readBufferSize, int writeBufferSize, int openTimeout,
        bool protocolOwnsChannel = false) =>
        new()
        {
            PortName = portName,
            BaudRate = baudRate,
            DataBits = dataBits,
            Parity = parity,
            StopBits = stopBits,
            Handshake = handshake,
            DtrEnable = dtrEnable,
            RtsEnable = rtsEnable,
            ReadBufferSize = readBufferSize,
            WriteBufferSize = writeBufferSize,
            OpenTimeout = openTimeout,
            HandshakeTimeout = s.Timeouts.HandshakeTimeout,
            DisconnectTimeout = s.Timeouts.DisconnectTimeout,
            SendTimeout = s.Timeouts.SendTimeout,
            RequestTimeout = s.Timeouts.RequestTimeout,
            IdleTimeout = s.Timeouts.IdleTimeout,
            PartialFrameTimeout = s.Timeouts.PartialFrameTimeout,
            LateReplyWindow = s.Timeouts.LateReplyWindow,
            Framing = protocolOwnsChannel ? new FramingOptions() : s.Framing.ToOptions(),
            Correlation = protocolOwnsChannel ? CorrelationMode.Sequential : s.Correlation.Mode,
            Heartbeat = s.Heartbeat.ToOptions(),
            Reconnect = s.Reconnect.ToOptions(),
        };
}
