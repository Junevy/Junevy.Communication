using Junevy.Communication.Channels;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// HSMS 的关联键提取器（P51-D7、Q34）：键是 System Bytes，但**只有应答类消息才取应答键**。
/// </summary>
/// <remarks>
/// <para>
/// 取应答键的：数据消息中 Function 为偶数（含 0）的、<c>Select.rsp</c>、<c>Deselect.rsp</c>、<c>Linktest.rsp</c>、
/// <c>Reject.req</c>（它拒绝的是我们发出的消息，带同样的 System Bytes）。
/// </para>
/// <para>
/// 不取应答键的：主消息（奇数 Function）与所有 <c>*.req</c> 控制消息。<strong>这一步是关键</strong>：
/// System Bytes 只在发送方内唯一，对方完全可以把它的主消息的号设成与我方在途请求相同的值。
/// 若主消息也取应答键，这条主动消息就会被误当成应答而吞掉。
/// </para>
/// </remarks>
internal sealed class HsmsKeyExtractor : IFrameKeyExtractor
{
    /// <inheritdoc />
    public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key)
        => TryGetKey(request, IsRequestClass, out key);

    /// <inheritdoc />
    public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key)
        => TryGetKey(frame, IsResponseClass, out key);

    /// <summary>
    /// 请求键覆盖所有数据消息与 <c>Select.req</c> / <c>Linktest.req</c>；<c>Deselect.req</c> 与
    /// <c>Separate.req</c> 没有应答，不占在途事务。
    /// </summary>
    private static bool IsRequestClass(byte function, byte sType) => sType switch
    {
        (int)HsmsControlKind.None => true,
        (int)HsmsControlKind.SelectRequest or (int)HsmsControlKind.LinktestRequest => true,
        _ => false,
    };

    /// <summary>应答键只给应答类消息，见类型注释（P51-D7）。</summary>
    private static bool IsResponseClass(byte function, byte sType) => sType switch
    {
        (int)HsmsControlKind.None => HsmsMessage.IsReplyFunction(function),
        (int)HsmsControlKind.SelectResponse or (int)HsmsControlKind.DeselectResponse
            or (int)HsmsControlKind.LinktestResponse or (int)HsmsControlKind.RejectRequest => true,
        _ => false,
    };

    private static bool TryGetKey(ReadOnlySpan<byte> frame, Func<byte, byte, bool> qualifies, out long key)
    {
        key = 0;
        if (!HsmsMessage.TryPeekHeader(frame, out byte _, out byte function, out byte sType))
            return false;
        if (!qualifies(function, sType))
            return false;
        if (!HsmsMessage.TryTakeSystemBytes(frame, out uint systemBytes))
            return false;

        key = systemBytes;
        return true;
    }
}