using System.Text;
using Junevy.Communication.WebApi;
using Junevy.Communication.WebApi.Serialization;
using Junevy.Communication.WebApi.Server.Body;
using Junevy.Communication.WebApi.Server.Tests.TestSupport;

namespace Junevy.Communication.WebApi.Server.Tests;

/// <summary>请求正文（P32-D7）：每条规则一个用例；错误的状态码与异常是否保留都要检查。</summary>
public sealed class RequestBodyReaderTests
{
    private static readonly IWebApiSerializer Serializer = new SystemTextJsonWebApiSerializer();

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void EmptyBody_IsRejectedWith400_EvenWithAJsonContentType()
    {
        BodyReadResult result = RequestBodyReader.Read(Array.Empty<byte>(), "application/json", typeof(Order), Serializer);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData(null)]
    public void NonJsonContentType_OnANonEmptyBody_IsRejectedWith415(string? contentType)
    {
        BodyReadResult result = RequestBodyReader.Read(Utf8("{}"), contentType, typeof(Order), Serializer);

        Assert.False(result.IsSuccess);
        Assert.Equal(415, result.StatusCode);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/problem+json")]
    [InlineData("application/vnd.mes+json")]
    public void JsonMediaTypes_AreAccepted(string contentType)
    {
        BodyReadResult result = RequestBodyReader.Read(Utf8("{\"id\":1}"), contentType, typeof(Order), Serializer);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, ((Order)result.Value!).Id);
    }

    [Fact]
    public void ByteArrayRequest_ReturnsTheRawBytes()
    {
        byte[] body = Utf8("{\"raw\":true}");

        BodyReadResult result = RequestBodyReader.Read(body, "application/json", typeof(byte[]), Serializer);

        Assert.True(result.IsSuccess);
        Assert.Equal(body, (byte[])result.Value!);
    }

    [Fact]
    public void StringRequest_DecodesAsUtf8WhenNoCharsetIsGiven()
    {
        BodyReadResult result = RequestBodyReader.Read(Utf8("{\"name\":\"订单\"}"), "application/json", typeof(string), Serializer);

        Assert.Equal("{\"name\":\"订单\"}", result.Value);
    }

    [Fact]
    public void StringRequest_HonoursTheCharsetParameter()
    {
        const string text = "{\"name\":\"订单\"}";
        byte[] body = Encoding.Unicode.GetBytes(text);

        BodyReadResult result = RequestBodyReader.Read(body, "application/json; charset=\"utf-16\"", typeof(string), Serializer);

        Assert.Equal(text, result.Value);
    }

    [Fact]
    public void StringRequest_WithAnUnknownCharset_IsRejectedWith415_AndKeepsTheCause()
    {
        BodyReadResult result = RequestBodyReader.Read(Utf8("{}"), "application/json; charset=x-no-such-charset", typeof(string), Serializer);

        Assert.False(result.IsSuccess);
        Assert.Equal(415, result.StatusCode);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public void TypedRequest_IsDeserializedWithTheSerializer()
    {
        BodyReadResult result = RequestBodyReader.Read(Utf8("{\"id\":42,\"name\":\"widget\"}"), "application/json", typeof(Order), Serializer);

        var order = (Order)result.Value!;
        Assert.Equal(42, order.Id);
        Assert.Equal("widget", order.Name);
    }

    [Fact]
    public void JsonNull_DeserializesToNull()
    {
        BodyReadResult result = RequestBodyReader.Read(Utf8("null"), "application/json", typeof(Order), Serializer);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    [Fact]
    public void InvalidJson_IsRejectedWith400_AndKeepsTheExceptionForTheLog()
    {
        BodyReadResult result = RequestBodyReader.Read(Utf8("{\"id\":"), "application/json", typeof(Order), Serializer);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.NotNull(result.Exception);
        Assert.DoesNotContain("{", result.Message!);
    }

    [Fact]
    public void SerializerFailure_OfAnyKind_IsRejectedWith400()
    {
        BodyReadResult result = RequestBodyReader.Read(Utf8("{}"), "application/json", typeof(Order), new ThrowingSerializer());

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.StatusCode);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    private sealed class ThrowingSerializer : IWebApiSerializer
    {
        public string ContentType => "application/json";

        public byte[] Serialize(object value, Type type) => throw new NotSupportedException();

        public object? Deserialize(byte[] data, Type type) => throw new InvalidOperationException("custom serializer failure");
    }
}
