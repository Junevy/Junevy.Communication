using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Junevy.Communication.WebApi.Tests;

/// <summary>
/// 基于 <see cref="TcpListener"/> 的最小 HTTP/1.1 服务端（127.0.0.1 的随机端口），只用于端到端测试：读到 <c>\r\n\r\n</c> 后按 <c>Content-Length</c> 读正文，
/// 然后按脚本对每个请求做一件事——写回原始响应、接受但不回应、写一半响应后断开、直接断开。不使用 HttpListener（非管理员需要 URL ACL）。
/// 脚本按请求到达的顺序消费，与连接无关；脚本用完后的请求得到 599。
/// </summary>
internal sealed class RawHttpServer : IDisposable
{
    private const string Crlf = "\r\n";

    private static readonly byte[] HeaderEnd = { 13, 10, 13, 10 };

    private readonly TcpListener listener;
    private readonly object sync = new object();
    private readonly Queue<Step> script = new Queue<Step>();
    private readonly List<RawRequest> requests = new List<RawRequest>();
    private readonly List<TcpClient> connections = new List<TcpClient>();
    private readonly List<Task> loops = new List<Task>();
    private readonly SemaphoreSlim requestSignal = new SemaphoreSlim(0);
    private int acceptedConnections;
    private volatile bool stopped;

    private RawHttpServer(TcpListener listener)
    {
        this.listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        lock (sync)
            loops.Add(Task.Run(AcceptLoopAsync));
    }

    private enum StepKind
    {
        Raw,
        Silent,
        Close,
        Half,
    }

    /// <summary>监听端口。</summary>
    public int Port { get; }

    /// <summary>服务器的基地址，例如 <c>http://127.0.0.1:50321</c>。</summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>已接受的连接数。</summary>
    public int AcceptedConnections => Volatile.Read(ref acceptedConnections);

    /// <summary>已收到的请求（快照，按到达顺序）。</summary>
    public IReadOnlyList<RawRequest> Requests
    {
        get
        {
            lock (sync)
                return requests.ToArray();
        }
    }

    /// <summary>启动服务端。</summary>
    public static RawHttpServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new RawHttpServer(listener);
    }

    /// <summary>得到一个当前没有任何进程监听的回环端口（连接会被拒绝）。</summary>
    public static int ReserveClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>追加一个文本响应（带 Content-Length）。</summary>
    public RawHttpServer Respond(int status, string body = "", string? contentType = "application/json; charset=utf-8", IDictionary<string, string>? headers = null)
        => RespondBytes(status, Encoding.UTF8.GetBytes(body), contentType, headers);

    /// <summary>追加一个字节响应（带 Content-Length）。</summary>
    public RawHttpServer RespondBytes(int status, byte[] body, string? contentType, IDictionary<string, string>? headers = null)
    {
        var head = new StringBuilder($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Length: {body.Length}\r\n");
        AppendHeaders(head, contentType, headers);
        return Enqueue(new Step(StepKind.Raw, Concat(Encoding.ASCII.GetBytes(head.Append("\r\n").ToString()), body)));
    }

    /// <summary>追加一个分块传输（<c>Transfer-Encoding: chunked</c>）的响应。</summary>
    public RawHttpServer RespondChunked(int status, string body, int chunkSize)
    {
        string head = $"HTTP/1.1 {status} {Reason(status)}{Crlf}Transfer-Encoding: chunked{Crlf}Content-Type: text/plain; charset=utf-8{Crlf}{Crlf}";
        var message = new List<byte>(Encoding.ASCII.GetBytes(head));
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        for (int offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            int size = Math.Min(chunkSize, bytes.Length - offset);
            message.AddRange(Encoding.ASCII.GetBytes(size.ToString("X") + Crlf));
            message.AddRange(bytes.Skip(offset).Take(size));
            message.AddRange(Encoding.ASCII.GetBytes(Crlf));
        }

        message.AddRange(Encoding.ASCII.GetBytes("0" + Crlf + Crlf));
        return Enqueue(new Step(StepKind.Raw, message.ToArray()));
    }

    /// <summary>追加"接受请求但不回应"：连接保持打开，直到服务端释放。</summary>
    public RawHttpServer Silent() => Enqueue(new Step(StepKind.Silent, Array.Empty<byte>()));

    /// <summary>追加"收到请求后直接断开连接，不写任何响应"。</summary>
    public RawHttpServer CloseWithoutResponse() => Enqueue(new Step(StepKind.Close, Array.Empty<byte>()));

    /// <summary>追加"写一半响应后断开"：头里声明了 100 字节，只写 10 字节。</summary>
    public RawHttpServer HalfResponse()
        => Enqueue(new Step(StepKind.Half, Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 100\r\n\r\n0123456789")));

    /// <summary>等到至少收到 <paramref name="count"/> 个请求（带超时），不轮询。</summary>
    public async Task WaitForRequestsAsync(int count, TimeSpan limit)
    {
        for (int i = 0; i < count; i++)
        {
            if (!await requestSignal.WaitAsync(limit))
                throw new TimeoutException("The server did not receive the expected request in time.");
        }
    }

    /// <summary>停止监听、断开所有连接并等待内部循环结束。可重复调用。</summary>
    public void Dispose()
    {
        if (stopped)
            return;

        stopped = true;
        listener.Stop();
        Task[] pending;
        lock (sync)
        {
            foreach (TcpClient connection in connections)
                connection.Close();

            pending = loops.ToArray();
        }

        Task.WhenAll(pending).Wait(TimeSpan.FromSeconds(5));
    }

    private RawHttpServer Enqueue(Step step)
    {
        lock (sync)
            script.Enqueue(step);
        return this;
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopped)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
            {
                return;
            }

            Interlocked.Increment(ref acceptedConnections);
            lock (sync)
            {
                connections.Add(client);
                loops.Add(Task.Run(() => ServeAsync(client)));
            }
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            NetworkStream stream = client.GetStream();
            while (!stopped)
            {
                RawRequest? request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request == null)
                    return;

                Step? step;
                lock (sync)
                {
                    requests.Add(request);
                    step = script.Count > 0 ? script.Dequeue() : null;
                }

                requestSignal.Release();
                if (!await ApplyAsync(stream, client, step).ConfigureAwait(false))
                    return;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
        {
            // 客户端或服务端释放时连接被关闭，属于预期。
        }
        finally
        {
            client.Close();
        }
    }

    // 返回 false 表示这条连接到此结束。
    private static async Task<bool> ApplyAsync(NetworkStream stream, TcpClient client, Step? step)
    {
        if (step == null)
        {
            byte[] fallback = Encoding.ASCII.GetBytes("HTTP/1.1 599 No Script\r\nContent-Length: 0\r\n\r\n");
            await stream.WriteAsync(fallback, 0, fallback.Length).ConfigureAwait(false);
            return true;
        }

        switch (step.Kind)
        {
            case StepKind.Raw:
                await stream.WriteAsync(step.Payload, 0, step.Payload.Length).ConfigureAwait(false);
                return true;
            case StepKind.Half:
                await stream.WriteAsync(step.Payload, 0, step.Payload.Length).ConfigureAwait(false);
                client.Client.Shutdown(SocketShutdown.Both);
                return false;
            case StepKind.Close:
                return false;
            default:
                // Silent：保持连接，读到客户端关闭为止。
                await DrainUntilClosedAsync(stream).ConfigureAwait(false);
                return false;
        }
    }

    private static async Task DrainUntilClosedAsync(NetworkStream stream)
    {
        var buffer = new byte[1024];
        while (await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false) > 0)
        {
        }
    }

    private static async Task<RawRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var received = new List<byte>();
        var buffer = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOf(received, HeaderEnd)) < 0)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0)
                return null;

            received.AddRange(buffer.Take(read));
        }

        string head = Encoding.ASCII.GetString(received.Take(headerEnd).ToArray());
        string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        string[] requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }

        int length = headers.TryGetValue("Content-Length", out string? value) ? int.Parse(value) : 0;
        int bodyStart = headerEnd + HeaderEnd.Length;
        while (received.Count - bodyStart < length)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0)
                return null;

            received.AddRange(buffer.Take(read));
        }

        return new RawRequest(requestLine[0], requestLine[1], headers, received.Skip(bodyStart).Take(length).ToArray());
    }

    private static int IndexOf(List<byte> data, byte[] pattern)
    {
        for (int i = 0; i + pattern.Length <= data.Count; i++)
        {
            int j = 0;
            while (j < pattern.Length && data[i + j] == pattern[j])
                j++;

            if (j == pattern.Length)
                return i;
        }

        return -1;
    }

    private static void AppendHeaders(StringBuilder head, string? contentType, IDictionary<string, string>? headers)
    {
        if (contentType != null)
            head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (headers == null)
            return;

        foreach (KeyValuePair<string, string> header in headers)
            head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        302 => "Found",
        500 => "Internal Server Error",
        _ => "Status",
    };

    private sealed class Step
    {
        public Step(StepKind kind, byte[] payload)
        {
            Kind = kind;
            Payload = payload;
        }

        public StepKind Kind { get; }

        public byte[] Payload { get; }
    }
}

/// <summary><see cref="RawHttpServer"/> 收到的一个请求。</summary>
internal sealed class RawRequest
{
    public RawRequest(string method, string target, IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        Method = method;
        Target = target;
        Headers = headers;
        Body = body;
    }

    /// <summary>HTTP 方法。</summary>
    public string Method { get; }

    /// <summary>请求行里的目标（路径与已编码的查询）。</summary>
    public string Target { get; }

    /// <summary>请求头（头名不区分大小写）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>请求正文。</summary>
    public byte[] Body { get; }
}
