using System.Text.RegularExpressions;
using Junevy.Communication.Channels;
using Junevy.Communication.Core.Buffers;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Plc;

namespace Junevy.Communication.DebugAssistant.Tests.Support;

/// <summary>
/// 内存中的 PLC 客户端（实现 <see cref="IPlcClient"/>）：地址 <c>Dn</c> 对应第 2n 个字节开始的字；读写直接作用于这块内存。
/// 可以让读取在 <see cref="ReadGate"/> 上等待（模拟慢速的一轮），用来测试轮询的跳过与刷新。
/// </summary>
internal sealed class FakePlcClient : IPlcClient
{
    private static readonly Regex Word = new(@"^D(\d+)$", RegexOptions.CultureInvariant);
    private readonly Dictionary<int, byte> _memory = [];
    private readonly object _gate = new();
    private bool _connected;

    /// <summary>非空时，每次读取先等待它（测试用来阻塞一轮）。</summary>
    public Func<CancellationToken, Task>? ReadGate { get; set; }

    /// <summary>读取的次数（包括被阻塞的读取）。</summary>
    public int ReadCount { get; private set; }

    /// <summary>为真时连接失败。</summary>
    public bool FailConnect { get; set; }

    /// <summary>非空时，读取直接返回它（模拟协议错误、超时等）。</summary>
    public CommResult<byte[]>? ReadOverride { get; set; }

    public int DisposeCount { get; private set; }

    public PlcDataFormat DataFormat { get; } = new(WordOrder.ABCD);

    public string Name => "fake-plc";

    public ConnectionState State => _connected ? ConnectionState.Connected : ConnectionState.Disconnected;

    public bool IsConnected => _connected;

    /// <summary>页签不读取统计；假客户端不提供（内部构造函数的类型无法在测试中创建）。</summary>
    public ConnectionStatistics Statistics => throw new NotSupportedException("The fake PLC client has no statistics.");

    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>引发连接状态变化（例如模拟掉线）。</summary>
    public void RaiseConnectionState(ConnectionState previous, ConnectionState current)
    {
        _connected = current == ConnectionState.Connected;
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previous, current, DisconnectReason.None, null, 0));
    }

    /// <summary>引发一个迟到的事件：事件里的状态已经过期，客户端的当前状态不变（库在线程池上触发事件时可能出现）。</summary>
    public void RaiseLateEvent(ConnectionState previous, ConnectionState stale) =>
        StateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previous, stale, DisconnectReason.None, null, 0));

    public Task<CommResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (FailConnect)
        {
            return Task.FromResult(CommResult.Fail("The fake connection was refused.", CommErrorKind.ConnectionClosed));
        }

        RaiseConnectionState(ConnectionState.Disconnected, ConnectionState.Connected);
        return Task.FromResult(CommResult.Success());
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connected)
        {
            RaiseConnectionState(ConnectionState.Connected, ConnectionState.Disconnected);
        }

        return Task.CompletedTask;
    }

    public Task<bool> WaitForConnectedAsync(int timeout, CancellationToken cancellationToken = default) => Task.FromResult(_connected);

    public CommResult<PlcAddress> ParseAddress(string address)
    {
        Match match = Word.Match(address);
        return match.Success
            ? CommResult<PlcAddress>.Success(new FakeAddress(address, int.Parse(match.Groups[1].Value)))
            : CommResult<PlcAddress>.Fail($"“{address}”不是假客户端认识的地址（应为 D 加编号）。", CommErrorKind.InvalidRequest);
    }

    public async Task<CommResult<byte[]>> ReadAsync(string address, int count, CancellationToken cancellationToken = default) =>
        await ReadBytesCoreAsync(address, count * 2, cancellationToken);

    public Task<CommResult> WriteAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        WriteBytesAsync(address, data, cancellationToken);

    public async Task<CommResult<bool[]>> ReadBitsAsync(string address, int count, CancellationToken cancellationToken = default)
    {
        CommResult<byte[]> bytes = await ReadBytesCoreAsync(address, (count + 7) / 8, cancellationToken);
        if (!bytes.IsSuccess)
        {
            return CommResult<bool[]>.Fail(bytes.ErrorMessage, bytes.ErrorKind);
        }

        return CommResult<bool[]>.Success(Enumerable.Range(0, count).Select(index => (bytes.Data![index / 8] & (1 << (index % 8))) != 0).ToArray());
    }

    public Task<CommResult> WriteBitsAsync(string address, ReadOnlyMemory<bool> values, CancellationToken cancellationToken = default)
    {
        byte[] bytes = new byte[(values.Length + 7) / 8];
        for (int index = 0; index < values.Length; index++)
        {
            if (values.Span[index])
            {
                bytes[index / 8] |= (byte)(1 << (index % 8));
            }
        }

        return WriteBytesAsync(address, bytes, cancellationToken);
    }

    public Task<CommResult<byte[]>> ReadBytesAsync(string address, int count, CancellationToken cancellationToken = default) =>
        ReadBytesCoreAsync(address, count, cancellationToken);

    public Task<CommResult> WriteBytesAsync(string address, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (!TryOffset(address, out int offset))
        {
            return Task.FromResult(CommResult.Fail($"“{address}”不是假客户端认识的地址。", CommErrorKind.InvalidRequest));
        }

        lock (_gate)
        {
            for (int index = 0; index < data.Length; index++)
            {
                _memory[offset + index] = data.Span[index];
            }
        }

        return Task.FromResult(CommResult.Success());
    }

    public void Dispose() => DisposeCount++;

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<CommResult<byte[]>> ReadBytesCoreAsync(string address, int count, CancellationToken cancellationToken)
    {
        if (!TryOffset(address, out int offset))
        {
            return CommResult<byte[]>.Fail($"“{address}”不是假客户端认识的地址。", CommErrorKind.InvalidRequest);
        }

        lock (_gate)
        {
            ReadCount++;
        }

        if (ReadGate is { } gate)
        {
            await gate(cancellationToken);
        }

        if (ReadOverride is { } failure)
        {
            return failure;
        }

        lock (_gate)
        {
            return CommResult<byte[]>.Success(Enumerable.Range(offset, count)
                .Select(index => _memory.TryGetValue(index, out byte value) ? value : (byte)0)
                .ToArray());
        }
    }

    /// <summary>地址 <c>Dn</c> 的字节偏移为 2n；不是这种写法时返回 false。</summary>
    private static bool TryOffset(string address, out int offset)
    {
        Match match = Word.Match(address);
        offset = match.Success ? int.Parse(match.Groups[1].Value) * 2 : -1;
        return match.Success;
    }
}

/// <summary>假客户端的地址：字单位的设备 <c>D</c>。</summary>
internal sealed class FakeAddress(string text, int number) : PlcAddress(text, FakeDevice.Instance, number, bitIndex: null);

/// <summary>假设备定义：字单位，编号到 65535。</summary>
internal sealed class FakeDevice : IPlcDeviceDefinition
{
    public static FakeDevice Instance { get; } = new();

    public string Symbol => "D";

    public PlcAddressUnit Unit => PlcAddressUnit.Word;

    public int Radix => 10;

    public int MaxNumber => 65535;
}
