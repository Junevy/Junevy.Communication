using Junevy.Communication.Serial.Internal;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 假端口工厂：每次 <see cref="Create"/> 产生一个新的 <see cref="FakeSerialPortHandle"/>（每次打开、每次重连都会调用）。
/// 所有创建过的端口按创建顺序保存，供测试检查与操纵。
/// </summary>
internal sealed class FakeSerialPortHandleFactory : ISerialPortHandleFactory
{
    private readonly object sync = new object();
    private readonly List<FakeSerialPortHandle> handles = new List<FakeSerialPortHandle>();

    /// <summary>每创建一个端口后调用，参数为创建序号（从 0 开始）与端口本身，用于为个别打开设置行为。</summary>
    public Action<int, FakeSerialPortHandle>? Configure { get; set; }

    /// <summary>已经调用过 <see cref="Create"/> 的次数（即打开尝试的次数）。</summary>
    public int CreateCount
    {
        get
        {
            lock (sync)
                return handles.Count;
        }
    }

    /// <summary>所有已创建端口的快照，按创建顺序排列。</summary>
    public IReadOnlyList<FakeSerialPortHandle> Handles
    {
        get
        {
            lock (sync)
                return handles.ToArray();
        }
    }

    /// <inheritdoc />
    public ISerialPortHandle Create(SerialChannelConfig config)
    {
        var handle = new FakeSerialPortHandle(config, DuplexStreamPair.Create());
        int index;
        lock (sync)
        {
            index = handles.Count;
            handles.Add(handle);
        }

        Configure?.Invoke(index, handle);
        return handle;
    }
}
