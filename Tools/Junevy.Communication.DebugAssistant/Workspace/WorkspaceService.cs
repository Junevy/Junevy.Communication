using System.IO;
using System.Text.Json;
using Junevy.Communication.DebugAssistant.Sessions;
using Junevy.Communication.DebugAssistant.Shell;

namespace Junevy.Communication.DebugAssistant.Workspace;

/// <summary>在页签与工作区内容之间转换，并读写单个页签的导入、导出文件（计划 Task 3）。</summary>
public sealed class WorkspaceService
{
    public WorkspaceService(WorkspaceStore store)
    {
        Store = store;
    }

    public WorkspaceStore Store { get; }

    /// <summary>把当前页签收集成工作区内容。</summary>
    public static WorkspaceDocument Capture(IReadOnlyList<SessionViewModel> sessions, SessionViewModel? selected)
    {
        WorkspaceSession[] items = sessions
            .Select(session => new WorkspaceSession(session.Kind.ToString(), session.Title, session.CaptureSettings()))
            .ToArray();
        int index = selected is null ? -1 : IndexOf(sessions, selected);
        return new WorkspaceDocument(WorkspaceDocument.CurrentSchemaVersion, items, index < 0 ? null : index);
    }

    /// <summary>
    /// 把工作区内容恢复为页签。页签一律未连接（计划 DA-D9）。未知的协议种类与无法应用的参数被跳过，并在
    /// <see cref="WorkspaceRestoreResult.Notices"/> 中说明。
    /// </summary>
    public static WorkspaceRestoreResult Restore(WorkspaceDocument document, ISessionFactory sessions)
    {
        var restored = new List<SessionViewModel>();
        var notices = new List<string>();
        int? selectedIndex = null;

        for (int savedIndex = 0; savedIndex < document.Sessions.Count; savedIndex++)
        {
            WorkspaceSession saved = document.Sessions[savedIndex];
            SessionViewModel? session = TryRestore(saved, sessions, notices);
            if (session is null)
            {
                continue;
            }

            if (savedIndex == document.SelectedIndex)
            {
                selectedIndex = restored.Count;
            }

            restored.Add(session);
        }

        return new WorkspaceRestoreResult(restored, selectedIndex, notices);
    }

    /// <summary>导出单个页签：一个只含该页签的工作区文件。</summary>
    public static WorkspaceDocument ToSingleSessionDocument(SessionViewModel session) =>
        new(WorkspaceDocument.CurrentSchemaVersion, [new WorkspaceSession(session.Kind.ToString(), session.Title, session.CaptureSettings())], 0);

    /// <summary>读取导入文件（一个页签）。格式错误或版本不符时抛出 <see cref="JsonException"/> 或 <see cref="InvalidDataException"/>。</summary>
    public static WorkspaceDocument ReadFile(string path)
    {
        WorkspaceDocument document = JsonSerializer.Deserialize<WorkspaceDocument>(File.ReadAllText(path), WorkspaceJson.Options)
            ?? throw new JsonException("The file is empty.");
        if (document.SchemaVersion != WorkspaceDocument.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"The file version {document.SchemaVersion} does not match {WorkspaceDocument.CurrentSchemaVersion}.");
        }

        return document;
    }

    /// <summary>写出导出文件。</summary>
    public static void WriteFile(string path, WorkspaceDocument document) =>
        File.WriteAllText(path, JsonSerializer.Serialize(document, WorkspaceJson.Options));

    private static SessionViewModel? TryRestore(WorkspaceSession saved, ISessionFactory sessions, List<string> notices)
    {
        if (!Enum.TryParse(saved.Kind, out ProtocolKind kind) || !Enum.IsDefined(kind))
        {
            notices.Add($"已跳过未知的协议种类：{saved.Kind}。");
            return null;
        }

        if (saved.Settings is null)
        {
            notices.Add($"已跳过 {saved.Title}：缺少参数。");
            return null;
        }

        SessionViewModel session = sessions.Create(kind);
        session.Title = saved.Title;
        try
        {
            session.ApplySettings(saved.Settings);
        }
        catch (ArgumentException)
        {
            notices.Add($"已跳过 {saved.Title}：参数与协议不符。");
            return null;
        }

        return session;
    }

    private static int IndexOf(IReadOnlyList<SessionViewModel> sessions, SessionViewModel session)
    {
        for (int i = 0; i < sessions.Count; i++)
        {
            if (ReferenceEquals(sessions[i], session))
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>恢复结果：恢复出的页签、应选中的下标（没有时为 null），以及需要告诉用户的说明。</summary>
public sealed record WorkspaceRestoreResult(
    IReadOnlyList<SessionViewModel> Sessions,
    int? SelectedIndex,
    IReadOnlyList<string> Notices);
