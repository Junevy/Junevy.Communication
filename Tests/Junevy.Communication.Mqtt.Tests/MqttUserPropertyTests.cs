namespace Junevy.Communication.Mqtt.Tests;

public sealed class MqttUserPropertyTests
{
    [Fact]
    public void NameOrValueIsNull_Throws()
    {
        Assert.Throws<ArgumentNullException>("name", () => new MqttUserProperty(null!, "v"));
        Assert.Throws<ArgumentNullException>("value", () => new MqttUserProperty("n", null!));
    }

    [Fact]
    public void KeepsNameAndValue()
    {
        var property = new MqttUserProperty("Content-Type", "text/plain");

        Assert.Equal("Content-Type", property.Name);
        Assert.Equal("text/plain", property.Value);
    }

    [Fact]
    public void Equality_ComparesNameAndValue()
    {
        var left = new MqttUserProperty("n", "v");
        var right = new MqttUserProperty("n", "v");
        var other = new MqttUserProperty("n", "w");

        Assert.True(left == right);
        Assert.False(left == other);
        Assert.True(left != other);
        Assert.True(left.Equals((object)right));
        Assert.False(left.Equals("not a property"));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void ToString_IsNameEqualsValue()
    {
        Assert.Equal("n=v", new MqttUserProperty("n", "v").ToString());
    }
}