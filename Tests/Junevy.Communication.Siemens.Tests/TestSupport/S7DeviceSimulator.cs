using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Siemens.Framing;
using Junevy.Communication.Siemens.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Siemens.Tests.S7RequestFrame;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// S7 设备替身（计划 Task 10 第 1 条）：把 Read / Write Var 翻译到一块按（区、DB 号）组织的字节内存上，
/// 分帧、握手校验与错误消息仍由被测的库负责——CC 直接回 V-S7-2，Setup 回 V-S7-4 的形状但协商值可配置。
/// 可配置项：协商的 PDU 长度（240 或 960）、PUT/GET 未开启（任何读写回头错误 0x81 0x04）、
/// 不应答（握手照常，之后的读写一律不回，用于心跳与 Dispose 用例）、应答里写死的引用号（用于引用号不符的用例）。
/// 没有内存的 DB 返回数据项码 0x0A，越过块尾的访问返回 0x05（17.3 节）。
/// </summary>
internal sealed class S7DeviceSimulator
{
    /// <summary>Setup Communication 的服务号（V-S7-3）。</summary>
    private const byte SetupService = 0xF0;

    // 应答外壳：TPKT 4 + COTP DT 3 + 头 12（协议号、ROSCTR、保留字、引用号、两个长度、错误类与错误码）。
    private const byte ProtocolId = 0x32;
    private const byte AckDataRosctr = 0x03;
    private const byte NoError = 0x00;
    private const byte PutGetErrorClass = 0x81;
    private const byte PutGetErrorCode = 0x04;
    private const int ResponseShellBytes = 19;

    // 参数块固定两项：服务号与项数（本替身只处理单项请求，P4-D12）。
    private const byte SingleItem = 0x01;
    private const byte SetupReserved = 0x00;

    // 区域代码 0x81–0x84 依次对应 S7Area 的四个区（17.3 节）；表外的代码查不到内存块，按 0x0A 回答。
    private const byte FirstAreaCode = 0x81;

    // 数据项错误码（17.3 节）：对象不存在与地址越界。
    private const byte ItemDoesNotExist = 0x0A;
    private const byte InvalidAddress = 0x05;

    // 位访问一次只推进一个字节，块尾检查按 1 字节计（17.1 节说明段）。
    private const int BitAccessBytes = 1;

    /// <summary>替身里每个内存块的字节数；块的末地址越界检查以此为准。</summary>
    private const int BlockBytes = 512;

    private readonly Dictionary<(S7Area, int), byte[]> blocks = new Dictionary<(S7Area, int), byte[]>
    {
        { (S7Area.Inputs, 0), new byte[BlockBytes] },
        { (S7Area.Outputs, 0), new byte[BlockBytes] },
        { (S7Area.Merkers, 0), new byte[BlockBytes] },
        { (S7Area.DataBlock, 1), new byte[BlockBytes] },
    };

    private readonly List<byte[]> requests = new List<byte[]>();
    private readonly byte[] connectionConfirm = ToBytes(V_S7_2);
    private readonly object gate = new object();

    private TaskCompletionSource<byte[]>? nextRequest;
    private int connectionRequestCount;

    /// <summary>创建替身。<paramref name="negotiatedPduBytes"/> 写进 Setup 应答（P4-D13 的下限是 64）。</summary>
    public S7DeviceSimulator(int negotiatedPduBytes = 240) => NegotiatedPduBytes = negotiatedPduBytes;

    /// <summary>Setup 应答里给出的 PDU 字节数。</summary>
    public int NegotiatedPduBytes { get; }

    /// <summary>PUT/GET 未开启：任何读写都回头错误 0x81 0x04（V-S7-12）。</summary>
    public bool PutGetDisabled { get; set; }

    /// <summary>不应答：握手照常，之后的读写请求一律不回复（心跳与 Dispose 用例）。</summary>
    public bool Silent { get; set; }

    /// <summary>应答里写死的引用号；非 null 时替代请求的引用号（引用号不符 → <c>ProtocolViolation</c>）。</summary>
    public ushort? ForeignReference { get; set; }

    /// <summary>收到的 COTP 连接请求数：重连会重新握手，因此这个数就是握手次数。</summary>
    public int ConnectionRequestCount => Volatile.Read(ref connectionRequestCount);

    /// <summary>收到的读写请求帧，按顺序；不应答与 PUT/GET 未开启时收到的帧同样记录。</summary>
    public IReadOnlyList<byte[]> Requests
    {
        get { lock (gate) return requests.ToArray(); }
    }

    /// <summary>取某个区的内存块：用例用它预置数据，或核对设备侧真正收到的字节。I、Q、M 区的 DB 号为 0。</summary>
    public byte[] Block(S7Area area, int dbNumber) => blocks[(area, dbNumber)];

    /// <summary>把替身接到 Testing 的设备宿主上：按 TPKT 长度分帧，逐帧应答。</summary>
    public DeviceSimulator ToDevice() => new DeviceSimulator(S7Framing.Create(), Handle);

    /// <summary>在回环随机端口上以 TCP 承载本替身；宿主由调用方释放。</summary>
    public DeviceSimulatorTcpHost HostTcp() => ToDevice().HostTcp();

    /// <summary>下一个读写请求到达的任务：静默模式下用它确认请求确实发出，不轮询也不睡眠。</summary>
    public Task<byte[]> NextRequestAsync()
    {
        lock (gate)
        {
            nextRequest = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            return nextRequest.Task;
        }
    }

    private byte[]? Handle(byte[] frame)
    {
        if (IsConnectionRequest(frame))
        {
            Interlocked.Increment(ref connectionRequestCount);
            return connectionConfirm;
        }

        ushort reference = ForeignReference ?? ReferenceOf(frame);
        switch (Service(frame))
        {
            case SetupService:
                return SetupAck(reference);
            case S7VarItem.ReadService:
                return Var(frame, reference, read: true);
            case S7VarItem.WriteService:
                return Var(frame, reference, read: false);

            // 表外的服务不猜语义：与不应答同样处理，由客户端的超时来判定。
            default:
                return null;
        }
    }

    private byte[]? Var(byte[] frame, ushort reference, bool read)
    {
        Record(frame);
        if (Silent)
            return null;
        if (PutGetDisabled)
            return Ack(reference, Array.Empty<byte>(), Array.Empty<byte>(), PutGetErrorClass, PutGetErrorCode);

        byte service = read ? S7VarItem.ReadService : S7VarItem.WriteService;
        bool bits = TransportSize(frame) == S7VarItem.RequestBit;
        byte code = AccessCheck(frame, bits ? BitAccessBytes : Quantity(frame), out byte[] block);
        if (code != S7VarItem.Success)
            return ItemError(reference, service, code);

        if (!read)
        {
            ApplyWrite(frame, block, bits);
            return Ack(reference, ItemParameters(service), new[] { S7VarItem.Success });
        }

        return ReadAck(frame, reference, block, bits);
    }

    private byte[] ReadAck(byte[] frame, ushort reference, byte[] block, bool bits)
    {
        int payloadBytes = bits ? BitAccessBytes : Quantity(frame);

        // 数据项头是返回码 + 传输尺寸 + 2 字节长度；0x03 与 0x04 的长度按位计（17.3 节）。
        var data = new byte[S7VarItem.ItemHeaderBytes + payloadBytes];
        data[0] = S7VarItem.Success;
        data[1] = bits ? S7VarItem.DataBit : S7VarItem.DataByte;
        int unitLength = bits ? Quantity(frame) : payloadBytes * S7VarItem.BitsPerByte;
        data[2] = (byte)(unitLength >> 8);
        data[3] = (byte)unitLength;
        block.AsSpan(ByteAddress(frame), payloadBytes).CopyTo(data.AsSpan(S7VarItem.ItemHeaderBytes));

        return Ack(reference, ItemParameters(S7VarItem.ReadService), data);
    }

    private static void ApplyWrite(byte[] frame, byte[] block, bool bits)
    {
        int byteAddress = ByteAddress(frame);
        if (!bits)
        {
            Payload(frame).CopyTo(block.AsSpan(byteAddress));
            return;
        }

        // 位写只推进一位：值字节的 0 或 1 落到目标字节的位号上，其余位保持（LSB 为位号 0）。
        int mask = 1 << BitIndexOf(frame);
        byte value = block[byteAddress];
        block[byteAddress] = Payload(frame)[0] != 0 ? (byte)(value | mask) : (byte)(value & ~mask);
    }

    private byte AccessCheck(byte[] frame, int length, out byte[] block)
    {
        block = Array.Empty<byte>();
        var key = ((S7Area)(AreaCode(frame) - FirstAreaCode), DbNumber(frame));
        if (!blocks.TryGetValue(key, out byte[]? found))
            return ItemDoesNotExist;

        if (ByteAddress(frame) + length > found.Length)
            return InvalidAddress;

        block = found;
        return S7VarItem.Success;
    }

    private byte[] ItemError(ushort reference, byte service, byte returnCode)
    {
        // 读应答即使失败也带完整的 4 字节数据项头（V-S7-11），写应答只有一个返回码（V-S7-8）。
        var data = new byte[service == S7VarItem.ReadService ? S7VarItem.ItemHeaderBytes : 1];
        data[0] = returnCode;
        return Ack(reference, ItemParameters(service), data);
    }

    private byte[] SetupAck(ushort reference)
    {
        // V-S7-4 的参数块：服务号、保留字节、两组固定的协议规格参数与末尾 2 字节的协商值。
        var parameters = new byte[]
        {
            SetupService, SetupReserved, 0x00, 0x01, 0x00, 0x01, (byte)(NegotiatedPduBytes >> 8), (byte)NegotiatedPduBytes,
        };
        return Ack(reference, parameters, Array.Empty<byte>());
    }

    private static byte[] ItemParameters(byte service) => new[] { service, SingleItem };

    private static byte[] Ack(ushort reference, byte[] parameters, byte[] data, byte errorClass = NoError, byte errorCode = NoError)
    {
        int total = ResponseShellBytes + parameters.Length + data.Length;
        using var buffer = new ByteBufferWriter(total);
        CotpFrames.WriteTpkt(buffer, total);
        CotpFrames.WriteDataTransfer(buffer);
        BigEndianFields.WriteUInt8(buffer, ProtocolId);
        BigEndianFields.WriteUInt8(buffer, AckDataRosctr);
        BigEndianFields.WriteUInt16(buffer, 0);
        BigEndianFields.WriteUInt16(buffer, reference);
        BigEndianFields.WriteUInt16(buffer, checked((ushort)parameters.Length));
        BigEndianFields.WriteUInt16(buffer, checked((ushort)data.Length));
        BigEndianFields.WriteUInt8(buffer, errorClass);
        BigEndianFields.WriteUInt8(buffer, errorCode);
        BigEndianFields.WriteBytes(buffer, parameters);
        BigEndianFields.WriteBytes(buffer, data);
        return buffer.ToArray();
    }

    private void Record(byte[] frame)
    {
        TaskCompletionSource<byte[]>? signal;
        lock (gate)
        {
            requests.Add(frame);
            signal = nextRequest;
            nextRequest = null;
        }

        signal?.TrySetResult(frame);
    }
}
