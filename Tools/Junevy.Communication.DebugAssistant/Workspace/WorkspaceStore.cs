using System.IO;
using System.Text.Json;

namespace Junevy.Communication.DebugAssistant.Workspace;

/// <summary>
/// 读写工作区文件。读取时遇到损坏或版本不符，把原文件改名为 <c>.bak</c> 保留，以空工作区启动（不覆盖用户的数据）。
/// 写入时先写临时文件，再替换原文件，写入中途失败时原文件保持不变（计划 Task 3）。
/// </summary>
public sealed class WorkspaceStore
{
    private readonly string _path;

    public WorkspaceStore(string path)
    {
        _path = path;
    }

    /// <summary>默认位置：%LocalAppData%\Junevy.Communication\DebugAssistant\workspace.json。</summary>
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Junevy.Communication",
        "DebugAssistant",
        "workspace.json");

    public string FilePath => _path;

    /// <summary>读取工作区。文件不存在时返回空工作区，没有任何提示。</summary>
    public WorkspaceLoadResult Load()
    {
        if (!File.Exists(_path))
        {
            return new WorkspaceLoadResult(WorkspaceDocument.Empty, null);
        }

        WorkspaceDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<WorkspaceDocument>(File.ReadAllText(_path), WorkspaceJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return BackUp($"工作区文件已损坏（{ex.Message}）");
        }

        if (document is null)
        {
            return BackUp("工作区文件为空");
        }

        if (document.SchemaVersion != WorkspaceDocument.CurrentSchemaVersion)
        {
            return BackUp($"工作区文件的版本为 {document.SchemaVersion}，与当前版本 {WorkspaceDocument.CurrentSchemaVersion} 不符");
        }

        return new WorkspaceLoadResult(document, null);
    }

    /// <summary>保存工作区：先写临时文件，再替换原文件。</summary>
    public void Save(WorkspaceDocument document)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        string temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, WorkspaceJson.Options));
        File.Move(temporary, _path, overwrite: true);
    }

    private WorkspaceLoadResult BackUp(string reason)
    {
        string backup = _path + ".bak";
        try
        {
            File.Move(_path, backup, overwrite: true);
        }
        catch (IOException)
        {
            return new WorkspaceLoadResult(WorkspaceDocument.Empty, $"{reason}，且无法改名保留，以空工作区启动。");
        }

        return new WorkspaceLoadResult(WorkspaceDocument.Empty, $"{reason}，已改名为 {System.IO.Path.GetFileName(backup)}，以空工作区启动。");
    }
}

/// <summary>读取结果：工作区内容，以及需要告诉用户的说明（没有问题时为 null）。</summary>
public sealed record WorkspaceLoadResult(WorkspaceDocument Document, string? Notice);
