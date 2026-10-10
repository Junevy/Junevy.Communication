namespace Junevy.Communication.Channels;

/// <summary>
/// 连接统计。所有属性线程安全：读取使用 <see cref="Interlocked"/> 或 <see cref="Volatile"/>；递增方法为 internal，仅由通道内部调用。
/// 时间戳以 UTC ticks 保存，0 表示 null（从未发生或未连接）。
/// </summary>
public sealed class ConnectionStatistics
{
    private long bytesSent;
    private long bytesReceived;
    private long framesSent;
    private long framesReceived;
    private long framesDropped;
    private long protocolErrors;
    private long reconnectCount;
    private int consecutiveHeartbeatFailures;
    private long lastSentAtTicks;
    private long lastReceivedAtTicks;
    private long connectedSinceTicks;

    internal ConnectionStatistics()
    {
    }

    /// <summary>累计发送的字节数。</summary>
    public long BytesSent => Interlocked.Read(ref bytesSent);

    /// <summary>累计接收的字节数（按交付的帧计）。</summary>
    public long BytesReceived => Interlocked.Read(ref bytesReceived);

    /// <summary>累计发送的帧数。</summary>
    public long FramesSent => Interlocked.Read(ref framesSent);

    /// <summary>累计接收的帧数。</summary>
    public long FramesReceived => Interlocked.Read(ref framesReceived);

    /// <summary>因队列满或迟到应答而丢弃的帧数。</summary>
    public long FramesDropped => Interlocked.Read(ref framesDropped);

    /// <summary>协议错误次数（分帧异常、校验失败、残余字节丢弃等）。</summary>
    public long ProtocolErrors => Interlocked.Read(ref protocolErrors);

    /// <summary>成功重连的次数。</summary>
    public long ReconnectCount => Interlocked.Read(ref reconnectCount);

    /// <summary>连续失败的心跳次数；一次成功即清零。</summary>
    public int ConsecutiveHeartbeatFailures => Volatile.Read(ref consecutiveHeartbeatFailures);

    /// <summary>最近一次发送完成的时间；从未发送时为 null。</summary>
    public DateTimeOffset? LastSentAt => ReadTimestamp(ref lastSentAtTicks);

    /// <summary>最近一次接收到帧的时间；从未接收时为 null。</summary>
    public DateTimeOffset? LastReceivedAt => ReadTimestamp(ref lastReceivedAtTicks);

    /// <summary>当前连接建立的时间；未连接时为 null。</summary>
    public DateTimeOffset? ConnectedSince => ReadTimestamp(ref connectedSinceTicks);

    /// <summary>记录一次发送完成。</summary>
    /// <param name="length">帧的字节数。</param>
    /// <param name="sentAt">发送完成的时间。</param>
    internal void RecordFrameSent(int length, DateTimeOffset sentAt)
    {
        Interlocked.Add(ref bytesSent, length);
        Interlocked.Increment(ref framesSent);
        Interlocked.Exchange(ref lastSentAtTicks, sentAt.UtcTicks);
    }

    /// <summary>记录一次接收（交付给上层的帧）。</summary>
    /// <param name="length">帧的字节数。</param>
    /// <param name="receivedAt">接收时间。</param>
    internal void RecordFrameReceived(int length, DateTimeOffset receivedAt)
    {
        Interlocked.Add(ref bytesReceived, length);
        Interlocked.Increment(ref framesReceived);
        Interlocked.Exchange(ref lastReceivedAtTicks, receivedAt.UtcTicks);
    }

    internal void IncrementFramesDropped() => Interlocked.Increment(ref framesDropped);

    internal void IncrementProtocolErrors() => Interlocked.Increment(ref protocolErrors);

    internal void IncrementReconnectCount() => Interlocked.Increment(ref reconnectCount);

    /// <summary>心跳失败计数加一。</summary>
    /// <returns>加一之后的连续失败次数。</returns>
    internal int IncrementConsecutiveHeartbeatFailures() => Interlocked.Increment(ref consecutiveHeartbeatFailures);

    internal void ResetConsecutiveHeartbeatFailures() => Interlocked.Exchange(ref consecutiveHeartbeatFailures, 0);

    /// <summary>设置（或清除，传 null）连接建立时间。</summary>
    internal void SetConnectedSince(DateTimeOffset? connectedAt) => Interlocked.Exchange(ref connectedSinceTicks, connectedAt?.UtcTicks ?? 0);

    private static DateTimeOffset? ReadTimestamp(ref long ticks)
    {
        long value = Interlocked.Read(ref ticks);
        return value == 0 ? (DateTimeOffset?)null : new DateTimeOffset(value, TimeSpan.Zero);
    }
}
