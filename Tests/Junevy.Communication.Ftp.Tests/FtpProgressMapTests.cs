using FluentFTP;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// SDK 的传输进度到 <see cref="FtpTransferProgress"/> 的映射（P63-D11）：SDK 用 -1 表示不确定，映射为 null。
/// </summary>
public sealed class FtpProgressMapTests
{
    [Fact]
    public void KnownTotal_ReportsTheBytesAndThePercentage()
    {
        var progress = new FtpProgress(50.0, 512, 0, TimeSpan.Zero, "local.bin", "/remote.bin", null);

        FtpTransferProgress mapped = FtpSdkProgressMap.ToTransferProgress(progress);

        Assert.Equal(512, mapped.TransferredBytes);
        Assert.Equal(50.0, mapped.Percent);
    }

    [Fact]
    public void Indeterminate_HasNoPercentage()
    {
        var progress = new FtpProgress(-1, 512, 0, TimeSpan.Zero, "local.bin", "/remote.bin", null);

        Assert.Null(FtpSdkProgressMap.ToTransferProgress(progress).Percent);
    }

    [Fact]
    public void Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FtpSdkProgressMap.ToTransferProgress(null!));
    }
}
