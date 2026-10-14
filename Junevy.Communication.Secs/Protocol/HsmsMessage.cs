using System.Buffers;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Secs.Protocol;

/// <summary>
/// 一条 HSMS 报文（P51-D6）：4 字节长度（大端、不含自身）+ 10 字节头 + 正文。
/// </summary>
/// <remarks>
/// <para>
/// 头依次是会话号（2）、头字节 2、头字节 3、PType（1）、SType（1）、System Bytes（4）。
/// 数据消息的会话号是设备号，头字节 2 = W 位 &lt;&lt; 7 | Stream，头字节 3 = Function，PType 与 SType 都是 0。
/// 控制消息的会话号是 0xFFFF，头字节 2 与 3 的含义随 SType 而变（<c>Select.rsp</c> 是"0、状态码"，
/// <c>Reject.req</c> 是"被拒消息的 SType、原因码"，其余为 0）；<c>Reject.req</c> 例外，它回送被拒消息的会话号。
/// </para>
/// <para>
/// 长度字段由本类型自己写——通道的分帧编码器是 passthrough，不会替协议加任何字节。
/// </para>
/// </remarks>
internal sealed class HsmsMessage
{
    /// <summary>长度前缀的字节数。</summary>
    internal const int LengthPrefixBytes = 4;

    /// <summary>头的字节数。</summary>
    internal const int HeaderBytes = 10;

    /// <summary>控制消息使用的会话号（SEMI E37）。</summary>
    internal const ushort AnySession = 0xFFFF;

    /// <summary>本库只接受 PType 0。</summary>
    internal const byte SupportedPType = 0;

    /// <summary>数据消息的 Stream 上界（头字节 2 的最高位是 W 位）。</summary>
    private const byte MaximumStream = 0x7F;

    /// <summary>头字节 2 的最高位是 W 位。</summary>
    private const byte ReplyExpectedFlag = 0x80;

    /// <summary>偶数 Function 是应答。</summary>
    private const int ReplyFunctionStride = 2;

    private readonly byte[] _body;

    private HsmsMessage(ushort sessionId, byte byte2, byte byte3, byte pType, byte sType, uint systemBytes, byte[] body)
    {
        SessionId = sessionId;
        Byte2 = byte2;
        Byte3 = byte3;
        PType = pType;
        SType = sType;
        SystemBytes = systemBytes;
        _body = body;
    }

    /// <summary>会话号：数据消息是设备号，控制消息通常是 <see cref="AnySession"/>。</summary>
    internal ushort SessionId { get; }

    /// <summary>头字节 2：数据消息是 W 位与 Stream，控制消息的含义随 <see cref="SType"/> 而变。</summary>
    internal byte Byte2 { get; }

    /// <summary>头字节 3：数据消息是 Function，<c>Select.rsp</c> 是状态码，<c>Reject.req</c> 是原因码。</summary>
    internal byte Byte3 { get; }

    /// <summary>PType；本库只接受 <see cref="SupportedPType"/>。</summary>
    internal byte PType { get; }

    /// <summary>SType：0 是数据消息，其余见 <see cref="HsmsControlKind"/>。</summary>
    internal byte SType { get; }

    /// <summary>System Bytes：发送方内唯一，用于把应答与请求配对。</summary>
    internal uint SystemBytes { get; }

    /// <summary>正文：数据消息是 SECS-II 编码的正文，控制消息为空。</summary>
    internal ReadOnlySpan<byte> Body => _body;

    /// <summary>控制消息的种类；未分配的 SType 原样返回，由派发方决定怎么拒绝（P51-D12）。</summary>
    internal HsmsControlKind Kind => (HsmsControlKind)SType;

    /// <summary>W 位，仅对数据消息有意义。</summary>
    internal bool ReplyExpected => (Byte2 & ReplyExpectedFlag) != 0;

    /// <summary>Stream，仅对数据消息有意义。</summary>
    internal byte Stream => (byte)(Byte2 & MaximumStream);

    /// <summary>Function，仅对数据消息有意义。</summary>
    internal byte Function => Byte3;

    /// <summary><c>Select.rsp</c> 的状态码：0 成功，1 已激活。</summary>
    internal byte Status => Byte3;

    /// <summary><c>Reject.req</c> 的原因码。</summary>
    internal byte RejectReason => Byte3;

    /// <summary>
    /// <c>Reject.req</c> 头字节 2：被拒消息的 SType；原因为 <see cref="HsmsRejectReason.PTypeNotSupported"/> 时是被拒消息的 PType。
    /// </summary>
    internal byte OffendingType => Byte2;

    /// <summary>把一条数据消息写成完整的 HSMS 报文。</summary>
    /// <param name="message">SECS-II 消息。</param>
    /// <param name="deviceId">设备号（会话号），0–32767。</param>
    /// <param name="systemBytes">System Bytes；新发起用 <see cref="HsmsSystemBytes.Next"/>，应答回送对方的。</param>
    /// <returns>完整报文。</returns>
    internal static byte[] Write(SecsMessage message, ushort deviceId, uint systemBytes)
    {
        if (message is null)
            throw new ArgumentNullException(nameof(message));

        // 长度字段写在最前面，正文得先编码出来才知道长度。
        byte[] body = Array.Empty<byte>();
        if (message.Body is not null)
        {
            using var encoded = new ByteBufferWriter();
            SecsItemCodec.Encode(encoded, message.Body);
            body = encoded.ToArray();
        }

        using var buffer = new ByteBufferWriter();
        WriteLengthPrefix(buffer, HeaderBytes + body.Length);
        WriteDataHeader(buffer, message, deviceId, systemBytes);
        BigEndianFields.WriteBytes(buffer, body);
        return buffer.ToArray();
    }

    /// <summary>读一条完整的 HSMS 报文。</summary>
    /// <param name="frame">通道分帧交出的完整报文（仍带 4 字节长度前缀）。</param>
    /// <param name="maxLength">允许的最大报文长度（P51-D5 的 <c>MaxMessageLength</c>）。</param>
    /// <returns>报文；结构不合法时返回 <see cref="CommErrorKind.ProtocolViolation"/> 的失败结果。</returns>
    internal static CommResult<HsmsMessage> Read(byte[] frame, int maxLength)
    {
        if (frame is null)
            throw new ArgumentNullException(nameof(frame));
        if (frame.Length > LengthPrefixBytes + maxLength)
            return Invalid($"The frame carries {frame.Length} byte(s), which is longer than the {maxLength} byte limit.");

        ReadOnlySpan<byte> data = new ReadOnlySpan<byte>(frame);
        if (!BigEndianFields.TryTakeUInt32(ref data, out uint declared))
            return Invalid($"The frame is truncated: the {LengthPrefixBytes} length byte(s) are missing.");
        if (declared != frame.Length - LengthPrefixBytes)
            return Invalid($"The length field declares {declared} byte(s) but the frame carries {frame.Length - LengthPrefixBytes} after it.");

        if (data.Length < HeaderBytes)
            return Invalid($"The frame carries a {HeaderBytes} byte header but only {data.Length} byte(s) follow the length field.");
        if (!BigEndianFields.TryTakeUInt16(ref data, out ushort sessionId)
            || !BigEndianFields.TryTakeUInt8(ref data, out byte byte2)
            || !BigEndianFields.TryTakeUInt8(ref data, out byte byte3)
            || !BigEndianFields.TryTakeUInt8(ref data, out byte pType)
            || !BigEndianFields.TryTakeUInt8(ref data, out byte sType)
            || !BigEndianFields.TryTakeUInt32(ref data, out uint systemBytes))
        {
            return Invalid($"The frame is truncated: its {HeaderBytes} byte header is incomplete.");
        }

        if (pType != SupportedPType)
            return Invalid($"The header declares PType {pType}, but only PType {SupportedPType} is supported.");

        return CommResult<HsmsMessage>.Success(new HsmsMessage(sessionId, byte2, byte3, pType, sType, systemBytes, data.ToArray()));
    }

    /// <summary>把头部的 10 个字节单独写出——S9 消息的正文就是被涉及消息的这 10 个字节（SEMI E5）。</summary>
    /// <param name="output">目标缓冲。</param>
    internal void WriteHeader(IBufferWriter<byte> output)
    {
        BigEndianFields.WriteUInt16(output, SessionId);
        BigEndianFields.WriteUInt8(output, Byte2);
        BigEndianFields.WriteUInt8(output, Byte3);
        BigEndianFields.WriteUInt8(output, PType);
        BigEndianFields.WriteUInt8(output, SType);
        BigEndianFields.WriteUInt32(output, SystemBytes);
    }

    /// <summary>读出头部字段但不复制，供只关心几个字段的调用方（例如关联键提取器）使用。</summary>
    /// <param name="frame">完整报文。</param>
    /// <param name="byte3">读到的头字节 3（数据消息的 Function）。</param>
    /// <param name="pType">读到的 PType。</param>
    /// <param name="sType">读到的 SType。</param>
    /// <returns>头部长度足够返回 true。</returns>
    internal static bool TryPeekHeader(ReadOnlySpan<byte> frame, out byte byte3, out byte pType, out byte sType)
    {
        if (!TryGetHeader(frame, out ReadOnlySpan<byte> header))
        {
            byte3 = 0;
            pType = 0;
            sType = 0;
            return false;
        }

        byte3 = header[3];
        pType = header[4];
        sType = header[5];
        return true;
    }

    /// <summary>读出 System Bytes。</summary>
    /// <param name="frame">完整报文。</param>
    /// <param name="systemBytes">读到的 System Bytes。</param>
    /// <returns>头部长度足够返回 true。</returns>
    internal static bool TryTakeSystemBytes(ReadOnlySpan<byte> frame, out uint systemBytes)
    {
        if (!TryGetHeader(frame, out ReadOnlySpan<byte> header))
        {
            systemBytes = 0;
            return false;
        }

        systemBytes = SystemBytesOf(header);
        return true;
    }

    /// <summary>
    /// 头部完整、但 PType 不是 <see cref="SupportedPType"/> 时取出拒绝它要用的字段（<see cref="Read"/> 不接受这种报文）。
    /// </summary>
    /// <param name="frame">完整报文。</param>
    /// <param name="sessionId">读到的会话号。</param>
    /// <param name="pType">读到的 PType。</param>
    /// <param name="systemBytes">读到的 System Bytes。</param>
    /// <returns>头部完整且 PType 不受支持时返回 true。</returns>
    internal static bool TryPeekUnsupportedPType(ReadOnlySpan<byte> frame, out ushort sessionId, out byte pType, out uint systemBytes)
    {
        sessionId = 0;
        systemBytes = 0;
        if (!TryPeekHeader(frame, out byte _, out pType, out byte _) || pType == SupportedPType)
            return false;

        ReadOnlySpan<byte> header = frame.Slice(LengthPrefixBytes, HeaderBytes);
        sessionId = (ushort)((header[0] << 8) | header[1]);
        systemBytes = SystemBytesOf(header);
        return true;
    }

    private static uint SystemBytesOf(ReadOnlySpan<byte> header)
        => (uint)((header[6] << 24) | (header[7] << 16) | (header[8] << 8) | header[9]);

    // 关联键提取器拿到的是整条报文，帧比长度前缀还短时连切都不能切。
    private static bool TryGetHeader(ReadOnlySpan<byte> frame, out ReadOnlySpan<byte> header)
    {
        if (frame.Length < LengthPrefixBytes + HeaderBytes)
        {
            header = ReadOnlySpan<byte>.Empty;
            return false;
        }

        header = frame.Slice(LengthPrefixBytes, HeaderBytes);
        return true;
    }

    /// <summary>偶数 Function 是应答，奇数是主消息（SEMI E5）。</summary>
    internal static bool IsReplyFunction(byte function) => function % ReplyFunctionStride == 0;

    /// <summary>完整报文的字节副本。</summary>
    internal byte[] ToArray()
    {
        using var buffer = new ByteBufferWriter(LengthPrefixBytes + HeaderBytes + _body.Length);
        WriteLengthPrefix(buffer, HeaderBytes + _body.Length);
        WriteHeader(buffer);
        BigEndianFields.WriteBytes(buffer, _body);
        return buffer.ToArray();
    }

    internal static HsmsMessage Control(ushort sessionId, byte byte2, byte byte3, HsmsControlKind kind, uint systemBytes)
        => new HsmsMessage(sessionId, byte2, byte3, SupportedPType, (byte)kind, systemBytes, Array.Empty<byte>());

    private static void WriteDataHeader(IBufferWriter<byte> output, SecsMessage message, ushort deviceId, uint systemBytes)
    {
        BigEndianFields.WriteUInt16(output, deviceId);
        BigEndianFields.WriteUInt8(output, (byte)(message.Stream | (message.ReplyExpected ? ReplyExpectedFlag : 0)));
        BigEndianFields.WriteUInt8(output, message.Function);
        BigEndianFields.WriteUInt8(output, SupportedPType);
        BigEndianFields.WriteUInt8(output, 0);
        BigEndianFields.WriteUInt32(output, systemBytes);
    }

    private static void WriteLengthPrefix(IBufferWriter<byte> output, int declaredLength)
        => BigEndianFields.WriteUInt32(output, (uint)declaredLength);

    private static CommResult<HsmsMessage> Invalid(string message)
        => CommResult<HsmsMessage>.Fail(message, CommErrorKind.ProtocolViolation);
}