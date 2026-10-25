using Junevy.Communication.Core.Buffers;
using Junevy.Communication.DebugAssistant.Common;
using Junevy.Communication.DebugAssistant.Protocols.Plc;
using Junevy.Communication.Plc;
using Junevy.Communication.Serial;
using Junevy.Communication.Tcp;
using Junevy.Communication.Udp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Plc;

/// <summary>套件选项的映射与校验，以及 PLC 页签使用的传输创建器（计划 Task 7 第 2、3 条的测试接缝）。</summary>
public sealed class PlcOptionsAndTransportTests
{
    [Fact]
    public void ApplyTo_WritesRetryAndReadModifyWriteOptions()
    {
        var editor = new PlcOptionsEditor(WordOrder.DCBA)
        {
            RetryCount = 4,
            RetryIntervalMs = 250,
            AllowReadModifyWriteBits = true,
        };
        var options = new PlcClientOptions();

        editor.ApplyTo(options);

        Assert.Equal((4, 250, true), (options.RetryCount, options.RetryInterval, options.AllowReadModifyWriteBits));
    }

    [Fact]
    public void DataFormat_IsLeftToTheProtocol_UntilAFieldIsOverridden()
    {
        var editor = new PlcOptionsEditor(WordOrder.CDAB, EncodingChoice.Ascii);
        Assert.Null(editor.DataFormatOverride());

        editor.OverrideWordOrder = true;
        editor.WordOrder = WordOrder.ABCD;
        PlcDataFormat? format = editor.DataFormatOverride();

        Assert.NotNull(format);
        Assert.Equal(WordOrder.ABCD, format!.WordOrder);
        Assert.Equal(System.Text.Encoding.ASCII, format.StringEncoding);
    }

    [Fact]
    public void Validation_RejectsOutOfRangeRetryValues()
    {
        var editor = new PlcOptionsEditor(WordOrder.ABCD)
        {
            RetryCount = 101,
            RetryIntervalMs = 0,
        };

        Assert.True(editor.HasErrors);
        Assert.Contains(editor.GetErrors(nameof(PlcOptionsEditor.RetryCount)), error => error.ErrorMessage == "重试次数必须在 0 到 100 之间（0 表示不重试）。");
        Assert.Contains(editor.GetErrors(nameof(PlcOptionsEditor.RetryIntervalMs)), error => error.ErrorMessage == "重试间隔必须在 1 到 600000 毫秒之间。");
    }

    [Theory]
    [InlineData(PlcTransport.Tcp, typeof(TcpClientChannelCreator))]
    [InlineData(PlcTransport.Udp, typeof(UdpChannelCreator))]
    [InlineData(PlcTransport.Serial, typeof(SerialChannelCreator))]
    public void Provider_ReturnsTheBuiltInCreatorForEachTransport(PlcTransport transport, Type expected)
    {
        var provider = new TransportCreatorProvider();

        Assert.IsType(expected, provider.Get(transport));
    }
}
