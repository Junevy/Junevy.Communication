using Junevy.Communication.Testing;
using Microsoft.Extensions.Logging;

namespace Junevy.Communication.Plc.Tests;

/// <summary>测试用的客户端配置。</summary>
internal class StubConfig : IPlcClientConfig
{
    /// <summary>用于区分配置实例的标记（测试用）。</summary>
    public string? Tag { get; set; }
}

/// <summary>继承 <see cref="StubConfig"/> 的配置：工厂只按精确类型匹配创建器，因此它没有对应的创建器。</summary>
internal sealed class DerivedStubConfig : StubConfig
{
}

/// <summary>
/// 测试用的客户端创建器：每次创建一个 <see cref="FakePlcClient"/>，并记录它使用的脚本化通道，便于检查释放。
/// </summary>
internal sealed class StubCreator : IPlcClientCreator
{
    private readonly List<ScriptedClientChannel> channels = new List<ScriptedClientChannel>();

    /// <inheritdoc />
    public Type ConfigType => typeof(StubConfig);

    /// <summary>创建过的通道，按创建顺序排列。</summary>
    public IReadOnlyList<ScriptedClientChannel> Channels => channels;

    /// <summary>创建次数。</summary>
    public int CreateCount => channels.Count;

    /// <inheritdoc />
    public IPlcClient Create(string name, IPlcClientConfig config, ILoggerFactory loggerFactory)
    {
        var channel = new ScriptedClientChannel();
        channels.Add(channel);
        return new FakePlcClient(channel, logger: loggerFactory.CreateLogger(name));
    }
}
