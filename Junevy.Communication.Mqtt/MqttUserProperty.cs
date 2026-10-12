namespace Junevy.Communication.Mqtt;

/// <summary>
/// MQTT 5 的用户属性：名称与值都是 UTF-8 文本，Broker 透传给订阅方（P61-D3）。
/// </summary>
public readonly struct MqttUserProperty : IEquatable<MqttUserProperty>
{
    /// <summary>创建一条用户属性。</summary>
    /// <param name="name">名称；不能为 null。</param>
    /// <param name="value">值；不能为 null。</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 或 <paramref name="value"/> 为 null。</exception>
    public MqttUserProperty(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);

        Name = name;
        Value = value;
    }

    /// <summary>名称。</summary>
    public string Name { get; }

    /// <summary>值。</summary>
    public string Value { get; }

    /// <summary>按名称与值判断相等。</summary>
    /// <param name="other">另一条属性。</param>
    /// <returns>两者相等时为 true。</returns>
    public bool Equals(MqttUserProperty other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal) && string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <summary>判断是否等于另一个对象。</summary>
    /// <param name="obj">待比较的对象。</param>
    /// <returns>是同类型的属性且相等时为 true。</returns>
    public override bool Equals(object? obj) => obj is MqttUserProperty other && Equals(other);

    /// <summary>返回由名称与值计算的哈希码。</summary>
    /// <returns>哈希码。</returns>
    public override int GetHashCode() => HashCode.Combine(Name, Value);

    /// <summary>返回 <c>Name=Value</c>。</summary>
    /// <returns>可读形式；不含任何正文。</returns>
    public override string ToString() => $"{Name}={Value}";

    /// <summary>按名称与值判断相等。</summary>
    /// <param name="left">左侧。</param>
    /// <param name="right">右侧。</param>
    /// <returns>两者相等时为 true。</returns>
    public static bool operator ==(MqttUserProperty left, MqttUserProperty right) => left.Equals(right);

    /// <summary>按名称与值判断不等。</summary>
    /// <param name="left">左侧。</param>
    /// <param name="right">右侧。</param>
    /// <returns>两者不等时为 true。</returns>
    public static bool operator !=(MqttUserProperty left, MqttUserProperty right) => !left.Equals(right);
}