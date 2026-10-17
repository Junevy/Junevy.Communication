using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;

namespace Junevy.Communication.Architecture.Tests;

/// <summary>
/// SDK 适配包的三条守卫（P6-C4 公开 API 不出现 SDK 类型、P6-C5 SDK 只出现在 <c>Sdk</c> 命名空间、P6-C6 适配包只依赖 Core）。
/// P6.2、P6.3 只需要在登记表里各加一行，就同时获得三条检查。
/// 本文件只在 net8.0 上编译：三个适配包按 P6-C1 只有 net8.0 目标，net472 下连程序集引用都建立不起来。
/// </summary>
public sealed class SdkAdapterArchitectureTests
{
    private const string CoreName = "Junevy.Communication.Core";
    private const string WebApiName = "Junevy.Communication.WebApi";
    private const string LibraryPrefix = "Junevy.Communication.";

    // 登记表（计划 Task 1）：适配程序集、它的 SDK 程序集名前缀、SDK 唯一允许出现的命名空间，以及它允许引用的 Junevy.Communication.* 程序集。
    // 服务端（P3.2）同时依赖 WebApi 的公共约定（P32-D2），因此它的允许列表比 SDK 适配包多一项。
    private static readonly (string AssemblyName, string SdkPrefix, string SdkNamespace, string[] AllowedLibraries)[] Adapters =
    {
        ("Junevy.Communication.Mqtt", "MQTTnet", "Junevy.Communication.Mqtt.Sdk", new[] { CoreName }),
        ("Junevy.Communication.OpcUa", "Opc.Ua", "Junevy.Communication.OpcUa.Sdk", new[] { CoreName }),
        ("Junevy.Communication.WebApi.Server", "Microsoft.AspNetCore", "Junevy.Communication.WebApi.Server.Hosting", new[] { CoreName, WebApiName }),
    };

    [Fact]
    public void SdkAdapters_ReferenceOnlyTheirAllowedLibraries()
    {
        var violations = new List<string>();
        foreach (var adapter in Adapters)
        {
            foreach (AssemblyName reference in Assembly.Load(adapter.AssemblyName).GetReferencedAssemblies())
            {
                string referenced = reference.Name!;
                if (referenced.StartsWith(LibraryPrefix, StringComparison.Ordinal) && !adapter.AllowedLibraries.Contains(referenced))
                {
                    violations.Add($"{adapter.AssemblyName} -> {referenced}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"SDK adapters may reference only their allowed libraries (assembly -> referenced): {string.Join(", ", violations)}");
    }

    [Fact]
    public void SdkAdapters_PublicApiNeverMentionsSdkTypes()
    {
        var violations = new List<string>();
        foreach (var adapter in Adapters)
        {
            foreach (Type type in SdkPublicApiWalker.ExposedTypes(Assembly.Load(adapter.AssemblyName)))
            {
                string? owner = type.Assembly.GetName().Name;
                if (owner is not null && owner.StartsWith(adapter.SdkPrefix, StringComparison.Ordinal))
                {
                    violations.Add($"{adapter.AssemblyName}: {type.FullName}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"SDK types reached the public API: {string.Join(", ", violations.Distinct())}");
    }

    [Fact]
    public void SdkAdapters_KeepSdkDependenciesInsideTheSdkNamespace()
    {
        foreach (var adapter in Adapters)
        {
            // 匹配根命名空间本身与它的子命名空间，但排除 Sdk 命名空间：NetArchTest 按正则 IsMatch 匹配。
            string root = adapter.SdkNamespace[..adapter.SdkNamespace.LastIndexOf('.')];
            string leaf = adapter.SdkNamespace[(adapter.SdkNamespace.LastIndexOf('.') + 1)..];

            TestResult result = Types.InAssembly(Assembly.Load(adapter.AssemblyName))
                .That().ResideInNamespaceMatching($"^{Regex.Escape(root)}(?!\\.{Regex.Escape(leaf)}$)")
                .ShouldNot().HaveDependencyOnAny(adapter.SdkPrefix)
                .GetResult();

            Assert.True(result.IsSuccessful,
                $"{adapter.AssemblyName}: types outside {adapter.SdkNamespace} depend on the SDK: "
                + string.Join(", ", result.FailingTypeNames ?? Array.Empty<string>()));
        }
    }
}