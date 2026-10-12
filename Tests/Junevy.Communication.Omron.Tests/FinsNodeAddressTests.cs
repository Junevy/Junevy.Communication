using Junevy.Communication.Omron.Protocol;
using Junevy.Communication.Testing;
using static Junevy.Communication.Omron.Tests.FinsTestSupport;
using static Junevy.Communication.Testing.HexVector;

namespace Junevy.Communication.Omron.Tests;

/// <summary>
/// 节点地址请求与应答（计划 Task 12 第 2 条、P4-D9 与 P4-D18）：请求体是 4 字节大端的客户端节点号
/// （0 表示由 PLC 分配，P4-D20 ⑥），应答体是客户端与服务端两个 4 字节节点号。命令不是 1、体长不是 8、
/// 节点号超出一个字节都返回 <c>ProtocolViolation</c>；FINS/TCP 头的错误码非 0 返回 <c>RemoteError</c>（P4-D14）。
/// </summary>
public sealed class FinsNodeAddressTests
{
    [Fact]
    public void V_F_1_Request_AskForAnAutoAssignedNode()
        => Assert.Equal(V_F_1, ToText(FinsNodeAddress.BuildRequest(0)));

    [Fact]
    public void NodeRequest_FixedClientNode_CarriesItInTheFourByteBody()
        => Assert.Equal(NodeRequest_FixedClientNode, ToText(FinsNodeAddress.BuildRequest(ClientNode)));

    [Fact]
    public void V_F_2_Response_YieldsTheClientAndServerNodes()
    {
        FinsNodeNumbers nodes = Succeeded(FinsNodeAddress.ReadResponse(ToBytes(V_F_2)));

        Assert.Equal(ClientNode, nodes.ClientNode);
        Assert.Equal(ServerNode, nodes.ServerNode);
    }

    [Theory]
    [InlineData(V_F_3)]
    [InlineData(V_F_4)]
    [InlineData(V_F_5_Response)]
    public void FinsFrameResponses_AreNotNodeAddresses(string vector)
        => AssertViolation(FinsNodeAddress.ReadResponse(ToBytes(vector)));

    [Fact]
    public void NodeCommandWithAFinsFrameBody_IsAProtocolViolation()
    {
        // 把 V-F-5 应答的 FINS/TCP 命令改成 1：体是 14 字节的 FINS 帧，不是两个节点号。
        AssertViolation(FinsNodeAddress.ReadResponse(Mutated(V_F_5_Response, TcpCommandIndex + 3, 0x01)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ShortBody_IsAProtocolViolation(int missingBytes)
    {
        byte[] frame = Truncated(V_F_2, ShellBytes + FinsNodeAddress.BodyBytes - missingBytes);

        // 同时改掉长度字段，让失败来自体长而不是壳长。
        frame[TcpLengthIndex + 3] = (byte)(frame.Length - 8);
        AssertViolation(FinsNodeAddress.ReadResponse(frame));
    }

    [Fact]
    public void LongBody_IsAProtocolViolation()
        => AssertViolation(FinsNodeAddress.ReadResponse(Appended(V_F_2, "00 00 00 00")));

    [Fact]
    public void TruncatedShell_IsAProtocolViolation()
        => AssertViolation(FinsNodeAddress.ReadResponse(Truncated(V_F_2, ShellBytes - 1)));

    [Fact]
    public void WrongMagic_IsAProtocolViolation()
        => AssertViolation(FinsNodeAddress.ReadResponse(Mutated(V_F_2, 0, 0x47)));

    [Fact]
    public void ClientNodeBeyondOneByte_IsAProtocolViolation()
        => AssertViolation(FinsNodeAddress.ReadResponse(Mutated(V_F_2, ClientNodeTailIndex - 1, 0x01)));

    [Fact]
    public void ServerNodeBeyondOneByte_IsAProtocolViolation()
        => AssertViolation(FinsNodeAddress.ReadResponse(Mutated(V_F_2, ServerNodeTailIndex - 1, 0x01)));

    [Fact]
    public void HeaderError_IsARemoteErrorWithThatCode()
    {
        AssertRemoteError(
            FinsNodeAddress.ReadResponse(WithTcpError(V_F_2, 0xFE01)), 0xFE01, TcpErrorMessage(0xFE01));
    }
}
