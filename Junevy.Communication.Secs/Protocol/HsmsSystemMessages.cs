using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// S9 消息与中止消息（SEMI E5，<c>SxF0</c>、<c>S9F1</c>、<c>S9F3</c>、<c>S9F5</c>、<c>S9F7</c>、<c>S9F9</c>）：
/// S9 的 Stream 固定是 9，正文是被涉及消息的 10 字节头。
/// </summary>
internal static class HsmsSystemMessages
{
    /// <summary>S9 消息的 Stream（SEMI E5）。</summary>
    private const byte SystemStream = 9;

    /// <summary>会话号不是本设备（SEMI E5 的 S9F1）。</summary>
    private const byte UnrecognizedDeviceFunction = 1;

    /// <summary>没有该 Stream 的处理器（S9F3）。</summary>
    private const byte UnknownStreamFunction = 3;

    /// <summary>该 Stream 没有这个 Function 的处理器（S9F5）。</summary>
    private const byte UnknownFunctionCode = 5;

    /// <summary>正文是非法的（S9F7）。</summary>
    private const byte IllegalDataFunction = 7;

    /// <summary>带 W 位的主消息超过 T3（S9F9）。</summary>
    private const byte LinktestTimeoutFunction = 9;

    /// <summary>S9F1：会话号不是本设备，正文是那条消息的头。</summary>
    /// <param name="offending">被涉及的消息。</param>
    /// <returns>消息。</returns>
    internal static SecsMessage UnrecognizedDeviceId(HsmsMessage offending)
        => WithHeader(UnrecognizedDeviceFunction, offending);

    /// <summary>S9F3：该 Stream 没有处理器。</summary>
    /// <param name="offending">被涉及的消息。</param>
    /// <returns>消息。</returns>
    internal static SecsMessage UnknownStream(HsmsMessage offending)
        => WithHeader(UnknownStreamFunction, offending);

    /// <summary>S9F5：该 Stream 没有这个 Function 的处理器。</summary>
    /// <param name="offending">被涉及的消息。</param>
    /// <returns>消息。</returns>
    internal static SecsMessage UnknownFunction(HsmsMessage offending)
        => WithHeader(UnknownFunctionCode, offending);

    /// <summary>S9F7：正文解码失败或应答不是预期的应答。</summary>
    /// <param name="offending">被涉及的消息。</param>
    /// <returns>消息。</returns>
    internal static SecsMessage IllegalData(HsmsMessage offending)
        => WithHeader(IllegalDataFunction, offending);

    /// <summary>S9F9：我方主消息超过 T3，正文是我方主消息的头（SEMI E5）。</summary>
    /// <param name="mine">我方发出的主消息对应的报文。</param>
    /// <returns>消息。</returns>
    internal static SecsMessage LinktestTimeout(HsmsMessage mine)
        => WithHeader(LinktestTimeoutFunction, mine);

    /// <summary>SxF0：中止事务，Function 为 0、没有正文（SEMI E5）。</summary>
    /// <param name="stream">被中止的主消息所在的 Stream。</param>
    /// <returns>消息。</returns>
    internal static SecsMessage Abort(byte stream)
        => SecsMessage.Create(stream, 0, replyExpected: false);

    private static SecsMessage WithHeader(byte function, HsmsMessage offending)
        => SecsMessage.Create(SystemStream, function, replyExpected: false, SecsItem.B(HeaderOf(offending)));

    // 头部这 10 个字节单独取出来：正文要的就是它，原样回送（含 W 位与对方的 System Bytes）。
    private static byte[] HeaderOf(HsmsMessage message)
    {
        using var buffer = new ByteBufferWriter(HsmsMessage.HeaderBytes);
        message.WriteHeader(buffer);
        return buffer.ToArray();
    }
}
