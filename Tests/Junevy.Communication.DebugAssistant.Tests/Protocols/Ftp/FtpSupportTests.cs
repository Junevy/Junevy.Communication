using Junevy.Communication.DebugAssistant.Protocols.Ftp;
using Junevy.Communication.Ftp;

namespace Junevy.Communication.DebugAssistant.Tests.Protocols.Ftp;

/// <summary>FTP 页的纯逻辑（计划第 7.12 节）：远程路径的拼接与上级目录，传输结果与目录类型的文字。</summary>
public sealed class FtpSupportTests
{
    [Fact]
    public void Combine_JoinsDirectoryAndName_WithoutDoubledSlashes()
    {
        Assert.Equal(("/a", "/x/y/z", "/"), (FtpSupport.Combine("/", "a"), FtpSupport.Combine("/x/", "/y/z/"), FtpSupport.Combine("/", "")));
    }

    [Fact]
    public void Parent_OfTheRoot_IsTheRoot()
    {
        Assert.Equal(("/a", "/", "/"), (FtpSupport.Parent("/a/b"), FtpSupport.Parent("/a"), FtpSupport.Parent("/")));
    }

    [Fact]
    public void Normalize_StartsWithASlash_AndDropsTheTrailingOne()
    {
        Assert.Equal(("/docs", "/"), (FtpSupport.Normalize("docs/"), FtpSupport.Normalize(string.Empty)));
    }

    [Fact]
    public void TransferText_SaysWhetherTheTargetWasSkipped()
    {
        Assert.Equal(("已传输：/x.bin", "目标已存在，已跳过：/x.bin"), (FtpSupport.TransferText(FtpTransferStatus.Transferred, "/x.bin"), FtpSupport.TransferText(FtpTransferStatus.Skipped, "/x.bin")));
    }

    [Fact]
    public void EntryTypes_HaveChineseNames()
    {
        Assert.Equal(("文件", "目录", "链接"), (FtpSupport.EntryTypeText(FtpEntryType.File), FtpSupport.EntryTypeText(FtpEntryType.Directory), FtpSupport.EntryTypeText(FtpEntryType.Link)));
    }
}
