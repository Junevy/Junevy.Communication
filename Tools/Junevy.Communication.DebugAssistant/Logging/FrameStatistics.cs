namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>页签的收发统计：帧数、字节数，以及最后一次收发的时间（计划第 5.3 节）。</summary>
public sealed record FrameStatistics(
    long TxFrames,
    long TxBytes,
    long RxFrames,
    long RxBytes,
    DateTimeOffset? LastTx,
    DateTimeOffset? LastRx)
{
    public static FrameStatistics Empty { get; } = new(0, 0, 0, 0, null, null);

    /// <summary>加入一帧，返回新的统计。</summary>
    public FrameStatistics Add(FrameEntry frame) => frame.Direction == FrameDirection.Tx
        ? this with { TxFrames = TxFrames + 1, TxBytes = TxBytes + frame.Length, LastTx = frame.Time }
        : this with { RxFrames = RxFrames + 1, RxBytes = RxBytes + frame.Length, LastRx = frame.Time };
}
