using System.Buffers;

namespace Junevy.Communication.Channels.Pipeline;

/// <summary>
/// 基于 <see cref="ArrayPool{T}"/> 的 <see cref="IBufferWriter{T}"/>（计划 8.1）。
/// 替代 net472 上不存在的 <c>ArrayBufferWriter&lt;T&gt;</c>，供 <see cref="StreamChannel"/> 编码发送负载。
/// 使用后必须 <see cref="Dispose"/> 归还缓冲；<see cref="WrittenMemory"/> 只在归还之前有效。
/// </summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int DefaultCapacity = 256;

    private byte[]? buffer;
    private int written;

    /// <summary>创建写入器，初始容量为 256 字节（按需增长）。</summary>
    public PooledBufferWriter()
    {
        buffer = ArrayPool<byte>.Shared.Rent(DefaultCapacity);
    }

    /// <summary>已写入的字节数。</summary>
    public int WrittenCount => written;

    /// <summary>已写入的内容（指向池化缓冲，归还之后失效）。</summary>
    public ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
            byte[] current = CurrentBuffer();
            return new ReadOnlyMemory<byte>(current, 0, written);
        }
    }

    /// <inheritdoc />
    public void Advance(int count)
    {
        byte[] current = CurrentBuffer();
        if (count < 0 || count > current.Length - written)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Cannot advance beyond the space returned by GetMemory or GetSpan.");

        written += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        byte[] current = CurrentBuffer();
        return new Memory<byte>(current, written, current.Length - written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        byte[] current = CurrentBuffer();
        return new Span<byte>(current, written, current.Length - written);
    }

    /// <summary>清空已写入的内容，保留缓冲以便复用。</summary>
    public void Reset() => written = 0;

    /// <summary>归还池化缓冲。可重复调用；之后不能再写入。</summary>
    public void Dispose()
    {
        byte[]? toReturn = buffer;
        buffer = null;
        written = 0;
        if (toReturn != null)
            ArrayPool<byte>.Shared.Return(toReturn);
    }

    // 获取当前缓冲；归还之后访问视为编程错误。
    private byte[] CurrentBuffer()
        => buffer ?? throw new ObjectDisposedException(nameof(PooledBufferWriter));

    // 保证剩余空间至少为 sizeHint（0 时至少 1 字节），不足时换用更大的池化数组并复制已写入部分。
    private void EnsureCapacity(int sizeHint)
    {
        byte[] current = CurrentBuffer();
        int required = written + Math.Max(sizeHint, 1);
        if (required <= current.Length)
            return;

        int newSize = Math.Max(current.Length * 2, required);
        byte[] grown = ArrayPool<byte>.Shared.Rent(newSize);
        Buffer.BlockCopy(current, 0, grown, 0, written);
        ArrayPool<byte>.Shared.Return(current);
        buffer = grown;
    }
}
