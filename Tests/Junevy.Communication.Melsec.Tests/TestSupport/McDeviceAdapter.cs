using System.Buffers;
using System.Globalization;
using System.Text;
using Junevy.Communication.Melsec.Protocol;
using Junevy.Communication.Plc;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Melsec.Tests;

using Batch = (ushort Command, ushort Subcommand, string Device, int Number, int Points);
using Request = (ushort Command, ushort Subcommand, McDevice? Device, int Number, int Points, byte[] Field);

/// <summary>
/// MELSEC 设备的测试替身（计划 Task 12）：把 MC 请求帧翻译成 <see cref="PlcMemory"/> 操作并生成应答帧。
/// 支持 0401 / 1401（字与位）与 0619 回环，帧为 3E、4E 或 4C（格式 4、5）；字段编码复用 Protocol/ 的编码器。
/// 无法解析或不支持的请求不回复；<see cref="EndCodeOverride"/> 注入异常结束码。
/// </summary>
internal sealed class McDeviceAdapter
{
    private const ushort ReadCommand = 0x0401;
    private const ushort WriteCommand = 0x1401;
    private const ushort LoopbackCommand = 0x0619;
    private const ushort WriteFailedCode = 0xC059;   // C059H：命令或子命令错误（写入数据与字段大小不符）

    private readonly McFrameType frameType;
    private readonly McEncoding encoding;
    private readonly McFieldCodec codec;
    private readonly bool sumCheck;
    private readonly PlcMemory memory;
    private readonly List<Batch> batches = new();

    /// <summary>创建设备替身。</summary>
    public McDeviceAdapter(McFrameType frameType, McEncoding encoding, bool sumCheck, PlcMemory memory)
    {
        this.frameType = frameType;
        this.encoding = encoding;
        this.sumCheck = sumCheck;
        this.memory = memory;
        codec = McFieldCodec.For(encoding);
    }

    /// <summary>非 null 时所有应答都带此结束码（测试异常应答）。</summary>
    public ushort? EndCodeOverride { get; set; }

    /// <summary>已处理的批量请求（命令、子命令、软元件、起始编号、点数），按到达顺序。</summary>
    public IReadOnlyList<Batch> Batches => batches;

    /// <summary>处理一个请求帧并返回应答帧；无法解析或不支持的请求返回 null。</summary>
    public byte[]? Handle(byte[] raw)
        => frameType != McFrameType.Frame4C ? HandleEthernet(raw)
            : encoding == McEncoding.Ascii ? Handle4CFormat4(raw)
            : Handle4CFormat5(raw);

    private byte[]? HandleEthernet(byte[] raw)
    {
        bool is4E = frameType == McFrameType.Frame4E;
        ReadOnlySpan<byte> rest = raw;
        ushort serial = 0;
        if (!McFieldCodec.TryTake(ref rest, codec.UInt16Size, out _)
            || (is4E && (!codec.TryTakeUInt16(ref rest, out serial) || !McFieldCodec.TryTake(ref rest, codec.UInt16Size, out _)))
            || !McFieldCodec.TryTake(ref rest, McAccessRoute.FieldSize(codec), out ReadOnlySpan<byte> route)
            || !codec.TryTakeUInt16(ref rest, out _)       // 请求数据长度
            || !codec.TryTakeUInt16(ref rest, out _)       // 监视定时器
            || !TryParseBody(rest, out Request request))
            return null;

        (ushort endCode, byte[] data) = Execute(request);
        using var frame = new MemoryStream();
        codec.WriteSubheader(frame, is4E ? McCommands.Response4E : McCommands.Response3E);
        if (is4E)
        {
            codec.WriteUInt16(frame, serial);
            codec.WriteUInt16(frame, 0);
        }

        frame.Write(route.ToArray(), 0, route.Length);
        codec.WriteUInt16(frame, checked((ushort)(codec.UInt16Size + data.Length)));
        codec.WriteUInt16(frame, endCode);
        frame.Write(data, 0, data.Length);
        return frame.ToArray();
    }

    // 格式 4：ENQ、帧识别号、访问路径、请求数据。应答为 NAK（异常）、ACK（无数据）或 STX 数据 ETX（和校验覆盖帧识别号到 ETX）。
    private byte[]? Handle4CFormat4(byte[] raw)
    {
        ReadOnlySpan<byte> rest = raw;
        if (!McFieldCodec.TryTake(ref rest, 1, out _) || !McFieldCodec.TryTake(ref rest, codec.UInt8Size, out _)
            || !McFieldCodec.TryTake(ref rest, Mc4CAccessRoute.FieldSize(codec), out ReadOnlySpan<byte> route)
            || !TryParseBody(rest, out Request request))
            return null;

        (ushort endCode, byte[] data) = Execute(request);
        byte[] header = route.ToArray();
        using var frame = new MemoryStream();
        if (endCode != 0 || data.Length == 0)
        {
            frame.WriteByte(endCode != 0 ? McSerialCodes.Nak : McSerialCodes.Ack);
            WriteFrameId(frame, header, endCode != 0 ? endCode : null);
            return frame.ToArray();
        }

        frame.WriteByte(McSerialCodes.Stx);
        int summedStart = (int)frame.Length;
        WriteFrameId(frame, header, null);
        frame.Write(data, 0, data.Length);
        frame.WriteByte(McSerialCodes.Etx);
        if (sumCheck)
            McChecksum.Write(frame, McChecksum.Sum(new ReadOnlySpan<byte>(frame.GetBuffer(), summedStart, (int)frame.Length - summedStart)));
        return frame.ToArray();
    }

    // 格式 5：DLE STX、附加码转义的（字节数、帧识别号、访问路径、请求数据）、DLE ETX、和校验；字节数与和校验都不含附加码。
    private byte[]? Handle4CFormat5(byte[] raw)
    {
        if (McDleFrame.Scan(new ReadOnlySequence<byte>(raw), out _, out byte[] payload) != McDleFrame.ScanResult.Complete)
            return null;

        ReadOnlySpan<byte> rest = payload;
        if (!McFieldCodec.TryTake(ref rest, codec.UInt16Size, out _) || !McFieldCodec.TryTake(ref rest, codec.UInt8Size, out _)
            || !McFieldCodec.TryTake(ref rest, Mc4CAccessRoute.FieldSize(codec), out ReadOnlySpan<byte> route)
            || !TryParseBody(rest, out Request request))
            return null;

        (ushort endCode, byte[] data) = Execute(request);
        using var logical = new MemoryStream();
        WriteFrameId(logical, route.ToArray(), null);
        codec.WriteUInt16(logical, McSerialCodes.ResponseId);
        codec.WriteUInt16(logical, endCode);
        logical.Write(data, 0, data.Length);
        byte[] body = logical.ToArray();

        using var counted = new MemoryStream();
        codec.WriteUInt16(counted, checked((ushort)body.Length));
        counted.Write(body, 0, body.Length);
        byte[] countedBytes = counted.ToArray();

        using var frame = new MemoryStream();
        frame.WriteByte(McSerialCodes.Dle);
        frame.WriteByte(McSerialCodes.Stx);
        McDleFrame.WriteEscaped(frame, countedBytes);
        frame.WriteByte(McSerialCodes.Dle);
        frame.WriteByte(McSerialCodes.Etx);
        if (sumCheck)
            McChecksum.Write(frame, McChecksum.Sum(countedBytes));
        return frame.ToArray();
    }

    private void WriteFrameId(MemoryStream frame, byte[] route, ushort? errorCode)
    {
        codec.WriteUInt8(frame, McSerialCodes.FrameId);
        frame.Write(route, 0, route.Length);
        if (errorCode is ushort code)
            codec.WriteUInt16(frame, code);
    }

    // 命令体：0619 为子命令、字节数与数据；0401 / 1401 为子命令、软元件、点数，写入时其后是数据；其他命令不支持。
    private bool TryParseBody(ReadOnlySpan<byte> body, out Request request)
    {
        request = default;
        ReadOnlySpan<byte> rest = body;
        if (!codec.TryTakeUInt16(ref rest, out ushort command) || !codec.TryTakeUInt16(ref rest, out ushort subcommand))
            return false;

        if (command == LoopbackCommand)
        {
            if (!codec.TryTakeUInt16(ref rest, out ushort count) || rest.Length < count)
                return false;

            request = (command, subcommand, null, 0, count, rest.Slice(0, count).ToArray());
            return true;
        }

        if (command != ReadCommand && command != WriteCommand)
            return false;

        if (!TryParseDevice(ref rest, out McDevice? device, out int number) || !codec.TryTakeUInt16(ref rest, out ushort points))
            return false;

        int fieldSize = command == ReadCommand ? 0 : subcommand == McCommands.BitUnit ? codec.BitFieldSize(points) : codec.WordFieldSize(points);
        if (rest.Length < fieldSize)
            return false;

        request = (command, subcommand, device, number, points, rest.Slice(0, fieldSize).ToArray());
        return true;
    }

    private bool TryParseDevice(ref ReadOnlySpan<byte> rest, out McDevice? device, out int number)
    {
        device = null;
        number = 0;
        if (encoding == McEncoding.Binary)
        {
            if (!McFieldCodec.TryTake(ref rest, 4, out ReadOnlySpan<byte> field))
                return false;

            number = field[0] | (field[1] << 8) | (field[2] << 16);
            byte binaryCode = field[3];
            device = McDeviceTable.Devices.FirstOrDefault(candidate => candidate.BinaryCode == binaryCode);
            return device != null;
        }

        if (!McFieldCodec.TryTake(ref rest, 2, out ReadOnlySpan<byte> code) || !McFieldCodec.TryTake(ref rest, 6, out ReadOnlySpan<byte> digits))
            return false;

        string symbolCode = Encoding.ASCII.GetString(code.ToArray());
        device = McDeviceTable.Devices.FirstOrDefault(candidate => candidate.AsciiCode == symbolCode);
        if (device == null)
            return false;

        string text = Encoding.ASCII.GetString(digits.ToArray());
        number = device.Radix == 16 ? Convert.ToInt32(text, 16) : int.Parse(text, CultureInfo.InvariantCulture);
        return true;
    }

    private (ushort EndCode, byte[] Data) Execute(Request request)
    {
        if (EndCodeOverride is ushort injected)
            return (injected, Array.Empty<byte>());

        if (request.Command == LoopbackCommand)
            return (0, request.Field);

        McDevice device = request.Device!;
        string symbol = device.Symbol;
        batches.Add((request.Command, request.Subcommand, symbol, request.Number, request.Points));
        bool bitUnit = request.Subcommand == McCommands.BitUnit;
        bool wordUnit = device.Unit == PlcAddressUnit.Word;
        if (request.Command == ReadCommand)
        {
            using var data = new MemoryStream();
            if (bitUnit)
                codec.WriteBits(data, memory.GetBits(symbol, request.Number, request.Points));
            else
                codec.WriteWords(data, ToLittleEndian(wordUnit
                    ? memory.GetWords(symbol, request.Number, request.Points)
                    : memory.GetBitsAsWords(symbol, request.Number, request.Points)));
            return (0, data.ToArray());
        }

        if (bitUnit)
        {
            var bits = new bool[request.Points];
            if (!codec.TryReadBits(request.Field, bits))
                return (WriteFailedCode, Array.Empty<byte>());

            memory.SetBits(symbol, request.Number, bits);
        }
        else
        {
            var littleEndian = new byte[request.Points * McFieldCodec.NormalizedWordBytes];
            if (!codec.TryReadWords(request.Field, littleEndian))
                return (WriteFailedCode, Array.Empty<byte>());

            ushort[] words = FromLittleEndian(littleEndian);
            if (wordUnit)
                memory.SetWords(symbol, request.Number, words);
            else
                memory.SetBitsFromWords(symbol, request.Number, words);
        }

        return (0, Array.Empty<byte>());
    }

    private static byte[] ToLittleEndian(ushort[] words)
        => words.SelectMany(word => new[] { (byte)word, (byte)(word >> 8) }).ToArray();

    private static ushort[] FromLittleEndian(byte[] bytes)
        => Enumerable.Range(0, bytes.Length / McFieldCodec.NormalizedWordBytes)
            .Select(i => (ushort)(bytes[2 * i] | (bytes[(2 * i) + 1] << 8))).ToArray();
}
