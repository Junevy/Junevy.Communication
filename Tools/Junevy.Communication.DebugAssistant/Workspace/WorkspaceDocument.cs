namespace Junevy.Communication.DebugAssistant.Workspace;

/// <summary>工作区文件的内容（计划 DA-D11）：打开的页签按顺序排列，以及当前选中的页签。</summary>
/// <param name="SchemaVersion">文件格式版本，当前为 <see cref="CurrentSchemaVersion"/>。</param>
/// <param name="Sessions">页签列表，按打开的顺序。</param>
/// <param name="SelectedIndex">当前选中的页签下标，没有时为 null。</param>
public sealed record WorkspaceDocument(int SchemaVersion, IReadOnlyList<WorkspaceSession> Sessions, int? SelectedIndex)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>空工作区。</summary>
    public static WorkspaceDocument Empty { get; } = new(CurrentSchemaVersion, [], null);
}

/// <summary>一个页签的保存内容。<see cref="Kind"/> 保存为文本：遇到不认识的种类时可以跳过，而不是整个文件失败。</summary>
public sealed record WorkspaceSession(string Kind, string Title, SessionSettings? Settings);
