using System.Globalization;
using System.Text;

namespace Junevy.Communication.DebugAssistant.Simulators.Melsec;

/// <summary>
/// MC 的线上格式（计划第 8.2 节，手册的帧结构）：请求的解析与应答的编码，二进制与 ASCII 两种编码，以及位与字的打包。
/// 位软元件的二进制打包：每个字节两个点，高半字节为偶数点，低半字节为奇数点（0 或 1）；ASCII 中每个点一个字符 '0' 或 '1'。
/// 字的 ASCII 形式为四个大写十六进制字符（高位在前）。
/// </summary>
internal static class McWire
{
    private const byte Dle = 0x10;
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;
    private const byte Nak = 0x15;
    private const byte Enq = 0x05;
    private const ushort LoopbackCommand = 0x0619;

    /// <summary>解析一个请求；格式不符时返回 null。</summary>
    public static McRequest? Parse(McSlaveOptions options, ReadOnlySpan<byte> request)
    {
        try
        {
            return (options.Frame, options.Encoding) switch
            {
                (McFrameKind.Frame3E, McEncodingKind.Binary) => ParseBinaryNetwork(request, is4E: false),
                (McFrameKind.Frame4E, McEncodingKind.Binary) => ParseBinaryNetwork(request, is4E: true),
                (McFrameKind.Frame3E, McEncodingKind.Ascii) => ParseAsciiNetwork(request, is4E: false),
                (McFrameKind.Frame4E, McEncodingKind.Ascii) => ParseAsciiNetwork(request, is4E: true),
                (McFrameKind.Frame4C, McEncodingKind.Ascii) => ParseAscii4C(request),
                _ => ParseBinary4C(request),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>编码应答。<paramref name="corruptChecksum"/> 为真时改写和校验字符（或 4C 格式 5 的校验）。</summary>
    public static byte[] Build(McSlaveOptions options, McRequest request, McResult result, bool corruptChecksum) => (options.Frame, options.Encoding) switch
    {
        (McFrameKind.Frame3E, McEncodingKind.Binary) => BuildBinaryNetwork(request, result, is4E: false),
        (McFrameKind.Frame4E, McEncodingKind.Binary) => BuildBinaryNetwork(request, result, is4E: true),
        (McFrameKind.Frame3E, McEncodingKind.Ascii) => BuildAsciiNetwork(request, result, is4E: false),
        (McFrameKind.Frame4E, McEncodingKind.Ascii) => BuildAsciiNetwork(request, result, is4E: true),
        (McFrameKind.Frame4C, McEncodingKind.Ascii) => BuildAscii4C(options, request, result, corruptChecksum),
        _ => BuildBinary4C(options, request, result, corruptChecksum),
    };

    public static byte[] PackBits(bool[] bits, McEncodingKind encoding)
    {
        if (encoding == McEncodingKind.Ascii)
        {
            return Encoding.ASCII.GetBytes(bits.Select(bit => bit ? '1' : '0').ToArray());
        }

        byte[] packed = new byte[(bits.Length + 1) / 2];
        for (int index = 0; index < bits.Length; index++)
        {
            if (bits[index])
            {
                packed[index / 2] |= (byte)(index % 2 == 0 ? 0x10 : 0x01);
            }
        }

        return packed;
    }

    public static bool[] UnpackBits(byte[] data, int count, McEncodingKind encoding)
    {
        bool[] bits = new bool[count];
        for (int index = 0; index < count; index++)
        {
            bits[index] = encoding == McEncodingKind.Ascii
                ? data[index] == '1'
                : index % 2 == 0 ? (data[index / 2] & 0xF0) != 0 : (data[index / 2] & 0x0F) != 0;
        }

        return bits;
    }

    public static byte[] PackWords(ushort[] words, McEncodingKind encoding)
    {
        if (encoding == McEncodingKind.Ascii)
        {
            return Encoding.ASCII.GetBytes(string.Concat(words.Select(word => word.ToString("X4", CultureInfo.InvariantCulture))));
        }

        byte[] data = new byte[words.Length * 2];
        for (int index = 0; index < words.Length; index++)
        {
            data[index * 2] = (byte)words[index];
            data[(index * 2) + 1] = (byte)(words[index] >> 8);
        }

        return data;
    }

    public static ushort[] UnpackWords(byte[] data, int count, McEncodingKind encoding)
    {
        ushort[] words = new ushort[count];
        for (int index = 0; index < count; index++)
        {
            words[index] = encoding == McEncodingKind.Ascii
                ? ParseHex(data.AsSpan(index * 4, 4))
                : (ushort)(data[index * 2] | (data[(index * 2) + 1] << 8));
        }

        return words;
    }

    /// <summary>按位软元件的字访问：每 16 个点组成一个字，点 0 为最低位。</summary>
    public static ushort[] WordsFromBits(bool[] bits)
    {
        ushort[] words = new ushort[(bits.Length + 15) / 16];
        for (int index = 0; index < bits.Length; index++)
        {
            if (bits[index])
            {
                words[index / 16] |= (ushort)(1 << (index % 16));
            }
        }

        return words;
    }

    public static bool[] BitsFromWords(ushort[] words)
    {
        bool[] bits = new bool[words.Length * 16];
        for (int index = 0; index < bits.Length; index++)
        {
            bits[index] = (words[index / 16] & (1 << (index % 16))) != 0;
        }

        return bits;
    }

    /// <summary>二进制 3E 或 4E 的请求：子头 50 00（3E）或 54 00（4E），访问路径，长度，计时器，命令块。</summary>
    private static McRequest? ParseBinaryNetwork(ReadOnlySpan<byte> frame, bool is4E)
    {
        byte expected = is4E ? (byte)0x54 : (byte)0x50;
        if (frame.Length < 2 || frame[0] != expected || frame[1] != 0x00)
        {
            return null;
        }

        int accessOffset = is4E ? 6 : 2;
        int lengthOffset = is4E ? 11 : 7;
        byte[] access = frame.Slice(accessOffset, 5).ToArray();
        byte[] serial = is4E ? frame.Slice(2, 2).ToArray() : [];
        int length = frame[lengthOffset] | (frame[lengthOffset + 1] << 8);
        int timerOffset = lengthOffset + 2;
        ReadOnlySpan<byte> block = frame.Slice(timerOffset + 2, length - 2);
        return ParseBinaryBlock(access, serial, block);
    }

    /// <summary>二进制命令块：命令、子命令，然后是软元件编号（3 字节）、代码（1 字节）、点数（2 字节）与数据；环回的数据为长度字段之后的全部内容。</summary>
    private static McRequest? ParseBinaryBlock(byte[] access, byte[] serial, ReadOnlySpan<byte> block)
    {
        ushort command = (ushort)(block[0] | (block[1] << 8));
        ushort subcommand = (ushort)(block[2] | (block[3] << 8));
        if (command == LoopbackCommand)
        {
            return new McRequest(access, serial, command, subcommand, null, 0, 0, [], block[4..].ToArray());
        }

        int number = block[4] | (block[5] << 8) | (block[6] << 16);
        McDeviceInfo? device = McDeviceTable.FindByCode(block[7]);
        int points = block[8] | (block[9] << 8);
        return new McRequest(access, serial, command, subcommand, device, number, points, block[10..].ToArray(), []);
    }

    /// <summary>ASCII 3E 或 4E 的请求：子头 5000 / 5400，访问路径（10 个字符），长度（4 个字符），计时器（4 个字符），命令块（ASCII）。</summary>
    private static McRequest? ParseAsciiNetwork(ReadOnlySpan<byte> frame, bool is4E)
    {
        string subheader = Encoding.ASCII.GetString(frame[..4]);
        if (!subheader.Equals(is4E ? "5400" : "5000", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int accessOffset = is4E ? 12 : 4;
        int lengthOffset = accessOffset + 10;
        byte[] access = frame.Slice(accessOffset, 10).ToArray();
        byte[] serial = is4E ? frame.Slice(4, 4).ToArray() : [];
        int length = ParseHex(frame.Slice(lengthOffset, 4));
        int timerOffset = lengthOffset + 4;
        ReadOnlySpan<byte> block = frame.Slice(timerOffset + 4, length - 4);
        return ParseAsciiBlock(access, serial, block);
    }

    /// <summary>ASCII 命令块：命令（4 字符）、子命令（4 字符）、软元件符号（2 字符）、编号（6 字符）、点数（4 字符）、数据。</summary>
    private static McRequest? ParseAsciiBlock(byte[] access, byte[] serial, ReadOnlySpan<byte> block)
    {
        ushort command = ParseHex(block[..4]);
        ushort subcommand = ParseHex(block.Slice(4, 4));
        if (command == LoopbackCommand)
        {
            return new McRequest(access, serial, command, subcommand, null, 0, 0, [], block[8..].ToArray());
        }

        string symbol = Encoding.ASCII.GetString(block.Slice(8, 2));
        McDeviceInfo? device = McDeviceTable.FindBySymbol(symbol);
        string numberText = Encoding.ASCII.GetString(block.Slice(10, 6));
        int number = device is { IsHexadecimal: true }
            ? int.Parse(numberText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : int.Parse(numberText, NumberStyles.None, CultureInfo.InvariantCulture);
        int points = ParseHex(block.Slice(16, 4));
        return new McRequest(access, serial, command, subcommand, device, number, points, block[20..].ToArray(), []);
    }

    /// <summary>4C 格式 5 的请求：DLE STX 之后是未转义的 2 字节长度与帧体，帧体前 8 字节为访问路径（子头 F8 起）。</summary>
    private static McRequest? ParseBinary4C(ReadOnlySpan<byte> frame)
    {
        byte[] body = Unescape(frame);
        if (body.Length < 4 || body[0] != Stx)
        {
            return null;
        }

        int length = body[1] | (body[2] << 8);
        if (body.Length < 3 + length)
        {
            return null;
        }

        byte[] payload = body[3..(3 + length)];
        byte[] access = payload[..8];
        return ParseBinaryBlock(access, [], payload[8..]);
    }

    /// <summary>4C 格式 4 的请求（ENQ 与 CRLF 已由分帧去掉）：ASCII 的 8 字节访问路径（16 个字符），然后是命令块。</summary>
    private static McRequest? ParseAscii4C(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 16 + 20)
        {
            return null;
        }

        return ParseAsciiBlock(frame[..16].ToArray(), [], frame[16..]);
    }

    private static byte[] BuildBinaryNetwork(McRequest request, McResult result, bool is4E)
    {
        byte[] data = result.EndCode == 0 ? result.Data : ErrorInfoBinary(request);
        List<byte> response = is4E ? [0xD4, 0x00, .. request.Serial, 0x00, 0x00] : [0xD0, 0x00];
        response.AddRange(request.Access);
        response.Add((byte)((2 + data.Length) & 0xFF));
        response.Add((byte)((2 + data.Length) >> 8));
        response.Add((byte)result.EndCode);
        response.Add((byte)(result.EndCode >> 8));
        response.AddRange(data);
        return [.. response];
    }

    private static byte[] BuildAsciiNetwork(McRequest request, McResult result, bool is4E)
    {
        byte[] data = result.EndCode == 0 ? result.Data : ErrorInfoAscii(request);
        string prefix = is4E
            ? "D400" + Encoding.ASCII.GetString(request.Serial) + "0000"
            : "D000";
        string text = prefix
            + Encoding.ASCII.GetString(request.Access)
            + (4 + data.Length).ToString("X4", CultureInfo.InvariantCulture)
            + result.EndCode.ToString("X4", CultureInfo.InvariantCulture)
            + Encoding.ASCII.GetString(data);
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>4C 格式 4 的应答：STX、访问路径（ASCII）、数据、ETX、和校验（两个 ASCII 字符）、CRLF；错误时为 NAK、访问路径、结束码、CRLF。</summary>
    private static byte[] BuildAscii4C(McSlaveOptions options, McRequest request, McResult result, bool corruptChecksum)
    {
        if (result.EndCode != 0)
        {
            return [Nak, .. request.Access, .. Encoding.ASCII.GetBytes(result.EndCode.ToString("X4", CultureInfo.InvariantCulture)), 0x0D, 0x0A];
        }

        List<byte> response = [Stx, .. request.Access, .. result.Data, Etx];
        if (options.SumCheck)
        {
            // 和校验含 ETX，从 STX 之后算起。
            byte checksum = Sum(response.ToArray(), 1);
            if (corruptChecksum)
            {
                checksum ^= 0x01;
            }

            response.AddRange(Encoding.ASCII.GetBytes(checksum.ToString("X2", CultureInfo.InvariantCulture)));
        }

        response.Add(0x0D);
        response.Add(0x0A);
        return [.. response];
    }

    /// <summary>
    /// 4C 格式 5 的应答：DLE STX、长度（2 字节）、帧识别号与访问路径、响应 ID FF FF、完成码（错误时为错误码）、数据，
    /// 然后 DLE ETX、和校验（两个 ASCII 字符，开启时）；帧体中的 DLE 写成 DLE DLE。
    /// </summary>
    private static byte[] BuildBinary4C(McSlaveOptions options, McRequest request, McResult result, bool corruptChecksum)
    {
        byte[] data = result.EndCode == 0 ? result.Data : [];
        byte[] payload = [.. request.Access, 0xFF, 0xFF, (byte)result.EndCode, (byte)(result.EndCode >> 8), .. data];
        byte[] unescaped = [(byte)payload.Length, (byte)(payload.Length >> 8), .. payload];

        List<byte> response = [Dle, Stx];
        response.AddRange(Escape(unescaped));
        response.Add(Dle);
        response.Add(Etx);
        if (options.SumCheck)
        {
            byte checksum = Sum(unescaped, 0);
            string text = checksum.ToString("X2", CultureInfo.InvariantCulture);
            if (corruptChecksum)
            {
                text = (checksum ^ 1).ToString("X2", CultureInfo.InvariantCulture);
            }

            response.AddRange(Encoding.ASCII.GetBytes(text));
        }

        return [.. response];
    }

    private static byte[] ErrorInfoBinary(McRequest request) =>
        [.. request.Access, (byte)request.Command, (byte)(request.Command >> 8), (byte)request.Subcommand, (byte)(request.Subcommand >> 8)];

    private static byte[] ErrorInfoAscii(McRequest request) => Encoding.ASCII.GetBytes(
        Encoding.ASCII.GetString(request.Access)
        + request.Command.ToString("X4", CultureInfo.InvariantCulture)
        + request.Subcommand.ToString("X4", CultureInfo.InvariantCulture));

    /// <summary>去掉 DLE 的转义（DLE DLE 还原为 DLE），从 DLE STX 之后开始，到 DLE ETX 为止。</summary>
    private static byte[] Unescape(ReadOnlySpan<byte> frame)
    {
        List<byte> body = [];
        int index = 0;
        if (frame.Length >= 2 && frame[0] == Dle && frame[1] == Stx)
        {
            index = 2;
        }

        while (index < frame.Length)
        {
            if (frame[index] == Dle)
            {
                if (index + 1 < frame.Length && frame[index + 1] == Etx)
                {
                    break;
                }

                if (index + 1 < frame.Length && frame[index + 1] == Dle)
                {
                    body.Add(Dle);
                    index += 2;
                    continue;
                }
            }

            body.Add(frame[index]);
            index++;
        }

        return [Stx, .. body];
    }

    private static IEnumerable<byte> Escape(byte[] data)
    {
        foreach (byte value in data)
        {
            yield return value;
            if (value == Dle)
            {
                yield return Dle;
            }
        }
    }

    /// <summary>和校验：从 <paramref name="start"/> 起的字节之和的低字节。</summary>
    private static byte Sum(byte[] data, int start)
    {
        int sum = 0;
        for (int index = start; index < data.Length; index++)
        {
            sum += data[index];
        }

        return (byte)sum;
    }

    private static ushort ParseHex(ReadOnlySpan<byte> text) =>
        ushort.Parse(Encoding.ASCII.GetString(text), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}
