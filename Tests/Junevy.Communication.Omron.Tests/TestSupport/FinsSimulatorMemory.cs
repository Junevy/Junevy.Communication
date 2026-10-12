using System.Buffers.Binary;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// FINS 设备侧的存储与它的大端字节布局（P46-D6）：物理存储与越界判定由共享库的 <see cref="PlcByteMemory"/> 承担，
/// 这里只做 FINS 特有的换算——字按大端占 2 个字节，位号 0–7 落在第二个字节、8–15 落在第一个字节。
/// </summary>
/// <remarks>
/// 每个软元件符号一个块，块长 = <c>words</c> × 2 字节。字代码与位代码落到同一个块，因此原生位写之后字读能看见（P4-D6）。
/// </remarks>
internal sealed class FinsSimulatorMemory
{
    // 共享库按（区名，块号）寻址：区名是软元件符号，块号恒为 0。
    private const int SingleBlock = 0;
    private const int BitsPerWord = 16;
    private const int BitsPerByte = 8;

    private readonly PlcByteMemory storage = new PlcByteMemory();

    /// <summary>按容量（字数）建出每个软元件符号的块。</summary>
    /// <param name="words">每个软元件可用的字数。</param>
    /// <param name="symbols">软元件符号。</param>
    public FinsSimulatorMemory(int words, IEnumerable<string> symbols)
    {
        foreach (string symbol in symbols.Distinct())
            storage.Define(symbol, SingleBlock, words * FinsMemoryArea.WordBytes);
    }

    /// <summary>预置字：用例用它准备设备侧的数据。</summary>
    /// <param name="symbol">软元件符号。</param>
    /// <param name="word">起始字号。</param>
    /// <param name="values">要写入的字，按大端落进字节存储。</param>
    public void SetWords(string symbol, int word, params ushort[] values)
    {
        var bytes = new byte[values.Length * FinsMemoryArea.WordBytes];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * FinsMemoryArea.WordBytes, FinsMemoryArea.WordBytes), values[i]);

        storage.TryWrite(symbol, SingleBlock, word * FinsMemoryArea.WordBytes, bytes);
    }

    /// <summary>读回字：用例用它核对设备真正收到的值；越出块尾的部分按 0 读出。</summary>
    /// <param name="symbol">软元件符号。</param>
    /// <param name="word">起始字号。</param>
    /// <param name="count">字数。</param>
    /// <returns>读到的字。</returns>
    public ushort[] GetWords(string symbol, int word, int count)
    {
        var result = new ushort[count];
        int length = count * FinsMemoryArea.WordBytes;
        storage.TryRead(symbol, SingleBlock, word * FinsMemoryArea.WordBytes, length, out byte[] raw);
        for (int i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(i * FinsMemoryArea.WordBytes, FinsMemoryArea.WordBytes));

        return result;
    }

    /// <summary>读一段字的原始大端字节（回环负载），越界时为空。</summary>
    /// <param name="symbol">软元件符号。</param>
    /// <param name="word">起始字号。</param>
    /// <param name="count">字数。</param>
    /// <returns>内存里的字节，按地址顺序。</returns>
    public byte[] ReadWordBytes(string symbol, int word, int count)
    {
        storage.TryRead(symbol, SingleBlock, word * FinsMemoryArea.WordBytes, count * FinsMemoryArea.WordBytes, out byte[] raw);
        return raw;
    }

    /// <summary>把请求里的大端字负载写进内存。</summary>
    /// <param name="symbol">软元件符号。</param>
    /// <param name="word">起始字号。</param>
    /// <param name="payload">大端字负载，长度应为 <paramref name="word"/> 个字。</param>
    public void WriteWordBytes(string symbol, int word, ReadOnlySpan<byte> payload)
        => storage.TryWrite(symbol, SingleBlock, word * FinsMemoryArea.WordBytes, payload);

    /// <summary>绝对位是否置起。</summary>
    /// <param name="symbol">软元件符号。</param>
    /// <param name="absoluteBit">绝对位号（字号 × 16 + 位号）。</param>
    /// <returns>该位是否置起。</returns>
    public bool IsPointSet(string symbol, int absoluteBit)
    {
        storage.TryRead(symbol, SingleBlock, ByteOf(absoluteBit), 1, out byte[] cell);
        return (cell[0] & (1 << BitInByte(absoluteBit))) != 0;
    }

    /// <summary>置起或清掉一个绝对位，只改它所在的那个字节。</summary>
    /// <param name="symbol">软元件符号。</param>
    /// <param name="absoluteBit">绝对位号。</param>
    /// <param name="value">true 置起、false 清掉。</param>
    public void SetPoint(string symbol, int absoluteBit, bool value)
    {
        int offset = ByteOf(absoluteBit);
        storage.TryRead(symbol, SingleBlock, offset, 1, out byte[] cell);

        int mask = 1 << BitInByte(absoluteBit);
        byte updated = value ? (byte)(cell[0] | mask) : (byte)(cell[0] & ~mask);
        storage.TryWrite(symbol, SingleBlock, offset, new[] { updated });
    }

    /// <summary>检查一次读写请求是否落在块内（替身回复越界结束码 11 03 的依据）。</summary>
    /// <param name="symbol">软元件符号。</param>
    /// <param name="bits">true 是位访问（数量按点计），false 是字访问（数量按字计）。</param>
    /// <param name="word">起始字号。</param>
    /// <param name="bitNumber">位访问的起始位号（0–15）；字访问忽略。</param>
    /// <param name="count">数量，按访问类型计。</param>
    /// <returns>落在块内返回 true。</returns>
    public bool FitsRequest(string symbol, bool bits, int word, int bitNumber, int count)
    {
        int firstByte = word * FinsMemoryArea.WordBytes;

        // 位访问只触及最后一个点的 1 个字节，跨过 16 位的点因此自动落到下一字上；字访问覆盖 count × 2 个字节。
        int lastByte = bits
            ? ByteOf((word * BitsPerWord) + bitNumber + count - 1)
            : firstByte + (count * FinsMemoryArea.WordBytes) - 1;

        return storage.TryRead(symbol, SingleBlock, firstByte, lastByte - firstByte + 1, out _) == PlcByteAccess.Ok;
    }

    // 位号 0–7 在大端字的第二个字节，8–15 在第一个字节。
    private static int ByteOf(int absoluteBit)
    {
        int word = absoluteBit / BitsPerWord;
        int bit = absoluteBit % BitsPerWord;
        return (word * FinsMemoryArea.WordBytes) + (bit < BitsPerByte ? 1 : 0);
    }

    // 掩码要用**字节内**的位号（% 8）：字节只装得下 8 位，(byte)(1 << 14) 会截断成 0。
    private static int BitInByte(int absoluteBit) => absoluteBit % BitsPerByte;
}