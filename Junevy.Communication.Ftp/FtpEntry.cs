namespace Junevy.Communication.Ftp;

/// <summary>
/// 远程目录中的一个条目（不可变）。由列目录操作返回，不含 <c>.</c> 与 <c>..</c>。
/// </summary>
public sealed class FtpEntry
{
    internal FtpEntry(string name, string fullName, FtpEntryType type, long? size, DateTime? modified)
    {
        Name = name;
        FullName = fullName;
        Type = type;
        Size = size;
        Modified = modified;
    }

    /// <summary>条目名称（不含目录部分）。</summary>
    public string Name { get; }

    /// <summary>服务器上的完整路径。</summary>
    public string FullName { get; }

    /// <summary>条目类型。</summary>
    public FtpEntryType Type { get; }

    /// <summary>文件大小（字节）；目录或服务器未报告大小时为 null。</summary>
    public long? Size { get; }

    /// <summary>
    /// 服务器报告的修改时间，原样采用、不做时区换算（MLSD 为 UTC，LIST 通常是服务器本地时间，见 README）；未知时为 null。
    /// </summary>
    public DateTime? Modified { get; }
}
