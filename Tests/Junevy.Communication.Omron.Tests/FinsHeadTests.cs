using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// FINS 头（计划 Task 12 第 3 条与第 17.2 节说明段）：ICF 0x80、RSV 0、GCT 0x02、六个路由字节与 SID 的写入顺序；
/// 应答侧只校验 ICF 的应答位（0x40）与 SID 回显（Task 12 第 5 条），路由字节不参与判断。
/// </summary>
public sealed class FinsHeadTests
{
    private const byte ReadSid = 0x01;

    [Fact]
    public void RequestHead_WritesTheTenFieldsInOrder()
    {
        using var buffer = new ByteBufferWriter(FinsHead.HeadBytes);
        FinsHead.WriteRequest(buffer, Route, ReadSid);

        // ICF、RSV、GCT、DNA、DA1、DA2、SNA、SA1、SA2、SID（P4-D18：网络号与单元号为 0，DA1 是服务端节点、SA1 是客户端节点）。
        Assert.Equal("80 00 02 00 01 00 00 0B 00 01", ToText(buffer.WrittenSpan));
    }

    [Theory]
    [InlineData(V_F_3)]
    [InlineData(V_F_5_Request)]
    [InlineData(V_F_6_Request)]
    [InlineData(V_F_7)]
    public void RequestHead_RebuildsTheHeadOfEveryRequestVector(string vector)
    {
        byte[] frame = ToBytes(vector);
        using var buffer = new ByteBufferWriter(FinsHead.HeadBytes);
        FinsHead.WriteRequest(buffer, Route, frame[SidIndex]);

        Assert.Equal(ToText(frame.AsSpan(IcfIndex, FinsHead.HeadBytes)), ToText(buffer.WrittenSpan));
    }

    [Fact]
    public void RequestHead_UsesTheRouteItIsGiven()
    {
        // P4-D18 的 DNA 与 DA2 是客户端选项，头编码只按路由写入，不含默认值。
        var routed = new FinsRoute(0x01, 0x02, 0x03, 0x04, 0x05, 0x06);
        using var buffer = new ByteBufferWriter(FinsHead.HeadBytes);
        FinsHead.WriteRequest(buffer, routed, 0x07);

        Assert.Equal("80 00 02 01 02 03 04 05 06 07", ToText(buffer.WrittenSpan));
    }

    [Theory]
    [InlineData(V_F_4, 0x01)]
    [InlineData(V_F_5_Response, 0x02)]
    [InlineData(V_F_6_Response, 0x03)]
    [InlineData(V_F_8, 0x05)]
    [InlineData(V_F_9, 0x06)]
    public void ResponseHead_AcceptsTheVectorHeadAndAdvancesPastIt(string vector, int sessionId)
    {
        byte[] frame = ToBytes(vector);
        ReadOnlySpan<byte> body = frame.AsSpan(ShellBytes);

        Assert.True(FinsHead.TryTakeResponse(ref body, (byte)sessionId, out string? violation), violation);
        Assert.Equal(frame.Length - ShellBytes - FinsHead.HeadBytes, body.Length);
    }

    [Theory]
    [InlineData(V_F_4, 0x80)]
    [InlineData(V_F_4, 0x00)]
    public void ResponseHead_WithoutTheResponseBit_IsAProtocolViolation(string vector, int icf)
        => AssertHeadViolation(Mutated(vector, IcfIndex, (byte)icf), ReadSid);

    [Theory]
    [InlineData(V_F_4, 0x42)]
    [InlineData(V_F_4, 0xC1)]
    public void ResponseHead_IgnoresTheIcfBitsBelowTheResponseFlag(string vector, int icf)
    {
        // 17.2 节说明段只要求应答位（0x40）置位；向量的 ICF 是 0xC0，其余位的取值不参与判断。
        byte[] frame = Mutated(vector, IcfIndex, (byte)icf);
        ReadOnlySpan<byte> body = frame.AsSpan(ShellBytes);

        Assert.True(FinsHead.TryTakeResponse(ref body, ReadSid, out string? violation), violation);
    }

    [Fact]
    public void ResponseHead_WithAnotherSid_IsAProtocolViolation()
        => AssertHeadViolation(Mutated(V_F_4, SidIndex, 0x02), ReadSid);

    [Fact]
    public void ResponseHead_ShorterThanTheHead_IsAProtocolViolation()
    {
        byte[] frame = Truncated(V_F_4, ShellBytes + FinsHead.HeadBytes - 1);
        ReadOnlySpan<byte> body = frame.AsSpan(ShellBytes);

        Assert.False(FinsHead.TryTakeResponse(ref body, ReadSid, out string? violation));
        Assert.NotNull(violation);
    }

    [Fact]
    public void ResponseHead_WithBothNodesSwapped_StillAccepted()
    {
        // 应答的 DA 与 SA 对调（17.2 节说明段），但 Task 12 第 5 条不要求校验路由，改写的节点号不影响解码。
        byte[] frame = Mutated(V_F_4, IcfIndex + 4, 0x02);
        frame[IcfIndex + 7] = 0x03;
        ReadOnlySpan<byte> body = frame.AsSpan(ShellBytes);

        Assert.True(FinsHead.TryTakeResponse(ref body, ReadSid, out string? violation), violation);
    }

    private static void AssertHeadViolation(byte[] frame, byte expectedSessionId)
    {
        ReadOnlySpan<byte> body = frame.AsSpan(ShellBytes);

        Assert.False(FinsHead.TryTakeResponse(ref body, expectedSessionId, out string? violation));
        Assert.NotNull(violation);
    }
}
