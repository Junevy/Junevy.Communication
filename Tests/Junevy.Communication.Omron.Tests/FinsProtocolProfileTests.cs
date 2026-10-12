using System.Buffers;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Omron.Framing;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 通道接线（计划 Task 13 第 4 条、P4-D8、P4-D10）：分帧取 FINS/TCP 长度字段、应答按发送顺序关联、
/// 握手与心跳探测指向本协议的实现；组件里已经设置同名属性时抛 <see cref="ArgumentException"/>，不静默覆盖；
/// 数据报交付的通道没有 FINS/TCP 分帧可用，因此不接受 FINS。
/// </summary>
public sealed class FinsProtocolProfileTests
{
    [Fact]
    public void Configure_SetsTheFinsComponentsAndReturnsTheSameInstance()
    {
        var components = new ChannelComponents();
        var session = new FinsSession();

        ChannelComponents configured = FinsProtocolProfile.Configure(components, Settings(), session, ChannelDelivery.Stream);

        Assert.Same(components, configured);
        Assert.NotNull(configured.FrameCodec);
        Assert.Equal(CorrelationMode.Sequential, configured.Correlation);
        Assert.Null(configured.KeyExtractor);
        Assert.IsType<FinsNodeHandshake>(configured.Initializer);

        // P4-D15：心跳探测经通道自身的 RequestAsync 读 D0，因此用工厂形式拿到通道引用。
        Assert.NotNull(configured.HealthProbeFactory);
        IHealthProbe probe = configured.HealthProbeFactory!(new ScriptedByteChannel(
            request => CommResult<byte[]>.Success(ReadAck(SessionIdOf(request), new byte[FinsMemoryArea.WordBytes]))));
        Assert.IsType<FinsReadProbe>(probe);
    }

    [Fact]
    public void Configure_FramesTheChannelByTheFinsLengthField()
    {
        ChannelComponents components = FinsProtocolProfile.Configure(new ChannelComponents(), Settings(), new FinsSession(), ChannelDelivery.Stream);
        ReadOnlySequence<byte> buffer = new ReadOnlySequence<byte>(Concat(V_F_2, V_F_4));

        Assert.True(components.FrameCodec!.CreateDecoder().TryDecode(ref buffer, out ReadOnlySequence<byte> frame));
        Assert.Equal(V_F_2, ToText(frame.ToArray()));
    }

    [Fact]
    public void Configure_DatagramDelivery_Throws()
    {
        // P4-D10：FINS 走 TCP 字节流，数据报交付的通道没有长度字段分帧可用。
        ArgumentException error = Assert.Throws<ArgumentException>(() => FinsProtocolProfile.Configure(
            new ChannelComponents(), Settings(), new FinsSession(), ChannelDelivery.Datagram));

        Assert.Contains("stream", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Configure_AlreadySetComponents_Throws()
    {
        AssertAlreadySet(new ChannelComponents { FrameCodec = FinsFraming.Create() });
        AssertAlreadySet(new ChannelComponents { Correlation = CorrelationMode.Keyed });
        AssertAlreadySet(new ChannelComponents { KeyExtractor = new FixedKeyExtractor() });
        AssertAlreadySet(new ChannelComponents { Initializer = new FinsNodeHandshake(Settings(), new FinsSession()) });
        AssertAlreadySet(new ChannelComponents { HealthProbe = new FinsReadProbe(new ScriptedByteChannel(
            _ => CommResult<byte[]>.Success(Array.Empty<byte>())), Session()) });
        AssertAlreadySet(new ChannelComponents { HealthProbeFactory = channel => new FinsReadProbe(channel, Session()) });
    }

    private static void AssertAlreadySet(ChannelComponents components)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => FinsProtocolProfile.Configure(components, Settings(), new FinsSession(), ChannelDelivery.Stream));

        Assert.Contains("is already set", error.Message, StringComparison.Ordinal);
    }

    private static FinsProtocolSettings Settings() => FinsProtocolSettings.Create(0, 0, 0);

    private static FinsSession Session()
    {
        var session = new FinsSession();
        session.ReplaceAfterHandshake(Route);
        return session;
    }

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
