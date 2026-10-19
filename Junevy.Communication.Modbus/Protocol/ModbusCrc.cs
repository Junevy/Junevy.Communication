namespace Junevy.Communication.Modbus.Protocol;

/// <summary>
/// CRC-16/MODBUS：初值 0xFFFF，反射多项式 0xA001，校验值低字节在前（串行线路的 RTU 帧）。
/// </summary>
internal static class ModbusCrc
{
    private const ushort ReflectedPolynomial = 0xA001;

    /// <summary>CRC 的初值。</summary>
    public const ushort Initial = 0xFFFF;

    /// <summary>计算整段数据的 CRC。</summary>
    /// <param name="data">数据。</param>
    /// <returns>CRC 值。</returns>
    public static ushort Compute(ReadOnlySpan<byte> data) => Update(Initial, data);

    /// <summary>从已有的 CRC 状态继续计算，便于分段累加。</summary>
    /// <param name="crc">已累计的 CRC 状态（首段使用 <see cref="Initial"/>）。</param>
    /// <param name="data">本段数据。</param>
    /// <returns>更新后的 CRC 状态。</returns>
    public static ushort Update(ushort crc, ReadOnlySpan<byte> data)
    {
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ ReflectedPolynomial) : (ushort)(crc >> 1);
        }

        return crc;
    }
}
