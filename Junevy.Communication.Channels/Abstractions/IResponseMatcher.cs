namespace Junevy.Communication.Channels;

/// <summary>
/// 判断入站帧是否为某个请求的应答（用于 <see cref="CorrelationMode.Matcher"/>）。
/// </summary>
public interface IResponseMatcher
{
    /// <summary>判断 <paramref name="frame"/> 是否为对 <paramref name="request"/> 的应答。</summary>
    /// <param name="request">请求负载。</param>
    /// <param name="frame">入站帧。</param>
    /// <returns>是应答返回 true。</returns>
    bool IsMatch(ReadOnlySpan<byte> request, ReadOnlySpan<byte> frame);
}
