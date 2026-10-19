using System.Text.Json;

namespace Junevy.Communication.DebugAssistant.Workspace;

/// <summary>工作区文件的 JSON 选项。只在这里配置，不设置全局的 <see cref="JsonSerializerOptions"/>。</summary>
internal static class WorkspaceJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}
