namespace Junevy.Communication.Channels;

/// <summary>
/// 从请求和应答中各提取一个关联键（用于 <see cref="CorrelationMode.Keyed"/>，例如 Modbus TCP 的事务 ID、MC 4E 的序列号）。
/// </summary>
public interface IFrameKeyExtractor
{
    /// <summary>从请求提取关联键。</summary>
    /// <param name="request">请求负载。</param>
    /// <param name="key">提取到的键。</param>
    /// <returns>提取成功返回 true。</returns>
    bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key);

    /// <summary>从应答提取关联键；提取失败的帧视为未认领。</summary>
    /// <param name="frame">入站帧。</param>
    /// <param name="key">提取到的键。</param>
    /// <returns>提取成功返回 true。</returns>
    bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key);
}
