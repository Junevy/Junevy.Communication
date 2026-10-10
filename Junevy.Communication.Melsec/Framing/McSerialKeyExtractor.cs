using Junevy.Communication.Channels;
using Junevy.Communication.Melsec.Protocol;

namespace Junevy.Communication.Melsec.Framing;

/// <summary>
/// 4E 的关联键（P2 计划 Task 11）：序列号位于副头部之后，二进制为 2 字节小端，ASCII 为 4 位十六进制。
/// 请求与应答的序列号位于相同的偏移。
/// </summary>
internal sealed class McSerialKeyExtractor : IFrameKeyExtractor
{
    private readonly McFieldCodec codec;

    /// <summary>创建关联键提取器。</summary>
    /// <param name="encoding">数据编码。</param>
    public McSerialKeyExtractor(McEncoding encoding)
    {
        codec = McFieldCodec.For(encoding);
    }

    /// <inheritdoc />
    public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key) => TryGetSerial(request, out key);

    /// <inheritdoc />
    public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key) => TryGetSerial(frame, out key);

    private bool TryGetSerial(ReadOnlySpan<byte> frame, out long key)
    {
        key = 0;
        ReadOnlySpan<byte> rest = frame;
        if (!McFieldCodec.TryTake(ref rest, codec.UInt16Size, out _) || !codec.TryTakeUInt16(ref rest, out ushort serial))
            return false;

        key = serial;
        return true;
    }
}
