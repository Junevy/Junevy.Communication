namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>模拟服务器的一个条目（目录列表用）。</summary>
public sealed record FakeFtpEntry(string Name, bool IsDirectory, long Size, DateTime Modified);

/// <summary>
/// 模拟服务器的内存文件系统：目录、文件内容与修改时间（计划 Task 3 第 1 条）。
/// 路径为绝对路径，分隔符为 <c>/</c>。线程安全。
/// </summary>
public sealed class FakeFtpFileSystem
{
    private readonly object sync = new();
    private readonly FakeFtpNode root = FakeFtpNode.Directory(DateTime.UtcNow);

    /// <summary>目录是否存在。</summary>
    public bool DirectoryExists(string path)
    {
        lock (sync)
        {
            return Find(path) is { IsDirectory: true };
        }
    }

    /// <summary>文件是否存在。</summary>
    public bool FileExists(string path)
    {
        lock (sync)
        {
            return Find(path) is { IsDirectory: false };
        }
    }

    /// <summary>逐级创建目录；已经存在的段保持不变。</summary>
    /// <param name="path">目录的绝对路径。</param>
    public void CreateDirectory(string path)
    {
        lock (sync)
        {
            FakeFtpNode current = root;
            foreach (string segment in Segments(path))
            {
                if (!current.Children.TryGetValue(segment, out FakeFtpNode? child))
                {
                    child = FakeFtpNode.Directory(DateTime.UtcNow);
                    current.Children[segment] = child;
                }

                current = child.IsDirectory ? child : throw new InvalidOperationException($"{segment} is a file.");
            }
        }
    }

    /// <summary>写入一个文件（父目录必须存在）。</summary>
    /// <param name="path">文件的绝对路径。</param>
    /// <param name="content">文件内容。</param>
    /// <param name="modified">修改时间。</param>
    public void WriteFile(string path, byte[] content, DateTime modified)
    {
        lock (sync)
        {
            FakeFtpNode parent = ParentNode(path);
            parent.Children[NameOf(path)] = FakeFtpNode.File(content, modified);
        }
    }

    /// <summary>读取文件内容。</summary>
    /// <param name="path">文件的绝对路径。</param>
    /// <returns>内容的副本。</returns>
    public byte[] ReadFile(string path)
    {
        lock (sync)
        {
            return (Find(path) ?? throw new FileNotFoundException(path)).Content.ToArray();
        }
    }

    /// <summary>文件的修改时间。</summary>
    /// <param name="path">文件的绝对路径。</param>
    public DateTime ModifiedTime(string path)
    {
        lock (sync)
        {
            return (Find(path) ?? throw new FileNotFoundException(path)).Modified;
        }
    }

    /// <summary>列出目录的直接子条目，按名称排序。</summary>
    /// <param name="path">目录的绝对路径。</param>
    /// <returns>条目列表（不含 <c>.</c> 与 <c>..</c>）。</returns>
    public IReadOnlyList<FakeFtpEntry> List(string path)
    {
        lock (sync)
        {
            FakeFtpNode directory = Find(path) is { IsDirectory: true } found ? found : throw new DirectoryNotFoundException(path);
            return directory.Children
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new FakeFtpEntry(pair.Key, pair.Value.IsDirectory, pair.Value.Content.Length, pair.Value.Modified))
                .ToList();
        }
    }

    /// <summary>删除文件。</summary>
    /// <param name="path">文件的绝对路径。</param>
    /// <returns>删除了则为 true；不存在或是目录则为 false。</returns>
    public bool DeleteFile(string path)
    {
        lock (sync)
        {
            FakeFtpNode? parent = Find(ParentOf(path));
            return parent is { IsDirectory: true } && parent.Children.TryGetValue(NameOf(path), out FakeFtpNode? node)
                && !node.IsDirectory && parent.Children.Remove(NameOf(path));
        }
    }

    /// <summary>删除目录；非递归时目录必须为空。</summary>
    /// <param name="path">目录的绝对路径。</param>
    /// <param name="recursive">为 true 时连同子条目一起删除。</param>
    /// <returns>删除了则为 true；不存在、不是目录，或非递归且非空，则为 false。</returns>
    public bool DeleteDirectory(string path, bool recursive)
    {
        lock (sync)
        {
            FakeFtpNode? parent = Find(ParentOf(path));
            if (parent is not { IsDirectory: true } || !parent.Children.TryGetValue(NameOf(path), out FakeFtpNode? node) || !node.IsDirectory)
            {
                return false;
            }

            if (node.Children.Count > 0 && !recursive)
            {
                return false;
            }

            return parent.Children.Remove(NameOf(path));
        }
    }

    /// <summary>重命名或移动文件与目录；源必须存在，目标不得存在，目标的父目录必须存在。</summary>
    /// <param name="from">源路径。</param>
    /// <param name="to">目标路径。</param>
    /// <returns>成功为 true。</returns>
    public bool Rename(string from, string to)
    {
        lock (sync)
        {
            FakeFtpNode? source = Find(from);
            FakeFtpNode? targetParent = Find(ParentOf(to));
            if (source is null || targetParent is not { IsDirectory: true } || Find(to) is not null)
            {
                return false;
            }

            FakeFtpNode? sourceParent = Find(ParentOf(from));
            sourceParent!.Children.Remove(NameOf(from));
            targetParent.Children[NameOf(to)] = source;
            return true;
        }
    }

    /// <summary>路径的父目录（根目录的父目录是根目录）。</summary>
    /// <param name="path">绝对路径。</param>
    /// <returns>父目录的绝对路径。</returns>
    public static string ParentOf(string path)
    {
        string[] segments = Segments(path);
        return segments.Length <= 1 ? "/" : "/" + string.Join('/', segments[..^1]);
    }

    /// <summary>路径的最后一段（根目录为空字符串）。</summary>
    /// <param name="path">绝对路径。</param>
    /// <returns>名称。</returns>
    public static string NameOf(string path)
    {
        string[] segments = Segments(path);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }

    private FakeFtpNode? Find(string path)
    {
        FakeFtpNode? current = root;
        foreach (string segment in Segments(path))
        {
            if (current is not { IsDirectory: true } || !current.Children.TryGetValue(segment, out current))
            {
                return null;
            }
        }

        return current;
    }

    private FakeFtpNode ParentNode(string path)
    {
        FakeFtpNode? parent = Find(ParentOf(path));
        return parent is { IsDirectory: true } ? parent : throw new DirectoryNotFoundException(ParentOf(path));
    }

    private static string[] Segments(string path) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries);

}

internal sealed class FakeFtpNode
{
    private FakeFtpNode(bool isDirectory, byte[] content, DateTime modified)
    {
        IsDirectory = isDirectory;
        Content = content;
        Modified = modified;
    }

    public bool IsDirectory { get; }

    public byte[] Content { get; }

    public DateTime Modified { get; }

    public Dictionary<string, FakeFtpNode> Children { get; } = new(StringComparer.Ordinal);

    public static FakeFtpNode Directory(DateTime modified) => new(isDirectory: true, Array.Empty<byte>(), modified);

    public static FakeFtpNode File(byte[] content, DateTime modified) => new(isDirectory: false, content, modified);
}
