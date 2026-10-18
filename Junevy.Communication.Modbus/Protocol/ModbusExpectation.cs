namespace Junevy.Communication.Modbus.Protocol;

/// <summary>
/// 一包的期望（P7-D10）：构造请求时记下，解析同一包的应答时使用。包含站号、功能码、读取的点数或字数，以及写入时必须原样回显的字节。
/// </summary>
internal sealed class ModbusExpectation
{
    /// <summary>创建期望。</summary>
    /// <param name="unitId">站号（RTU 与 MBAP 都由帧层检查）。</param>
    /// <param name="functionCode">请求的功能码。</param>
    /// <param name="count">读取的点数（01、02）或字数（03、04、17）；写入时不使用。</param>
    /// <param name="echo">写入时应答必须原样回显的请求字节（05、06、0F、10 为前 4 字节，16 为 6 字节）；读取时为 null。</param>
    public ModbusExpectation(byte unitId, byte functionCode, int count, byte[]? echo)
    {
        UnitId = unitId;
        FunctionCode = functionCode;
        Count = count;
        Echo = echo;
    }

    /// <summary>站号。</summary>
    public byte UnitId { get; }

    /// <summary>请求的功能码。</summary>
    public byte FunctionCode { get; }

    /// <summary>读取的点数（01、02）或字数（03、04、17）。</summary>
    public int Count { get; }

    /// <summary>写入时应答必须原样回显的字节；读取时为 null。</summary>
    public byte[]? Echo { get; }

    /// <summary>读取应答的数据字节数：位功能码按位计，字功能码每字 2 字节。</summary>
    public int ExpectedDataByteCount => FunctionCode is ModbusFunctionCodes.ReadCoils or ModbusFunctionCodes.ReadDiscreteInputs
        ? ModbusBits.ByteCount(Count)
        : Count * 2;
}
