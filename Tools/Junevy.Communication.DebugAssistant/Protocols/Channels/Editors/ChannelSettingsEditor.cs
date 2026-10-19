using Junevy.Communication.Channels;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>
/// 通道参数的全部编辑器的组合（计划第 5.5 节）。传输页（TCP 客户端、UDP、串口、TCP 服务端）和 PLC 页共用它。
/// 协议拥有的分帧与关联（PLC 与 SECS 等）由协议页决定，编辑器中的对应部分不会被映射。
/// </summary>
public sealed class ChannelSettingsEditor
{
    public ChannelSettingsEditor()
    {
        Framing = new FramingEditor();
        Correlation = new CorrelationEditor();
        Heartbeat = new HeartbeatEditor();
        Reconnect = new ReconnectEditor();
        Timeouts = new TimeoutEditor();
        Socket = new SocketEditor();
        Tls = new TlsEditor();
        Handshake = new HandshakeEditor();
    }

    public FramingEditor Framing { get; }

    public CorrelationEditor Correlation { get; }

    public HeartbeatEditor Heartbeat { get; }

    public ReconnectEditor Reconnect { get; }

    public TimeoutEditor Timeouts { get; }

    public SocketEditor Socket { get; }

    public TlsEditor Tls { get; }

    public HandshakeEditor Handshake { get; }

    /// <summary>所有编辑器都没有校验错误。</summary>
    public bool IsValid => !Framing.HasErrors && !Correlation.HasErrors && !Heartbeat.HasErrors
        && !Reconnect.HasErrors && !Timeouts.HasErrors && !Socket.HasErrors;

    /// <summary>
    /// 生成库的通道部件：关联模式、键提取器与握手钩子（计划 DA-D12）。
    /// 匹配器不在这里：它按请求传入（<c>RequestOptions.Matcher</c>），见 <see cref="CorrelationEditor.ToMatchRule"/>。
    /// </summary>
    public ChannelComponents ToComponents()
    {
        var components = new ChannelComponents
        {
            Correlation = Correlation.Mode,
            Initializer = Handshake.ToInitializer(),
        };
        if (Correlation.Mode == CorrelationMode.Keyed)
        {
            components.KeyExtractor = Correlation.ToKeyExtractor();
        }

        return components;
    }
}
