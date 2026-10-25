using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.DebugAssistant.Simulators.Melsec;

/// <summary>
/// 4C 格式 5 的分帧（计划第 8.2 节）：DLE STX 开始，DLE ETX 结束，数据中的 DLE 写成 DLE DLE；
/// 长度为 2 字节（小端，计数未转义的字节）；和校验为两个 ASCII 十六进制字符（未转义数据之和的低字节，不含 DLE ETX）。
/// 和校验是否存在由 <paramref name="sumCheck"/> 决定（与 PLC 侧设置一致）：开启时 DLE ETX 之后还有两个字符，关闭时帧在 DLE ETX 处结束。
/// 解码器切出的帧是原始字节（含 DLE 转义、DLE STX、DLE ETX 与和校验字符），由引擎去转义；解码器不校验和校验的数值。
/// </summary>
internal sealed class McFormat5FrameDecoder(bool sumCheck) : IFrameDecoder
{
    private const byte Dle = 0x10;
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const int SumCheckLength = 2;

    public bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame)
    {
        byte[] data = buffer.ToArray();
        int start = IndexOfPair(data, 0, Dle, Stx);
        if (start < 0)
        {
            frame = default;
            return false;
        }

        // 找到 DLE ETX，并跳过转义的 DLE 对。
        int end = start + 2;
        while (end + 1 < data.Length && !(data[end] == Dle && data[end + 1] == Etx))
        {
            end += data[end] == Dle ? 2 : 1;
        }

        int frameEnd = end + 2 + (sumCheck ? SumCheckLength : 0);
        if (end + 1 >= data.Length || frameEnd > data.Length)
        {
            frame = default;
            return false;
        }

        frame = buffer.Slice(0, frameEnd);
        buffer = buffer.Slice(frameEnd);
        return true;
    }

    private static int IndexOfPair(byte[] data, int from, byte first, byte second)
    {
        for (int index = from; index + 1 < data.Length; index++)
        {
            if (data[index] == first && data[index + 1] == second)
            {
                return index;
            }
        }

        return -1;
    }
}

/// <summary>4C 格式 5 的分帧工厂：请求用 <see cref="McFormat5FrameDecoder"/>；应答由引擎编码好，原样发送。</summary>
internal sealed class McFormat5FrameCodecFactory(bool sumCheck) : IFrameCodecFactory
{
    public IFrameDecoder CreateDecoder() => new McFormat5FrameDecoder(sumCheck);

    public IFrameEncoder CreateEncoder() => PassthroughFrameEncoder.Instance;
}
