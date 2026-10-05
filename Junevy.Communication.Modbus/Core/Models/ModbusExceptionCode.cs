namespace Junevy.Communication.Modbus.Core.Models;

/// <summary>Modbus 响应异常码（MODBUS Application Protocol V1.1b3 第 7 节）。</summary>
public enum ModbusExceptionCode
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    ServerDeviceFailure = 0x04,
    Acknowledge = 0x05,
    ServerDeviceBusy = 0x06,
    MemoryParityError = 0x08,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetDeviceFailedToRespond = 0x0B,
}

public static class ModbusExceptionCodeExtensions
{
    public static string Describe(this ModbusExceptionCode code) => code switch
    {
        ModbusExceptionCode.IllegalFunction => "Illegal function",
        ModbusExceptionCode.IllegalDataAddress => "Illegal data address",
        ModbusExceptionCode.IllegalDataValue => "Illegal data value",
        ModbusExceptionCode.ServerDeviceFailure => "Server device failure",
        ModbusExceptionCode.Acknowledge => "Acknowledge",
        ModbusExceptionCode.ServerDeviceBusy => "Server device busy",
        ModbusExceptionCode.MemoryParityError => "Memory parity error",
        ModbusExceptionCode.GatewayPathUnavailable => "Gateway path unavailable",
        ModbusExceptionCode.GatewayTargetDeviceFailedToRespond => "Gateway target device failed to respond",
        _ => $"Unknown exception code 0x{(byte)code:X2}",
    };
}
