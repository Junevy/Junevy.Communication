namespace Junevy.Communication.DebugAssistant.Simulators.Memory;

/// <summary>
/// 模拟设备的全部存储区（计划第 8.1 节）：按名称登记字区、位区与字节区。变化的存储区名称由 <see cref="TakeDirtyAreas"/> 汇总，
/// 界面按固定间隔取出后刷新，而不是每次写入都刷新。
/// </summary>
public sealed class SimulatorMemory
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WordArea> _words = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, BitArea> _bits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ByteArea> _bytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>登记字区；名称重复时抛出 <see cref="ArgumentException"/>。</summary>
    public WordArea AddWords(string name, int length) => Register<WordArea, ushort>(_words, new WordArea(name, length));

    /// <summary>登记位区；名称重复时抛出 <see cref="ArgumentException"/>。</summary>
    public BitArea AddBits(string name, int length) => Register<BitArea, bool>(_bits, new BitArea(name, length));

    /// <summary>登记字节区；名称重复时抛出 <see cref="ArgumentException"/>。</summary>
    public ByteArea AddBytes(string name, int length) => Register<ByteArea, byte>(_bytes, new ByteArea(name, length));

    public WordArea? FindWords(string name) => Find(_words, name);

    public BitArea? FindBits(string name) => Find(_bits, name);

    public ByteArea? FindBytes(string name) => Find(_bytes, name);

    /// <summary>取出并清空自上次调用以来有变化的存储区名称。</summary>
    public IReadOnlyList<string> TakeDirtyAreas()
    {
        lock (_gate)
        {
            List<string> areas = [.. _dirty];
            _dirty.Clear();
            return areas;
        }
    }

    private TArea Register<TArea, TValue>(Dictionary<string, TArea> map, TArea area)
        where TArea : MemoryArea<TValue>
    {
        lock (_gate)
        {
            map.Add(area.Name, area);
        }

        area.Changed += (_, e) =>
        {
            lock (_gate)
            {
                _dirty.Add(e.Area);
            }
        };
        return area;
    }

    private TArea? Find<TArea>(Dictionary<string, TArea> map, string name)
        where TArea : class
    {
        lock (_gate)
        {
            return map.GetValueOrDefault(name);
        }
    }
}
