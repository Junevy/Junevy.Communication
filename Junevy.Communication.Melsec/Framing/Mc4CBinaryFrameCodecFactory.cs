using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Melsec.Framing;

/// <summary>
/// 格式 5（二进制）的分帧工厂：解码器为 <see cref="Mc4CBinaryFrameDecoder"/>；编码器原样输出，附加码与和校验由协议层写入。
/// 每条连接调用一次 <see cref="CreateDecoder"/>。
/// </summary>
internal sealed class Mc4CBinaryFrameCodecFactory : IFrameCodecFactory
{
    private readonly bool sumCheck;
    private readonly int maxFrameLength;

    /// <summary>创建工厂。</summary>
    /// <param name="sumCheck">是否带和校验，必须与 PLC 侧设置一致。</param>
    /// <param name="maxFrameLength">单帧的最大字节数。</param>
    public Mc4CBinaryFrameCodecFactory(bool sumCheck, int maxFrameLength)
    {
        this.sumCheck = sumCheck;
        this.maxFrameLength = maxFrameLength;
    }

    /// <inheritdoc />
    public IFrameDecoder CreateDecoder() => new Mc4CBinaryFrameDecoder(sumCheck, maxFrameLength);

    /// <inheritdoc />
    public IFrameEncoder CreateEncoder() => PassthroughFrameEncoder.Instance;
}
