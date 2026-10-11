using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Junevy.Communication.WebApi.Serialization;

/// <summary>
/// 基于 <c>System.Text.Json</c> 的默认序列化器（P3-D15）。默认选项：属性名 camelCase、读取时不区分大小写、中文等非 ASCII 字符不转义成 <c>\uXXXX</c>
/// （部分 MES 处理不了转义的中文，日志也无法阅读）。选项只作用于本实例，不修改任何全局设置。
/// </summary>
public sealed class SystemTextJsonWebApiSerializer : IWebApiSerializer
{
    private const string JsonContentType = "application/json; charset=utf-8";

    private readonly JsonSerializerOptions options;

    /// <summary>使用默认选项创建序列化器。</summary>
    public SystemTextJsonWebApiSerializer()
        : this(CreateDefaultOptions())
    {
    }

    /// <summary>使用指定的选项创建序列化器。</summary>
    /// <param name="options">序列化选项；首次使用后 <c>System.Text.Json</c> 会把它冻结，不能再修改。</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> 为 null。</exception>
    public SystemTextJsonWebApiSerializer(JsonSerializerOptions options)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public string ContentType => JsonContentType;

    /// <inheritdoc />
    public byte[] Serialize(object value, Type type) => JsonSerializer.SerializeToUtf8Bytes(value, type, options);

    /// <inheritdoc />
    public object? Deserialize(byte[] data, Type type) => JsonSerializer.Deserialize(data, type, options);

    private static JsonSerializerOptions CreateDefaultOptions()
        => new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };
}
