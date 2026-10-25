using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

public sealed class FramePreviewTests
{
    [Fact]
    public void Delimiter_SplitsTheInputIntoFrames()
    {
        var options = new FramingOptions { Mode = FramingMode.Delimiter, Delimiters = ["\r\n"] };

        FramePreview.Result result = FramePreview.Decode(options, [0x41, 0x0D, 0x0A, 0x42, 0x43, 0x0D, 0x0A]);

        Assert.Equal(new[] { new byte[] { 0x41 }, new byte[] { 0x42, 0x43 } }, result.Frames);
        Assert.Equal(0, result.LeftoverBytes);
        Assert.Null(result.Notice);
    }

    [Fact]
    public void FixedLength_LeavesAnIncompleteFrameAsLeftover()
    {
        var options = new FramingOptions { Mode = FramingMode.FixedLength, FrameLength = 2 };

        FramePreview.Result result = FramePreview.Decode(options, [0x01, 0x02, 0x03]);

        Assert.Single(result.Frames);
        Assert.Equal(1, result.LeftoverBytes);
        Assert.NotNull(result.Notice);
    }

    [Fact]
    public void LengthField_UsesTheHeaderToCutFrames()
    {
        var options = new FramingOptions { Mode = FramingMode.LengthField, LengthFieldOffset = 0, LengthFieldSize = 1 };

        FramePreview.Result result = FramePreview.Decode(options, [0x02, 0xAA, 0xBB, 0x01, 0xCC]);

        Assert.Equal(new[] { new byte[] { 0x02, 0xAA, 0xBB }, new byte[] { 0x01, 0xCC } }, result.Frames);
    }

    [Fact]
    public void StartEnd_KeepsTheMarkersWhenAsked()
    {
        var options = new FramingOptions { Mode = FramingMode.StartEnd, StartMarker = "hex:02", EndMarker = "hex:03", KeepMarkers = true };

        FramePreview.Result result = FramePreview.Decode(options, [0x00, 0x02, 0x41, 0x03]);

        Assert.Equal(new byte[] { 0x02, 0x41, 0x03 }, Assert.Single(result.Frames));
    }

    [Fact]
    public void IdleGap_CannotBePreviewed_AndSaysSo()
    {
        FramePreview.Result result = FramePreview.Decode(new FramingOptions { Mode = FramingMode.IdleGap }, [0x01]);

        Assert.Empty(result.Frames);
        Assert.NotNull(result.Notice);
    }

    [Fact]
    public void LibraryRejectsAnIllegalCombination_AndThePreviewShowsItsMessage()
    {
        // 二进制长度字段的宽度只能是 1、2、4（计划 R11）：库抛出 ArgumentException，预览显示它的消息。
        var options = new FramingOptions { Mode = FramingMode.LengthField, LengthFieldSize = 3 };

        FramePreview.Result result = FramePreview.Decode(options, [0x00]);

        Assert.Empty(result.Frames);
        Assert.False(string.IsNullOrWhiteSpace(result.Notice));
    }

    [Fact]
    public void FramingEditor_PreviewCommand_ShowsEachFrameAsHex()
    {
        var editor = new FramingEditor { Mode = FramingMode.Delimiter, Delimiters = "hex:0D 0A", PreviewInput = "hex:41 0D 0A 42" };

        editor.PreviewCommand.Execute(null);

        Assert.Equal("帧 1（1 字节）: 41", editor.PreviewText.Split(Environment.NewLine)[0]);
    }

    [Fact]
    public void FramingEditor_PreviewCommand_ShowsAFormatErrorInsteadOfThrowing()
    {
        var editor = new FramingEditor { Mode = FramingMode.Raw, PreviewInput = "hex:0" };

        editor.PreviewCommand.Execute(null);

        Assert.False(string.IsNullOrWhiteSpace(editor.PreviewText));
    }
}
