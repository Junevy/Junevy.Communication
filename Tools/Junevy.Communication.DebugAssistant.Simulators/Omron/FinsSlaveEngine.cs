using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Memory;

namespace Junevy.Communication.DebugAssistant.Simulators.Omron;

/// <summary>FINS 从站的设置（计划第 7.14 节）：服务器节点号，以及分配给客户端的节点号（请求的客户端节点为 0 时使用）。</summary>
public sealed record FinsSlaveOptions(byte ServerNode = 1, byte AssignedClientNode = 0x0B);

/// <summary>
/// FINS 从站引擎（计划第 8.1 节、第 8.2 节）：FINS/TCP 的节点地址请求（命令 0 → 1），FINS 帧的内存区读写（命令 0101 与 0102），
/// 字区与位区代码按计划第 17.3 节。应答的 DA 与 SA 对调，ICF 置位应答位。越界或区不存在返回结束码 0x1103 或 0x1101。
/// 区为 CIO、W、H、A、D 与 EM 的 E0_–EC_，每个字区 32768 个字；位区代码为字区代码减去 0x80 的位形式。
/// </summary>
public sealed class FinsSlaveEngine : ISimulatorEngine
{
    private const int FinsTcpHeaderLength = 16;
    private const byte FinsTcpMagic0 = (byte)'F';
    private const uint CommandNodeRequest = 0;
    private const uint CommandNodeResponse = 1;
    private const uint CommandFrame = 2;
    private const ushort CommandMemoryRead = 0x0101;
    private const ushort CommandMemoryWrite = 0x0102;
    private const ushort EndSuccess = 0x0000;
    private const ushort EndCommandNotSupported = 0x0401;
    private const ushort EndDataTooShort = 0x1001;
    private const ushort EndAddressRange = 0x1103;
    private const ushort EndAreaMissing = 0x1101;
    private const int AreaLength = 32768;

    /// <summary>字区的名称与字区代码；EM 的 E0_–EC_ 为 0xA0 加组号。</summary>
    private static readonly (string Name, byte WordCode)[] Areas =
    [
        ("D", 0x82),
        ("CIO", 0xB0),
        ("W", 0xB1),
        ("H", 0xB2),
        ("A", 0xB3),
        .. Enumerable.Range(0, 13).Select(group => ($"E{group:X}_", (byte)(0xA0 + group))),
    ];

    private readonly SimulatorMemory _memory;
    private readonly FinsSlaveOptions _options;

    public FinsSlaveEngine(SimulatorMemory memory, FinsSlaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(options);
        _memory = memory;
        _options = options;
        foreach ((string name, _) in Areas)
        {
            _ = memory.FindWords(name) ?? memory.AddWords(name, AreaLength);
        }
    }

    /// <inheritdoc />
    public IFrameCodecFactory? CreateRequestCodec(SimulatorTransport transport) =>
        transport == SimulatorTransport.Udp
            ? null
            : FrameCodecFactory.Create(new FramingOptions
            {
                Mode = FramingMode.LengthField,
                LengthFieldOffset = 4,
                LengthFieldSize = 4,
                LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
            });

    /// <inheritdoc />
    public SimulatorReply Handle(ReadOnlySpan<byte> request, SimulatorConnection connection, FaultPlan fault)
    {
        if (request.Length < FinsTcpHeaderLength || request[0] != FinsTcpMagic0 || request[1] != (byte)'I' || request[2] != (byte)'N' || request[3] != (byte)'S')
        {
            return SimulatorReply.Silent("无效的 FINS/TCP 帧，已忽略");
        }

        uint command = ReadUInt32(request, 8);
        return command switch
        {
            CommandNodeRequest => NodeAddress(request, connection),
            CommandFrame => FinsFrame(request[FinsTcpHeaderLength..], fault),
            _ => SimulatorReply.Silent($"FINS/TCP 命令 {command} 不支持，已忽略"),
        };
    }

    private SimulatorReply NodeAddress(ReadOnlySpan<byte> request, SimulatorConnection connection)
    {
        // 节点地址请求的载荷是 4 字节的客户端节点（大端），节点号在最低字节。
        byte requested = request.Length >= FinsTcpHeaderLength + 4 ? request[FinsTcpHeaderLength + 3] : (byte)0;
        byte client = requested == 0 ? _options.AssignedClientNode : requested;
        byte[] payload = [0x00, 0x00, 0x00, client, 0x00, 0x00, 0x00, _options.ServerNode];
        return SimulatorReply.Respond(FinsTcp(CommandNodeResponse, 0, payload), $"节点地址：客户端 {client:X2}，服务端 {_options.ServerNode:X2}");
    }

    private SimulatorReply FinsFrame(ReadOnlySpan<byte> frame, FaultPlan fault)
    {
        if (frame.Length < 12 || frame[4] != _options.ServerNode)
        {
            return SimulatorReply.Silent("FINS 帧的目的节点不是本模拟器，已忽略");
        }

        byte[] header = [0xC0, 0x00, frame[2], frame[6], frame[7], frame[8], frame[3], frame[4], frame[5], frame[9]];
        byte command = frame[10];
        byte sub = frame[11];
        (ushort endCode, byte[] data, string summary) = fault.ForcedErrorCode is { } code
            ? ((ushort)code, Array.Empty<byte>(), $"强制结束码 {code:X4}")
            : Execute(command, sub, frame[12..]);
        byte[] response = [.. header, command, sub, (byte)(endCode >> 8), (byte)endCode, .. data];
        return SimulatorReply.Respond(FinsTcp(CommandFrame, 0, response), $"FINS {command:X2}{sub:X2} {summary}");
    }

    private (ushort EndCode, byte[] Data, string Summary) Execute(byte command, byte sub, ReadOnlySpan<byte> data)
    {
        ushort code = (ushort)((command << 8) | sub);
        if (code != CommandMemoryRead && code != CommandMemoryWrite)
        {
            return (EndCommandNotSupported, [], $"命令 {code:X4} 不支持");
        }

        if (data.Length < 6)
        {
            return (EndDataTooShort, [], "命令数据太短");
        }

        byte area = data[0];
        int address = (data[1] << 8) | data[2];
        byte bit = data[3];
        int count = (data[4] << 8) | data[5];
        bool bitArea = area < 0x80;
        byte wordCode = bitArea ? (byte)(area | 0x80) : area;
        WordArea? words = FindWordArea(wordCode);
        if (words is null)
        {
            return (EndAreaMissing, [], $"区 {area:X2} 不存在");
        }

        return code == CommandMemoryRead
            ? ReadMemory(words, bitArea, address, bit, count)
            : WriteMemory(words, bitArea, address, bit, count, data[6..]);
    }

    private static (ushort EndCode, byte[] Data, string Summary) ReadMemory(WordArea area, bool bitArea, int address, byte bit, int count)
    {
        if (bitArea)
        {
            int lastBit = bit + count - 1;
            int words = (lastBit / 16) + 1;
            if (address + words > area.Length || count < 1)
            {
                return (EndAddressRange, [], "地址越界");
            }

            ushort[] values = area.Read(address, words);
            byte[] bits = new byte[count];
            for (int index = 0; index < count; index++)
            {
                int position = bit + index;
                bits[index] = (byte)((values[position / 16] >> (position % 16)) & 1);
            }

            return (EndSuccess, bits, $"读位 {area.Name}{address}.{bit:00} 数量 {count}");
        }

        if (address + count > area.Length || count < 1)
        {
            return (EndAddressRange, [], "地址越界");
        }

        ushort[] read = area.Read(address, count);
        byte[] bytes = new byte[read.Length * 2];
        for (int index = 0; index < read.Length; index++)
        {
            bytes[index * 2] = (byte)(read[index] >> 8);
            bytes[(index * 2) + 1] = (byte)read[index];
        }

        return (EndSuccess, bytes, $"读字 {area.Name}{address} 数量 {count}");
    }

    private static (ushort EndCode, byte[] Data, string Summary) WriteMemory(WordArea area, bool bitArea, int address, byte bit, int count, ReadOnlySpan<byte> data)
    {
        if (bitArea)
        {
            int lastBit = bit + count - 1;
            int words = (lastBit / 16) + 1;
            if (address + words > area.Length || count < 1 || data.Length < count)
            {
                return (EndAddressRange, [], "地址越界");
            }

            ushort[] values = area.Read(address, words);
            for (int index = 0; index < count; index++)
            {
                int position = bit + index;
                ushort mask = (ushort)(1 << (position % 16));
                values[position / 16] = data[index] != 0 ? (ushort)(values[position / 16] | mask) : (ushort)(values[position / 16] & ~mask);
            }

            area.Write(address, values);
            return (EndSuccess, [], $"写位 {area.Name}{address}.{bit:00} 数量 {count}");
        }

        if (address + count > area.Length || count < 1 || data.Length < count * 2)
        {
            return (EndAddressRange, [], "地址越界");
        }

        ushort[] written = new ushort[count];
        for (int index = 0; index < count; index++)
        {
            written[index] = (ushort)((data[index * 2] << 8) | data[(index * 2) + 1]);
        }

        area.Write(address, written);
        return (EndSuccess, [], $"写字 {area.Name}{address} 数量 {count}");
    }

    private WordArea? FindWordArea(byte wordCode)
    {
        foreach ((string name, byte code) in Areas)
        {
            if (code == wordCode)
            {
                return _memory.FindWords(name);
            }
        }

        return null;
    }

    /// <summary>FINS/TCP 报文：FINS、长度（计长度之后的字节）、命令、错误码、载荷。</summary>
    private static byte[] FinsTcp(uint command, uint error, byte[] payload)
    {
        uint length = (uint)(8 + payload.Length);
        return
        [
            (byte)'F', (byte)'I', (byte)'N', (byte)'S',
            (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length,
            (byte)(command >> 24), (byte)(command >> 16), (byte)(command >> 8), (byte)command,
            (byte)(error >> 24), (byte)(error >> 16), (byte)(error >> 8), (byte)error,
            .. payload,
        ];
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
