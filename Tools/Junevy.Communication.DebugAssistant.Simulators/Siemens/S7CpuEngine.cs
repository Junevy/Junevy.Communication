using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Memory;

namespace Junevy.Communication.DebugAssistant.Simulators.Siemens;

/// <summary>S7 CPU 的设置（计划第 7.14 节）：协商的 PDU 长度（240、480 或 960）与 PUT/GET 是否开放。</summary>
public sealed record S7CpuOptions(int PduSize = 240, bool PutGetEnabled = true);

/// <summary>
/// S7 CPU 引擎（计划第 8.1 节、第 8.2 节）：ISO-on-TCP 上的 COTP CR → CC、Setup Communication（PDU 取请求与设置的较小值）、
/// 单项的 Read Var 与 Write Var。区为 I（0x81）、Q（0x82）、M（0x83）与数据块 DB（0x84，存储区 "DB{n}"）。
/// PUT/GET 关闭时返回头部错误 0x81 0x04；数据块不存在返回数据项返回码 0x0A，越过区的末尾返回 0x05。
/// COTP 与 Setup 的进度保存在会话状态中。
/// </summary>
public sealed class S7CpuEngine : ISimulatorEngine
{
    private const byte Tpkt = 0x03;
    private const byte CotpConnectRequest = 0xE0;
    private const byte CotpConnectConfirm = 0xD0;
    private const byte CotpData = 0xF0;
    private const byte S7Protocol = 0x32;
    private const byte JobRosctr = 0x01;
    private const byte AckDataRosctr = 0x03;
    private const byte FunctionSetup = 0xF0;
    private const byte FunctionRead = 0x04;
    private const byte FunctionWrite = 0x05;
    private const byte ItemTransportBit = 0x01;
    private const byte DataTransportBit = 0x03;
    private const byte DataTransportByte = 0x04;
    private const byte ReturnSuccess = 0xFF;
    private const byte ReturnAddressOutOfRange = 0x05;
    private const byte ReturnNoObject = 0x0A;
    private const byte PutGetErrorClass = 0x81;
    private const byte PutGetErrorCode = 0x04;
    private const byte AreaInputs = 0x81;
    private const byte AreaOutputs = 0x82;
    private const byte AreaMerkers = 0x83;
    private const byte AreaDataBlock = 0x84;
    private const int AreaLength = 65536;
    private const string StageKey = "s7.stage";
    private const string ConnectedStage = "connected";
    private const string SetupStage = "setup";
    private const ushort ServerReference = 0x000C;

    private readonly SimulatorMemory _memory;
    private readonly S7CpuOptions _options;

    public S7CpuEngine(SimulatorMemory memory, S7CpuOptions options)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(options);
        if (options.PduSize is not (240 or 480 or 960))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.PduSize, "PDU 长度只能是 240、480 或 960。");
        }

        _memory = memory;
        _options = options;
        foreach (string name in new[] { "I", "Q", "M" })
        {
            _ = memory.FindBytes(name) ?? memory.AddBytes(name, AreaLength);
        }
    }

    /// <inheritdoc />
    public IFrameCodecFactory? CreateRequestCodec(SimulatorTransport transport) =>
        transport == SimulatorTransport.Udp
            ? null
            : FrameCodecFactory.Create(new FramingOptions
            {
                Mode = FramingMode.LengthField,
                LengthFieldOffset = 2,
                LengthFieldSize = 2,
                LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
                LengthAdjustment = -4,
            });

    /// <inheritdoc />
    public SimulatorReply Handle(ReadOnlySpan<byte> request, SimulatorConnection connection, FaultPlan fault)
    {
        if (request.Length < 7 || request[0] != Tpkt)
        {
            return SimulatorReply.Silent("无效的 TPKT 帧，已忽略");
        }

        return request[5] switch
        {
            CotpConnectRequest => Connect(request, connection),
            CotpData => Data(request, connection, fault),
            _ => SimulatorReply.Silent("不支持的 COTP 类型，已忽略"),
        };
    }

    /// <summary>连接请求（CR）：确认（CC）的目的引用取请求的源引用，源引用为本端固定值，参数照抄（TSAP 等）。</summary>
    private static SimulatorReply Connect(ReadOnlySpan<byte> request, SimulatorConnection connection)
    {
        byte[] parameters = request[11..].ToArray();
        byte[] cotp =
        [
            CotpConnectConfirm, request[8], request[9], (byte)(ServerReference >> 8), (byte)ServerReference, 0x00, .. parameters,
        ];
        connection.Items[StageKey] = ConnectedStage;
        return SimulatorReply.Respond(WrapTpkt([(byte)cotp.Length, .. cotp]), "COTP 连接请求（CR → CC）");
    }

    private SimulatorReply Data(ReadOnlySpan<byte> request, SimulatorConnection connection, FaultPlan fault)
    {
        if (!connection.Items.ContainsKey(StageKey))
        {
            return SimulatorReply.Silent("未建立 COTP 连接，已忽略");
        }

        ReadOnlySpan<byte> s7 = request[7..];
        if (s7.Length < 10 || s7[0] != S7Protocol || s7[1] != JobRosctr)
        {
            return SimulatorReply.Silent("不支持的 S7 报文，已忽略");
        }

        ushort reference = (ushort)((s7[4] << 8) | s7[5]);
        int parameterLength = (s7[6] << 8) | s7[7];
        int dataLength = (s7[8] << 8) | s7[9];
        if (s7.Length < 10 + parameterLength + dataLength || parameterLength == 0)
        {
            return SimulatorReply.Silent("S7 报文长度不符，已忽略");
        }

        ReadOnlySpan<byte> parameters = s7.Slice(10, parameterLength);
        ReadOnlySpan<byte> data = s7.Slice(10 + parameterLength, dataLength);
        if (fault.ForcedErrorCode is { } code)
        {
            return Respond(reference, [], [], (byte)(code >> 8), (byte)code, $"强制错误 {code:X4}");
        }

        return parameters[0] switch
        {
            FunctionSetup => Setup(parameters, reference, connection),
            FunctionRead when !_options.PutGetEnabled => Respond(reference, [], [], PutGetErrorClass, PutGetErrorCode, "读取：PUT/GET 未开放"),
            FunctionWrite when !_options.PutGetEnabled => Respond(reference, [], [], PutGetErrorClass, PutGetErrorCode, "写入：PUT/GET 未开放"),
            FunctionRead => ReadVar(parameters, reference),
            FunctionWrite => WriteVar(parameters, data, reference),
            _ => Respond(reference, [], [], 0x81, 0x01, $"功能码 {parameters[0]:X2} 不支持"),
        };
    }

    /// <summary>Setup Communication：请求的 PDU 长度与设置取较小值；会话进入 Setup 阶段。</summary>
    private SimulatorReply Setup(ReadOnlySpan<byte> parameters, ushort reference, SimulatorConnection connection)
    {
        ushort requested = (ushort)((parameters[6] << 8) | parameters[7]);
        ushort negotiated = Math.Min(requested, (ushort)_options.PduSize);
        connection.Items[StageKey] = SetupStage;
        byte[] answer = [FunctionSetup, 0x00, parameters[2], parameters[3], parameters[4], parameters[5], (byte)(negotiated >> 8), (byte)negotiated];
        return Respond(reference, answer, [], 0, 0, $"Setup Communication，协商 PDU {negotiated}");
    }

    private SimulatorReply ReadVar(ReadOnlySpan<byte> parameters, ushort reference)
    {
        S7Item item = S7Item.Parse(parameters.Slice(2, 12));
        (byte returnCode, byte[] bytes) = ReadItem(item);
        byte[] data = returnCode != ReturnSuccess
            ? [returnCode, 0x00, 0x00, 0x00]
            : item.Transport == ItemTransportBit
                ? [ReturnSuccess, DataTransportBit, 0x00, 0x01, (byte)((bytes[0] >> item.BitIndex) & 1)]
                : [ReturnSuccess, DataTransportByte, (byte)((bytes.Length * 8) >> 8), (byte)(bytes.Length * 8), .. bytes];
        return Respond(reference, [FunctionRead, 0x01], data, 0, 0, $"读取 {item.Describe()} 数量 {item.Count}");
    }

    private SimulatorReply WriteVar(ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> data, ushort reference)
    {
        S7Item item = S7Item.Parse(parameters.Slice(2, 12));
        byte returnCode = data.Length >= 4
            ? WriteItem(item, data[1], data.Slice(4, data[1] == DataTransportBit ? 1 : ((data[2] << 8) | data[3]) / 8))
            : ReturnAddressOutOfRange;
        return Respond(reference, [FunctionWrite, 0x01], [returnCode], 0, 0, $"写入 {item.Describe()} 数量 {item.Count}");
    }

    private (byte ReturnCode, byte[] Bytes) ReadItem(S7Item item)
    {
        ByteArea? area = FindArea(item);
        if (area is null)
        {
            return (ReturnNoObject, []);
        }

        int count = item.Transport == ItemTransportBit ? 1 : item.Count;
        if (item.ByteOffset + count > area.Length)
        {
            return (ReturnAddressOutOfRange, []);
        }

        return (ReturnSuccess, area.Read(item.ByteOffset, count));
    }

    private byte WriteItem(S7Item item, byte dataTransport, ReadOnlySpan<byte> value)
    {
        ByteArea? area = FindArea(item);
        if (area is null)
        {
            return ReturnNoObject;
        }

        if (item.Transport == ItemTransportBit)
        {
            if (item.ByteOffset + 1 > area.Length || value.IsEmpty)
            {
                return ReturnAddressOutOfRange;
            }

            byte current = area.Read(item.ByteOffset, 1)[0];
            byte mask = (byte)(1 << item.BitIndex);
            byte updated = value[0] != 0 ? (byte)(current | mask) : (byte)(current & ~mask);
            area.Write(item.ByteOffset, [updated]);
            return ReturnSuccess;
        }

        if (item.ByteOffset + value.Length > area.Length)
        {
            return ReturnAddressOutOfRange;
        }

        area.Write(item.ByteOffset, value);
        return ReturnSuccess;
    }

    private ByteArea? FindArea(S7Item item) => item.Area switch
    {
        AreaDataBlock => _memory.FindBytes($"DB{item.DbNumber}"),
        AreaMerkers => _memory.FindBytes("M"),
        AreaInputs => _memory.FindBytes("I"),
        AreaOutputs => _memory.FindBytes("Q"),
        _ => null,
    };

    private static SimulatorReply Respond(ushort reference, byte[] parameters, byte[] data, byte errorClass, byte errorCode, string summary) =>
        SimulatorReply.Respond(WrapTpkt([0x02, 0xF0, 0x80, .. Ack(reference, parameters, data, errorClass, errorCode)]), summary);

    /// <summary>TPKT 的外层：03 00 与总长度（大端，含自身）。</summary>
    private static byte[] WrapTpkt(byte[] payload)
    {
        int total = 4 + payload.Length;
        return [Tpkt, 0x00, (byte)(total >> 8), (byte)total, .. payload];
    }

    /// <summary>S7 的 ack-data 报文：头部 12 字节（含错误类与错误码），然后是参数与数据。</summary>
    private static byte[] Ack(ushort reference, byte[] parameters, byte[] data, byte errorClass, byte errorCode) =>
    [
        S7Protocol, AckDataRosctr, 0x00, 0x00, (byte)(reference >> 8), (byte)reference,
        (byte)(parameters.Length >> 8), (byte)parameters.Length, (byte)(data.Length >> 8), (byte)data.Length,
        errorClass, errorCode, .. parameters, .. data,
    ];

    /// <summary>一个 S7ANY 项：传输尺寸、数量、数据块号、区与位地址。</summary>
    private sealed record S7Item(byte Transport, int Count, int DbNumber, byte Area, int Address)
    {
        public static S7Item Parse(ReadOnlySpan<byte> item) => new(
            item[3],
            (item[4] << 8) | item[5],
            (item[6] << 8) | item[7],
            item[8],
            (item[9] << 16) | (item[10] << 8) | item[11]);

        public int ByteOffset => Address >> 3;

        public int BitIndex => Address & 7;

        public string Describe() => Area switch
        {
            AreaDataBlock => $"DB{DbNumber}.{ByteOffset}",
            AreaMerkers => $"M{ByteOffset}",
            AreaInputs => $"I{ByteOffset}",
            AreaOutputs => $"Q{ByteOffset}",
            _ => $"区 {Area:X2}",
        };
    }
}
