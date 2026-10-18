using FluentFTP;

namespace Junevy.Communication.Ftp.Sdk;

/// <summary>
/// SDK 的列目录条目（<c>FtpListItem</c>）到 <see cref="FtpEntry"/> 的映射（P63-D9）。
/// SDK 用 -1 表示大小未知、用 <see cref="DateTime.MinValue"/> 表示时间未知，这两个哨兵在这里转换为 null，不让它们进入公开类型。
/// </summary>
internal static class FtpSdkEntryMap
{
    /// <summary>映射一个条目。时间原样采用，不做时区换算（SDK 的 TimeConversion 保持 ServerTime）。</summary>
    /// <param name="item">SDK 的条目；不能为 null。</param>
    /// <returns>本库的条目。</returns>
    public static FtpEntry ToEntry(FtpListItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        FtpEntryType type = ToEntryType(item.Type);
        long? size = type == FtpEntryType.Directory || item.Size < 0 ? null : item.Size;
        DateTime? modified = item.Modified == DateTime.MinValue ? null : item.Modified;

        return new FtpEntry(item.Name, item.FullName, type, size, modified);
    }

    private static FtpEntryType ToEntryType(FtpObjectType type) => type switch
    {
        FtpObjectType.Directory => FtpEntryType.Directory,
        FtpObjectType.Link => FtpEntryType.Link,
        _ => FtpEntryType.File,
    };
}
