using System.Buffers;

namespace Junevy.Communication.Channels.Framing;

/// <summary>
/// Delimiter 编码器：负载原样写出，并在末尾追加 <c>suffix</c>（为 null 时不追加，即原样发送）。
/// </summary>
internal sealed class DelimiterFrameEncoder : IFrameEncoder
{
    private readonly byte[]? suffix;

    /// <param name="suffix">发送时追加的字节（通常是 <c>Delimiters[0]</c>）；为 null 时不追加。</param>
    public DelimiterFrameEncoder(byte[]? suffix)
    {
        this.suffix = suffix;
    }

    public void Encode(ReadOnlySpan<byte> payload, IBufferWriter<byte> output)
    {
        byte[]? delimiter = suffix;
        int total = payload.Length + (delimiter?.Length ?? 0);
        if (total == 0)
            return;

        Span<byte> destination = output.GetSpan(total);
        payload.CopyTo(destination);
        if (delimiter != null)
            new ReadOnlySpan<byte>(delimiter).CopyTo(destination.Slice(payload.Length));

        output.Advance(total);
    }
}
