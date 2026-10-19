using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Diagnostics;
using Junevy.Communication.DebugAssistant.Common;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>分帧编辑器（6 种模式，每种只显示自己的参数）。映射到 <see cref="FramingOptions"/>。</summary>
public sealed partial class FramingEditor : ValidatedEditor
{
    [ObservableProperty]
    private FramingMode _mode = FramingMode.Raw;

    [ObservableProperty]
    [Range(1, FramingOptions.DefaultMaxFrameLength * 16, ErrorMessage = "最大帧长必须在 1 到 1048576 之间。")]
    private int _maxFrameLength = FramingOptions.DefaultMaxFrameLength;

    /// <summary>分隔符，每行一个，使用 <c>hex:</c> 或文本语法（与库一致）。</summary>
    [ObservableProperty]
    private string _delimiters = "hex:0D 0A";

    [ObservableProperty]
    private bool _keepDelimiter;

    [ObservableProperty]
    private bool _appendDelimiterOnSend = true;

    [ObservableProperty]
    [Range(1, 65536, ErrorMessage = "定长的长度必须在 1 到 65536 之间。")]
    private int _fixedLength = 8;

    [ObservableProperty]
    [Range(0, 65535, ErrorMessage = "长度字段的偏移必须在 0 到 65535 之间。")]
    private int _lengthFieldOffset;

    [ObservableProperty]
    [Range(1, 8, ErrorMessage = "长度字段的宽度必须在 1 到 8 之间（二进制只能是 1、2、4）。")]
    private int _lengthFieldSize = 2;

    [ObservableProperty]
    private LengthFieldEncoding _lengthFieldEncoding = LengthFieldEncoding.BinaryBigEndian;

    [ObservableProperty]
    private int _lengthAdjustment;

    [ObservableProperty]
    [Range(0, 65535, ErrorMessage = "剥离的字节数必须在 0 到 65535 之间。")]
    private int _initialBytesToStrip;

    [ObservableProperty]
    private string _startMarker = "hex:02";

    [ObservableProperty]
    private string _endMarker = "hex:03";

    [ObservableProperty]
    private bool _keepMarkers = true;

    [ObservableProperty]
    [Range(1, 60000, ErrorMessage = "空闲间隔必须在 1 到 60000 毫秒之间。")]
    private int _gapTimeout = 20;

    /// <summary>生成库的分帧选项。调用前应确认 <see cref="ObservableValidator.HasErrors"/> 为 false。</summary>
    public FramingOptions ToOptions()
    {
        return new FramingOptions
        {
            Mode = Mode,
            MaxFrameLength = MaxFrameLength,
            Delimiters = Mode == FramingMode.Delimiter ? SplitDelimiters(Delimiters) : null,
            KeepDelimiter = KeepDelimiter,
            AppendDelimiterOnSend = AppendDelimiterOnSend,
            FrameLength = FixedLength,
            LengthFieldOffset = LengthFieldOffset,
            LengthFieldSize = LengthFieldSize,
            LengthFieldEncoding = LengthFieldEncoding,
            LengthAdjustment = LengthAdjustment,
            InitialBytesToStrip = InitialBytesToStrip,
            StartMarker = Mode == FramingMode.StartEnd ? StartMarker : null,
            EndMarker = Mode == FramingMode.StartEnd ? EndMarker : null,
            KeepMarkers = KeepMarkers,
            GapTimeout = GapTimeout,
        };
    }

    /// <summary>预览的输入：一段字节（<c>hex:</c> 或文本语法），用当前的分帧设置切分。</summary>
    [ObservableProperty]
    private string _previewInput = "hex:";

    /// <summary>预览的结果文字：每帧一行（十六进制），或说明。</summary>
    [ObservableProperty]
    private string _previewText = string.Empty;

    /// <summary>用当前设置切分预览输入（不连接，不发送）。</summary>
    [RelayCommand]
    private void Preview()
    {
        try
        {
            FramePreview.Result result = FramePreview.Decode(ToOptions(), ByteInput.Parse(PreviewInput, EncodingChoice.Utf8));
            var lines = result.Frames.Select((frame, index) => $"帧 {index + 1}（{frame.Length} 字节）: {HexFormatter.ToHex(frame)}").ToList();
            if (result.Notice is not null)
            {
                lines.Add(result.Notice);
            }

            PreviewText = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            PreviewText = ex.Message;
        }
    }

    /// <summary>把分隔符文本拆成每行一个，忽略空行。</summary>
    internal static string[] SplitDelimiters(string text) => text
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
