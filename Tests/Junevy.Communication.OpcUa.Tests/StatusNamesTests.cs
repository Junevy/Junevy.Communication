using System.Reflection;
using Junevy.Communication.OpcUa.Sdk;
using Opc.Ua;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 符号名表（2026-10-25）：名称取自 SDK <see cref="StatusCodes"/> 的公开字段，而不是 <c>StatusCode.SymbolicId</c> 背后的登记表——
/// 那张表在两个线程同时首次初始化 SDK 类型时可能被重置为 31 项（DebugAssistant 全量并行测试中复现）。
/// </summary>
public sealed class StatusNamesTests
{
    public static IEnumerable<object[]> SdkFields() =>
        typeof(StatusCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(StatusCode))
            .Select(field => new object[] { field.Name });

    [Theory]
    [MemberData(nameof(SdkFields))]
    public void EveryCodeTheSdkDeclares_HasItsSymbolicName(string fieldName)
    {
        var status = (StatusCode)typeof(StatusCodes).GetField(fieldName)!.GetValue(null)!;

        Assert.Equal(status.SymbolicId, StatusNames.Lookup(status.Code));
    }

    [Fact]
    public void FlagBits_DoNotChangeTheName()
        => Assert.Equal("BadNodeIdUnknown", StatusNames.Lookup(0x8034_0400u));

    [Fact]
    public void ConcurrentLookups_AllSeeTheName()
    {
        string[] results = new string[64];

        Parallel.For(0, results.Length, index => results[index] = StatusNames.Lookup(0x8034_0000u));

        Assert.All(results, name => Assert.Equal("BadNodeIdUnknown", name));
    }
}
