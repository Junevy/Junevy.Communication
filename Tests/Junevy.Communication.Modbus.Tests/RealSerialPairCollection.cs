namespace Junevy.Communication.Modbus.Tests;

/// <summary>
/// 打开真实串口对（<c>JUNEVY_SERIAL_PAIR</c>）的测试集合。同一对端口同一时间只能被一个测试打开，
/// 而不同集合之间默认并行，因此凡是打开真实串口对的测试类都放进这个集合，串行执行。
/// </summary>
[CollectionDefinition(Name)]
public sealed class RealSerialPairCollection
{
    public const string Name = "RealSerialPair";
}
