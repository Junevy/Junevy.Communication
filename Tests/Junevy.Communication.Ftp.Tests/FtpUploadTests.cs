using System.Text;
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp.Tests.TestSupport;
using Junevy.Communication.Testing;

namespace Junevy.Communication.Ftp.Tests;

/// <summary>上传（P63-D5、D6、D11；计划 Task 5 第 1 条）。涉及真实套接字，放入 <c>SocketTiming</c> 集合。</summary>
[Collection(SocketTimingCollection.Name)]
public sealed class FtpUploadTests
{
    private const string Content = "0123456789ABCDEFGHIJ";

    [Fact]
    public async Task UploadFile_SendsTheContent_AndReportsTransferred()
    {
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin", Content);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.UploadFileAsync(source, "/docs/up.bin");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(FtpTransferStatus.Transferred, result.Data);
        Assert.Equal(Content, Encoding.ASCII.GetString(server.FileSystem.ReadFile("/docs/up.bin")));
    }

    [Fact]
    public async Task UploadFile_Overwrite_ReplacesAnExistingFile()
    {
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin", Content);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.UploadFileAsync(source, "/docs/recipe.txt", FtpExistsMode.Overwrite);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(Content, Encoding.ASCII.GetString(server.FileSystem.ReadFile("/docs/recipe.txt")));
    }

    [Fact]
    public async Task UploadFile_Skip_WhenTheRemoteFileExists_IsSkipped_AndLeavesItUnchanged()
    {
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin", Content);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.UploadFileAsync(source, "/docs/recipe.txt", FtpExistsMode.Skip);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(FtpTransferStatus.Skipped, result.Data);
        Assert.Equal("hello world!", Encoding.ASCII.GetString(server.FileSystem.ReadFile("/docs/recipe.txt")));
        Assert.DoesNotContain(server.Commands.Snapshot(), c => c.Verb == "STOR");
    }

    [Fact]
    public async Task UploadFile_MissingLocalSource_IsInvalidRequest_AndNoConnectionIsMade()
    {
        using var local = new TempDirectory();
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.UploadFileAsync(local.File("none.bin"), "/docs/x.bin");

        Assert.Equal(CommErrorKind.InvalidRequest, result.ErrorKind);
        Assert.Equal(0, server.ConnectionCount);
    }

    [Fact]
    public async Task UploadStream_ReadsFromTheCurrentPosition()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("XXXhello")) { Position = 3 };

        CommResult<FtpTransferStatus> result = await client.UploadAsync(source, "/docs/stream.txt");

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("hello", Encoding.ASCII.GetString(server.FileSystem.ReadFile("/docs/stream.txt")));
    }

    [Fact]
    public async Task UploadStream_DoesNotDisposeTheCallersStream()
    {
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        using var source = new MemoryStream(Encoding.ASCII.GetBytes("hello"));

        await client.UploadAsync(source, "/docs/stream.txt");

        Assert.Equal(5, source.Length);
        Assert.Equal(0, source.Read(new byte[1], 0, 0));
    }

    [Fact]
    public async Task UploadFile_StorRepliedWith552_IsResourceExhausted()
    {
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin", Content);
        await using FakeFtpServer server = FtpFixtures.Start();
        server.Faults.FailCommand("STOR", 552);
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.UploadFileAsync(source, "/docs/up.bin");

        Assert.Equal(CommErrorKind.ResourceExhausted, result.ErrorKind);
        Assert.Equal(552, result.ProtocolErrorCode);
    }

    [Fact]
    public async Task UploadFile_DataConnectionRejected_IsConnectionClosed_WithCode425()
    {
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin", Content);
        await using FakeFtpServer server = FtpFixtures.Start();
        server.Faults.RejectDataConnections(425);
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.UploadFileAsync(source, "/docs/up.bin");

        Assert.Equal(CommErrorKind.ConnectionClosed, result.ErrorKind);
        Assert.Equal(425, result.ProtocolErrorCode);
    }

    [Fact]
    public async Task UploadFile_IntoAMissingRemoteDirectory_IsRemoteError_WithCode550()
    {
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin", Content);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));

        CommResult<FtpTransferStatus> result = await client.UploadFileAsync(source, "/missing/up.bin");

        Assert.Equal(CommErrorKind.RemoteError, result.ErrorKind);
        Assert.Equal(550, result.ProtocolErrorCode);
    }

    [Fact]
    public async Task UploadFile_ReportsProgress_MonotonicallyAndEndsAtTheLength()
    {
        using var local = new TempDirectory();
        string source = WriteLocal(local, "up.bin", Content);
        await using FakeFtpServer server = FtpFixtures.Start();
        await using FtpClient client = FtpClient.Create(FtpTestClients.OptionsFor(server));
        var progress = new TransferProgressRecorder();

        CommResult<FtpTransferStatus> result = await client.UploadFileAsync(source, "/docs/up.bin", progress: progress);

        Assert.True(result.IsSuccess, result.ToString());
        IReadOnlyList<FtpTransferProgress> reports = progress.Snapshot();
        Assert.NotEmpty(reports);
        Assert.Equal(reports.OrderBy(r => r.TransferredBytes).Select(r => r.TransferredBytes), reports.Select(r => r.TransferredBytes));
        Assert.Equal(Content.Length, reports[^1].TransferredBytes);
    }

    private static string WriteLocal(TempDirectory directory, string name, string content)
    {
        string path = directory.File(name);
        File.WriteAllText(path, content, Encoding.ASCII);
        return path;
    }
}
