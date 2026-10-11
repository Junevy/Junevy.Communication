using System.Text;
using Junevy.Communication.WebApi.Serialization;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="ResponseEnvelopeCodec"/> 的读取测试（P3-D15）：字段定位始终用 <c>JsonDocument</c>，业务码按 JSON 原文比较。
/// </summary>
public sealed class ResponseEnvelopeCodecTests
{
    [Theory]
    [InlineData("{\"code\":0}", "0")]
    [InlineData("{\"code\":\"0\"}", "0")]
    [InlineData("{\"code\":200}", "200")]
    [InlineData("{\"code\":\"E42\"}", "E42")]
    [InlineData("{\"code\":1.5}", "1.5")]
    [InlineData("{\"code\":true}", "true")]
    [InlineData("{\"code\":false}", "false")]
    public void Read_CodeIsTheJsonTextWhateverItsType(string json, string expectedCode)
    {
        EnvelopeReadResult result = Read(json);

        Assert.True(result.IsValid);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public void Read_ExtractsMessageAndTheRawDataJson()
    {
        EnvelopeReadResult result = Read("{\"code\":0,\"msg\":\"ok\",\"data\":{\"orderNo\":\"工单\",\"n\":[1,2]}}");

        Assert.Equal("ok", result.Message);
        Assert.Equal("{\"orderNo\":\"工单\",\"n\":[1,2]}", result.DataJson);
    }

    [Theory]
    [InlineData("{\"code\":0}")]
    [InlineData("{\"code\":0,\"data\":null}")]
    public void Read_MissingOrNullData_HasNoDataJson(string json)
    {
        Assert.Null(Read(json).DataJson);
    }

    [Theory]
    [InlineData("{\"code\":0,\"data\":\"text\"}", "\"text\"")]
    [InlineData("{\"code\":0,\"data\":[1,2]}", "[1,2]")]
    [InlineData("{\"code\":0,\"data\":7}", "7")]
    public void Read_DataOfAnyJsonKind_IsReturnedAsRawJson(string json, string expected)
    {
        Assert.Equal(expected, Read(json).DataJson);
    }

    [Fact]
    public void Read_NestedPaths_AreFollowedSegmentBySegment()
    {
        EnvelopeSettings settings = Settings(o =>
        {
            o.CodePath = "result.status.code";
            o.MessagePath = "result.message";
            o.DataPath = "result.payload";
        });

        EnvelopeReadResult result = Read("{\"result\":{\"status\":{\"code\":\"0\"},\"message\":\"done\",\"payload\":{\"id\":1}}}", settings);

        Assert.True(result.IsValid);
        Assert.Equal("0", result.Code);
        Assert.Equal("done", result.Message);
        Assert.Equal("{\"id\":1}", result.DataJson);
    }

    [Fact]
    public void Read_NonStringMessage_IsReturnedAsItsJsonText()
    {
        Assert.Equal("{\"a\":1}", Read("{\"code\":1,\"msg\":{\"a\":1}}").Message);
        Assert.Null(Read("{\"code\":1,\"msg\":null}").Message);
        Assert.Null(Read("{\"code\":1}").Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"msg\":\"no code\"}")]
    [InlineData("{\"code\":null}")]
    [InlineData("{\"code\":{\"nested\":1}}")]
    [InlineData("{\"code\":[0]}")]
    [InlineData("{\"Code\":0}")]
    public void Read_MissingOrUnusableCode_IsInvalid(string json)
    {
        EnvelopeReadResult result = Read(json);

        Assert.False(result.IsValid);
        Assert.Contains("code", result.Error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    public void Read_RootThatIsNotAnObject_IsInvalid(string json)
    {
        Assert.False(Read(json).IsValid);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("   ")]
    public void Read_NotJson_IsInvalidWithoutThrowing(string text)
    {
        EnvelopeReadResult result = Read(text);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Read_PathThroughANonObject_IsMissing()
    {
        EnvelopeSettings settings = Settings(o => o.CodePath = "result.code");

        Assert.False(Read("{\"result\":5}", settings).IsValid);
    }

    [Theory]
    [InlineData("code", new[] { "code" })]
    [InlineData("result.code", new[] { "result", "code" })]
    public void TryParsePath_SplitsOnDots(string path, string[] expected)
    {
        Assert.True(EnvelopeSettings.TryParsePath(path, out string[] segments));
        Assert.Equal(expected, segments);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".code")]
    [InlineData("code.")]
    [InlineData("a..b")]
    [InlineData("a. .b")]
    public void TryParsePath_RejectsEmptySegments(string path)
    {
        Assert.False(EnvelopeSettings.TryParsePath(path, out _));
    }

    private static EnvelopeReadResult Read(string json, EnvelopeSettings? settings = null)
        => ResponseEnvelopeCodec.Read(Encoding.UTF8.GetBytes(json), settings ?? Settings(_ => { }));

    private static EnvelopeSettings Settings(Action<ResponseEnvelopeOptions> configure)
    {
        var options = new ResponseEnvelopeOptions { Enabled = true };
        configure(options);
        return new EnvelopeSettings(options);
    }
}
