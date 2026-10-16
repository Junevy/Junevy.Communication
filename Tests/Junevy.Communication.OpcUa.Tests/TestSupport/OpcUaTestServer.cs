using Junevy.Communication.Core;
using Junevy.Communication.Core.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Opc.Ua;
using Opc.Ua.Server.Fluent;
using Opc.Ua.Server.Hosting;

namespace Junevy.Communication.OpcUa.Tests.TestSupport;

/// <summary>
/// 测试服务器的配置（计划第 7 节第 1 条）。
/// </summary>
public sealed class OpcUaTestServerOptions
{
    /// <summary>端点地址；默认由 <see cref="OpcUaTestServer"/> 在分配端口时填入。</summary>
    public string EndpointUrl { get; set; } = string.Empty;

    /// <summary>证书与信任列表的根目录；每次运行一个唯一目录。</summary>
    public string PkiRoot { get; set; } = string.Empty;

    /// <summary>是否开放 <c>None</c> 安全策略的端点（关掉就是只开放加密端点）。</summary>
    public bool IncludeUnsecurePolicyNone { get; set; } = true;

    /// <summary>是否允许匿名登录。</summary>
    public bool AllowAnonymous { get; set; } = true;

    /// <summary>端点里报告的主机名；null 表示与 <see cref="EndpointUrl"/> 相同。</summary>
    public string? ReportedHostName { get; set; }

    /// <summary>服务器上报的每次读 / 写的节点上限。</summary>
    public uint MaxNodesPerReadOrWrite { get; set; } = 5;

    /// <summary>服务器上报的每次浏览的引用数上限。</summary>
    public uint MaxNodesPerBrowse { get; set; } = 2;

    /// <summary>服务器上报的每次订阅的监控项上限。</summary>
    public uint MaxMonitoredItemsPerCall { get; set; } = 5;
}

/// <summary>
/// 进程内托管的官方 OPC UA 服务器（<c>OPCFoundation.NetStandard.Opc.Ua.Server</c> 2.0.1）。
/// 按调研结论用**通用宿主**启动：<c>OpcUaServerHostedService</c> 才是真正监听端口的那一步，
/// 单独调 <c>IOpcUaServerFactory.CreateServer</c> 不会监听（计划第 19.2.12 节）。
/// </summary>
public sealed class OpcUaTestServer : IAsyncDisposable
{
    /// <summary>测试用命名空间 URI。</summary>
    public const string NamespaceUri = "urn:junevy:opcua:test";

    private readonly OpcUaTestServerOptions options;
    private readonly ITelemetryContext telemetry;
    private readonly ILoggerFactory? probeLoggerFactory;
    private readonly Dictionary<string, NodeId> ids = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Counter> counters = new(StringComparer.Ordinal);
    private IHost? host;
    private int disposed;

    private OpcUaTestServer(OpcUaTestServerOptions options, ITelemetryContext telemetry, ILoggerFactory? probeLoggerFactory)
    {
        this.options = options;
        this.telemetry = telemetry;
        this.probeLoggerFactory = probeLoggerFactory;
    }

    /// <summary>测试命名空间在服务器上的索引。</summary>
    public ushort NamespaceIndex { get; private set; }

    /// <summary>端点地址（客户端连这个）。</summary>
    public string EndpointUrl => options.EndpointUrl;

    /// <summary>服务器端点里实际报告的地址（可能带一个不可解析的主机名）。</summary>
    public string AdvertisedUrl => options.ReportedHostName is null
        ? options.EndpointUrl
        : $"opc.tcp://{options.ReportedHostName}:{new Uri(options.EndpointUrl).Port}/UA/";

    /// <summary>服务器是否在运行。</summary>
    public bool IsRunning => host is not null;

    /// <summary>
    /// 启动一个服务器。
    /// </summary>
    /// <param name="options">配置。</param>
    /// <param name="loggerFactory">日志工厂；为 null 时静默。</param>
    /// <returns>已启动的服务器。</returns>
    public static async Task<OpcUaTestServer> StartAsync(OpcUaTestServerOptions options, ILoggerFactory? loggerFactory = null)
    {
        var server = new OpcUaTestServer(
            options,
            DefaultTelemetry.Create(builder =>
            {
                if (loggerFactory is not null)
                {
                    builder.Services.AddSingleton(loggerFactory);
                }
            }),
            loggerFactory);

        await server.StartHostAsync().ConfigureAwait(false);
        return server;
    }

    /// <summary>
    /// 取一个节点的 NodeId（用浏览名，如 <c>Test/Int32</c>）。
    /// </summary>
    /// <param name="browseName">浏览名。</param>
    /// <returns>节点标识。</returns>
    public NodeId Id(string browseName) => ids.TryGetValue(browseName, out NodeId id)
        ? id
        : throw new KeyNotFoundException($"The test server has no node '{browseName}'.");

    /// <summary>
    /// 改一个计数器的值（订阅用例用它制造数据变化）。
    /// </summary>
    /// <param name="browseName">计数器的浏览名。</param>
    /// <param name="delta">增量。</param>
    public void Bump(string browseName, int delta = 1)
    {
        if (counters.TryGetValue(browseName, out Counter? counter))
        {
            counter.Value += delta;
        }
    }

    /// <summary>启动（可在同一端口上重复调用，重启用例用）。</summary>
    /// <returns>异步操作。</returns>
    public async Task StartHostAsync()
    {
        var builder = new HostApplicationBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(telemetry);
        builder.Services.AddOpcUa()
            .AddOpcTcpTransport()
            .AddServer(server =>
            {
                server.ApplicationName = "JunevyOpcUaTestServer";
                server.EndpointUrls.Add(AdvertisedUrl);
                server.IncludeUnsecurePolicyNone = options.IncludeUnsecurePolicyNone;
                server.PkiRoot = options.PkiRoot;
                server.AutoAcceptUntrustedCertificates = true;
                server.ConfigureBuilder = configuration => configuration.SetOperationLimits(new OperationLimits
                {
                    MaxNodesPerRead = options.MaxNodesPerReadOrWrite,
                    MaxNodesPerWrite = options.MaxNodesPerReadOrWrite,
                    MaxNodesPerBrowse = options.MaxNodesPerBrowse,
                    MaxMonitoredItemsPerCall = options.MaxMonitoredItemsPerCall,
                });
                server.UserTokenPolicies.Clear();
                server.UserTokenPolicies.Add(new OpcUaUserTokenPolicy
                {
                    TokenType = options.AllowAnonymous ? UserTokenType.Anonymous : UserTokenType.UserName,
                });
            })
            .AddNodeManager(NamespaceUri, Build);

        host = builder.Build();
        await host.StartAsync().ConfigureAwait(false);
        await WaitUntilReadyAsync().ConfigureAwait(false);
    }

    // 通用宿主返回时服务器只是"开始"启动：TCP 监听已经就绪，地址空间还在建。
    // 这时连上去会拿到 BadServerHalted（F20），因此起好之后先用一次真实会话探一下。
    private async Task WaitUntilReadyAsync()
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            await using var probe = OpcUaClient.Create(
                new OpcUaClientOptions
                {
                    EndpointUrl = options.EndpointUrl,
                    PkiRoot = Path.Combine(options.PkiRoot, "probe"),
                    ConnectTimeout = 3000,
                    ApplicationName = "JunevyOpcUaTestProbe",
                },
                probeLoggerFactory);

            CommResult result = await probe.ConnectAsync().ConfigureAwait(false);

            // 成功即就绪；被策略挡住（无匿名 / 无 None 端点）同样说明服务器已应答，不是"还没起来"。
            if (result.IsSuccess
                || result.ErrorKind is CommErrorKind.NotSupported or CommErrorKind.AuthenticationFailed)
            {
                return;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }

        throw new TimeoutException($"The test server at {options.EndpointUrl} did not become ready.");
    }

    /// <summary>停止（端口随即释放，重启用例用）。</summary>
    /// <returns>异步操作。</returns>
    public async Task StopHostAsync()
    {
        if (host is null)
        {
            return;
        }

        await host.StopAsync().ConfigureAwait(false);
        host = null;
    }

    /// <summary>释放：停服务器并删除证书目录。</summary>
    /// <returns>异步释放。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await StopHostAsync().ConfigureAwait(false);

        try
        {
            Directory.Delete(options.PkiRoot, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // 临时目录清理是尽力而为；残留的证书目录由操作系统回收。
        }
    }

    private void Build(INodeManagerBuilder builder)
    {
        NamespaceIndex = builder.DefaultNamespaceIndex;

        INodeBuilder<FolderState> test = builder.AddFolder("Test", ObjectIds.ObjectsFolder);
        INodeBuilder<FolderState> sub = builder.AddFolder("Sub", test.Node.NodeId);
        INodeBuilder<FolderState> writable = builder.AddFolder("Writable", test.Node.NodeId);
        Record("Test", test.Node.NodeId);
        Record("Sub", sub.Node.NodeId);
        Record("Writable", writable.Node.NodeId);

        // 一条**非层级**的正向引用：浏览"只取正向层级引用"时它不该出现，用来钉住这条过滤。
        ReferenceBuilderExtensions.AddReference(test, sub.Node.NodeId, true, ReferenceTypeIds.GeneratesEvent);

        // 只读节点：读矩阵用，类型各一项。
        Add<bool>(builder, test, "Test", "Bool", () => true, null);
        Add<sbyte>(builder, test, "Test", "SByte", () => -1, null);
        Add<byte>(builder, test, "Test", "Byte", () => 1, null);
        Add<short>(builder, test, "Test", "Int16", () => -1, null);
        Add<ushort>(builder, test, "Test", "UInt16", () => 1, null);
        Add<int>(builder, test, "Test", "Int32", () => 42, null);
        Add<uint>(builder, test, "Test", "UInt32", () => 42, null);
        Add<long>(builder, test, "Test", "Int64", () => 42, null);
        Add<ulong>(builder, test, "Test", "UInt64", () => 42, null);
        Add<float>(builder, test, "Test", "Float", () => 1.5f, null);
        Add<double>(builder, test, "Test", "Double", () => 1.5, null);
        Add<string>(builder, test, "Test", "String", () => "hello", null);
        Add<DateTimeUtc>(builder, test, "Test", "DateTime", () => DateTimeUtc.From(new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc)), null);
        Add<Uuid>(builder, test, "Test", "Guid", () => new Uuid(Guid.Parse("11112222-3333-4444-5555-666677778888")), null);
        Add<ByteString>(builder, test, "Test", "ByteString", () => new ByteString(new byte[] { 1, 2 }), null);
        Add<ArrayOf<byte>>(builder, test, "Test", "ByteArray", () => ArrayOf.ToArrayOf<byte>(new byte[] { 7, 8 }), null);
        Add<ArrayOf<int>>(builder, test, "Test", "Int32Array", () => ArrayOf.ToArrayOf<int>(new[] { 1, 2, 3 }), null);
        Add<ArrayOf<double>>(builder, test, "Test", "DoubleArray", () => ArrayOf.ToArrayOf<double>(new[] { 1.5, 2.5 }), null);
        Add<ArrayOf<string>>(builder, test, "Test", "StringArray", () => ArrayOf.ToArrayOf<string>(new[] { "a", "b" }), null);
        Add<LocalizedText>(builder, test, "Test", "LocalizedText", () => new LocalizedText("en", "hello"), null);
        Add<StatusCode>(builder, test, "Test", "StatusCode", () => new StatusCode(0x40000000u, "Uncertain"), null);
        Add<NodeId>(builder, test, "Test", "NodeId", () => new NodeId("Other", NamespaceIndex), null);

        // 不受支持的内置类型：只读出文本或码值，结构体 / 多维数组 / 异构数组读出时 Value 为 null。
        // 注意多维数组放不上来：流式（fluent）表面拒绝把它封进 Variant（F22）。
        Add<QualifiedName>(builder, test, "Test", "QualifiedName", () => new QualifiedName("Speed", NamespaceIndex), null);
        Add<ArrayOf<Variant>>(builder, test, "Test", "VariantArray", () => ArrayOf.ToArrayOf<Variant>(new[] { new Variant(1), new Variant("x") }), null);
        // 流式表面只收内置类型、枚举与 ExtensionObject（F22）；结构体要包一层才能当变量值发布。
        Add<ExtensionObject>(
            builder,
            test,
            "Test",
            "Struct",
            () => new ExtensionObject(new Argument
            {
                Name = "name",
                ValueRank = ValueRanks.Scalar,
                ArrayDimensions = ArrayOf.ToArrayOf(new uint[] { 0 }),
                Value = new Variant(1),
            }),
            null);

        // 可写节点：写矩阵用，每种可写类型一项，取值与读矩阵不同，便于区分往返。
        Add<bool>(builder, writable, "Writable", "Bool", () => false, _ => { });
        Add<sbyte>(builder, writable, "Writable", "SByte", () => -2, _ => { });
        Add<byte>(builder, writable, "Writable", "Byte", () => 2, _ => { });
        Add<short>(builder, writable, "Writable", "Int16", () => -3, _ => { });
        Add<ushort>(builder, writable, "Writable", "UInt16", () => 3, _ => { });
        Add<int>(builder, writable, "Writable", "Int32", () => -4, _ => { });
        Add<uint>(builder, writable, "Writable", "UInt32", () => 4, _ => { });
        Add<long>(builder, writable, "Writable", "Int64", () => -5, _ => { });
        Add<ulong>(builder, writable, "Writable", "UInt64", () => 5, _ => { });
        Add<float>(builder, writable, "Writable", "Float", () => -1.25f, _ => { });
        Add<double>(builder, writable, "Writable", "Double", () => -2.25, _ => { });
        Add<string>(builder, writable, "Writable", "String", () => "before", _ => { });
        Add<DateTimeUtc>(builder, writable, "Writable", "DateTime", () => DateTimeUtc.From(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)), _ => { });
        Add<Uuid>(builder, writable, "Writable", "Guid", () => new Uuid(Guid.Parse("99998888-7777-6666-5555-444433332222")), _ => { });
        Add<ByteString>(builder, writable, "Writable", "ByteString", () => new ByteString(new byte[] { 9 }), _ => { });
        AddRaw(builder, writable, "Writable", "BoolArray", ArrayOf.ToArrayOf<bool>(new[] { true, false }));
        AddRaw(builder, writable, "Writable", "SByteArray", ArrayOf.ToArrayOf<sbyte>(new sbyte[] { -1, -2 }));
        AddRaw(builder, writable, "Writable", "Int16Array", ArrayOf.ToArrayOf<short>(new short[] { -3, -4 }));
        AddRaw(builder, writable, "Writable", "UInt16Array", ArrayOf.ToArrayOf<ushort>(new ushort[] { 3, 4 }));
        AddRaw(builder, writable, "Writable", "Int32Array", ArrayOf.ToArrayOf<int>(new[] { -5, -6 }));
        AddRaw(builder, writable, "Writable", "UInt32Array", ArrayOf.ToArrayOf<uint>(new uint[] { 5, 6 }));
        AddRaw(builder, writable, "Writable", "Int64Array", ArrayOf.ToArrayOf<long>(new long[] { -7, -8 }));
        AddRaw(builder, writable, "Writable", "UInt64Array", ArrayOf.ToArrayOf<ulong>(new ulong[] { 7, 8 }));
        AddRaw(builder, writable, "Writable", "FloatArray", ArrayOf.ToArrayOf<float>(new[] { -1.5f, -2.5f }));
        AddRaw(builder, writable, "Writable", "DoubleArray", ArrayOf.ToArrayOf<double>(new[] { -3.5, -4.5 }));
        AddRaw(builder, writable, "Writable", "StringArray", ArrayOf.ToArrayOf<string>(new[] { "x", "y" }));
        AddRaw(builder, writable, "Writable", "DateTimeArray", ArrayOf.ToArrayOf<DateTimeUtc>(new[] { DateTimeUtc.From(new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc)) }));
        AddRaw(builder, writable, "Writable", "GuidArray", ArrayOf.ToArrayOf<Uuid>(new[] { new Uuid(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")) }));

        var counterBox = new Counter.Box();
        (IVariableBuilder<int> CounterVariable, IValueUpdater<int> CounterUpdater) = Add<int>(
            builder,
            test,
            "Test",
            "Counter",
            () => counterBox.Value,
            value => counterBox.Value = value);
        counters["Test/Counter"] = new Counter(counterBox, CounterUpdater);

        Add<int>(builder, test, "Test", "ReadOnly", () => 9, null);

        for (int index = 0; index < 12; index++)
        {
            int value = index;
            Add<int>(builder, test, "Test", "Scalar_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), () => value, _ => { });
        }

        Add<int>(builder, sub, "Sub", "Leaf", () => 5, _ => { });
    }

    private (IVariableBuilder<T> Variable, IValueUpdater<T> Updater) Add<T>(
        INodeManagerBuilder builder,
        INodeBuilder parent,
        string parentPath,
        string browseName,
        Func<T> read,
        Action<T>? write)
    {
        string path = parentPath + "/" + browseName;
        IVariableBuilder<T> variable = builder.AddVariable<T>(browseName, parent.Node.NodeId);

        // OnRead 单独用不够，服务器会一直回 BadWaitingForInitialData；必须 Bind + SetValue（F11）。
        RuntimeValueBuilderExtensions.Bind(variable, out IValueUpdater<T> updater);
        updater.SetValue(read());

        // 嵌套文件夹不能从命名空间根按浏览路径解析（"Sub/Leaf" 找不到），因此按父节点的 NodeId 定位（F12）。
        INodeBuilder created = builder
            .Node(parent.Node.NodeId)
            .Child(new QualifiedName(browseName, builder.DefaultNamespaceIndex));

        if (write is not null)
        {
            variable.OnWrite(value => { updater.SetValue(value); write(value); });
            AccessLevelBuilderExtensions.Writable(created, true);
        }

        Record(path, created.Node.NodeId);
        return (variable, updater);
    }

    /// <summary>改计数器的值：订阅用例用它触发数据变更。</summary>
    /// <param name="browsePath">计数器的浏览路径。</param>
    /// <param name="value">新值。</param>
    public void SetCounter(string browsePath, int value)
    {
        if (counters.TryGetValue(browsePath, out Counter? counter))
        {
            counter.Set(value);
        }
    }

    private void Record(string browseName, NodeId nodeId) => ids[browseName] = nodeId;

    /// <summary>
    /// 数组写入用的节点。流式（fluent）强类型写入只接受与声明类型**完全一致**的 .NET 值，
    /// 本库写出去的是 <c>T[]</c>（公开 API 不暴露 SDK 的 <c>ArrayOf&lt;T&gt;</c>），
    /// 因此这些节点走原始写处理器，自己把 <c>Variant</c> 解回 <c>ArrayOf&lt;T&gt;</c>（F24）。
    /// </summary>
    private void AddRaw<T>(INodeManagerBuilder builder, INodeBuilder parent, string parentPath, string browseName, ArrayOf<T> initial)
    {
        string path = parentPath + "/" + browseName;
        IVariableBuilder<ArrayOf<T>> variable = builder.AddVariable<ArrayOf<T>>(browseName, parent.Node.NodeId);
        RuntimeValueBuilderExtensions.Bind(variable, out IValueUpdater<ArrayOf<T>> updater);
        updater.SetValue(initial);

        INodeBuilder created = builder
            .Node(parent.Node.NodeId)
            .Child(new QualifiedName(browseName, builder.DefaultNamespaceIndex));

        AccessLevelBuilderExtensions.Writable(created, true);
        created.OnWrite(new NodeValueWriteEventHandlerAsync((_, _, _, variant, _) =>
        {
            object? raw = variant.AsBoxedObject(Variant.BoxingBehavior.Legacy);
            if (raw is T[] elements)
            {
                updater.SetValue(ArrayOf.ToArrayOf(elements));
                return new ValueTask<AttributeWriteResult>(new AttributeWriteResult(StatusCodes.Good));
            }

            return new ValueTask<AttributeWriteResult>(new AttributeWriteResult(StatusCodes.BadTypeMismatch));
        }));

        Record(path, created.Node.NodeId);
    }

    /// <summary>计数器：测试要能改值，服务器要从它读值，改完还要通知订阅（F29）。</summary>
    private sealed class Counter
    {
        private readonly Box box;
        private readonly IValueUpdater<int> updater;

        public Counter(Box box, IValueUpdater<int> updater)
        {
            this.box = box;
            this.updater = updater;
        }

        /// <summary>构造一个只带值的计数器（不需要通知）。</summary>
        /// <param name="box">值。</param>
        public Counter(Box box)
            : this(box, null!)
        {
        }

        /// <summary>当前值。</summary>
        public int Value
        {
            get => box.Value;
            set => box.Value = value;
        }

        /// <summary>改值并通知服务器，监控项下一次采样就能看到。</summary>
        /// <param name="value">新值。</param>
        public void Set(int value)
        {
            updater.SetValue(value);
            updater.NotifyChange();
        }

        /// <summary>值本身。</summary>
        public sealed class Box
        {
            /// <summary>当前值。</summary>
            public int Value { get; set; }
        }
    }
}
