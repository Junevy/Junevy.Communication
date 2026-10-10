namespace Junevy.Communication.Melsec.Protocol;

/// <summary>
/// MC 请求数据（命令、子命令与参数）的编码：0401 批量读、1401 批量写（字与位）、0619 回环。
/// 三者只通过 <see cref="McFieldCodec"/> 输出字段，因此二进制、ASCII 以及 3E / 4E / 4C 共用这里的字段顺序（P2 设计 Q2）。
/// </summary>
internal static class McRequestBody
{
    /// <summary>批量读出：命令、子命令、软元件、点数（手册 p.89、p.91）。</summary>
    /// <param name="codec">字段编码策略。</param>
    /// <param name="subcommand">字单位或位单位子命令。</param>
    /// <param name="device">软元件。</param>
    /// <param name="number">首个软元件的编号。</param>
    /// <param name="points">字数或点数。</param>
    /// <returns>请求数据。</returns>
    public static byte[] BatchRead(McFieldCodec codec, ushort subcommand, McDevice device, int number, int points)
    {
        using var body = new MemoryStream();
        WriteBatchHeader(codec, body, McCommands.BatchRead, subcommand, device, number, points);
        return body.ToArray();
    }

    /// <summary>字单位批量写入（手册 p.94）。</summary>
    /// <param name="codec">字段编码策略。</param>
    /// <param name="device">软元件。</param>
    /// <param name="number">首个软元件的编号。</param>
    /// <param name="littleEndianWords">规范化布局的字数据（每字 2 字节小端）。</param>
    /// <returns>请求数据。</returns>
    public static byte[] BatchWriteWords(McFieldCodec codec, McDevice device, int number, ReadOnlySpan<byte> littleEndianWords)
    {
        using var body = new MemoryStream();
        WriteBatchHeader(codec, body, McCommands.BatchWrite, McCommands.WordUnit, device, number, littleEndianWords.Length / 2);
        codec.WriteWords(body, littleEndianWords);
        return body.ToArray();
    }

    /// <summary>位单位批量写入（手册 p.96）。</summary>
    /// <param name="codec">字段编码策略。</param>
    /// <param name="device">软元件。</param>
    /// <param name="number">首个软元件的编号。</param>
    /// <param name="points">要写入的点值。</param>
    /// <returns>请求数据。</returns>
    public static byte[] BatchWriteBits(McFieldCodec codec, McDevice device, int number, ReadOnlySpan<bool> points)
    {
        using var body = new MemoryStream();
        WriteBatchHeader(codec, body, McCommands.BatchWrite, McCommands.BitUnit, device, number, points.Length);
        codec.WriteBits(body, points);
        return body.ToArray();
    }

    /// <summary>回环测试：子命令 0000，回环数据的字节数，然后是数据（手册 p.184–185）。</summary>
    /// <param name="codec">字段编码策略。</param>
    /// <param name="data">回环数据（1 至 960 字节）。</param>
    /// <returns>请求数据。</returns>
    public static byte[] Loopback(McFieldCodec codec, ReadOnlySpan<byte> data)
    {
        using var body = new MemoryStream();
        codec.WriteUInt16(body, McCommands.Loopback);
        codec.WriteUInt16(body, McCommands.LoopbackSubcommand);
        codec.WriteUInt16(body, checked((ushort)data.Length));
        codec.WriteRaw(body, data);
        return body.ToArray();
    }

    private static void WriteBatchHeader(McFieldCodec codec, Stream body, ushort command, ushort subcommand, McDevice device, int number, int points)
    {
        codec.WriteUInt16(body, command);
        codec.WriteUInt16(body, subcommand);
        codec.WriteDevice(body, device, number);
        codec.WriteUInt16(body, checked((ushort)points));
    }
}
