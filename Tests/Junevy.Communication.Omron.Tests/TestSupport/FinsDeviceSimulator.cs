using System.Buffers;
using System.Buffers.Binary;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Omron.Framing;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// FINS 设备替身（计划 Task 14 第 1 条）：节点地址请求一律应答（请求的客户端节点为 0 时从 <see cref="FinsTestSupport.ClientNode"/> 起逐次另派），
/// 内存区读写按 17.3 节的码表翻译到一块按软元件符号保存的字内存上；分帧、结束码屏蔽与错误消息仍由被测的库负责。
/// 字代码与位代码解析到同一符号、同一份内存，因此原生位写之后字读能看见（P4-D6，另见摩擦记录 F7）；码表外的区域与越过
/// <see cref="AreaCapacity"/> 的访问回 11 03（V-F-8）。故障注入见各属性：不应答、FINS/TCP 错误码、非致命结束码、写死的 SID。
/// </summary>
internal sealed class FinsDeviceSimulator
{
    /// <summary>替身里每个软元件可用的字数：内存稀疏保存，不预分配；越界检查以此为准（P4-D16）。</summary>
    public const int AreaCapacity = 4096;

    // 应答头（V-F-4 与 V-F-3 的对照）：ICF 置应答位，RSV 与 GCT 原样回显，请求的源端与目的端互换。
    private const byte ResponseIcf = 0xC0;
    private const int ReservedAndTimerBytes = 2;
    private const int RouteBlockBytes = 3;
    private const int ReservedOffset = 1;
    private const int DestinationOffset = 3;
    private const int SourceOffset = 6;

    // 结束码（第 17.3 节）：正常 00 00，带非致命错误标志 00 40（V-F-9），越界 11 03（V-F-8）。
    private const byte NoErrorMinor = 0x00;
    private const byte NonFatalMinor = 0x40;
    private const byte OutOfRangeMajor = 0x11;
    private const byte OutOfRangeMinor = 0x03;

    // FINS 的软元件一律按字寻址，位号 0–15 落在这 16 位里，位 0 是大端字的最低位（P4-D6）。
    private const int BitsPerWord = 16;

    private const byte BitOn = 0x01;
    private const byte BitOff = 0x00;
    private const byte AutoAssignedNode = 0x00;

    /// <summary>17.3 节码表：字代码与位代码都解析到同一个软元件符号。</summary>
    private static readonly Dictionary<byte, string> AreaSymbols = FinsDeviceTable.Devices
        .SelectMany(device => new[] { (device.WordAreaCode, device.Symbol), (device.BitAreaCode, device.Symbol) })
        .ToDictionary(pair => pair.Item1, pair => pair.Item2);

    /// <summary>位区代码集合：出现在这里的就是位访问，数量按点计。</summary>
    private static readonly HashSet<byte> BitAreaCodes = new HashSet<byte>(FinsDeviceTable.Devices.Select(device => device.BitAreaCode));

    private readonly List<byte[]> requests = new List<byte[]>();
    private readonly List<TaskCompletionSource<byte[]>> arrivals = new List<TaskCompletionSource<byte[]>>();
    private readonly object gate = new object();

    private int handshakeCount;

    /// <summary>成功应答是否带非致命错误标志（结束码 00 40，P4-D14 屏蔽后仍算成功）。</summary>
    public bool NonFatalEndCode { get; set; }

    /// <summary>FINS/TCP 外壳的错误码：非 null 时读写请求一律带它回头，且不进入内存访问（P4-D14）。</summary>
    public uint? TcpError { get; set; }

    /// <summary>应答里写死的 SID；非 null 时替代请求的 SID（SID 不符 → <c>ProtocolViolation</c>）。</summary>
    public byte? ForeignSessionId { get; set; }

    /// <summary>不应答：节点握手照常，之后的读写请求一律不回复（心跳与 Dispose 用例）。</summary>
    public bool Silent { get; set; }

    /// <summary>设备侧的字内存：用例用它预置数据，或核对设备真正收到的值。键是软元件符号（"D"、"CIO"、"E2_" …）。</summary>
    public PlcMemory Memory { get; } = new PlcMemory();

    /// <summary>收到的读写请求帧，按顺序。静默与错误注入时收到的帧同样记录。</summary>
    public IReadOnlyList<byte[]> Requests
    {
        get { lock (gate) return requests.ToArray(); }
    }

    /// <summary>收到的节点地址请求数：重连会重新握手，因此这个数就是握手次数。</summary>
    public int HandshakeCount => Volatile.Read(ref handshakeCount);

    /// <summary>把替身接到 Testing 的设备宿主上：按 FINS/TCP 长度分帧，逐帧应答。</summary>
    public DeviceSimulator ToDevice() => new DeviceSimulator(FinsFraming.Create(), Handle);

    /// <summary>在回环随机端口上以 TCP 承载本替身；宿主由调用方释放。</summary>
    public DeviceSimulatorTcpHost HostTcp() => ToDevice().HostTcp();

    /// <summary>下一个读写请求到达的任务：静默模式下用它确认请求确实发出，不轮询也不睡眠。</summary>
    public Task<byte[]> NextRequestAsync()
    {
        var source = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) arrivals.Add(source);
        return source.Task;
    }

    /// <summary>处理一帧：节点地址握手、读写应答与故障注入都在这里。除供替身自检测试用外，只由 <see cref="ToDevice"/> 的回调调用。</summary>
    /// <param name="frame">完整的一帧。</param>
    /// <returns>应答帧；不应答时为空。</returns>
    public byte[]? Handle(byte[] frame)
    {
        if (!FinsTcpFrame.TryUnwrap(frame, out uint command, out _, out _, out _))
            return null;

        if (command == FinsTcpFrame.CommandNodeRequest)
        {
            Interlocked.Increment(ref handshakeCount);
            return NodeAck(ClientNodeFor(frame), ServerNode);
        }

        Record(frame);
        if (Silent)
            return null;

        if (TcpError is uint error)
            return FinsTcpFrame.Build(FinsTcpFrame.CommandFinsFrame, error, Array.Empty<byte>());

        return ServiceOf(frame) switch
        {
            FinsMemoryArea.CommandRead => Access(frame, read: true),
            FinsMemoryArea.CommandWrite => Access(frame, read: false),

            // 表外的服务码不猜语义：与不应答同样处理，由客户端的超时来判定。
            _ => null,
        };
    }

    // P4-D18：请求的客户端节点为 0 时由设备分配，固定请求原样回显。分配号随握手次数递增，
    // 因此重连用例能证明路由取自最近一次握手。
    private byte ClientNodeFor(byte[] request)
    {
        byte requested = request[ClientNodeTailIndex];
        return requested == AutoAssignedNode
            ? (byte)(ClientNode + Volatile.Read(ref handshakeCount) - 1)
            : requested;
    }

    private byte[] Access(byte[] request, bool read)
    {
        byte area = AreaCodeOf(request);
        if (!AreaSymbols.TryGetValue(area, out string? symbol))
            return Reject(request);

        bool bits = BitAreaCodes.Contains(area);
        int word = WordAddressOf(request);
        int count = QuantityOf(request);

        // 位访问的最后一个字由起始位号与点数推出：跨过 16 位的点落在下一字上。
        int lastWord = bits ? word + (BitNumberOf(request) + count - 1) / BitsPerWord : word + count - 1;
        if (lastWord >= AreaCapacity)
            return Reject(request);

        return bits ? Points(request, symbol, word, read) : Words(request, symbol, word, read);
    }

    // 字访问：数量按字计，数据每字 2 字节（V-F-3、V-F-5）。
    private byte[] Words(byte[] request, string symbol, int word, bool read)
    {
        if (read)
            return Ack(request, BigEndian(Memory.GetWords(symbol, word, QuantityOf(request))));

        Memory.SetWords(symbol, word, FromBigEndian(request.AsSpan(RequestPayloadIndex)));
        return Ack(request, Array.Empty<byte>());
    }

    // 位访问：数量按点计，每点 1 字节的 00 或 01，起始绝对位号由字号与位号合成（V-F-6、V-F-7）。
    private byte[] Points(byte[] request, string symbol, int word, bool read)
    {
        int absolute = word * BitsPerWord + BitNumberOf(request);
        if (!read)
        {
            foreach (byte value in request.AsSpan(RequestPayloadIndex))
                SetPoint(symbol, absolute++, value != BitOff);

            return Ack(request, Array.Empty<byte>());
        }

        var data = new byte[QuantityOf(request) * FinsMemoryArea.BytesPerBitPoint];
        for (int point = 0; point < data.Length; point++)
            data[point] = IsPointSet(symbol, absolute + point) ? BitOn : BitOff;

        return Ack(request, data);
    }

    // 位读写都改写字内存里覆盖的那个字：替身只有一份物理存储（F7 的变通）。
    private bool IsPointSet(string symbol, int absoluteBit)
        => (Memory.GetWords(symbol, absoluteBit / BitsPerWord, 1)[0] >> absoluteBit % BitsPerWord & 1) != 0;

    private void SetPoint(string symbol, int absoluteBit, bool value)
    {
        int number = absoluteBit / BitsPerWord;
        ushort word = Memory.GetWords(symbol, number, 1)[0];
        ushort mask = (ushort)(1 << absoluteBit % BitsPerWord);
        Memory.SetWords(symbol, number, value ? (ushort)(word | mask) : (ushort)(word & ~mask));
    }

    private byte[] Ack(byte[] request, byte[] data)
        => Reply(request, FinsErrors.NoError, NonFatalEndCode ? NonFatalMinor : NoErrorMinor, data);

    private byte[] Reject(byte[] request) => Reply(request, OutOfRangeMajor, OutOfRangeMinor, Array.Empty<byte>());

    private byte[] Reply(byte[] request, byte major, byte minor, byte[] data)
    {
        int bodyBytes = FinsHead.HeadBytes + BigEndianFields.UInt16Bytes * 2 + data.Length;   // 命令回显与结束码各 2 字节
        using var buffer = new ByteBufferWriter(FinsTcpFrame.ShellBytes + bodyBytes);
        FinsTcpFrame.WriteShell(buffer, FinsTcpFrame.CommandFinsFrame, FinsErrors.NoError, bodyBytes);
        BigEndianFields.WriteUInt8(buffer, ResponseIcf);
        BigEndianFields.WriteBytes(buffer, request.AsSpan(ShellBytes + ReservedOffset, ReservedAndTimerBytes));
        BigEndianFields.WriteBytes(buffer, request.AsSpan(ShellBytes + SourceOffset, RouteBlockBytes));
        BigEndianFields.WriteBytes(buffer, request.AsSpan(ShellBytes + DestinationOffset, RouteBlockBytes));
        BigEndianFields.WriteUInt8(buffer, ForeignSessionId ?? request[SidIndex]);
        BigEndianFields.WriteBytes(buffer, request.AsSpan(CommandIndex, BigEndianFields.UInt16Bytes));
        BigEndianFields.WriteUInt8(buffer, major);
        BigEndianFields.WriteUInt8(buffer, minor);
        BigEndianFields.WriteBytes(buffer, data);
        return buffer.ToArray();
    }

    // 字数据每字 2 字节、高位在前（V-F-5 的数据 00 FF）。
    private static byte[] BigEndian(ushort[] words)
    {
        var data = new byte[words.Length * FinsMemoryArea.WordBytes];
        for (int i = 0; i < words.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(i * FinsMemoryArea.WordBytes), words[i]);

        return data;
    }

    private static ushort[] FromBigEndian(ReadOnlySpan<byte> payload)
    {
        var words = new ushort[payload.Length / FinsMemoryArea.WordBytes];
        for (int i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(i * FinsMemoryArea.WordBytes));

        return words;
    }

    private void Record(byte[] frame)
    {
        TaskCompletionSource<byte[]>[] waiting;
        lock (gate)
        {
            requests.Add(frame);
            waiting = arrivals.ToArray();
            arrivals.Clear();
        }

        foreach (TaskCompletionSource<byte[]> source in waiting)
            source.TrySetResult(frame);
    }
}
