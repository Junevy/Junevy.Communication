# 调试助手（Junevy.Communication.DebugAssistant）

WPF 应用，用于现场调试 Junevy.Communication 的各个协议包。每种协议一个页签（共 19 种）：可以连接设备或服务端、读写数据、收发报文，并查看操作记录与原始报文。另有四种模拟设备（Modbus 从站、MC 从站、S7 CPU、FINS 从站），没有真机时也能做回环测试。

本工具不打包发布，只供开发与现场调试使用。

## 运行要求

- Windows，.NET 8 SDK（用于 `dotnet run`）与 .NET 8 桌面运行时（WPF，`net8.0-windows`）。
- ASP.NET Core 运行时 8：WebApi 服务端页使用嵌入式 Kestrel。
- Junevy.Controls 3.3.0（界面控件库）。它来自本地 NuGet 源，还原时必须能找到该版本（DA-D3）。

## 启动

在仓库根目录执行：

```bash
dotnet run --project Tools/Junevy.Communication.DebugAssistant
```

## 界面

- **左侧协议树**：PLC、工厂系统、自定义协议、模拟设备四组。双击叶节点打开页签；双击分组只展开或收起。
- **页签**：标题可以双击或按 F2 改名。关闭页签会先释放它的连接、服务端或模拟器（限时 5 秒）。默认名称为"协议短名 #n"。
- **报文与日志面板**（每个页签底部，默认折叠，点击标题展开）：TX / RX 报文（十六进制与 ASCII）、操作记录（时间、操作、目标、耗时、成败、错误种类、协议错误码、消息）与库的日志。支持暂停、筛选、清空，以及导出为 .txt。
- **工具栏**：保存工作区、导入页签、导出当前页签（单个页签的 .json）、关闭全部页签（需要确认）、切换浅色与深色主题。

## 19 种页签

| 分组 | 页签 | 主要功能 |
|---|---|---|
| PLC | Modbus（TCP / RTU / UDP） | 传输与帧格式的组合：Modbus TCP、RTU over TCP、Modbus UDP、RTU over UDP、Modbus RTU；读写线圈与寄存器，掩码写（0x16）、读写合一（0x17） |
| PLC | 三菱 MC（MELSEC） | 3E 与 4E（二进制或 ASCII，TCP 或 UDP），4C（串口）；软元件速查表 |
| PLC | 西门子 S7 | ISO-on-TCP，机架与槽号，连接类型 PG、OP、Basic；String 与 WString 读写 |
| PLC | 欧姆龙 FINS | FINS/TCP，客户端节点、目的网络与单元；区域速查表 |
| 工厂系统 | SECS/HSMS 设备端 | 被动或主动连接；按 S、F 登记自动应答规则；SML 编辑与常用模板 |
| 工厂系统 | MQTT 客户端 | MQTT 3.1.1 与 5.0，TLS，自动重连，遗嘱，订阅与发布 |
| 工厂系统 | OPC UA 客户端 | 浏览、读写、订阅（只支持 None 安全策略与匿名登录） |
| 工厂系统 | WebApi REST 客户端 | 请求构造，认证，重试，业务信封，健康检查，事件时间线 |
| 工厂系统 | WebApi SOAP 客户端 | SOAP 1.1 与 1.2，原始 XML 调用；Fault 显示为远端错误 |
| 工厂系统 | WebApi 服务端（HTTP 服务端） | 路由表（固定 JSON、回显、业务失败、指定状态码，可设延迟），去重，请求日志 |
| 工厂系统 | FTP / FTPS 客户端 | 无加密、显式或隐式 TLS；目录浏览，上传与下载（进度，Overwrite 或 Skip），新建目录，删除，递归删除（需要确认），重命名 |
| 自定义协议 | TCP 客户端 | 分帧、请求与应答关联、心跳、重连、TLS，定时循环发送 |
| 自定义协议 | TCP 服务端 | 监听，会话列表，远端白名单，自动应答规则 |
| 自定义协议 | UDP | 定向与非定向，广播，组播，TTL |
| 自定义协议 | 串口 | 串口参数，空闲间隔分帧 |
| 模拟设备 | Modbus 从站 | 站号，RTU 或 MBAP，内存表，故障注入 |
| 模拟设备 | MC 从站 | 3E、4E、4C 帧的 MC 从站，内存表，故障注入 |
| 模拟设备 | S7 CPU | ISO-on-TCP 从站，区 I、Q、M 与 DB，PUT/GET 开关，内存表 |
| 模拟设备 | FINS 从站 | FINS/TCP 从站，节点号，区域内存表 |

## 报文截断

日志采集依赖库在 Debug 级别写出的字节日志（`TX {Length} bytes: {Hex}`，见 Channels README 的 Logging 一节，DA-D5）。库的日志每帧最多输出 256 字节的十六进制，超出部分被截断：十六进制列标注"（已截断，共 N 字节）"，ASCII 列标注"（已截断）"。`Length` 仍是完整长度。这是已知限制，记录在计划第 33.2 节的 F-1。

MQTT、OPC UA、FTP 与 WebApi 没有字节级日志。它们的页签显示操作记录与各包的事件。

## 模拟设备与故障注入

每种模拟设备是一个页签，按以下顺序使用：

1. **宿主参数**：传输（TCP、非定向 UDP 或串口），监听地址与端口或串口。串口上的 Modbus 固定为 RTU，串口上的 MC 固定为 4C。
2. **设备参数**：Modbus 的站号与帧格式，MC 的帧与编码，S7 的 PDU 大小与 PUT/GET，FINS 的节点号。
3. **内存表**：字区按字显示并可编辑；位区与字节区只读。
4. **故障注入**：应答延迟、不应答、强制错误码、破坏校验、断开。"收到下一个请求后断开"只生效一次。
5. **请求日志**：每个处理过的请求一行。

启动模拟器后，在客户端页签中连接同一个地址即可互连。

## 工作区

- 文件位置：`%LocalAppData%\Junevy.Communication\DebugAssistant\workspace.json`（`schemaVersion: 1`）。
- 退出时自动保存，也可以手动保存。启动时恢复页签，但全部处于未连接状态，不会自动连接。
- 密码不写入文件：MQTT 与 FTP 的密码只保存在内存中；TLS 与 WebApi 的凭据只保存环境变量名。
- 文件损坏或版本不符时，改名为 `.bak` 保留，以空工作区启动并给出提示。不认识的协议种类被跳过，并给出提示。

## 测试

```bash
dotnet test Tests/Junevy.Communication.DebugAssistant.Tests
```

串口测试只在设置了环境变量 `JUNEVY_SERIAL_PAIR` 时运行，值为虚拟串口对 `COM20,COM21`。开发机上只允许使用这一对端口，不能打开 COM3 或 COM4。没有设置该变量时，串口用例跳过（DA-D16）。

## 新增一种页签（DA-D19）

新增一种页签需要改四处，其他地方不得出现针对 `ProtocolKind` 的 `switch`：

1. `Shell/ProtocolCatalog.cs`：登记一条 `ProtocolDescriptor`（种类、分组、标题、短名、图标）。
2. `Protocols/<协议>/`：参数记录 `XxxSettings`；在 `Workspace/SessionSettings.cs` 加一行 `[JsonDerivedType(typeof(XxxSettings), "<判别符>")]`。
3. `Protocols/<协议>/XxxSessionViewModel.cs`：继承 `SessionViewModel`；在 `Hosting/SessionRegistration.cs` 的 `Pages` 表中加一行工厂。键控服务由目录自动生成，缺少工厂时启动即失败，不会静默退回占位页签。
4. `Views/<协议>/XxxPageView.xaml`：在 `Views/DataTemplates.xaml` 中按视图模型类型加一个 `DataTemplate`。

架构测试检查每个种类都有描述、参数类型、视图模型注册与 `DataTemplate`。

## 已知限制

- 字节日志每帧最多 256 字节（见上文"报文截断"）。
- MQTT 页无法显示重连后恢复了几个订阅：库没有公开该数量（F-18，待决定，需要改库）。
- FTP 页无法区分"目录为空"、"目录不存在"与"无权限列出"：库把列目录的 550 应答当作空列表（F-20，待决定，需要改库）。
- S7 与 FINS 没有在真机上验证；SECS、MQTT 与 FTP 只在进程内替身上验证（与各包 README 一致）。
- 不包含 SECS 主机端、MQTT Broker、OPC UA 服务端、FTP 服务端与 GEM（见计划第 32 节）。
