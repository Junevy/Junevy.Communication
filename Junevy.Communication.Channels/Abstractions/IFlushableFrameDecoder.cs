using System.Buffers;

namespace Junevy.Communication.Channels;

/// <summary>
/// 按"静默时间"判定帧结束的分帧器：输入停顿 <see cref="FlushTimeout"/> 毫秒后，接收循环调用 <see cref="TryFlush"/> 交出残余数据。
/// </summary>
public interface IFlushableFrameDecoder : IFrameDecoder
{
    /// <summary>静默判定时间（毫秒）。</summary>
    int FlushTimeout { get; }

    /// <summary>
    /// 把缓冲中的全部数据作为一帧交出；缓冲为空时返回 false。
    /// </summary>
    /// <param name="buffer">待交出的缓冲；成功时清空。</param>
    /// <param name="frame">交出的帧（仅在返回 true 时有效）。</param>
    /// <returns>交出一帧返回 true。</returns>
    bool TryFlush(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> frame);
}
