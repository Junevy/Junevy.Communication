using System.IO.Ports;
using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;

/// <summary>TCP 客户端页签的参数（计划 DA-D11）。</summary>
public sealed record TcpClientSettings(string Host, int Port, string LocalAddress, int LocalPort, ChannelSettingsValues Channel)
    : SessionSettings;

/// <summary>UDP 页签的参数。<see cref="Directed"/> 为 true 时发送到 <see cref="RemoteHost"/>:<see cref="RemotePort"/>，否则用非定向模式发送到 <see cref="TargetHost"/>:<see cref="TargetPort"/>。</summary>
public sealed record UdpSettings(
    string LocalAddress,
    int LocalPort,
    bool Directed,
    string RemoteHost,
    int RemotePort,
    string TargetHost,
    int TargetPort,
    bool EnableBroadcast,
    string MulticastGroups,
    int MulticastTimeToLive,
    bool MulticastLoopback,
    bool ReuseAddress,
    int RequestRetryCount,
    int MaxDatagramSize,
    ChannelSettingsValues Channel)
    : SessionSettings;

/// <summary>串口页签的参数。<see cref="PortName"/> 只保存名称，不自动打开。</summary>
public sealed record SerialSettings(
    string PortName,
    int BaudRate,
    int DataBits,
    Parity Parity,
    StopBits StopBits,
    Handshake Handshake,
    bool DtrEnable,
    bool RtsEnable,
    int ReadBufferSize,
    int WriteBufferSize,
    int OpenTimeout,
    ChannelSettingsValues Channel)
    : SessionSettings;
