namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 测试用的地址类型：派生自 <see cref="PlcAddress"/>，不增加任何成员。
/// </summary>
internal sealed class TestAddress : PlcAddress
{
    public TestAddress(TestDevice device, int number, int? bitIndex, string text)
        : base(text, device, number, bitIndex)
    {
    }
}
