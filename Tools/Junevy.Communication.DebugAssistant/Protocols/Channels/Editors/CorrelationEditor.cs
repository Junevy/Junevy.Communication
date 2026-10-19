using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Common;

namespace Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;

/// <summary>请求与应答的关联方式（计划第 1 节）：顺序、匹配器（每个请求的规则）或键（协议中的序号字段）。</summary>
public sealed partial class CorrelationEditor : ValidatedEditor
{
    [ObservableProperty]
    private CorrelationMode _mode = CorrelationMode.Sequential;

    /// <summary>匹配器的规则：true 为"应答以某段字节开头"，false 为"应答回显请求的一段字节"。</summary>
    [ObservableProperty]
    private bool _matchByPrefix = true;

    [ObservableProperty]
    private string _responsePrefix = "hex:";

    [ObservableProperty]
    [Range(0, 65535, ErrorMessage = "回显偏移必须在 0 到 65535 之间。")]
    private int _echoOffset;

    [ObservableProperty]
    [Range(1, 255, ErrorMessage = "回显长度必须在 1 到 255 之间。")]
    private int _echoLength = 1;

    [ObservableProperty]
    [Range(0, 65535, ErrorMessage = "请求键的偏移必须在 0 到 65535 之间。")]
    private int _requestKeyOffset;

    [ObservableProperty]
    private int _requestKeyWidth = 2;

    [ObservableProperty]
    private KeyByteOrder _requestKeyOrder = KeyByteOrder.BigEndian;

    [ObservableProperty]
    [Range(0, 65535, ErrorMessage = "应答键的偏移必须在 0 到 65535 之间。")]
    private int _responseKeyOffset;

    [ObservableProperty]
    private int _responseKeyWidth = 2;

    [ObservableProperty]
    private KeyByteOrder _responseKeyOrder = KeyByteOrder.BigEndian;

    /// <summary>匹配器规则（<see cref="CorrelationMode.Matcher"/> 时使用，每个请求经 <c>RequestOptions.Matcher</c> 传入）。</summary>
    public ResponseMatchRule ToMatchRule() => MatchByPrefix
        ? new PrefixMatchRule(ByteInput.Parse(ResponsePrefix, EncodingChoice.Utf8))
        : new EchoMatchRule(EchoOffset, EchoLength);

    /// <summary>键提取器（<see cref="CorrelationMode.Keyed"/> 时使用）。</summary>
    public IFrameKeyExtractor ToKeyExtractor() => new ConfigurableKeyExtractor(
        new KeyField(RequestKeyOffset, RequestKeyWidth, RequestKeyOrder),
        new KeyField(ResponseKeyOffset, ResponseKeyWidth, ResponseKeyOrder));
}
