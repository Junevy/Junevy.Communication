namespace Junevy.Communication.DebugAssistant.Protocols.Modbus;

/// <summary>
/// 掩码写（功能码 0x16）的掩码助手（计划第 7.2 节）。设备的结果是 <c>(当前 AND 与掩码) OR (或掩码 AND NOT 与掩码)</c>，
/// 因此置位第 n 位与清零第 n 位各需要两个掩码：与掩码清掉第 n 位，或掩码决定它的新值。
/// </summary>
public static class ModbusMask
{
    /// <summary>置位第 <paramref name="bit"/> 位：与掩码清掉该位，或掩码只有该位。</summary>
    public static (ushort AndMask, ushort OrMask) SetBit(int bit)
    {
        ushort mask = BitOf(bit);
        return ((ushort)~mask, mask);
    }

    /// <summary>清零第 <paramref name="bit"/> 位：与掩码清掉该位，或掩码为零。</summary>
    public static (ushort AndMask, ushort OrMask) ClearBit(int bit)
    {
        ushort mask = BitOf(bit);
        return ((ushort)~mask, 0);
    }

    /// <summary>位号必须在 0 到 15 之间。</summary>
    private static ushort BitOf(int bit)
    {
        if (bit is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(bit), bit, "位号必须在 0 到 15 之间。");
        }

        return (ushort)(1 << bit);
    }
}
