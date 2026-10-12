using Junevy.Communication.Channels;
using Junevy.Communication.Omron.Framing;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 固定应答的 FINS 设备替身（计划 Task 13 第 5 条）：每次打开连接都应答一次节点地址请求，节点号由
/// <c>assignClientNode</c> 按握手次数给出（重连后另派新号的场景）；读请求按"请求的字数 × 2"回
/// <c>readData</c> 给出的内容，写请求一律回 V-F-5 的应答。经 <see cref="DuplexClientChannel"/> 与
/// <see cref="DeviceSimulator"/> 跑在内存流上，因此没有套接字时序。只记录读写请求帧，握手帧不计。
/// </summary>
internal sealed class FinsCannedPlc : IAsyncDisposable
{
    private readonly List<byte[]> requests;
    private readonly FinsClient client;

    private FinsCannedPlc(FinsClient client, List<byte[]> requests)
    {
        this.client = client;
        this.requests = requests;
    }

    /// <summary>已连接的客户端。</summary>
    public FinsClient Client => client;

    /// <summary>客户端发出的读写请求，按顺序。</summary>
    public IReadOnlyList<byte[]> Requests => requests;

    /// <summary>连接替身：节点地址握手完成后返回客户端与请求记录。</summary>
    /// <param name="readData">按请求的数据字节数给出读应答的数据。</param>
    /// <param name="options">客户端选项；为 null 时使用默认值。</param>
    /// <param name="assignClientNode">按握手次序（从 1 开始）给出派给客户端的节点号；为 null 时固定用向量里的 0x0B。</param>
    /// <returns>已连接的替身。</returns>
    public static async Task<FinsCannedPlc> StartAsync(
        Func<int, byte[]> readData,
        FinsClientOptions? options = null,
        Func<int, byte>? assignClientNode = null)
    {
        var recorded = new List<byte[]>();
        FinsClientOptions clientOptions = options ?? new FinsClientOptions();
        FinsProtocolSettings settings = FinsProtocolSettings.From(clientOptions);
        var session = new FinsSession();
        Func<int, byte> assign = assignClientNode ?? (_ => ClientNode);
        var handshakes = 0;

        ChannelComponents components = FinsProtocolProfile.Configure(new ChannelComponents(), settings, session, ChannelDelivery.Stream);
        var channel = new DuplexClientChannel(new ClientChannelSettings(), components, pair =>
        {
            byte clientNode = assign(Interlocked.Increment(ref handshakes));
            _ = new DeviceSimulator(FinsFraming.Create(), frame => Reply(frame, recorded, readData, clientNode))
                .RunAsync(pair.B, CancellationToken.None);
            return Task.CompletedTask;
        });

        var client = new FinsClient(channel, settings, clientOptions, session, null);
        Succeeded(await client.ConnectAsync());
        return new FinsCannedPlc(client, recorded);
    }

    /// <summary>释放客户端及其通道。</summary>
    public ValueTask DisposeAsync() => client.DisposeAsync();

    private static byte[] Reply(byte[] frame, List<byte[]> recorded, Func<int, byte[]> readData, byte clientNode)
    {
        if (!FinsTcpFrame.TryUnwrap(frame, out uint command, out _, out _, out string? violation))
            throw new InvalidOperationException($"The canned PLC received an unframed request: {violation}");

        if (command == FinsTcpFrame.CommandNodeRequest)
            return NodeAck(clientNode, ServerNode);

        ushort service = (ushort)(frame[CommandIndex] << 8 | frame[CommandIndex + 1]);
        byte sessionId = SessionIdOf(frame);
        recorded.Add(frame);
        return service switch
        {
            FinsMemoryArea.CommandRead => ReadAck(sessionId, readData(QuantityOf(frame) * FinsMemoryArea.WordBytes)),
            FinsMemoryArea.CommandWrite => WriteAck(sessionId),
            _ => throw new InvalidOperationException($"The canned PLC received service 0x{service:X4}."),
        };
    }
}
