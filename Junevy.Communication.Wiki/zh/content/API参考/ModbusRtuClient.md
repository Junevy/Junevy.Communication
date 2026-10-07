# ModbusRtuClient

<cite>
**本文引用的文件**
- [Junevy.Communication.Modbus/Rtu/ModbusRtuClient.cs](file://Junevy.Communication.Modbus/Rtu/ModbusRtuClient.cs)
- [Junevy.Communication.Modbus/Rtu/ModbusRtuClientConfig.cs](file://Junevy.Communication.Modbus/Rtu/ModbusRtuClientConfig.cs)
- [Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs](file://Junevy.Communication.Modbus/Core/Transports/ModbusTransportBase.cs)
</cite>

命名空间 `Junevy.Communication.Modbus.Rtu`。sealed 类，继承 `ModbusTransportBase`。旧名 `ModbusRTU`（1.0.x 破坏性更名）。

## 构造重载（4 个，与 TCP 对称）

```csharp
new ModbusRtuClient(ModbusRtuClientConfig config)
new ModbusRtuClient(config, ILogger<ModbusRtuClient> logger)
new ModbusRtuClient(config, logger, IResponseParser responseParser)        // 默认 RtuProtocolParser
new ModbusRtuClient(config, logger, responseParser, IModbusFrameBuilder)   // 默认 ModbusFrameBuilder
```

## ModbusRtuClientConfig 全字段（12 项）

| 属性 | 类型 / 默认 | 说明 |
|---|---|---|
| `PortName` | string / `"COM20"` | 串口名（如 COM3、/dev/ttyUSB0）；工厂层校验**不得为空** |
| `BaudRate` | int / `9600` | 波特率（工厂层 ≤0 回填 9600） |
| `Parity` | `Parity` / `None` | 校验位（System.IO.Ports） |
| `DataBits` | int / `8` | 数据位 5–8（工厂层越界回填 8） |
| `StopBits` | `StopBits` / `One` | 停止位 |
| `DtrEnable` | bool / `false` | DTR 信号 |
| `RtsEnable` | bool / `false` | RTS 信号 |
| `ReadTimeout` | int / `2000` | 同步路径设为 `SerialPort.ReadTimeout`；异步路径经 linked CTS 强制。⚠️ 与 TCP 不同，它只约束**单次** Read 阻塞，不是整帧总时限 |
| `WriteTimeout` | int / `2000` | 同步路径设为 `SerialPort.WriteTimeout` |
| `RetryCount` | int / `3` | 首次失败后重试次数。RTU 的失败不需要新连接（如 CRC 错），按此次数重试 |
| `RetryInterval` | int / `100` | 重试间隔毫秒 |
| `FrameReadInterval` | int / `30` | **RTU 独有**：Read-until-frame 循环中两次解析尝试之间的等待毫秒 |
| `Reconnect` | bool / `false` | 串口故障后重开开关 |

（**没有** `ConnectTimeout`——串口 Open 是本地操作，无网络握手。）

## 连接行为

- `OpenConnection`：串口已开则先 Close → `ConfigurePort()`（把 12 项配置逐一赋给 `SerialPort`）→ `Open()`；失败日志后返回 false。
- 异步连接 = `Task.Run(OpenConnection, ct)`（先检查一次取消）。
- `IsConnected` = `!disposed && serialPort.IsOpen`。
- `InvalidateConnection`（失败标记）= 串口 Close；`DisposeConnection` = Close + `serialPort.Dispose()`。

## 收发行为（与 TCP 的关键差异）

- **发送前清缓冲**：`DiscardInBuffer()` + `DiscardOutBuffer()`——丢弃上一次请求可能残留的半帧，再写入本帧（ArrayPool 租 `MaxRtuAduLength=256`）。
- **接收是 Read-until-frame 循环**（`ReceiveFrame`/`ReceiveFrameAsync`）：
  1. 读到字节累计 ≥5 才开始尝试解析（RTU 最小合法帧 5 字节）；
  2. 每轮把累计缓冲交给 `RtuProtocolParser.ParseResponse` 做逐字节扫描；
  3. 解析成功且 CRC 通过 → 返回该帧；解析未成功 → 等 `FrameReadInterval`（默认 30ms）再读；
  4. 缓冲租借 `MaxRtuAduLength+1=257`——**恰满 256 字节的完整帧**不会误判为溢出（审查修复 B8，读满即报 `ProtocolViolation`）。
- **读超时的两种来源**（异步路径精确区分）：
  - 同步：`SerialPort.Read` 抛 `TimeoutException` → `Fail(Timeout)`；
  - 异步：`BaseStream.ReadAsync` + linked CTS `CancelAfter(ReadTimeout)`；OCE 且**只有**超时 CTS 触发 → `Fail(Timeout)`；用户令牌触发 → `Fail(Cancelled)`。
- **异常响应只认 CRC**：解析器遇到 FC|0x80 候选帧，CRC 不通过则当作噪声跳过继续扫（不会把噪声误报为从站异常）。
- **无事务 ID**：`AssignsTransactionId=false`，请求对象原样使用。
- RTU 请求开始会记 Information 级日志（`LogRequestStarted` 覆写），TCP 默认不记。

## 配置转发 / 骨架钩子取值

`RequiresNewConnection` 保持基类默认 **false**——RTU **不**依据 `ErrorKind` 销毁串口；重连只发生在 `EnsureConnected` 发现 `IsOpen=false` 时。因此 RTU 的失败（如 CRC 错）即使 `Reconnect=false` 也按 `RetryCount` 在同一串口上重试（与 TCP 相反）。消息文本用 " [Request] Port not open." / " [Request] Send frame failed."。

## RS-485 多从站

一条总线 = 一个 `ModbusRtuClient` 实例（一个物理串口）。多个逻辑从站**共享**该连接，在工厂层用别名表达（`RegisterAlias("slave-1", "rs485-bus")`），逐从站用 `SlaveId` 区分。见 [[API参考/工厂与依赖注入]]。

## 使用注意

1. `FrameReadInterval` 过小会空转 CPU、过大拖慢多帧场景；默认 30ms 适配多数 9600bps 场景，高速总线可调小。
2. 同步 `Request` 内部用 `Thread.Sleep(FrameReadInterval)`；异步用 `Task.Delay`——不要在 UI 线程调同步 API。
3. 串口被其他进程占用时 `Open()` 抛 `UnauthorizedAccessException`（在通信异常白名单内，归为请求失败而非崩溃）。
