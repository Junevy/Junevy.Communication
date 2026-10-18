# Junevy.Communication.Ftp

FTP and FTPS client for the [Junevy.Communication](https://github.com/Junevy/Junevy.Communication) communication library.

- **Client only.** No FTP server is part of this package.
- **FTP and FTPS.** Explicit TLS (`AUTH TLS`) and implicit TLS.
- **Passive mode and binary transfers only.** Active mode and ASCII transfers are not supported.
- **Not SFTP.** SFTP runs over SSH and is a different protocol; use an SSH library for it.
- **.NET 8 only**, like the MQTT and OPC UA packages.

The protocol itself is handled by [FluentFTP](https://github.com/robinrodricks/FluentFTP) 55. This package is a thin adapter around it: it exposes its own types, keeps the SDK out of the public API, translates every SDK failure into `CommResult`, and adds the behaviour the SDK leaves to the caller (on-demand connection, serialized operations, atomic downloads, and existence checks where the SDK is silent).

## Getting started

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp;

using FtpClient client = FtpClient.Create(new FtpClientOptions
{
    Host = "files.local",
    UserName = "line1",
    Password = "secret",
});

CommResult<IReadOnlyList<FtpEntry>> listing = await client.ListAsync("/recipes");
if (!listing.IsSuccess)
{
    Console.WriteLine(listing.ErrorMessage);   // for example: The remote directory does not exist: /recipes
    return;
}

foreach (FtpEntry entry in listing.Data!)
{
    Console.WriteLine($"{entry.Type} {entry.FullName} {entry.Size}");
}

CommResult<FtpTransferStatus> uploaded = await client.UploadFileAsync(@"C:\recipes\r1.txt", "/recipes/r1.txt");

CommResult<FtpTransferStatus> downloaded = await client.DownloadFileAsync(
    "/recipes/r1.txt",
    @"C:\cache\r1.txt",
    FtpExistsMode.Skip);
if (downloaded.IsSuccess && downloaded.Data == FtpTransferStatus.Skipped)
{
    // The local file already existed: nothing was transferred, and the server was not contacted.
}
```

`FtpClientOptions` is a plain POCO of simple properties, so it binds straight from `appsettings`:

```csharp
var options = new FtpClientOptions();
configuration.GetSection("Ftp").Bind(options);   // enums bind by name, e.g. "Encryption": "Implicit"
using FtpClient client = FtpClient.Create(options);
```

`Create` validates the options once and takes a **snapshot**: changing the options object afterwards does not affect the client.

## Connection model

- **On demand.** Each client holds one control connection. An operation that starts while the client is not connected connects first, so calling `ConnectAsync` is optional. Call it to check the address and credentials early.
- **Liveness check.** Before each operation on an open connection, the client sends one `NOOP` to confirm the server still has the connection. If the server has dropped it (for example after an idle timeout), the operation reconnects first instead of failing. This costs one round trip per operation.
- **Serialized operations.** A control connection runs one command at a time, so operations on the same client are queued and run one after another. A second call waits for the first; it does not fail. For parallel transfers, create several clients with different names (`FtpClientFactory` gives each name its own client).
- **Disconnect.** `DisconnectAsync` sends `QUIT`. The next operation reconnects.
- **`IsConnected`** reports the state of the SDK's connection. It can stay `true` after the server has closed an idle connection until the next operation discovers it.
- **Connection drop during an operation.** The SDK waits for a reply that never arrives, so a drop, reset or silent server during an operation is reported as `Timeout` after `ReadTimeout`. The next operation reconnects.

### Disposal

`Dispose` and `DisposeAsync` are repeatable. A transfer that is in progress finishes with a failed result (it does not throw), and operations waiting in the queue return `ConnectionClosed` without contacting the server. Afterwards `ConnectAsync` and `DisconnectAsync` throw `ObjectDisposedException`, and every other operation returns `ConnectionClosed`.

## Options

| Property | Default | Meaning |
|---|---|---|
| `Host` | — | Server host name or IP address. **Required.** |
| `Port` | `21` | Control connection port. Implicit FTPS usually uses `990`; set it yourself. |
| `UserName` | null | Null means anonymous login. Required when `Password` is set. |
| `Password` | null | Never written to a log, at any level. |
| `Encryption` | `None` | `None`, `Explicit` (`AUTH TLS` on the control connection) or `Implicit` (TLS from the first byte). |
| `AllowUntrustedCertificates` | `false` | Accepts any certificate, including one whose host name does not match. `Create` logs a warning when this is on. |
| `EncodingName` | null | Encoding for file names and listings, e.g. `GBK`. Null uses UTF-8 when the server supports it, otherwise the SDK's default. See [Encoding](#encoding). |
| `ConnectTimeout` | `10000` | Milliseconds to establish the control connection. |
| `ReadTimeout` | `15000` | Milliseconds to wait for a reply on the control connection. Also used as the write timeout. |
| `DataConnectionTimeout` | `15000` | Milliseconds to establish a data connection and to read or write data on it. |

`Create` rejects invalid options with an `ArgumentException`. The `ParamName` is `options.<Property>`, and the message states the allowed range (`ArgumentOutOfRangeException` for numeric ranges).

## Uploads and downloads

```csharp
var progress = new Progress<FtpTransferProgress>(p =>
    Console.WriteLine($"{p.TransferredBytes} bytes, {p.Percent?.ToString("F0") ?? "?"}%"));

await client.DownloadFileAsync("/recipes/big.bin", @"C:\cache\big.bin", progress: progress);

using var buffer = new MemoryStream(data);
CommResult<FtpTransferStatus> sent = await client.UploadAsync(buffer, "/recipes/r2.bin");
```

- **Existing targets.** `FtpExistsMode.Overwrite` (the default) replaces the target. `FtpExistsMode.Skip` returns success with `FtpTransferStatus.Skipped` and leaves the target unchanged. For a download the local file is checked before anything is sent, so a skipped download does not contact the server. For an upload the check is on the remote file, so it needs a connection.
- **Downloads are atomic.** A download to a file first writes a temporary file (`<name>.<random>.part`) in the same directory, then replaces the target. If the transfer fails, the temporary file is deleted and the existing local file keeps its old content. Missing local parent directories are created.
- **Uploads are not atomic.** With `Overwrite` the SDK removes the remote file and then uploads it, so a failure in the middle can leave no remote file at all. If the device must never see a partial file, upload to a temporary name and rename it once the upload succeeds (`RenameAsync`).
- **Streams.** `UploadAsync` reads from the stream's current position to its end. `DownloadAsync` writes from the current position. A failed stream download leaves the bytes it has already written in the stream. The library never disposes the stream you pass in.
- **Progress** is reported as `FtpTransferProgress` with `TransferredBytes` and `Percent`. `Percent` is null when the total length is unknown. Progress callbacks run on the SDK's thread, so they must not block.

## Directory semantics

| Operation | Behaviour |
|---|---|
| `ListAsync(dir)` | Returns the entries of `dir`, without `.` and `..`. Uses `MLSD` when the server supports it, otherwise parses `LIST`. A missing directory fails with `RemoteError`; an empty directory succeeds with an empty list. |
| `CreateDirectoryAsync(dir)` | Creates every missing level. A directory that already exists counts as success. If the path is a file, the call fails with `RemoteError`. |
| `DeleteFileAsync(path)` | A missing file fails with `RemoteError` (reply `550`). |
| `DeleteDirectoryAsync(dir)` | **Recursive.** Deletes the directory with all its files and subdirectories. A missing directory fails with `RemoteError` (reply `550`). It does not only delete empty directories. |
| `RenameAsync(from, to)` | A missing source fails with `RemoteError` (reply `550`). When `to` already exists, the server decides whether it is replaced or the call fails. |
| `FileExistsAsync`, `DirectoryExistsAsync` | Return `CommResult<bool>`. |
| `GetFileSizeAsync(path)` | Fails with `RemoteError` when the file is missing or the server does not report a size. It never returns a sentinel value such as `-1`. |

`FtpEntry` has `Name`, `FullName`, `Type` (`File`, `Directory` or `Link`), `Size` and `Modified`. `Size` is null for directories and when the server does not report it. `Modified` is null when the server does not report it.

### Times and time zones

`FtpEntry.Modified` is the time the server reports, without any time-zone conversion. `MLSD` times are UTC (RFC 3659). `LIST` times are usually the server's local time, which the library cannot know. Convert the value yourself when the difference matters.

## Encoding

`EncodingName` sets the encoding used for file names in commands and listings. Set it for devices and Windows servers that use a code page such as `GBK`:

```csharp
using FtpClient legacy = FtpClient.Create(new FtpClientOptions { Host = "plc-ftp.local", EncodingName = "GBK" });
```

.NET 8 needs the code page provider to use `GBK`. The first time `EncodingName` is validated, the library registers `CodePagesEncodingProvider` for the **whole process**. An unknown name fails `Create` with an `ArgumentException`.

## FTPS

```csharp
FtpClient secure = FtpClient.Create(new FtpClientOptions
{
    Host = "files.local",
    Port = 990,
    Encryption = FtpEncryption.Implicit,
    UserName = "line1",
    Password = "secret",
});
```

- `Explicit` starts plain on the control port (21 by default) and then upgrades with `AUTH TLS`. `Implicit` starts with TLS from the first byte, usually on port 990. **The port is not changed for you**; set it when you choose `Implicit`.
- Data connections are encrypted as well (`PROT P`).
- Certificates are validated by default. `AllowUntrustedCertificates = true` accepts any certificate, including a host name mismatch, and logs a warning at `Create`. Use it only for test devices.
- A TLS failure is `AuthenticationFailed` with no reply code.

## Error classification

Failures come back as `CommResult` with an `ErrorKind` and, when the server sent a reply, a `ProtocolErrorCode` holding the FTP reply code.

| Situation | `ErrorKind` | `ProtocolErrorCode` |
|---|---|---|
| The caller's cancellation token fires | `Cancelled` | — |
| Connect timeout, reply timeout or data-connection timeout, including a drop during an operation | `Timeout` | — |
| Login refused (`530`) | `AuthenticationFailed` | 530 |
| TLS failure (untrusted certificate, host name mismatch) | `AuthenticationFailed` | — |
| `421` (service not available, connection closed) | `ConnectionClosed` | 421 |
| `425` (cannot open data connection), `426` (transfer aborted) | `ConnectionClosed` | 425 or 426 |
| `452`, `552` (insufficient storage) | `ResourceExhausted` | 452 or 552 |
| `502`, `504` (command not implemented) | `NotSupported` | 502 or 504 |
| Other `4xx` and `5xx`, including `550` and `553` | `RemoteError` | the reply code |
| Connection refused, socket error, control connection lost | `ConnectionClosed` | — |
| The SDK reports a failed transfer without a reply code | `RemoteError` | — |
| Missing remote file or directory found by the library's own check | `RemoteError` | — |
| Empty path, or an upload source that does not exist locally | `InvalidRequest` | — |
| Local file system error (cannot create a local directory or temporary file) | `Unspecified` | — |
| Any operation after disposal | `ConnectionClosed` | — |
| Any other SDK failure | `Unspecified` | — |

Exceptions never escape the public methods. The original exception is kept in `CommResult.Exception` for diagnostics only.

## Dependency injection

```csharp
using Junevy.Communication.Ftp;
using Junevy.Communication.Ftp.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddFtpClients();
using ServiceProvider provider = services.BuildServiceProvider();

FtpClientFactory factory = provider.GetRequiredService<FtpClientFactory>();
FtpClient client = factory.GetOrAdd("line1", new FtpClientOptions { Host = "files.local", UserName = "line1", Password = "secret" });
```

`AddFtpClients()` may be called repeatedly. `GetOrAdd` returns the existing client for a name, so options passed later for the same name are ignored. Disposing the factory disposes the clients it created. When the container has an `ILoggerFactory`, the factory passes it to the clients.

## Logging

Pass an `ILoggerFactory` to `Create` to receive logs. The client logs operations with the remote path, byte counts and duration, and failures at Warning. SDK logs are bridged to the same factory under the category `Junevy.Communication.Ftp.Sdk` at Trace, Debug, Warning and Error. **Passwords and file contents are never logged, at any level.** The SDK's host, user name and password redaction stays on and cannot be switched off.

## Verification status

1. Verified only against the in-process fake FTP server used by the tests. Not verified against vsftpd, FileZilla Server, IIS FTP, or a device or PLC with a built-in FTP server.
2. The `LIST` parsing depends on FluentFTP. A listing format that FluentFTP does not recognise can give different entries.
3. Modification times are not converted between time zones.
4. Renaming onto an existing target is decided by the server.
5. An idle drop is detected with one `NOOP` before each operation. A drop during an operation is reported as `Timeout` after `ReadTimeout`, not as `ConnectionClosed`, because the SDK waits for a reply that never comes.
6. Uploads are not atomic (see [Uploads and downloads](#uploads-and-downloads)).
7. Concurrent calls on one client are serialized. This was verified with ten concurrent uploads on one client: one control connection, and no commands interleaved.

## License

MIT
