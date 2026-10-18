using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace Junevy.Communication.Ftp.Tests.TestSupport;

/// <summary>
/// 一条控制连接上的会话：逐条读取并按顺序应答，因此同一连接上的命令严格串行（Q42）。
/// 命令分派在本文件，登录与 TLS 在 <c>FakeFtpSession.Auth.cs</c>，被动模式在 <c>FakeFtpSession.Data.cs</c>。
/// </summary>
internal sealed partial class FakeFtpSession : IDisposable
{
    private readonly FakeFtpServer server;
    private readonly TcpClient tcp;
    private readonly int connectionId;
    private Stream stream;
    private int commandNumber;
    private FakeFtpCommand? current;
    private bool loggedIn;
    private string? pendingUser;
    private bool secured;
    private bool dataProtected;
    private string workingDirectory = "/";
    private TcpListener? passiveListener;

    public FakeFtpSession(FakeFtpServer server, TcpClient tcp, int connectionId)
    {
        this.server = server;
        this.tcp = tcp;
        this.connectionId = connectionId;
        stream = tcp.GetStream();
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (server.Options.Tls == FakeFtpTls.Implicit)
            {
                await UpgradeToTlsAsync(cancellationToken).ConfigureAwait(false);
            }

            await ReplyAsync(220, "Fake FTP server ready.", cancellationToken).ConfigureAwait(false);
            while (await HandleNextCommandAsync(cancellationToken).ConfigureAwait(false))
            {
            }
        }
        catch (Exception ex) when (IsConnectionEnd(ex))
        {
            // 客户端断开、TLS 握手被拒绝或服务器停止：会话结束。
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        passiveListener?.Stop();
        stream.Dispose();
        tcp.Dispose();
    }

    private static bool IsConnectionEnd(Exception exception) =>
        exception is IOException or System.Net.Sockets.SocketException or AuthenticationException or ObjectDisposedException or OperationCanceledException;

    private async Task<bool> HandleNextCommandAsync(CancellationToken cancellationToken)
    {
        string? line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            return false;
        }

        int space = line.IndexOf(' ');
        string verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
        string argument = space < 0 ? string.Empty : line[(space + 1)..];
        commandNumber++;
        current = server.Commands.Received(connectionId, verb, argument);
        try
        {
            bool keepOpen = await ExecuteAsync(verb, argument, cancellationToken).ConfigureAwait(false);
            return keepOpen && !server.Faults.ShouldCloseAfter(commandNumber);
        }
        finally
        {
            current = null;
        }
    }

    private async Task<bool> ExecuteAsync(string verb, string argument, CancellationToken cancellationToken)
    {
        if (server.Faults.TryFailReply(verb, argument, out int faultCode))
        {
            await ReplyAsync(faultCode, "Injected failure.", cancellationToken).ConfigureAwait(false);
            return true;
        }

        TimeSpan delay = server.Faults.ReplyDelay(verb);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return await DispatchAsync(verb, argument, cancellationToken).ConfigureAwait(false);
    }

    // 返回 false 表示结束会话（QUIT）。
    private async Task<bool> DispatchAsync(string verb, string argument, CancellationToken cancellationToken)
    {
        switch (verb)
        {
            case "USER":
                await UserAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "PASS":
                await PassAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "AUTH":
                await AuthAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "PBSZ":
                await ReplyAsync(200, "PBSZ=0", cancellationToken).ConfigureAwait(false);
                return true;
            case "PROT":
                await ProtAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "FEAT":
                await FeatAsync(cancellationToken).ConfigureAwait(false);
                return true;
            case "OPTS":
                await OptsAsync(argument, cancellationToken).ConfigureAwait(false);
                return true;
            case "SYST":
                await ReplyAsync(215, "UNIX Type: L8", cancellationToken).ConfigureAwait(false);
                return true;
            case "TYPE":
                await ReplyAsync(200, $"Type set to {argument}.", cancellationToken).ConfigureAwait(false);
                return true;
            case "NOOP":
                await ReplyAsync(200, "NOOP command successful.", cancellationToken).ConfigureAwait(false);
                return true;
            case "PWD":
                await PwdAsync(cancellationToken).ConfigureAwait(false);
                return true;
            case "PASV":
                await PassiveAsync(extended: false, cancellationToken).ConfigureAwait(false);
                return true;
            case "EPSV":
                await PassiveAsync(extended: true, cancellationToken).ConfigureAwait(false);
                return true;
            case "QUIT":
                await ReplyAsync(221, "Goodbye.", cancellationToken).ConfigureAwait(false);
                return false;
            default:
                return await DispatchFileCommandAsync(verb, argument, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            if (one[0] == '\n')
            {
                break;
            }

            bytes.Add(one[0]);
        }

        return server.Options.FileNameEncoding.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    private Task ReplyAsync(int code, string text, CancellationToken cancellationToken) =>
        SendAsync(code, $"{code} {text}\r\n", cancellationToken);

    private async Task MultilineReplyAsync(int code, string header, IEnumerable<string> body, CancellationToken cancellationToken)
    {
        var text = new System.Text.StringBuilder().Append(code).Append('-').Append(header).Append("\r\n");
        foreach (string line in body)
        {
            text.Append(' ').Append(line).Append("\r\n");
        }

        text.Append(code).Append(" End\r\n");
        await SendAsync(code, text.ToString(), cancellationToken).ConfigureAwait(false);
    }

    private async Task SendAsync(int code, string raw, CancellationToken cancellationToken)
    {
        byte[] bytes = server.Options.FileNameEncoding.GetBytes(raw);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // 一条命令可能有多个应答（150 与 226）：记录最后一个，它标志命令完成（Q42 据此检查串行）。
        if (current is not null)
        {
            current.ReplyCode = code;
            current.RepliedAt = DateTime.UtcNow;
        }
    }
}
