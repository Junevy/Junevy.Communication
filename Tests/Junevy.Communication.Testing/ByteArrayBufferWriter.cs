using System.Buffers;

namespace Junevy.Communication.Testing;

/// <summary>
/// 基于数组的 <see cref="IBufferWriter{T}"/>，用于在测试中收集编码器的输出（net472 没有 <c>ArrayBufferWriter&lt;T&gt;</c>）。
/// </summary>
public sealed class ByteArrayBufferWriter : IBufferWriter<byte>
{
    private byte[] buffer = new byte[256];
    private int count;

    /// <summary>已写入的字节数。</summary>
    public int Count => count;

    /// <summary>标记已写入的字节数。</summary>
    /// <param name="count">新增的字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> 为负或超过已获取的空间。</exception>
    public void Advance(int count)
    {
        if (count < 0 || count > buffer.Length - this.count)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Cannot advance beyond the space returned by GetMemory or GetSpan.");

        this.count += count;
    }

    /// <summary>获取至少 <paramref name="sizeHint"/> 字节的可写内存（0 表示任意大小，至少一个字节）。</summary>
    /// <param name="sizeHint">期望的最小空间。</param>
    /// <returns>可写内存。</returns>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return new Memory<byte>(buffer, count, buffer.Length - count);
    }

    /// <summary>获取至少 <paramref name="sizeHint"/> 字节的可写空间（0 表示任意大小，至少一个字节）。</summary>
    /// <param name="sizeHint">期望的最小空间。</param>
    /// <returns>可写空间。</returns>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return new Span<byte>(buffer, count, buffer.Length - count);
    }

    /// <summary>复制出已写入的全部字节。</summary>
    /// <returns>字节数组副本。</returns>
    public byte[] ToArray()
    {
        var result = new byte[count];
        Array.Copy(buffer, result, count);
        return result;
    }

    private void EnsureCapacity(int sizeHint)
    {
        int required = count + Math.Max(sizeHint, 1);
        if (required <= buffer.Length)
            return;

        Array.Resize(ref buffer, Math.Max(buffer.Length * 2, required));
    }
}
