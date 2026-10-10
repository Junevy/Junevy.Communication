using Junevy.Communication.Serial.Internal;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Serial.Tests;

/// <summary>
/// 假串口端口：以 <see cref="DuplexStreamPair"/> 代替 <c>SerialPort.BaseStream</c>，不触及任何真实串口。
/// 通道一侧是 <see cref="BaseStream"/>（A 端），设备一侧是 <see cref="Device"/>（B 端）。
/// </summary>
internal sealed class FakeSerialPortHandle : ISerialPortHandle
{
    private readonly DuplexStreamPair pair;
    private volatile bool isOpen;
    private volatile bool discardedWhileOpen;
    private int discardCount;
    private int disposeCount;

    /// <summary>创建假端口。</summary>
    /// <param name="config">通道交给工厂的端口参数（快照）。</param>
    /// <param name="pair">内存双工流对。</param>
    public FakeSerialPortHandle(SerialChannelConfig config, DuplexStreamPair pair)
    {
        Config = config;
        this.pair = pair;
    }

    /// <summary>通道交给工厂的端口参数（构造时的快照）。</summary>
    public SerialChannelConfig Config { get; }

    /// <summary>内存双工流对，测试用 <see cref="DuplexStreamPair.Abort"/> 模拟链路中断。</summary>
    public DuplexStreamPair Pair => pair;

    /// <summary>设备一侧的流（B 端）：测试通过它模拟设备发送与接收。</summary>
    public Stream Device => pair.B;

    /// <summary>非 null 时，<see cref="Open"/> 等待此门闩（最长 10 秒），用于模拟驱动阻塞。</summary>
    public ManualResetEventSlim? OpenGate { get; set; }

    /// <summary>非 null 时，<see cref="Open"/> 抛出此异常，用于模拟端口不存在、被占用或拒绝访问。</summary>
    public Exception? OpenException { get; set; }

    /// <inheritdoc />
    public Stream BaseStream => pair.A;

    /// <summary><see cref="DiscardInBuffer"/> 被调用的次数。</summary>
    public int DiscardCount => Volatile.Read(ref discardCount);

    /// <summary>最近一次 <see cref="DiscardInBuffer"/> 是否发生在端口已打开之后。</summary>
    public bool DiscardedWhileOpen => discardedWhileOpen;

    /// <summary><see cref="Dispose"/> 被调用的次数。</summary>
    public int DisposeCount => Volatile.Read(ref disposeCount);

    /// <inheritdoc />
    public void Open()
    {
        ManualResetEventSlim? gate = OpenGate;
        if (gate != null && !gate.Wait(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("The fake serial port was never released.");

        Exception? failure = OpenException;
        if (failure != null)
            throw failure;

        isOpen = true;
    }

    /// <inheritdoc />
    public void DiscardInBuffer()
    {
        Interlocked.Increment(ref discardCount);
        discardedWhileOpen = isOpen;
    }

    /// <summary>关闭假端口：打断两端的读写，模拟真实端口被关闭。</summary>
    public void Dispose()
    {
        Interlocked.Increment(ref disposeCount);
        isOpen = false;
        pair.Dispose();
    }
}
