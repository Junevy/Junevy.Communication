using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// FINS/TCP 外壳（计划 Task 12 第 1、5 条与第 17.2 节说明段）：魔数 "FINS"、长度字段（4 字节大端，计长度字段之后的全部字节）、
/// 命令与错误码。Task 12 第 6 条要求的截断与长度不符在这里就是 <c>ProtocolViolation</c>。
/// </summary>
public sealed class FinsTcpFrameTests
{
    [Theory]
    [InlineData(V_F_1, FinsTcpFrame.CommandNodeRequest)]
    [InlineData(V_F_2, FinsTcpFrame.CommandNodeResponse)]
    [InlineData(V_F_3, FinsTcpFrame.CommandFinsFrame)]
    [InlineData(V_F_4, FinsTcpFrame.CommandFinsFrame)]
    [InlineData(V_F_5_Request, FinsTcpFrame.CommandFinsFrame)]
    [InlineData(V_F_5_Response, FinsTcpFrame.CommandFinsFrame)]
    [InlineData(V_F_6_Request, FinsTcpFrame.CommandFinsFrame)]
    [InlineData(V_F_6_Response, FinsTcpFrame.CommandFinsFrame)]
    [InlineData(V_F_7, FinsTcpFrame.CommandFinsFrame)]
    [InlineData(V_F_8, FinsTcpFrame.CommandFinsFrame)]
    [InlineData(V_F_9, FinsTcpFrame.CommandFinsFrame)]
    public void EveryVector_UnwrapsIntoCommandErrorAndBody(string vector, uint expectedCommand)
    {
        byte[] frame = ToBytes(vector);

        Assert.True(FinsTcpFrame.TryUnwrap(frame, out uint command, out uint error, out ReadOnlySpan<byte> body, out string? violation), violation);

        // 长度字段 = 帧长 - 8（魔数与长度字段自身），所以体长度总是帧长减去 16 字节的壳。
        Assert.Equal(expectedCommand, command);
        Assert.Equal(0u, error);
        Assert.Equal(frame.Length - ShellBytes, body.Length);
        Assert.Equal(ToText(frame.AsSpan(ShellBytes)), ToText(body));
    }

    [Fact]
    public void Build_CountsEveryByteAfterTheLengthField()
    {
        byte[] frame = FinsTcpFrame.Build(FinsTcpFrame.CommandFinsFrame, 0, ToBytes("80 00 02"));

        Assert.Equal("46 49 4E 53 00 00 00 0B 00 00 00 02 00 00 00 00 80 00 02", ToText(frame));
    }

    [Fact]
    public void Build_WritesTheHeaderErrorCodeAndRoundTripsThroughUnwrap()
    {
        byte[] finsBody = ToBytes(V_F_3).AsSpan(ShellBytes).ToArray();

        byte[] frame = FinsTcpFrame.Build(FinsTcpFrame.CommandFinsFrame, 0xFE01, finsBody);

        Assert.True(FinsTcpFrame.TryUnwrap(frame, out uint command, out uint error, out ReadOnlySpan<byte> unwrapped, out string? violation), violation);
        Assert.Equal(FinsTcpFrame.CommandFinsFrame, command);
        Assert.Equal(0xFE01u, error);
        Assert.Equal(ToText(finsBody), ToText(unwrapped));
    }

    [Theory]
    [InlineData(V_F_1, 0, 0x47)]
    [InlineData(V_F_1, MagicTailIndex, 0x52)]
    [InlineData(V_F_4, 0, 0x47)]
    public void WrongMagic_IsAProtocolViolation(string vector, int index, int value)
        => AssertUnwrapViolation(Mutated(vector, index, (byte)value));

    [Theory]
    [InlineData(V_F_4, TcpLengthIndex + 3, 0x1B)]
    [InlineData(V_F_4, TcpLengthIndex + 3, 0x00)]
    [InlineData(V_F_2, TcpLengthIndex + 3, 0x20)]
    [InlineData(V_F_2, TcpLengthIndex + 2, 0x01)]
    public void LengthFieldNotMatchingTheFrame_IsAProtocolViolation(string vector, int index, int value)
        => AssertUnwrapViolation(Mutated(vector, index, (byte)value));

    [Fact]
    public void TruncatedShell_IsAProtocolViolation()
        => AssertUnwrapViolation(Truncated(V_F_4, ShellBytes - 1));

    [Fact]
    public void MissingLastBodyByte_IsAProtocolViolation()
        => AssertUnwrapViolation(Truncated(V_F_4, ToBytes(V_F_4).Length - 1));

    [Fact]
    public void AppendedBytesBeyondTheDeclaredLength_AreAProtocolViolation()
        => AssertUnwrapViolation(Appended(V_F_4, "00"));

    [Fact]
    public void EmptyFrame_IsAProtocolViolation()
        => AssertUnwrapViolation(Array.Empty<byte>());

    private static void AssertUnwrapViolation(byte[] frame)
    {
        Assert.False(FinsTcpFrame.TryUnwrap(frame, out _, out _, out _, out string? violation));
        Assert.NotNull(violation);
    }
}
