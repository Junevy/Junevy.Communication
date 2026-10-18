---
name: using-junevy-ftp
description: Use when a .NET project references Junevy.Communication.Ftp or needs FTP or FTPS file transfer — listing a directory, uploading or downloading files, atomic downloads, file and directory operations, explicit or implicit TLS, GBK file names, registering with dependency injection, or handling FtpClient results and CommErrorKind. Also covers timeouts after a dropped connection, parallel transfer requests, and SFTP requests this package does not serve.
---

# using-junevy-ftp

FTP and FTPS client from the Junevy.Communication library: list, upload, download, file and directory operations, explicit and implicit TLS. **Client only, passive mode and binary transfers only, .NET 8 only. Not SFTP.**

## Minimum working code

```csharp
using Junevy.Communication.Core.Results;
using Junevy.Communication.Ftp;

using FtpClient client = FtpClient.Create(new FtpClientOptions
{
    Host = "files.local",
    UserName = "line1",
    Password = "secret",
});

CommResult<FtpTransferStatus> uploaded = await client.UploadFileAsync(@"C:\recipes\r1.txt", "/recipes/r1.txt");
if (!uploaded.IsSuccess)
{
    Console.WriteLine(uploaded.ErrorMessage);
}
```

Options bind straight from `appsettings`:

```csharp
var options = new FtpClientOptions();
configuration.GetSection("Ftp").Bind(options);   // enums bind by name: "Encryption": "Explicit"
using FtpClient client = FtpClient.Create(options);
```

## The five mistakes that cost the most time

1. **Using it for SFTP.** This package is FTP and FTPS only. SFTP runs over SSH on a different protocol and port, so an SFTP server will not connect. Use an SSH or SFTP library for it.

2. **Expecting parallel transfers from one client.** Operations on one client run one after another: a second call waits for the first. For parallel transfers, create several clients with different names through `FtpClientFactory.GetOrAdd(name, options)`.

   ```csharp
   FtpClient first = factory.GetOrAdd("line1-a", options);
   FtpClient second = factory.GetOrAdd("line1-b", options);   // a separate control connection
   ```

3. **Garbled Chinese file names on an older device.** Such devices and Windows FTP servers commonly use `GBK`. Set `EncodingName = "GBK"`. With the default (null), the library uses UTF-8 only when the server supports it.

   ```csharp
   new FtpClientOptions { Host = "plc-ftp.local", EncodingName = "GBK" }
   ```

4. **Implicit FTPS on the default port.** `Encryption = FtpEncryption.Implicit` does **not** change the port. Implicit TLS usually listens on `990`; the default is `21`. Set `Port = 990` yourself. `Explicit` uses the control port and upgrades it with `AUTH TLS`.

   ```csharp
   new FtpClientOptions { Host = "files.local", Port = 990, Encryption = FtpEncryption.Implicit }
   ```

5. **Assuming `DeleteDirectoryAsync` removes only empty directories.** It is **recursive**: every file and subdirectory under the path is deleted. A missing directory fails with `RemoteError`. Confirm the path before calling it.

## Results, not exceptions

Every operation returns `CommResult`, `CommResult<bool>`, `CommResult<long>`, `CommResult<IReadOnlyList<FtpEntry>>` or `CommResult<FtpTransferStatus>`. Decide on `ErrorKind`; never match `ErrorMessage` text. `ProtocolErrorCode` carries the FTP reply code when the server sent one.

| You see | Meaning | What to do |
|---|---|---|
| `InvalidRequest` | Empty path, or an upload source that does not exist locally | Fix the request. Nothing was sent: a missing local source does not even connect |
| `AuthenticationFailed` | Login refused (`530`), or a TLS certificate problem | Check the credentials and the certificate. `AllowUntrustedCertificates` is for test devices only |
| `Timeout` | Connect, reply or data timeout, including a drop during an operation | Retry. The next operation reconnects |
| `ConnectionClosed` | Refused, reset, `421`, `425` or `426`, or used after disposal | Retry. Check the server, and the firewall for passive data ports |
| `RemoteError` with `ProtocolErrorCode` 550 | The file, directory or rename source does not exist | Check the path |
| `ResourceExhausted` | Server storage is full (`452`, `552`) | Free space on the server |
| `NotSupported` | The server does not implement the command (`502`, `504`) | Use another server or another method |
| `Cancelled` | Your cancellation token fired | Usually expected |

`FtpExistsMode.Skip` is not an error: it returns success with `FtpTransferStatus.Skipped`.

## Dependency injection

```csharp
using Junevy.Communication.Ftp.DependencyInjection;

services.AddFtpClients();

FtpClientFactory factory = provider.GetRequiredService<FtpClientFactory>();
FtpClient client = factory.GetOrAdd("line1", options);
```

`AddFtpClients()` may be called repeatedly. `GetOrAdd` returns the existing client for a name, so options passed later for that name are ignored; `Create` snapshots the options once.

## API reference

| Member | Notes |
|---|---|
| `FtpClient.Create(options, loggerFactory?)` | Validates and snapshots the options. Throws `ArgumentException` with `ParamName = "options.<Property>"`. |
| `ConnectAsync` / `DisconnectAsync` | Connecting is optional. `DisconnectAsync` sends `QUIT`; the next operation reconnects. |
| `IsConnected` | The SDK's state. It can stay `true` after the server dropped an idle connection, until the next operation. |
| `ListAsync` | Entries without `.` and `..`. A missing directory is `RemoteError`; an empty one succeeds. |
| `FileExistsAsync`, `DirectoryExistsAsync`, `GetFileSizeAsync` | `CommResult<bool>` and `CommResult<long>`. A missing file or an unknown size is `RemoteError`. |
| `CreateDirectoryAsync` | Creates missing levels. An existing directory is success; a file in the way is `RemoteError`. |
| `DeleteFileAsync`, `DeleteDirectoryAsync`, `RenameAsync` | `DeleteDirectoryAsync` is recursive. Renaming onto an existing target depends on the server. |
| `UploadFileAsync`, `UploadAsync` | **Not atomic.** `Overwrite` (default) or `Skip`. Progress is `IProgress<FtpTransferProgress>`. |
| `DownloadFileAsync` | **Atomic:** writes a temporary file, then replaces the target. `Skip` checks the local file before connecting. |
| `DownloadAsync` | Writes to a stream you own; the library does not dispose it. |
| `Dispose` / `DisposeAsync` | Repeatable. A transfer in progress finishes as a failed result. Afterwards `ConnectAsync` and `DisconnectAsync` throw `ObjectDisposedException`, and other operations return `ConnectionClosed`. |

The full option table, the error table and the "Verification status" are in [Junevy.Communication.Ftp/README.md](file://Junevy.Communication.Ftp/README.md).

## Behaviour worth knowing

- Only .NET 8. The package does not target net472.
- SDK types never appear in this library's public API; every result is a `CommResult`.
- A drop during an operation is reported as `Timeout` after `ReadTimeout`, not as `ConnectionClosed`. The SDK waits for a reply that never comes.
- Times in `FtpEntry.Modified` are as the server reports them. No time-zone conversion is done.
- Passwords and file contents are never logged. SDK logs go to the category `Junevy.Communication.Ftp.Sdk`.
- Verified only against the in-process fake FTP server used by the tests. It has not been verified against vsftpd, FileZilla Server, IIS FTP, or device FTP servers.
