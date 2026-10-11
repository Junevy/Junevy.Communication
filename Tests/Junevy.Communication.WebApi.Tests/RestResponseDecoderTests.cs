using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.WebApi.Http;
using Junevy.Communication.WebApi.Rest;
using Junevy.Communication.WebApi.Serialization;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="RestResponseDecoder"/> 的测试（计划 Task 8 第 2 条，设计 15.7）：状态码归类表、业务包络、原始类型、空正文、反序列化失败。
/// </summary>
public sealed class RestResponseDecoderTests
{
    public static IEnumerable<object[]> StatusTable()
    {
        yield return new object[] { 400, "Bad Request", CommErrorKind.RemoteError, "HTTP 400 Bad Request." };
        yield return new object[] { 401, "Unauthorized", CommErrorKind.AuthenticationFailed, "HTTP 401 Unauthorized." };
        yield return new object[] { 403, "Forbidden", CommErrorKind.AuthenticationFailed, "HTTP 403 Forbidden." };
        yield return new object[] { 404, "Not Found", CommErrorKind.RemoteError, "HTTP 404 Not Found." };
        yield return new object[] { 408, "Request Timeout", CommErrorKind.RemoteError, "HTTP 408 Request Timeout." };
        yield return new object[] { 429, "Too Many Requests", CommErrorKind.ResourceExhausted, "HTTP 429 Too Many Requests." };
        yield return new object[] { 500, "Internal Server Error", CommErrorKind.RemoteError, "HTTP 500 Internal Server Error." };
        yield return new object[] { 503, "Service Unavailable", CommErrorKind.RemoteError, "HTTP 503 Service Unavailable." };
        yield return new object[] { 302, "Found", CommErrorKind.RemoteError, "HTTP 302 Found." };
        yield return new object[] { 418, null!, CommErrorKind.RemoteError, "HTTP 418." };
    }

    [Theory]
    [MemberData(nameof(StatusTable))]
    public void Decode_NonSuccessStatus_IsClassifiedByTheStatusCode(int status, string? reason, CommErrorKind expectedKind, string expectedMessage)
    {
        WebApiResult<string> result = Decoder().Decode<string>(Response(status, "ignored body", reason: reason));

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedKind, result.ErrorKind);
        Assert.Equal(expectedMessage, result.ErrorMessage);
        Assert.Equal(status, result.StatusCode);
        Assert.Null(result.RemoteCode);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    public void Decode_SuccessStatus_DeserializesTheBody(int status)
    {
        WebApiResult<Order> result = Decoder().Decode<Order>(Response(status, "{\"orderNo\":\"工单\",\"quantity\":3}"));

        Assert.True(result.IsSuccess);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal("工单", result.Data!.OrderNo);
        Assert.Equal(3, result.Data.Quantity);
    }

    [Fact]
    public void Decode_UndeserializableBody_IsAProtocolViolationWithTheStatusCodeAndException()
    {
        WebApiResult<Order> result = Decoder().Decode<Order>(Response(200, "<html>not json</html>"));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(result.Exception);
    }

    [Theory]
    [InlineData(204, "")]
    [InlineData(200, "")]
    public void Decode_EmptyBody_IsSuccessWithDefaultData(int status, string body)
    {
        WebApiResult<Order> result = Decoder().Decode<Order>(Response(status, body));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Data);
        Assert.Equal(status, result.StatusCode);
    }

    [Fact]
    public void Decode_EmptyBody_ForAValueType_IsDefault()
    {
        WebApiResult<int> result = Decoder().Decode<int>(Response(204, ""));

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Data);
    }

    [Fact]
    public void Decode_RawBytes_ReturnsTheBodyUntouchedAndSkipsTheEnvelope()
    {
        byte[] body = Encoding.UTF8.GetBytes("{\"code\":99,\"msg\":\"would fail\"}");

        WebApiResult<byte[]> result = Decoder(Envelope()).Decode<byte[]>(Response(200, body));

        Assert.True(result.IsSuccess);
        Assert.Equal(body, result.Data);
    }

    [Fact]
    public void Decode_String_UsesTheResponseCharsetAndDefaultsToUtf8()
    {
        WebApiResult<string> declared = Decoder().Decode<string>(Response(200, Encoding.UTF8.GetBytes("工单"), "text/plain; charset=utf-8"));
        WebApiResult<string> latin1 = Decoder().Decode<string>(Response(200, Encoding.GetEncoding("iso-8859-1").GetBytes("café"), "text/plain; charset=iso-8859-1"));
        WebApiResult<string> none = Decoder().Decode<string>(Response(200, Encoding.UTF8.GetBytes("工单"), null));

        Assert.Equal("工单", declared.Data);
        Assert.Equal("café", latin1.Data);
        Assert.Equal("工单", none.Data);
    }

    [Fact]
    public void Decode_String_WithAnUnknownCharset_IsAProtocolViolation()
    {
        WebApiResult<string> result = Decoder().Decode<string>(Response(200, "x", "text/plain; charset=no-such-charset"));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Contains("no-such-charset", result.ErrorMessage);
    }

    [Fact]
    public void Decode_RawTypes_OnNonSuccess_StillFailByStatus()
    {
        WebApiResult<byte[]> result = Decoder(Envelope()).Decode<byte[]>(Response(404, "{\"code\":1}", reason: "Not Found"));

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Null(result.RemoteCode);
    }

    [Fact]
    public void Decode_Envelope_SuccessCodeYieldsTheDataObject()
    {
        WebApiResult<Order> result = Decoder(Envelope()).Decode<Order>(Response(200, "{\"code\":0,\"msg\":\"ok\",\"data\":{\"orderNo\":\"工单\",\"quantity\":9}}"));

        Assert.True(result.IsSuccess);
        Assert.Equal("工单", result.Data!.OrderNo);
        Assert.Equal(9, result.Data.Quantity);
    }

    [Theory]
    [InlineData("{\"code\":0,\"data\":{\"orderNo\":\"A\"}}")]
    [InlineData("{\"code\":\"0\",\"data\":{\"orderNo\":\"A\"}}")]
    public void Decode_Envelope_NumericAndStringCodesAreEquivalent(string json)
    {
        Assert.True(Decoder(Envelope()).Decode<Order>(Response(200, json)).IsSuccess);
    }

    [Fact]
    public void Decode_Envelope_NestedPathsAndCustomSuccessCodes()
    {
        EnvelopeSettings envelope = Envelope(o =>
        {
            o.CodePath = "result.status";
            o.MessagePath = "result.message";
            o.DataPath = "result.payload";
            o.SuccessCodes = new[] { "OK", "200" };
        });

        WebApiResult<Order> ok = Decoder(envelope).Decode<Order>(Response(200, "{\"result\":{\"status\":\"OK\",\"payload\":{\"orderNo\":\"N\"}}}"));
        WebApiResult<Order> also = Decoder(envelope).Decode<Order>(Response(200, "{\"result\":{\"status\":200,\"payload\":{\"orderNo\":\"M\"}}}"));
        WebApiResult<Order> failed = Decoder(envelope).Decode<Order>(Response(200, "{\"result\":{\"status\":\"ERR\",\"message\":\"no stock\"}}"));

        Assert.Equal("N", ok.Data!.OrderNo);
        Assert.Equal("M", also.Data!.OrderNo);
        Assert.Equal(CommErrorKind.RemoteError, failed.ErrorKind);
        Assert.Equal("ERR", failed.RemoteCode);
        Assert.Equal("no stock", failed.ErrorMessage);
    }

    [Fact]
    public void Decode_Envelope_FailureCodeOn200_IsARemoteErrorWithTheBusinessCode()
    {
        WebApiResult<Order> result = Decoder(Envelope()).Decode<Order>(Response(200, "{\"code\":1001,\"msg\":\"工单不存在\"}"));

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("1001", result.RemoteCode);
        Assert.Equal("工单不存在", result.ErrorMessage);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public void Decode_Envelope_FailureWithoutAMessage_HasAFixedEnglishMessage()
    {
        WebApiResult<Order> result = Decoder(Envelope()).Decode<Order>(Response(200, "{\"code\":7}"));

        Assert.Equal("The remote service reported the business code 7.", result.ErrorMessage);
    }

    [Fact]
    public void Decode_Envelope_NonSuccessStatusWithAnEnvelope_TakesCodeAndMessageFromIt()
    {
        WebApiResult<Order> result = Decoder(Envelope()).Decode<Order>(Response(500, "{\"code\":\"E42\",\"msg\":\"station locked\"}", reason: "Internal Server Error"));

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal("E42", result.RemoteCode);
        Assert.Equal("station locked", result.ErrorMessage);
        Assert.Equal(500, result.StatusCode);
    }

    [Fact]
    public void Decode_Envelope_NonSuccessStatusKeepsItsClassification()
    {
        WebApiResult<Order> result = Decoder(Envelope()).Decode<Order>(Response(401, "{\"code\":\"A1\",\"msg\":\"bad token\"}"));

        Assert.Equal(CommErrorKind.AuthenticationFailed, result.ErrorKind);
        Assert.Equal("A1", result.RemoteCode);
    }

    [Theory]
    [InlineData("<html>gateway error</html>")]
    [InlineData("{\"error\":\"no code field\"}")]
    [InlineData("")]
    public void Decode_Envelope_NonSuccessStatusWithoutAUsableEnvelope_FallsBackToTheStatusMessage(string body)
    {
        WebApiResult<Order> result = Decoder(Envelope()).Decode<Order>(Response(502, body, reason: "Bad Gateway"));

        Assert.Equal("HTTP 502 Bad Gateway.", result.ErrorMessage);
        Assert.Null(result.RemoteCode);
    }

    [Theory]
    [InlineData("{\"msg\":\"no code\"}")]
    [InlineData("<html>")]
    [InlineData("[1,2]")]
    [InlineData("{\"code\":null}")]
    public void Decode_Envelope_SuccessStatusWithAnUnusableEnvelope_IsAProtocolViolation(string body)
    {
        WebApiResult<Order> result = Decoder(Envelope()).Decode<Order>(Response(200, body));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.RemoteCode);
    }

    [Fact]
    public void Decode_Envelope_SuccessWithoutDataOrWithNullData_HasDefaultData()
    {
        WebApiResult<Order> missing = Decoder(Envelope()).Decode<Order>(Response(200, "{\"code\":0}"));
        WebApiResult<Order> nullData = Decoder(Envelope()).Decode<Order>(Response(200, "{\"code\":0,\"data\":null}"));

        Assert.True(missing.IsSuccess);
        Assert.Null(missing.Data);
        Assert.True(nullData.IsSuccess);
        Assert.Null(nullData.Data);
    }

    [Fact]
    public void Decode_Envelope_DataThatCannotBeDeserialized_IsAProtocolViolation()
    {
        WebApiResult<Order> result = Decoder(Envelope()).Decode<Order>(Response(200, "{\"code\":0,\"data\":\"not an order\"}"));

        Assert.Equal(CommErrorKind.ProtocolViolation, result.ErrorKind);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public void Decode_Envelope_LocatesFieldsWithJsonDocumentEvenWhenTheSerializerIsReplaced()
    {
        var decoder = new RestResponseDecoder(new FixedSerializer(), Envelope());

        WebApiResult<Order> result = decoder.Decode<Order>(Response(200, "{\"code\":0,\"data\":{\"x\":1}}"));

        Assert.True(result.IsSuccess);
        Assert.Equal("{\"x\":1}", result.Data!.OrderNo);
    }

    [Fact]
    public void Decode_UsesTheSuppliedSerializer()
    {
        var decoder = new RestResponseDecoder(new FixedSerializer(), Envelope(o => o.Enabled = false));

        WebApiResult<Order> result = decoder.Decode<Order>(Response(200, "anything"));

        Assert.Equal("anything", result.Data!.OrderNo);
    }

    private static RestResponseDecoder Decoder(EnvelopeSettings? envelope = null)
        => new RestResponseDecoder(new SystemTextJsonWebApiSerializer(), envelope ?? Envelope(o => o.Enabled = false));

    private static EnvelopeSettings Envelope(Action<ResponseEnvelopeOptions>? configure = null)
    {
        var options = new ResponseEnvelopeOptions { Enabled = true };
        configure?.Invoke(options);
        return new EnvelopeSettings(options);
    }

    private static HttpExchangeResponse Response(int status, string body, string? contentType = "application/json", string? reason = null)
        => Response(status, Encoding.UTF8.GetBytes(body), contentType, reason);

    private static HttpExchangeResponse Response(int status, byte[] body, string? contentType = "application/json", string? reason = null)
        => new HttpExchangeResponse(status, reason, new Dictionary<string, string>(), body, contentType);

    public sealed class Order
    {
        public string? OrderNo { get; set; }

        public int Quantity { get; set; }
    }

    private sealed class FixedSerializer : IWebApiSerializer
    {
        public string ContentType => "application/x-fixed";

        public byte[] Serialize(object value, Type type) => Array.Empty<byte>();

        // 把收到的 JSON 原样放进 OrderNo，证明拿到的是包络里 data 的原始 JSON。
        public object? Deserialize(byte[] data, Type type) => new Order { OrderNo = Encoding.UTF8.GetString(data) };
    }
}
