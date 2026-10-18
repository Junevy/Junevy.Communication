using Junevy.Communication.Modbus.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 模拟的 Modbus 从站（设备替身）：在 <see cref="PlcMemory"/> 上实现 01–06、0F、10、16、17，按请求的帧格式回复。
/// 越过地址上限返回异常码 02，功能码不支持返回异常码 01，数量越界返回异常码 03。
/// 站号集合之外的请求不回复（用来测超时）。可以在回复前加上噪声字节（用来测分帧的重新同步）。
/// </summary>
internal sealed partial class ModbusSlave
{
    private const int DefaultLimit = 1000;

    private readonly ModbusFraming framing;
    private readonly PlcMemory memory;
    private readonly int limit;
    private int requests;

    /// <summary>创建模拟从站。</summary>
    /// <param name="framing">请求与回复的帧格式。</param>
    /// <param name="memory">存储区内容（C、DI、IR、HR）。</param>
    /// <param name="limit">每个存储区的地址上限（不含）。</param>
    public ModbusSlave(ModbusFraming framing, PlcMemory memory, int limit = DefaultLimit)
    {
        this.framing = framing;
        this.memory = memory;
        this.limit = limit;
    }

    /// <summary>会回复的站号。</summary>
    public HashSet<byte> Units { get; } = new HashSet<byte> { 1, 2 };

    /// <summary>每个回复前加在字节流前面的噪声字节（只用于流）。</summary>
    public byte[] NoisePrefix { get; set; } = Array.Empty<byte>();

    /// <summary>按请求序号（从 1 开始）决定是否丢弃该请求（不回复）。</summary>
    public Func<int, bool>? DropRequest { get; set; }

    /// <summary>收到的请求数（含没有回复的请求）。</summary>
    public int RequestCount => Volatile.Read(ref requests);

    /// <summary>处理一个请求帧，返回回复帧；不回复时返回 null。</summary>
    /// <param name="request">请求帧。</param>
    /// <returns>回复帧或 null。</returns>
    public byte[]? Handle(byte[] request)
    {
        int number = Interlocked.Increment(ref requests);
        if (DropRequest?.Invoke(number) == true)
            return null;
        if (!TryReadRequest(request, out byte unit, out byte[] pdu, out ushort transactionId))
            return null;
        if (!Units.Contains(unit))
            return null;

        byte[] reply = Execute(pdu);
        return NoisePrefix.Concat(TestModbus.Frame(transactionId, framing, unit, reply)).ToArray();
    }

    private bool TryReadRequest(byte[] request, out byte unit, out byte[] pdu, out ushort transactionId)
    {
        unit = 0;
        pdu = Array.Empty<byte>();
        transactionId = 0;
        if (framing == ModbusFraming.Mbap)
        {
            if (request.Length < 8)
                return false;
            transactionId = (ushort)((request[0] << 8) | request[1]);
            unit = request[6];
            pdu = request.Skip(7).ToArray();
            return true;
        }

        if (request.Length < 4)
            return false;
        unit = request[0];
        pdu = request.Skip(1).Take(request.Length - 3).ToArray();
        return true;
    }

    private byte[] Execute(byte[] pdu)
    {
        byte function = pdu[0];
        try
        {
            return function switch
            {
                ModbusFunctionCodes.ReadCoils => ReadBits(pdu, "C"),
                ModbusFunctionCodes.ReadDiscreteInputs => ReadBits(pdu, "DI"),
                ModbusFunctionCodes.ReadHoldingRegisters => ReadWords(pdu, "HR"),
                ModbusFunctionCodes.ReadInputRegisters => ReadWords(pdu, "IR"),
                ModbusFunctionCodes.WriteSingleCoil => WriteSingleCoil(pdu),
                ModbusFunctionCodes.WriteSingleRegister => WriteSingleRegister(pdu),
                ModbusFunctionCodes.WriteMultipleCoils => WriteMultipleCoils(pdu),
                ModbusFunctionCodes.WriteMultipleRegisters => WriteMultipleRegisters(pdu),
                ModbusFunctionCodes.MaskWriteRegister => MaskWrite(pdu),
                ModbusFunctionCodes.ReadWriteMultipleRegisters => ReadWriteMultiple(pdu),
                _ => Exception(function, 0x01),
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return Exception(function, 0x02);
        }
    }

    private byte[] ReadBits(byte[] pdu, string area)
    {
        int address = Be16(pdu, 1);
        int count = Be16(pdu, 3);
        if (count < 1 || count > 2000)
            return Exception(pdu[0], 0x03);
        CheckRange(address, count);

        bool[] bits = memory.GetBits(area, address, count);
        var packed = new byte[(count + 7) / 8];
        for (int i = 0; i < count; i++)
        {
            if (bits[i])
                packed[i / 8] |= (byte)(1 << (i % 8));
        }

        return Concat(new[] { pdu[0], (byte)packed.Length }, packed);
    }

    private byte[] ReadWords(byte[] pdu, string area)
    {
        int address = Be16(pdu, 1);
        int count = Be16(pdu, 3);
        if (count < 1 || count > 125)
            return Exception(pdu[0], 0x03);
        CheckRange(address, count);

        ushort[] words = memory.GetWords(area, address, count);
        return Concat(new[] { pdu[0], (byte)(count * 2) }, Words(words));
    }

    private void CheckRange(int address, int count)
    {
        if (address + count > limit)
            throw new ArgumentOutOfRangeException(nameof(address));
    }

    private static byte[] Exception(byte function, byte code) => new[] { (byte)(function | ModbusFunctionCodes.ExceptionFlag), code };

    private static int Be16(byte[] bytes, int index) => (bytes[index] << 8) | bytes[index + 1];

    private static byte[] Words(ushort[] words)
    {
        var bytes = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++)
        {
            bytes[i * 2] = (byte)(words[i] >> 8);
            bytes[i * 2 + 1] = (byte)words[i];
        }

        return bytes;
    }

    private static byte[] Concat(byte[] head, byte[] tail) => head.Concat(tail).ToArray();
}
