namespace Junevy.Communication.DebugAssistant.Common;

/// <summary>
/// 校验计算（计划 DA-D12）。CRC-16/MODBUS 低字节在前（Modbus RTU 的线上格式）；
/// CRC-16/CCITT-FALSE 高字节在前（XMODEM 类协议的常见写法，计划未指定，此处为助手的约定）。
/// </summary>
public static class Checksums
{
    /// <summary>返回 <paramref name="payload"/> 加上校验后的新数组。</summary>
    public static byte[] Append(ChecksumKind kind, ReadOnlySpan<byte> payload)
    {
        byte[] trailer = Compute(kind, payload);
        var result = new byte[payload.Length + trailer.Length];
        payload.CopyTo(result);
        trailer.CopyTo(result, payload.Length);
        return result;
    }

    /// <summary>只计算校验本身（不含负载）。</summary>
    public static byte[] Compute(ChecksumKind kind, ReadOnlySpan<byte> payload) => kind switch
    {
        ChecksumKind.None => [],
        ChecksumKind.Sum8 => [Sum8(payload)],
        ChecksumKind.Xor8 => [Xor8(payload)],
        ChecksumKind.Lrc => [Lrc(payload)],
        ChecksumKind.Crc16Modbus => LowByteFirst(Crc16Modbus(payload)),
        ChecksumKind.Crc16CcittFalse => HighByteFirst(Crc16CcittFalse(payload)),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown checksum kind."),
    };

    private static byte Sum8(ReadOnlySpan<byte> payload)
    {
        byte sum = 0;
        foreach (byte value in payload)
        {
            sum = unchecked((byte)(sum + value));
        }

        return sum;
    }

    private static byte Xor8(ReadOnlySpan<byte> payload)
    {
        byte result = 0;
        foreach (byte value in payload)
        {
            result ^= value;
        }

        return result;
    }

    /// <summary>纵向冗余校验：和的二进制补码（Modbus ASCII 使用的写法）。</summary>
    private static byte Lrc(ReadOnlySpan<byte> payload) => unchecked((byte)(0 - Sum8(payload)));

    private static ushort Crc16Modbus(ReadOnlySpan<byte> payload)
    {
        const ushort reflectedPolynomial = 0xA001;
        ushort crc = 0xFFFF;
        foreach (byte value in payload)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ reflectedPolynomial) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    private static ushort Crc16CcittFalse(ReadOnlySpan<byte> payload)
    {
        const ushort polynomial = 0x1021;
        ushort crc = 0xFFFF;
        foreach (byte value in payload)
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ polynomial) : (ushort)(crc << 1);
            }
        }

        return crc;
    }

    private static byte[] LowByteFirst(ushort crc) => [(byte)(crc & 0xFF), (byte)(crc >> 8)];

    private static byte[] HighByteFirst(ushort crc) => [(byte)(crc >> 8), (byte)(crc & 0xFF)];
}
