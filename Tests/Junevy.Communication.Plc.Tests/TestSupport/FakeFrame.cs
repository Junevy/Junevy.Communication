using System.Buffers;
using System.Buffers.Binary;
using Junevy.Communication.Core.Results;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 测试协议的帧格式（自定义，只用于套件测试，不对应任何真实协议）。
/// 请求：[操作 1][符号 1][编号 4，大端][点数或字数 2，大端][负载]。应答：[状态 1][负载]；状态 0 表示成功，其余为对端错误码。
/// </summary>
internal static class FakeFrame
{
    public const byte ReadWordsOp = 0x01;
    public const byte WriteWordsOp = 0x02;
    public const byte ReadBitsOp = 0x03;
    public const byte WriteBitsOp = 0x04;
    public const byte StatusOk = 0x00;
    public const int HeaderLength = 8;
    public const int StatusLength = 1;
    public const int WordBytes = 2;

    private const int SymbolOffset = 1;
    private const int NumberOffset = 2;
    private const int CountOffset = 6;

    /// <summary>写入请求头。</summary>
    public static void WriteHeader(IBufferWriter<byte> output, byte op, char symbol, int number, int count)
    {
        Span<byte> header = output.GetSpan(HeaderLength).Slice(0, HeaderLength);
        header[0] = op;
        header[SymbolOffset] = (byte)symbol;
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(NumberOffset), number);
        BinaryPrimitives.WriteUInt16BigEndian(header.Slice(CountOffset), (ushort)count);
        output.Advance(HeaderLength);
    }

    /// <summary>写入负载。</summary>
    public static void WriteBytes(IBufferWriter<byte> output, ReadOnlySpan<byte> bytes)
    {
        output.Write(bytes);
    }

    /// <summary>读取请求头：操作、符号、编号与点数或字数。</summary>
    public static (byte Op, char Symbol, int Number, int Count) ReadHeader(ReadOnlySpan<byte> request)
    {
        return (request[0],
                (char)request[SymbolOffset],
                BinaryPrimitives.ReadInt32BigEndian(request.Slice(NumberOffset)),
                BinaryPrimitives.ReadUInt16BigEndian(request.Slice(CountOffset)));
    }

    /// <summary>解析数据应答：状态非 0 为对端错误；长度不符为协议违规。</summary>
    public static CommResult<byte[]> ParseData(ReadOnlySpan<byte> frame, int expectedLength)
    {
        if (frame.Length < StatusLength)
            return CommResult<byte[]>.Fail("The response is empty.", CommErrorKind.ProtocolViolation);

        byte status = frame[0];
        if (status != StatusOk)
            return CommResult<byte[]>.Fail($"The device returned status 0x{status:X2}.", CommErrorKind.RemoteError, status);

        if (frame.Length - StatusLength != expectedLength)
            return CommResult<byte[]>.Fail("The response length does not match the request.", CommErrorKind.ProtocolViolation);

        return CommResult<byte[]>.Success(frame.Slice(StatusLength).ToArray());
    }

    /// <summary>解析无数据的应答（写入）。</summary>
    public static CommResult ParseAck(ReadOnlySpan<byte> frame)
    {
        CommResult<byte[]> data = ParseData(frame, 0);
        return data.ToResult();
    }
}
