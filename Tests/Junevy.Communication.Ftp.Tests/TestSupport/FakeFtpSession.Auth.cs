using System.Net.Security;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>登录、TLS、功能列表与选项（RFC 959、2228、2389 的子集，只含 FluentFTP 实际发出的命令）。</summary>
internal sealed partial class FakeFtpSession
{
    private async Task UserAsync(string argument, CancellationToken cancellationToken)
    {
        if (server.Options.AllowAnonymous && argument.Equals("anonymous", StringComparison.OrdinalIgnoreCase))
        {
            loggedIn = true;
            await ReplyAsync(230, "Anonymous access granted.", cancellationToken).ConfigureAwait(false);
        }
        else if (argument == server.Options.UserName)
        {
            pendingUser = argument;
            await ReplyAsync(331, "Password required.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            pendingUser = null;
            await ReplyAsync(530, "Login incorrect.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PassAsync(string argument, CancellationToken cancellationToken)
    {
        bool accepted = pendingUser is not null && argument == server.Options.Password;
        pendingUser = null;
        if (accepted)
        {
            loggedIn = true;
            await ReplyAsync(230, "Login successful.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReplyAsync(530, "Login incorrect.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AuthAsync(string argument, CancellationToken cancellationToken)
    {
        if (server.Options.Tls != FakeFtpTls.Explicit || secured || !argument.Equals("TLS", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(502, "AUTH is not supported here.", cancellationToken).ConfigureAwait(false);
            return;
        }

        // 应答必须先于握手发出：客户端收到 234 之后才开始 TLS 握手。
        await ReplyAsync(234, "AUTH TLS successful.", cancellationToken).ConfigureAwait(false);
        await UpgradeToTlsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpgradeToTlsAsync(CancellationToken cancellationToken)
    {
        var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions { ServerCertificate = server.Certificate },
            cancellationToken).ConfigureAwait(false);
        stream = ssl;
        secured = true;
    }

    private async Task ProtAsync(string argument, CancellationToken cancellationToken)
    {
        if (!secured)
        {
            await ReplyAsync(503, "Send AUTH first.", cancellationToken).ConfigureAwait(false);
            return;
        }

        dataProtected = argument.Equals("P", StringComparison.OrdinalIgnoreCase);
        await ReplyAsync(200, $"Protection level set to {argument}.", cancellationToken).ConfigureAwait(false);
    }

    private Task FeatAsync(CancellationToken cancellationToken) =>
        MultilineReplyAsync(211, "Features:", FeatureLines(), cancellationToken);

    private IEnumerable<string> FeatureLines()
    {
        FakeFtpOptions options = server.Options;
        if (options.AdvertiseUtf8)
        {
            yield return "UTF8";
        }

        if (options.AdvertiseMlst)
        {
            yield return "MLST type*;size*;modify*;";
        }

        if (options.AdvertiseSize)
        {
            yield return "SIZE";
        }

        if (options.AdvertiseMdtm)
        {
            yield return "MDTM";
        }

        yield return "PASV";
        yield return "EPSV";

        if (options.Tls == FakeFtpTls.Explicit && !secured)
        {
            yield return "AUTH TLS";
        }

        if (options.Tls != FakeFtpTls.None)
        {
            yield return "PBSZ";
            yield return "PROT";
        }
    }

    private async Task OptsAsync(string argument, CancellationToken cancellationToken)
    {
        if (server.Options.AdvertiseUtf8 && argument.StartsWith("UTF8", StringComparison.OrdinalIgnoreCase))
        {
            await ReplyAsync(200, "UTF8 mode enabled.", cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await ReplyAsync(502, "Option not supported.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PwdAsync(CancellationToken cancellationToken)
    {
        if (await RequireLoginAsync(cancellationToken).ConfigureAwait(false))
        {
            await ReplyAsync(257, $"\"{workingDirectory}\" is the current directory.", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> RequireLoginAsync(CancellationToken cancellationToken)
    {
        if (loggedIn)
        {
            return true;
        }

        await ReplyAsync(530, "Please login with USER and PASS.", cancellationToken).ConfigureAwait(false);
        return false;
    }
}
