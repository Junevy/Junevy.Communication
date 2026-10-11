namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// <see cref="ConnectionStatisticsRecorder"/> 的每个记录方法都反映到 <see cref="ConnectionStatistics"/>（P25-D10）。
/// </summary>
public sealed class ConnectionStatisticsRecorderTests
{
    private static readonly DateTimeOffset Moment = new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewRecorder_StartsWithZeroStatistics()
    {
        ConnectionStatistics statistics = new ConnectionStatisticsRecorder().Statistics;

        Assert.Equal(0, statistics.BytesSent);
        Assert.Equal(0, statistics.FramesReceived);
        Assert.Equal(0, statistics.ConsecutiveHeartbeatFailures);
        Assert.Null(statistics.ConnectedSince);
    }

    [Fact]
    public void RecordFrameSent_UpdatesBytesFramesAndTheLastSentTime()
    {
        var recorder = new ConnectionStatisticsRecorder();

        recorder.RecordFrameSent(12, Moment);

        Assert.Equal(12, recorder.Statistics.BytesSent);
        Assert.Equal(1, recorder.Statistics.FramesSent);
        Assert.Equal(Moment, recorder.Statistics.LastSentAt);
    }

    [Fact]
    public void RecordFrameReceived_UpdatesBytesFramesAndTheLastReceivedTime()
    {
        var recorder = new ConnectionStatisticsRecorder();

        recorder.RecordFrameReceived(7, Moment);

        Assert.Equal(7, recorder.Statistics.BytesReceived);
        Assert.Equal(1, recorder.Statistics.FramesReceived);
        Assert.Equal(Moment, recorder.Statistics.LastReceivedAt);
    }

    [Fact]
    public void IncrementFramesDropped_CountsDroppedFrames()
    {
        var recorder = new ConnectionStatisticsRecorder();

        recorder.IncrementFramesDropped();

        Assert.Equal(1, recorder.Statistics.FramesDropped);
    }

    [Fact]
    public void IncrementProtocolErrors_CountsProtocolErrors()
    {
        var recorder = new ConnectionStatisticsRecorder();

        recorder.IncrementProtocolErrors();

        Assert.Equal(1, recorder.Statistics.ProtocolErrors);
    }

    [Fact]
    public void IncrementReconnectCount_CountsReconnects()
    {
        var recorder = new ConnectionStatisticsRecorder();

        recorder.IncrementReconnectCount();
        recorder.IncrementReconnectCount();

        Assert.Equal(2, recorder.Statistics.ReconnectCount);
    }

    [Fact]
    public void IncrementConsecutiveHeartbeatFailures_ReturnsTheRunningCount()
    {
        var recorder = new ConnectionStatisticsRecorder();

        Assert.Equal(1, recorder.IncrementConsecutiveHeartbeatFailures());
        Assert.Equal(2, recorder.IncrementConsecutiveHeartbeatFailures());
        Assert.Equal(2, recorder.Statistics.ConsecutiveHeartbeatFailures);
    }

    [Fact]
    public void ResetConsecutiveHeartbeatFailures_ClearsTheRunningCount()
    {
        var recorder = new ConnectionStatisticsRecorder();
        recorder.IncrementConsecutiveHeartbeatFailures();

        recorder.ResetConsecutiveHeartbeatFailures();

        Assert.Equal(0, recorder.Statistics.ConsecutiveHeartbeatFailures);
    }

    [Fact]
    public void SetConnectedSince_SetsAndClearsTheConnectionTime()
    {
        var recorder = new ConnectionStatisticsRecorder();

        recorder.SetConnectedSince(Moment);
        Assert.Equal(Moment, recorder.Statistics.ConnectedSince);

        recorder.SetConnectedSince(null);
        Assert.Null(recorder.Statistics.ConnectedSince);
    }
}
