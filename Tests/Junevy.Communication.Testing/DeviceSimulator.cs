using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Channels;

namespace Junevy.Communication.Testing;

/// <summary>
/// 模拟设备：按 <see cref="IFrameCodecFactory"/> 解析入站帧，把每帧交给回调；回调返回非 null 时，按同一编码规则回复。
/// 可以在任意流上运行（<see cref="DuplexStreamPair"/> 或 TCP 连接），也可以通过 <see cref="HostTcp"/> 以 TCP 承载。
/// </summary>
public sealed class DeviceSimulator
{
    private readonly IFrameCodecFactory codec;
    private readonly Func<byte[], byte[]?> handler;

    /// <summary>
    /// 创建模拟设备。
    /// </summary>
    /// <param name="codec">分帧与编码规则。</param>
    /// <param name="handler">帧处理回调：返回 null 表示不回复。</param>
    /// <exception cref="ArgumentNullException">任一参数为 null。</exception>
    public DeviceSimulator(IFrameCodecFactory codec, Func<byte[], byte[]?> handler)
    {
        this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
        this.handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    /// <summary>
    /// 在流上应答，直到对端关闭、链路中断或取消。取消与链路中断都视为正常结束，不抛出异常。
    /// </summary>
    /// <param name="stream">设备一侧的流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>运行结束的任务。</returns>
    public async Task RunAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        IFrameDecoder decoder = codec.CreateDecoder();
        IFrameEncoder encoder = codec.CreateEncoder();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipe = new Pipe();
        _ = FillAsync(stream, pipe.Writer, session.Token);

        try
        {
            while (true)
            {
                ReadResult result = await pipe.Reader.ReadAsync(session.Token).ConfigureAwait(false);
                ReadOnlySequence<byte> buffer = result.Buffer;
                while (decoder.TryDecode(ref buffer, out ReadOnlySequence<byte> frame))
                {
                    byte[]? reply = handler(frame.ToArray());
                    if (reply != null)
                        await WriteReplyAsync(stream, encoder, reply, session.Token).ConfigureAwait(false);
                }

                // 未成帧的数据保留在缓冲中，等待后续数据到达。
                pipe.Reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方取消：正常结束。
        }
        catch (IOException)
        {
            // 链路中断：设备掉线，正常结束。
        }
        finally
        {
            session.Cancel();
            pipe.Reader.Complete();
        }
    }

    /// <summary>
    /// 以 TCP 承载本模拟设备：在回环地址的随机端口上同步开始监听，立即返回。每个连接由 <see cref="RunAsync"/> 处理。
    /// 与被测的 TcpServer 无关，直接使用 <see cref="TcpListener"/>。
    /// </summary>
    /// <returns>监听宿主；调用方负责释放。</returns>
    public DeviceSimulatorTcpHost HostTcp() => new DeviceSimulatorTcpHost(this);

    /// <summary>
    /// 把流的内容读入管道；流结束或出错时完成写入端（错误视为流结束，由解析循环处理）。
    /// </summary>
    private static async Task FillAsync(Stream stream, PipeWriter writer, CancellationToken cancellationToken)
    {
        var chunk = new byte[4096];
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                FlushResult flush = await writer.WriteAsync(new ReadOnlyMemory<byte>(chunk, 0, read), cancellationToken)
                    .ConfigureAwait(false);
                if (flush.IsCompleted)
                    break;
            }
        }
        catch (Exception)
        {
            // 链路错误或取消：结束填充循环。
        }
        finally
        {
            writer.Complete();
        }
    }

    private static async Task WriteReplyAsync(Stream stream, IFrameEncoder encoder, byte[] payload, CancellationToken cancellationToken)
    {
        var output = new ByteArrayBufferWriter();
        encoder.Encode(payload, output);

        byte[] wire = output.ToArray();
        await stream.WriteAsync(wire, 0, wire.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 以 TCP 承载的模拟设备，由 <see cref="DeviceSimulator.HostTcp"/> 创建。每个入站连接在独立任务中由 <see cref="DeviceSimulator.RunAsync"/> 处理。
/// </summary>
public sealed class DeviceSimulatorTcpHost : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly DeviceSimulator simulator;
    private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
    private readonly List<TcpClient> clients = new List<TcpClient>();
    private readonly object clientsLock = new object();
    private int acceptedConnectionCount;
    private volatile bool disposed;

    internal DeviceSimulatorTcpHost(DeviceSimulator simulator)
    {
        this.simulator = simulator;
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        var endPoint = listener.LocalEndpoint as IPEndPoint
            ?? throw new InvalidOperationException("The listener is not bound to an IP endpoint.");
        Port = endPoint.Port;

        _ = AcceptLoopAsync();
    }

    /// <summary>设备监听的端口（回环地址，随机分配）。</summary>
    public int Port { get; }

    /// <summary>已接受的连接数。</summary>
    public int AcceptedConnectionCount => Volatile.Read(ref acceptedConnectionCount);

    /// <summary>停止监听并关闭全部连接。可重复调用。</summary>
    public ValueTask DisposeAsync()
    {
        if (disposed)
            return default;
        disposed = true;

        shutdown.Cancel();

        try
        {
            listener.Stop();
        }
        catch (SocketException)
        {
            // 监听器已停止。
        }

        lock (clientsLock)
        {
            foreach (TcpClient client in clients)
                client.Dispose();

            clients.Clear();
        }

        return default;
    }

    private async Task AcceptLoopAsync()
    {
        while (!disposed)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // DisposeAsync 之后 listener.Stop() 使挂起的 Accept 抛出异常，正常退出。
                break;
            }

            lock (clientsLock)
                clients.Add(client);

            Interlocked.Increment(ref acceptedConnectionCount);
            _ = RunSessionAsync(client);
        }
    }

    private async Task RunSessionAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                await simulator.RunAsync(client.GetStream(), shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 单个连接的异常不终止设备宿主。
        }
    }
}
