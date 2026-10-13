using Junevy.Communication.Secs.Hsms;
using Junevy.Communication.Secs.Protocol;
using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Secs.Tests;

/// <summary>
/// HSMS 状态（P51-D10、Q35）：状态只在一处变化、事件按发生顺序发出、订阅者抛异常不影响状态机、
/// 读到的永远是快照。都不涉及套接字。
/// </summary>
public sealed class HsmsSessionStateTests
{
    [Fact]
    public void MoveTo_RaisesOneEventPerChangeWithTheReason()
    {
        var logger = new TestLogger();
        var state = new HsmsSessionState(logger);
        var changes = new List<HsmsStateChangedEventArgs>();
        state.StateChanged += (sender, args) => changes.Add(args);

        state.MoveTo(HsmsState.NotSelected, "The peer connected.");
        state.MoveTo(HsmsState.Selected, "Select.rsp 0 (ok) was sent.");

        Assert.Equal(HsmsState.Selected, state.State);
        Assert.Equal(2, changes.Count);
        Assert.Equal(HsmsState.NotConnected, changes[0].Previous);
        Assert.Equal(HsmsState.NotSelected, changes[0].Current);
        Assert.Equal("The peer connected.", changes[0].Reason);
        Assert.Equal(HsmsState.Selected, changes[1].Current);
        Assert.Equal("Select.rsp 0 (ok) was sent.", changes[1].Reason);

        // 状态变化记 Information（一致），并且不含任何报文正文（P51-D16、Q36）。
        Assert.NotEmpty(logger.GetEntries(LogLevel.Information));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("00 00 00 0A"));
    }

    [Fact]
    public void TheSameState_IsNotAnEvent()
    {
        var state = new HsmsSessionState(new TestLogger());
        int raised = 0;
        state.StateChanged += (sender, args) => raised++;

        state.MoveTo(HsmsState.NotSelected, "first");
        state.MoveTo(HsmsState.NotSelected, "second");

        Assert.Equal(1, raised);
        Assert.Equal(HsmsState.NotSelected, state.State);
    }

    [Fact]
    public void AThrowingSubscriber_DoesNotStopTheOtherSubscribersOrTheStateMachine()
    {
        var logger = new TestLogger();
        var state = new HsmsSessionState(logger);
        int reached = 0;
        state.StateChanged += (sender, args) => throw new InvalidOperationException("A subscriber blew up.");
        state.StateChanged += (sender, args) => reached++;

        state.MoveTo(HsmsState.NotSelected, "The peer connected.");
        state.MoveTo(HsmsState.Selected, "Select.rsp 0 (ok) was sent.");

        Assert.Equal(2, reached);
        Assert.Equal(HsmsState.Selected, state.State);
        Assert.Contains(logger.GetEntries(LogLevel.Error), entry => entry.Exception is InvalidOperationException);
    }

    [Fact]
    public void Reset_AlwaysGoesToNotConnected()
    {
        var state = new HsmsSessionState(new TestLogger());
        var changes = new List<HsmsStateChangedEventArgs>();
        state.StateChanged += (sender, args) => changes.Add(args);

        state.MoveTo(HsmsState.Selected, "Select.rsp 0 (ok) was sent.");
        state.Reset("The connection was closed.");

        Assert.Equal(HsmsState.NotConnected, state.State);
        Assert.Equal(2, changes.Count);
        Assert.Equal("The connection was closed.", changes[1].Reason);
    }

    [Fact]
    public async Task State_IsASnapshotThatCanBeReadWhileItChanges()
    {
        var state = new HsmsSessionState(new TestLogger());
        var readers = new Task[4];
        for (int i = 0; i < readers.Length; i++)
            readers[i] = Task.Run(() =>
            {
                for (int step = 0; step < 200; step++)
                    Assert.True(Enum.IsDefined(typeof(HsmsState), state.State));
            });

        for (int step = 0; step < 200; step++)
        {
            state.MoveTo(HsmsState.NotSelected, "toggle");
            state.MoveTo(HsmsState.Selected, "toggle");
        }

        await Task.WhenAll(readers);
        Assert.Equal(HsmsState.Selected, state.State);
    }
}
