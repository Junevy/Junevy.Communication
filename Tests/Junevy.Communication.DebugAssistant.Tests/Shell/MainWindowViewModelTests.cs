using System.Diagnostics;
using Junevy.Communication.DebugAssistant.Services;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.DebugAssistant.Tests.Shell;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void OpenSession_OnALeaf_AddsANamedSessionAndSelectsIt()
    {
        var (services, _) = TestShell.Create();
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();

        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));

        SessionViewModel session = Assert.Single(vm.Sessions);
        Assert.Equal(ProtocolKind.Modbus, session.Kind);
        Assert.Equal("Modbus #1", session.Title);
        Assert.Same(session, vm.SelectedSession);
        Assert.True(vm.HasSessions);
    }

    [Fact]
    public void OpenSession_OnAGroup_DoesNothing()
    {
        var (services, _) = TestShell.Create();
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();

        vm.OpenSessionCommand.Execute(vm.Tree[0]);

        Assert.Empty(vm.Sessions);
        Assert.Null(vm.SelectedSession);
        Assert.False(vm.HasSessions);
    }

    [Fact]
    public void OpenSession_NumbersEachKindOnItsOwn()
    {
        var (services, _) = TestShell.Create();
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();

        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Mc));

        Assert.Equal(new[] { "Modbus #1", "Modbus #2", "MC #1" }, vm.Sessions.Select(s => s.Title));
    }

    [Fact]
    public async Task CloseSession_DisposesBeforeItIsRemoved()
    {
        List<TrackingSession> created = [];
        var (services, _) = TestShell.Create(s => created = TestShell.TrackKind(s, ProtocolKind.Modbus));
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        TrackingSession session = created.Single();
        session.IsListed = () => vm.Sessions.Contains(session);

        await vm.CloseSessionCommand.ExecuteAsync(session);

        Assert.Equal(1, session.DisposeCount);
        Assert.True(session.ListedWhenDisposed);
        Assert.Empty(vm.Sessions);
        Assert.Null(vm.SelectedSession);
    }

    [Fact]
    public async Task CloseSession_WhenDisposeThrows_StillRemovesTheTabAndLogsTheFailure()
    {
        List<TrackingSession> created = [];
        var (services, log) = TestShell.Create(s => created = TestShell.TrackKind(s, ProtocolKind.Modbus));
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        TrackingSession session = created.Single();
        session.DisposeFailure = new InvalidOperationException("boom");

        await vm.CloseSessionCommand.ExecuteAsync(session);

        Assert.Empty(vm.Sessions);
        TestLogEntry entry = Assert.Single(log.GetEntries(LogLevel.Error));
        Assert.Equal("Releasing the session 'Modbus #1' failed.", entry.Message);
        Assert.IsType<InvalidOperationException>(entry.Exception);
    }

    [Fact]
    public async Task CloseAllSessions_WhenDeclined_KeepsEverything()
    {
        var confirm = new FakeConfirmService(answer: false);
        var (services, _) = TestShell.Create(s =>
        {
            s.AddSingleton<IConfirmService>(confirm);
            TestShell.TrackKind(s, ProtocolKind.Modbus);
        });
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));

        await vm.CloseAllSessionsCommand.ExecuteAsync(null);

        Assert.Single(vm.Sessions);
        Assert.Equal(1, confirm.Asked);
    }

    [Fact]
    public async Task CloseAllSessions_WhenConfirmed_ClosesEveryTab()
    {
        var confirm = new FakeConfirmService(answer: true);
        var (services, _) = TestShell.Create(s =>
        {
            s.AddSingleton<IConfirmService>(confirm);
            TestShell.TrackKind(s, ProtocolKind.Modbus);
        });
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));

        await vm.CloseAllSessionsCommand.ExecuteAsync(null);

        Assert.Empty(vm.Sessions);
        Assert.False(vm.HasSessions);
    }

    [Fact]
    public async Task ShutdownAsync_ReleasesEverySessionConcurrently_AndClearsTheTabs()
    {
        List<TrackingSession> created = [];
        var (services, _) = TestShell.Create(s => created = TestShell.TrackKind(s, ProtocolKind.Modbus));
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        foreach (TrackingSession session in created)
        {
            session.Gate = new TaskCompletionSource();
        }

        Task shutdown = vm.ShutdownAsync();

        // 两个释放都已经开始，而且还没有任何一个完成：是并发的。
        Assert.All(created, session => Assert.Equal(1, session.DisposeCount));
        Assert.False(shutdown.IsCompleted);

        foreach (TrackingSession session in created)
        {
            session.Gate!.SetResult();
        }

        await shutdown;
        Assert.Empty(vm.Sessions);
    }

    [Fact]
    public async Task CloseSession_WhenReleaseNeverFinishes_RemovesTheTabAfterFiveSeconds()
    {
        List<TrackingSession> created = [];
        var (services, log) = TestShell.Create(s => created = TestShell.TrackKind(s, ProtocolKind.Modbus));
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        TrackingSession session = created.Single();
        session.Gate = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();

        await vm.CloseSessionCommand.ExecuteAsync(session);

        // 时限 5 秒：下限取 80%，上限取期望值加 2 秒（计划 AGENTS 的套接字测试约定）。
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(7));
        Assert.Empty(vm.Sessions);
        TestLogEntry warning = Assert.Single(log.GetEntries(LogLevel.Warning));
        Assert.Equal("Releasing the session 'Modbus #1' took longer than 5 seconds.", warning.Message);
    }

    [Fact]
    public void ToggleTheme_DelegatesToTheThemeService()
    {
        var theme = new FakeThemeService();
        var (services, _) = TestShell.Create(s => s.AddSingleton<IThemeService>(theme));
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();

        vm.ToggleThemeCommand.Execute(null);

        Assert.Equal(1, theme.Toggles);
    }

    private static ProtocolTreeItem Leaf(MainWindowViewModel vm, ProtocolKind kind) =>
        vm.Tree.SelectMany(group => group.Children).OfType<ProtocolTreeItem>().Single(item => item.Descriptor?.Kind == kind);

    private sealed class FakeConfirmService(bool answer) : IConfirmService
    {
        public int Asked { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message)
        {
            Asked++;
            return Task.FromResult(answer);
        }
    }

    private sealed class FakeThemeService : IThemeService
    {
        public int Toggles { get; private set; }

        public void Toggle() => Toggles++;
    }
}
