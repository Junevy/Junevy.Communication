using System.Reflection;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Core.Tests;

/// <summary>
/// <see cref="CommResult"/>、<see cref="CommResult{T}"/> 与 <see cref="CommErrorKind"/> 的契约测试。
/// </summary>
public sealed class CommResultTests
{
    [Fact]
    public void Success_IsCachedAndHasNoneKind()
    {
        var first = CommResult.Success();
        var second = CommResult.Success();

        Assert.Same(first, second);
        Assert.True(first.IsSuccess);
        Assert.Equal(CommErrorKind.None, first.ErrorKind);
    }

    [Fact]
    public void Fail_WithNone_Throws()
    {
        Assert.Throws<ArgumentException>(() => CommResult.Fail("message", CommErrorKind.None));
        Assert.Throws<ArgumentException>(() => CommResult<int>.Fail("message", CommErrorKind.None));
    }

    [Fact]
    public void Fail_NullMessage_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => CommResult.Fail(null!, CommErrorKind.Timeout));
        Assert.Throws<ArgumentNullException>(() => CommResult<int>.Fail(null!, CommErrorKind.Timeout));
    }

    [Fact]
    public void As_PropagatesFailureFields()
    {
        var exception = new InvalidOperationException("inner");
        var failure = CommResult.Fail("MC end code", CommErrorKind.RemoteError, 0xC051L, exception);

        var converted = failure.As<int>();

        Assert.False(converted.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, converted.ErrorKind);
        Assert.Equal("MC end code", converted.ErrorMessage);
        Assert.Equal(0xC051L, converted.ProtocolErrorCode);
        Assert.Same(exception, converted.Exception);

        var typedFailure = CommResult<string>.Fail("timeout", CommErrorKind.Timeout, null, exception);
        var typedConverted = typedFailure.As<int>();

        Assert.False(typedConverted.IsSuccess);
        Assert.Equal(CommErrorKind.Timeout, typedConverted.ErrorKind);
        Assert.Equal("timeout", typedConverted.ErrorMessage);
        Assert.Null(typedConverted.ProtocolErrorCode);
        Assert.Same(exception, typedConverted.Exception);
    }

    [Fact]
    public void As_OnSuccess_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => CommResult.Success().As<int>());
        Assert.Throws<InvalidOperationException>(() => CommResult<int>.Success(1).As<string>());
    }

    [Fact]
    public void Properties_HaveNoPublicSetters()
    {
        AssertNoPublicSetters(typeof(CommResult));
        AssertNoPublicSetters(typeof(CommResult<int>));
    }

    [Fact]
    public void ErrorKindValues_MatchModbusErrorKind()
    {
        // 设计文档第 4 节：0–7 与 ModbusErrorKind 数值一一对应（RemoteError 对应 Modbus 的 ModbusException）；8–11 为通道族新增。
        var expected = new (int Value, string Name)[]
        {
            (0, "None"), (1, "Unspecified"), (2, "InvalidRequest"), (3, "ConnectionClosed"),
            (4, "Timeout"), (5, "ProtocolViolation"), (6, "RemoteError"), (7, "Cancelled"),
            (8, "NotConnected"), (9, "AuthenticationFailed"), (10, "ResourceExhausted"), (11, "NotSupported"),
        };

        foreach (var (value, name) in expected)
        {
            var kind = (CommErrorKind)value;
            Assert.Equal(value, (int)kind);
            Assert.Equal(name, kind.ToString());
        }

        Assert.Equal(expected.Length, Enum.GetValues(typeof(CommErrorKind)).Length);
    }

    [Fact]
    public void ToString_FormatsKindMessageAndCode()
    {
        Assert.Equal("Success", CommResult.Success().ToString());
        Assert.Equal("Success", CommResult<int>.Success(1).ToString());
        Assert.Equal("Timeout: request timed out (code 0xC051)",
            CommResult.Fail("request timed out", CommErrorKind.Timeout, 0xC051L).ToString());
        Assert.Equal("Timeout: request timed out",
            CommResult.Fail("request timed out", CommErrorKind.Timeout).ToString());
    }

    [Fact]
    public void ToResult_CopiesFailureAndMapsSuccessToCachedInstance()
    {
        Assert.Same(CommResult.Success(), CommResult<int>.Success(1).ToResult());

        var exception = new IOException("link down");
        var result = CommResult<int>.Fail("link down", CommErrorKind.ConnectionClosed, 3L, exception).ToResult();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Equal("link down", result.ErrorMessage);
        Assert.Equal(3L, result.ProtocolErrorCode);
        Assert.Same(exception, result.Exception);
    }

    private static void AssertNoPublicSetters(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(properties);

        foreach (var property in properties)
            Assert.Null(property.GetSetMethod(nonPublic: false));
    }
}
