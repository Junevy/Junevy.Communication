using FluentFTP;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// SDK 列目录条目到 <see cref="FtpEntry"/> 的映射（P63-D9）：类型、大小（目录为 null）、修改时间（未知为 null）。
/// </summary>
public sealed class FtpEntryMapTests
{
    private static readonly DateTime KnownTime = new(2026, 10, 12, 8, 30, 0);

    [Fact]
    public void File_KeepsNameSizeAndModifiedTime()
    {
        var item = new FtpListItem
        {
            Name = "recipe.txt",
            FullName = "/recipes/recipe.txt",
            Type = FtpObjectType.File,
            Size = 12,
            Modified = KnownTime,
        };

        FtpEntry entry = FtpSdkEntryMap.ToEntry(item);

        Assert.Equal("recipe.txt", entry.Name);
        Assert.Equal("/recipes/recipe.txt", entry.FullName);
        Assert.Equal(FtpEntryType.File, entry.Type);
        Assert.Equal(12, entry.Size);
        Assert.Equal(KnownTime, entry.Modified);
    }

    [Fact]
    public void Directory_HasNoSize_EvenWhenTheServerReportsOne()
    {
        var item = new FtpListItem { Name = "archive", FullName = "/archive", Type = FtpObjectType.Directory, Size = 4096, Modified = KnownTime };

        FtpEntry entry = FtpSdkEntryMap.ToEntry(item);

        Assert.Equal(FtpEntryType.Directory, entry.Type);
        Assert.Null(entry.Size);
    }

    [Fact]
    public void Link_IsALinkAndKeepsItsKnownSize()
    {
        var item = new FtpListItem { Name = "current", FullName = "/current", Type = FtpObjectType.Link, Size = 7, Modified = KnownTime };

        FtpEntry entry = FtpSdkEntryMap.ToEntry(item);

        Assert.Equal(FtpEntryType.Link, entry.Type);
        Assert.Equal(7, entry.Size);
    }

    [Fact]
    public void UnknownSize_IsNull()
    {
        // FluentFTP 用 -1 表示服务器没有报告大小（FtpListItem.Size 的默认值）。
        var item = new FtpListItem { Name = "a.bin", FullName = "/a.bin", Type = FtpObjectType.File, Size = -1, Modified = KnownTime };

        Assert.Null(FtpSdkEntryMap.ToEntry(item).Size);
    }

    [Fact]
    public void UnknownModifiedTime_IsNull()
    {
        // 默认的 DateTime.MinValue 表示服务器没有报告时间。
        var item = new FtpListItem { Name = "a.bin", FullName = "/a.bin", Type = FtpObjectType.File, Size = 1, Modified = default };

        Assert.Null(FtpSdkEntryMap.ToEntry(item).Modified);
    }

    [Fact]
    public void ModifiedTime_IsKeptAsReported_WithoutConversion()
    {
        var reported = new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc);
        var item = new FtpListItem { Name = "a.bin", FullName = "/a.bin", Type = FtpObjectType.File, Size = 1, Modified = reported };

        Assert.Equal(reported, FtpSdkEntryMap.ToEntry(item).Modified);
    }
}
