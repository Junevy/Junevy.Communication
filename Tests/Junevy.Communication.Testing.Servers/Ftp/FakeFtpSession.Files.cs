using System.Globalization;

namespace Junevy.Communication.Testing.Servers;

/// <summary>文件与目录命令（RFC 959 的子集）：CWD、MKD、RMD、DELE、RNFR/RNTO、SIZE、MDTM。</summary>
internal sealed partial class FakeFtpSession
{
    private const string MdtmFormat = "yyyyMMddHHmmss";

    private string? renameFrom;

    // 文件与目录命令的分派；未知的命令返回 502。
    private async Task<bool> DispatchFileCommandAsync(string verb, string argument, CancellationToken cancellationToken)
    {
        switch (verb)
        {
            case "CWD":
                await ChangeDirectoryAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "MKD":
                await MakeDirectoryAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "RMD":
                await RemoveDirectoryAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "DELE":
                await DeleteFileAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "RNFR":
                await RenameFromAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "RNTO":
                await RenameToAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "SIZE" when server.Options.AdvertiseSize:
                await SizeAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "MDTM" when server.Options.AdvertiseMdtm:
                await MdtmAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "LIST":
                await ListAsync(argument, machineReadable: false, cancellationToken).ConfigureAwait(false);
                return true;
            case "MLSD":
                await ListAsync(argument, machineReadable: true, cancellationToken).ConfigureAwait(false);
                return true;
            case "RETR":
                await RetrieveAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "STOR":
                await StoreAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            default:
                await ReplyAsync(502, "Command not implemented.", cancellationToken).ConfigureAwait(false);
                return true;
        }
    }

    private async Task ChangeDirectoryAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string target = Resolve(argument);
        if (server.FileSystem.DirectoryExists(target))
        {
            workingDirectory = target;
            await ReplyAsync(250, "Directory successfully changed.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReplyAsync(550, "No such directory.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MakeDirectoryAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string target = Resolve(argument);
        FakeFtpFileSystem fileSystem = server.FileSystem;
        if (fileSystem.DirectoryExists(target) || fileSystem.FileExists(target))
        {
            await ReplyAsync(550, "The name already exists.", cancellationToken).ConfigureAwait(false);
        }
        else if (!fileSystem.DirectoryExists(FakeFtpFileSystem.ParentOf(target)))
        {
            await ReplyAsync(550, "The parent directory does not exist.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            fileSystem.CreateDirectory(target);
            await ReplyAsync(257, $"\"{target}\" created.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RemoveDirectoryAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        bool removed = server.FileSystem.DeleteDirectory(Resolve(argument), recursive: false);
        await ReplyAsync(removed ? 250 : 550, removed ? "Directory removed." : "Cannot remove the directory.", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task DeleteFileAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        bool deleted = server.FileSystem.DeleteFile(Resolve(argument));
        await ReplyAsync(deleted ? 250 : 550, deleted ? "File deleted." : "Cannot delete the file.", cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RenameFromAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string target = Resolve(argument);
        renameFrom = null;
        if (server.FileSystem.FileExists(target) || server.FileSystem.DirectoryExists(target))
        {
            renameFrom = target;
            await ReplyAsync(350, "Ready for RNTO.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReplyAsync(550, "The source does not exist.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RenameToAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string? source = renameFrom;
        renameFrom = null;
        if (source is null)
        {
            await ReplyAsync(503, "RNFR is required first.", cancellationToken).ConfigureAwait(false);
            return;
        }

        bool renamed = server.FileSystem.Rename(source, Resolve(argument));
        await ReplyAsync(renamed ? 250 : 550, renamed ? "Rename successful." : "Rename failed.", cancellationToken).ConfigureAwait(false);
    }

    private async Task SizeAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string target = Resolve(argument);
        if (server.FileSystem.FileExists(target))
        {
            long size = server.FileSystem.ReadFile(target).Length;
            await ReplyAsync(213, size.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReplyAsync(550, "The file does not exist.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task MdtmAsync(string argument, CancellationToken cancellationToken)
    {
        if (!await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string target = Resolve(argument);
        if (server.FileSystem.FileExists(target))
        {
            string modified = server.FileSystem.ModifiedTime(target).ToString(MdtmFormat, CultureInfo.InvariantCulture);
            await ReplyAsync(213, modified, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReplyAsync(550, "The file does not exist.", cancellationToken).ConfigureAwait(false);
        }
    }

    // 相对路径相对于当前目录；. 与 .. 按段折叠；结果总是绝对路径。
    private string Resolve(string path)
    {
        string combined = path.StartsWith('/') ? path : workingDirectory.TrimEnd('/') + "/" + path;
        var segments = new List<string>();
        foreach (string segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment != ".")
            {
                segments.Add(segment);
            }
        }

        return "/" + string.Join('/', segments);
    }
}
