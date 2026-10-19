using System.IO;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;
using Microsoft.Extensions.DependencyInjection;

namespace Junevy.Communication.DebugAssistant.Tests.Workspace;

public sealed class WorkspaceServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "junevy-da-service-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void CaptureThenRestore_RecreatesTheTabsInOrder_NotConnected_AndSelectsTheSame()
    {
        var (services, _) = TestShell.Create();
        var factory = services.GetRequiredService<ISessionFactory>();
        SessionViewModel first = factory.Create(ProtocolKind.Modbus);
        SessionViewModel second = factory.Create(ProtocolKind.Mc);
        first.Title = "产线 A";

        WorkspaceDocument captured = WorkspaceService.Capture([first, second], second);
        WorkspaceRestoreResult restored = WorkspaceService.Restore(captured, factory);

        Assert.Equal(new[] { "产线 A", "MC #1" }, restored.Sessions.Select(s => s.Title));
        Assert.Equal(new[] { ProtocolKind.Modbus, ProtocolKind.Mc }, restored.Sessions.Select(s => s.Kind));
        Assert.All(restored.Sessions, s => Assert.Equal(SessionState.NotConnected, s.State));
        Assert.Equal(1, restored.SelectedIndex);
        Assert.Empty(restored.Notices);
    }

    [Fact]
    public void Restore_SkipsUnknownKinds_WithANotice()
    {
        var (services, _) = TestShell.Create();
        var document = new WorkspaceDocument(
            WorkspaceDocument.CurrentSchemaVersion,
            [new WorkspaceSession("Gem", "GEM 页", new PlaceholderSettings()), new WorkspaceSession("Ftp", "FTP #1", new PlaceholderSettings())],
            SelectedIndex: 0);

        WorkspaceRestoreResult restored = WorkspaceService.Restore(document, services.GetRequiredService<ISessionFactory>());

        Assert.Equal(new[] { "FTP #1" }, restored.Sessions.Select(s => s.Title));
        Assert.Contains("Gem", Assert.Single(restored.Notices), StringComparison.Ordinal);
        Assert.Null(restored.SelectedIndex);
    }

    [Fact]
    public void Restore_SelectionFollowsTheRestoredTabs_WhenAnEarlierTabWasSkipped()
    {
        var (services, _) = TestShell.Create();
        var document = new WorkspaceDocument(
            WorkspaceDocument.CurrentSchemaVersion,
            [new WorkspaceSession("Gem", "GEM 页", new PlaceholderSettings()), new WorkspaceSession("Ftp", "FTP 页", new PlaceholderSettings())],
            SelectedIndex: 1);

        WorkspaceRestoreResult restored = WorkspaceService.Restore(document, services.GetRequiredService<ISessionFactory>());

        Assert.Equal(0, restored.SelectedIndex);
        Assert.Equal("FTP 页", restored.Sessions[restored.SelectedIndex!.Value].Title);
    }

    [Fact]
    public void Restore_TabWithoutSettings_IsSkipped()
    {
        var (services, _) = TestShell.Create();
        var document = new WorkspaceDocument(WorkspaceDocument.CurrentSchemaVersion, [new WorkspaceSession("Modbus", "Modbus #1", null)], null);

        WorkspaceRestoreResult restored = WorkspaceService.Restore(document, services.GetRequiredService<ISessionFactory>());

        Assert.Empty(restored.Sessions);
        Assert.Single(restored.Notices);
    }

    [Fact]
    public async Task SingleSessionFile_RoundTripsEveryKind_WithItsSettingsType()
    {
        Directory.CreateDirectory(_directory);
        var (services, _) = TestShell.Create();
        var factory = services.GetRequiredService<ISessionFactory>();

        foreach (ProtocolKind kind in Enum.GetValues<ProtocolKind>())
        {
            SessionViewModel session = factory.Create(kind);
            string path = Path.Combine(_directory, $"{kind}.json");

            WorkspaceService.WriteFile(path, WorkspaceService.ToSingleSessionDocument(session));
            WorkspaceDocument read = WorkspaceService.ReadFile(path);

            Assert.Equal(session.CaptureSettings().GetType(), read.Sessions.Single().Settings!.GetType());
            Assert.Equal(kind.ToString(), read.Sessions.Single().Kind);
            await session.DisposeAsync();
        }
    }

    [Fact]
    public void ReadFile_WithAnotherSchemaVersion_IsRejected()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "future.json");
        File.WriteAllText(path, """{ "schemaVersion": 2, "sessions": [], "selectedIndex": null }""");

        Assert.Throws<InvalidDataException>(() => WorkspaceService.ReadFile(path));
    }
}
