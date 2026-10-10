namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// 和校验（手册 p.37）：低 8 位的和，以 2 个大写 ASCII 十六进制字符传输；二进制格式 5 也是如此。
/// </summary>
internal static class McChecksum
{
    /// <summary>和校验字段的宽度（字符数）。</summary>
    public const int Length = 2;

    private static readonly McFieldCodec AsciiCodec = McFieldCodec.For(McEncoding.Ascii);

    /// <summary>计算低 8 位和。</summary>
    /// <param name="bytes">参与求和的字节。</param>
    /// <returns>低 8 位和。</returns>
    public static byte Sum(ReadOnlySpan<byte> bytes)
    {
        byte sum = 0;
        foreach (byte value in bytes)
            sum += value;

        return sum;
    }

    /// <summary>写入 2 个 ASCII 十六进制字符。</summary>
    /// <param name="output">输出。</param>
    /// <param name="sum">和校验值。</param>
    public static void Write(Stream output, byte sum) => AsciiCodec.WriteUInt8(output, sum);

    /// <summary>读取 2 个 ASCII 十六进制字符；字段宽度不是 2 时失败。</summary>
    /// <param name="field">字段。</param>
    /// <param name="sum">读取到的和校验值。</param>
    /// <returns>读取成功返回 true。</returns>
    public static bool TryRead(ReadOnlySpan<byte> field, out byte sum)
    {
        if (field.Length != Length)
        {
            sum = 0;
            return false;
        }

        return AsciiCodec.TryReadUInt8(field, out sum);
    }
}
