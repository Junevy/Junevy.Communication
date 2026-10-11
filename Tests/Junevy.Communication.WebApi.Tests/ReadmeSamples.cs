using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Junevy.Communication.WebApi.Tests;

// 本文件中 README-SAMPLE-BEGIN / README-SAMPLE-END 之间的代码，与 Junevy.Communication.WebApi/README.md 里的示例逐字相同。
// ReadmeSampleTests 会检查两者保持一致：改这里就要同步改 README（反之亦然）。

// README-SAMPLE-BEGIN SigningInterceptor
/// <summary>Adds a timestamp and an HMAC signature before every attempt. It runs again for every attempt (retries included), so both are always fresh.</summary>
public sealed class SigningInterceptor : WebApiInterceptor
{
    private readonly byte[] secret;
    private readonly Func<DateTimeOffset> clock;

    public SigningInterceptor(string secret, Func<DateTimeOffset>? clock = null)
    {
        this.secret = Encoding.UTF8.GetBytes(secret);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public override Task OnSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken)
    {
        string timestamp = clock().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var sha256 = SHA256.Create();
        string bodyHash = Convert.ToBase64String(sha256.ComputeHash(context.Body));
        string payload = $"{context.Method}\n{context.RequestUri.PathAndQuery}\n{timestamp}\n{bodyHash}";

        using var hmac = new HMACSHA256(secret);
        string signature = BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", string.Empty).ToLowerInvariant();

        context.Headers["X-Timestamp"] = timestamp;
        context.Headers["X-Signature"] = signature;
        context.MarkSensitiveHeader("X-Signature"); // logs and event arguments show *** instead of the value
        return Task.CompletedTask;
    }
}
// README-SAMPLE-END SigningInterceptor

// README-SAMPLE-BEGIN TokenRefreshInterceptor
/// <summary>Sends the current token with every attempt; on a 401 it refreshes the token and asks for one replay.</summary>
public sealed class TokenRefreshInterceptor : WebApiInterceptor
{
    private const string RefreshedKey = "token-refreshed";

    private readonly Func<CancellationToken, Task<string>> refreshToken;
    private string token;

    public TokenRefreshInterceptor(string initialToken, Func<CancellationToken, Task<string>> refreshToken)
    {
        token = initialToken;
        this.refreshToken = refreshToken;
    }

    public override Task OnSendingAsync(WebApiSendingContext context, CancellationToken cancellationToken)
    {
        context.Headers["Authorization"] = "Bearer " + Volatile.Read(ref token);
        context.MarkSensitiveHeader("Authorization");
        return Task.CompletedTask;
    }

    public override async Task OnReceivedAsync(WebApiReceivedContext context, CancellationToken cancellationToken)
    {
        // Items is shared by all attempts of one call, so a call refreshes the token at most once.
        if (context.StatusCode != 401 || context.Items.ContainsKey(RefreshedKey))
            return;

        Volatile.Write(ref token, await refreshToken(cancellationToken).ConfigureAwait(false));
        context.Items[RefreshedKey] = true;
        context.RequestReplay(); // send again right away; OnSendingAsync runs again and picks up the new token
    }
}
// README-SAMPLE-END TokenRefreshInterceptor
