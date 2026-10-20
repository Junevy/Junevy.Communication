using System.IO;
using System.Text.Json;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;
using Junevy.Communication.DebugAssistant.Tests.Support;
using Junevy.Communication.DebugAssistant.Workspace;

namespace Junevy.Communication.DebugAssistant.Tests.Workspace;

/// <summary>
/// 工作区往返覆盖全部 19 种页签（计划 Task 19 第 3 条）：每种页签的参数保存到文件并读回，应用到新页签后参数不变；
/// 每个协议种类都有实现的页签（占位页签已删除）。
/// </summary>
public sealed class AllKindsSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "junevy-da-allkinds-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task EveryProtocolKind_HasAPage_AndItsSettingsRoundTripThroughTheWorkspaceFile()
    {
        Assert.Equal(19, Enum.GetValues<ProtocolKind>().Length);
        Assert.Equal(Enum.GetValues<ProtocolKind>().Length, ProtocolCatalog.All.Count());

        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "workspace.json");
        var sessions = new List<WorkspaceSession>();
        var before = new List<string>();
        foreach (ProtocolDescriptor descriptor in ProtocolCatalog.All)
        {
            await using SessionViewModel page = TestShell.CreateSession(descriptor.Kind);
            SessionSettings settings = page.CaptureSettings();
            sessions.Add(new WorkspaceSession(descriptor.Kind.ToString(), descriptor.ShortName, settings));
            before.Add(JsonSerializer.Serialize(settings));
        }
        new WorkspaceStore(path).Save(new WorkspaceDocument(WorkspaceDocument.CurrentSchemaVersion, sessions, null));

        WorkspaceLoadResult loaded = new WorkspaceStore(path).Load();

        Assert.Null(loaded.Notice);
        Assert.Equal(sessions.Count, loaded.Document.Sessions.Count);
        List<ProtocolKind> kinds = ProtocolCatalog.All.Select(descriptor => descriptor.Kind).ToList();
        for (int index = 0; index < kinds.Count; index++)
        {
            await using SessionViewModel restored = TestShell.CreateSession(kinds[index]);
            restored.ApplySettings(loaded.Document.Sessions[index].Settings!);
            Assert.Equal(before[index], JsonSerializer.Serialize(restored.CaptureSettings()));
        }
    }
}
