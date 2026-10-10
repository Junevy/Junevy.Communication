namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// <see cref="ConnectionStatistics"/> 的可空时间戳与并发递增测试。
/// </summary>
public sealed class ConnectionStatisticsTests
{
    [Fact]
    public void Timestamps_AreNullUntilRecorded()
    {
        var statistics = new ConnectionStatistics();
        Assert.Null(statistics.LastSentAt);
        Assert.Null(statistics.LastReceivedAt);
        Assert.Null(statistics.ConnectedSince);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        statistics.RecordFrameSent(3, now);
        statistics.RecordFrameReceived(5, now);
        statistics.SetConnectedSince(now);

        Assert.Equal(now, statistics.LastSentAt);
        Assert.Equal(now, statistics.LastReceivedAt);
        Assert.Equal(now, statistics.ConnectedSince);
        Assert.Equal(3, statistics.BytesSent);
        Assert.Equal(1, statistics.FramesSent);
        Assert.Equal(5, statistics.BytesReceived);
        Assert.Equal(1, statistics.FramesReceived);

        statistics.SetConnectedSince(null);
        Assert.Null(statistics.ConnectedSince);
    }

    [Fact]
    public void Counters_IncrementAndReset()
    {
        var statistics = new ConnectionStatistics();

        statistics.IncrementFramesDropped();
        statistics.IncrementProtocolErrors();
        statistics.IncrementReconnectCount();
        Assert.Equal(1, statistics.FramesDropped);
        Assert.Equal(1, statistics.ProtocolErrors);
        Assert.Equal(1, statistics.ReconnectCount);

        Assert.Equal(1, statistics.IncrementConsecutiveHeartbeatFailures());
        Assert.Equal(2, statistics.IncrementConsecutiveHeartbeatFailures());
        statistics.ResetConsecutiveHeartbeatFailures();
        Assert.Equal(0, statistics.ConsecutiveHeartbeatFailures);
    }

    [Fact]
    public void ConcurrentRecording_LosesNoUpdates()
    {
        var statistics = new ConnectionStatistics();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Parallel.For(0, 8, _ =>
        {
            for (int i = 0; i < 10000; i++)
                statistics.RecordFrameSent(1, now);
        });

        Assert.Equal(80000, statistics.FramesSent);
        Assert.Equal(80000, statistics.BytesSent);
    }
}
