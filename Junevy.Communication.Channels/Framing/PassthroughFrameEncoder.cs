using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// 原样编码器：负载不做任何改动。用于没有分隔符语义的分帧模式（长度头等由协议包自己放入负载）。
/// </summary>
internal sealed class PassthroughFrameEncoder : IFrameEncoder
{
    /// <summary>共享的无状态实例。</summary>
    public static readonly PassthroughFrameEncoder Instance = new PassthroughFrameEncoder();

    private PassthroughFrameEncoder()
    {
    }

    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        if (payload.Length == 0)
            return;

        payload.CopyTo(output.GetSpan(payload.Length));
        output.Advance(payload.Length);
    }
}
