using System.Globalization;
using System.Text;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>目录列表：LIST（Unix 风格）与 MLSD（机器可读的 facts，时间为 UTC，精确到秒）。</summary>
internal sealed partial class FakeFtpSession
{
    private const string MlsdTimeFormat = "yyyyMMddHHmmss";

    private async Task ListAsync(string argument, bool machineReadable, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string target = Resolve(WithoutOptions(argument));
        if (!server.FileSystem.DirectoryExists(target))
        {
            await ReplyAsync(550, "No such directory.", cancellationToken).ConfigureAwait(false);
            return;
        }

        var listing = new StringBuilder();
        foreach (FakeFtpEntry entry in server.FileSystem.List(target))
        {
            listing.Append(machineReadable ? MlsdLine(entry) : UnixLine(entry)).Append("\r\n");
        }

        await TransferAsync(server.Options.FileNameEncoding.GetBytes(listing.ToString()), cancellationToken).ConfigureAwait(false);
    }

    // FluentFTP 可能在路径前放选项（例如 -a）；这里去掉以 '-' 开头的单词。
    private static string WithoutOptions(string argument) =>
        string.Join(' ', argument.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(word => !word.StartsWith('-')));

    private static string UnixLine(FakeFtpEntry entry)
    {
        string permissions = entry.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--";
        string date = entry.Modified.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);
        return $"{permissions} 1 owner group {entry.Size,10} {date} {entry.Name}";
    }

    private static string MlsdLine(FakeFtpEntry entry)
    {
        string type = entry.IsDirectory ? "type=dir" : $"type=file;size={entry.Size}";
        string modified = entry.Modified.ToString(MlsdTimeFormat, CultureInfo.InvariantCulture);
        return $"{type};modify={modified}; {entry.Name}";
    }
}
