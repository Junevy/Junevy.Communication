namespace Junevy.Communication.DebugAssistant.Simulators.Modbus;

/// <summary>CRC-16/MODBUS（多项式 0xA001 的反射形式，初值 0xFFFF），低字节在前（MODBUS over Serial Line V1.02）。</summary>
internal static class Crc16Modbus
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    /// <summary>在 <paramref name="body"/> 后追加 CRC，返回完整的 RTU 帧。</summary>
    public static byte[] Append(ReadOnlySpan<byte> body)
    {
        ushort crc = Compute(body);
        byte[] frame = new byte[body.Length + 2];
        body.CopyTo(frame);
        frame[^2] = (byte)crc;
        frame[^1] = (byte)(crc >> 8);
        return frame;
    }

    /// <summary>完整帧的 CRC 是否正确（至少包含站号、功能码与 CRC 两字节）。</summary>
    public static bool Verify(ReadOnlySpan<byte> frame) =>
        frame.Length >= 4 && Compute(frame[..^2]) == (ushort)(frame[^2] | (frame[^1] << 8));
}
