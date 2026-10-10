using Junevy.Communication.Core.Diagnostics;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="HexFormatter"/> 的格式与截断测试。
/// </summary>
public sealed class HexFormatterTests
{
    [Fact]
    public void Format_Truncates()
    {
        var data = new byte[300];
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)i;

        Assert.Equal("00-01-02-03...(+296 bytes)", HexFormatter.ToHex(data, maxBytes: 4));
    }

    [Fact]
    public void Format_JoinsWithDashes_WithoutTruncation()
    {
        byte[] data = { 0xAA, 0xBB, 0xCC };

        Assert.Equal("AA-BB-CC", HexFormatter.ToHex(data));
        Assert.Equal("AA-BB-CC", HexFormatter.ToHex(data, maxBytes: 3));
        Assert.Equal(string.Empty, HexFormatter.ToHex(ReadOnlySpan<byte>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => HexFormatter.ToHex(data, maxBytes: -1));
    }
}
