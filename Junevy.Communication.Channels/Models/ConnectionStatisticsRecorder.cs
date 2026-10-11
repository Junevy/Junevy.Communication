namespace Junevy.Communication.Channels;

/// <summary>
/// 连接统计的记录器（P25-D10）。实现自定义 <see cref="IClientChannel"/> 或 <see cref="IByteChannel"/> 时使用：
/// 实现类把记录器保持为私有字段，只把 <see cref="Statistics"/> 交给使用者，使用者无法通过它改写统计。
/// </summary>
public sealed class ConnectionStatisticsRecorder
{
    private readonly ConnectionStatistics statistics = new ConnectionStatistics();

    /// <summary>统计对象；交给使用者即可，记录方法留在实现内部。</summary>
    public ConnectionStatistics Statistics => statistics;

    /// <summary>记录一次发送完成。</summary>
    /// <param name="length">帧的字节数。</param>
    /// <param name="sentAt">发送完成的时间。</param>
    public void RecordFrameSent(int length, DateTimeOffset sentAt) => statistics.RecordFrameSent(length, sentAt);

    /// <summary>记录一次接收（交付给上层的帧）。</summary>
    /// <param name="length">帧的字节数。</param>
    /// <param name="receivedAt">接收时间。</param>
    public void RecordFrameReceived(int length, DateTimeOffset receivedAt) => statistics.RecordFrameReceived(length, receivedAt);

    /// <summary>记录一次因队列满或迟到应答而丢弃的帧。</summary>
    public void IncrementFramesDropped() => statistics.IncrementFramesDropped();

    /// <summary>记录一次协议错误（分帧异常、校验失败、残余字节丢弃等）。</summary>
    public void IncrementProtocolErrors() => statistics.IncrementProtocolErrors();

    /// <summary>记录一次成功的重连。</summary>
    public void IncrementReconnectCount() => statistics.IncrementReconnectCount();

    /// <summary>心跳失败计数加一。</summary>
    /// <returns>加一之后的连续失败次数。</returns>
    public int IncrementConsecutiveHeartbeatFailures() => statistics.IncrementConsecutiveHeartbeatFailures();

    /// <summary>心跳成功时清零连续失败计数。</summary>
    public void ResetConsecutiveHeartbeatFailures() => statistics.ResetConsecutiveHeartbeatFailures();

    /// <summary>设置（或清除，传 null）连接建立时间。</summary>
    /// <param name="connectedAt">连接建立的时间；未连接时传 null。</param>
    public void SetConnectedSince(DateTimeOffset? connectedAt) => statistics.SetConnectedSince(connectedAt);
}
