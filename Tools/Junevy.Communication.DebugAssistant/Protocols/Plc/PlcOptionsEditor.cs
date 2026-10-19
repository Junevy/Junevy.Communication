using System.ComponentModel.DataAnnotations;
using CommunityToolkit.Mvvm.ComponentModel;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Protocols.Plc;

/// <summary>
/// PLC 面板的套件选项（计划第 7.1 节）：重试次数、重试间隔、读改写位；以及字序与字符串编码。
/// 字序与编码由协议的选项（<c>DataFormat</c>）决定，未覆盖时使用协议的默认值（界面标出该默认值）。
/// </summary>
public sealed partial class PlcOptionsEditor : ValidatedEditor
{
    public PlcOptionsEditor(WordOrder protocolWordOrder, EncodingChoice protocolEncoding = EncodingChoice.Ascii)
    {
        ProtocolWordOrder = protocolWordOrder;
        ProtocolEncoding = protocolEncoding;
        WordOrder = protocolWordOrder;
        StringEncoding = protocolEncoding;
    }

    /// <summary>协议的默认字序，界面上显示为"协议默认"的说明。</summary>
    public WordOrder ProtocolWordOrder { get; }

    /// <summary>协议的默认字符串编码。</summary>
    public EncodingChoice ProtocolEncoding { get; }

    /// <summary>为真时使用 <see cref="WordOrder"/>，否则使用协议的字序。</summary>
    [ObservableProperty]
    private bool _overrideWordOrder;

    [ObservableProperty]
    private WordOrder _wordOrder;

    /// <summary>为真时使用 <see cref="StringEncoding"/>，否则使用协议的编码。</summary>
    [ObservableProperty]
    private bool _overrideEncoding;

    [ObservableProperty]
    private EncodingChoice _stringEncoding;

    [ObservableProperty]
    [Range(0, 100, ErrorMessage = "重试次数必须在 0 到 100 之间（0 表示不重试）。")]
    private int _retryCount;

    [ObservableProperty]
    [Range(1, 600000, ErrorMessage = "重试间隔必须在 1 到 600000 毫秒之间。")]
    private int _retryIntervalMs = 1000;

    [ObservableProperty]
    private bool _allowReadModifyWriteBits;

    /// <summary>覆盖的数据格式；字序与编码都使用协议默认时返回 null，由协议自行决定。</summary>
    public PlcDataFormat? DataFormatOverride()
    {
        if (!OverrideWordOrder && !OverrideEncoding)
        {
            return null;
        }

        return new PlcDataFormat(
            OverrideWordOrder ? WordOrder : ProtocolWordOrder,
            (OverrideEncoding ? StringEncoding : ProtocolEncoding).ToEncoding());
    }

    /// <summary>把重试与读改写选项写入协议选项（基类的字段）。</summary>
    public void ApplyTo(PlcClientOptions options)
    {
        options.RetryCount = RetryCount;
        options.RetryInterval = RetryIntervalMs;
        options.AllowReadModifyWriteBits = AllowReadModifyWriteBits;
    }
}
