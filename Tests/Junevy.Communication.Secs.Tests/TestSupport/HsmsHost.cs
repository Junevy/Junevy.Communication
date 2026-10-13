using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>主机收到的一帧：连接序号、十六进制原文与解析结果。</summary>
internal sealed class HsmsHostFrame
{
    /// <summary>记录一帧。</summary>
    /// <param name="connection">它来自第几个连接（从 0 开始）。</param>
    /// <param name="bytes">完整报文。</param>
    internal HsmsHostFrame(int connection, byte[] bytes)
    {
        Connection = connection;
        Text = HexVector.ToText(bytes);
        Message = HsmsTestFrames.Parse(bytes);
    }

    /// <summary>它来自第几个连接（从 0 开始）。</summary>
    internal int Connection { get; }

    /// <summary>十六进制原文。</summary>
    internal string Text { get; }

    /// <summary>解析结果。</summary>
    internal HsmsMessage Message { get; }
}

/// <summary>主机侧收到的帧队列：记录并按到达顺序取出。</summary>
internal sealed class HsmsHostInbox
{
    private readonly ConcurrentQueue<HsmsHostFrame> frames = new ConcurrentQueue<HsmsHostFrame>();

    /// <summary>收到一帧。</summary>
    /// <param name="connection">连接序号。</param>
    /// <param name="bytes">完整报文。</param>
    internal void Add(int connection, byte[] bytes) => frames.Enqueue(new HsmsHostFrame(connection, bytes));

    /// <summary>还没取走的帧数。</summary>
    internal int Count => frames.Count;

    /// <summary>取下一帧；<paramref name="milliseconds"/> 内没有新帧则抛出超时。</summary>
    /// <param name="milliseconds">等待上限（毫秒）。</param>
    /// <returns>最早到达的那一帧。</returns>
    internal async Task<HsmsHostFrame> TakeAsync(int milliseconds)
    {
        await Polling.WaitUntilAsync(() => frames.Count > 0, milliseconds).ConfigureAwait(false);
        return frames.TryDequeue(out HsmsHostFrame? frame)
            ? frame
            : throw new InvalidOperationException("The frame was taken by another waiter.");
    }
}

/// <summary>
/// 最小主机的"连接"一侧（设备端监听时用）：连上设备端，只做报文收发——分帧与编码取自被测库
/// （<see cref="HsmsFraming"/> 与共享测试库的 <see cref="DeviceSimulator"/>），不做任何 HSMS 语义。
/// </summary>
/// <remarks>
/// 发送只有这一个出口（<see cref="SendAsync"/>）：应答由测试自己按收到的报文构造，因此不存在两个写者交错的问题。
/// </remarks>
internal sealed class HsmsHostConnector : IAsyncDisposable
{
    private const int ConnectionIndex = 0;

    private readonly TcpClient client;
    private readonly NetworkStream stream;
    private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
    private readonly HsmsHostInbox inbox = new HsmsHostInbox();
    private readonly Task pump;
    private volatile bool peerClosed;

    private HsmsHostConnector(TcpClient client)
    {
        this.client = client;
        stream = client.GetStream();
        pump = PumpAsync();
    }

    /// <summary>连接到设备端的监听端口。</summary>
    /// <param name="port">设备端监听的端口。</param>
    /// <returns>主机一侧；调用方负责释放。</returns>
    internal static async Task<HsmsHostConnector> ConnectAsync(int port)
    {
        var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        return new HsmsHostConnector(client);
    }

    /// <summary>还没取走的帧数。</summary>
    internal int ReceivedCount => inbox.Count;

    /// <summary>取下一帧；<paramref name="milliseconds"/> 内没有新帧则抛出超时。</summary>
    /// <param name="milliseconds">等待上限（毫秒）。</param>
    /// <returns>最早到达的那一帧。</returns>
    internal Task<HsmsHostFrame> NextFrameAsync(int milliseconds) => inbox.TakeAsync(milliseconds);

    /// <summary>等设备端关闭连接；<paramref name="milliseconds"/> 内没关则抛出超时。</summary>
    /// <param name="milliseconds">等待上限（毫秒）。</param>
    /// <returns>关闭后返回的任务。</returns>
    internal async Task AwaitPeerCloseAsync(int milliseconds)
        => await Polling.WaitUntilAsync(() => peerClosed, milliseconds).ConfigureAwait(false);

    /// <summary>发一条完整报文。</summary>
    /// <param name="frame">完整报文（带 4 字节长度前缀）。</param>
    /// <returns>发送结束后的任务。</returns>
    internal async Task SendAsync(byte[] frame)
    {
        await stream.WriteAsync(frame, 0, frame.Length).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>停止监听并关闭连接。可重复调用。</summary>
    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        client.Dispose();
        await pump.ConfigureAwait(false);
    }

    // 读侧由共享测试库的分帧应答器承担：记录每一帧，不自动应答。
    private Task PumpAsync()
    {
        var simulator = new DeviceSimulator(
            HsmsFraming.Create(HsmsTimers.MaxMessageLength),
            frame =>
            {
                inbox.Add(ConnectionIndex, frame);
                return null;
            });

        return ReadUntilClosedAsync(simulator);
    }

    // 读到 EOF、本地释放或链路复位都表示连接结束；此时读侧看到的帧已经全部入队。
    private async Task ReadUntilClosedAsync(DeviceSimulator simulator)
    {
        try
        {
            await simulator.RunAsync(stream, shutdown.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 连接已经不存在了：DisposeAsync 关掉客户端后读流必然失败。
        }
        finally
        {
            peerClosed = true;
        }
    }
}

/// <summary>主机的应答脚本：参数是连接序号与收到的帧，返回 null 表示不应答。</summary>
internal delegate byte[]? HsmsHostScript(int connection, byte[] frame);

/// <summary>
/// 最小主机的"监听"一侧（设备端主动连接时用）：监听回环端口，按脚本应答每一条报文。
/// 分帧与编码同样取自被测库，脚本只看得到帧与连接序号。
/// </summary>
internal sealed class HsmsHostListener : IDisposable
{
    private readonly ScriptedTcpServer server;

    private HsmsHostListener(ScriptedTcpServer server, HsmsHostInbox inbox)
    {
        this.server = server;
        Inbox = inbox;
    }

    /// <summary>收到的帧队列。</summary>
    internal HsmsHostInbox Inbox { get; }

    /// <summary>监听的端口（回环地址，随机分配）。</summary>
    internal int Port => server.Port;

    /// <summary>已接受的连接数。</summary>
    internal int AcceptedCount => server.AcceptedConnectionCount;

    /// <summary>开始监听。</summary>
    /// <param name="script">应答脚本。</param>
    /// <returns>主机一侧；调用方负责释放。</returns>
    internal static HsmsHostListener Listen(HsmsHostScript script)
    {
        var inbox = new HsmsHostInbox();
        ScriptedTcpServer server = ScriptedTcpServer.Start(
            (index, stream, token) => new DeviceSimulator(
                    HsmsFraming.Create(HsmsTimers.MaxMessageLength),
                    frame =>
                    {
                        inbox.Add(index, frame);
                        return script(index, frame);
                    })
                .RunAsync(stream, token));
        return new HsmsHostListener(server, inbox);
    }

    /// <summary>取下一帧；<paramref name="milliseconds"/> 内没有新帧则抛出超时。</summary>
    /// <param name="milliseconds">等待上限（毫秒）。</param>
    /// <returns>最早到达的那一帧。</returns>
    internal Task<HsmsHostFrame> NextFrameAsync(int milliseconds) => Inbox.TakeAsync(milliseconds);

    /// <summary>主动断开第 <paramref name="index"/> 个连接（按接受顺序，从 0 开始）。</summary>
    /// <param name="index">连接序号。</param>
    internal void DropConnection(int index) => server.CloseConnection(index);

    /// <summary>停止监听并关闭全部连接。可重复调用。</summary>
    public void Dispose() => server.Dispose();
}

/// <summary>主机的应答脚本库。</summary>
internal static class HsmsHostScripts
{
    /// <summary>应答 <c>Select.req</c>（状态 0）与 <c>Linktest.req</c>，其余不应答。</summary>
    /// <param name="connection">连接序号。</param>
    /// <param name="frame">收到的帧。</param>
    /// <returns>应答报文；不应答时为 null。</returns>
    internal static byte[]? SelectAndLinktest(int connection, byte[] frame) => Answer(frame, HsmsControl.SelectOk);

    /// <summary>把 <c>Select.req</c> 应答成指定状态码；其余不应答。</summary>
    /// <param name="status">Select.rsp 的状态码。</param>
    /// <returns>脚本。</returns>
    internal static HsmsHostScript SelectWithStatus(byte status) => (connection, frame) => Answer(frame, status);

    /// <summary>第一个连接选中成功，之后的 <c>Select.req</c> 应答成指定状态码（重连被拒的用例）。</summary>
    /// <param name="status">重连时 <c>Select.rsp</c> 的状态码。</param>
    /// <returns>脚本。</returns>
    internal static HsmsHostScript SelectThenRejectWithStatus(byte status)
        => (connection, frame) => Answer(frame, connection == 0 ? HsmsControl.SelectOk : status);

    /// <summary>第一个连接选中成功，之后的 <c>Select.req</c> 一律不应答（T6 超时的用例）。</summary>
    /// <returns>脚本。</returns>
    internal static HsmsHostScript SelectThenSilence()
        => (connection, frame) => connection == 0 ? Answer(frame, HsmsControl.SelectOk) : null;

    // 应答只有两类：Select.rsp 与 Linktest.rsp，都原样回送对方的 System Bytes。
    private static byte[]? Answer(byte[] frame, byte selectStatus)
    {
        HsmsMessage message = HsmsTestFrames.Parse(frame);
        return message.Kind switch
        {
            HsmsControlKind.SelectRequest => HsmsControl.SelectResponse(message.SystemBytes, selectStatus).ToArray(),
            HsmsControlKind.LinktestRequest => HsmsControl.LinktestResponse(message.SystemBytes).ToArray(),
            _ => null,
        };
    }
}
