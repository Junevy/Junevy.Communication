using System.IO;
using Junevy.Communication.DebugAssistant.Services;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Tests.Workspace;

/// <summary>主窗口的工作区命令：启动恢复、保存、退出前保存，以及单个页签的导入与导出（计划 Task 3）。</summary>
public sealed class WorkspaceCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "junevy-da-command-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WorkspacePath => Path.Combine(_directory, "workspace.json");

    [Fact]
    public async Task ShutdownAsync_SavesTheWorkspaceBeforeReleasingTheSessions()
    {
        var (services, _) = TestShell.Create(s => s.AddSingleton(new WorkspaceStore(WorkspacePath)));
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();
        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));
        vm.Sessions[0].Title = "产线 A";

        await vm.ShutdownAsync();

        WorkspaceDocument saved = new WorkspaceStore(WorkspacePath).Load().Document;
        Assert.Equal("产线 A", Assert.Single(saved.Sessions).Title);
        Assert.Empty(vm.Sessions);
    }

    [Fact]
    public void RestoreWorkspace_RestoresTheSavedTabs_AndNotifiesTheSkippedOnes()
    {
        Directory.CreateDirectory(_directory);
        new WorkspaceStore(WorkspacePath).Save(new WorkspaceDocument(
            WorkspaceDocument.CurrentSchemaVersion,
            [new WorkspaceSession("Ftp", "FTP #1", new PlaceholderSettings()), new WorkspaceSession("Gem", "GEM", new PlaceholderSettings())],
            SelectedIndex: 0));
        var (services, _) = TestShell.Create(s => s.AddSingleton(new WorkspaceStore(WorkspacePath)));
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();

        vm.RestoreWorkspace();

        Assert.Equal(new[] { "FTP #1" }, vm.Sessions.Select(s => s.Title));
        Assert.Same(vm.Sessions[0], vm.SelectedSession);
        RecordingNotificationService notifications = (RecordingNotificationService)services.GetRequiredService<INotificationService>();
        (NotificationKind kind, string message) = Assert.Single(notifications.Shown);
        Assert.Equal(NotificationKind.Warning, kind);
        Assert.Contains("Gem", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreWorkspace_WithACorruptFile_StartsEmpty_AndNotifies()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(WorkspacePath, "{ broken");
        var (services, _) = TestShell.Create(s => s.AddSingleton(new WorkspaceStore(WorkspacePath)));
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();

        vm.RestoreWorkspace();

        Assert.Empty(vm.Sessions);
        RecordingNotificationService notifications = (RecordingNotificationService)services.GetRequiredService<INotificationService>();
        Assert.Contains("workspace.json.bak", Assert.Single(notifications.Shown).Message, StringComparison.Ordinal);
        Assert.True(File.Exists(WorkspacePath + ".bak"));
    }

    [Fact]
    public void SaveWorkspace_NotifiesSuccess()
    {
        var (services, _) = TestShell.Create(s => s.AddSingleton(new WorkspaceStore(WorkspacePath)));
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();

        vm.SaveWorkspaceCommand.Execute(null);

        Assert.True(File.Exists(WorkspacePath));
        RecordingNotificationService notifications = (RecordingNotificationService)services.GetRequiredService<INotificationService>();
        Assert.Equal(NotificationKind.Success, Assert.Single(notifications.Shown).Kind);
    }

    [Fact]
    public void ExportThenImport_RecreatesTheSelectedTab()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "modbus.json");
        var (services, _) = TestShell.Create();
        var files = (FakeFileDialogService)services.GetRequiredService<IFileDialogService>();
        MainWindowViewModel source = services.GetRequiredService<MainWindowViewModel>();
        source.OpenSessionCommand.Execute(Leaf(source, ProtocolKind.Modbus));
        source.Sessions[0].Title = "产线 B";
        files.SaveTo = path;

        source.ExportSessionCommand.Execute(null);

        Assert.True(File.Exists(path));
        var (otherServices, _) = TestShell.Create();
        var otherFiles = (FakeFileDialogService)otherServices.GetRequiredService<IFileDialogService>();
        MainWindowViewModel target = otherServices.GetRequiredService<MainWindowViewModel>();
        otherFiles.OpenFrom = path;

        target.ImportSessionCommand.Execute(null);

        SessionViewModel imported = Assert.Single(target.Sessions);
        Assert.Equal(ProtocolKind.Modbus, imported.Kind);
        Assert.Equal("产线 B", imported.Title);
        Assert.Same(imported, target.SelectedSession);
    }

    [Fact]
    public void ExportSession_IsDisabledWithoutASelectedTab()
    {
        var (services, _) = TestShell.Create();
        MainWindowViewModel vm = services.GetRequiredService<MainWindowViewModel>();

        Assert.False(vm.ExportSessionCommand.CanExecute(null));

        vm.OpenSessionCommand.Execute(Leaf(vm, ProtocolKind.Modbus));

        Assert.True(vm.ExportSessionCommand.CanExecute(null));
    }

    private static ProtocolTreeItem Leaf(MainWindowViewModel vm, ProtocolKind kind) =>
        vm.Tree.SelectMany(group => group.Children).OfType<ProtocolTreeItem>().Single(item => item.Descriptor?.Kind == kind);
}
