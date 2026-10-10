using System.Buffers;

namespace Junevy.Communication.Channels;

/// <summary>
/// 把一个负载编码为线路上的字节（例如追加分隔符）。编码器无状态，可被并发调用。
/// </summary>
public interface IFrameEncoder
{
    /// <summary>
    /// 编码 <paramref name="payload"/> 并写入 <paramref name="output"/>。
    /// </summary>
    /// <param name="payload">负载。</param>
    /// <param name="output">接收编码结果的写入器。</param>
    void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output);
}
