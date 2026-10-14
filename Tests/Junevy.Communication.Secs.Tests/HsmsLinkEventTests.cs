using Junevy.Communication.Channels;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// 握手与链路事件对 HSMS 状态的作用（2026-10-14 合并验收发现的缺陷）。通道与服务端的事件由各自的派发任务排队送出，
/// 可能晚于实际变化、甚至晚于下一条链路的握手；这里逐个复现这些顺序，证明晚到的事件不会覆盖新链路的状态。都不涉及套接字。
/// </summary>
public sealed class HsmsLinkEventTests
{
    [Fact]
    public void ALateConnectingEvent_AfterTheHandshakeStarted_DoesNotResetTheSession()
    {
        // 实测的缺陷：首次连接时通道的 Connecting 事件晚于握手开始才送达，原实现把它当成"断开"复位成 NotConnected。
        HsmsSessionState state = NewState();
        state.BeginHandshake("The peer accepted the connection.");

        state.ApplyClientEvent(Event(ConnectionState.Disconnected, ConnectionState.Connecting), ConnectionState.Connecting);

        Assert.Equal(HsmsState.NotSelected, state.State);
    }

    [Fact]
    public void ASuccessfulHandshake_ReportsSelectedOnlyWhenTheLinkIsTakenOver()
    {
        HsmsSessionState state = NewState();
        state.BeginHandshake("The peer accepted the connection.");
        state.EndHandshake();
        Assert.Equal(HsmsState.NotSelected, state.State);

        state.ApplyClientEvent(Event(ConnectionState.Connecting, ConnectionState.Connected), ConnectionState.Connected);

        Assert.Equal(HsmsState.Selected, state.State);
    }

    [Fact]
    public void AConnectedEventForALinkThatIsAlreadyGone_IsIgnoredAndItsLossResets()
    {
        HsmsSessionState state = NewState();
        state.BeginHandshake("The peer accepted the connection.");
        state.EndHandshake();

        // 握手成功之后链路随即断开：Connected 送达时通道已在重连，不报 Selected；随后的断开事件复位。
        state.ApplyClientEvent(Event(ConnectionState.Connecting, ConnectionState.Connected), ConnectionState.Reconnecting);
        Assert.Equal(HsmsState.NotSelected, state.State);

        state.ApplyClientEvent(Event(ConnectionState.Connected, ConnectionState.Reconnecting), ConnectionState.Reconnecting);
        Assert.Equal(HsmsState.NotConnected, state.State);
    }

    [Fact]
    public void ALateLossOfTheOldLink_DoesNotResetTheHandshakeOfTheNewOne()
    {
        HsmsSessionState state = SelectedState();

        // 旧链路断了、重连已经开始新一轮握手，旧链路的断开事件这时才送达。
        state.BeginHandshake("The peer accepted the connection.");
        state.ApplyClientEvent(Event(ConnectionState.Connected, ConnectionState.Reconnecting), ConnectionState.Reconnecting);
        Assert.Equal(HsmsState.NotSelected, state.State);

        state.EndHandshake();
        state.ApplyClientEvent(Event(ConnectionState.Reconnecting, ConnectionState.Connected), ConnectionState.Connected);
        Assert.Equal(HsmsState.Selected, state.State);
    }

    [Fact]
    public void ALateLossWhenTheLinkIsAlreadyBack_IsIgnored()
    {
        HsmsSessionState state = SelectedState();

        state.ApplyClientEvent(Event(ConnectionState.Connected, ConnectionState.Reconnecting), ConnectionState.Connected);

        Assert.Equal(HsmsState.Selected, state.State);
    }

    [Theory]
    [InlineData(ConnectionState.Reconnecting)]
    [InlineData(ConnectionState.Disconnecting)]
    [InlineData(ConnectionState.Disconnected)]
    public void LosingTheSelectedLink_Resets(ConnectionState next)
    {
        HsmsSessionState state = SelectedState();

        state.ApplyClientEvent(Event(ConnectionState.Connected, next), next);

        Assert.Equal(HsmsState.NotConnected, state.State);
    }

    [Fact]
    public void PassiveSessions_AttachOnlyWhileAliveAndALateCloseDoesNotResetANewHandshake()
    {
        HsmsSessionState state = NewState();
        state.BeginHandshake("The peer connected.");
        state.EndHandshake();

        // SessionConnected 送达时会话已断：不报 Selected；它的 SessionClosed 随后复位。
        state.LinkAttached(alive: false, "Select.rsp 0 (ok) was sent.");
        Assert.Equal(HsmsState.NotSelected, state.State);
        state.LinkLost(stillConnected: false, "The session closed.");
        Assert.Equal(HsmsState.NotConnected, state.State);

        // 新会话的握手进行中，旧会话的 SessionClosed 才送达：不复位。
        state.BeginHandshake("The peer connected.");
        state.LinkLost(stillConnected: false, "The old session closed.");
        Assert.Equal(HsmsState.NotSelected, state.State);
    }

    [Fact]
    public void AFailedHandshake_EndsTheHandshakeSoTheNextLossIsNotIgnored()
    {
        HsmsSessionState state = NewState();
        state.BeginHandshake("The peer connected.");
        state.Reset("The handshake failed.");

        state.MoveTo(HsmsState.Selected, "Pretend a later link was selected.");
        state.LinkLost(stillConnected: false, "That link closed.");

        Assert.Equal(HsmsState.NotConnected, state.State);
    }

    [Fact]
    public async Task TheActiveHandshake_LeavesTheSessionNotSelectedUntilTheChannelReportsConnected()
    {
        HsmsSessionState state = NewState();
        var handshake = new HsmsActiveHandshake(state, HsmsTimers.Fast(), new TestLogger());
        var channel = new ScriptedByteChannel(request => CommResult<byte[]>.Success(
            HsmsControl.SelectResponse(HsmsTestFrames.Parse(request).SystemBytes, HsmsControl.SelectOk).ToArray()));

        CommResult result = await handshake.InitializeAsync(channel, CancellationToken.None);

        // 握手只负责报文往来；Selected 来自通道的 Connected 事件，与"断开"同属一条有序的事件流。
        Assert.True(result.IsSuccess);
        Assert.Equal(HsmsState.NotSelected, state.State);
    }

    [Fact]
    public async Task ThePassiveHandshake_LeavesTheSessionNotSelectedUntilTheServerReportsTheSession()
    {
        HsmsSessionState state = NewState();
        var handshake = new HsmsPassiveHandshake(state, HsmsTimers.Fast(), new TestLogger());

        CommResult result = await handshake.InitializeAsync(new OneFrameChannel(HsmsControl.SelectRequest(1).ToArray()), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(HsmsState.NotSelected, state.State);
    }

    private static HsmsSessionState NewState() => new HsmsSessionState(new TestLogger());

    private static HsmsSessionState SelectedState()
    {
        HsmsSessionState state = NewState();
        state.BeginHandshake("The peer accepted the connection.");
        state.EndHandshake();
        state.ApplyClientEvent(Event(ConnectionState.Connecting, ConnectionState.Connected), ConnectionState.Connected);
        Assert.Equal(HsmsState.Selected, state.State);
        return state;
    }

    private static ConnectionStateChangedEventArgs Event(ConnectionState previous, ConnectionState current)
        => new ConnectionStateChangedEventArgs(previous, current, DisconnectReason.None, null, 0);

    // 只送出一帧的通道：被动端握手按到达顺序逐帧接收，这里给它一条 Select.req。
    private sealed class OneFrameChannel : IByteChannel
    {
        private byte[]? frame;

        internal OneFrameChannel(byte[] frame) => this.frame = frame;

        public event EventHandler<FrameReceivedEventArgs>? FrameReceived
        {
            add { }
            remove { }
        }

        public Task<CommResult> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => Task.FromResult(CommResult.Success());

        public Task<CommResult<byte[]>> RequestAsync(ReadOnlyMemory<byte> payload, RequestOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(CommResult<byte[]>.Fail("The passive handshake does not send requests.", CommErrorKind.NotSupported));

        public Task<CommResult<byte[]>> ReceiveAsync(RequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            byte[]? next = Interlocked.Exchange(ref frame, null);
            return Task.FromResult(next != null
                ? CommResult<byte[]>.Success(next)
                : CommResult<byte[]>.Fail("There are no more frames.", CommErrorKind.ConnectionClosed));
        }
    }
}
