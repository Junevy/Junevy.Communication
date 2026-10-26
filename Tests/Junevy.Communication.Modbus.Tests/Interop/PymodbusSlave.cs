using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Junevy.Communication.Modbus.Tests.Interop;

/// <summary>
/// 在子进程中运行的 pymodbus 从站（脚本 <c>Interop/pymodbus_slave.py</c>）。数据布局见脚本开头的说明。
/// 收到的每个请求记为（站号，功能码），供测试断言客户端实际发出的功能码。
/// </summary>
internal sealed class PymodbusSlave : IAsyncDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);

    private readonly Process _process;
    private readonly ConcurrentQueue<(int Unit, int Function)> _requests = new();
    private readonly StringBuilder _errors = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PymodbusSlave(Process process)
    {
        _process = process;
    }

    /// <summary>网络传输时从站监听的端口；串口时为 0。</summary>
    public int Port { get; private set; }

    /// <summary>到目前为止收到的请求（按到达顺序）。</summary>
    public IReadOnlyList<(int Unit, int Function)> Requests => _requests.ToArray();

    /// <summary>在 127.0.0.1 的空闲端口上启动 TCP 从站。</summary>
    public static Task<PymodbusSlave> StartTcpAsync(ModbusFraming framing, string reject = "")
    {
        int port = FreeTcpPort();
        return StartAsync($"--transport tcp --framer {Framer(framing)} --port {port} --reject \"{reject}\"", port);
    }

    /// <summary>在 127.0.0.1 的空闲端口上启动 UDP 从站。</summary>
    public static Task<PymodbusSlave> StartUdpAsync(ModbusFraming framing, string reject = "")
    {
        int port = FreeUdpPort();
        return StartAsync($"--transport udp --framer {Framer(framing)} --port {port} --reject \"{reject}\"", port);
    }

    /// <summary>在串口上启动 RTU 从站。</summary>
    public static Task<PymodbusSlave> StartSerialAsync(string portName, int baudRate, string reject = "")
        => StartAsync($"--transport serial --framer rtu --serial {portName} --baud {baudRate} --reject \"{reject}\"", 0);

    /// <summary>等待从站记录到至少 <paramref name="count"/> 个请求（标准输出异步到达，可能晚于应答）。</summary>
    public async Task<IReadOnlyList<(int Unit, int Function)>> WaitForRequestsAsync(int count)
    {
        var deadline = Stopwatch.StartNew();
        while (_requests.Count < count && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Delay(20);
        return Requests;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            await Task.Run(() =>
            {
                if (!_process.WaitForExit(5000))
                    _process.Kill();
            });
        }
        catch (InvalidOperationException)
        {
            // 进程已经退出。
        }

        _process.Dispose();
    }

    private static async Task<PymodbusSlave> StartAsync(string arguments, int port)
    {
        string python = Environment.GetEnvironmentVariable(PymodbusFactAttribute.VariableName)!;
        string script = Path.Combine(AppContext.BaseDirectory, "Interop", "pymodbus_slave.py");
        var info = new ProcessStartInfo(python, $"-u \"{script}\" {arguments}")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var slave = new PymodbusSlave(new Process { StartInfo = info, EnableRaisingEvents = true }) { Port = port };
        slave._process.OutputDataReceived += (_, e) => slave.OnOutput(e.Data);
        slave._process.ErrorDataReceived += (_, e) => slave.OnError(e.Data);
        slave._process.Exited += (_, _) => slave._ready.TrySetResult(false);
        slave._process.Start();
        slave._process.BeginOutputReadLine();
        slave._process.BeginErrorReadLine();

        Task finished = await Task.WhenAny(slave._ready.Task, Task.Delay(ReadyTimeout));
        if (finished != slave._ready.Task || !slave._ready.Task.Result)
        {
            await slave.DisposeAsync();
            throw new InvalidOperationException($"pymodbus slave did not start ({arguments}). stderr:\n{slave.ErrorText()}");
        }

        return slave;
    }

    private void OnOutput(string? line)
    {
        if (line is null)
            return;
        if (line == "READY")
        {
            _ready.TrySetResult(true);
            return;
        }

        string[] parts = line.Split(' ');
        if (parts.Length == 3 && parts[0] == "REQ")
            _requests.Enqueue((int.Parse(parts[1]), int.Parse(parts[2])));
    }

    private void OnError(string? line)
    {
        if (line is null)
            return;
        lock (_errors)
            _errors.AppendLine(line);
    }

    private string ErrorText()
    {
        lock (_errors)
            return _errors.ToString();
    }

    private static string Framer(ModbusFraming framing) => framing == ModbusFraming.Mbap ? "socket" : "rtu";

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int FreeUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }
}
