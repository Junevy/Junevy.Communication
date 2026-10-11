---
tags: [架构设计, Junevy.Communication, Plc]
---

# Plc 地址型设备协议套件

> 代码基线：`Junevy.Communication.Plc`（net472;net8.0，`1.0.0-preview.1`，P2）。与代码冲突时以代码为准。设计依据与差异见设计文档第 12 节与第 20.2 节（P2 实施修订）。

## 职责划分

套件负责所有"按地址读写存储区"的协议共有的部分；协议包只写协议差异。

| 归属 | 内容 | 代码位置 |
|---|---|---|
| 套件（Plc） | `IPlcClient`、`PlcClientBase<TAddress>`（请求管线）、类型化扩展、地址与软元件定义、选项、工厂、DI | [Junevy.Communication.Plc/Client/PlcClientBase.cs](file://Junevy.Communication.Plc/Client/PlcClientBase.cs)、[Junevy.Communication.Plc/Pipeline/PlcRequestPipeline.cs](file://Junevy.Communication.Plc/Pipeline/PlcRequestPipeline.cs)、[Junevy.Communication.Plc/Extensions/](file://Junevy.Communication.Plc/Extensions/)、[Junevy.Communication.Plc/Addressing/](file://Junevy.Communication.Plc/Addressing/) |
| 协议（如 MELSEC） | 地址类型与设备表、十个抽象钩子与 `ValidateRange`、帧编解码、通道组件（分帧、关联、键提取器、心跳探测）、客户端工厂、创建器、DI 扩展 | 见 [[协议细节/MELSEC MC 协议]] 与 [[Agent协作/新增协议指南]] |
| 公共工具（Core） | 字节序转换 `WordOrder`、`ByteTransform`（[Junevy.Communication.Core/Buffers/](file://Junevy.Communication.Core/Buffers/)） | 设计 P2-D3 |

## 请求管线

`PlcRequestPipeline` 对每个公开操作依次执行：

1. 参数验证，包括协议的 `ValidateRange`（不可表示的请求返回 `InvalidRequest`）。
2. 已释放检查：返回 `NotConnected`，消息 `The PLC client has been disposed.`。
3. 未连接检查：立即返回 `NotConnected`，不等待、不重试。
4. 获取客户端内部的操作锁。读-改-写与所有分包都在锁内完成，所以同一客户端内的操作顺序不会被打断（P2-D6）。
5. 按每次请求的单位上限拆包，逐包发送并解析；任一包失败则整体返回该失败，不返回部分数据。

边界处所有异常都收口为失败结果；调用方取消返回 `Cancelled`。公开操作不向外抛出运行期异常（P2-D14）。

## 重试（P2-D7）

- `RetryCount` 默认 `0`，快速失败；轮询循环本身就是重试。
- 重试针对单个请求（拆包时只重试失败的那一包）。
- `RemoteError`、`InvalidRequest`、`NotSupported`、`Cancelled`、`NotConnected` 不重试。
- `Timeout`、`ConnectionClosed`、`ProtocolViolation` 在 `RetryCount > 0` 且 `RetryInterval` 内 `WaitForConnectedAsync` 成功时重试，否则返回真实的 `ErrorKind`。解析期的协议违规也纳入（计划第 20 节 Task 4 审阅接受）。

## 位操作（P2-D10）

- 读取字设备中的一位：读整个字再取位。协议只需实现字与位两组钩子，字设备的位读取由字读取完成。
- 写入字设备中的一位：默认 `NotSupported`。`PlcClientOptions.AllowReadModifyWriteBits = true` 时在操作锁内读-改-写，并每次记录 Warning。读-改-写与 PLC 程序的并发写会竞争，因此默认关闭。

## 类型化读写（P2-D5、D8、D9）

- 类型化方法使用显式名称：`WriteInt16Async`、`WriteFloatAsync` 等。没有按类型重载的 `WriteAsync` 族，字面量不会绑定到错误宽度。
- 数据格式由协议选项决定（`PlcDataFormat`；MELSEC 默认字序 `DCBA`、ASCII 字符串）。没有按调用覆盖。
- 原始字节 `ReadAsync` / `WriteAsync` 使用协议的规范化布局，与编码方式无关。

## 所有权（P2-D13）

客户端拥有通道。`Dispose` / `DisposeAsync` 释放通道，之后的操作返回 `NotConnected`。

## 工厂与依赖注入

- `PlcClientFactory`（[Junevy.Communication.Plc/Factory/PlcClientFactory.cs](file://Junevy.Communication.Plc/Factory/PlcClientFactory.cs)）按配置对象的精确运行时类型选择 `IPlcClientCreator`；没有对应创建器时抛出 `NotSupportedException`。
- `GetOrAdd` 名称已存在时返回已有实例，配置被忽略。名称与别名经 Core 的 `NamedRegistry` 管理。
- `PlcClientFactoryBuilder` 供没有 DI 容器的宿主（例如 Prism）使用，同一配置类型的两个创建器在 `Build` 时报错。
- `AddPlcClients()`（[Junevy.Communication.Plc/DependencyInjection/PlcServiceCollectionExtensions.cs](file://Junevy.Communication.Plc/DependencyInjection/PlcServiceCollectionExtensions.cs)）以 `TryAddSingleton` 注册工厂，可重复调用。协议包的 `AddX()` 通过 `TryAddEnumerable` 注册自己的创建器。

## 测试底座

- `Junevy.Communication.Testing` 提供 `PlcMemory`（线程安全的稀疏字与位存储）、`DeviceSimulator`（把请求流翻译为应答的设备端循环，可挂在 TCP、UDP 或流上）、`SequenceFactory`（分段的 `ReadOnlySequence<byte>`，用于跨缓冲边界的分帧测试）。
- 协议的测试替身（MELSEC 为 `McDeviceAdapter`）把请求翻译成 `PlcMemory` 操作，挂在 `DeviceSimulator` 上。

## 扩展性（P2 实测结论）

P2 用 MELSEC 检验了套件的扩展性。协议只写了地址、帧、钩子、组件与工厂，连接、重连、心跳、超时、拆包、重试、类型化读写、日志与 DI 都由套件与通道族提供。摩擦记录（计划第 20.2 节）的主要结论：

- 构建钩子返回 `void`，不能报告"协议不可表示"的参数；这类检查放在 `ValidateRange`。
- `PlcAddress.Device` 为接口类型，协议的地址类型需要用 `new` 隐藏为具体类型。
- `IFrameCodecFactory` 拿不到 `FramingOptions.MaxFrameLength`，`ChannelComponents` 会静默覆盖同名的传输配置设置（协议需要守卫）。
- 通道族与套件的若干内部类型（统计对象的构造函数、透传编码器）只能通过替代方案获得；这些属于 Channels 的通用能力，P2 后评估。

相关笔记：[[架构设计/通讯库总体架构（协议族）]]、[[API参考/通道与传输（TCP UDP 串口 TLS）]]。

## P2.5 变更：协议的创建入口

- 协议只提供一个创建入口 `Create(IChannelConfig transport, …)`；内置传输的重载只是它的快捷方式。
- 通道由 `PlcChannelBinding.CreateChannel(creator, name, transport, configureComponents, loggerFactory)` 创建：组件由创建器的 `Delivery` 与 `Medium` 决定，传输配置的类型必须是创建器的 `ConfigType`。
- `PlcClientFactory` 与 `ChannelFactory` 派生 Core 的 `NamedFactory<TItem>`；`TryGet` 的输出参数名为 `item`（P25-D12）。
- 详细说明见 [[架构设计/通道与协议的扩展面（P2.5）]]。
