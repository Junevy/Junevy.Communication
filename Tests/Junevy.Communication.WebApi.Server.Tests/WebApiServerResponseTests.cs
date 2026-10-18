namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>处理器返回值的构造规则（P32-D6）：成功只能是 2xx，业务失败的码与状态码有范围。</summary>
public sealed class WebApiServerResponseTests
{
    [Fact]
    public void Ok_WithoutAStatus_LeavesTheStatusToTheFormatter()
    {
        var response = WebApiServerResponse<int>.Ok(5);

        Assert.True(response.IsSuccess);
        Assert.Equal(5, response.Data);
        Assert.Null(response.StatusCode);
        Assert.Null(response.Code);
        Assert.Null(response.Message);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(299)]
    public void Ok_AcceptsTheTwoHundredSeries(int statusCode)
    {
        Assert.Equal(statusCode, WebApiServerResponse<int>.Ok(1, statusCode).StatusCode);
    }

    [Theory]
    [InlineData(199)]
    [InlineData(300)]
    [InlineData(404)]
    public void Ok_RejectsStatusesOutsideTheTwoHundredSeries(int statusCode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WebApiServerResponse<int>.Ok(1, statusCode));
    }

    [Fact]
    public void Fail_CarriesTheCodeAndMessage_AndNoData()
    {
        var response = WebApiServerResponse<int>.Fail("E42", "rejected", 409);

        Assert.False(response.IsSuccess);
        Assert.Equal("E42", response.Code);
        Assert.Equal("rejected", response.Message);
        Assert.Equal(409, response.StatusCode);
        Assert.Equal(default, response.Data);
    }

    [Fact]
    public void Fail_RejectsAnEmptyCode()
    {
        Assert.Throws<ArgumentException>(() => WebApiServerResponse<int>.Fail(string.Empty, "message"));
    }

    [Fact]
    public void Fail_RejectsANullMessage()
    {
        Assert.Throws<ArgumentNullException>(() => WebApiServerResponse<int>.Fail("E42", null!));
    }

    [Theory]
    [InlineData(199)]
    [InlineData(600)]
    public void Fail_RejectsStatusesOutsideTheBusinessRange(int statusCode)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WebApiServerResponse<int>.Fail("E42", "message", statusCode));
    }

    [Fact]
    public void Headers_AreCaseInsensitive()
    {
        var response = WebApiServerResponse<int>.Ok(1);

        response.Headers["X-Trace"] = "abc";

        Assert.Equal("abc", response.Headers["x-trace"]);
    }
}
