using System.Collections.ObjectModel;

namespace Junevy.Communication.DebugAssistant.Logging;

/// <summary>
/// 页签的报文与日志。写入可以来自任何线程（<see cref="Append"/>）；其余成员只在界面线程上使用。
/// 条目先进入有界的待处理队列，由 <see cref="Flush"/> 按批（约每 100 ms）移入 <see cref="Visible"/>（DA-D10）。
/// </summary>
public sealed class SessionLog
{
    public const int DefaultCapacity = 5000;

    private readonly int _capacity;
    private readonly LinkedList<SessionEntry> _all = new();
    private readonly object _pendingGate = new();
    private readonly Queue<SessionEntry> _pending = new();
    private FrameStatistics _statistics = FrameStatistics.Empty;
    private LogFilter _filter = LogFilter.All;
    private bool _paused;

    public SessionLog(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>界面绑定的可见条目（已通过筛选，并按上限裁剪）。</summary>
    public ObservableCollection<SessionEntry> Visible { get; } = [];

    /// <summary>可见条目与待处理队列的上限。</summary>
    public int Capacity => _capacity;

    /// <summary>批量刷新或筛选变化之后触发（界面线程）。</summary>
    public event EventHandler? Changed;

    /// <summary>筛选条件；修改时立即重建可见条目。</summary>
    public LogFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            Rebuild();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>暂停时不再刷新界面；条目继续进入待处理队列（队列满时丢弃最旧的）。</summary>
    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>收发统计（任何线程可读）。计数在写入时完成，不受暂停与队列裁剪影响。</summary>
    public FrameStatistics Statistics
    {
        get
        {
            lock (_pendingGate)
            {
                return _statistics;
            }
        }
    }

    /// <summary>有待处理的条目（任何线程可读）。</summary>
    public bool HasPending
    {
        get
        {
            lock (_pendingGate)
            {
                return _pending.Count > 0;
            }
        }
    }

    /// <summary>写入一条条目。可以从任何线程调用。</summary>
    public void Append(SessionEntry entry)
    {
        lock (_pendingGate)
        {
            _pending.Enqueue(entry);
            if (_pending.Count > _capacity)
            {
                _pending.Dequeue();
            }

            if (entry is FrameEntry frame)
            {
                _statistics = _statistics.Add(frame);
            }
        }
    }

    /// <summary>把待处理的条目移入可见集合（界面线程）。暂停时不做任何事。</summary>
    public void Flush()
    {
        if (_paused)
        {
            return;
        }

        List<SessionEntry> batch;
        lock (_pendingGate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = [.. _pending];
            _pending.Clear();
        }

        foreach (SessionEntry entry in batch)
        {
            _all.AddLast(entry);
            if (_all.Count > _capacity)
            {
                _all.RemoveFirst();
            }

            if (_filter.Matches(entry))
            {
                AddVisible(entry);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>清空可见条目与待处理队列（界面线程）。统计保留。</summary>
    public void Clear()
    {
        _all.Clear();
        lock (_pendingGate)
        {
            _pending.Clear();
        }

        Visible.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        Visible.Clear();
        foreach (SessionEntry entry in _all)
        {
            if (_filter.Matches(entry))
            {
                AddVisible(entry);
            }
        }
    }

    private void AddVisible(SessionEntry entry)
    {
        Visible.Add(entry);
        while (Visible.Count > _capacity)
        {
            Visible.RemoveAt(0);
        }
    }
}
