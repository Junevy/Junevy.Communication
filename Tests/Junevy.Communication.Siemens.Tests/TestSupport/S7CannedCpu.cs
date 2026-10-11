using Junevy.Communication.Channels;
using Junevy.Communication.Siemens.Framing;
using Junevy.Communication.Siemens.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Siemens.Tests.S7RequestFrame;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 固定应答的 S7 CPU 替身（计划 Task 9 第 1、4 条）：握手按给定的 Setup 向量协商 PDU，
/// 读请求按"请求的字节数"回 <c>readData</c> 给出的内容，写请求一律回 V-S7-8（写应答不含数据，与长度无关）。
/// 经 <see cref="DuplexClientChannel"/> 与 <see cref="DeviceSimulator"/> 跑在内存流上，因此没有套接字时序；
/// 只记录读与写的请求帧，握手帧不计。替身不实现设备内存，因此数值只用于逐字节核对第 17.1 节的向量。
/// </summary>
internal sealed class S7CannedCpu : IAsyncDisposable
{
    /// <summary>Setup Communication 的服务号（V-S7-3）。</summary>
    private const byte SetupService = 0xF0;

    /// <summary>读应答的数据项头字节数：返回码 + 传输尺寸 + 2 字节长度（17.1 节说明段）。</summary>
    private const int ItemHeaderBytes = 4;

    private readonly List<byte[]> requests;
    private readonly S7Client client;

    private S7CannedCpu(S7Client client, List<byte[]> requests)
    {
        this.client = client;
        this.requests = requests;
    }

    /// <summary>已连接的客户端。</summary>
    public S7Client Client => client;

    /// <summary>客户端发出的读写请求，按顺序。</summary>
    public IReadOnlyList<byte[]> Requests => requests;

    /// <summary>连接替身：握手完成后返回客户端与请求记录。</summary>
    /// <param name="readData">按请求的字节数给出读应答的数据。</param>
    /// <param name="options">客户端选项；为 null 时使用默认值。</param>
    /// <param name="setupAck">Setup 应答向量，决定协商的 PDU 长度（默认 V-S7-4 的 240）。</param>
    /// <returns>已连接的替身。</returns>
    public static async Task<S7CannedCpu> StartAsync(
        Func<int, byte[]> readData, S7ClientOptions? options = null, string setupAck = V_S7_4)
    {
        var recorded = new List<byte[]>();
        S7ClientOptions clientOptions = options ?? new S7ClientOptions();
        S7ProtocolSettings settings = S7ProtocolSettings.From(clientOptions);
        var session = new S7Session();
        ChannelComponents components = S7ProtocolProfile.Configure(new ChannelComponents(), settings, session, ChannelDelivery.Stream);
        var channel = new DuplexClientChannel(new ClientChannelSettings(), components, pair =>
        {
            _ = new DeviceSimulator(S7Framing.Create(), frame => Reply(frame, recorded, readData, setupAck))
                .RunAsync(pair.B, CancellationToken.None);
            return Task.CompletedTask;
        });

        var client = new S7Client(channel, settings, clientOptions, session, null);
        Succeeded(await client.ConnectAsync());
        return new S7CannedCpu(client, recorded);
    }

    /// <summary>释放客户端及其通道。</summary>
    public ValueTask DisposeAsync() => client.DisposeAsync();

    private static byte[] Reply(byte[] frame, List<byte[]> recorded, Func<int, byte[]> readData, string setupAck)
    {
        if (IsConnectionRequest(frame))
            return ToBytes(V_S7_2);

        ushort reference = ReferenceOf(frame);
        switch (Service(frame))
        {
            case SetupService:
                return WithReference(setupAck, reference);

            case S7VarItem.ReadService:
                recorded.Add(frame);
                return ReadAck(reference, readData(Quantity(frame)));

            case S7VarItem.WriteService:
                recorded.Add(frame);
                return WithReference(V_S7_8, reference);

            default:
                throw new InvalidOperationException($"The canned CPU received service 0x{Service(frame):X2}.");
        }
    }

    // 以 V-S7-6 为模板按负载长度改写三处长度字段：TPKT 总长、应答头的数据长度（项头 + 负载）、数据项长度（按位计）。
    private static byte[] ReadAck(ushort reference, byte[] payload)
    {
        var ack = new byte[ItemPayload + payload.Length];
        ToBytes(V_S7_6).AsSpan(0, ItemPayload).CopyTo(ack);
        payload.CopyTo(ack, ItemPayload);

        WriteLength(ack, TpktLengthHigh, (ushort)ack.Length);
        WriteLength(ack, DataLengthHigh, (ushort)(ItemHeaderBytes + payload.Length));
        WriteLength(ack, UnitLengthHigh, (ushort)(payload.Length * S7VarItem.BitsPerByte));
        return WithReference(ack, reference);
    }

    private static void WriteLength(byte[] frame, int highIndex, ushort value)
    {
        frame[highIndex] = (byte)(value >> 8);
        frame[highIndex + 1] = (byte)value;
    }
}
