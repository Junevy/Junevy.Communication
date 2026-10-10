using System.Buffers.Binary;

namespace Junevy.Communication.Plc.Tests;

/// <summary>
/// 测试设备：在内存中保存字与位，按 <see cref="FakeFrame"/> 的格式处理请求并生成应答。
/// 字的应答使用小端布局（与 <c>PlcDataFormat</c> 默认的 DCBA 字序一致）。
/// </summary>
internal sealed class FakeDevice
{
    private readonly Dictionary<(char Symbol, int Number), ushort> words = new Dictionary<(char Symbol, int Number), ushort>();
    private readonly Dictionary<(char Symbol, int Number), bool> bits = new Dictionary<(char Symbol, int Number), bool>();

    /// <summary>设置字的值（测试用）。</summary>
    public void SetWord(char symbol, int number, ushort value) => words[(symbol, number)] = value;

    /// <summary>读取字的值；未写过的字为 0。</summary>
    public ushort GetWord(char symbol, int number) => words.TryGetValue((symbol, number), out ushort value) ? value : (ushort)0;

    /// <summary>设置位的值（测试用）。</summary>
    public void SetBit(char symbol, int number, bool value) => bits[(symbol, number)] = value;

    /// <summary>读取位的值；未写过的位为 false。</summary>
    public bool GetBit(char symbol, int number) => bits.TryGetValue((symbol, number), out bool value) && value;

    /// <summary>处理一个请求并返回应答帧。</summary>
    /// <param name="request">请求帧。</param>
    /// <returns>应答帧。</returns>
    public byte[] Respond(byte[] request)
    {
        (byte op, char symbol, int number, int count) = FakeFrame.ReadHeader(request);
        ReadOnlySpan<byte> payload = request.AsSpan(FakeFrame.HeaderLength);
        switch (op)
        {
            case FakeFrame.ReadWordsOp:
                return ReadWords(symbol, number, count);
            case FakeFrame.WriteWordsOp:
                WriteWords(symbol, number, payload);
                return Acknowledge();
            case FakeFrame.ReadBitsOp:
                return ReadBits(symbol, number, count);
            case FakeFrame.WriteBitsOp:
                WriteBits(symbol, number, payload);
                return Acknowledge();
            default:
                throw new InvalidOperationException($"The test device does not support operation 0x{op:X2}.");
        }
    }

    private byte[] ReadWords(char symbol, int number, int count)
    {
        byte[] response = new byte[FakeFrame.StatusLength + count * FakeFrame.WordBytes];
        response[0] = FakeFrame.StatusOk;
        for (int i = 0; i < count; i++)
        {
            int at = FakeFrame.StatusLength + i * FakeFrame.WordBytes;
            BinaryPrimitives.WriteUInt16LittleEndian(response.AsSpan(at, FakeFrame.WordBytes), GetWord(symbol, number + i));
        }

        return response;
    }

    private void WriteWords(char symbol, int number, ReadOnlySpan<byte> payload)
    {
        for (int i = 0; i < payload.Length / FakeFrame.WordBytes; i++)
            SetWord(symbol, number + i, BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(i * FakeFrame.WordBytes, FakeFrame.WordBytes)));
    }

    private byte[] ReadBits(char symbol, int number, int count)
    {
        byte[] response = new byte[FakeFrame.StatusLength + count];
        response[0] = FakeFrame.StatusOk;
        for (int i = 0; i < count; i++)
            response[FakeFrame.StatusLength + i] = (byte)(GetBit(symbol, number + i) ? 1 : 0);

        return response;
    }

    private void WriteBits(char symbol, int number, ReadOnlySpan<byte> payload)
    {
        for (int i = 0; i < payload.Length; i++)
            SetBit(symbol, number + i, payload[i] != 0);
    }

    private static byte[] Acknowledge() => new[] { FakeFrame.StatusOk };
}
