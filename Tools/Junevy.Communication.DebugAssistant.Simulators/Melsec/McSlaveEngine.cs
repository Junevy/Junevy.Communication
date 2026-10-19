using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Memory;

namespace Junevy.Communication.DebugAssistant.Simulators.Melsec;

/// <summary>
/// MELSEC 从站引擎（计划第 8.1 节、第 8.2 节）：3E、4E 的二进制与 ASCII，4C 的格式 4 与格式 5。支持批量读写（0401、1401，
/// 子命令 0000 为字单位、0001 为位单位）与环回（0619）。位软元件按字访问时每个字为 16 个点。模拟器不校验访问路径，
/// 请求中的访问路径照抄到应答。结束码 C051 表示软元件或范围不合法，C059 表示命令不支持。
/// 强制错误码由故障注入给出，应答带上错误信息（访问路径、命令与子命令共 9 字节）。
/// </summary>
public sealed class McSlaveEngine : ISimulatorEngine
{
    private const ushort EndOk = 0x0000;
    private const ushort EndDeviceError = 0xC051;
    private const ushort EndCommandError = 0xC059;
    private const ushort CommandBatchRead = 0x0401;
    private const ushort CommandBatchWrite = 0x1401;
    private const ushort CommandLoopback = 0x0619;
    private const ushort SubcommandWord = 0x0000;
    private const ushort SubcommandBit = 0x0001;
    private const int AreaLength = 65536;

    private readonly SimulatorMemory _memory;
    private readonly McSlaveOptions _options;

    public McSlaveEngine(SimulatorMemory memory, McSlaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(options);
        _memory = memory;
        _options = options;
        foreach (McDeviceInfo device in McDeviceTable.All)
        {
            if (device.IsBit)
            {
                _ = memory.FindBits(device.Symbol) ?? memory.AddBits(device.Symbol, AreaLength);
            }
            else
            {
                _ = memory.FindWords(device.Symbol) ?? memory.AddWords(device.Symbol, AreaLength);
            }
        }
    }

    /// <inheritdoc />
    public IFrameCodecFactory? CreateRequestCodec(SimulatorTransport transport)
    {
        if (transport == SimulatorTransport.Udp)
        {
            return null;
        }

        return (_options.Frame, _options.Encoding) switch
        {
            (McFrameKind.Frame3E, McEncodingKind.Binary) => FrameCodecFactory.Create(LengthField(7, 2, LengthFieldEncoding.BinaryLittleEndian)),
            (McFrameKind.Frame4E, McEncodingKind.Binary) => FrameCodecFactory.Create(LengthField(11, 2, LengthFieldEncoding.BinaryLittleEndian)),
            (McFrameKind.Frame3E, McEncodingKind.Ascii) => FrameCodecFactory.Create(LengthField(14, 4, LengthFieldEncoding.AsciiHex)),
            (McFrameKind.Frame4E, McEncodingKind.Ascii) => FrameCodecFactory.Create(LengthField(22, 4, LengthFieldEncoding.AsciiHex)),
            (McFrameKind.Frame4C, McEncodingKind.Ascii) => FrameCodecFactory.Create(new FramingOptions
            {
                Mode = FramingMode.StartEnd,
                StartMarker = "\u0005",
                EndMarker = "\r\n",
                KeepMarkers = false,
            }),
            _ => new McFormat5FrameCodecFactory(_options.SumCheck),
        };
    }

    /// <inheritdoc />
    public SimulatorReply Handle(ReadOnlySpan<byte> request, SimulatorConnection connection, FaultPlan fault)
    {
        McRequest? parsed = McWire.Parse(_options, request);
        if (parsed is null)
        {
            return SimulatorReply.Silent("无效的 MC 帧，已忽略");
        }

        McResult result = fault.ForcedErrorCode is { } code
            ? new McResult((ushort)code, [], $"强制结束码 {code:X4}")
            : Execute(parsed);
        byte[] response = McWire.Build(_options, parsed, result, fault.CorruptChecksum);
        return SimulatorReply.Respond(response, $"{parsed.Command:X4}/{parsed.Subcommand:X4} {result.Summary}");
    }

    private McResult Execute(McRequest request)
    {
        if (request.Command == CommandLoopback)
        {
            return new McResult(EndOk, request.Loopback, "环回");
        }

        if (request.Command is not (CommandBatchRead or CommandBatchWrite))
        {
            return new McResult(EndCommandError, [], $"命令 {request.Command:X4} 不支持");
        }

        if (request.Device is null || request.Subcommand is not (SubcommandWord or SubcommandBit))
        {
            return new McResult(EndDeviceError, [], "软元件或子命令不合法");
        }

        bool bitUnit = request.Subcommand == SubcommandBit;
        if (bitUnit && !request.Device.IsBit)
        {
            return new McResult(EndDeviceError, [], "字软元件不能按位访问");
        }

        return request.Command == CommandBatchRead ? Read(request, bitUnit) : Write(request, bitUnit);
    }

    private McResult Read(McRequest request, bool bitUnit)
    {
        McDeviceInfo device = request.Device!;
        if (device.IsBit)
        {
            BitArea area = _memory.FindBits(device.Symbol)!;
            int count = bitUnit ? request.Points : request.Points * 16;
            if (request.Number + count > area.Length)
            {
                return new McResult(EndDeviceError, [], "软元件范围超出");
            }

            bool[] bits = area.Read(request.Number, count);
            return bitUnit
                ? new McResult(EndOk, McWire.PackBits(bits, _options.Encoding), "读位")
                : new McResult(EndOk, McWire.PackWords(McWire.WordsFromBits(bits), _options.Encoding), "读字（位软元件）");
        }

        WordArea words = _memory.FindWords(device.Symbol)!;
        if (request.Number + request.Points > words.Length)
        {
            return new McResult(EndDeviceError, [], "软元件范围超出");
        }

        return new McResult(EndOk, McWire.PackWords(words.Read(request.Number, request.Points), _options.Encoding), "读字");
    }

    private McResult Write(McRequest request, bool bitUnit)
    {
        McDeviceInfo device = request.Device!;
        if (device.IsBit)
        {
            BitArea area = _memory.FindBits(device.Symbol)!;
            int count = bitUnit ? request.Points : request.Points * 16;
            if (request.Number + count > area.Length)
            {
                return new McResult(EndDeviceError, [], "软元件范围超出");
            }

            bool[] bits = bitUnit
                ? McWire.UnpackBits(request.Data, request.Points, _options.Encoding)
                : McWire.BitsFromWords(McWire.UnpackWords(request.Data, request.Points, _options.Encoding));
            area.Write(request.Number, bits);
            return new McResult(EndOk, [], bitUnit ? "写位" : "写字（位软元件）");
        }

        WordArea words = _memory.FindWords(device.Symbol)!;
        if (request.Number + request.Points > words.Length)
        {
            return new McResult(EndDeviceError, [], "软元件范围超出");
        }

        words.Write(request.Number, McWire.UnpackWords(request.Data, request.Points, _options.Encoding));
        return new McResult(EndOk, [], "写字");
    }

    private static FramingOptions LengthField(int offset, int size, LengthFieldEncoding encoding) => new()
    {
        Mode = FramingMode.LengthField,
        LengthFieldOffset = offset,
        LengthFieldSize = size,
        LengthFieldEncoding = encoding,
    };
}

/// <summary>执行结果：结束码、应答的数据（读出的字节，或环回的数据），以及日志摘要。</summary>
internal sealed record McResult(ushort EndCode, byte[] Data, string Summary);

/// <summary>
/// 解析后的请求：访问路径（二进制为 5 字节，ASCII 为 10 个字符；4C 为子头之后的 8 字节）与 4E 的序列号，
/// 命令与子命令，软元件，点数，写入的数据，以及环回的数据。
/// </summary>
internal sealed record McRequest(
    byte[] Access,
    byte[] Serial,
    ushort Command,
    ushort Subcommand,
    McDeviceInfo? Device,
    int Number,
    int Points,
    byte[] Data,
    byte[] Loopback);
