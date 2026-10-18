using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>
/// 同一个客户端上的操作串行执行（P63-D4，Q42）：并发发起的操作排队，全部成功，且同一控制连接上的命令没有交错。
/// 涉及真实套接字，放入 <c>SocketTiming</c> 集合。
/// </summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpSerializationTests
{
    [Fact]
    public async Task TenConcurrentUploads_AllSucceed_AndNeverInterleaveOnTheControlConnection()
    {
        using var local = new TempDirectory();
        string source = local.File("up.bin");
        File.WriteAllText(source, "0123456789", Encoding.ASCII);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        Task<CommResult<FtpTransferStatus>>[] uploads = Enumerable.Range(0, 10)
            .Select(i => client.UploadFileAsync(source, $"/docs/parallel-{i}.bin"))
            .ToArray();
        CommResult<FtpTransferStatus>[] results = await Task.WhenAll(uploads);

        Assert.All(results, r => Assert.True(r.IsSuccess, r.ToString()));
        Assert.Equal(1, server.ConnectionCount);
        Assert.Equal(10, server.Commands.Snapshot().Count(c => c.Verb == "STOR"));
        AssertNoInterleaving(server.Commands.Snapshot());
    }

    // 同一连接上，每条命令的接收时间不早于上一条命令的应答时间：服务器的会话逐条处理，客户端没有并发发送。
    private static void AssertNoInterleaving(IReadOnlyList<FakeFtpCommand> commands)
    {
        foreach (IGrouping<int, FakeFtpCommand> connection in commands.GroupBy(c => c.ConnectionId))
        {
            List<FakeFtpCommand> ordered = connection.ToList();
            for (int i = 1; i < ordered.Count; i++)
            {
                FakeFtpCommand previous = ordered[i - 1];
                Assert.NotNull(previous.RepliedAt);
                Assert.True(
                    ordered[i].ReceivedAt >= previous.RepliedAt,
                    $"{ordered[i].Verb} arrived before the reply to {previous.Verb} on connection {connection.Key}");
            }
        }
    }
}
