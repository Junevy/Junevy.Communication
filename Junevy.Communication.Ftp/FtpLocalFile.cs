using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Sdk;

namespace Junevy.Communication.Ftp;

/// <summary>
/// 下载用的本地文件操作（P63-D5、D7）：创建父目录、临时文件名、提交（替换目标）与清理。
/// 下载先写到与目标同目录的临时文件，成功后才替换目标，因此失败时原文件保持不变（Q43）。
/// </summary>
internal static class FtpLocalFile
{
    /// <summary>确保目标的父目录存在。</summary>
    /// <param name="localPath">本地目标路径。</param>
    /// <returns>成功时为 null；无法创建时为 Unspecified 的结果。</returns>
    public static CommResult? EnsureParentDirectory(string localPath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(localPath));
        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FtpErrorMap.LocalFileSystem(ex);
        }
    }

    /// <summary>与目标在同一目录下的临时文件名：<c>&lt;文件名&gt;.&lt;随机串&gt;.part</c>（P63-D7）。</summary>
    /// <param name="localPath">本地目标路径。</param>
    /// <returns>临时文件的完整路径（尚未创建）。</returns>
    public static string TemporaryPathFor(string localPath) =>
        $"{Path.GetFullPath(localPath)}.{Guid.NewGuid():N}.part";

    /// <summary>用下载完成的临时文件替换目标；失败时删除临时文件。</summary>
    /// <param name="temporaryPath">临时文件。</param>
    /// <param name="localPath">目标路径。</param>
    /// <returns>替换成功为 Transferred；否则为 Unspecified 的结果。</returns>
    public static CommResult<FtpTransferStatus> Commit(string temporaryPath, string localPath)
    {
        try
        {
            File.Move(temporaryPath, localPath, overwrite: true);
            return CommResult<FtpTransferStatus>.Success(FtpTransferStatus.Transferred);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporaryPath);
            return FtpErrorMap.LocalFileSystem(ex).As<FtpTransferStatus>();
        }
    }

    /// <summary>删除临时文件。清理是尽力而为的：原文件保持不变是首要目标。</summary>
    /// <param name="path">临时文件。</param>
    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删除失败只会留下一个 .part 文件，不影响结果。
        }
    }
}
