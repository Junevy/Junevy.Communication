using System.Net;
using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Server.Tests.ReadmeSamples;
using Junevy.Communication.WebApi.Server;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>
/// README 的示例：编译并运行 <c>ReadmeSamples.cs</c> 中的代码，并检查 README 里的代码块与它逐字一致（P32 Task 8）。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class ReadmeSampleTests
{
    private const string Loopback = "http://127.0.0.1:0";

    [Theory]
    [InlineData("order-model")]
    [InlineData("quick-start")]
    [InlineData("envelope")]
    [InlineData("interceptor")]
    [InlineData("deduplication")]
    public void EachSampleBlock_IsVerbatimInTheReadme(string name)
    {
        string readme = ReadBlock(ReadmePath(), name);
        string samples = ReadBlock(Path.Combine(TestProjectDirectory(), "ReadmeSamples.cs"), name);

        Assert.Equal(samples, readme);
    }

    [Fact]
    public async Task QuickStart_ServesRoutesAndStopsCleanly()
    {
        await using WebApiServer server = await OrderService.StartAsync(Loopback);
        using var http = new HttpClient();

        HttpResponseMessage get = await http.GetAsync(server.Urls[0] + "/orders/5");
        HttpResponseMessage post = await http.PostAsync(server.Urls[0] + "/orders", new StringContent("{\"id\":1,\"name\":\"a\"}", Encoding.UTF8, "application/json"));
        await server.StopAsync();

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
    }

    [Fact]
    public async Task Envelope_WritesTheConfiguredFieldsAndTheBusinessFailure()
    {
        await using WebApiServer server = EnvelopeService.Create(Loopback);
        await server.StartAsync();
        using var http = new HttpClient();

        HttpResponseMessage failed = await http.PostAsync(server.Urls[0] + "/orders", new StringContent("{\"id\":0}", Encoding.UTF8, "application/json"));
        string body = await failed.Content.ReadAsStringAsync();

        Assert.Contains("\"code\":\"E100\"", body);
        Assert.Contains("\"result\"", body);
    }

    [Fact]
    public async Task Interceptor_RejectsARequestWithoutTheDeviceKey()
    {
        var components = new WebApiServerComponents();
        components.Interceptors.Add(new RequireDeviceKey());
        var server = new WebApiServer("mes", new WebApiServerConfig { Urls = new[] { Loopback } }, components);
        server.MapGet<Order>("/orders/{id:int}", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));
        await using (server)
        {
            await server.StartAsync();
            using var http = new HttpClient();

            HttpResponseMessage response = await http.GetAsync(server.Urls[0] + "/orders/1");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Deduplication_ReplaysTheFirstResponseForTheSameKey()
    {
        await using WebApiServer server = DeduplicatedService.Create(Loopback);
        await server.StartAsync();
        using var http = new HttpClient();
        HttpRequestMessage Post()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, server.Urls[0] + "/orders")
            {
                Content = new StringContent("{\"id\":1,\"name\":\"a\"}", Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Idempotency-Key", "order-1");
            return request;
        }

        HttpResponseMessage first = await http.SendAsync(Post());
        HttpResponseMessage second = await http.SendAsync(Post());

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    // 从 Markdown 的 <!-- sample: name --> 与 <!-- /sample --> 之间（或 C# 的 BEGIN/END 标记之间）取出代码，去掉首尾的空行。
    private static string ReadBlock(string path, string name)
    {
        string text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        bool markdown = path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
        string begin = markdown ? "```csharp // sample: " + name + "\n" : "// README-SAMPLE-BEGIN " + name + "\n";
        string end = markdown ? "\n```" : "\n// README-SAMPLE-END " + name;
        int start = text.IndexOf(begin, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The block '{name}' is missing from {Path.GetFileName(path)}.");
        int stop = text.IndexOf(end, start + begin.Length, StringComparison.Ordinal);
        Assert.True(stop >= 0, $"The block '{name}' is not closed in {Path.GetFileName(path)}.");
        return text[(start + begin.Length)..stop].Trim('\n');
    }

    private static string ReadmePath() => Path.Combine(RepositoryRoot(), "Junevy.Communication.WebApi.Server", "README.md");

    private static string TestProjectDirectory() => Path.Combine(RepositoryRoot(), "Tests", "Junevy.Communication.WebApi.Server.Tests");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Junevy.Communication.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
