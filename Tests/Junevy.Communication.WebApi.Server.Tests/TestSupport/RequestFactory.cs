using System.Text;
using Junevy.Communication.WebApi.Server.Config;

namespace Junevy.Communication.WebApi.Server.Tests.TestSupport;

/// <summary>测试辅助：构造请求上下文（经内部构造函数），以及随机环境变量的临时管理。</summary>
internal static class RequestFactory
{
    /// <summary>创建请求上下文。请求头名称不区分大小写。</summary>
    public static WebApiServerContext Create(IDictionary<string, string>? headers = null, string method = "GET", string path = "/test")
    {
        var copy = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        return new WebApiServerContext(
            "test", 1, method, path, null,
            new Dictionary<string, string>(), new Dictionary<string, string>(), copy,
            null, Array.Empty<byte>(), null);
    }

    /// <summary>Basic 认证的 Authorization 头的值。</summary>
    public static string Basic(string userAndPassword) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(userAndPassword));
}

/// <summary>
/// 测试用的环境变量：使用随机名称，<see cref="Dispose"/> 时清除，避免在测试之间留下状态。
/// </summary>
internal sealed class TemporaryVariables : IDisposable
{
    private readonly List<string> names = new List<string>();

    /// <summary>设置一个随机名称的环境变量，返回变量名。</summary>
    public string Set(string value)
    {
        string name = "JUNEVY_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, value);
        names.Add(name);
        return name;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (string name in names)
            Environment.SetEnvironmentVariable(name, null);
    }
}
