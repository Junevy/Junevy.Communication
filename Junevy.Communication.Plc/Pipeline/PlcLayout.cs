namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 套件内部共用的布局常量与协议契约检查。字节布局是规范化布局（P2-D5）：每个字 2 字节，字按地址递增排列，与编码方式无关。
/// </summary>
internal static class PlcLayout
{
    /// <summary>一个字的字节数。</summary>
    public const int WordBytes = 2;

    /// <summary>一个字的位数。位软元件以 16 点为一字访问。</summary>
    public const int BitsPerWord = 16;

    /// <summary>一个字节的位数。字节软元件上的位访问以 8 点为一字节（P4-D6）。</summary>
    public const int BitsPerByte = 8;

    /// <summary>一次操作允许的最大字数，保证字节数不会溢出 <see cref="int"/>。</summary>
    public const int MaxWordCount = int.MaxValue / WordBytes;

    /// <summary>一次操作允许的最大字节数，与 <see cref="MaxWordCount"/> 同理：由字节数推导出的字数与偏移都不会溢出 <see cref="int"/>。</summary>
    public const int MaxByteCount = MaxWordCount * WordBytes;

    /// <summary>
    /// <paramref name="bytes"/> 个字节换算成覆盖它们所需的字数（向上取整）。
    /// </summary>
    /// <param name="bytes">字节数。</param>
    /// <returns>字数。</returns>
    // 写成两项之和而不是 (bytes + 1) / 2：字节数接近 int 上限时加法会溢出。
    public static int WordsFor(int bytes) => bytes / WordBytes + bytes % WordBytes;

    /// <summary>
    /// 检查协议钩子返回的单包点数。小于 1 是协议实现错误，抛出 <see cref="InvalidOperationException"/>，由流水线转换为 Unspecified。
    /// </summary>
    /// <param name="points">钩子返回的值。</param>
    /// <param name="hook">钩子名称，用于错误消息。</param>
    /// <returns>校验通过的点数。</returns>
    public static int RequirePositive(int points, string hook)
    {
        if (points < 1)
            throw new InvalidOperationException($"{hook} returned {points}; a protocol must allow at least one point per packet.");

        return points;
    }

    /// <summary>
    /// 检查协议钩子返回的应答数据长度。不一致是协议实现错误，抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    /// <param name="actual">实际长度。</param>
    /// <param name="expected">期望长度。</param>
    /// <param name="hook">钩子名称，用于错误消息。</param>
    public static void RequireLength(int actual, int expected, string hook)
    {
        if (actual != expected)
            throw new InvalidOperationException($"{hook} returned {actual} item(s) for a packet of {expected}.");
    }
}
