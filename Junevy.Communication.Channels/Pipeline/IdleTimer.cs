using System.Diagnostics;
using System.IO.Pipelines;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 解析循环使用的半帧 / 静默计时（P25 Task 10：自 <see cref="StreamChannel"/> 拆出为独立文件）。计时器只负责唤醒解析循环（<see cref="PipeReader.CancelPendingRead"/>）；
/// 是否到期由截止时间戳判定，因此计时器的提前或残留唤醒不会误判。
/// 仅由解析循环访问。
/// </summary>
internal sealed class IdleTimer : IDisposable
{
    private readonly System.Threading.Timer timer;
    private readonly int timeoutMs;
    private long deadline;
    private long armedLength = -1;

    public IdleTimer(PipeReader reader, int timeoutMs)
    {
        this.timeoutMs = timeoutMs;
        timer = new System.Threading.Timer(static state => ((PipeReader)state!).CancelPendingRead(), reader,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    /// <summary>是否启用（超时 > 0）。</summary>
    public bool IsEnabled => timeoutMs > 0;

    /// <summary>是否已武装（缓冲中有未成帧数据）。</summary>
    public bool IsArmed => armedLength >= 0;

    /// <summary>武装时缓冲中的字节数；没有新数据时缓冲长度与之相等。</summary>
    public long ArmedLength => armedLength;

    /// <summary>截止时间是否已到。</summary>
    public bool IsDue(long now) => now >= deadline;

    /// <summary>以当前时刻重新武装：截止时间 = 现在 + 超时（有新数据时调用）。</summary>
    public void Arm(long length)
    {
        deadline = Stopwatch.GetTimestamp() + ToTicks(timeoutMs);
        armedLength = length;
        timer.Change(timeoutMs, System.Threading.Timeout.Infinite);
    }

    /// <summary>按剩余时间重新安排唤醒，不改变截止时间（计时器提前唤醒时调用）。</summary>
    public void Resume(long now)
        => timer.Change(ToMilliseconds(deadline - now), System.Threading.Timeout.Infinite);

    /// <summary>撤销计时。</summary>
    public void Disarm()
    {
        armedLength = -1;
        timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public void Dispose() => timer.Dispose();

    private static long ToTicks(int milliseconds)
        => (long)(milliseconds * (Stopwatch.Frequency / 1000.0));

    private static int ToMilliseconds(long ticks)
    {
        int milliseconds = (int)Math.Ceiling(ticks * 1000.0 / Stopwatch.Frequency);
        return Math.Max(1, milliseconds);
    }
}
