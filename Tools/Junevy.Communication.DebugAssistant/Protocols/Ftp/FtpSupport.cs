using System.Globalization;
using Junevy.Communication.Ftp;

namespace Junevy.Communication.DebugAssistant.Protocols.Ftp;

/// <summary>
/// FTP 页的纯逻辑：远程路径的拼接与上级目录、目录条目的显示行、传输结果的文字（不依赖界面，便于单元测试）。
/// 远程路径用 "/" 分隔；根目录是 "/"。
/// </summary>
public static class FtpSupport
{
    /// <summary>目录与名称拼成远程路径。名称可以是多级相对路径（例如 <c>a/b</c>），库会逐级创建。</summary>
    public static string Combine(string directory, string name)
    {
        string trimmedName = name.Trim('/');
        string trimmedDirectory = directory.TrimEnd('/');
        return trimmedName.Length == 0 ? Normalize(directory) : $"{trimmedDirectory}/{trimmedName}";
    }

    /// <summary>上级目录；根目录的上级仍是根目录。</summary>
    public static string Parent(string path)
    {
        string trimmed = path.TrimEnd('/');
        int index = trimmed.LastIndexOf('/');
        return index <= 0 ? "/" : trimmed[..index];
    }

    /// <summary>规范化为以 "/" 开头、没有结尾斜杠的远程路径（根目录为 "/"）。</summary>
    public static string Normalize(string path)
    {
        string trimmed = path.Trim().Trim('/');
        return trimmed.Length == 0 ? "/" : "/" + trimmed;
    }

    /// <summary>目录条目的显示行。修改时间是服务器报告的值，不做时区转换（计划 7.12）。</summary>
    public static FtpEntryRow ToRow(FtpEntry entry) => new(
        entry.Name,
        entry.FullName,
        EntryTypeText(entry.Type),
        entry.Size is { } size ? size.ToString(CultureInfo.InvariantCulture) : "—",
        entry.Modified is { } modified ? modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "—",
        entry.Type == FtpEntryType.Directory);

    public static string EntryTypeText(FtpEntryType type) => type switch
    {
        FtpEntryType.File => "文件",
        FtpEntryType.Directory => "目录",
        FtpEntryType.Link => "链接",
        _ => type.ToString(),
    };

    /// <summary>传输结果的文字：传输完成，或目标已存在而跳过。</summary>
    public static string TransferText(FtpTransferStatus status, string target) => status switch
    {
        FtpTransferStatus.Transferred => $"已传输：{target}",
        FtpTransferStatus.Skipped => $"目标已存在，已跳过：{target}",
        _ => status.ToString(),
    };

    /// <summary>进度的文字：有百分比时显示百分比，否则显示已传输的字节数。</summary>
    public static string ProgressText(FtpTransferProgress progress) =>
        progress.Percent is { } percent
            ? $"{percent.ToString("0", CultureInfo.InvariantCulture)}%"
            : $"{progress.TransferredBytes.ToString(CultureInfo.InvariantCulture)} 字节";

    /// <summary>可选的文本：空白表示没有。</summary>
    public static string? Optional(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
