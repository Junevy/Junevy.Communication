using Junevy.Communication.DebugAssistant.Common;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels;

/// <summary>
/// 服务端的自动应答规则（计划第 7.13 节）：收到以 <see cref="Match"/> 开头的帧时，回显该帧（<see cref="Echo"/> 为真）或回复 <see cref="Reply"/>。
/// <see cref="Match"/> 为空时匹配所有帧。规则按列表顺序检查，第一条匹配的生效。
/// </summary>
/// <param name="Match">匹配的前缀，<c>hex:</c> 或文本语法（UTF-8）。</param>
/// <param name="Reply">回复的内容；回显时不使用。</param>
/// <param name="Echo">为真时回显收到的帧。</param>
public sealed record AutoReplyRule(string Match, string Reply, bool Echo)
{
    /// <summary>解析文本。格式错误、或不回显而回复为空时抛出 <see cref="FormatException"/>。</summary>
    public CompiledAutoReply Compile()
    {
        byte[] match = Match.Length == 0 ? [] : ByteInput.Parse(Match, EncodingChoice.Utf8);
        if (Echo)
        {
            return new CompiledAutoReply(match, reply: null);
        }

        byte[] reply = ByteInput.Parse(Reply, EncodingChoice.Utf8);
        if (reply.Length == 0)
        {
            throw new FormatException("回复内容不能为空。");
        }

        return new CompiledAutoReply(match, reply);
    }
}

/// <summary>已解析的自动应答规则。由界面线程编译，之后在库的事件线程上只读使用。</summary>
public sealed class CompiledAutoReply
{
    private readonly byte[] _match;
    private readonly byte[]? _reply;

    /// <param name="match">匹配的前缀；为空时匹配所有帧。</param>
    /// <param name="reply">回复的内容；为 null 时回显收到的帧。</param>
    public CompiledAutoReply(byte[] match, byte[]? reply)
    {
        _match = match;
        _reply = reply;
    }

    /// <summary>帧匹配时返回应答：回显返回帧的副本，否则返回回复内容的副本；不匹配时返回 false。</summary>
    public bool TryReply(ReadOnlySpan<byte> frame, out byte[] reply)
    {
        if (_match.Length > 0 && !frame.StartsWith(_match))
        {
            reply = [];
            return false;
        }

        reply = _reply is null ? frame.ToArray() : (byte[])_reply.Clone();
        return true;
    }
}
