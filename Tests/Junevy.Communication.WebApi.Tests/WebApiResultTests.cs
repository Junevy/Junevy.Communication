using Junevy.Communication.Core.Results;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="WebApiResult{T}"/> 与 <see cref="WebApiAvailability"/> 的契约测试。
/// </summary>
public sealed class WebApiResultTests
{
    [Fact]
    public void Success_CarriesDataAndStatusCode()
    {
        var result = WebApiResult<string>.Success("payload", 200);

        Assert.True(result.IsSuccess);
        Assert.Equal("payload", result.Data);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal(CommErrorKind.None, result.ErrorKind);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.RemoteCode);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void Success_WithoutDataOrStatusCode_IsAllowed()
    {
        var result = WebApiResult<string>.Success(null, null);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Data);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public void Fail_CarriesEveryField()
    {
        var exception = new InvalidOperationException("inner");

        var result = WebApiResult<int>.Fail("rejected", CommErrorKind.RemoteError, 500, "E42", exception);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, result.Data);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal("E42", result.RemoteCode);
        Assert.Equal("rejected", result.ErrorMessage);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Same(exception, result.Exception);
    }

    [Fact]
    public void Fail_OptionalFieldsDefaultToNull()
    {
        var result = WebApiResult<int>.Fail("no response", CommErrorKind.ConnectionClosed);

        Assert.Null(result.StatusCode);
        Assert.Null(result.RemoteCode);
        Assert.Null(result.Exception);
    }

    [Fact]
    public void Fail_NullMessage_ThrowsArgumentNullException()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => WebApiResult<int>.Fail(null!, CommErrorKind.Timeout));

        Assert.Equal("message", exception.ParamName);
    }

    [Fact]
    public void Fail_NoneKind_ThrowsArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => WebApiResult<int>.Fail("message", CommErrorKind.None));

        Assert.Equal("kind", exception.ParamName);
    }

    [Fact]
    public void As_ConvertsFailureAndKeepsEveryField()
    {
        var exception = new InvalidOperationException("inner");
        var failure = WebApiResult<int>.Fail("rejected", CommErrorKind.RemoteError, 500, "E42", exception);

        WebApiResult<string> converted = failure.As<string>();

        Assert.False(converted.IsSuccess);
        Assert.Equal("rejected", converted.ErrorMessage);
        Assert.Equal(CommErrorKind.RemoteError, converted.ErrorKind);
        Assert.Equal(500, converted.StatusCode);
        Assert.Equal("E42", converted.RemoteCode);
        Assert.Same(exception, converted.Exception);
    }

    [Fact]
    public void As_OnSuccess_ThrowsInvalidOperationException()
    {
        var success = WebApiResult<int>.Success(1, 200);

        Assert.Throws<InvalidOperationException>(() => success.As<string>());
    }

    [Theory]
    [InlineData(null, "Success")]
    [InlineData(200, "Success (HTTP 200)")]
    public void ToString_Success_AppendsTheStatusCode(int? statusCode, string expected)
    {
        Assert.Equal(expected, WebApiResult<int>.Success(1, statusCode).ToString());
    }

    [Theory]
    [InlineData(null, null, "Timeout: too slow")]
    [InlineData(503, null, "Timeout: too slow (HTTP 503)")]
    [InlineData(null, "E42", "Timeout: too slow (remote code E42)")]
    [InlineData(500, "E42", "Timeout: too slow (HTTP 500, remote code E42)")]
    public void ToString_Failure_AppendsStatusCodeAndRemoteCode(int? statusCode, string? remoteCode, string expected)
    {
        var failure = WebApiResult<int>.Fail("too slow", CommErrorKind.Timeout, statusCode, remoteCode);

        Assert.Equal(expected, failure.ToString());
    }

    [Fact]
    public void Availability_DefaultIsUnknown()
    {
        Assert.Equal(WebApiAvailability.Unknown, default(WebApiAvailability));
        Assert.Equal(0, (int)WebApiAvailability.Unknown);
        Assert.Equal(1, (int)WebApiAvailability.Online);
        Assert.Equal(2, (int)WebApiAvailability.Offline);
    }
}
