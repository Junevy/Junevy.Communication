using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc.Pipeline;

/// <summary>
/// 流水线看到的协议钩子。<see cref="PlcClientBase{TAddress}"/> 以私有适配器实现它，把 protected 钩子转交给流水线；
/// 流水线因此不依赖客户端类型，可以单独测试。
/// </summary>
internal interface IPlcProtocol<TAddress> where TAddress : PlcAddress
{
    bool TryParseAddress(string address, [NotNullWhen(true)] out TAddress? parsed, [NotNullWhen(false)] out string? error);

    CommResult ValidateRange(TAddress address, PlcAccess access, int count);

    int GetMaxReadPoints(TAddress address, PlcAccess access);

    int GetMaxWritePoints(TAddress address, PlcAccess access);

    void BuildReadWordsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output);

    CommResult<byte[]> ParseReadWordsResponse(int count, byte[] frame);

    void BuildWriteWordsRequest(TAddress address, int offset, ReadOnlyMemory<byte> words, IBufferWriter<byte> output);

    void BuildReadBitsRequest(TAddress address, int offset, int count, IBufferWriter<byte> output);

    CommResult<bool[]> ParseReadBitsResponse(int count, byte[] frame);

    void BuildWriteBitsRequest(TAddress address, int offset, ReadOnlyMemory<bool> values, IBufferWriter<byte> output);

    CommResult ParseWriteResponse(byte[] frame);
}
