using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>
/// 超时编辑器（计划第 7.13 节）。字段与 <c>TcpClientChannelConfig</c> 的超时属性一一对应，由传输页映射。
/// 0 表示关闭的超时（<see cref="IdleTimeout"/>、<see cref="PartialFrameTimeout"/>），-1 表示库的默认值（<see cref="LateReplyWindow"/>）。
/// </summary>
public sealed partial class TimeoutEditor : ValidatedEditor
{
    [ObservableProperty]
    [Range(1, 600000, ErrorMessage = "连接超时必须在 1 到 600000 毫秒之间。")]
    private int _connectTimeout = 2000;

    [ObservableProperty]
    [Range(1, 600000, ErrorMessage = "握手超时必须在 1 到 600000 毫秒之间。")]
    private int _handshakeTimeout = 5000;

    [ObservableProperty]
    [Range(1, 600000, ErrorMessage = "发送超时必须在 1 到 600000 毫秒之间。")]
    private int _sendTimeout = 2000;

    [ObservableProperty]
    [Range(1, 600000, ErrorMessage = "请求超时必须在 1 到 600000 毫秒之间。")]
    private int _requestTimeout = 2000;

    [ObservableProperty]
    [Range(0, 86400000, ErrorMessage = "空闲超时必须在 0 到 86400000 毫秒之间（0 表示关闭）。")]
    private int _idleTimeout;

    [ObservableProperty]
    [Range(0, 600000, ErrorMessage = "半帧超时必须在 0 到 600000 毫秒之间（0 表示关闭）。")]
    private int _partialFrameTimeout;

    [ObservableProperty]
    [Range(1, 600000, ErrorMessage = "断开超时必须在 1 到 600000 毫秒之间。")]
    private int _disconnectTimeout = 1000;

    /// <summary>请求超时后是否重建连接（TCP 默认开启）。</summary>
    [ObservableProperty]
    private bool _resetOnRequestTimeout = true;

    /// <summary>迟到应答的保留时间，-1 表示库的默认值。</summary>
    [ObservableProperty]
    [Range(-1, 600000, ErrorMessage = "迟到应答窗口必须为 -1（默认）或 0 到 600000 毫秒。")]
    private int _lateReplyWindow = -1;
}
