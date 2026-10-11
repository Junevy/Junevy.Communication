using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Rest;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 包 README 里的两个示例拦截器（<see cref="SigningInterceptor"/> 与 <see cref="TokenRefreshInterceptor"/>）：
/// 用真实的客户端和 <see cref="StubHttpHandler"/> 跑一遍，并检查 README 里的代码与 <c>ReadmeSamples.cs</c> 逐字一致。
/// </summary>
public sealed class ReadmeSampleTests
{
    private const string Secret = "readme-secret";

    [Fact]
    public async Task SigningInterceptor_AddsATimestampAndAVerifiableSignature()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        var signer = new SigningInterceptor(Secret, () => now);
        await using var rig = new RestRig(configureComponents: components => components.Interceptors.Add(signer), script: handler => handler.Enqueue(200, "{}"));

        WebApiResult<string> result = await rig.Client.PostAsync<string>("orders?id=7", new { OrderNo = "A-1" });

        Assert.True(result.IsSuccess);
        StubRequest sent = rig.Handler.Requests.Single();
        Assert.Equal("1760000000", sent.Headers["X-Timestamp"]);
        Assert.Equal(ExpectedSignature("POST", "/api/orders?id=7", "1760000000", sent.Body), sent.Headers["X-Signature"]);
    }

    [Fact]
    public async Task SigningInterceptor_RunsAgainOnEveryAttempt_SoARetryCarriesAFreshSignature()
    {
        var clock = new List<DateTimeOffset> { DateTimeOffset.FromUnixTimeSeconds(1000), DateTimeOffset.FromUnixTimeSeconds(1005) };
        int calls = 0;
        var signer = new SigningInterceptor(Secret, () => clock[calls++]);
        await using var rig = new RestRig(c => c.Retry.RetryCount = 1, components => components.Interceptors.Add(signer),
                                          handler => handler.Enqueue(503).Enqueue(200, "{}"));

        WebApiResult<string> result = await rig.Client.GetAsync<string>("orders");

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "1000", "1005" }, rig.Handler.Requests.Select(request => request.Headers["X-Timestamp"]));
        Assert.NotEqual(rig.Handler.Requests[0].Headers["X-Signature"], rig.Handler.Requests[1].Headers["X-Signature"]);
    }

    [Fact]
    public async Task SigningInterceptor_TheSignatureIsMaskedInEventsAndLogs()
    {
        var signer = new SigningInterceptor(Secret, () => DateTimeOffset.FromUnixTimeSeconds(1));
        await using var rig = new RestRig(configureComponents: components => components.Interceptors.Add(signer), script: handler => handler.Enqueue(200, "{}"));

        await rig.Client.GetAsync<string>("orders");

        Assert.Equal("***", rig.Sending.Single().Headers["X-Signature"]);
        Assert.DoesNotContain(rig.Logger.Entries, entry => entry.Message.Contains(rig.Handler.Requests.Single().Headers["X-Signature"]));
    }

    [Fact]
    public async Task TokenRefreshInterceptor_On401_RefreshesTheTokenAndReplaysThePostOnce()
    {
        int refreshes = 0;
        var interceptor = new TokenRefreshInterceptor("old-token", _ => Task.FromResult("new-token-" + ++refreshes));
        await using var rig = new RestRig(configureComponents: components => components.Interceptors.Add(interceptor),
                                          script: handler => handler.Enqueue(401).Enqueue(200, "{}"));

        WebApiResult<string> result = await rig.Client.PostAsync<string>("report", new { A = 1 });

        Assert.True(result.IsSuccess);
        Assert.Equal(1, refreshes);
        Assert.Equal("Bearer old-token", rig.Handler.Requests[0].Headers["Authorization"]);
        Assert.Equal("Bearer new-token-1", rig.Handler.Requests[1].Headers["Authorization"]);
        Assert.Equal(2, rig.Sending.Count);
        Assert.Equal(new[] { true, false }, rig.Responses.Select(response => response.WillRetry));
        Assert.Equal(2, Assert.Single(rig.Completed).Attempts);
    }

    [Fact]
    public async Task TokenRefreshInterceptor_AStillRejectedToken_IsNotRefreshedTwiceInOneCall()
    {
        int refreshes = 0;
        var interceptor = new TokenRefreshInterceptor("old-token", _ => Task.FromResult("new-token-" + ++refreshes));
        await using var rig = new RestRig(configureComponents: components => components.Interceptors.Add(interceptor),
                                          script: handler => handler.Enqueue(401).Enqueue(401).Enqueue(200, "{}"));

        WebApiResult<string> result = await rig.Client.GetAsync<string>("orders");

        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal(1, refreshes);
        Assert.Equal(2, rig.Handler.Requests.Count);
    }

    [Fact]
    public async Task TokenRefreshInterceptor_AValidToken_IsNeverRefreshed()
    {
        int refreshes = 0;
        var interceptor = new TokenRefreshInterceptor("good", _ => Task.FromResult("x" + ++refreshes));
        await using var rig = new RestRig(configureComponents: components => components.Interceptors.Add(interceptor), script: handler => handler.Enqueue(200, "{}"));

        await rig.Client.GetAsync<string>("orders");

        Assert.Equal(0, refreshes);
        Assert.Equal("***", rig.Sending.Single().Headers["Authorization"]);
    }

    [Fact]
    public void TheReadmeContainsTheSampleCodeVerbatim()
    {
        string root = FindRepositoryRoot();
        string readme = Normalize(File.ReadAllText(Path.Combine(root, "Junevy.Communication.WebApi", "README.md")));
        string samples = File.ReadAllText(Path.Combine(root, "Tests", "Junevy.Communication.WebApi.Tests", "ReadmeSamples.cs"));

        foreach (string name in new[] { "SigningInterceptor", "TokenRefreshInterceptor" })
        {
            string sample = Normalize(Between(samples, "// README-SAMPLE-BEGIN " + name, "// README-SAMPLE-END " + name));
            Assert.True(readme.Contains(sample), $"The README does not contain the {name} sample from ReadmeSamples.cs verbatim.");
        }
    }

    private static string ExpectedSignature(string method, string pathAndQuery, string timestamp, byte[] body)
    {
        using var sha256 = SHA256.Create();
        string payload = $"{method}\n{pathAndQuery}\n{timestamp}\n{Convert.ToBase64String(sha256.ComputeHash(body))}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static string Between(string text, string begin, string end)
    {
        int start = text.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
        return text.Substring(start, text.IndexOf(end, StringComparison.Ordinal) - start);
    }

    // 忽略换行风格与行尾空白，使 README（Markdown）与源文件的差异只来自内容。
    private static string Normalize(string text)
        => string.Join("\n", text.Replace("\r\n", "\n").Split('\n').Select(line => line.TrimEnd())).Trim();

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "Junevy.Communication.slnx")))
            directory = Path.GetDirectoryName(directory);

        return directory ?? throw new InvalidOperationException("The repository root (Junevy.Communication.slnx) was not found above the test output directory.");
    }
}
