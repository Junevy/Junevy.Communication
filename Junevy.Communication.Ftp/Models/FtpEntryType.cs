namespace Junevy.Communication.Ftp;

/// <summary>目录条目的类型。</summary>
public enum FtpEntryType
{
    /// <summary>普通文件。</summary>
    File,

    /// <summary>目录。</summary>
    Directory,

    /// <summary>符号链接。</summary>
    Link,
}
