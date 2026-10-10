using System.Reflection;

namespace Junevy.Communication.Channels.Tests;

/// <summary>
/// 接口形状测试：验证 <c>IsConnected</c> 只由 <see cref="IConnectable"/> 声明，通过 <see cref="IClientChannel"/> 访问时无歧义。
/// </summary>
public sealed class InterfaceShapeTests
{
    [Fact]
    public void IByteChannel_HasNoIsConnected()
    {
        // 若 IByteChannel 再声明 IsConnected，同时继承 IConnectable 与 IByteChannel 的 IClientChannel 访问它时会产生 CS0229。
        Assert.Empty(typeof(IByteChannel).GetProperties().Where(property => property.Name == "IsConnected"));
    }

    [Fact]
    public void IClientChannel_IsConnected_IsUnambiguous()
    {
        // 编译期证明：ReadIsConnected 能够编译，说明通过 IClientChannel 读取 IsConnected 没有歧义（运行时无需调用它）。
        // 运行期证明：IClientChannel 经接口继承恰好找到一个 IsConnected，且来自 IConnectable。
        List<PropertyInfo> candidates = new[] { typeof(IClientChannel) }
            .Concat(typeof(IClientChannel).GetInterfaces())
            .SelectMany(type => type.GetProperties())
            .Where(property => property.Name == "IsConnected")
            .ToList();

        PropertyInfo property = Assert.Single(candidates);
        Assert.Equal(typeof(IConnectable), property.DeclaringType);
    }

    private static bool ReadIsConnected(IClientChannel channel) => channel.IsConnected;
}
