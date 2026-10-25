namespace Junevy.Communication.DebugAssistant.Simulators.Memory;

/// <summary>
/// 模拟设备的一个存储区（计划第 8.1 节）：读写加锁，越界抛出 <see cref="ArgumentOutOfRangeException"/>，
/// 写入后在锁外触发 <see cref="Changed"/>，界面据此节流刷新。
/// </summary>
public abstract class MemoryArea<T>
{
    private readonly object _gate = new();
    private readonly T[] _data;

    protected MemoryArea(string name, int length)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        Name = name;
        _data = new T[length];
    }

    /// <summary>存储区名称，例如 "HR"、"DB1"。</summary>
    public string Name { get; }

    /// <summary>元素个数。</summary>
    public int Length => _data.Length;

    /// <summary>写入后触发（在锁外），参数为存储区名称与写入的起始位置和个数。</summary>
    public event EventHandler<MemoryAreaChangedEventArgs>? Changed;

    /// <summary>读取从 <paramref name="start"/> 开始的 <paramref name="count"/> 个元素。</summary>
    public T[] Read(int start, int count)
    {
        CheckRange(start, count);
        lock (_gate)
        {
            return _data.AsSpan(start, count).ToArray();
        }
    }

    /// <summary>从 <paramref name="start"/> 开始写入 <paramref name="values"/>。</summary>
    public void Write(int start, ReadOnlySpan<T> values)
    {
        CheckRange(start, values.Length);
        lock (_gate)
        {
            values.CopyTo(_data.AsSpan(start));
        }

        Changed?.Invoke(this, new MemoryAreaChangedEventArgs(Name, start, values.Length));
    }

    private void CheckRange(int start, int count)
    {
        if (start < 0 || count < 0 || start > _data.Length - count)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, $"存储区 {Name} 的范围 {start}+{count} 超出长度 {_data.Length}。");
        }
    }
}

/// <summary>存储区变化的参数：存储区名称，以及写入的起始位置和个数。</summary>
public sealed record MemoryAreaChangedEventArgs(string Area, int Start, int Count);
