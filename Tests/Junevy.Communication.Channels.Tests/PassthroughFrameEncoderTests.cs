using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Buffers;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 公开的透传编码器：原样输出负载，空负载不写入任何字节（计划 Task 4）。
/// </summary>
public sealed class PassthroughFrameEncoderTests
{
    [Fact]
    public void Encode_WritesThePayloadUnchanged()
    {
        using var output = new ByteBufferWriter();

        PassthroughFrameEncoder.Instance.Encode(new byte[] { 0x10, 0x02, 0x41 }, output);

        Assert.Equal(new byte[] { 0x10, 0x02, 0x41 }, output.ToArray());
    }

    [Fact]
    public void Encode_EmptyPayload_WritesNothing()
    {
        using var output = new ByteBufferWriter();

        PassthroughFrameEncoder.Instance.Encode(ReadOnlySpan<byte>.Empty, output);

        Assert.Equal(0, output.WrittenCount);
    }
}
