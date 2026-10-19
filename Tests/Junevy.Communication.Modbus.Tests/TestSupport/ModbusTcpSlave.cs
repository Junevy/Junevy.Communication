using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 以 TCP 承载的模拟从站（测试专用）。每个入站连接由 <see cref="DeviceSimulator.RunAsync"/> 处理；
/// 回复可以按序号延迟（迟到应答）。延迟的回复在后台发出，不阻塞后续请求的处理——与真实设备的行为一致。
/// </summary>
internal sealed class ModbusTcpSlave : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly DeviceSimulator simulator;
    private readonly Func<int, int>? replyDelayMs;
    private readonly CancellationTokenSource stop = new CancellationTokenSource();
    private readonly Task acceptLoop;
    private int acceptedConnections;
    private int replies;

    /// <summary>创建并开始监听回环地址的随机端口。</summary>
    /// <param name="framing">帧格式。</param>
    /// <param name="slave">模拟从站。</param>
    /// <param name="replyDelayMs">按回复序号（从 1 开始）返回回复前等待的毫秒数；null 或 0 表示立即回复。</param>
    public ModbusTcpSlave(ModbusFraming framing, ModbusSlave slave, Func<int, int>? replyDelayMs = null)
    {
        this.replyDelayMs = replyDelayMs;
        simulator = new DeviceSimulator(ModbusDeviceCodecs.For(framing), slave.Handle);
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = AcceptAsync();
    }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>已接受的连接数（客户端重连时增加）。</summary>
    public int AcceptedConnectionCount => Volatile.Read(ref acceptedConnections);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try
        {
            await acceptLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 监听器已停止：正常结束。
        }
    }

    private async Task AcceptAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            Interlocked.Increment(ref acceptedConnections);
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = new DelayingStream(client.GetStream(), NextDelay);
                await simulator.RunAsync(stream, stop.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 连接结束（对端关闭或释放）：不影响其他连接。
            }
        }
    }

    private int NextDelay()
    {
        if (replyDelayMs is null)
            return 0;

        return replyDelayMs(Interlocked.Increment(ref replies));
    }
}
