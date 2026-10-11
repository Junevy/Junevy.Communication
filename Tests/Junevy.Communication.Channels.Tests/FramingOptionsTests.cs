namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 分帧配置的默认帧长常量与 <see cref="FramingOptions.MaxFrameLength"/> 的默认值一致（P25-D8）。
/// </summary>
public sealed class FramingOptionsTests
{
    [Fact]
    public void DefaultMaxFrameLength_EqualsTheDefaultOfMaxFrameLength()
    {
        Assert.Equal(new FramingOptions().MaxFrameLength, FramingOptions.DefaultMaxFrameLength);
    }
}
