using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Common;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>连接后握手编辑器（计划 DA-D12 的 <see cref="ScriptedInitializer"/>）。</summary>
public sealed partial class HandshakeEditor : ValidatedEditor
{
    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _send = "hex:";

    /// <summary>是否等待应答；为空时只发送，不比较。</summary>
    [ObservableProperty]
    private bool _expectReply;

    [ObservableProperty]
    private string _expectedReply = "hex:";

    [ObservableProperty]
    [Range(1, 600000, ErrorMessage = "握手应答超时必须在 1 到 600000 毫秒之间。")]
    private int _replyTimeout = 2000;

    /// <summary>生成握手钩子；未启用时返回 null。</summary>
    public IConnectionInitializer? ToInitializer()
    {
        if (!Enabled)
        {
            return null;
        }

        byte[] send = ByteInput.Parse(Send, EncodingChoice.Utf8);
        byte[]? expected = ExpectReply ? ByteInput.Parse(ExpectedReply, EncodingChoice.Utf8) : null;
        return new ScriptedInitializer(send, expected, ReplyTimeout);
    }
}
