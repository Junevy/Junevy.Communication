using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Siemens.Framing;
using Junevy.Communication.Testing;
using static Junevy.Communication.Siemens.Tests.S7TestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Siemens.Tests;

/// <summary>
/// 通道接线（计划第 12 节第 5 条、P4-D8、P4-D10）：分帧取 TPKT 长度、关联固定为 Sequential、握手与心跳探测工厂指向本协议的实现；
/// 组件里已经设置同名属性时抛 <see cref="ArgumentException"/>，不静默覆盖；数据报交付的通道不接受 S7。
/// </summary>
public sealed class S7ProtocolProfileTests
{
    [Fact]
    public void Configure_SetsTheS7ComponentsAndReturnsTheSameInstance()
    {
        var components = new ChannelComponents();
        var session = new S7Session();

        ChannelComponents configured = S7ProtocolProfile.Configure(components, Settings(), session, ChannelDelivery.Stream);

        Assert.Same(components, configured);
        Assert.NotNull(configured.FrameCodec);
        Assert.Equal(CorrelationMode.Sequential, configured.Correlation);
        Assert.Null(configured.KeyExtractor);
        Assert.IsType<S7Handshake>(configured.Initializer);

        // P4-D15：心跳探测经通道自身的 RequestAsync 读 MB0，因此用工厂形式拿到通道引用。
        Assert.NotNull(configured.HealthProbeFactory);
        IHealthProbe probe = configured.HealthProbeFactory!(new ScriptedByteChannel(
            _ => CommResult<byte[]>.Success(WithReference(V_S7_10_Ack, 1))));
        Assert.IsType<S7ReadProbe>(probe);
    }

    [Fact]
    public void Configure_FramesTheChannelByTpktLength()
    {
        ChannelComponents components = S7ProtocolProfile.Configure(new ChannelComponents(), Settings(), new S7Session(), ChannelDelivery.Stream);
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Concat(V_S7_2, V_S7_10_Ack));

        Assert.True(components.FrameCodec!.CreateDecoder().TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(V_S7_2, ToText(frame.ToArray()));
    }

    [Fact]
    public void Configure_DatagramDelivery_Throws()
    {
        // P4-D10：S7 走 ISO-on-TCP 字节流，数据报交付的通道没有 TPKT 分帧可用。
        ArgumentException error = Assert.Throws<ArgumentException>(() => S7ProtocolProfile.Configure(
            new ChannelComponents(), Settings(), new S7Session(), ChannelDelivery.Datagram));

        Assert.Contains("stream", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configure_AlreadySetComponents_Throws()
    {
        AssertAlreadySet(new ChannelComponents { FrameCodec = S7Framing.Create() });
        AssertAlreadySet(new ChannelComponents { Correlation = CorrelationMode.Keyed });
        AssertAlreadySet(new ChannelComponents { KeyExtractor = new FixedKeyExtractor() });
        AssertAlreadySet(new ChannelComponents { Initializer = new S7Handshake(Settings(), new S7Session()) });
        AssertAlreadySet(new ChannelComponents { HealthProbe = new S7ReadProbe(new ScriptedByteChannel(
            _ => CommResult<byte[]>.Success(Array.Empty<byte>()))) });
        AssertAlreadySet(new ChannelComponents { HealthProbeFactory = channel => new S7ReadProbe(channel) });
    }

    private static void AssertAlreadySet(ChannelComponents components)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => S7ProtocolProfile.Configure(components, Settings(), new S7Session(), ChannelDelivery.Stream));

        Assert.Contains("is already set", error.Message, StringComparison.Ordinal);
    }

    private static S7ProtocolSettings Settings() => S7ProtocolSettings.Create(Pg, Rack, Slot);

    /// <summary>只用来占位 <see cref="ChannelComponents.KeyExtractor"/>：关联模式一旦是 Keyed 就必须有提取器。</summary>
    private sealed class FixedKeyExtractor : IFrameKeyExtractor
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
}
