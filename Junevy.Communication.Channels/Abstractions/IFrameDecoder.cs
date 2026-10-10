using System.Buffers;

namespace Junevy.Communication.Channels;

/// <summary>
/// 从字节缓冲中切出完整帧。每条连接一个实例（分帧器可以有内部状态）。
/// </summary>
public interface IFrameDecoder
{
    /// <summary>
    /// 从缓冲区切出一帧：成功时 <paramref name="buffer"/> 前移并返回 true；数据不足返回 false。
    /// 数据非法时抛出 <see cref="Framing.FrameDecodeException"/>，通道层将其归类为 ProtocolViolation。
    /// </summary>
    /// <param name="buffer">待解析的缓冲；成功时前移到帧之后。</param>
    /// <param name="frame">切出的帧（仅在返回 true 时有效）。</param>
    /// <returns>切出一帧返回 true。</returns>
    bool TryDecode(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame);
}
