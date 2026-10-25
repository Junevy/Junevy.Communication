using System.Text;
using Junevy.Communication.DebugAssistant.Common;

namespace Junevy.Communication.DebugAssistant.Tests.Common;

public sealed class ByteInputTests
{
    static ByteInputTests()
    {
        // GBK 需要代码页提供程序（应用启动时注册，测试同样注册）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Theory]
    [InlineData("hex:0D 0A", new byte[] { 0x0D, 0x0A })]
    [InlineData("hex:0D-0A", new byte[] { 0x0D, 0x0A })]
    [InlineData("hex:0d0a", new byte[] { 0x0D, 0x0A })]
    public void HexPrefix_IsParsedByTheLibrary_InEveryEncoding(string text, byte[] expected)
    {
        Assert.Equal(expected, ByteInput.Parse(text, EncodingChoice.Gbk));
        Assert.Equal(expected, ByteInput.Parse(text, EncodingChoice.Ascii));
    }

    [Theory]
    [InlineData(@"A\r\n", new byte[] { 0x41, 0x0D, 0x0A })]
    [InlineData(@"\t\0\\", new byte[] { 0x09, 0x00, 0x5C })]
    [InlineData(@"\x7F\xAB", new byte[] { 0x7F, 0xAB })]
    public void TextEscapes_AreTheSameAsTheLibrary_InAsciiAndUtf8(string text, byte[] expected)
    {
        Assert.Equal(expected, ByteInput.Parse(text, EncodingChoice.Ascii));
        Assert.Equal(expected, ByteInput.Parse(text, EncodingChoice.Utf8));
    }

    [Fact]
    public void Gbk_EncodesChineseCharacters_WithTheGbkCodePage()
    {
        // "中" 的 GBK 编码为 D6 D0。
        Assert.Equal(new byte[] { 0xD6, 0xD0 }, ByteInput.Parse("中", EncodingChoice.Gbk));
    }

    [Fact]
    public void Utf8_EncodesChineseCharacters_AsUtf8()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("中"), ByteInput.Parse("中", EncodingChoice.Utf8));
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"abc\")]
    [InlineData(@"\q")]
    [InlineData(@"\x4")]
    [InlineData("hex:0")]
    [InlineData("hex:ZZ")]
    public void MalformedInput_ThrowsFormatException(string text)
    {
        Assert.Throws<FormatException>(() => ByteInput.Parse(text, EncodingChoice.Ascii));
    }
}
