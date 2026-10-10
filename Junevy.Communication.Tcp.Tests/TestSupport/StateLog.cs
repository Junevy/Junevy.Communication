using System.Diagnostics;
using Junevy.Communication.Channels;

namespace Junevy.Communication.Tcp.Tests;

/// <summary>
/// 按派发顺序记录通道的状态事件，并附带派发时刻的 <see cref="Stopwatch"/> 时间戳（用于测量区间）。
/// </summary>
internal sealed class StateLog
{
    private readonly object sync = new object();
    private readonly List<StateEntry> entries = new List<StateEntry>();

    /// <summary>订阅通道的状态事件。</summary>
    /// <param name="channel">要记录的通道。</param>
    public StateLog(IConnectable channel)
    {
        channel.StateChanged += OnStateChanged;
    }

    /// <summary>已记录的全部事件（快照，按派发顺序）。</summary>
    public IReadOnlyList<StateEntry> Entries
    {
        get
        {
            lock (sync)
                return entries.ToList();
        }
    }

    /// <summary>
    /// 等待满足条件的事件出现（可限定在某个事件之后）。超时抛出 <see cref="TimeoutException"/>。
    /// </summary>
    /// <param name="predicate">事件条件。</param>
    /// <param name="milliseconds">等待上限（毫秒）。</param>
    /// <param name="after">只考虑时间戳晚于该事件的记录；为 null 时考虑全部记录。</param>
    /// <returns>第一条满足条件的事件。</returns>
    public async Task<StateEntry> WaitForAsync(Func<StateEntry, bool> predicate, int milliseconds, StateEntry? after = null)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            foreach (StateEntry entry in Entries)
            {
                if (after != null && entry.Timestamp <= after.Timestamp)
                    continue;

                if (predicate(entry))
                    return entry;
            }

            if (stopwatch.ElapsedMilliseconds > milliseconds)
                throw new TimeoutException($"No matching state change was observed within {milliseconds} ms.");

            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private void OnStateChanged(object? sender, ConnectionStateChangedEventArgs args)
    {
        lock (sync)
            entries.Add(new StateEntry(args, Stopwatch.GetTimestamp()));
    }
}

/// <summary>
/// 一条状态事件的记录。
/// </summary>
internal sealed class StateEntry
{
    /// <summary>创建记录。</summary>
    public StateEntry(ConnectionStateChangedEventArgs args, long timestamp)
    {
        Previous = args.PreviousState;
        Current = args.CurrentState;
        Reason = args.Reason;
        ReconnectAttempt = args.ReconnectAttempt;
        Timestamp = timestamp;
    }

    /// <summary>变化前的状态。</summary>
    public ConnectionState Previous { get; }

    /// <summary>变化后的状态。</summary>
    public ConnectionState Current { get; }

    /// <summary>原因。</summary>
    public DisconnectReason Reason { get; }

    /// <summary>重连尝试序号。</summary>
    public int ReconnectAttempt { get; }

    /// <summary>派发时刻的 <see cref="Stopwatch"/> 时间戳。</summary>
    public long Timestamp { get; }

    /// <summary>与更早的事件之间的毫秒数。</summary>
    public double MillisecondsSince(StateEntry earlier)
        => (Timestamp - earlier.Timestamp) * 1000.0 / Stopwatch.Frequency;
}
