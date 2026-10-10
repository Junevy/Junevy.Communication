using Junevy.Communication.Channels;

namespace Junevy.Communication.Serial.Tests;

/// <summary>收集通道派发的 <c>FrameReceived</c> 事件（按派发顺序），线程安全。</summary>
internal sealed class FrameSink
{
    private readonly object sync = new object();
    private readonly List<byte[]> frames = new List<byte[]>();

    /// <summary>订阅通道的帧事件。</summary>
    /// <param name="channel">要收集的通道。</param>
    public FrameSink(IClientChannel channel)
    {
        channel.FrameReceived += (sender, args) =>
        {
            lock (sync)
                frames.Add(args.Data);
        };
    }

    /// <summary>已收集的帧数。</summary>
    public int Count
    {
        get
        {
            lock (sync)
                return frames.Count;
        }
    }

    /// <summary>已收集帧的快照，按派发顺序排列。</summary>
    public IReadOnlyList<byte[]> Snapshot()
    {
        lock (sync)
            return frames.ToArray();
    }
}
