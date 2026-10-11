using System.Buffers;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 串口 4C 帧（手册 p.32–37）：<see cref="McEncoding.Ascii"/> 为格式 4，<see cref="McEncoding.Binary"/> 为格式 5。
/// 两种格式共用访问路径、帧识别号与请求数据（P2 设计 Q2），只在控制码、附加码与和校验的规则上不同。
/// 收到的应答是完整的一帧：格式 4 不含 CR LF（由通道分隔符去掉）；格式 5 含附加码与和校验。
/// </summary>
internal sealed class Mc4CFrame
{
    private readonly McEncoding encoding;
    private readonly McFieldCodec codec;
    private readonly bool sumCheck;

    /// <summary>创建 4C 帧的编解码器。</summary>
    /// <param name="encoding">ASCII（格式 4）或二进制（格式 5）。</param>
    /// <param name="sumCheck">是否使用和校验，必须与 PLC 侧设置一致。</param>
    public Mc4CFrame(McEncoding encoding, bool sumCheck)
    {
        this.encoding = encoding;
        codec = McFieldCodec.For(encoding);
        this.sumCheck = sumCheck;
    }

    private bool IsFormat4 => encoding == McEncoding.Ascii;

    /// <summary>写入请求帧：格式 4 以 ENQ 开头，格式 5 以 DLE STX 开头。格式 4 不含 CR LF，由通道分隔符追加。</summary>
    /// <param name="output">输出缓冲。</param>
    /// <param name="route">访问路径。</param>
    /// <param name="body">请求数据（命令、子命令与参数，已按编码写好）。</param>
    public void WriteRequest(IBufferWriter<byte> output, Mc4CAccessRoute route, ReadOnlySpan<byte> body)
        => output.Write(BuildRequest(route, body));

    /// <summary>编码请求帧并返回字节（客户端与心跳探测直接使用）。</summary>
    /// <param name="route">访问路径。</param>
    /// <param name="body">请求数据。</param>
    /// <returns>完整的请求帧（格式 4 不含 CR LF）。</returns>
    public byte[] BuildRequest(Mc4CAccessRoute route, ReadOnlySpan<byte> body)
    {
        using var frame = new MemoryStream();
        if (IsFormat4)
            WriteRequestFormat4(frame, route, body);
        else
            WriteRequestFormat5(frame, route, body);

        return frame.ToArray();
    }

    /// <summary>
    /// 解码应答帧。格式 4 的 NAK 错误码与格式 5 的完成码非 0 时，返回携带结束码的成功结果（数据为空），
    /// 由 <see cref="McResponse.CheckEndCode"/> 转换为 RemoteError。
    /// </summary>
    /// <param name="frame">完整的应答帧。</param>
    /// <param name="kind">应答数据的种类。</param>
    /// <param name="count">字数、点数或回环字节数。</param>
    /// <returns>解码结果；帧结构、和校验或帧识别号错误为 ProtocolViolation。</returns>
    public CommResult<McResponse> ReadResponse(ReadOnlySpan<byte> frame, McDataKind kind, int count)
        => IsFormat4 ? ReadResponseFormat4(frame, kind, count) : ReadResponseFormat5(frame, kind, count);

    private void WriteRequestFormat4(MemoryStream frame, Mc4CAccessRoute route, ReadOnlySpan<byte> body)
    {
        frame.WriteByte(McSerialCodes.Enq);
        int summedStart = (int)frame.Length;
        WriteHeader(frame, route);
        codec.WriteRaw(frame, body);
        if (sumCheck)
            McChecksum.Write(frame, McChecksum.Sum(new ReadOnlySpan<byte>(frame.GetBuffer(), summedStart, (int)frame.Length - summedStart)));
    }

    private void WriteRequestFormat5(MemoryStream frame, Mc4CAccessRoute route, ReadOnlySpan<byte> body)
    {
        // 数据字节数 = 帧识别号 + 访问路径 + 请求数据，不含附加码（手册 p.33）。
        using var payload = new MemoryStream();
        codec.WriteUInt16(payload, checked((ushort)(codec.UInt8Size + Mc4CAccessRoute.FieldSize(codec) + body.Length)));
        WriteHeader(payload, route);
        codec.WriteRaw(payload, body);
        byte[] bytes = payload.ToArray();

        frame.WriteByte(McSerialCodes.Dle);
        frame.WriteByte(McSerialCodes.Stx);
        McDleFrame.WriteEscaped(frame, bytes);
        frame.WriteByte(McSerialCodes.Dle);
        frame.WriteByte(McSerialCodes.Etx);
        if (sumCheck)
            McChecksum.Write(frame, McChecksum.Sum(bytes));
    }

    private CommResult<McResponse> ReadResponseFormat4(ReadOnlySpan<byte> frame, McDataKind kind, int count)
    {
        if (frame.Length == 0)
            return McResponse.Violation("The format 4 response is empty.");

        switch (frame[0])
        {
            case McSerialCodes.Ack:
                return ReadAck(frame.Slice(1), kind, count);
            case McSerialCodes.Nak:
                return ReadNak(frame.Slice(1));
            case McSerialCodes.Stx:
                return ReadDataFormat4(frame, kind, count);
            default:
                return McResponse.Violation("The format 4 response starts with an unexpected control code.");
        }
    }

    private CommResult<McResponse> ReadAck(ReadOnlySpan<byte> rest, McDataKind kind, int count)
    {
        if (!TryTakeHeader(ref rest) || !rest.IsEmpty)
            return McResponse.Violation("The format 4 ACK response is malformed.");

        return McResponseData.Read(codec, ReadOnlySpan<byte>.Empty, kind, count);
    }

    private CommResult<McResponse> ReadNak(ReadOnlySpan<byte> rest)
    {
        if (!TryTakeHeader(ref rest) || rest.Length != codec.UInt16Size || !codec.TryReadUInt16(rest, out ushort errorCode) || errorCode == 0)
            return McResponse.Violation("The format 4 NAK response is malformed or carries no error code.");

        return McResponse.Decoded(errorCode, Array.Empty<byte>());
    }

    private CommResult<McResponse> ReadDataFormat4(ReadOnlySpan<byte> frame, McDataKind kind, int count)
    {
        int etx = frame.IndexOf(McSerialCodes.Etx);
        if (etx < 0)
            return McResponse.Violation("The format 4 data response has no ETX.");

        // 和校验的范围是帧识别号到 ETX（含 ETX，P2-D16 ①）；数据位于 STX 与 ETX 之间。
        ReadOnlySpan<byte> content = frame.Slice(1, etx - 1);
        if (!SumMatches(frame.Slice(1, etx), frame.Slice(etx + 1)) || !TryTakeHeader(ref content))
            return McResponse.Violation("The format 4 response checksum or frame header is wrong.");

        return McResponseData.Read(codec, content, kind, count);
    }

    private CommResult<McResponse> ReadResponseFormat5(ReadOnlySpan<byte> frame, McDataKind kind, int count)
    {
        // ReadOnlySpan 无法包成 ReadOnlySequence，整帧复制一次后交给扫描器。
        var sequence = new ReadOnlySequence<byte>(frame.ToArray());
        if (McDleFrame.Scan(sequence, out int end, out byte[] payload) != McDleFrame.ScanResult.Complete)
            return McResponse.Violation("The format 5 response is not a complete DLE STX ... DLE ETX frame.");

        if (!SumMatches(payload, frame.Slice(end)))
            return McResponse.Violation("The format 5 response checksum is wrong.");

        ReadOnlySpan<byte> rest = payload;
        if (!codec.TryTakeUInt16(ref rest, out ushort byteCount) || byteCount != rest.Length)
            return McResponse.Violation("The format 5 byte count does not match the frame.");

        if (!TryTakeHeader(ref rest) || !TryTakeResponseId(ref rest))
            return McResponse.Violation("The format 5 frame identifier or response ID is wrong.");

        if (!codec.TryTakeUInt16(ref rest, out ushort completion))
            return McResponse.Violation("The format 5 response is too short for a completion code.");

        return completion != 0
            ? McResponse.Decoded(completion, Array.Empty<byte>())
            : McResponseData.Read(codec, rest, kind, count);
    }

    private void WriteHeader(Stream output, Mc4CAccessRoute route)
    {
        codec.WriteUInt8(output, McSerialCodes.FrameId);
        route.Write(codec, output);
    }

    /// <summary>切出帧识别号（F8）与访问路径；帧识别号不符或长度不足时返回 false。</summary>
    private bool TryTakeHeader(ref ReadOnlySpan<byte> rest)
    {
        return McFieldCodec.TryTake(ref rest, codec.UInt8Size, out ReadOnlySpan<byte> frameId)
            && codec.TryReadUInt8(frameId, out byte identifier) && identifier == McSerialCodes.FrameId
            && McFieldCodec.TryTake(ref rest, Mc4CAccessRoute.FieldSize(codec), out _);
    }

    private bool TryTakeResponseId(ref ReadOnlySpan<byte> rest)
        => codec.TryTakeUInt16(ref rest, out ushort responseId) && responseId == McSerialCodes.ResponseId;

    /// <summary>
    /// 和校验：开启时 <paramref name="tail"/> 必须是 2 个 ASCII 十六进制字符且等于 <paramref name="summed"/> 的低 8 位；
    /// 关闭时 <paramref name="tail"/> 必须为空。
    /// </summary>
    private bool SumMatches(ReadOnlySpan<byte> summed, ReadOnlySpan<byte> tail)
    {
        if (!sumCheck)
            return tail.IsEmpty;

        return McChecksum.TryRead(tail, out byte sum) && sum == McChecksum.Sum(summed);
    }
}
