# Changelog

本项目所有显著变更记录于此。格式参考 Keep a Changelog，版本遵循 SemVer。

## [Unreleased] — 分支 fix/modbus-p1-critical（v1.0.2）

### 新增（Added）

- 2026-10-05 `ModbusFactoryBuilder`：无 Microsoft DI 容器场景下的 `ModbusFactory` 流式构建器（适配 Prism 等第三方容器直接 `RegisterInstance`）。提供 `WithLoggerFactory` / `WithTcpParser` / `WithRtuParser` / `WithFrameBuilder` / `WithConnectionManager` / `Build`；未设置项与 `AddModbusFactory` 的 DI 默认值一致。
- 2026-10-05 `ModbusErrorKind` 结构化错误分类（`ModbusResult.ErrorKind` + `Fail` 重载），重连判断不再依赖错误消息字符串匹配。
- 2026-10-05 `ModbusExceptionCode` 线上异常码枚举（含 `Describe()` 扩展）；`ModbusException.ErrorCode` 类型同步更换，参数校验统一改抛标准异常。
- 2026-10-05 `ModbusTransportBase` 公共传输基类：TCP/RTU 的请求/重试/重连骨架去重（两文件 1233 行 → 752 行）。
- 2026-10-05 TCP 真异步 I/O（NetworkStream + 全链路 `CancellationToken`，net8.0 取消即时生效；net472 在下一个 I/O 边界生效）。

### 变更（Changed，破坏性）

- 2026-10-05 `ModbusTCP`/`ModbusRTU` 更名 `ModbusTcpClient`/`ModbusRtuClient`；命名空间 `TCP`/`RTU` → `Tcp`/`Rtu`；配置类更名 `ModbusTcpClientConfig`/`ModbusRtuClientConfig`。
- 2026-10-05 `ReadTimeOut`/`WriteTimeOut` → `ReadTimeout`/`WriteTimeout`；`ModbusRequest.Start`/`Length` → `StartAddress`/`Quantity`；`IntervalTime` → `FrameReadInterval`；删除 `SetPort`（`Port` 改为普通可写属性）与 `CheckConnection`（用 `IsConnected`）。
- 2026-10-05 库不再修改调用者的 `ModbusRequest`；`IModbusFrameBuilder` 协议参数显式化；`ModbusRequest` 收敛为纯数据类（移除 UI 事件）。
- 2026-10-05 目标框架 `net472;net6.0` → `net472;net8.0`；Microsoft.Extensions.* 与 System.IO.Ports 升级至 8.0.0；生成 NuGet 文档文件、MIT 许可证表达式。
- 2026-10-07 重试与重连解耦：Reconnect=false 时，需要重建连接的失败（超时、连接关闭、发送失败）立即返回真实错误，不再空转重试并被覆盖为 "Not connected"；未连接且 Reconnect=false 时请求立即返回，不再等待 RetryInterval。Reconnect=true 的行为不变。
- 2026-10-07 RegisterAlias 在目标键不存在或别名键等于目标键时返回 false（此前允许创建悬空别名）；RemoveAlias 对直接实例不再"删除后放回"；TryRemove / RegisterAlias / RemoveAlias 的注册表结构变更在内部锁下执行，消除并发移除/建别名与主键移除的竞态。

### 修复（Fixed）

- 2026-10-05 TCP 事务 ID 由库内自增管理并按精确值匹配响应，消除旧响应错配风险（此前上线值恒为 `TransactionId+1` 且从不递增）。
- 2026-10-05 Modbus 异常响应（FC|0x80）在解析器层即返回失败（含异常码），作为终态不重试——此前在裸 `Request` 层被误报为成功；RTU 侧不再被"等待后续帧"吞成超时。
- 2026-10-05 结果式 API 不再逃逸异常（非法 MBAP 长度、RTU 零长解析帧改为失败返回），同步/异步行为对齐。
- 2026-10-05 `ModbusConnectionManager.GetOrAdd` 并发竞态不再泄漏输家实例（输家安全释放后进入赢家路径）。
- 2026-10-05 DI 容器中 `IModbusConnectionManager` 与 `IModbusFactory` 共享同一注册表。
- 2026-10-05 Connect/Disconnect 与请求经 `requestLock` 串行化，消除连接重建与在途请求的竞态。
- 2026-10-07 TCP 异步请求的 ReadTimeout / WriteTimeout 生效（此前 Socket.ReceiveTimeout 对 NetworkStream.ReadAsync/WriteAsync 无效，服务端不应答时 RequestAsync 永久挂起）；ReadTimeout 为整帧总时限，超时返回 ErrorKind.Timeout，用户取消返回 ErrorKind.Cancelled。
- 2026-10-07 异步请求的自动重连改用 OpenConnectionAsync（此前 Task.Run 包装同步连接，占用线程池线程且不响应取消）；TCP 异步连接中用户取消不再被当作连接超时吞掉。
- 2026-10-07 高层 API（ReadCoils / ReadHoldingRegisters / ReadWriteMultipleRegisters / GetCommEvent* / ReportServerId / ReadExceptionStatus 等）保留底层的 ErrorKind（此前一律重置为 Unspecified）；底层返回数据过短时返回 ProtocolViolation，不再抛出 ArgumentException；ModbusHelper.ParseCoils 的长度检查修正为 3 + 字节数；通信异常按类型归类为 Timeout / ConnectionClosed 而非 Unspecified；RTU 接收缓冲溢出报 ProtocolViolation。
- 2026-10-07 ModbusConnectionManager.TryRemove 移除带别名的主连接时，同时移除所有指向它的别名并释放实例（此前实例既不释放也不可达，串口/套接字泄漏）；同一实例被多个直接键引用时仅在最后一个引用移除时释放。
