using System.Buffers;

namespace Junevy.Communication.Core.Buffers;

/// <summary>
/// 可增长的字节写入器，实现 <see cref="IBufferWriter{T}"/>（net472 没有 <c>ArrayBufferWriter&lt;T&gt;</c>）。
/// 可选地从 <see cref="ArrayPool{T}"/> 租用数组：池化时 <see cref="Dispose"/> 归还数组，之后 <see cref="WrittenMemory"/> 失效。
/// 释放之后，除 <see cref="Dispose"/> 外的成员都抛出 <see cref="ObjectDisposedException"/>，与池化与否无关。
/// </summary>
public sealed class ByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultInitialCapacity = 256;
    private const int GrowthFactor = 2;

    private readonly ArrayPool<byte>? pool;
    private byte[] buffer;
    private int written;
    private bool disposed;

    /// <summary>创建写入器：初始容量 256 字节，不池化。</summary>
    public ByteBufferWriter()
        : this(DefaultInitialCapacity)
    {
    }

    /// <summary>创建写入器。</summary>
    /// <param name="initialCapacity">初始容量（字节），必须大于 0。</param>
    /// <param name="pool">租用与归还数组所用的池；为 null 时不池化。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> 不大于 0。</exception>
    public ByteBufferWriter(int initialCapacity, ArrayPool<byte>? pool = null)
    {
        if (initialCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialCapacity), initialCapacity, "The initial capacity must be greater than zero.");

        this.pool = pool;
        buffer = Rent(initialCapacity);
    }

    /// <summary>已写入的字节数。</summary>
    public int WrittenCount
    {
        get
        {
            ThrowIfDisposed();
            return written;
        }
    }

    /// <summary>已写入的内容。池化时只在 <see cref="Dispose"/> 之前有效。</summary>
    public ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
            ThrowIfDisposed();
            return new ReadOnlyMemory<byte>(buffer, 0, written);
        }
    }

    /// <summary>已写入的内容。指向内部缓冲，之后的写入可能使其失效。</summary>
    public ReadOnlySpan<byte> WrittenSpan
    {
        get
        {
            ThrowIfDisposed();
            return new ReadOnlySpan<byte>(buffer, 0, written);
        }
    }

    /// <summary>复制出已写入的全部字节。</summary>
    /// <returns>字节数组副本。</returns>
    public byte[] ToArray() => WrittenSpan.ToArray();

    /// <summary>清空已写入的内容，保留缓冲以便复用。</summary>
    public void Clear()
    {
        ThrowIfDisposed();
        written = 0;
    }

    /// <summary>标记已写入的字节数。</summary>
    /// <param name="count">新增的字节数。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> 为负或超过已获取的空间。</exception>
    public void Advance(int count)
    {
        ThrowIfDisposed();
        if (count < 0 || count > buffer.Length - written)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Cannot advance beyond the space returned by GetMemory or GetSpan.");

        written += count;
    }

    /// <summary>获取至少 <paramref name="sizeHint"/> 字节的可写内存（0 表示任意大小，至少一个字节）。</summary>
    /// <param name="sizeHint">期望的最小空间。</param>
    /// <returns>可写内存。</returns>
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return new Memory<byte>(buffer, written, buffer.Length - written);
    }

    /// <summary>获取至少 <paramref name="sizeHint"/> 字节的可写空间（0 表示任意大小，至少一个字节）。</summary>
    /// <param name="sizeHint">期望的最小空间。</param>
    /// <returns>可写空间。</returns>
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return new Span<byte>(buffer, written, buffer.Length - written);
    }

    /// <summary>释放写入器；池化时归还缓冲。可重复调用；之后除 Dispose 外的成员抛出 <see cref="ObjectDisposedException"/>。</summary>
    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        ReturnToPool(buffer);
        buffer = Array.Empty<byte>();
        written = 0;
    }

    // 保证剩余空间至少为 sizeHint（0 时至少 1 字节）。空间不足时租用更大的数组、复制已写入的部分，并归还旧数组。
    private void EnsureCapacity(int sizeHint)
    {
        ThrowIfDisposed();
        int required = written + Math.Max(sizeHint, 1);
        if (required <= buffer.Length)
            return;

        byte[] grown = Rent(Math.Max(buffer.Length * GrowthFactor, required));
        Buffer.BlockCopy(buffer, 0, grown, 0, written);
        ReturnToPool(buffer);
        buffer = grown;
    }

    private byte[] Rent(int minimumLength) => pool?.Rent(minimumLength) ?? new byte[minimumLength];

    private void ReturnToPool(byte[] array) => pool?.Return(array);

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(ByteBufferWriter));
    }
}
