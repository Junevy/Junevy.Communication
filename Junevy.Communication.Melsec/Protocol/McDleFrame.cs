using System.Buffers;
using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 格式 5 的 DLE 边界与附加码（手册 p.33–35）：帧以 DLE STX 开头，以未转义的 DLE ETX 结束；帧内的每个 10H 写成 10H 10H，解码时成对还原。
/// 只处理字节，供协议解码与通道分帧共用。
/// </summary>
internal static class McDleFrame
{
    /// <summary>扫描结果。</summary>
    public enum ScanResult
    {
        /// <summary>找到未转义的 DLE ETX。</summary>
        Complete,

        /// <summary>数据不足，尚未结束。</summary>
        Incomplete,

        /// <summary>不是合法的格式 5 帧结构。</summary>
        Invalid,
    }

    /// <summary>扫描以 DLE STX 开头的帧，直到未转义的 DLE ETX。</summary>
    /// <param name="raw">原始字节（含附加码，不含和校验）。</param>
    /// <param name="end">DLE ETX 之后的位置（仅在 Complete 时有效）。</param>
    /// <param name="payload">去掉 DLE STX 与 DLE ETX，并把成对的 10H 10H 还原为 10H 的字节（仅在 Complete 时有效）。</param>
    /// <returns>扫描结果。</returns>
    public static ScanResult Scan(ReadOnlySequence<byte> raw, out int end, out byte[] payload)
    {
        end = 0;
        payload = Array.Empty<byte>();
        var reader = new ByteSequenceReader(raw);
        if (!reader.TryRead(out byte first))
            return ScanResult.Incomplete;

        if (first != McSerialCodes.Dle)
            return ScanResult.Invalid;

        if (!reader.TryRead(out byte second))
            return ScanResult.Incomplete;

        if (second != McSerialCodes.Stx)
            return ScanResult.Invalid;

        // 输入是一帧（分帧器先按 MaxFrameLength 截取），长度必然在 int 范围内。
        var bytes = new List<byte>((int)raw.Length);
        while (reader.TryRead(out byte value))
        {
            if (value != McSerialCodes.Dle)
            {
                bytes.Add(value);
                continue;
            }

            if (!reader.TryPeek(out byte next))
                return ScanResult.Incomplete;

            if (next == McSerialCodes.Dle)
            {
                reader.Advance(1);
                bytes.Add(McSerialCodes.Dle);
                continue;
            }

            if (next != McSerialCodes.Etx)
                return ScanResult.Invalid;

            reader.Advance(1);
            end = (int)reader.Consumed;
            payload = bytes.ToArray();
            return ScanResult.Complete;
        }

        return ScanResult.Incomplete;
    }

    /// <summary>把负载写为附加码形式：每个 10H 写成 10H 10H（手册 p.35）。</summary>
    /// <param name="output">输出。</param>
    /// <param name="payload">负载。</param>
    public static void WriteEscaped(Stream output, ReadOnlySpan<byte> payload)
    {
        foreach (byte value in payload)
        {
            output.WriteByte(value);
            if (value == McSerialCodes.Dle)
                output.WriteByte(McSerialCodes.Dle);
        }
    }
}
