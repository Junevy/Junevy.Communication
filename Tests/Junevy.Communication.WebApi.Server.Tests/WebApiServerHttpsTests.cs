using System.Net;
using System.Security.Cryptography.X509Certificates;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Testing;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>HTTPS（P32-D13）：证书来自调用方、来自 .pfx 文件（口令来自环境变量），以及文件不存在时的 <c>InvalidRequest</c>。</summary>
[Collection(SocketTimingCollection.Name)]
public sealed class WebApiServerHttpsTests
{
    [Fact]
    public async Task CertificateFromComponents_ServesHttps()
    {
        using TestCertificate certificate = TestCertificates.CreateSelfSigned();
        var config = new WebApiServerConfig { Urls = new[] { "https://127.0.0.1:0" } };
        await using var server = new WebApiServer("tls", config, new WebApiServerComponents { Certificate = certificate.Certificate });
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order { Id = 9 })));

        CommResult result = await server.StartAsync();

        Assert.True(result.IsSuccess, result.ErrorMessage);
        using HttpResponseMessage response = await SendPinnedAsync(certificate.Thumbprint, server.Urls[0] + "/x");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CertificateFromFile_ServesHttps_WithThePasswordReadFromTheVariable()
    {
        using TestCertificate certificate = TestCertificates.CreateSelfSigned();
        using var variables = new TemporaryVariables();
        string password = "pfx-password";
        string passwordVariable = variables.Set(password);
        string path = Path.Combine(Path.GetTempPath(), "junevy-webapi-server-" + Guid.NewGuid().ToString("N") + ".pfx");
        File.WriteAllBytes(path, certificate.Certificate.Export(X509ContentType.Pfx, password));
        try
        {
            var config = new WebApiServerConfig
            {
                Urls = new[] { "https://127.0.0.1:0" },
                Https = new WebApiServerHttpsOptions { CertificatePath = path, CertificatePasswordVariable = passwordVariable },
            };
            await using var server = new WebApiServer("tls", config);
            server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

            CommResult result = await server.StartAsync();

            Assert.True(result.IsSuccess, result.ErrorMessage);
            using HttpResponseMessage response = await SendPinnedAsync(certificate.Thumbprint, server.Urls[0] + "/x");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MissingCertificateFile_IsInvalidRequest_AndTheServerStaysStopped()
    {
        string path = Path.Combine(Path.GetTempPath(), "junevy-missing-" + Guid.NewGuid().ToString("N") + ".pfx");
        var config = new WebApiServerConfig
        {
            Urls = new[] { "https://127.0.0.1:0" },
            Https = new WebApiServerHttpsOptions { CertificatePath = path },
        };
        await using var server = new WebApiServer("tls", config);
        server.MapGet<Order>("/x", (_, _) => Task.FromResult(WebApiServerResponse<Order>.Ok(new Order())));

        CommResult result = await server.StartAsync();

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Contains(path, result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(WebApiServerState.Stopped, server.State);
    }

    // 证书是自签名的：只接受指纹与测试证书相同的服务器，其余校验项不放宽。
    private static async Task<HttpResponseMessage> SendPinnedAsync(string thumbprint, string url)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented?.Thumbprint == thumbprint,
        };
        using var http = new HttpClient(handler);
        return await http.GetAsync(url);
    }
}
