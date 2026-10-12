namespace Junevy.Communication.Testing;

/// <summary>按字节访问一块存储的结果（<see cref="PlcByteMemory"/>）。</summary>
public enum PlcByteAccess
{
    /// <summary>访问成功。</summary>
    Ok,

    /// <summary>该（区，块）没有定义。</summary>
    NoSuchBlock,

    /// <summary>访问越过块的末尾。</summary>
    BeyondEnd,
}

/// <summary>
/// 按（区名，块号）组织的字节存储，线程安全。给设备替身用：替身只做协议翻译，块的存在性与越界判定交给这里。
/// </summary>
/// <remarks>
/// <para><see cref="Define"/> 返回的数组就是内部存储本身——用例可以直接预置内容，但必须在替身开始应答**之前**完成；
/// 用例核对内容也要在操作结束**之后**进行，否则读到的可能是半成品。跨线程读写请用 <see cref="TryRead"/> / <see cref="TryWrite"/>。</para>
/// <para>区名区分大小写，块号由协议自己的语义决定（可以只是 0，也可以是数据块号）。</para>
/// </remarks>
public sealed class PlcByteMemory
{
    private readonly object gate = new object();
    private readonly Dictionary<(string Area, int Block), byte[]> blocks = new Dictionary<(string, int), byte[]>();

    /// <summary>定义一个新块（内容全 0）并返回它的存储本身。</summary>
    /// <param name="area">区名，区分大小写。</param>
    /// <param name="block">块号。</param>
    /// <param name="length">块长（字节），必须大于 0。</param>
    /// <returns>该块的存储；预置内容直接写它即可。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="length"/> 不大于 0。</exception>
    /// <exception cref="InvalidOperationException">同一（区，块）已经定义过。</exception>
    public byte[] Define(string area, int block, int length)
    {
        if (length < 1)
            throw new ArgumentOutOfRangeException(nameof(length), length, "The block length must be greater than zero.");

        lock (gate)
        {
            var key = (RequireArea(area), block);
            if (blocks.ContainsKey(key))
                throw new InvalidOperationException($"The block '{key.Item1}' {key.Item2} is already defined.");

            var buffer = new byte[length];
            blocks.Add(key, buffer);
            return buffer;
        }
    }

    /// <summary>取已定义块的存储本身。</summary>
    /// <param name="area">区名，区分大小写。</param>
    /// <param name="block">块号。</param>
    /// <returns>该块的存储。</returns>
    /// <exception cref="KeyNotFoundException">该（区，块）没有定义。</exception>
    public byte[] Block(string area, int block)
    {
        lock (gate)
            return blocks[(RequireArea(area), block)];
    }

    /// <summary>读取块内的一段。</summary>
    /// <param name="area">区名，区分大小写。</param>
    /// <param name="block">块号。</param>
    /// <param name="offset">块内起始偏移（字节）。</param>
    /// <param name="count">读取长度（字节）。</param>
    /// <param name="data">读到的字节；失败时为空数组。</param>
    /// <returns>成功为 <see cref="PlcByteAccess.Ok"/>；块不存在或越过末尾时为对应值，此时块不变。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> 或 <paramref name="count"/> 为负数。</exception>
    public PlcByteAccess TryRead(string area, int block, int offset, int count, out byte[] data)
    {
        ValidateRange(offset, count);

        lock (gate)
        {
            if (!blocks.TryGetValue((RequireArea(area), block), out byte[]? buffer))
            {
                data = Array.Empty<byte>();
                return PlcByteAccess.NoSuchBlock;
            }

            if (!Fits(buffer.Length, offset, count))
            {
                data = Array.Empty<byte>();
                return PlcByteAccess.BeyondEnd;
            }

            data = new byte[count];
            Array.Copy(buffer, offset, data, 0, count);
            return PlcByteAccess.Ok;
        }
    }

    /// <summary>写入块内的一段。</summary>
    /// <param name="area">区名，区分大小写。</param>
    /// <param name="block">块号。</param>
    /// <param name="offset">块内起始偏移（字节）。</param>
    /// <param name="data">要写入的字节。</param>
    /// <returns>成功为 <see cref="PlcByteAccess.Ok"/>；块不存在或越过末尾时为对应值，此时**块不变**（不写入任何字节）。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> 为负数。</exception>
    public PlcByteAccess TryWrite(string area, int block, int offset, ReadOnlySpan<byte> data)
    {
        ValidateRange(offset, data.Length);

        lock (gate)
        {
            if (!blocks.TryGetValue((RequireArea(area), block), out byte[]? buffer))
                return PlcByteAccess.NoSuchBlock;

            if (!Fits(buffer.Length, offset, data.Length))
                return PlcByteAccess.BeyondEnd;

            data.CopyTo(buffer.AsSpan(offset, data.Length));
            return PlcByteAccess.Ok;
        }
    }

    private static bool Fits(int blockLength, int offset, int count)
        => offset <= blockLength && count <= blockLength - offset;

    private static void ValidateRange(int offset, int count)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "The offset must not be negative.");
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "The count must not be negative.");
    }

    private static string RequireArea(string area)
        => area ?? throw new ArgumentNullException(nameof(area));
}