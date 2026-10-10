namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 测试用的软元件定义：只保存构造参数，供地址解析器与套件测试使用。
/// </summary>
internal sealed class TestDevice : IPlcDeviceDefinition
{
    public TestDevice(string symbol, PlcAddressUnit unit, int radix, int maxNumber)
    {
        Symbol = symbol;
        Unit = unit;
        Radix = radix;
        MaxNumber = maxNumber;
    }

    public string Symbol { get; }

    public PlcAddressUnit Unit { get; }

    public int Radix { get; }

    public int MaxNumber { get; }
}
