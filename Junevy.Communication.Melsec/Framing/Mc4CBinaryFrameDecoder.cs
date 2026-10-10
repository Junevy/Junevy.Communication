using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Melsec.Protocol;

namespace Junevy.Communication.Melsec.Framing;

/// <summary>
/// 格式 5 的分帧器：从 DLE STX 切到未转义的 DLE ETX，开启和校验时再取 2 个字节。交出的帧保留附加码，由协议层还原。
/// 缓冲开头不是 DLE STX，或 DLE 后出现非法字节时抛出 <see cref="FrameDecodeException"/>；超过 MaxFrameLength 仍未结束时同样抛出。
/// 扫描只看长度不超过 MaxFrameLength 的窗口，因此不会为超长的数据复制整个缓冲。
/// </summary>
internal sealed class Mc4CBinaryFrameDecoder : IFrameDecoder
{
    private readonly bool sumCheck;
    private readonly int maxFrameLength;

    /// <summary>创建分帧器。</summary>
    /// <param name="sumCheck">是否带和校验（2 个 ASCII 字符）。</param>
    /// <param name="maxFrameLength">单帧的最大字节数（含附加码与和校验）。</param>
    public Mc4CBinaryFrameDecoder(bool sumCheck, int maxFrameLength)
    {
        this.sumCheck = sumCheck;
        this.maxFrameLength = maxFrameLength;
    }

    /// <inheritdoc />
    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        frame = default;
        byte[] window = buffer.Slice(0, Math.Min(buffer.Length, maxFrameLength)).ToArray();
        McDleFrame.ScanResult result = McDleFrame.Scan(window, out int end, out _);
        if (result == McDleFrame.ScanResult.Invalid)
            throw new FrameDecodeException("The format 5 frame must start with DLE STX and contain only DLE pairs before DLE ETX.");

        int total = end + (sumCheck ? McChecksum.Length : 0);
        if (result == McDleFrame.ScanResult.Incomplete || total > window.Length)
        {
            if (buffer.Length >= maxFrameLength)
                throw new FrameDecodeException($"The format 5 frame exceeds MaxFrameLength ({maxFrameLength} bytes).");

            return false;
        }

        frame = buffer.Slice(0, total);
        buffer = buffer.Slice(total);
        return true;
    }
}
