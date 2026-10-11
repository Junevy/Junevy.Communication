using System.Text;
using System.Text.Json;
using Junevy.Communication.WebApi.Serialization;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// <see cref="SystemTextJsonWebApiSerializer"/> 的测试（P3-D15）：camelCase、读取不区分大小写、中文不转义、可替换的选项。
/// </summary>
public sealed class SystemTextJsonWebApiSerializerTests
{
    private readonly IWebApiSerializer serializer = new SystemTextJsonWebApiSerializer();

    [Fact]
    public void ContentType_IsJsonUtf8()
    {
        Assert.Equal("application/json; charset=utf-8", serializer.ContentType);
    }

    [Fact]
    public void Serialize_UsesCamelCasePropertyNames()
    {
        byte[] bytes = serializer.Serialize(new Order { OrderNo = "A-1", Quantity = 3 }, typeof(Order));

        Assert.Equal("{\"orderNo\":\"A-1\",\"quantity\":3}", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Serialize_KeepsChineseTextUnescaped()
    {
        byte[] bytes = serializer.Serialize(new Order { OrderNo = "工单" }, typeof(Order));

        string json = Encoding.UTF8.GetString(bytes);
        Assert.Contains("工单", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void Deserialize_IgnoresCaseOfPropertyNames()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"ORDERNO\":\"工单\",\"Quantity\":7}");

        var order = (Order?)serializer.Deserialize(json, typeof(Order));

        Assert.Equal("工单", order!.OrderNo);
        Assert.Equal(7, order.Quantity);
    }

    [Fact]
    public void Deserialize_InvalidJson_ThrowsSoTheCallerCanClassifyIt()
    {
        Assert.ThrowsAny<JsonException>(() => serializer.Deserialize(Encoding.UTF8.GetBytes("{not json"), typeof(Order)));
    }

    [Fact]
    public void CustomOptions_AreUsed()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = null };
        var custom = new SystemTextJsonWebApiSerializer(options);

        byte[] bytes = custom.Serialize(new Order { OrderNo = "A-1" }, typeof(Order));

        Assert.StartsWith("{\"OrderNo\":\"A-1\"", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void NullOptions_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new SystemTextJsonWebApiSerializer(null!));
    }

    private sealed class Order
    {
        public string? OrderNo { get; set; }

        public int Quantity { get; set; }
    }
}
