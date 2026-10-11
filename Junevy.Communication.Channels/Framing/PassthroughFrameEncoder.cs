using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// 原样编码器：负载不做任何改动。用于没有分隔符语义的分帧模式（长度头等由协议包自己放入负载）。
/// 自定义分帧器需要透传时直接使用 <see cref="Instance"/>；空负载不写入任何字节。
/// </summary>
public sealed class PassthroughFrameEncoder : IFrameEncoder
{
    /// <summary>共享的无状态实例。</summary>
    public static readonly PassthroughFrameEncoder Instance = new PassthroughFrameEncoder();

    private PassthroughFrameEncoder()
    {
    }

    /// <summary>把负载原样写入 <paramref name="output"/>；空负载不写入。</summary>
    /// <param name="payload">负载。</param>
    /// <param name="output">接收编码结果的写入器。</param>
    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        if (payload.Length == 0)
            return;

        output.Write(payload);
    }
}
