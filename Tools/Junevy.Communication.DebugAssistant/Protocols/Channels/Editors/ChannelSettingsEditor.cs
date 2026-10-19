using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>通道类型：决定通道参数面板显示哪些部分。</summary>
public enum ChannelTransport
{
    /// <summary>TCP：分帧、关联、心跳、重连、超时、套接字与 TLS。</summary>
    Tcp,

    /// <summary>串口：分帧（默认空闲间隔）、关联、心跳、重连、超时。</summary>
    Serial,

    /// <summary>UDP：数据报没有分帧，也没有套接字与 TLS 选项。</summary>
    Udp,
}

/// <summary>
/// 通道参数的全部编辑器的组合（计划第 5.5 节）。传输页（TCP 客户端、UDP、串口）共用它；PLC 页与 TCP 服务端另有组合方式。
/// </summary>
public sealed class ChannelSettingsEditor
{
    /// <param name="transport">通道类型。</param>
    /// <param name="protocolOwnsChannelFraming">为真时分帧、关联与握手由协议决定（PLC 页签），界面不显示这些分组。</param>
    public ChannelSettingsEditor(ChannelTransport transport = ChannelTransport.Tcp, bool protocolOwnsChannelFraming = false)
    {
        Transport = transport;
        ProtocolOwnsChannelFraming = protocolOwnsChannelFraming;
        Framing = new FramingEditor();
        Correlation = new CorrelationEditor();
        Heartbeat = new HeartbeatEditor();
        Reconnect = new ReconnectEditor();
        Timeouts = new TimeoutEditor();
        Socket = new SocketEditor();
        Tls = new TlsEditor();
        Handshake = new HandshakeEditor();
        if (transport == ChannelTransport.Serial)
        {
            Framing.Mode = FramingMode.IdleGap;
        }
    }

    public ChannelTransport Transport { get; }

    /// <summary>分帧、关联与握手由协议决定（例如 PLC 协议）。</summary>
    public bool ProtocolOwnsChannelFraming { get; }

    /// <summary>数据报没有分帧（库会拒绝），串口与 TCP 有分帧；协议决定分帧时不显示。</summary>
    public bool ShowsFraming => Transport != ChannelTransport.Udp && !ProtocolOwnsChannelFraming;

    /// <summary>关联方式只在通道自己决定分帧时显示。</summary>
    public bool ShowsCorrelation => !ProtocolOwnsChannelFraming;

    /// <summary>连接后的握手只在通道自己决定时显示。</summary>
    public bool ShowsHandshake => !ProtocolOwnsChannelFraming;

    /// <summary>套接字与 TLS 只属于 TCP。</summary>
    public bool ShowsSocketAndTls => Transport == ChannelTransport.Tcp;

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
        && !Reconnect.HasErrors && !Timeouts.HasErrors && !Socket.HasErrors && !Tls.HasErrors && !Handshake.HasErrors;

    /// <summary>
    /// 生成库的通道部件：关联模式、键提取器与握手钩子（计划 DA-D12）。
    /// 匹配器不在这里：它按请求传入（<c>RequestOptions.Matcher</c>），见 <see cref="CorrelationEditor.ToMatchRule"/>。
    /// </summary>
    public TComponents ToComponents<TComponents>()
        where TComponents : ChannelComponents, new()
    {
        var components = new TComponents
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

    /// <summary>保存所有编辑器的参数（工作区与导出，计划 DA-D11）。</summary>
    public ChannelSettingsValues Capture() => new(new Dictionary<string, Dictionary<string, string>>
    {
        ["framing"] = EditorSnapshot.Capture(Framing),
        ["correlation"] = EditorSnapshot.Capture(Correlation),
        ["heartbeat"] = EditorSnapshot.Capture(Heartbeat),
        ["reconnect"] = EditorSnapshot.Capture(Reconnect),
        ["timeouts"] = EditorSnapshot.Capture(Timeouts),
        ["socket"] = EditorSnapshot.Capture(Socket),
        ["tls"] = EditorSnapshot.Capture(Tls),
        ["handshake"] = EditorSnapshot.Capture(Handshake),
    });

    /// <summary>恢复保存的参数；缺少的节保持默认值。</summary>
    public void Apply(ChannelSettingsValues values)
    {
        ApplySection(values, "framing", Framing);
        ApplySection(values, "correlation", Correlation);
        ApplySection(values, "heartbeat", Heartbeat);
        ApplySection(values, "reconnect", Reconnect);
        ApplySection(values, "timeouts", Timeouts);
        ApplySection(values, "socket", Socket);
        ApplySection(values, "tls", Tls);
        ApplySection(values, "handshake", Handshake);
    }

    private static void ApplySection(ChannelSettingsValues values, string name, object editor)
    {
        if (values.Sections.TryGetValue(name, out Dictionary<string, string>? section))
        {
            EditorSnapshot.Apply(editor, section);
        }
    }
}
