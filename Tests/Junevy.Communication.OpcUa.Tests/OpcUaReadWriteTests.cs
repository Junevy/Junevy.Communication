using Junevy.Communication.Core.Results;
using Junevy.Communication.OpcUa.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Junevy.Communication.OpcUa.Tests;

/// <summary>
/// 读与写（P62-D6、D7、D8）：值映射、时间戳、逐项状态、批量与上限、类型不符、
/// 单节点便捷方法，以及"日志里不出现值"。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class OpcUaReadWriteTests
{
    private readonly ILoggerFactory loggerFactory;

    public OpcUaReadWriteTests()
        => loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning));

    private OpcUaClientOptions OptionsFor(string endpointUrl) => new OpcUaClientOptions
    {
        EndpointUrl = endpointUrl,
        PkiRoot = OpcUaTestServerFactory.UniquePkiRoot(),
        ConnectTimeout = 10000,
        OperationTimeout = 5000,
    };

    private async Task<OpcUaTestServer> StartServerAsync(Action<OpcUaTestServerOptions>? configure = null, ILoggerFactory? log = null)
        => await OpcUaTestServerFactory.StartOwnAsync(configure, log ?? loggerFactory);

    private async Task<OpcUaClient> ConnectedAsync(OpcUaTestServer server)
    {
        OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), loggerFactory);
        Assert.True((await client.ConnectAsync()).IsSuccess);
        return client;
    }

    [Fact]
    public async Task Read_ScalarsAndArrays_ReturnTheRightValueAndType()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        var expected = new (string Node, object? Value)[]
        {
            ("Test/Bool", true),
            ("Test/SByte", (sbyte)-1),
            ("Test/Byte", (byte)1),
            ("Test/Int16", (short)-1),
            ("Test/UInt16", (ushort)1),
            ("Test/Int32", 42),
            ("Test/UInt32", 42u),
            ("Test/Int64", 42L),
            ("Test/UInt64", 42UL),
            ("Test/Float", 1.5f),
            ("Test/Double", 1.5),
            ("Test/String", "hello"),
            ("Test/DateTime", new DateTime(2026, 10, 12, 1, 2, 3, DateTimeKind.Utc)),
            ("Test/Guid", Guid.Parse("11112222-3333-4444-5555-666677778888")),
            ("Test/ByteString", new byte[] { 1, 2 }),
            ("Test/ByteArray", new byte[] { 7, 8 }),
            ("Test/Int32Array", new[] { 1, 2, 3 }),
            ("Test/DoubleArray", new[] { 1.5, 2.5 }),
            ("Test/StringArray", new[] { "a", "b" }),
            ("Test/LocalizedText", "hello"),
            ("Test/StatusCode", new OpcUaStatusCode(0x40000000)),
        };

        var nodeIds = new string[expected.Length];
        for (int i = 0; i < expected.Length; i++)
        {
            nodeIds[i] = Text(server, expected[i].Node);
        }

        CommResult<IReadOnlyList<OpcUaValue>> result = await client.ReadAsync(nodeIds);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(expected.Length, result.Data.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            OpcUaValue value = result.Data[i];
            Assert.True(value.Status.IsGood, $"{expected[i].Node}: {value.Status}");
            Assert.Equal(expected[i].Value, value.Value);
            Assert.Equal(expected[i].Value!.GetType(), value.Value!.GetType());
        }
    }

    [Fact]
    public async Task Read_Timestamps_AreUtc()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<OpcUaValue> value = await client.ReadAsync(Text(server, "Test/Int32"));

        Assert.True(value.IsSuccess, value.ToString());
        Assert.NotNull(value.Data.SourceTimestamp);
        Assert.Equal(DateTimeKind.Utc, value.Data.SourceTimestamp!.Value.Kind);
        Assert.NotNull(value.Data.ServerTimestamp);
        Assert.Equal(DateTimeKind.Utc, value.Data.ServerTimestamp!.Value.Kind);
        Assert.InRange(
            (DateTime.UtcNow - value.Data.SourceTimestamp!.Value).TotalMinutes,
            -1,
            5);
    }

    [Fact]
    public async Task Read_StructureNode_ReportsUnsupportedTypeAndKeepsTheStatus()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<OpcUaValue> value = await client.ReadAsync(Text(server, "Test/Struct"));

        Assert.True(value.IsSuccess, value.ToString());
        Assert.True(value.Data.Status.IsGood);
        Assert.Null(value.Data.Value);
        Assert.False(string.IsNullOrEmpty(value.Data.UnsupportedType));
    }

    [Fact]
    public async Task Read_OneUnknownNodeInTheBatch_OnlyThatItemIsBad()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<IReadOnlyList<OpcUaValue>> result = await client.ReadAsync(new[]
        {
            Text(server, "Test/Int32"),
            "i=999999",
            Text(server, "Test/String"),
        });

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(3, result.Data.Count);
        Assert.Equal(42, result.Data[0].Value);
        Assert.True(result.Data[1].Status.IsBad);
        Assert.Equal(0x80340000u, result.Data[1].Status.Code);
        Assert.Equal("hello", result.Data[2].Value);
    }

    [Fact]
    public async Task Read_NamespaceUriForm_Works()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        string text = $"nsu={OpcUaTestServer.NamespaceUri};i={Index(server, "Test/Int32")}";

        CommResult<OpcUaValue> value = await client.ReadAsync(text);

        Assert.True(value.IsSuccess, value.ToString());
        Assert.Equal(42, value.Data.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("i=not-a-number")]
    [InlineData("bogus=1")]
    [InlineData("nsu=urn:unknown:namespace;i=1")]
    public async Task Read_InvalidNodeId_FailsWithoutSendingAnything(string nodeId)
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<IReadOnlyList<OpcUaValue>> result = await client.ReadAsync(new[] { nodeId });

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Contains(nodeId.Trim(), result.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Read_EmptyList_SucceedsWithAnEmptyList()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<IReadOnlyList<OpcUaValue>> result = await client.ReadAsync(Array.Empty<string>());

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task Read_MoreNodesThanTheServerLimit_StillComesBackCompleteAndInOrder()
    {
        await using OpcUaTestServer server = await StartServerAsync(options => options.MaxNodesPerReadOrWrite = 5);
        await using OpcUaClient client = await ConnectedAsync(server);

        var nodeIds = new string[12];
        var expected = new int[12];
        for (int i = 0; i < 12; i++)
        {
            nodeIds[i] = Text(server, "Test/Scalar_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            expected[i] = i;
        }

        CommResult<IReadOnlyList<OpcUaValue>> result = await client.ReadAsync(nodeIds);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(12, result.Data.Count);
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(expected[i], result.Data[i].Value);
        }
    }

    [Fact]
    public async Task Read_WithoutAConnection_ReportsNotConnected()
    {
        await using OpcUaClient client = OpcUaClient.Create(
            OptionsFor(OpcUaTestServerFactory.FreeEndpointUrl()),
            loggerFactory);

        CommResult result = (await client.ReadAsync(new[] { "i=2258" })).ToResult();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.NotConnected, result.ErrorKind);
    }

    [Fact]
    public async Task Write_SupportedTypes_RoundTrip()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        var cases = new (string Node, object Value)[]
        {
            ("Bool", true),
            ("SByte", (sbyte)-8),
            ("Byte", (byte)8),
            ("Int16", (short)-16),
            ("UInt16", (ushort)16),
            ("Int32", -32),
            ("UInt32", 32u),
            ("Int64", -64L),
            ("UInt64", 64UL),
            ("Float", -8.5f),
            ("Double", -16.5),
            ("String", "after"),
            ("DateTime", new DateTime(2027, 3, 4, 5, 6, 7, DateTimeKind.Utc)),
            ("Guid", Guid.Parse("12345678-1234-1234-1234-123456789abc")),
            ("ByteString", new byte[] { 4, 5, 6 }),
            ("BoolArray", new[] { false, true }),
            ("SByteArray", new sbyte[] { -7, -8 }),
            ("Int16Array", new short[] { -17, -18 }),
            ("UInt16Array", new ushort[] { 17, 18 }),
            ("Int32Array", new[] { -33, -34 }),
            ("UInt32Array", new uint[] { 33, 34 }),
            ("Int64Array", new long[] { -65, -66 }),
            ("UInt64Array", new ulong[] { 65, 66 }),
            ("FloatArray", new[] { -1.25f, -2.25f }),
            ("DoubleArray", new[] { -5.5, -6.5 }),
            ("StringArray", new[] { "p", "q" }),
            ("DateTimeArray", new[] { new DateTime(2027, 7, 8, 9, 10, 11, DateTimeKind.Utc) }),
            ("GuidArray", new[] { Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff") }),
        };

        var writes = new OpcUaWriteValue[cases.Length];
        var nodeIds = new string[cases.Length];
        for (int i = 0; i < cases.Length; i++)
        {
            nodeIds[i] = Text(server, "Writable/" + cases[i].Node);
            writes[i] = new OpcUaWriteValue(nodeIds[i], cases[i].Value);
        }

        CommResult<IReadOnlyList<OpcUaStatusCode>> written = await client.WriteAsync(writes);
        Assert.True(written.IsSuccess, written.ToString());
        for (int i = 0; i < cases.Length; i++)
        {
            Assert.True(written.Data[i].IsGood, $"{cases[i].Node}: {written.Data[i]}");
        }

        CommResult<IReadOnlyList<OpcUaValue>> readBack = await client.ReadAsync(nodeIds);
        Assert.True(readBack.IsSuccess, readBack.ToString());
        for (int i = 0; i < cases.Length; i++)
        {
            Assert.Equal(cases[i].Value, readBack.Data[i].Value);
            Assert.Equal(cases[i].Value.GetType(), readBack.Data[i].Value!.GetType());
        }
    }

    [Fact]
    public async Task Write_LocalDateTime_IsStoredAsTheSameInstantInUtc()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        var local = new DateTime(2027, 1, 2, 3, 4, 5, DateTimeKind.Local);
        string nodeId = Text(server, "Writable/DateTime");

        CommResult status = await client.WriteAsync(nodeId, local);
        Assert.True(status.IsSuccess, status.ToString());

CommResult<OpcUaValue> readBack = await client.ReadAsync(nodeId);
        Assert.True(readBack.IsSuccess, readBack.ToString());
        Assert.NotNull(readBack.Data.Value);
        var stored = (DateTime)readBack.Data.Value!;
        Assert.Equal(local.ToUniversalTime(), stored);
        Assert.Equal(DateTimeKind.Utc, stored.Kind);
    }

    [Fact]
    public async Task Write_WrongType_GivesBadTypeMismatchForThatItemOnly()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<IReadOnlyList<OpcUaStatusCode>> result = await client.WriteAsync(new[]
        {
            new OpcUaWriteValue(Text(server, "Writable/Int16"), 42),
            new OpcUaWriteValue(Text(server, "Writable/Int32"), -4242),
        });

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(2, result.Data.Count);
        Assert.True(result.Data[0].IsBad);
        Assert.Equal(0x80740000u, result.Data[0].Code);
        Assert.True(result.Data[1].IsGood);
    }

    [Fact]
    public async Task Write_ReadOnlyNode_GivesTheServersBadStatusForThatItem()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<IReadOnlyList<OpcUaStatusCode>> result = await client.WriteAsync(new[]
        {
            new OpcUaWriteValue(Text(server, "Test/ReadOnly"), 1),
            new OpcUaWriteValue(Text(server, "Writable/Int32"), -1),
        });

        Assert.True(result.IsSuccess, result.ToString());
        Assert.True(result.Data[0].IsBad);
        Assert.True(result.Data[1].IsGood);
    }

    [Fact]
    public async Task Write_UnsupportedDotNetType_FailsTheWholeCall()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult result = (await client.WriteAsync(new[]
        {
            new OpcUaWriteValue(Text(server, "Writable/Int32"), 1.5m),
        })).ToResult();

        Assert.False(result.IsSuccess);
        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
    }

    [Fact]
    public async Task Write_MoreNodesThanTheServerLimit_StillComesBackComplete()
    {
        await using OpcUaTestServer server = await StartServerAsync(options => options.MaxNodesPerReadOrWrite = 5);
        await using OpcUaClient client = await ConnectedAsync(server);

        var writes = new OpcUaWriteValue[12];
        for (int i = 0; i < 12; i++)
        {
            writes[i] = new OpcUaWriteValue(
                Text(server, "Test/Scalar_" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                i);
        }

        CommResult<IReadOnlyList<OpcUaStatusCode>> result = await client.WriteAsync(writes);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(12, result.Data.Count);
        Assert.All(result.Data, status => Assert.True(status.IsGood, status.ToString()));
    }

    [Fact]
    public async Task SingleNodeExtensions_TurnBadItemStatusesIntoFailures()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult good = await client.WriteAsync(Text(server, "Writable/Int32"), 77);
        Assert.True(good.IsSuccess, good.ToString());

        CommResult bad = await client.WriteAsync(Text(server, "Test/ReadOnly"), 1);
        Assert.False(bad.IsSuccess);
        Assert.Equal(0x803B0000u, bad.ProtocolErrorCode);
        Assert.Equal(CommErrorKind.RemoteError, bad.ErrorKind);
    }

[Fact]
    public async Task SingleNodeExtensions_BadItemStatus_BecomesAFailureCarryingTheServerCode()
    {
        await using OpcUaTestServer server = await StartServerAsync();
        await using OpcUaClient client = await ConnectedAsync(server);

        CommResult<OpcUaValue> value = await client.ReadAsync("i=999999");

        Assert.False(value.IsSuccess);
        Assert.Equal(0x80340000u, value.ProtocolErrorCode);
    }

    [Theory]
    [InlineData(0x00000000u, true)]
    [InlineData(0x40000000u, true)]
    [InlineData(0x803B0000u, false)]
    [InlineData(0x80340000u, false)]
    public void SingleNodeExtensions_UncertainCountsAsSuccessAndBadDoesNot(uint code, bool expectedSuccess)
    {
        // 进程内测试服务器产生不出 Uncertain 的数据值状态，这条规则只能单独钉（P62-D8）。
        CommResult decided = OpcUaClientExtensions.Decide(new OpcUaStatusCode(code), "ns=2;i=1");

        Assert.Equal(expectedSuccess, decided.IsSuccess);
        if (!expectedSuccess)
        {
            Assert.Equal(code, (uint)decided.ProtocolErrorCode!.Value);
        }
    }

    [Fact]
    public async Task ReadAndWrite_LogNothingAboutTheValues()
    {
        using var collector = new CollectingLoggerProvider();
        using ILoggerFactory verbose = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(collector);
        });

        await using OpcUaTestServer server = await StartServerAsync(log: verbose);
        await using OpcUaClient client = OpcUaClient.Create(OptionsFor(server.EndpointUrl), verbose);
        Assert.True((await client.ConnectAsync()).IsSuccess);

        string secret = "s3cr3t-value-9d2f";
        Assert.True((await client.WriteAsync(Text(server, "Writable/String"), secret)).IsSuccess);
        Assert.True((await client.ReadAsync(Text(server, "Writable/String"))).IsSuccess);
        await client.DisconnectAsync();

        string everything = string.Join('\n', collector.Messages);
        Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
    }

    private static string Text(OpcUaTestServer server, string browsePath) => server.Id(browsePath).ToString();

    private static string Index(OpcUaTestServer server, string browsePath) => server.Id(browsePath).IdentifierAsString;

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> messages = new List<string>();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (messages)
                {
                    return messages.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CollectingLogger(messages);

        public void Dispose()
        {
        }

        private sealed class CollectingLogger : ILogger
        {
            private readonly List<string> messages;

            public CollectingLogger(List<string> messages) => this.messages = messages;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}



