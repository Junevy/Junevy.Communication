using System.Threading;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Memory;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Testing;
using Junevy.Communication.Udp;

namespace Junevy.Communication.DebugAssistant.Tests.Simulators.EndToEnd;

/// <summary>
/// 端到端测试集合。真实串口对同一时间只能被一个测试打开，因此所有端到端测试（包括使用串口对的宿主测试）都放进这个不并行的集合。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public static class SimulatorEndToEndCollection
{
    /// <summary>集合名称。</summary>
    public const string Name = "SimulatorEndToEnd";
}

/// <summary>
/// 挂着模拟器宿主的测试场（计划第 8.3 节）。TCP 与 UDP 使用回环地址上的空闲端口；串口使用 <c>JUNEVY_SERIAL_PAIR</c> 的第一个端口，
/// 客户端连接第二个端口。存储区与故障注入由测试直接操作；请求数由宿主的 <see cref="SimulatorHost.RequestHandled"/> 计数。
/// </summary>
internal sealed class SimulatorRig : IAsyncDisposable
{
    private readonly SimulatorHost _host;
    private readonly int _port;
    private int _requests;

    private SimulatorRig(SimulatorTransport transport, SimulatorHost host, int port, SimulatorMemory memory, FaultInjector faults)
    {
        Transport = transport;
        _host = host;
        _port = port;
        Memory = memory;
        Faults = faults;
    }

    /// <summary>宿主的传输方式。</summary>
    public SimulatorTransport Transport { get; }

    /// <summary>引擎使用的存储区。</summary>
    public SimulatorMemory Memory { get; }

    /// <summary>引擎所用的故障注入。</summary>
    public FaultInjector Faults { get; }

    /// <summary>宿主处理过的请求数（含不应答与断开）。</summary>
    public int RequestCount => Volatile.Read(ref _requests);

    /// <summary>
    /// 启动宿主。引擎由 <paramref name="createEngine"/> 用场里的存储区创建，测试因此可以在启动前预先写入数据。
    /// 启动失败时测试失败。
    /// </summary>
    public static async Task<SimulatorRig> StartAsync(SimulatorTransport transport, Func<SimulatorMemory, ISimulatorEngine> createEngine)
    {
        var memory = new SimulatorMemory();
        var faults = new FaultInjector();
        ISimulatorEngine engine = createEngine(memory);
        int port = transport == SimulatorTransport.Serial ? 0 : FreePort.Get();
        IChannelConfig hostConfig = transport switch
        {
            SimulatorTransport.Tcp => new TcpServerConfig { ListenAddress = "127.0.0.1", Port = port },
            SimulatorTransport.Udp => new UdpChannelConfig { LocalAddress = "127.0.0.1", LocalPort = port },
            _ => new SerialChannelConfig { PortName = SerialPairEnvironment.Read().First, BaudRate = 115200 },
        };

        var host = new SimulatorHost(hostConfig, engine, faults);
        var rig = new SimulatorRig(transport, host, port, memory, faults);
        host.RequestHandled += (_, _) => Interlocked.Increment(ref rig._requests);
        Assert.True((await host.StartAsync()).IsSuccess, "the simulator must start");
        return rig;
    }

    /// <summary>
    /// 客户端的通道配置：与宿主相同的传输方式，应答超时为 <paramref name="requestTimeoutMilliseconds"/>。
    /// 协议组件提供分帧，因此串口配置的分帧保持默认值。
    /// </summary>
    public IChannelConfig ClientConfig(int requestTimeoutMilliseconds) => Transport switch
    {
        SimulatorTransport.Tcp => new TcpClientChannelConfig { Host = "127.0.0.1", Port = _port, RequestTimeout = requestTimeoutMilliseconds },
        SimulatorTransport.Udp => new UdpChannelConfig { RemoteHost = "127.0.0.1", RemotePort = _port, RequestTimeout = requestTimeoutMilliseconds },
        _ => new SerialChannelConfig
        {
            PortName = SerialPairEnvironment.Read().Second,
            BaudRate = 115200,
            RequestTimeout = requestTimeoutMilliseconds,
        },
    };

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();
}
