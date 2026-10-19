using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Settings;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>
/// TCP 服务端页的参数（计划第 7.13 节）：监听地址与端口、等待队列、会话数量与白名单、停止超时；
/// 以及每个会话使用的分帧、心跳、超时、套接字、握手与 TLS。映射到 <see cref="TcpServerConfig"/> 与 <see cref="TcpChannelComponents"/>。
/// </summary>
public sealed partial class TcpServerEditor : ValidatedEditor
{
    public TcpServerEditor()
    {
        Framing = new FramingEditor();
        Heartbeat = new HeartbeatEditor();
        Timeouts = new TimeoutEditor();
        Socket = new SocketEditor();
        Handshake = new HandshakeEditor();
        Tls = new ServerTlsEditor();
    }

    /// <summary>监听地址，0.0.0.0 表示所有本机地址。</summary>
    [ObservableProperty]
    private string _listenAddress = "0.0.0.0";

    [ObservableProperty]
    [Range(1, 65535, ErrorMessage = "端口必须在 1 到 65535 之间。")]
    private int _port = 5000;

    [ObservableProperty]
    [Range(1, 2048, ErrorMessage = "等待队列长度必须在 1 到 2048 之间。")]
    private int _backlog = 100;

    [ObservableProperty]
    [Range(0, 100000, ErrorMessage = "最大会话数必须在 0 到 100000 之间（0 表示不限）。")]
    private int _maxSessions;

    /// <summary>允许连接的远端地址，逗号或换行分隔；留空表示不限制。</summary>
    [ObservableProperty]
    private string _allowedRemoteAddresses = string.Empty;

    [ObservableProperty]
    [Range(0, 600000, ErrorMessage = "停止超时必须在 0 到 600000 毫秒之间。")]
    private int _stopTimeout = 3000;

    public FramingEditor Framing { get; }

    public HeartbeatEditor Heartbeat { get; }

    /// <summary>服务端使用其中的：会话握手、会话空闲、半帧、发送、请求、请求超时后重建、迟到应答窗口；连接与断开超时不适用。</summary>
    public TimeoutEditor Timeouts { get; }

    public SocketEditor Socket { get; }

    /// <summary>每个会话建立后执行的握手（计划 DA-D12）。</summary>
    public HandshakeEditor Handshake { get; }

    public ServerTlsEditor Tls { get; }

    /// <summary>所有编辑器都没有校验错误。</summary>
    public bool IsValid => !HasErrors && !Framing.HasErrors && !Heartbeat.HasErrors && !Timeouts.HasErrors
        && !Socket.HasErrors && !Handshake.HasErrors && !Tls.HasErrors;

    /// <summary>生成库的服务端配置。调用前应确认 <see cref="IsValid"/>。</summary>
    public TcpServerConfig ToConfig() => new()
    {
        ListenAddress = ListenAddress,
        Port = Port,
        Backlog = Backlog,
        MaxSessions = MaxSessions,
        AllowedRemoteAddresses = ParseAddresses(AllowedRemoteAddresses),
        StopTimeout = StopTimeout,
        SessionHandshakeTimeout = Timeouts.HandshakeTimeout,
        SessionIdleTimeout = Timeouts.IdleTimeout,
        PartialFrameTimeout = Timeouts.PartialFrameTimeout,
        SendTimeout = Timeouts.SendTimeout,
        RequestTimeout = Timeouts.RequestTimeout,
        ResetOnRequestTimeout = Timeouts.ResetOnRequestTimeout,
        LateReplyWindow = Timeouts.LateReplyWindow,
        Framing = Framing.ToOptions(),
        Heartbeat = Heartbeat.ToOptions(),
        Socket = Socket.ToOptions(),
        Tls = Tls.ToOptions(),
    };

    /// <summary>生成库的会话部件：握手钩子在每个会话建立前运行。</summary>
    public TcpChannelComponents ToComponents() => new() { Initializer = Handshake.ToInitializer() };

    /// <summary>保存所有参数（工作区与导出）：本页的字段放在 <c>server</c> 节，其余与通道参数相同。</summary>
    public ChannelSettingsValues Capture() => new(new Dictionary<string, Dictionary<string, string>>
    {
        ["server"] = EditorSnapshot.Capture(this),
        ["framing"] = EditorSnapshot.Capture(Framing),
        ["heartbeat"] = EditorSnapshot.Capture(Heartbeat),
        ["timeouts"] = EditorSnapshot.Capture(Timeouts),
        ["socket"] = EditorSnapshot.Capture(Socket),
        ["handshake"] = EditorSnapshot.Capture(Handshake),
        ["tls"] = EditorSnapshot.Capture(Tls),
    });

    /// <summary>恢复保存的参数；缺少的节保持默认值。</summary>
    public void Apply(ChannelSettingsValues values)
    {
        ApplySection(values, "server", this);
        ApplySection(values, "framing", Framing);
        ApplySection(values, "heartbeat", Heartbeat);
        ApplySection(values, "timeouts", Timeouts);
        ApplySection(values, "socket", Socket);
        ApplySection(values, "handshake", Handshake);
        ApplySection(values, "tls", Tls);
    }

    /// <summary>白名单：逗号或换行分隔，去掉空项；没有项时返回 null（不限制）。</summary>
    private static string[]? ParseAddresses(string text)
    {
        string[] items = text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Length == 0 ? null : items;
    }

    private static void ApplySection(ChannelSettingsValues values, string name, object editor)
    {
        if (values.Sections.TryGetValue(name, out Dictionary<string, string>? section))
        {
            EditorSnapshot.Apply(editor, section);
        }
    }
}
