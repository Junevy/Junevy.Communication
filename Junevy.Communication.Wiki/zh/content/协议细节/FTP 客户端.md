---
tags: [协议细节, Junevy.Communication, Ftp, FTP, FTPS]
---

# FTP 客户端

`Junevy.Communication.Ftp` 是本库第三个 **SDK 适配包**（P6.3）：FTP 与 FTPS 客户端，只做客户端，**只面向 net8.0**，只用被动模式与二进制传输，**不做 SFTP**（SFTP 跑在 SSH 上，是另一个协议）。协议本身由 [FluentFTP](https://github.com/robinrodricks/FluentFTP) 55.0.0 实现，本包是一层适配：公开 API 里没有 SDK 类型，所有失败都翻译成 Core 的 `CommResult`。

> 版本 `1.0.0-preview.1`。**只与测试里的进程内模拟服务器联调过，没有对 vsftpd、FileZilla Server、IIS FTP 或设备内置的 FTP 服务器验证过。**

## 连接模型（P63-D3、D4）

- 每个 `FtpClient` 持有一条控制连接，**按需连接**：操作开始时未连接就先连接，`ConnectAsync` 可以省略（它只用来提前验证地址与凭据）。
- **每次操作前对已连接的会话发送一条 NOOP**（`IsStillConnected`）。SDK 的 `IsConnected` 不会察觉服务器的空闲断线（F28），没有这一步，"下一次操作自动重连"无法成立。代价是每次操作多一次往返；没有定时心跳。
- 连接级失败（断线、超时）之后库不主动断开：超时后 SDK 已经把自己置为未连接，下一次操作按需连接（F31）；服务器关闭的连接由下一次操作前的存活检查发现（F28）；425、426 只说明这一次传输失败，控制连接完好，下一次操作沿用它。原先"失败后关闭 SDK 连接"的分支在 P6.3 审阅中删除（设计问题 A）。
- 同一个客户端上的操作**串行**：`SemaphoreSlim` 排队，第二个调用等待第一个完成，**不是失败**。需要并行传输时，宿主用工厂的不同名称创建多个客户端。

### 释放

- 进行中的操作返回失败结果：不抛异常，也不等服务器的延迟。排队中的操作返回 `ConnectionClosed`，不执行（Q40、Q42）。
- 释放之后，`ConnectAsync` 与 `DisconnectAsync` 抛 `ObjectDisposedException`，其余操作返回 `ConnectionClosed`，不连接服务器。
- 释放前先发 QUIT（尽力而为，最多等 `ReadTimeout`）。

## 操作中断线归为 Timeout（F30，偏离计划）

控制连接在操作进行中断线（FIN 或 RST），SDK 仍在等待应答，要等到读超时才抛出 `TimeoutException`。库把它归为 **`Timeout`**，不归 `ConnectionClosed`：超时之后无法可靠地区分"断了"与"慢"。与计划的 `ConnectionClosed` 不同，用户 2026-10-18 接受现状（设计 20.11）。

## 传输（P63-D6、D7、D11）

- **上传不是原子的**。`UploadFileAsync`、`UploadAsync`：覆盖时 SDK 先删远程文件再上传（F22），中途失败时远程文件可能消失。本期不做原子上传（设计第 19 节排除）。
- **下载是原子的**。`DownloadFileAsync` 先写同目录的 `<文件名>.<随机串>.part` 临时文件，成功后 `File.Move(…, overwrite: true)` 替换目标；失败时删除临时文件，原文件不变（Q43）。父目录不存在时创建。
- **流**：`UploadAsync` 从流的当前位置读到末尾，`DownloadAsync` 从当前位置写；库不释放调用方的流。SDK 会把可定位的流定位到 0（F23），所以用 `FtpStreamView` 包一层：以调用方流的当前位置为起点，内层可定位时视图的位置与长度相对于起点，否则只能顺序读写。同步、异步与 `Span` 的读写都转发给内层流，只支持异步的流（ASP.NET Core 的请求体）同样可用（P6.3 审阅 R2）。
- **已存在时**：`FtpExistsMode.Overwrite`（默认）或 `Skip`，`Skip` 的结果是 `FtpTransferStatus.Skipped`。下载的 `Skip` 在连接前检查本地文件，不联网；上传的 `Skip` 要查远程文件，因此需要连接。
- **进度**：`IProgress<FtpTransferProgress>`，在 SDK 的回调线程上报告；总长未知时 `Percent` 为 null（不可定位的流上传、服务器不报告大小的下载）。可定位的流按当前位置到末尾的长度计算百分比（P6.3 审阅设计问题 B）。

## 目录语义（P63-D8、D9、D10）

| 操作 | 语义 |
|---|---|
| `ListAsync` | 不含 `.` 与 `..`。服务器支持 MLSD 时用 MLSD，否则解析 LIST。目录不存在时失败（库先检查：列表为空时才查目录）。 |
| `CreateDirectoryAsync` | 逐级创建；已存在算成功；服务器拒绝（权限不足、路径上是文件）时 `RemoteError`，带 MKD 的应答码（通常 550）。SDK 对 MKD 的任何 550 都返回 false，库先记下应答码再检查目录是否存在（F15，P6.3 审阅 R4）。 |
| `DeleteFileAsync` | 文件不存在时 `RemoteError`（550，由 SDK 报告）。 |
| `DeleteDirectoryAsync` | **递归删除**（SDK 的 `Recursive`）；目录不存在时 `RemoteError`（550）。 |
| `RenameAsync` | 源不存在时 `RemoteError`（550）；目标已存在时由服务器决定。 |
| `FileExistsAsync`、`DirectoryExistsAsync` | `CommResult<bool>`。 |
| `GetFileSizeAsync` | 文件不存在或服务器不报告大小时 `RemoteError`，不用 -1 哨兵。 |

`FtpEntry.Modified` 原样采用服务器的时间，**不做时区换算**：MLSD 是 UTC（RFC 3659），LIST 通常是服务器本地时间，库不知道服务器的时区。`Size` 与 `Modified` 为 null 表示目录或服务器未报告。

## 编码（P63-D12）

`EncodingName` 为 null 时，服务器支持 UTF-8 就用 UTF-8，否则用 SDK 的默认编码。指定名称（如 `GBK`）时，`FtpEncodings` 第一次解析该名称会注册 `CodePagesEncodingProvider`。这是**进程级**注册，.NET 8 用 GBK 必须先注册（F2：用户 2026-10-18 接受进程级注册；备选是不注册、只用 `CodePagesEncodingProvider.Instance.GetEncoding`）。名称无效时 `Create` 抛 `ArgumentException`。

## FTPS（P63-D13）

- `Explicit`：在控制端口（默认 21）明文开始，用 `AUTH TLS` 升级。`Implicit`：一开始就是 TLS，通常用 990 端口。**库不替你改端口**，选择 `Implicit` 时要自己设 `Port`。
- 数据连接同样加密（`PROT P`）。
- 证书默认校验。`AllowUntrustedCertificates = true` 接受任何证书（包括主机名不符），`Create` 记 Warning。
- TLS 失败归为 `AuthenticationFailed`（无应答码）。

## 超时（P63-D14）

| 选项 | 作用 |
|---|---|
| `ConnectTimeout` | 建立控制连接。 |
| `ReadTimeout` | 等待控制连接上的应答；同时作为写超时。 |
| `DataConnectionTimeout` | 建立数据连接，以及数据连接上的读写。 |

不使用 SDK 的自动重试（`RetryAttempts = 1`），重试由宿主决定。

## 错误分类（`Sdk/FtpErrorMap`）

全包只有这一处做分类，映射表与原因见计划第 16 节。

| 情况 | `ErrorKind` | 应答码 |
|---|---|---|
| 调用方取消 | `Cancelled` | — |
| 建立连接、等待应答、数据连接超时；操作中断线（F30） | `Timeout` | — |
| 530 登录失败；TLS 认证失败 | `AuthenticationFailed` | 530 / — |
| 421、425、426 | `ConnectionClosed` | 应答码 |
| 452、552 | `ResourceExhausted` | 应答码 |
| 502、504 | `NotSupported` | 应答码 |
| 其他 4xx、5xx（含 550、553） | `RemoteError` | 应答码 |
| 连接被拒、套接字错误、控制连接中断 | `ConnectionClosed` | — |
| SDK 报告传输失败但没有应答码 | `RemoteError` | — |
| 库自己检查出的"不存在" | `RemoteError` | — |
| 路径为空；上传的本地源文件不存在 | `InvalidRequest` | — |
| 本地文件系统错误 | `Unspecified` | — |
| 释放之后的操作 | `ConnectionClosed` | — |
| 其他 SDK 异常 | `Unspecified` | — |

## SDK 行为与补偿（计划第 18.2 节 F1–F31 的要点）

- **SDK 静默成功的地方，库先检查存在性**（F14–F18）：列目录为空，删除与重命名的路径。
- **SDK 忽略流的位置**（F23）：用以调用方当前位置为起点的视图包装。
- **登录失败后 SDK 仍报告已连接**（F9）：失败时发送 QUIT 并关闭。
- **超时之后 SDK 对象不能直接复用**（F31）：不先 `Connect` 的操作抛 `NullReferenceException`。超时后 SDK 已置为未连接，库在下一次操作前按需连接。
- **中断线 = 读超时**（F30）：见上文的 Timeout 说明。
- **覆盖上传非原子**（F22）：文档写明，不实现原子上传。
- **CodePages 注册是进程级的**（F2）：用户 2026-10-18 接受现状。

## 测试服务器

进程内的模拟服务器在 `Tests/Junevy.Communication.Ftp.Tests/TestSupport/FakeFtp*`，共 11 个文件，有效行 1207。**FTP 没有可在进程内直接启动的官方服务器**，所以自写。它只实现 FluentFTP 实际发出的命令，其余返回 502，因此每一条实现的命令都由客户端的真实调用触发；清单写在计划第 18.2 节。

故障注入（`FakeFtpFaults`）：`DropConnectionOn`（收到命令时直接断开，不应答）、`DropConnectionBeforeTransferReply`（传输完成后、226 之前以 RST 断开）、`DelayReply`、`FailCommand`、`AbortTransfersAfter`（426）、`RejectDataConnections`（425）、`CloseControlConnectionAfter`。服务器方法 `DropAllControlConnections` 模拟空闲断线。服务器侧计数（`ConnectionCount`、命令日志）用于证明串行（10 个并发上传只用一条控制连接，命令没有交错）和释放（释放后没有 DELE）。

## 依赖与日志

- 依赖：`FluentFTP` 55.0.0。没有其他 SDK 依赖。
- 日志：`ILoggerFactory` 可选。每个操作结束时记录一条（`FtpOperation`）：成功记 Debug（操作名、远程路径、耗时，传输带字节数），失败记 Warning（带分类与应答码），调用方取消记 Debug；耗时不含排队等待（P6.3 审阅 R1）；SDK 日志桥接到类别 `Junevy.Communication.Ftp.Sdk`，级别 Verbose → Trace、Info → Debug、Warn → Warning、Error → Error。**密码与文件内容在任何级别都不记录**（测试钉住）；SDK 的主机、用户名、密码脱敏开关保持默认，不允许打开（P63-D15）。

## DI 与 Skill

- `FtpClientFactory : NamedFactory<FtpClient>`；`AddFtpClients()` 可重复调用（P6-C9）。
- 使用方的 Skill：`Skills/using-junevy-ftp/SKILL.md`；包内 README：`Junevy.Communication.Ftp/README.md`。

## 相关

- 作者视角的坑与做法：[[Agent协作/新增协议指南]] 的第七节"SDK 协议适配"
- 复用件：Core 的 `CommResult`、`NamedFactory`、`TimeoutScope`
- 设计：设计文档第 14.3 节与第 20.11 节"实施修订（P6.3）"；SDK 摩擦 F1–F31 与模拟服务器的命令清单见 `docs/superpowers/plans/2026-10-12-p63-ftp.md` 第 18.2 节；协议族的总体位置见 [[架构设计/通讯库总体架构（协议族）]]
