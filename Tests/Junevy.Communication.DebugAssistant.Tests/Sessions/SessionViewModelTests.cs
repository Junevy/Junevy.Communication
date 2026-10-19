using Junevy.Communication.Core.Results;
using Junevy.Communication.DebugAssistant.Logging;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Services;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Tests.Sessions;

public sealed class SessionViewModelTests
{
    [Fact]
    public async Task ConnectCommand_RecordsSuccess_AndTheStateControlsTheCommands()
    {
        await using var session = new ScriptedSession(Dependencies(), connect: (s, _) =>
        {
            s.SetState(SessionState.Connected);
            return Task.CompletedTask;
        });
        Assert.True(session.IsEditable);
        Assert.True(session.ConnectCommand.CanExecute(null));
        Assert.False(session.DisconnectCommand.CanExecute(null));

        await session.ConnectCommand.ExecuteAsync(null);

        OperationRecord record = Assert.Single(session.Operations);
        Assert.True(record.Succeeded);
        Assert.Equal("连接", record.Operation);
        Assert.Equal("已连接", session.StateText);
        Assert.False(session.IsEditable);
        Assert.False(session.ConnectCommand.CanExecute(null));
        Assert.True(session.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConnectCommand_WhenTheCoreThrows_RecordsAnUnspecifiedFailure_AndLogsIt()
    {
        await using var session = new ScriptedSession(Dependencies(), connect: (_, _) => throw new InvalidOperationException("boom"));

        await session.ConnectCommand.ExecuteAsync(null);

        OperationRecord record = Assert.Single(session.Operations);
        Assert.False(record.Succeeded);
        Assert.Equal(CommErrorKind.Unspecified, record.ErrorKind);
        Assert.Equal("boom", record.Message);
        session.Log.Flush();
        LogEntry logged = Assert.Single(session.Log.Visible.Cast<LogEntry>(), e => e.Level == LogLevel.Error);
        Assert.Equal("连接 failed.", logged.Message);
    }

    [Fact]
    public async Task CancelConnect_RecordsCancelled()
    {
        await using var session = new ScriptedSession(Dependencies(), connect: async (_, ct) => await Task.Delay(Timeout.Infinite, ct));

        Task running = session.ConnectCommand.ExecuteAsync(null);
        Assert.True(session.ConnectCancelCommand.CanExecute(null));
        session.ConnectCancelCommand.Execute(null);
        await running;

        OperationRecord record = Assert.Single(session.Operations);
        Assert.Equal(CommErrorKind.Cancelled, record.ErrorKind);
        Assert.False(record.Succeeded);
    }

    [Fact]
    public async Task DisconnectCommand_IsOnlyAvailableWhileConnected()
    {
        await using var session = new ScriptedSession(Dependencies());
        Assert.False(session.DisconnectCommand.CanExecute(null));

        session.SetState(SessionState.Connected);

        Assert.True(session.DisconnectCommand.CanExecute(null));
    }

    [Fact]
    public async Task DisposeAsync_DisconnectsTheSession()
    {
        bool disconnected = false;
        var session = new ScriptedSession(Dependencies(), disconnect: (_, _) =>
        {
            disconnected = true;
            return Task.CompletedTask;
        });

        await session.DisposeAsync();

        Assert.True(disconnected);
    }

    [Fact]
    public async Task Operations_AreCappedAt500_NewestFirst()
    {
        await using var session = new ScriptedSession(Dependencies());
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        for (int i = 0; i < 510; i++)
        {
            session.Record(new OperationRecord(now, $"op {i}", string.Empty, TimeSpan.Zero, true, CommErrorKind.None, null, string.Empty));
        }

        Assert.Equal(500, session.Operations.Count);
        Assert.Equal("op 509", session.Operations[0].Operation);
        Assert.Equal("op 10", session.Operations[^1].Operation);
    }

    [Fact]
    public async Task PostToUi_RunsTheActionOnTheDispatcher()
    {
        var dispatcher = new ManualUiDispatcher();
        await using var session = new ScriptedSession(FakeServices.Create(dispatcher));
        bool ran = false;

        session.Post(() => ran = true);
        Assert.False(ran);
        Assert.Equal(1, dispatcher.Pending);

        dispatcher.RunPending();
        Assert.True(ran);
    }

    private static SessionServices Dependencies() => FakeServices.Create();
}
