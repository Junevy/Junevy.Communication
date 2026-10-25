using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.DebugAssistant.Simulators.Faults;
using Junevy.Communication.DebugAssistant.Simulators.Hosting;
using Junevy.Communication.DebugAssistant.Simulators.Memory;

namespace Junevy.Communication.DebugAssistant.Simulators.Modbus;

/// <summary>Modbus 的帧格式：MBAP（TCP 与 UDP）或 RTU（串口，以及 TCP 与 UDP 上的 RTU over TCP）。</summary>
public enum ModbusFrameMode
{
    Mbap,
    Rtu,
}

/// <summary>Modbus 从站的设置：应答的站号列表与帧格式（计划第 7.14 节）。串口只使用 RTU（界面保证）。</summary>
public sealed record ModbusSlaveOptions(IReadOnlyCollection<byte> UnitIds, ModbusFrameMode Frame);

/// <summary>
/// Modbus 从站引擎（计划第 8.1 节、第 8.2 节）：MBAP 或 RTU 帧，PDU 与异常按协议处理。站号不在列表中的请求不应答。
/// 破坏校验只对 RTU 有效（改写 CRC）；MBAP 没有校验字节。
/// </summary>
public sealed class ModbusSlaveEngine : ISimulatorEngine
{
    /// <summary>四个数据区的名称与长度（每个区 65536 个元素，协议地址 0 到 65535）。</summary>
    public const string CoilArea = "C";
    public const string InputArea = "DI";
    public const string HoldingArea = "HR";
    public const string RegisterArea = "IR";
    private const int AreaLength = 65536;

    private readonly ModbusPduProcessor _processor;
    private readonly HashSet<byte> _units;
    private readonly ModbusFrameMode _frame;

    public ModbusSlaveEngine(SimulatorMemory memory, ModbusSlaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(options);
        if (options.UnitIds.Count == 0)
        {
            throw new ArgumentException("至少需要一个站号。", nameof(options));
        }

        _units = [.. options.UnitIds];
        _frame = options.Frame;
        _processor = new ModbusPduProcessor(
            memory.FindBits(CoilArea) ?? memory.AddBits(CoilArea, AreaLength),
            memory.FindBits(InputArea) ?? memory.AddBits(InputArea, AreaLength),
            memory.FindWords(HoldingArea) ?? memory.AddWords(HoldingArea, AreaLength),
            memory.FindWords(RegisterArea) ?? memory.AddWords(RegisterArea, AreaLength));
    }

    /// <inheritdoc />
    public IFrameCodecFactory? CreateRequestCodec(SimulatorTransport transport) => transport switch
    {
        SimulatorTransport.Udp => null,
        _ when _frame == ModbusFrameMode.Rtu || transport == SimulatorTransport.Serial => new RtuFrameCodecFactory(),
        _ => FrameCodecFactory.Create(new FramingOptions
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = 4,
            LengthFieldSize = 2,
            LengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian,
        }),
    };

    /// <inheritdoc />
    public SimulatorReply Handle(ReadOnlySpan<byte> request, SimulatorConnection connection, FaultPlan fault)
    {
        if (!TryReadAdu(request, out ModbusAdu adu))
        {
            return SimulatorReply.Silent("无效的 Modbus 帧，已忽略");
        }

        if (!_units.Contains(adu.Unit))
        {
            return SimulatorReply.Silent($"站号 {adu.Unit} 不在本模拟器中，已忽略");
        }

        ModbusPduResult result = fault.ForcedErrorCode is { } code
            ? new ModbusPduResult([(byte)(adu.Pdu[0] | 0x80), (byte)code], $"{adu.Pdu[0]:X2} 强制错误 {code:X4}")
            : _processor.Process(adu.Pdu);
        byte[] response = adu.IsMbap ? BuildMbap(adu, result.Pdu) : BuildRtu(adu.Unit, result.Pdu, fault.CorruptChecksum);
        return SimulatorReply.Respond(response, $"站号 {adu.Unit}：{result.Summary}");
    }

    private bool TryReadAdu(ReadOnlySpan<byte> request, out ModbusAdu adu)
    {
        adu = default!;
        if (_frame == ModbusFrameMode.Mbap)
        {
            if (request.Length < 8)
            {
                return false;
            }

            int protocol = (request[2] << 8) | request[3];
            int length = (request[4] << 8) | request[5];
            if (protocol != 0 || length != request.Length - 6)
            {
                return false;
            }

            adu = new ModbusAdu(true, request[..2].ToArray(), request[6], request[7..].ToArray());
            return true;
        }

        if (request.Length < 4 || !Crc16Modbus.Verify(request))
        {
            return false;
        }

        adu = new ModbusAdu(false, [], request[0], request[1..^2].ToArray());
        return true;
    }

    private static byte[] BuildMbap(ModbusAdu adu, byte[] pdu)
    {
        int length = pdu.Length + 1;
        return [adu.Transaction[0], adu.Transaction[1], 0, 0, (byte)(length >> 8), (byte)length, adu.Unit, .. pdu];
    }

    private static byte[] BuildRtu(byte unit, byte[] pdu, bool corruptChecksum)
    {
        byte[] frame = Crc16Modbus.Append([unit, .. pdu]);
        if (corruptChecksum)
        {
            frame[^1] ^= 0x01;
        }

        return frame;
    }
}

/// <summary>一个请求的应用数据单元：MBAP 的事务号（RTU 没有）、站号与 PDU。</summary>
internal sealed record ModbusAdu(bool IsMbap, byte[] Transaction, byte Unit, byte[] Pdu);
