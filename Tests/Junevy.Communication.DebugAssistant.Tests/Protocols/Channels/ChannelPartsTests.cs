using Junevy.Communication.DebugAssistant.Protocols.Channels;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

/// <summary>
/// DA-D12 的匹配器与键提取器（计划 Task 4 第 3 条）：边界条件（偏移越界、长度不足）返回不匹配或失败，从不抛出异常。
/// 握手钩子由 <see cref="HandshakeLoopbackTests"/> 在真实的回环连接上验证。
/// </summary>
public sealed class ChannelPartsTests
{
    // ———— 匹配器 ————

    [Fact]
    public void PrefixRule_MatchesOnlyFramesThatStartWithThePrefix()
    {
        var matcher = new ConfigurableResponseMatcher(new PrefixMatchRule([0x02, 0x41]));

        Assert.True(matcher.IsMatch([0x01], [0x02, 0x41, 0x00]));
        Assert.False(matcher.IsMatch([0x01], [0x02, 0x42]));
        Assert.False(matcher.IsMatch([0x01], [0x02]));
    }

    [Fact]
    public void EchoRule_MatchesWhenTheFrameRepeatsTheRequestBytes()
    {
        var matcher = new ConfigurableResponseMatcher(new EchoMatchRule(Offset: 1, Length: 2));

        Assert.True(matcher.IsMatch([0x10, 0xAA, 0xBB], [0x00, 0xAA, 0xBB, 0x99]));
        Assert.False(matcher.IsMatch([0x10, 0xAA, 0xBB], [0x00, 0xAA, 0xBC]));
    }

    [Fact]
    public void EchoRule_OutOfRange_IsNoMatch_AndDoesNotThrow()
    {
        var matcher = new ConfigurableResponseMatcher(new EchoMatchRule(Offset: 4, Length: 2));

        Assert.False(matcher.IsMatch([0x01, 0x02, 0x03, 0x04, 0x05, 0x06], [0x01, 0x02]));
        Assert.False(matcher.IsMatch([0x01], [0x01, 0x02, 0x03, 0x04, 0x05, 0x06]));
    }

    [Fact]
    public void Matcher_RejectsAnEmptyPrefixAndANonPositiveEchoLength()
    {
        Assert.Throws<ArgumentException>(() => new ConfigurableResponseMatcher(new PrefixMatchRule([])));
        Assert.Throws<ArgumentException>(() => new ConfigurableResponseMatcher(new EchoMatchRule(0, 0)));
        Assert.Throws<ArgumentException>(() => new ConfigurableResponseMatcher(new EchoMatchRule(-1, 1)));
    }

    // ———— 键提取器 ————

    [Theory]
    [InlineData(KeyByteOrder.BigEndian, new byte[] { 0x01, 0x02 }, 0x0102)]
    [InlineData(KeyByteOrder.LittleEndian, new byte[] { 0x01, 0x02 }, 0x0201)]
    public void Width2_ReadsTheKeyInTheRequestedByteOrder(KeyByteOrder order, byte[] frame, long expected)
    {
        var extractor = new ConfigurableKeyExtractor(new KeyField(0, 2, order), new KeyField(0, 2, order));

        Assert.True(extractor.TryGetResponseKey(frame, out long key));
        Assert.Equal(expected, key);
    }

    [Fact]
    public void Width1_And4_ReadTheKeyAtTheOffset()
    {
        var extractor = new ConfigurableKeyExtractor(
            new KeyField(2, 1, KeyByteOrder.BigEndian),
            new KeyField(1, 4, KeyByteOrder.BigEndian));

        Assert.True(extractor.TryGetRequestKey([0x00, 0x00, 0x7F], out long single));
        Assert.Equal(0x7F, single);
        Assert.True(extractor.TryGetResponseKey([0x00, 0x00, 0x00, 0x00, 0x2A], out long four));
        Assert.Equal(0x0000002A, four);
    }

    [Fact]
    public void KeyTooShort_ReturnsFalse_AndDoesNotThrow()
    {
        var extractor = new ConfigurableKeyExtractor(new KeyField(3, 2, KeyByteOrder.BigEndian), new KeyField(0, 1, KeyByteOrder.BigEndian));

        Assert.False(extractor.TryGetRequestKey([0x01, 0x02, 0x03], out _));
        Assert.False(extractor.TryGetResponseKey([], out _));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 2)]
    public void KeyField_RejectsAnInvalidWidthOrOffset(int offset, int width)
    {
        Assert.Throws<ArgumentException>(() => new ConfigurableKeyExtractor(
            new KeyField(offset, width, KeyByteOrder.BigEndian),
            new KeyField(0, 1, KeyByteOrder.BigEndian)));
    }
}
