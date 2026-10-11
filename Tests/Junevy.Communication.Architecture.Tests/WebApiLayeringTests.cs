using System.Text.RegularExpressions;
using Junevy.Communication.WebApi;
using NetArchTest.Rules;

namespace Junevy.Communication.Architecture.Tests;

/// <summary>
/// WebApi 内部命名空间的依赖方向守卫（P3-D6、P3-D18，计划第 3 节）。WebApi 是单程序集，分层靠命名空间：
/// NetArchTest 读取 IL，因此方法体中的类型引用也会被检查，这是反射做不到的。
/// </summary>
public sealed class WebApiLayeringTests
{
    private const string Root = "Junevy.Communication.WebApi";
    private const string Serialization = Root + ".Serialization";
    private const string Auth = Root + ".Auth";
    private const string Http = Root + ".Http";
    private const string Rest = Root + ".Rest";
    private const string Soap = Root + ".Soap";
    private const string Factory = Root + ".Factory";
    private const string DependencyInjection = Root + ".DependencyInjection";

    // 分层表（计划第 3 节）：命名空间 → 允许依赖的 WebApi 内部命名空间。Server（3.2）的依赖由 3.2 计划确定，届时在此加一行。
    private static readonly Dictionary<string, string[]> Layers = new Dictionary<string, string[]>
    {
        [Root] = Array.Empty<string>(),
        [Serialization] = new[] { Root },
        [Auth] = new[] { Root },
        [Http] = new[] { Root, Auth },
        [Rest] = new[] { Root, Serialization, Http },
        [Soap] = new[] { Root, Http },
        [Factory] = new[] { Root, Rest, Soap },
        [DependencyInjection] = new[] { Root, Factory },
    };

    public static IEnumerable<object[]> LayerNamespaces => Layers.Keys.Select(name => new object[] { name });

    [Fact]
    public void EveryNamespaceInTheAssemblyIsInTheLayerTable()
    {
        string[] unregistered = typeof(WebApiResult<>).Assembly.GetTypes()
            .Select(type => type.Namespace)
            .Where(name => name != null && name.StartsWith(Root, StringComparison.Ordinal) && !Layers.ContainsKey(name))
            .Distinct()
            .ToArray()!;

        Assert.True(unregistered.Length == 0, $"Namespaces missing from the layer table: {string.Join(", ", unregistered)}");
    }

    [Theory]
    [MemberData(nameof(LayerNamespaces))]
    public void Namespace_DependsOnlyOnTheNamespacesTheLayerTableAllows(string layer)
    {
        // 禁止集合 = 表中除自身与允许项以外的命名空间；NetArchTest 按命名空间前缀匹配方法体与签名中引用的类型。
        string[] forbidden = Layers.Keys
            .Where(name => name != layer && !Layers[layer].Contains(name))
            .ToArray();

        TestResult result = Types.InAssembly(typeof(WebApiResult<>).Assembly)
            .That().ResideInNamespaceMatching($"^{Regex.Escape(layer)}$")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful,
                    $"Types in {layer} depend on namespaces the layer table does not allow: {string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>())}");
    }
}
