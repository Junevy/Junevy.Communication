using System.Reflection;
using Junevy.Communication.Channels.Framing;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// <see cref="ComponentPrecedence"/> 的规则（P25-D3）：协议组件与同名的传输配置冲突时抛出 <see cref="ArgumentException"/>，参数名为 <c>config</c>。
/// </summary>
public sealed class ComponentPrecedenceTests
{
    private const string FramingMessage =
        "Framing is defined by ChannelComponents.FrameCodec; leave the configuration's Framing at its default.";

    private const string CorrelationMessage =
        "Correlation is defined by ChannelComponents.Correlation; leave the configuration's Correlation at its default or set it to the same value.";

    private static readonly Dictionary<string, Action<FramingOptions>> ChangesByProperty = new Dictionary<string, Action<FramingOptions>>
    {
        [nameof(FramingOptions.Mode)] = options => options.Mode = FramingMode.Delimiter,
        [nameof(FramingOptions.MaxFrameLength)] = options => options.MaxFrameLength = 1024,
        [nameof(FramingOptions.Delimiters)] = options => options.Delimiters = new[] { "\\r\\n" },
        [nameof(FramingOptions.KeepDelimiter)] = options => options.KeepDelimiter = true,
        [nameof(FramingOptions.AppendDelimiterOnSend)] = options => options.AppendDelimiterOnSend = false,
        [nameof(FramingOptions.FrameLength)] = options => options.FrameLength = 4,
        [nameof(FramingOptions.LengthFieldOffset)] = options => options.LengthFieldOffset = 1,
        [nameof(FramingOptions.LengthFieldSize)] = options => options.LengthFieldSize = 4,
        [nameof(FramingOptions.LengthFieldEncoding)] = options => options.LengthFieldEncoding = LengthFieldEncoding.BinaryLittleEndian,
        [nameof(FramingOptions.LengthAdjustment)] = options => options.LengthAdjustment = 2,
        [nameof(FramingOptions.InitialBytesToStrip)] = options => options.InitialBytesToStrip = 3,
        [nameof(FramingOptions.StartMarker)] = options => options.StartMarker = "\\x02",
        [nameof(FramingOptions.EndMarker)] = options => options.EndMarker = "\\x03",
        [nameof(FramingOptions.KeepMarkers)] = options => options.KeepMarkers = false,
        [nameof(FramingOptions.GapTimeout)] = options => options.GapTimeout = 50,
    };

    [Fact]
    public void WithoutComponents_AnyConfigurationIsAccepted()
    {
        ComponentPrecedence.Validate(null, ChangedFraming(nameof(FramingOptions.Mode)), new FramingOptions(),
            CorrelationMode.Keyed, CorrelationMode.Sequential);
    }

    [Fact]
    public void ComponentsThatSetNeitherFramingNorCorrelation_AcceptAnyConfiguration()
    {
        ComponentPrecedence.Validate(new ChannelComponents(), ChangedFraming(nameof(FramingOptions.Mode)), new FramingOptions(),
            CorrelationMode.Keyed, CorrelationMode.Sequential);
    }

    [Fact]
    public void FrameCodec_WithoutAFramingConfiguration_IsNotChecked()
    {
        var components = new ChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        ComponentPrecedence.Validate(components, null, null, CorrelationMode.Sequential, CorrelationMode.Sequential);
    }

    [Fact]
    public void FrameCodec_WithTheDefaultFraming_IsAccepted()
    {
        var components = new ChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        ComponentPrecedence.Validate(components, new FramingOptions(), new FramingOptions(),
            CorrelationMode.Sequential, CorrelationMode.Sequential);
    }

    [Fact]
    public void FrameCodec_WithDelimitersEqualInContentButNotInstance_IsAccepted()
    {
        var components = new ChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };
        var defaults = new FramingOptions { Delimiters = new[] { "\\r\\n" } };
        var configured = new FramingOptions { Delimiters = new[] { "\\r\\n" } };

        ComponentPrecedence.Validate(components, configured, defaults, CorrelationMode.Sequential, CorrelationMode.Sequential);
    }

    [Theory]
    [InlineData(nameof(FramingOptions.Mode))]
    [InlineData(nameof(FramingOptions.MaxFrameLength))]
    [InlineData(nameof(FramingOptions.Delimiters))]
    [InlineData(nameof(FramingOptions.KeepDelimiter))]
    [InlineData(nameof(FramingOptions.AppendDelimiterOnSend))]
    [InlineData(nameof(FramingOptions.FrameLength))]
    [InlineData(nameof(FramingOptions.LengthFieldOffset))]
    [InlineData(nameof(FramingOptions.LengthFieldSize))]
    [InlineData(nameof(FramingOptions.LengthFieldEncoding))]
    [InlineData(nameof(FramingOptions.LengthAdjustment))]
    [InlineData(nameof(FramingOptions.InitialBytesToStrip))]
    [InlineData(nameof(FramingOptions.StartMarker))]
    [InlineData(nameof(FramingOptions.EndMarker))]
    [InlineData(nameof(FramingOptions.KeepMarkers))]
    [InlineData(nameof(FramingOptions.GapTimeout))]
    public void FrameCodec_WithAChangedFramingProperty_IsRejected(string property)
    {
        var components = new ChannelComponents { FrameCodec = FrameCodecFactory.Create(new FramingOptions()) };

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            ComponentPrecedence.Validate(components, ChangedFraming(property), new FramingOptions(),
                CorrelationMode.Sequential, CorrelationMode.Sequential));

        Assert.StartsWith(FramingMessage, exception.Message);
        Assert.Equal("config", exception.ParamName);
    }

    [Fact]
    public void Correlation_AtTheTransportDefault_IsAcceptedWhateverTheComponentSets()
    {
        var components = new ChannelComponents { Correlation = CorrelationMode.Keyed };

        ComponentPrecedence.Validate(components, null, null, CorrelationMode.Sequential, CorrelationMode.Sequential);
    }

    [Fact]
    public void Correlation_EqualToTheComponent_IsAccepted()
    {
        var components = new ChannelComponents { Correlation = CorrelationMode.Keyed };

        ComponentPrecedence.Validate(components, null, null, CorrelationMode.Keyed, CorrelationMode.Sequential);
    }

    [Fact]
    public void Correlation_NonDefaultAndDifferentFromTheComponent_IsRejected()
    {
        var components = new ChannelComponents { Correlation = CorrelationMode.Keyed };

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            ComponentPrecedence.Validate(components, null, null, CorrelationMode.Matcher, CorrelationMode.Sequential));

        Assert.StartsWith(CorrelationMessage, exception.Message);
        Assert.Equal("config", exception.ParamName);
    }

    [Fact]
    public void ChangesByProperty_CoversExactlyTheDeclaredFramingOptionsProperties()
    {
        string[] declared = typeof(FramingOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] covered = ChangesByProperty.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        string[] missing = declared.Except(covered).ToArray();
        string[] extra = covered.Except(declared).ToArray();

        Assert.True(missing.Length == 0 && extra.Length == 0,
            "HasSameValues and the precedence Theory must cover every public FramingOptions property. "
            + $"Missing from ChangesByProperty: [{string.Join(", ", missing)}]. "
            + $"Not declared on FramingOptions: [{string.Join(", ", extra)}].");
    }

    // 返回只把一个属性改为非默认值的分帧配置。
    private static FramingOptions ChangedFraming(string property)
    {
        var options = new FramingOptions();
        ChangesByProperty[property](options);
        return options;
    }
}
