using Junevy.Communication.Channels;
using Junevy.Communication.Channels.Framing;
using Junevy.Communication.Core.Resilience;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// <see cref="PlcComponentWiring"/> 的契约（计划 Task 4、P45-D4）：协议总是拥有分帧、关联、键提取器、心跳探测与心跳探测工厂，
/// 调用方已经设置其中任何一项时抛 <see cref="ArgumentException"/> 而不是静默覆盖；握手钩子只在协议提供了初始化器时才属于协议。
/// 守卫全部通过之后才赋值，因此不会留下半配置的组件。
/// </summary>
public sealed class PlcComponentWiringTests
{
    private const string Owner = "ExampleProfile";

    [Fact]
    public void Apply_WithNullComponents_IsAnArgumentNullException()
    {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(
            () => PlcComponentWiring.Apply<ChannelComponents>(null!, Owner, FrameCodec(), CorrelationMode.Sequential, null, null, ProbeFactory));

        Assert.Equal("components", error.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Apply_WithABlankOwner_IsAnArgumentException(string? owner)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Apply(new ChannelComponents(), owner));

        Assert.Equal("owner", error.ParamName);
    }

    [Fact]
    public void Apply_WithANullProbeFactory_IsAnArgumentNullException()
    {
        ArgumentNullException error = Assert.Throws<ArgumentNullException>(
            () => PlcComponentWiring.Apply(new ChannelComponents(), Owner, FrameCodec(), CorrelationMode.Sequential, null, null, null!));

        Assert.Equal("healthProbeFactory", error.ParamName);
    }

    [Fact]
    public void Apply_RejectsAnAlreadySetFrameCodec()
        => AssertAlreadySet(nameof(ChannelComponents.FrameCodec), new ChannelComponents { FrameCodec = FrameCodec() });

    [Fact]
    public void Apply_RejectsAnAlreadySetCorrelation()
        => AssertAlreadySet(nameof(ChannelComponents.Correlation), new ChannelComponents { Correlation = CorrelationMode.Keyed });

    [Fact]
    public void Apply_RejectsAnAlreadySetKeyExtractor()
        => AssertAlreadySet(nameof(ChannelComponents.KeyExtractor), new ChannelComponents { KeyExtractor = new ConstantKeyExtractor() });

    [Fact]
    public void Apply_RejectsAnAlreadySetHealthProbe()
        => AssertAlreadySet(nameof(ChannelComponents.HealthProbe), new ChannelComponents { HealthProbe = new NoProbe() });

    [Fact]
    public void Apply_RejectsAnAlreadySetHealthProbeFactory()
        => AssertAlreadySet(nameof(ChannelComponents.HealthProbeFactory), new ChannelComponents { HealthProbeFactory = ProbeFactory });

    [Fact]
    public void Apply_WithAPresetInitializerAndNoProtocolInitializer_KeepsTheCallersValue()
    {
        IConnectionInitializer preset = new NoInitializer();
        var components = new ChannelComponents { Initializer = preset };

        Apply(components, Owner);

        Assert.Same(preset, components.Initializer);
    }

    [Fact]
    public void Apply_WithAPresetInitializerAndAProtocolInitializer_IsAnArgumentException()
        => AssertAlreadySet(nameof(ChannelComponents.Initializer), new ChannelComponents { Initializer = new NoInitializer() }, new NoInitializer());

    [Fact]
    public void Apply_WhenTheLastGuardFails_LeavesNoComponentAssigned()
    {
        var components = new ChannelComponents { HealthProbeFactory = ProbeFactory };

        Assert.Throws<ArgumentException>(() => Apply(components, Owner, new NoInitializer()));

        Assert.Null(components.FrameCodec);
        Assert.Null(components.Correlation);
        Assert.Null(components.KeyExtractor);
        Assert.Null(components.Initializer);
        Assert.Null(components.HealthProbe);
    }

    [Fact]
    public void Apply_AssignsEveryComponentAndReturnsTheSameInstance()
    {
        var components = new ChannelComponents();
        IFrameCodecFactory codec = FrameCodec();
        IFrameKeyExtractor extractor = new ConstantKeyExtractor();
        var initializer = new NoInitializer();

        ChannelComponents configured = PlcComponentWiring.Apply(
            components, Owner, codec, CorrelationMode.Keyed, extractor, initializer, ProbeFactory);

        Assert.Same(components, configured);
        Assert.Same(codec, configured.FrameCodec);
        Assert.Equal(CorrelationMode.Keyed, configured.Correlation);
        Assert.Same(extractor, configured.KeyExtractor);
        Assert.Same(initializer, configured.Initializer);
        Assert.NotNull(configured.HealthProbeFactory);
        Assert.IsType<NoProbe>(configured.HealthProbeFactory(null!));
    }

    [Fact]
    public void Apply_WithoutAFrameCodecAndKeyExtractor_LeavesBothNull()
    {
        var components = new ChannelComponents();

        // 数据报交付的协议没有分帧可用，因此没有分帧与握手，也没有键提取器（关联不是 Keyed）。
        PlcComponentWiring.Apply(components, Owner, frameCodec: null, CorrelationMode.Sequential, keyExtractor: null, initializer: null, ProbeFactory);

        Assert.Null(components.FrameCodec);
        Assert.Null(components.KeyExtractor);
        Assert.Null(components.Initializer);
        Assert.NotNull(components.HealthProbeFactory);
    }

    [Fact]
    public void Apply_KeepsTheReconnectPolicyTheCallerConfigured()
    {
        var policy = new FixedIntervalBackoff(interval: 250);
        var components = new ChannelComponents { ReconnectPolicy = policy };

        Apply(components, Owner);

        Assert.Same(policy, components.ReconnectPolicy);
    }

    [Fact]
    public void Apply_ChecksTheAlwaysOwnedPropertiesInTheDocumentedOrder()
    {
        // 同时预设分帧与心跳探测工厂时先报分帧：分帧、关联、键提取器、握手、心跳探测、心跳探测工厂。
        var components = new ChannelComponents
        {
            FrameCodec = FrameCodec(),
            HealthProbeFactory = ProbeFactory,
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => Apply(components, Owner));

        Assert.StartsWith("ChannelComponents.FrameCodec is already set; ExampleProfile sets it and does not override it.", error.Message, StringComparison.Ordinal);
        Assert.Equal("components", error.ParamName);
    }

    private static void AssertAlreadySet(string property, ChannelComponents components, IConnectionInitializer? protocolInitializer = null)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => Apply(components, Owner, protocolInitializer));

        Assert.Equal("components", error.ParamName);
        Assert.StartsWith($"ChannelComponents.{property} is already set; {Owner}", error.Message, StringComparison.Ordinal);
    }

    private static ChannelComponents Apply(
        ChannelComponents components,
        string? owner,
        IConnectionInitializer? initializer = null)
        => PlcComponentWiring.Apply(
            components, owner!, FrameCodec(), CorrelationMode.Sequential, null, initializer, ProbeFactory);

    private static IFrameCodecFactory FrameCodec() => FrameCodecFactory.Create(new FramingOptions());

    private static readonly Func<IByteChannel, IHealthProbe> ProbeFactory = CreateProbe;

    private static IHealthProbe CreateProbe(IByteChannel channel) => new NoProbe();

    /// <summary>只用来占位 <see cref="ChannelComponents.KeyExtractor"/>。</summary>
    private sealed class ConstantKeyExtractor : IFrameKeyExtractor
    {
        public bool TryGetRequestKey(ReadOnlySpan<byte> request, out long key)
        {
            key = request.Length;
            return true;
        }

        public bool TryGetResponseKey(ReadOnlySpan<byte> frame, out long key)
        {
            key = frame.Length;
            return true;
        }
    }

    private sealed class NoProbe : IHealthProbe
    {
        public Task<CommResult> ProbeAsync(CancellationToken cancellationToken)
            => Task.FromResult(CommResult.Success());
    }

    private sealed class NoInitializer : IConnectionInitializer
    {
        public Task<CommResult> InitializeAsync(IByteChannel channel, CancellationToken cancellationToken)
            => Task.FromResult(CommResult.Success());
    }
}