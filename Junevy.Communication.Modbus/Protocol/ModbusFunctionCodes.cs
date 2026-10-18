namespace Junevy.Communication.Modbus.Protocol;

/// <summary>Modbus 功能码（规范 V1.1b3 第 6 节）。只列出本库使用的功能码。</summary>
internal static class ModbusFunctionCodes
{
    public const byte ReadCoils = 0x01;
    public const byte ReadDiscreteInputs = 0x02;
    public const byte ReadHoldingRegisters = 0x03;
    public const byte ReadInputRegisters = 0x04;
    public const byte WriteSingleCoil = 0x05;
    public const byte WriteSingleRegister = 0x06;
    public const byte WriteMultipleCoils = 0x0F;
    public const byte WriteMultipleRegisters = 0x10;
    public const byte MaskWriteRegister = 0x16;
    public const byte ReadWriteMultipleRegisters = 0x17;

    /// <summary>异常应答的标志位：异常应答的功能码是请求功能码与 0x80 的或。</summary>
    public const byte ExceptionFlag = 0x80;
}
