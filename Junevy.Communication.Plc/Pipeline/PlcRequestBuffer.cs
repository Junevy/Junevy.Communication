using System.Buffers;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 单包请求的写入缓冲区（net472 没有 <c>ArrayBufferWriter&lt;T&gt;</c>）。每包使用一个新实例，因此不需要同步。
/// </summary>
internal sealed class PlcRequestBuffer : IBufferWriter<byte>
{
    private const int InitialCapacity = 256;

    private byte[] buffer = new byte[InitialCapacity];
    private int written;

    /// <summary>已写入的请求字节。</summary>
    public ReadOnlyMemory<byte> WrittenMemory => buffer.AsMemory(0, written);

    /// <inheritdoc />
    public void Advance(int count)
    {
        if (count < 0 || count > buffer.Length - written)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Cannot advance beyond the space returned by GetMemory or GetSpan.");

        written += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureSpace(sizeHint);
        return buffer.AsMemory(written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureSpace(sizeHint);
        return buffer.AsSpan(written);
    }

    private void EnsureSpace(int sizeHint)
    {
        // 与 IBufferWriter 的约定一致：sizeHint 为 0 时至少返回一个字节的空间。
        int required = written + Math.Max(sizeHint, 1);
        if (required > buffer.Length)
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, required));
    }
}
