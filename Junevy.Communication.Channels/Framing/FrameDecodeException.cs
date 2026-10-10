namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// 分帧器检测到非法数据时抛出的异常（例如超长帧、非法长度字段）。通道层将其归类为 ProtocolViolation。
/// </summary>
public sealed class FrameDecodeException : Exception
{
    /// <summary>
    /// 初始化异常。
    /// </summary>
    /// <param name="message">错误信息（英文）。</param>
    public FrameDecodeException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// 初始化异常。
    /// </summary>
    /// <param name="message">错误信息（英文）。</param>
    /// <param name="innerException">内部异常；没有时为 null。</param>
    public FrameDecodeException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
