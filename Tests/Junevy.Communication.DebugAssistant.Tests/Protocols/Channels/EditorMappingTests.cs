using Junevy.Communication.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels;
using Junevy.Communication.DebugAssistant.Protocols.Channels.Editors;
using Junevy.Communication.Tcp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Channels;

/// <summary>编辑器到库选项的映射，每个字段一行（计划 Task 4 第 3 条）。</summary>
public sealed class EditorMappingTests
{
    [Theory]
    [InlineData(FramingMode.Delimiter, "hex:0D 0A\nhex:00", true, false, 2)]
    [InlineData(FramingMode.Raw, "hex:0D 0A", true, false, 0)]
    public void Framing_Delimiters_AreSplitPerLine_OnlyForDelimiterMode(FramingMode mode, string text, bool keep, bool append, int count)
    {
        var editor = new FramingEditor { Mode = mode, Delimiters = text, KeepDelimiter = keep, AppendDelimiterOnSend = append };

        FramingOptions options = editor.ToOptions();

        Assert.Equal(count, options.Delimiters?.Length ?? 0);
        Assert.Equal(keep, options.KeepDelimiter);
        Assert.Equal(append, options.AppendDelimiterOnSend);
    }

    [Fact]
    public void Framing_LengthField_FieldsAreMapped()
    {
        var editor = new FramingEditor
        {
            Mode = FramingMode.LengthField,
            LengthFieldOffset = 4,
            LengthFieldSize = 2,
            LengthFieldEncoding = LengthFieldEncoding.BinaryLittleEndian,
            LengthAdjustment = -4,
            InitialBytesToStrip = 6,
            MaxFrameLength = 300,
        };

        FramingOptions options = editor.ToOptions();

        Assert.Equal(4, options.LengthFieldOffset);
        Assert.Equal(2, options.LengthFieldSize);
        Assert.Equal(LengthFieldEncoding.BinaryLittleEndian, options.LengthFieldEncoding);
        Assert.Equal(-4, options.LengthAdjustment);
        Assert.Equal(6, options.InitialBytesToStrip);
        Assert.Equal(300, options.MaxFrameLength);
    }

    [Fact]
    public void Framing_FixedLength_MapsToFrameLength()
    {
        FramingOptions options = new FramingEditor { Mode = FramingMode.FixedLength, FixedLength = 16 }.ToOptions();

        Assert.Equal(16, options.FrameLength);
    }

    [Theory]
    [InlineData(FramingMode.StartEnd, "hex:02", "hex:03", "hex:02", "hex:03")]
    [InlineData(FramingMode.Raw, "hex:02", "hex:03", null, null)]
    public void Framing_Markers_AreOnlySetInStartEndMode(FramingMode mode, string start, string end, string? expectedStart, string? expectedEnd)
    {
        FramingOptions options = new FramingEditor { Mode = mode, StartMarker = start, EndMarker = end }.ToOptions();

        Assert.Equal(expectedStart, options.StartMarker);
        Assert.Equal(expectedEnd, options.EndMarker);
    }

    [Fact]
    public void Framing_GapTimeout_IsMapped()
    {
        Assert.Equal(35, new FramingEditor { Mode = FramingMode.IdleGap, GapTimeout = 35 }.ToOptions().GapTimeout);
    }

    [Fact]
    public void Framing_OutOfRangeValues_AreReportedAsValidationErrors()
    {
        var editor = new FramingEditor { FixedLength = 0 };

        Assert.True(editor.HasErrors);
        Assert.Contains(editor.GetErrors(nameof(FramingEditor.FixedLength)), e => e.ErrorMessage == "定长的长度必须在 1 到 65536 之间。");
    }

    [Fact]
    public void Heartbeat_PayloadAndExpectedReply_AreOnlySentWhenEnabled()
    {
        var disabled = new HeartbeatEditor { Enabled = false, Payload = "hex:00", ExpectedReply = "hex:01" }.ToOptions();
        var enabledNoReply = new HeartbeatEditor { Enabled = true, Payload = "hex:00", ExpectedReply = string.Empty }.ToOptions();
        var enabled = new HeartbeatEditor { Enabled = true, Payload = "hex:00", ExpectedReply = "hex:01", Interval = 1000, Timeout = 500, MaxFailures = 5, OnlyWhenIdle = false }.ToOptions();

        Assert.Null(disabled.Payload);
        Assert.Null(disabled.ExpectedReply);
        Assert.Null(enabledNoReply.ExpectedReply);
        Assert.Equal("hex:00", enabled.Payload);
        Assert.Equal("hex:01", enabled.ExpectedReply);
        Assert.Equal((1000, 500, 5, false), (enabled.Interval, enabled.Timeout, enabled.MaxFailures, enabled.OnlyWhenIdle));
    }

    [Fact]
    public void Reconnect_FieldsAreMapped()
    {
        ReconnectOptions options = new ReconnectEditor
        {
            Enabled = true,
            Mode = ReconnectMode.FixedInterval,
            Interval = 2000,
            MaxInterval = 8000,
            MaxAttempts = 3,
            OnInitialFailure = true,
        }.ToOptions();

        Assert.Equal((true, ReconnectMode.FixedInterval, 2000, 8000, 3, true),
            (options.Enabled, options.Mode, options.Interval, options.MaxInterval, options.MaxAttempts, options.OnInitialFailure));
    }

    [Fact]
    public void Socket_FieldsAreMapped_IncludingKeepAlive()
    {
        TcpSocketOptions options = new SocketEditor
        {
            NoDelay = false,
            ReceiveBufferSize = 4096,
            SendBufferSize = 8192,
            LingerTime = 0,
            KeepAliveEnabled = false,
            KeepAliveTime = 1000,
            KeepAliveInterval = 500,
            KeepAliveRetryCount = 7,
        }.ToOptions();

        Assert.False(options.NoDelay);
        Assert.Equal((4096, 8192, 0), (options.ReceiveBufferSize, options.SendBufferSize, options.LingerTime));
        Assert.Equal((false, 1000, 500, 7), (options.KeepAlive.Enabled, options.KeepAlive.Time, options.KeepAlive.Interval, options.KeepAlive.RetryCount));
    }

    [Fact]
    public void Tls_WhenDisabled_NoCertificateIsMapped()
    {
        var options = new TlsEditor { Enabled = false, ClientCertificate = ClientCertificateKind.Pfx, PfxPath = "c.pfx" }.ToOptions();

        Assert.False(options.Enabled);
        Assert.Null(options.ClientCertificate);
    }

    [Theory]
    [InlineData(ClientCertificateKind.None, null)]
    [InlineData(ClientCertificateKind.Store, "ABCD")]
    public void Tls_StoreOrNoCertificate_IsMapped(ClientCertificateKind kind, string? thumbprint)
    {
        var options = new TlsEditor { Enabled = true, ClientCertificate = kind, Thumbprint = thumbprint ?? string.Empty }.ToOptions();

        Assert.Equal(kind == ClientCertificateKind.None ? null : thumbprint, options.ClientCertificate?.Thumbprint);
    }

    [Fact]
    public void Tls_PfxCertificate_KeepsOnlyTheNameOfThePasswordVariable()
    {
        var options = new TlsEditor
        {
            Enabled = true,
            ClientCertificate = ClientCertificateKind.Pfx,
            PfxPath = @"C:\certs\client.pfx",
            PfxPasswordVariable = "CLIENT_PFX_PASSWORD",
            TargetHost = "plc.local",
            AllowUntrustedServerCertificate = true,
        }.ToOptions();

        Assert.Equal(@"C:\certs\client.pfx", options.ClientCertificate!.PfxPath);
        Assert.Equal("CLIENT_PFX_PASSWORD", options.ClientCertificate.PfxPasswordEnvironmentVariable);
        Assert.Equal("plc.local", options.TargetHost);
        Assert.True(options.AllowUntrustedServerCertificate);
    }

    [Fact]
    public void Correlation_PrefixRule_IsTheResponsePrefix()
    {
        var rule = new CorrelationEditor { MatchByPrefix = true, ResponsePrefix = "hex:02 41" }.ToMatchRule();

        Assert.Equal(new byte[] { 0x02, 0x41 }, Assert.IsType<PrefixMatchRule>(rule).Prefix);
    }

    [Fact]
    public void Correlation_EchoRule_IsTheOffsetAndLength()
    {
        var rule = Assert.IsType<EchoMatchRule>(new CorrelationEditor { MatchByPrefix = false, EchoOffset = 2, EchoLength = 3 }.ToMatchRule());

        Assert.Equal((2, 3), (rule.Offset, rule.Length));
    }

    [Fact]
    public void Correlation_Keyed_ProducesAKeyExtractor_AndSequentialDoesNot()
    {
        var keyed = new ChannelSettingsEditor();
        keyed.Correlation.Mode = CorrelationMode.Keyed;
        var sequential = new ChannelSettingsEditor();

        Assert.NotNull(keyed.ToComponents<ChannelComponents>().KeyExtractor);
        Assert.Equal(CorrelationMode.Keyed, keyed.ToComponents<ChannelComponents>().Correlation);
        Assert.Null(sequential.ToComponents<ChannelComponents>().KeyExtractor);
        Assert.Equal(CorrelationMode.Sequential, sequential.ToComponents<ChannelComponents>().Correlation);
    }

    [Fact]
    public void Handshake_WhenEnabled_ProducesAnInitializer_AndWhenDisabledNone()
    {
        var enabled = new HandshakeEditor { Enabled = true, Send = "hex:02", ExpectReply = true, ExpectedReply = "hex:06" };
        var disabled = new HandshakeEditor { Enabled = false };

        Assert.NotNull(enabled.ToInitializer());
        Assert.Null(disabled.ToInitializer());
    }

    [Fact]
    public void ChannelSettings_AreValid_WhenEveryEditorIs()
    {
        var settings = new ChannelSettingsEditor();

        Assert.True(settings.IsValid);
        settings.Timeouts.RequestTimeout = 0;
        Assert.False(settings.IsValid);
    }
}
