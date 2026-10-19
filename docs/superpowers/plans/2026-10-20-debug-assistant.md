# 调试助手实施计划：Junevy.Communication.DebugAssistant（WPF）

- 日期：2026-10-20；分支：`feat/debug-assistant`（Task 0 从 master `fe82d79` 新建）。
- 执行者：另一个智能体。审阅者：在用户通知后验收，重点与前几期相同：
  - 代码：质量、clean code、架构设计；
  - 功能：实现是否完整，以及缺陷检查。
- 用户的要求（2026-10-20）：
  1. 新建一个调试助手，包含本仓库的全部通讯协议；另外确认能否用 channel 自定义基于 socket 的通讯协议（结论见第 1 节）。
  2. 不使用 Prism，优先用微软系的工具包：CommunityToolkit.Mvvm、微软 DI 容器。UI 使用 Junevy.Controls（本地 NuGet 源中的包）。
  3. 布局：左侧 SideMenu 或 TreeMenu，右侧 TabControl。双击左侧的协议，右侧新增一个页签；页签标题可以改名，页签可以关闭。
  4. 尽量展示每个协议的全部特有功能，例如 MELSEC 的不同帧类型。
- 用户的决定（2026-10-20 问答）：
  - 项目放在本仓库 `Tools/`；Modbus 页覆盖旧工具的功能后，删除 `Junevy.Communication.Test`（DA-D1、DA-D18）；
  - 报文用日志采集，**不改库**（DA-D5）；
  - **做**模拟设备：Modbus、MC、S7、FINS 的模拟从站（DA-D14，里程碑 4）；
  - 保存并恢复工作区（DA-D11）。
- 依据：
  - 各包 README 与 `Skills/using-junevy-*/SKILL.md`：公开 API 与行为，是功能矩阵（第 7 节）的来源；
  - 设计文档 `docs/superpowers/specs/2026-10-10-communication-core-design.md`：第 3 节（程序集表，本期要加三行）、第 5 节（通道族）、第 12 节（Plc 套件）；
  - 黄金向量：P2 计划第 18 节（MC）、P4 计划第 17 节（S7、FINS）、P5.1 计划第 17 节（HSMS）、P7 计划第 21 节（Modbus）；
  - 知识库：`Agent协作/开发约定与验收基线.md`（硬性约定、质量标准、文档同步义务表）；
  - Junevy.Controls skill（`junevy-controls`）与它的 README：选型规则、主题令牌、TabControl、TreeView 的用法与陷阱。
- 本期目标有三个：
  1. 交付可用的调试助手，共 19 种页签：15 种覆盖本仓库全部协议的现有角色，另外 4 种是模拟设备（第 4.3 节）；
  2. **验证各包公开 API 的可用性**：助手和模拟器只能使用公开 API。发现的缺口按第 2 节第 8 条记入摩擦记录（第 33.2 节），本期**不改库**；
  3. 模拟器与客户端是两份独立实现，用既有的黄金向量交叉验证两边。
- **可行性门槛**：
  - 门槛 1：里程碑 2 结束（Task 6）。执行者汇报外壳、日志采集与三种传输页的情况；
  - 门槛 2：里程碑 4 结束（Task 14）。执行者汇报模拟器与客户端的端到端矩阵。
  - 出现必须改库才能实现的功能时，停下等用户决定：改库，或者从本期去掉该功能。

---

## 0. 已固定的设计决策

| # | 决策 | 理由 |
|---|---|---|
| DA-D1 | **项目与位置**（用户决定）：<br>• `Tools/Junevy.Communication.DebugAssistant`：WPF 应用，`WinExe`；<br>• `Tools/Junevy.Communication.DebugAssistant.Simulators`：模拟器类库，不引用 WPF；<br>• `Tests/Junevy.Communication.DebugAssistant.Tests`：测试项目。<br>`Junevy.Communication.slnx` 新增解决方案文件夹 `/Tools/` 放前两个项目，测试项目放进 `/Tests/`。三个项目都 `IsPackable=false` | 用户决定；与库项目分开，避免被误当成包发布 |
| DA-D2 | **构建设置**：<br>• 新增 `build/Junevy.Communication.Tools.props`，由两个工具项目显式 Import（沿用 D2"显式 Import、不用 Directory.Build.props"）；<br>• 内容：`Nullable`、`ImplicitUsings`、`LangVersion latest`、`TreatWarningsAsErrors`、`IsPackable=false`；**不** Import `Common.props`（它带 `net472` 与 XML 文档要求）；<br>• 应用与测试为 `net8.0-windows`。模拟器为 `net8.0`，不引用 WPF，可以不开窗口测试。<br>只面向 .NET 8，因为 Mqtt、OpcUa、Ftp、WebApi.Server 只有 `net8.0` | 助手要引用全部协议包，必须取它们的交集 |
| DA-D3 | **技术栈**（版本固定）：<br>• `CommunityToolkit.Mvvm` 8.4.2：`ObservableObject`、`[ObservableProperty]`、`[RelayCommand]`（含异步与取消）；<br>• `Microsoft.Extensions.Hosting` 10.0.12：DI、日志、配置。必须是 10.0.12，因为 OPC UA SDK 2.0.1 会带入 `Microsoft.Extensions.*` 10.0.12，更低的版本会报 NU1605；<br>• `Microsoft.Xaml.Behaviors.Wpf` 1.1.158：把视图事件转成命令；<br>• `Junevy.Controls` 3.3.0，来自本地源 `D:\Documents\Nuget\Local`，对应 Junevy.Controls 仓库提交 `7d59681`。<br>**禁止** Prism、其他 MVVM 或 IoC 框架、Serilog（旧工具用了它）。日志只用 `Microsoft.Extensions.Logging` 加助手自己的提供程序。架构测试守住这条（第 3 节 U2） | 用户要求微软系、不用 Prism；版本以本机缓存中已有的为准，离线也能还原 |
| DA-D4 | **助手扮演的角色**：<br>• 各包现有的角色：PLC 客户端（Modbus、MC、S7、FINS）、SECS 设备端、MQTT 客户端、OPC UA 客户端、REST 与 SOAP 客户端、WebApi 服务端、FTP 客户端、TCP 客户端与服务端、UDP、串口；<br>• 新增 4 个模拟从站（DA-D14）。<br>不做：SECS 主机端、MQTT Broker、OPC UA 服务端、FTP 服务端、Modbus ASCII，以及各包 README 里"Not supported / Not provided"列出的功能 | 助手展示的是库的能力。库没有的角色，助手不另写一份 |
| DA-D5 | **报文采集**（用户决定：日志采集，不改库）：<br>• 每个页签有自己的 `ILoggerFactory`，最低级别 `Debug`，挂一个页签私有的 `SessionLogSink : ILoggerProvider`；<br>• 通道在 `Debug` 级别写出 `TX {Length} bytes: {Hex}`、`RX {Length} bytes: {Hex}`，数据报写出 `… to {Destination}` / `… from {Remote}`（Channels README 的"Logging"一节写明的行为）。Sink 读结构化状态里的 `{OriginalFormat}`、`Length`、`Hex`、`Destination` / `Remote`，生成报文条目，其余日志生成普通条目；<br>• **已知限制**：`HexFormatter.ToHex` 每帧最多输出 256 字节，超出部分写成 `...(+N bytes)`。界面显示"已截断，共 N 字节"，`Length` 仍是完整长度。这个限制在 README 与第 33.2 节摩擦记录里写明；<br>• MQTT、OPC UA、FTP、WebApi 没有字节级日志，页签显示操作日志与各包的事件；<br>• 测试锁定日志模板（Task 2）：模板一变，测试就失败，不会悄悄丢失报文显示 | 用户决定。不改库，但依赖的行为必须由测试守住 |
| DA-D6 | **客户端的创建方式**：每个页签在"连接"时直接调用 `XxxClient.Create(…, sessionLoggerFactory)`（或 `new TcpServer(config, logger, components)` 等），**不经过**包里的命名工厂（`PlcClientFactory`、`MqttClientFactory` 等）。原因：工厂用容器的全局 `ILoggerFactory`，无法按页签分开报文；工厂里的名称是全局的，与可改名的页签对不上。容器里不注册 `AddModbus()` 一类协议注册 | 每个页签的生命周期由页签自己管理；日志按页签隔离 |
| DA-D7 | **左侧导航用 `jv:TreeView`，不用 `jv:SideMenu`**：<br>• 数据模型为 `TreeMenuItem`，按"PLC / 工厂系统 / 自定义协议 / 模拟设备"四组展开；<br>• 双击叶节点用 `ItemDoubleClick` 路由事件，它冒泡，事件参数带 `Item`（数据项），3.3.0 已包含；<br>• 双击分组节点只展开或收起，不打开页签 | `SideMenu` 继承 `ListBox`，由选中驱动，没有带数据项的双击事件，也不能分组。用户允许二选一 |
| DA-D8 | **页签**：`jv:TabControl`，`ItemsSource="{Binding Sessions}"`，`CanRename="True"`。<br>• **改名**：`ItemContainerStyle` 派生自 `DefaultTabControlItemStyle`，写 `<Setter Property="Header" Value="{Binding Title, Mode=TwoWay}"/>`，**不设 `ItemTemplate`**，使 `Header` 是字符串，控件自带的双击或 F2 改名就会生效。<br>　- 2026-10-20 已在 3.3.0 上验证：改名提交后 `vm.Title` 随之改变；视图模型改 `Title`，页签标题也随之改变。`Header` 为数据项时（不写 Setter）无法进入编辑；<br>　- 空白名称由控件还原为原值；<br>• **关闭**：`TabClosing` 由视图层行为 `CloseTabToCommandBehavior` 处理：置 `e.Cancel = true`，把 `e.Item` 交给 `CloseSessionCommand`。命令先 `await` 会话释放，再从 `Sessions` 中移除。视图模型不接触 `TabCloseEventArgs`；<br>• **默认名称**："<协议短名> #n"，n 按协议各自递增；名称不要求唯一 | 满足"标题可改名、页签可关闭"；释放是异步的，不能在 UI 线程上同步等待（WebApiServer 的 README 明确警告） |
| DA-D9 | **会话生命周期**：<br>• 连接或启动期间，参数区只读。"连接"时把参数映射成库的配置，**每次连接都创建新的客户端**，所以改了参数重新连接即可生效；<br>• 库的构造函数抛出的 `ArgumentException` 显示为参数校验错误，不让程序崩溃；<br>• 关闭页签等于释放会话：断开、停止服务端、停止模拟器，限时 5 秒，失败写日志；<br>• 退出程序：先保存工作区，再并发释放全部会话，总时限 10 秒；<br>• 恢复工作区时，页签一律处于未连接状态，**不自动连接** | 库的选项在 `Create` 时快照，必须重新创建才能生效；自动连接会让启动时意外连到现场设备 |
| DA-D10 | **线程**：<br>• 库的事件在线程池上触发，统一经 `IUiDispatcher` 回到 UI 线程（测试里换成同步实现）；<br>• 日志条目先进一个有界环形缓冲（默认每个页签 5000 条，可调），每 100 ms 批量刷新到界面，轮询时也不会把 UI 淹没；<br>• MQTT 的 `MessageReceived`、OPC UA 的通知回调只入队，不在回调里做任何阻塞操作（两个 README 都要求）；<br>• 全程不允许同步等待异步操作（`.Result`、`.Wait()`、`GetAwaiter().GetResult()`） | 库的契约 + WPF 的单线程模型 |
| DA-D11 | **工作区**（用户决定保存）：<br>• 路径 `%LocalAppData%\Junevy.Communication\DebugAssistant\workspace.json`，带 `schemaVersion: 1`；<br>• 内容：打开的页签按顺序记录协议种类、标题、参数，以及当前选中的页签；<br>• 参数按协议各有一个 `record`，用 System.Text.Json 的 `[JsonPolymorphic]` / `[JsonDerivedType]` 区分种类；<br>• **不保存密码**（MQTT、FTP 的密码字段标 `[JsonIgnore]`）。TLS 与 WebApi 的凭据本来就只保存环境变量名；<br>• 退出时保存，也可以手动"保存工作区"；<br>• 单个页签可以导入或导出参数（`.json`）；<br>• 文件损坏时改名为 `.bak` 保留，以空工作区启动，并用 `MessageBar` 提示；不认识的协议种类跳过，并给出提示 | 用户决定；密码不落盘 |
| DA-D12 | **自定义协议的助手端部件**（第 1 节结论的落地，全部 internal，都是纯逻辑，可单测）：<br>• `ConfigurableResponseMatcher : IResponseMatcher`：两种规则，"应答以某段字节开头"，以及"应答与请求在偏移 N 处的 M 个字节相同"；<br>• `ConfigurableKeyExtractor : IFrameKeyExtractor`：请求、应答各自配置偏移、宽度（1/2/4）和字节序；<br>• `ScriptedInitializer : IConnectionInitializer`：每次连接后发送一段字节，可选等待并比较应答；<br>• 校验辅助（发送前追加到负载末尾）：无、Sum8、XOR8、CRC-16/MODBUS（低字节在前）、CRC-16/CCITT-FALSE、LRC；<br>• 字节输入统一用库的 `ByteSequenceParser` 语法（`hex:` 前缀，或带 `\r \n \t \0 \\ \xHH` 转义的文本），文本编码可选 UTF-8、ASCII、GBK | 第 1 节：库提供了扩展点，但需要写代码；助手把常见的写法做成可配置的部件，使用者不用写代码 |
| DA-D13 | **SML**：SECS 页需要编辑消息，库只有 `ToSml()`，没有解析器（Secs README 写明"Output only"）。<br>• 助手内写一个 internal 的 `SmlParser`，只解析 `ToSml()` 的输出格式：`L`、`A`、`B`、`BOOLEAN`、`U1`–`U8`、`I1`–`I8`、`F4`、`F8`，不支持 `J` / `C2`（库也不支持）；<br>• 性质测试：对每种格式和嵌套列表，`Parse(item.ToSml()).ToSml() == item.ToSml()`；<br>• 解析错误给出行号与列号 | 不改库（DA-D15）。以后要把解析器移进 Secs 包时，这些测试可以直接带过去 |
| DA-D14 | **模拟设备**（用户决定做）：<br>• 放在独立的类库 `…DebugAssistant.Simulators`，只引用 Core、Channels、Tcp、Udp、Serial 的**公开** API；<br>• **不引用**任何协议包（Modbus、Melsec、Siemens、Omron、Plc）：模拟器是独立实现，才能与客户端交叉验证；<br>• 不加任何 `InternalsVisibleTo`（架构测试只允许 `<库>.Tests`）；<br>• 范围见第 8 节；以黄金向量为准 | 测试项目里已有的替身用了协议包的内部类型（例如 `S7DeviceSimulator` 引用 `Siemens.Protocol`），不能搬进应用 |
| DA-D15 | **本期不改任何库项目的生产代码**（`Junevy.Communication.*` 下的 16 个库项目：Core、Channels、Tcp、Udp、Serial、Plc、Melsec、Siemens、Omron、Modbus、Secs、WebApi、WebApi.Server、Mqtt、OpcUa、Ftp）。允许改的现有代码只有：<br>• 删除 `Junevy.Communication.Test`（DA-D18）；<br>• Task 16 把三个 SDK 测试服务器迁到共享测试项目（只改测试代码）；<br>• `Junevy.Communication.slnx`；<br>• 文档。<br>需要改库时，按第 2 节第 8 条记录并停下 | 本期的目标之二是验证公开 API |
| DA-D16 | **串口测试只能用 COM20 / COM21**（虚拟串口对，经环境变量 `JUNEVY_SERIAL_PAIR` 与共享测试库的 `SerialPairFact`），**绝不能打开 COM3 / COM4**。没有设置环境变量时，串口用例跳过 | 开发机的约束 |
| DA-D17 | **界面语言**：界面用中文。库返回的错误消息原样显示（英文），旁边显示 `ErrorKind` 的中文名称和十六进制的 `ProtocolErrorCode` | 错误消息是库的契约，不翻译 |
| DA-D18 | **删除旧工具**（用户决定）：Task 8 的 Modbus 页覆盖旧工具的全部功能后，删除 `Junevy.Communication.Test` 目录及它在 slnx 中的条目。旧工具的功能是：TCP 或串口连接，读写 C、DI、IR、HR，结果显示在表格中。历史版本留在 git 里 | 用户决定 |
| DA-D19 | **扩展点：新增一种页签的成本固定**：<br>• `ProtocolCatalog` 里加一条 `ProtocolDescriptor`（种类、分组、标题、短名、图标）；<br>• 一个参数 `record`，加一行 `[JsonDerivedType]`；<br>• 一个 `SessionViewModel` 子类，在 DI 中按种类注册为键控服务（`AddKeyedTransient<SessionViewModel, T>(kind)`，.NET 8 的键控服务）；<br>• 一个视图，加一个按类型匹配的 `DataTemplate`。<br>其他地方不得出现针对 `ProtocolKind` 的 `switch`。架构测试（Task 2）检查每个种类都有描述、参数类型、视图模型注册与 `DataTemplate` | 19 种页签以后还会增加（例如 GEM）；集中登记，避免漏改 |

## 1. 结论：能否用 Channel 自定义基于 Socket 的协议

**能。** 通道族本来就是为自定义字节协议设计的（Channels README："Reference it directly when you write a protocol package … or a custom transport"）。各部分的能力与边界如下（依据：Channels、Tcp、Udp、Serial 的 README 与公开类型）：

| 关注点 | 只改配置就能做 | 需要写代码（`ChannelComponents`） | 助手的处理 |
|---|---|---|---|
| 传输 | TCP 客户端（`TcpClientChannelConfig`）、TCP 服务端（`TcpServer`，含会话、白名单、最大会话数）、UDP（定向、非定向、广播、组播）、串口 | 其他字节流传输：派生 `StreamClientChannel`（例如命名管道） | 前四种各一个页签 |
| 分帧（只用于字节流） | `FramingOptions` 的 6 种模式：`Raw`、`Delimiter`、`FixedLength`、`LengthField`（偏移、宽度 1/2/4 或 ASCII 1–8、大小端、ASCII 十六进制或十进制、修正值、剥离字节数）、`StartEnd`、`IdleGap` | 其他帧格式，例如 DLE 转义或按功能码定长：自写 `IFrameCodecFactory` / `IFrameDecoder` | 分帧编辑器覆盖 6 种模式的全部参数 |
| 请求与应答的关联 | `Sequential`（默认） | `Matcher`：需要 `IResponseMatcher`；`Keyed`：需要 `IFrameKeyExtractor` | DA-D12 的可配置匹配器与键提取器 |
| 连接后握手 | — | `IConnectionInitializer` | DA-D12 的 `ScriptedInitializer` |
| 心跳 | `HeartbeatOptions`：`Payload`、`ExpectedReply`、间隔、超时、失败次数、`OnlyWhenIdle` | 协议级探测：`HealthProbeFactory` | 配置即可；串口与 UDP 必须填 `ExpectedReply`（库会校验） |
| 重连 | `ReconnectOptions`：固定间隔或指数退避、上限、次数、首次失败是否重连 | 自定义退避：`ReconnectPolicy` | 配置即可 |
| TLS | 客户端与服务端的 TLS、证书来源（证书存储或 PFX 加环境变量）、双向认证 | 证书校验回调 | 配置即可；证书密码只填环境变量名 |
| 文本收发 | — | — | `SendTextAsync` / `RequestTextAsync` / `ReceiveTextAsync`（库的扩展方法） |
| 校验和 | 通道不计算校验和：除了 `Delimiter` 会追加分隔符，编码器原样发送负载 | 由协议在负载里加上 | DA-D12 的校验辅助 |

不支持的有：DTLS（BCL 没有实现）；UDP 上的分帧（一个数据报就是一帧，设置分帧会被拒绝）；TLS 断开时的 close_notify。

**本期的验证**：Task 5、Task 6 在 TCP 客户端与 TCP 服务端页之间，对 6 种分帧 × 3 种关联方式做端到端测试；Task 4 对每个可配置部件做单元测试。

## 2. 执行规则（每个 Task 都适用）

1. **开工前**读知识库 `Agent协作/开发约定与验收基线.md`，并加载 `junevy-controls` skill；写某个控件的非简单 XAML 之前，先读 Junevy.Controls README 的对应章节（skill 第 8 节的阅读地图）。
2. **每个 Task 一个或多个提交**，提交信息写明 Task 编号，例如 `feat(debug-assistant): Task 3 workspace persistence`。不推送。
3. **先写测试**：纯逻辑（映射、解析、校验、模拟器引擎、工作区）先写失败的测试；视图模型用同步的 `IUiDispatcher` 与假时钟测试；需要窗口的测试在 STA 线程上运行（测试支持里写 `StaRunner`，不引入第三方 STA 包）。
4. **测试通过的标准**：`pwsh build/test-affected.ps1 -Base fe82d79 -NoBuild -Repeat 3` 三遍全绿。每一次失败都如实写进第 33.3 节，不能用重跑来掩盖。
5. **构建**：`dotnet build Junevy.Communication.slnx --no-incremental` 的 Debug 与 Release 都是 0 错误、0 警告。删除旧工具后，基线里那 2 条既有警告也随之消失。
6. **串口**只用 COM20 / COM21（DA-D16）。
7. **公开 API**：助手与模拟器只用公开 API（DA-D14、DA-D15）。用反射或 `InternalsVisibleTo` 访问内部成员，审阅时直接打回。
8. **摩擦与缺口**：发现库的公开 API 不够用时，在第 33.2 节记一条（现象 / 变通或缺口 / 建议）。有变通方法就按变通实现；没有变通、只能改库时停下，汇报并等用户决定。
9. **不确定时提问**（AGENTS.md）：功能不明确、发现更好的实现、或者某项可以扩展时，在汇报里提出问题，不擅自扩大范围。
10. **知识库只写不提交**：写完运行 `git status --short -- Junevy.Communication.Wiki` 与 `git diff --stat -- Junevy.Communication.Wiki`，把结果放进汇报。
11. **每个 Task 结束时**在第 33.3 节写汇报：做了什么、测试数、偏差、需要审阅者决定的事。

## 3. 代码质量标准（审阅者逐条验收，不符合即打回）

- **沿用**：P2 计划第 2 节的 Q1–Q15（文件不超过 300 行、方法不超过 60 行、不重复、依赖方向、可见性最小化、无魔法数、无死代码、`ConfigureAwait`、测试使用黄金向量等），以及 P2.5 计划第 2 节的 Q16–Q20。
  - 例外：UI 线程上的视图模型代码**不加** `ConfigureAwait(false)`（继续执行时要回到 UI 线程）；
  - 模拟器、日志 Sink、工作区存储等非 UI 代码照常加 `ConfigureAwait(false)`。
- **U1 视图不写逻辑**：code-behind 只能有 `InitializeComponent()`。交互用 Behaviors、命令与转换器。例外只有 `App.xaml.cs` 的宿主启动与退出。
- **U2 分层**（架构测试守住，Task 2）：
  - `ViewModels`、`Services`、`Sessions` 命名空间里的类型不能引用 `PresentationFramework`、`PresentationCore`、`WindowsBase`、`Junevy.Controls`、`Microsoft.Xaml.Behaviors` 中的类型；
  - 模拟器程序集不能引用任何 WPF 程序集或协议包；
  - 整个解决方案不能引用 Prism。
- **U3 主题**：
  - 颜色只用 `DynamicResource Theme.Brush.*`。XAML 中出现 `#RRGGBB` 色值的数量必须为 0（Task 21 用 grep 检查）；
  - 主窗口要设置 `Background` 与 `TextElement.Foreground`；
  - 提供浅色 / 深色切换（`ThemeManager.ToggleTheme()`），切换后所有页签都正确显示。
- **U4 控件选型**按 skill 第 2 节：
  - `RadioButton`、`ToggleButton`、`Label`、`TextBlock`（带图标的标题）、`ProgressBar`、`PasswordBox` 用 `jv:` 前缀；
  - 上下文菜单用 `jv:ContextMenu` + `jv:ContextMenuItem`，不能用 `jv:MenuItem`；
  - `jv:TabControl` 的派生样式必须 `BasedOn="{StaticResource DefaultDerivedTabControlStyle}"`；
  - `Border.CornerRadius` 不能逐实例写在属性上（会报 MC3015）。
- **U5 异步**：
  - 不能同步等待异步操作；
  - `async void` 只能出现在事件处理器里（按 U1 实际不该出现）；
  - 每个长时间运行的操作都能取消（`[RelayCommand(IncludeCancelCommand = true)]` 或会话级 `CancellationTokenSource`）。
- **U6 结果显示统一**：
  - `CommResult`、`WebApiResult<T>`、`SecsResult` 统一经 `OperationRecord` 显示：时间、操作、目标、耗时、成败、`ErrorKind`、`ProtocolErrorCode`（十六进制）、消息；
  - 不允许各页面各写一套。
- **U7 输入校验**：数字、十六进制、地址、节点 ID、主题、URL 等都在界面上校验，并给出消息（`ObservableValidator` 或等价方式）。用户输入不能导致未处理的异常。
- **U8 文件大小**：XAML 文件不超过 400 行，超过就拆成 `UserControl`。C# 文件按 Q 规则不超过 300 行。

## 4. 解决方案结构

### 4.1 项目与引用

| 项目 | TFM | 引用 |
|---|---|---|
| `Tools/Junevy.Communication.DebugAssistant` | `net8.0-windows`，WPF，`WinExe` | 包：Junevy.Controls 3.3.0、CommunityToolkit.Mvvm 8.4.2、Microsoft.Extensions.Hosting 10.0.12、Microsoft.Xaml.Behaviors.Wpf 1.1.158。项目引用：Modbus、Melsec、Siemens、Omron、Secs、Mqtt、OpcUa、WebApi、WebApi.Server、Ftp、Tcp、Udp、Serial，以及 Simulators（Core、Channels、Plc 经传递引入） |
| `Tools/Junevy.Communication.DebugAssistant.Simulators` | `net8.0` | 项目引用：Core、Channels、Tcp、Udp、Serial；包：Microsoft.Extensions.Logging.Abstractions（经 Core 传递，不重复引用） |
| `Tests/Junevy.Communication.DebugAssistant.Tests` | `net8.0-windows`，`UseWPF` | Import `build/Junevy.Communication.Tests.props` 之后，把 `TargetFrameworks` 改为 `net8.0-windows`，并把 `Microsoft.Extensions.DependencyInjection` 升到 10.0.12（`Update`，与 OpcUa.Tests 相同）；项目引用：应用、Simulators、Testing、Testing.Servers（Task 16） |

`WebApi.Server` 依赖 ASP.NET Core 共享框架，因此运行助手的机器需要安装 **ASP.NET Core Runtime 8**（开发机已有 8.0.29 / 8.0.31）。Tools README 写明这一点。

### 4.2 应用的目录结构（命名空间与目录一致）

```text
Tools/Junevy.Communication.DebugAssistant/
  App.xaml(.cs)                  宿主启动与退出（DA-D9）、注册 GBK 编码提供程序
  Hosting/                       ServiceRegistration（DI 注册，按模块分文件）
  Shell/                         MainWindow、MainWindowViewModel、ProtocolCatalog、ProtocolDescriptor、ProtocolKind
  Sessions/                      SessionViewModel 基类、SessionFactory（键控解析）、OperationRecord、SessionState
  Logging/                       SessionLogSink、SessionLog（环形缓冲与批量刷新）、LogEntry、FrameEntry
  Workspace/                     WorkspaceStore、WorkspaceDocument、SessionSettings（多态根）
  Services/                      IUiDispatcher、IFileDialogService、IConfirmService、INotificationService 及其 WPF 实现（WPF 实现放 Views/Platform）
  Common/                        ByteInput、ChecksumKind、ValueParsers、EncodingChoice
  Protocols/
    Channels/                    通道设置编辑器的视图模型、DA-D12 部件、TcpClient/TcpServer/Udp/Serial 页
    Plc/                         PLC 公共面板（地址、类型化读写、监控表、数据格式）
    Modbus/ Melsec/ Siemens/ Omron/
    Secs/                        SECS 页、SmlParser、应答规则
    Mqtt/ OpcUa/ WebApi/ Ftp/
    Simulators/                  模拟设备页（视图模型），引擎在 Simulators 项目
  Views/                         全部 XAML：Shell、各协议的 UserControl、DataTemplates.xaml、Behaviors、Converters、Platform
```

### 4.3 页签种类（`ProtocolKind`，共 19 种；左侧树按此分组）

| 分组 | 种类（树上的标题 → 默认短名） |
|---|---|
| PLC | Modbus（TCP / RTU / UDP）→ `Modbus`；三菱 MC（MELSEC）→ `MC`；西门子 S7 → `S7`；欧姆龙 FINS → `FINS` |
| 工厂系统 | SECS/HSMS 设备端 → `SECS`；MQTT 客户端 → `MQTT`；OPC UA 客户端 → `OPC UA`；WebApi REST 客户端 → `REST`；WebApi SOAP 客户端 → `SOAP`；WebApi 服务端 → `HTTP 服务端`；FTP / FTPS 客户端 → `FTP` |
| 自定义协议（通道） | TCP 客户端 → `TCP 客户端`；TCP 服务端 → `TCP 服务端`；UDP → `UDP`；串口 → `串口` |
| 模拟设备 | Modbus 从站 → `Modbus 从站`；MC 从站 → `MC 从站`；S7 CPU → `S7 CPU`；FINS 从站 → `FINS 从站` |

## 5. 应用架构

### 5.1 分层与依赖方向

```text
Views（XAML、Behaviors、Converters、Platform 实现） ──绑定──▶ ViewModels（Shell、Sessions、Protocols/*）
                                                            │
                                                            ▼
                                Services 抽象（IUiDispatcher、IFileDialogService …） + Workspace + Logging
                                                            │
                                                            ▼
                                  Junevy.Communication.* 公开 API  +  DebugAssistant.Simulators
```

- 视图模型只依赖服务抽象与库，不依赖视图（U2）。
- WPF 实现（`WpfUiDispatcher`、`WpfFileDialogService`，以及基于 `ConfirmDialogService` 的确认服务、基于 `MessageBarService` 的通知服务）放在 `Views/Platform`，在 DI 中注册为抽象的实现。

### 5.2 会话模型

`SessionViewModel`（抽象，`ObservableObject`，`IAsyncDisposable`）：

| 成员 | 说明 |
|---|---|
| `Kind`、`Title`（可改名，DA-D8） | `Title` 绑定页签的 `Header` |
| `State`（`SessionState`：未连接 / 连接中 / 已连接 / 重连中 / 运行中 / 已停止 / 故障）、`StateText` | 由各协议的状态事件映射而来，统一显示 |
| `Log`（`SessionLog`） | 报文与日志（第 5.3 节） |
| `Operations`（`ObservableCollection<OperationRecord>`，有上限） | U6 |
| `ConnectCommand` / `DisconnectCommand`（服务端与模拟器为启动 / 停止） | 异步，可取消 |
| `IsEditable` | 未连接时为 true，控制参数区是否可编辑（DA-D9） |
| `CaptureSettings()` / `ApplySettings(SessionSettings)` | 工作区（第 5.4 节） |
| `DisposeAsync()` | 关闭页签与退出程序时调用 |

- 子类只实现三件事：参数与库配置之间的映射、连接与断开、协议特有的命令。
- `SessionFactory` 按 `ProtocolKind` 从 DI 解析键控的 `SessionViewModel`，并为它创建页签私有的 `ILoggerFactory`（DA-D5）。

### 5.3 日志与报文

- `SessionLogSink : ILoggerProvider` 按第 DA-D5 条把日志分成两类条目：
  - `FrameEntry`：方向、长度、十六进制、远端、是否截断；
  - `LogEntry`：级别、类别短名、消息、异常摘要。
- `SessionLog` 的能力：
  - 有界环形缓冲；
  - 100 ms 批量刷新；
  - 筛选：只看报文、只看日志、最低级别；
  - 显示：十六进制或 ASCII，可显示时间戳；
  - 操作：暂停、清空、复制选中行、导出为 `.txt`；
  - 统计：发送和接收的帧数、字节数，以及最后一次收发的时间。
- 有 `ConnectionStatistics` 的会话（通道、PLC 客户端、TCP 会话）每秒刷新一次统计：字节数、帧数、丢弃帧数、协议错误数、重连次数、连续心跳失败次数、建立连接的时间。

### 5.4 工作区

- `WorkspaceStore`：读写文件（DA-D11）；写入时先写临时文件再替换。
- `SessionSettings` 是各协议参数 `record` 的多态基类型。
- `MainWindowViewModel` 的行为：
  - 启动时加载工作区，逐个创建会话并恢复参数与标题；
  - 退出时调用 `CaptureSettings()` 收集参数。

### 5.5 外壳的交互

- `ProtocolCatalog` 生成树。节点类型为 `ProtocolTreeItem : TreeMenuItem`（`TreeMenuItem` 是未密封的公开 POCO，没有 `Tag` 一类的附加数据），新增只读属性 `Descriptor`：分组节点为 null，叶节点为对应的 `ProtocolDescriptor`。
- 树的 `ItemDoubleClick` 经 Behaviors 的 `InvokeCommandAction`（`EventArgsParameterPath="Item"`）调用 `OpenSessionCommand(TreeMenuItem)`：
  - 叶节点：创建会话，加入 `Sessions`，选中它；
  - 分组节点：不做任何事，展开与收起由控件处理。
- 没有页签时，右侧显示空状态提示："双击左侧协议，打开一个调试页"。
- 工具栏：
  - 保存工作区；
  - 导入页签、导出当前页签；
  - 切换主题；
  - 关闭全部页签（需确认）。

## 6. 界面布局

```text
┌──────────────────────────────────────────────────────────────────────────────┐
│ 工具栏：保存工作区 │ 导入 │ 导出 │ 主题 │ 关闭全部                                 │
├───────────────┬──────────────────────────────────────────────────────────────┤
│ jv:TreeView   │ jv:TabControl（页签：标题双击 / F2 改名，× 关闭）               │
│ ▸ PLC         │ ┌──────────────────────────────────────────────────────────┐ │
│   Modbus      │ │ 连接区（jv:GroupBox，可折叠）：传输 / 帧格式 / 参数 … [连接]  │ │
│   MC …        │ │ 状态：jv:Label（DisplayMode 随状态）＋ 统计                 │ │
│ ▸ 工厂系统     │ ├──────────────────────────────┬───────────────────────────┤ │
│ ▸ 自定义协议   │ │ 协议操作区（各协议不同）        │ 操作记录（OperationRecord） │ │
│ ▸ 模拟设备     │ ├──────────────────────────────┴───────────────────────────┤ │
│               │ │ 报文与日志面板（GridSplitter 可调高度；筛选、HEX/ASCII、导出） │ │
│               │ └──────────────────────────────────────────────────────────┘ │
├───────────────┴──────────────────────────────────────────────────────────────┤
│ jv:MessageBar（全局通知：工作区加载失败、未处理的错误）                            │
└──────────────────────────────────────────────────────────────────────────────┘
```

- 每个页签内的布局一致：连接区、状态行、协议操作区、操作记录、报文与日志面板。
- 连接区里的通道参数（超时、心跳、重连、分帧、TLS 等）共用同一组编辑器（Task 4），PLC 页与自定义协议页复用它们。
- **视图不保存状态**：`TabControl` 切换页签时会重建内容视图，所以所有状态都放在视图模型里，包括日志、表格内容与输入框文本。

## 7. 功能矩阵：每种页签展示的特有功能

审阅者按这张表逐项验收。"依据"指对应包的 README 小节，或 Skill 中的同名内容。表中出现的每个选项都必须能在界面上设置，并能在报文、日志或操作记录里观察到效果。

### 7.1 PLC 公共面板（Modbus、MC、S7、FINS 共用，Task 7）

| 功能 | 界面 | 对应 API / 依据 |
|---|---|---|
| 地址实时校验 | 输入时调用 `ParseAddress`，不发报文；显示存储区、单位、进制以及协议附加信息（例如 Modbus 的站号），错误时显示解析器的消息 | `IPlcClient.ParseAddress`（Plc README "Parsing an Address"） |
| 类型化读写 | 数据类型：Int16、UInt16、Int32、UInt32、Int64、UInt64、Float、Double、String（字节长度；写入时可指定定长）、Bool、Bits（数量）、Byte、Bytes（字节数）、原始字（`ReadAsync` / `WriteAsync`）；可读多个，写入时值用逗号分隔 | Plc README "Typed Reads and Writes"、"Byte Access"、"Strings" |
| 字序与字符串编码 | `WordOrder` 四选一，编码选 ASCII、UTF-8 或 GBK；默认值随协议不同（MC 为 DCBA，S7 与 Modbus 为 ABCD，FINS 为 CDAB），界面标出协议的默认值 | `PlcDataFormat` |
| 监控表 | 多行，每行包括地址、类型、数量或长度、当前值、新值（写入）、状态、更新时间；可以周期轮询（间隔不小于 50 ms），上一轮未完成时跳过本轮，断开时停止 | 类型化 API；DA-D10 |
| 套件选项 | `RetryCount`、`RetryInterval`、`AllowReadModifyWriteBits` | `PlcClientOptions` |
| 拆包可见 | 超出单包上限的读写，在报文面板里显示为多帧 | 各协议 README 的"Limits per Request" |
| 错误分类 | 在操作记录里显示 `InvalidRequest`、`NotConnected`、`RemoteError`（附 `ProtocolErrorCode`）、`Timeout`、`ConnectionClosed`、`ProtocolViolation`、`NotSupported`、`Cancelled` | Plc README "Results, Not Exceptions" |
| 通道参数 | 超时、`ResetOnRequestTimeout`、心跳、重连（Task 4 的编辑器）；协议拥有的分帧与关联方式不显示（协议会拒绝修改） | 各协议 README 的"Heartbeat and Reconnect"、"Transports" |

### 7.2 Modbus（Task 8）

| 功能 | 界面 | 依据 |
|---|---|---|
| 传输 × 帧格式 | 传输选 TCP、UDP 或串口，帧格式选自动、MBAP 或 RTU，界面显示组合的名称：Modbus TCP、RTU over TCP、Modbus UDP、RTU over UDP、Modbus RTU。"串口 + MBAP"不可选，并提示原因 | Modbus README "Quick start"、Options `Framing` |
| 默认站号与站号前缀 | `UnitId`；地址可写成 `s=2;HR100`；"临时站号"输入框会把前缀拼进地址。RTU 时检查 1–247 | README "Addresses"、"One connection, several stations" |
| 地址写法 | 符号写法 `C5`、`DI7`、`IR3`、`HR100`（零基），Modicon 写法 `40001`、`400001`（一基），寄存器位 `HR100.5`；校验面板显示换算后的协议地址 | README "Addresses" |
| 功能码视图 | 01、02、03、04 读；05 / 0F 写线圈，06 / 10 写寄存器（按 `UseSingleWriteFunctions` 与点数自动选择，界面提示将使用的功能码，报文面板可以核对）；**16 掩码写**（AND / OR 掩码输入，外加"置位 / 清零第 n 位"助手，自动算出两个掩码）；**17 读写合一**（读地址与数量、写地址与数据） | README "Function codes"、"Mask write and read-write multiple" |
| Modbus 选项 | `UseSingleWriteFunctions`、`UseMaskWriteForBits`、`AllowReadModifyWriteBits`，界面说明三者的关系与优先级 | README Options |
| 只读区 | 对 IR、DI 写入时返回 `InvalidRequest`，不发报文（报文面板为空，以此为证） | README 表格最后一行 |
| 异常码 | 显示 `RemoteError`，`ProtocolErrorCode` 为 01–0B，消息带名称 | README "Error handling" |
| RTU over TCP 的超时重建 | 显示 `ResetOnRequestTimeout` 与 `RetryCount` 的说明；超时后状态变为重连中 | README "Heartbeat and reconnect" |
| 旧工具的功能 | TCP 或串口连接，读写 C、DI、IR、HR，结果显示在表格中（DA-D18） | 旧 `MainWindowViewModel` |

### 7.3 三菱 MC（Task 9）

| 功能 | 界面 | 依据 |
|---|---|---|
| **帧类型 × 编码 × 传输** | 帧类型选 3E、4E 或 4C，编码选二进制或 ASCII。TCP / UDP 只能选 3E、4E；串口只能选 4C，且 ASCII 为格式 4、二进制为格式 5。不合法的组合不可选，并说明原因 | Melsec README "Scope"、"Transports" |
| 和校验 | `SumCheck`，只在串口时可编辑 | Options |
| 访问路径 | `NetworkNumber`、`PcNumber`、`ModuleIoNumber`（十六进制输入）、`ModuleStationNumber`、`StationNumber`、`SelfStationNumber`；4C 的字段只在串口时显示 | Options |
| 监视定时器 | `MonitoringTimer`（单位 250 ms），旁边显示折算的毫秒数；大于或等于请求超时时给出警告 | Options |
| 软元件速查 | 列出 28 种软元件的符号、单位、进制；十六进制软元件（X、Y、B、W、SB、SW、DX、DY、ZR）高亮；点击即填入地址。表由助手维护，测试逐项用 `ParseAddress` 核对单位与进制（MC 的软元件表是 internal，见摩擦 F-3） | README "Devices" |
| 地址规则 | 位号为十六进制 `D100.F`；二进制帧上限 `0xFFFFFF`，ASCII 帧的十进制软元件上限 `999999`，超出时由解析器报错 | README "Addresses" |
| 字设备写位 | 默认返回 `NotSupported`；勾选 `AllowReadModifyWriteBits` 后可以写，日志里出现警告 | README "Addresses" |
| 环回心跳 | 心跳使用 0619 环回测试，固定数据 `1234ABCD`；开启心跳后可以在报文面板看到。环回没有单独的公开入口（摩擦 F-2） | README "Heartbeat and Reconnect" |
| 结束码 | 显示 `MC end code 0xC051.` 与 `ProtocolErrorCode` | README "Errors" |
| UDP 下的 4E 序列号 | 4E over UDP 的应答按序列号匹配，迟到的应答被丢弃（报文面板与日志可见） | README "Transports" |

### 7.4 西门子 S7（Task 10）

| 功能 | 界面 | 依据 |
|---|---|---|
| 连接参数 | `Rack`（0–7）、`Slot`（0–31）、`ConnectionType`（PG / OP / Basic）；显示算出的远端 TSAP，公式为 `(ConnectionType << 8) \| (Rack << 5) \| Slot`，本地 TSAP 为 `0x0100` | Siemens README "Options" |
| 协商的 PDU | 连接后显示 `NegotiatedPduSize`，并显示据此算出的单包上限：读 PDU−18、写 PDU−28 字节 | README "Limits per Request" |
| 地址写法 | `DB1.DBX12.3`、`DBB`、`DBW`、`DBD`，`M` / `I` / `Q` 及其 `B` / `W` / `D` 形式，可带 `%` 前缀；校验面板显示字节地址，并提示 `MX0.0` 一类写法会被拒绝 | README "Addresses" |
| **S7 字符串** | `String`（maxLength 1–254）与 `WString`（1–16382）的读写面板，显示头部的声明长度与实际长度 | `ReadS7StringAsync` / `WriteS7StringAsync` / `ReadS7WStringAsync` / `WriteS7WStringAsync` |
| 按字节精确访问 | `ReadBytesAsync("MB0", n)` 正好读 n 个字节 | README "Quick Start" |
| PLC 端检查清单 | 帮助区列出：允许 PUT/GET、取消优化块访问、开放 102 端口；`0x8104` 等错误码会附带提示 | README "PLC-side settings"、"Errors" |
| 错误分类 | 头部错误（类 / 码）与数据项返回码分别显示 | README "Errors" |

### 7.5 欧姆龙 FINS（Task 10）

| 功能 | 界面 | 依据 |
|---|---|---|
| 节点参数 | `ClientNode`（0 表示由 CPU 分配，1–254 为固定值，255 被拒绝）、`DestinationNetwork`、`DestinationUnit` | Omron README "Options" |
| 握手结果 | 连接后显示 `ClientNode` / `ServerNode`；请求固定节点时，如果 CPU 给出不同的节点，界面标出差异 | README "Options" 最后一段 |
| 区域与地址 | `CIO`、`W`、`H`、`A`、`D`、`E0_`–`EC_`，地址与位号均为十进制（`W10.03`）；速查表同 MC 的做法（FINS 的区域表是 internal，见 F-3） | README "Addresses" |
| 原生位写 | `WriteBitsAsync("W10.15", …)` 跨字写入下一字（报文为位区代码） | README "Addresses" |
| 字节语义差异 | 显示提示：`ReadByteAsync` 返回字的高字节（与 MC 相反） | README "Data Format and Strings" |
| 结束码 | 显示屏蔽后的结束码；状态位被屏蔽后为 0 时视为成功，日志里有调试行 | README "Errors" |

### 7.6 SECS/HSMS 设备端（Task 15）

| 功能 | 界面 | 依据 |
|---|---|---|
| 连接模式 | `Passive`（监听）/ `Active`（主动连接主机）、`Host`、`Port`、`DeviceId` | Secs README "Options" |
| 计时器 | T3、T5、T6、T7、T8、`LinktestInterval`、`MaxMessageLength`，每个都附说明 | README "Options" |
| 会话状态 | 未连接 / 未选择 / 已选择，以及 `StateChanged` 的原因 | README "State and Events" |
| **自动应答规则** | 表格：S、F（奇数）、应答方式（SML 应答、返回 null 即中止 `SxF0`、不登记），列表显示收到的主消息 SML；没有登记的 S/F 由库回 S9F3 / S9F5（从日志可以看到） | README "Handlers"、"S9 System Messages" |
| 发送主消息 | SML 编辑器（`SmlParser`，DA-D13），W 位；结果显示应答的 SML，或 `SecsError`（`Aborted`、`Rejected` 及原因码、`ReplyTimeout`、`InvalidReply`、`NotSelected`、`ConnectionLost`） | README "Sending" |
| 常用模板 | S1F1、S1F13、S5F1、S6F11、S10F1 的 SML 模板，一键填入 | 常用的设备端主消息 |
| SML 显示 | 所有消息都用 `ToSml()` 显示 | README "SML Text" |
| 局限说明 | 没有处理器的主消息只能从日志看到头部（S、F、W、系统字节），看不到正文（摩擦 F-4） | README "Logging" |

### 7.7 MQTT（Task 17）

| 功能 | 界面 | 依据 |
|---|---|---|
| 连接选项 | `Host`、`Port`、`ClientId`、`UserName`、`Password`（不保存）、协议版本 V3.1.1 / V5.0、`CleanSession`、`SessionExpiryInterval`（仅 V5）、`KeepAliveSeconds`、`Timeout` | Mqtt README "Options" |
| TLS | `UseTls`、`AllowUntrustedCertificates`（开启时显示警告） | README "TLS" |
| 自动重连 | `AutoReconnect`、`ReconnectInterval`、`MaxReconnectInterval`；状态显示重连中，以及恢复了多少个订阅 | README "Reconnect and subscription restore" |
| 遗嘱 | `WillTopic`、`WillPayload`、`WillQos`、`WillRetain` | README "Last will and testament" |
| 订阅 | 列表，可添加或取消订阅；支持通配符；显示 Broker 授予的 QoS | README "Publishing and subscribing" |
| 发布 | 主题，负载（文本或十六进制），QoS 0 / 1 / 2，`Retain`；**MQTT 5 属性**：`ContentType`、`ResponseTopic`、`CorrelationData`（十六进制）、`MessageExpiryInterval`、`UserProperties` 列表。在 V3.1.1 上发布带属性的消息时返回 `InvalidRequest`，界面演示这一点 | README "MQTT 5 properties" |
| 接收 | 消息列表：主题、QoS、Retain、负载（文本或十六进制）、MQTT 5 属性 | README "Receiving" |
| 错误 | `AuthenticationFailed`、`ResourceExhausted` 等，附原因码 | README "Error classification" |

### 7.8 OPC UA（Task 17）

| 功能 | 界面 | 依据 |
|---|---|---|
| 连接选项 | `EndpointUrl`、`ApplicationName`、`ApplicationUri`、`PkiRoot`、`ConnectTimeout`、`OperationTimeout`、`SessionTimeout`、`KeepAliveInterval`、重连间隔；醒目提示只支持 None 安全策略与匿名登录 | OpcUa README 开头、"Options" |
| 浏览 | 从 `i=85` 开始逐层展开（懒加载），显示 NodeId（`ns=` 形式）、浏览名、节点类别；双击变量加入读写列表 | README "Browsing" |
| 批量读 | 每一项都有自己的值、类型、`OpcUaStatusCode`（好 / 不确定 / 坏，以及符号名）、源时间戳与服务器时间戳、不支持的类型名 | README "Batch semantics"、"Value types" |
| 写入（不做转换） | 显式选择 .NET 类型（表中的 15 种，包括一维数组与 `byte[]` 即 ByteString）；类型不匹配时显示服务器返回的 `BadTypeMismatch` | README "Value types" |
| 订阅 | `PublishingInterval`、`SamplingInterval`、`QueueSize`、`DiscardOldest`；实时通知列表；显示每一项的订阅状态；可以释放订阅 | README "Subscriptions" |
| 节点 ID 提示 | 推荐 `nsu=` 写法，`ns=` 写法提示服务器重启后可能变化 | README "Node ids" |

### 7.9 WebApi REST 客户端（Task 18）

| 功能 | 界面 | 依据 |
|---|---|---|
| 请求构造 | 方法、相对路径（拒绝绝对地址时演示 `InvalidRequest`）、查询参数表、头表、正文（JSON 编辑器 `jv:CodeEditor SyntaxLanguage=Json`，或文本、十六进制）、`ContentType` | WebApi README "Paths, queries and bodies" |
| 业务信封 | `Envelope`：`Enabled`、`CodePath`、`MessagePath`、`DataPath`、`SuccessCodes`；显示 `RemoteCode` | README "Response data" |
| 重试与幂等 | `RetryCount`、`RetryDelay`、`RetryUnsafeMethods`；每次调用可填 `IdempotencyKey`；时间线显示每次尝试与 `WillRetry` / `RetryDelay` | README "Retries and idempotency" |
| 事件时间线 | `RequestSending` → `ResponseReceived` → `RequestCompleted`，含次数与耗时 | README "Event sequence" |
| 可用性 | `HealthCheckPath`、`HealthCheckInterval`、`OfflineThreshold`；显示 Unknown / Online / Offline 与变化事件 | README "Availability and health checks" |
| 认证 | Basic、Bearer、ApiKey，只填环境变量名 | README "Credentials" |
| 日志脱敏 | `RedactedHeaders`、`RedactedQueryParameters`、`LogBodies`；在日志面板可以看到 `***` | README "Logging and redaction" |
| 结果 | 状态码、`ErrorKind`、`RemoteCode`、正文文本 | README "Results and error kinds" |

### 7.10 WebApi SOAP 客户端（Task 18）

| 功能 | 界面 | 依据 |
|---|---|---|
| 连接选项 | `Url`、版本 1.1 / 1.2、`DefaultNamespace`、`HealthCheckUrl` | WebApi README "Quick start: SOAP" |
| 原始调用 | SOAPAction 加正文 XML（`jv:CodeEditor SyntaxLanguage=Xml`），调用 `InvokeRawAsync`，显示返回的 XML | 同上 |
| Fault | 显示 `RemoteError`，`RemoteCode` 为 fault code；Fault 不重试 | 同上 |
| 共有能力 | 重试、可用性、事件、脱敏（与 REST 共用编辑器） | 同上 |

### 7.11 WebApi 服务端（Task 18）

| 功能 | 界面 | 依据 |
|---|---|---|
| 监听 | `Urls`（可以写 `http://127.0.0.1:0` 让系统选端口），启动后显示实际绑定的地址；状态为 Stopped / Starting / Running / Stopping | WebApi.Server README "Quick start"、"Lifecycle" |
| 路由表 | 方法（GET、POST、PUT、DELETE）、模板（含约束）、响应方式（固定 JSON、回显请求正文、业务失败 code 与 msg、指定状态码、延迟毫秒数，用于演示 504）；**运行中不能修改路由**（库要求启动前注册） | README "Routes"、"Responses" |
| 信封 | `Envelope` 各路径、`BusinessFailureStatusCode` | README "Responses" |
| 限制 | `MaxConcurrentRequests`、`RequestTimeout`、`MaxRequestBodySize` | README "Concurrency and timeouts" |
| 认证 | Basic、Bearer、ApiKey（只填环境变量名）；监听所有地址又不开认证时，日志里有警告 | README "Authentication" |
| 幂等去重 | `Deduplication`（窗口）；重复请求返回 `Idempotent-Replayed`，正在执行时返回 409，正文不同时返回 422 | README "Idempotent replay" |
| HTTPS | 证书路径加密码环境变量名 | README "HTTPS" |
| 请求日志 | `RequestReceived` / `RequestCompleted`：`Outcome`、状态码、路由模板、耗时 | README "Interceptors and events"、"Status codes" |

### 7.12 FTP / FTPS（Task 19）

| 功能 | 界面 | 依据 |
|---|---|---|
| 连接选项 | `Host`、`Port`、`UserName`、`Password`（不保存）、`Encryption`（None / Explicit / Implicit；选 Implicit 时提示端口通常为 990，不会自动修改）、`AllowUntrustedCertificates`、`EncodingName`（例如 GBK）、三个超时 | Ftp README "Options"、"FTPS"、"Encoding" |
| 远程浏览 | 目录列表：类型、大小、修改时间（注明不做时区转换）；可进入子目录、返回上级、刷新 | README "Directory semantics"、"Times and time zones" |
| 传输 | 上传、下载文件，进度用 `jv:ProgressBar` 显示（`Percent` 为空时显示不定进度）；`FtpExistsMode` 选 Overwrite 或 Skip，显示 `Skipped` 状态；说明下载是原子的、上传不是 | README "Uploads and downloads" |
| 文件与目录操作 | 新建目录（逐级创建）、删除文件、**递归删除目录（需确认）**、重命名、文件或目录是否存在、文件大小 | README "Directory semantics" |
| 连接模型 | 按需连接；显示 `IsConnected`；说明同一客户端的操作排队执行 | README "Connection model" |

### 7.13 自定义协议：TCP 客户端、UDP、串口（Task 5）与 TCP 服务端（Task 6）

| 功能 | 界面 | 依据 |
|---|---|---|
| 分帧编辑器 | 6 种模式，每种的参数按模式显示：分隔符可以有多个，`KeepDelimiter`、`AppendDelimiterOnSend`；定长；长度字段（偏移、宽度、编码、修正值、剥离字节数）；起止标记与 `KeepMarkers`；空闲间隔；`MaxFrameLength`。编辑器里实时预览：输入一串十六进制，显示能切出哪些帧（用库的 `FrameCodecFactory` 解码，不连接也能看） | Channels README "Framing" |
| 关联 | Sequential / Matcher / Keyed，后两者使用 DA-D12 的可配置部件 | 第 1 节 |
| 握手 | `ScriptedInitializer`：每次连接后发送一段字节，可选期待的应答 | 第 1 节 |
| 心跳与重连 | `HeartbeatOptions` 与 `ReconnectOptions` 的全部字段 | Tcp README "Reconnect and Heartbeat" |
| 超时 | 连接、握手、发送、请求、`LateReplyWindow`、`IdleTimeout`、`PartialFrameTimeout`、`DisconnectTimeout`、`ResetOnRequestTimeout` | Tcp README "Timeouts" |
| 套接字选项（TCP） | `NoDelay`、缓冲区大小、`LingerTime`、KeepAlive（启用、首次探测时间、间隔、次数） | Tcp README "Socket Options" |
| TLS（TCP） | 客户端：`TargetHost`、协议版本、吊销检查、客户端证书（证书存储或 PFX）；连接后显示 `IsTlsActive`。服务端：服务器证书、`ClientCertificateRequired` | Tcp README "TLS" |
| 发送区 | 文本或十六进制，编码，校验辅助（DA-D12）；三种操作：发送（`SendAsync`）、请求并等待应答（`RequestAsync`，可设超时）、等待一帧（`ReceiveAsync`）；定时循环发送（间隔、次数）；发送历史 | Channels README、"Text Protocols" |
| 主动帧 | 显示 `FrameReceived` 收到的帧 | `IByteChannel.FrameReceived` |
| 统计 | `ConnectionStatistics` 的全部字段 | 第 5.3 节 |
| **UDP 专有** | 定向（远端地址与端口）或非定向（只绑定本地端口）；`EnableBroadcast`、`MulticastGroups`、TTL、`MulticastLoopback`、`ReuseAddress`、`MaxDatagramSize`、`RequestRetryCount`；`SendToAsync` / `RequestToAsync` 可指定目标；接收列表显示来源地址；非定向模式下不能用内置心跳（界面说明原因） | Udp README |
| **串口专有** | 端口列表（可刷新；不预选端口，也不自动打开）、波特率、数据位、校验、停止位、流控、DTR、RTS、读写缓冲、`OpenTimeout`；默认分帧为 IdleGap，并提示 USB 转串口的延迟；启用心跳时必须填 `ExpectedReply` | Serial README |
| **TCP 服务端专有** | 监听地址与端口、`Backlog`、`MaxSessions`、`AllowedRemoteAddresses`、会话的握手与空闲超时、`StopTimeout`；会话列表（Id、远端、连接时间、TLS、统计）；可向选中的会话发送、广播、关闭某个会话；自动应答规则（回显，或"收到匹配的内容时回复指定内容"）；停止后可以再次启动 | Tcp README "TCP Server"、"Sessions and handshake" |

### 7.14 模拟设备（Task 11–14，详见第 8 节）

| 功能 | 界面 |
|---|---|
| 宿主 | Modbus：TCP（MBAP 或 RTU over TCP）、UDP（MBAP 或 RTU）、串口 RTU。MC：TCP / UDP 上的 3E、4E（二进制或 ASCII），串口上的 4C 格式 4 / 格式 5（可开和校验）。S7：ISO-on-TCP。FINS：FINS/TCP |
| 设备参数 | Modbus：应答的站号列表。MC：在请求日志中显示收到的访问路径（模拟器不校验路径）。S7：协商的 PDU 长度（240 / 480 / 960）、PUT/GET 开关。FINS：服务器节点号、分配给客户端的节点号 |
| 内存 | 按区显示的表格（虚拟化并分页）：字可以按 UInt16、Int16、十六进制、二进制显示和编辑，位按每行 16 个显示；客户端写入后表格实时更新；"自增寄存器"开关，用来模拟变化的数据 |
| 故障注入 | 应答延迟（毫秒）、不应答、强制错误码（Modbus 异常码、MC 结束码、S7 错误类与错误码、FINS 结束码）、破坏校验（RTU 的 CRC、4C 的和校验）、收到下一个请求后断开连接 |
| 请求日志 | 每个请求都有解码后的摘要（功能码或命令、地址、数量），以及报文面板里的十六进制 |

## 8. 模拟设备

### 8.1 结构（`…DebugAssistant.Simulators`，全部 `internal` + 少量 `public` 供应用使用）

```text
Memory/      SimulatorMemory、WordArea、BitArea、ByteArea（加锁；变化事件按区汇总，供界面节流刷新）
Faults/      FaultPlan（延迟、不应答、强制错误、破坏校验、断开）、FaultInjector
Hosting/     SimulatorHost：把 ISimulatorEngine 挂到 TcpServer、非定向 UdpChannel 或 SerialChannel 上；
             每个 TCP 会话的状态存在 ITcpSession.Items 里（S7 的 COTP / Setup 进度，FINS 的节点分配）
Modbus/      ModbusSlaveEngine（PDU：01–06、0F、10、16、17；异常 01、02、03）、MbapRequestFraming、RtuRequestDecoder、Crc16Modbus
Melsec/      McSlaveEngine（0401 / 1401 的子命令 0000 / 0001，0619）、3E / 4E 二进制与 ASCII 的请求分帧、
             Mc4CFormat4Codec（ENQ … CR LF）、Mc4CFormat5Codec（DLE STX … DLE ETX，DLE 转义，和校验）
Siemens/     S7CpuEngine（COTP CR → CC、Setup Communication、Read Var / Write Var 单项，区 0x81–0x84）、TPKT 分帧
Omron/       FinsSlaveEngine（节点地址请求与响应、FINS 帧，命令 0101 / 0102，字区与位区代码，EM 组）、FINS/TCP 分帧
```

```csharp
// 引擎是纯函数：一个请求帧进来，得到"应答 / 不应答 / 断开"；不接触套接字，便于用黄金向量测试。
internal interface ISimulatorEngine
{
    IFrameCodecFactory? CreateRequestCodec(SimulatorTransport transport);   // 数据报返回 null
    SimulatorReply Handle(ReadOnlySpan<byte> request, SimulatorConnection connection);
}
```

### 8.2 分帧（只用公开的 `FramingOptions`，表达不了的自写 `IFrameDecoder`）

| 协议 / 帧 | 请求分帧 |
|---|---|
| Modbus MBAP | `LengthField`：偏移 4、宽度 2、大端 |
| Modbus RTU（串口、TCP） | 自写 `RtuRequestDecoder`：01–06 为 8 字节；0F / 10 由偏移 6 的字节数算出；16 为 10 字节；17 由偏移 10 的字节数算出；CRC 不符时丢弃 1 个字节，重新同步 |
| MC 3E / 4E 二进制 | `LengthField`：偏移 7 / 11、宽度 2、`BinaryLittleEndian` |
| MC 3E / 4E ASCII | `LengthField`：偏移 14 / 22、宽度 4、`AsciiHex` |
| MC 4C 格式 4 | `StartEnd`：起始 `\x05`，结束 `\r\n` |
| MC 4C 格式 5 | 自写解码器：处理 DLE 转义，可开和校验 |
| S7 TPKT | `LengthField`：偏移 2、宽度 2、大端、修正值 −4 |
| FINS/TCP | `LengthField`：偏移 4、宽度 4、大端 |
| 所有 UDP | 不设分帧（一个数据报就是一帧） |

### 8.3 验证

- **黄金向量（字节完全一致）**：
  - MC：P2 计划第 18 节 V1–V10（V5、V10 的错误应答经故障注入产生；V6 为环回）；
  - S7、FINS：P4 计划第 17.1、17.2 节，包括握手；
  - Modbus：P7 计划第 21.1–21.3 节。
  - 先按向量设置内存，再把请求交给引擎，应答必须与向量逐字节相同。
- **端到端矩阵（Task 14）**：用本仓库的客户端连接模拟器，覆盖第 7.14 节"宿主"一行的每种组合，对每种组合做读写往返、写位、拆包（超过单包上限）、故障注入后的错误分类（`RemoteError` 加 `ProtocolErrorCode`、超时、断开后重连）。串口组合用 COM20 / COM21（DA-D16）。
- **两边结论不一致时**：以黄金向量为准。如果发现客户端有缺陷，**不改库**，记入第 33.2 节，并立即汇报。

## 9. 度量（Task 0 记录基线，Task 21 复测）

- 全仓库：用 `build/metrics` 下的 `dupscan.py`、`methodlen.py`、`effective_lines.py` 记录基线。生产代码与测试代码分开统计，统计范围见 `build/metrics/README`。
- 新项目单独统计：有效行数、超过 60 行的方法数（目标 0）、重复窗口数（目标：生产代码 0，测试代码在汇报中说明来源）。
- 记录在第 33.4 节。

---

## 里程碑 0：基线与骨架

### 10. Task 0：基线与骨架

1. 从 master `fe82d79` 新建分支 `feat/debug-assistant`。记录基线：
   - Debug 与 Release 的构建警告；
   - `pwsh build/test-affected.ps1 -All -Repeat 1` 的通过数、失败数、跳过数；
   - 第 9 节的度量。
2. 新建 `build/Junevy.Communication.Tools.props`（DA-D2）与三个项目（DA-D1），并加入 slnx：
   - 应用项目：空主窗口，主题字典已合并，窗口的底色与前景已设置；
   - Simulators 项目：空项目；
   - 测试项目：只有一条冒烟测试。
3. 包按 DA-D3 固定版本。
4. 在第 33.3 节记录四项验证，每项写明命令与结果：
   - Junevy.Controls 3.3.0 能从本地源还原；
   - 应用引用全部协议包后没有 NU1605；
   - 在 WPF 进程里启动 `WebApiServer`（`http://127.0.0.1:0`）成功，并能停止；
   - 在 WPF 进程里加载 OPC UA 客户端程序集，没有绑定错误。
5. 验收：
   - 构建 0 错误、0 警告；
   - 应用能启动，显示空主窗口，可以切换主题；
   - 冒烟测试通过。

## 里程碑 1：外壳与会话基础设施

### 11. Task 1：外壳（宿主、DI、主窗口、树、页签）

1. `App.xaml.cs`：
   - 用 `Host.CreateApplicationBuilder()` 构建宿主，从 DI 取出 `MainWindow`；
   - 启动时注册 `CodePagesEncodingProvider`；
   - 退出流程按 DA-D9 执行。
2. `ProtocolKind`、`ProtocolDescriptor`、`ProtocolCatalog`（第 4.3 节的 19 项）。左侧 `jv:TreeView` 的设置：
   - `DisplayMode="Chevron"`；
   - 图标从库内的 iconfont 中选，不引入新的图标资源。
3. 右侧 `jv:TabControl` 按 DA-D8 配置（改名用 Setter，关闭用 Behavior）。空状态提示、工具栏（第 5.5 节）。
4. 本 Task 先实现一个 `PlaceholderSessionViewModel`，保证每种协议都能打开页签。它在后续各 Task 中逐个被真实的页签取代，Task 19 结束时删除。
5. 测试：
   - 目录完整，19 种各一项，分组正确；
   - 双击叶节点打开页签，标题自动编号；
   - 双击分组节点不打开页签；
   - **改名**：STA 测试走真实的 `jv:TabControl`，进入编辑态，提交后检查 `vm.Title`；再从视图模型改名，检查 `Header`（固化 DA-D8 的验证结果）；
   - **关闭**：会话被释放并从集合中移除；释放出错时页签仍被移除，并写日志；
   - 切换主题后，主窗口的底色画刷随之改变。

### 12. Task 2：会话基础设施

1. 实现第 5.2 节的 `SessionViewModel` 基类与 `SessionFactory`（键控服务），以及 `OperationRecord` 与 U6 的结果映射（`CommResult`、`WebApiResult<T>`、`SecsResult` 三种）。
2. 实现第 5.3 节的 `SessionLogSink`、`SessionLog` 与日志面板视图（DataGrid 虚拟化），以及 `IUiDispatcher` 与它的同步测试实现。
3. 实现确认与通知服务，WPF 实现分别基于 `ConfirmDialogService` 与 `MessageBarService`；文件对话框服务。
4. 测试：
   - **日志模板锁定**：用库的 `TcpServer` 与 `TcpClientChannel` 在回环地址上收发一帧，Sink 必须得到方向、长度、十六进制都正确的 `FrameEntry`；UDP 的 `to {Destination}` / `from {Remote}` 也各测一条；
   - 超过 256 字节的帧被标记为截断，长度为完整长度；
   - 环形缓冲的上限；批量刷新（假时钟）；筛选与导出；
   - 三种结果的映射（每种 `ErrorKind` 一行）；
   - **架构测试**：U2 的三条；DA-D19 的登记完整性：每个 `ProtocolKind` 都有描述、参数类型、键控注册与 `DataTemplate`（`DataTemplate` 的检查在 STA 线程上加载资源字典进行）。

### 13. Task 3：工作区

1. 实现 `WorkspaceStore`、`WorkspaceDocument`、`SessionSettings` 多态（DA-D11）。启动时恢复，退出时保存；工具栏的保存、导入、导出。
2. 测试：
   - 每种页签的参数往返都不丢字段（随各协议的 Task 逐个补齐，Task 19 结束时 19 种全部覆盖）；
   - 密码不写入文件；
   - 文件损坏时生成 `.bak`，以空工作区启动，并发出通知；
   - 不认识的种类被跳过；
   - `schemaVersion` 不符时给出提示；
   - 写入中途失败时，原文件保持不变。

## 里程碑 2：自定义协议（通道）

### 14. Task 4：通道参数编辑器与字节工具

1. 编辑器（视图模型 + `UserControl`）：分帧（含实时预览）、关联、心跳、重连、超时、套接字、TLS。各编辑器有 `ToXxx()` 方法，把参数映射成库的配置对象。
2. DA-D12 的部件：`ConfigurableResponseMatcher`、`ConfigurableKeyExtractor`、`ScriptedInitializer`、校验辅助、`ByteInput`（文本 / 十六进制 / 编码）。
3. 测试：
   - 每个编辑器的映射用表驱动测试，每个字段一行；非法组合由库抛出 `ArgumentException`，测试检查界面显示为校验错误；
   - 校验算法用已知向量：CRC-16/MODBUS 对 `01 03 00 00 00 01` 为 `84 0A`；CRC-16/CCITT-FALSE 对 ASCII `123456789` 为 `29B1`；Sum8、XOR8、LRC 各取 3 条手算向量；
   - 匹配器与键提取器：边界条件（偏移越界、长度不足）返回不匹配，不抛异常。

### 15. Task 5：TCP 客户端、UDP、串口页

1. 实现第 7.13 节的通用部分，以及 UDP 专有、串口专有的功能。
2. 测试（端到端，回环地址，端口取 0）：
   - TCP 客户端对库的 `TcpServer`：6 种分帧各收发一次；三种关联方式（Keyed 用两个交错的请求，验证按键匹配）；握手；心跳失败后进入重连；
   - UDP：定向的请求与应答；非定向的 `SendToAsync`；在回环地址上测广播（如果环境不允许，测试注明后跳过）；
   - 串口：COM20 ↔ COM21 的请求与应答（`SerialPairFact`）；
   - 定时循环发送能停止，停止后不再发送。

### 16. Task 6：TCP 服务端页

1. 实现第 7.13 节"TCP 服务端专有"：会话列表、发送、广播、关闭会话、自动应答规则，以及启动与停止。
2. 测试：
   - 用 Task 5 的 TCP 客户端页的视图模型连接 TCP 服务端页的视图模型（两个页签互连）；
   - 自动应答；白名单拒绝；最大会话数；停止后再启动。
3. **门槛 1 汇报**：外壳、日志采集、四个自定义协议页、摩擦记录、度量。

## 里程碑 3：PLC 客户端

### 17. Task 7：PLC 公共面板

1. 实现第 7.1 节：地址校验、类型化读写、监控表、数据格式、套件选项。
2. 每个协议页只提供"创建客户端"和"协议特有区"两部分，公共面板对它们一视同仁（面向 `IPlcClient`）。
3. 测试：
   - 每种数据类型的值解析与格式化，各取边界值；
   - 监控表轮询：上一轮未完成时跳过本轮、断开时停止、写入后立即刷新；
   - 测试接缝：PLC 会话一律调用协议包公开的 `Create(transport, transportCreator, options, loggerFactory)` 重载，`IChannelCreator` 由服务 `ITransportCreatorProvider` 提供。正式运行时返回库内置的 `TcpClientChannelCreator`、`UdpChannelCreator`、`SerialChannelCreator`；测试时替换为返回 Testing 库 `ScriptedClientChannel` 的 creator（可配合 `RecordingChannelCreator` 检查传入的配置与组件），不连接真实设备。

### 18. Task 8：Modbus 页，删除旧工具

1. 实现第 7.2 节。
2. 对照第 7.2 节最后一行确认覆盖了旧工具的功能，然后删除 `Junevy.Communication.Test`（DA-D18），同时修改 slnx 与知识库中的引用（Task 20）。
3. 测试：
   - 五种传输与帧格式组合的配置映射；
   - 掩码助手（置位、清零第 n 位得到的两个掩码，各取 3 条向量）；
   - 功能码提示与 `UseSingleWriteFunctions` 一致。
   - 端到端测试放在 Task 14（对模拟器）。

### 19. Task 9：MC 页

1. 实现第 7.3 节；软元件速查表与 `ParseAddress` 的一致性测试（28 项）。
2. 测试：帧类型、编码、传输的合法组合表（全排列，不合法的组合不可选）；访问路径的十六进制输入；监视定时器的毫秒换算与警告。

### 20. Task 10：S7 页、FINS 页

1. 实现第 7.4、7.5 节。
2. 测试：
   - TSAP 计算（3 条向量）；
   - PDU 换算出的单包上限（240 → 读 222、写 212；960 → 读 942、写 932）；
   - String / WString 的长度范围校验；
   - FINS 节点参数的范围；FINS 区域速查表与 `ParseAddress` 的一致性。

## 里程碑 4：模拟设备

### 21. Task 11：模拟器的公共部分

1. 实现第 8.1 节的 `Memory`、`Faults`、`Hosting`，以及 `ISimulatorEngine`。
2. 测试：
   - 内存区越界与并发读写；
   - 故障注入的每一种（延迟用假时钟或宽松的时间断言，沿用 Testing 库的 `TimingAssert`）；
   - 宿主在 TCP、UDP、串口（COM20 / 21）上的收发，引擎用测试替身。

### 22. Task 12：Modbus 从站、MC 从站

1. 实现第 8.1 节的 Modbus、Melsec 两部分，以及第 8.2 节的分帧。
2. 测试：第 8.3 节的黄金向量（Modbus：P7 计划第 21.1–21.3 节；MC：P2 计划第 18 节 V1–V10），RTU 请求解码器的重新同步，4C 格式 5 的 DLE 转义与和校验。

### 23. Task 13：S7 CPU、FINS 从站

1. 实现第 8.1 节的 Siemens、Omron 两部分。
2. 测试：P4 计划第 17.1、17.2 节的黄金向量；PDU 协商；PUT/GET 关闭时返回 `0x81 0x04`；越过块尾返回 `0x05`；不存在的 DB 返回 `0x0A`；FINS 越界返回结束码 `0x1103`。

### 24. Task 14：模拟设备页与端到端矩阵

1. 实现第 7.14 节的四种页签：宿主参数、设备参数、内存表、故障注入、请求日志。
2. 端到端矩阵（第 8.3 节），按组合列出测试名，写进第 33.3 节。
3. **门槛 2 汇报**：矩阵结果、客户端与模拟器不一致之处、摩擦记录。

## 里程碑 5：工厂系统协议

### 25. Task 15：SECS 设备端页

1. 实现第 7.6 节与 DA-D13 的 `SmlParser`。
2. 测试：
   - SML 往返性质（每种格式、空列表、多层嵌套、`A` 中的 `\xHH`、浮点数的 G9 / G17）；
   - 解析错误的行号与列号；
   - 端到端：测试项目里写一个最小 HSMS 主机，只用 Channels / Tcp 的公开 API，帧取自 P5.1 计划第 17 节。用例包括：
     - 选择会话；
     - 主机发 S1F1 W，自动应答规则回 S1F2；
     - 设备发 S5F1 W，主机回 S5F2；
     - 主机不应答时得到 `ReplyTimeout`（T3 设为很短）；
     - 没有登记的 S/F 收到 S9F5。

### 26. Task 16：共享 SDK 测试服务器

1. 新建 `Tests/Junevy.Communication.Testing.Servers`（`net8.0`，`IsPackable=false`）。用 `git mv` 迁入三组替身，类型改为 `public`，命名空间改为 `Junevy.Communication.Testing.Servers`：
   - `Mqtt.Tests` 的 `TestBroker`（含 Scenarios）与 `TestObservations`；
   - `OpcUa.Tests` 的 `OpcUaTestServer` 与 `OpcUaServerFixture`；
   - `Ftp.Tests` 的 `FakeFtp*`。
2. Mqtt、OpcUa、Ftp 三个测试项目改为引用它，测试代码只改 `using` 与项目引用。迁移前后通过数必须一致，写进汇报。
3. 理由：助手的 MQTT、OPC UA、FTP 页需要端到端测试。复制这三组替身会产生约 1000 行重复的测试代码（P7 R7 把 `ScriptedClientChannel` 移进共享库是同类做法）。替身只用公开 API，不依赖适配包的内部类型（2026-10-20 已核对 `using`）。

### 27. Task 17：MQTT 页、OPC UA 页

1. 实现第 7.7、7.8 节。
2. 测试（端到端，使用 Task 16 的服务器）：
   - MQTT：订阅、发布、接收（文本与十六进制）；V5 属性的往返；在 V3.1.1 上发布带属性的消息时返回 `InvalidRequest`；遗嘱参数的映射；
   - OPC UA：浏览 `i=85`；批量读中混入一个未知节点，验证逐项状态；每种可写类型各写一次（类型选择器的转换）；订阅收到初始值，释放订阅后不再收到。

### 28. Task 18：WebApi 页（REST、SOAP、服务端）

1. 实现第 7.9、7.10、7.11 节。
2. 测试（端到端，在本进程内用"HTTP 服务端页"驱动"REST 客户端页"）：
   - 固定响应、回显、业务失败加信封、指定状态码；延迟超过 `RequestTimeout` 时客户端看到 504；
   - 重试只发生在带幂等键的 POST 上；去重的 replay、409、422；
   - 认证失败返回 401；健康检查使可用性变为 Online。
   - SOAP：服务端页只做 REST，所以 SOAP 页的测试经 `WebApiComponents.HttpHandler`（`SoapClient` 构造函数的第三个参数）注入脚本化的 `HttpMessageHandler`，测原始调用、SOAP 1.1 / 1.2 的信封与 Fault 映射。视图模型通过一个测试用的组件钩子取得这个处理器，正式运行时为 null。

### 29. Task 19：FTP 页

1. 实现第 7.12 节。
2. 测试（端到端，使用 `FakeFtpServer`）：列目录；上传与下载及进度；Skip 模式；新建目录；递归删除（需确认，用测试替身确认）；重命名；GBK 编码的文件名。
3. 删除 Task 1 的 `PlaceholderSessionViewModel`。确认 19 种页签都已实现，工作区往返测试覆盖全部 19 种。

## 里程碑 6：文档与验收

### 30. Task 20：文档

1. `Tools/README.md`：
   - 用途、运行要求（ASP.NET Core Runtime 8、本地 NuGet 源中的 Junevy.Controls 3.3.0）、启动方式；
   - 19 种页签的说明；
   - 报文截断的限制（DA-D5）；
   - 新增一种页签的步骤（DA-D19）；
   - 模拟设备的使用方法与故障注入；
   - 工作区文件的位置。
2. 根目录 `readme.md`：新增"Tools"一节。设计文档第 3 节：程序集表加三行，目录树中删除 `Junevy.Communication.Test`、加入 `Tools/`。
3. `CHANGELOG.md`：新增"调试助手"一节，写明时间、新增的项目、删除的旧工具、Task 16 的测试迁移。
4. Skills：在 `Skills/` 中检索旧工具名与"调试工具"，有引用就更新；没有引用则在汇报中说明已检查。
5. 知识库（只写不提交），按 DA-D15 与第 2 节第 10 条：
   - `项目概述/技术栈与仓库结构`：WPF 工具一节改为调试助手；
   - `测试/测试工程与验收方式`：新增两个测试项目，删除旧工具的那一行；
   - `Agent协作/开发约定与验收基线`：警告检查的排除列表去掉旧工具，加入 Tools 的约定（第 3 节 U1–U8）；
   - `项目概述/待办与路线图`：加一行本期计划，并记下后续可选项（第 32 节）；
   - 新建笔记 `工具/调试助手.md`：架构、扩展点、日志采集的约定，挂进 `知识库首页.md` 的分类索引。

### 31. Task 21：最终度量与手动验收

1. 复测第 9 节的度量，填写第 33.4 节。
2. 检查：
   - XAML 中硬编码的色值为 0（U3）；
   - 同步等待异步操作为 0（U5）；
   - `Junevy.Communication.*` 库项目的生产代码 diff 为空：`git diff fe82d79 -- 'Junevy.Communication.*/**/*.cs'` 只允许出现在被删除的 `Junevy.Communication.Test` 下。
3. **手动验收清单**（第 32 节之前的每条都要截图，图放在 `docs/superpowers/plans/assets/debug-assistant/`，在汇报中列出）：
   - 打开 19 种页签，各改名一次、关闭一次；重启程序后工作区恢复；
   - 浅色与深色主题下各截一张主界面；
   - MC 3E 二进制、3E ASCII、4E、4C（COM20 / 21）分别连接 MC 从站，读写 D100 与 M0；
   - Modbus TCP 与 RTU over TCP 分别连接 Modbus 从站，写 16 掩码、17 读写合一；
   - S7 连接 S7 CPU，读写 String；开启 PUT/GET 拒绝后看到错误提示；
   - FINS 连接 FINS 从站，显示自动分配的节点号；
   - 故障注入：不应答 → `Timeout`；断开 → 重连中再到已连接；
   - TCP 客户端页与 TCP 服务端页互连（Delimiter 分帧、Keyed 关联）；
   - HTTP 服务端页与 REST 客户端页互连（信封、重试、去重）。
4. 填写第 34 节的自查清单，通知审阅者。

---

## 32. 未纳入本计划的条目（后续可选）

- 改库的选项（都需要用户决定）：
  - Channels 的公开帧观察钩子（替代日志采集，消除 256 字节截断，F-1）；
  - MC 的公开环回接口（F-2）；
  - MC 与 FINS 公开软元件 / 区域表（F-3）；
  - Secs 的消息观察事件（F-4）；
  - 把 `SmlParser` 移进 Secs 包。
- SECS 主机端、MQTT Broker、OPC UA 服务端、FTP 服务端（库没有这些角色）；
- Modbus ASCII、MC 的随机读写与多块读写、S7 的多项读写（库不支持）；
- GEM（P5.2 尚未开工），完成后作为新的页签种类，按 DA-D19 接入；
- 脚本或自动化测试序列（例如批量执行一组读写并断言）。

## 33. 调研记录与执行记录

### 33.1 调研已确认的事实（2026-10-20，计划作者已核对，执行者不必重做）

| # | 事实 | 来源 |
|---|---|---|
| R1 | Junevy.Controls 3.3.0 的 nupkg 在 `D:\Documents\Nuget\Local`，nuspec 中记录的提交是 `7d59681`，与本机 Junevy.Controls 仓库的 HEAD 相同；目标框架为 `net48` 与 `net8.0-windows7.0`，依赖 AvalonEdit 6.3.1.120 | 解开 nupkg 检查 |
| R2 | `jv:TabControl` 的改名只在 `Header is string` 时进入编辑态；`ItemContainerStyle` 中用 `Header="{Binding Title, Mode=TwoWay}"` 时，提交改名会写回视图模型，视图模型改名也会更新 `Header`；不写 Setter 时，`Header` 是数据项，无法改名 | 草稿区实验程序，结果为 V1、V2 通过，V3 无法进入编辑（Task 1 会把它固化成测试） |
| R3 | `TabClosing` / `TabClosed` 的路由策略是 `Direct`，`TabCloseEventArgs` 有 `Item` 与 `Cancel`；`ItemsSource` 是 `ObservableCollection` 时，控件会自己移除数据项，宿主在 `TabClosing` 中自己移除并置 `Cancel` 也受支持 | Junevy.Controls README "TabControl 与 TabControlItem" |
| R4 | `jv:TreeView.ItemDoubleClick` 的路由策略是 `Bubble`，参数类型为 `TreeItemEventArgs`（`Item`、`Container`），3.3.0 已包含；双击展开箭头不会触发 | `Controls/Menu/TreeView*.cs`、`TreeItemEventArgs.cs` |
| R5 | `jv:CodeEditor` 支持 `SyntaxLanguage=Json` 与 `Xml`，`Text` 是可绑定的依赖属性 | `CodeLanguage` 枚举 |
| R6 | 通道的报文日志在 `Debug` 级别：流式通道为 `TX/RX {Length} bytes: {Hex}`，数据报为 `TX … to {Destination}` / `RX … from {Remote}`；`HexFormatter.ToHex` 用 `-` 分隔字节，最多输出 256 字节 | `StreamChannel.cs:310/531`、`DatagramChannel.cs:287/339`、`HexFormatter.cs` |
| R7 | OPC UA SDK 2.0.1 需要 `Microsoft.Extensions.*` 10.0.12，`OpcUa.Tests` 用 `Update` 把 DI 升到 10.0.12；本机缓存中有 CommunityToolkit.Mvvm 8.4.2、Microsoft.Extensions.Hosting 10.0.12、Microsoft.Xaml.Behaviors.Wpf 1.1.158 | csproj 与 NuGet 缓存 |
| R8 | 本机已安装 ASP.NET Core 8.0.29 / 8.0.31 与 WindowsDesktop 8.0.x 运行时 | `dotnet --list-runtimes` |
| R9 | 测试项目里的设备替身（`S7DeviceSimulator`、`FinsDeviceSimulator`、`ModbusSlave`、`HsmsHost`）引用了协议包的内部命名空间，不能被应用复用；`TestBroker`、`OpcUaTestServer`、`FakeFtpServer` 只用公开 API | 各文件的 `using` |
| R10 | 架构测试只允许 `<库>.Tests` 作为 `InternalsVisibleTo` 的友元，另有生产库之间的两条登记（Channels → Tcp、Udp） | `ArchitectureTests.RegisteredGrants` |
| R11 | `FramingOptions` 的二进制长度字段宽度只能是 1、2、4，ASCII 为 1–8 | `FrameCodecFactory.cs:107` |

### 33.2 摩擦记录（库的公开 API 不够用的地方；执行者继续追加，格式：现象 / 变通或缺口 / 建议）

| # | 现象 | 变通或缺口 | 建议 |
|---|---|---|---|
| F-1 | 报文只能从日志中获取，并且超过 256 字节就被截断 | 按页签挂日志 Sink（DA-D5）；界面标出截断 | 在 Channels 中提供公开的帧观察钩子（用户已决定本期不做） |
| F-2 | MC 的环回测试（0619）只用于心跳，没有公开方法 | 开启心跳后，从报文面板间接观察 | 如果需要单独发送环回，在 `McClient` 上加 `LoopbackAsync` |
| F-3 | MC 的 `McDeviceTable`、FINS 的 `FinsDeviceTable` 是 internal，助手无法列出全部软元件或区域 | 助手维护一份显示用的表，并用 `ParseAddress` 测试其一致性 | 公开只读的设备列表（Modbus 的 `ModbusDevice` 已经是公开的静态实例） |
| F-4 | `HsmsEquipment` 只能按 S/F 登记处理器，没有"观察所有收到的消息"的事件；没有登记的主消息只能在日志中看到头部 | 由自动应答规则登记的 S/F 可以显示正文；其余只显示头部 | 增加只读的 `MessageReceived` 事件 |
| F-5 | Secs README 写"Body text in SML is reserved for `Trace`"，但代码中没有任何 `Trace` 级别的日志 | 不影响助手 | 修改 README 的措辞（"目前不记录正文"），或者实现 Trace 日志，需要用户决定 |
| F-7 | 分隔符、起止标记、心跳内容等文本只能按 UTF-8 解释（`ByteSequenceParser`），无法选择 GBK 等编码 | 助手的字节输入可以选择编码；要用 GBK 的分隔符，写成 `hex:` 形式 | 为这些配置增加编码选项（或接受 `hex:` 作为唯一做法） |
| F-6 | `TcpServerConfig.Port` 不接受 0（`TcpServerOptions` 要求 1–65535），无法让系统选择端口。测试只能先探测一个空闲端口（回环地址，有极小的竞争） | 测试用 `FreePort` 探测空闲端口 | 允许 `Port = 0` 由系统选择，`LocalEndPoint` 已经给出实际端口，因此改动很小 |
| F-8 | 库在构造期校验的参数规则只有英文消息：UDP 非定向不能用内置心跳、UDP 与串口的内置心跳必须填写 `ExpectedReply`。界面只能原样显示英文 | 页签在界面上用一行中文说明同一规则；连接失败时操作记录显示库的消息 | 参数错误提供参数名与错误码，助手即可本地化 |

### 33.3 执行汇报与偏差（执行者填写：每个 Task 的汇报、门槛汇报、偏差与需要审阅者决定的事）

**Task 0（2026-10-20，分支 `feat/debug-assistant`，基于 master `fe82d79`）**

- 做了什么：
  - 新增 `build/Junevy.Communication.Tools.props`（DA-D2，只设置 Nullable、ImplicitUsings、LangVersion、TreatWarningsAsErrors、IsPackable，不 Import Common.props）。
  - 新增三个项目：`Tools/Junevy.Communication.DebugAssistant`（WinExe，net8.0-windows，UseWPF）、`Tools/Junevy.Communication.DebugAssistant.Simulators`（net8.0，暂为空项目，只引用 Core、Channels、Tcp、Udp、Serial）、`Tests/Junevy.Communication.DebugAssistant.Tests`（net8.0-windows，UseWPF，引用应用与模拟器）。
  - `Junevy.Communication.slnx`：新增 `/Tools/` 文件夹，测试项目登记在 `/Tests/`。
  - 应用骨架：`App.xaml` 只合并 `Junevy.Controls;component/Themes/Generic.xaml`；`App.xaml.cs` 构建 `Host`、注册 GBK 代码页提供程序、清除默认日志提供程序，退出时释放宿主；`Views/Shell/MainWindow` 设置 `Background` 与 `TextElement.Foreground`（DynamicResource 主题令牌）；工具栏"切换主题"经 `IThemeService`（`Views/Platform/WpfThemeService` 调用 `ThemeManager.ToggleTheme()`）。
  - 测试 3 条：`MainWindowSmokeTests.ToggleThemeCommand_ChangesTheWindowBackground`、`RuntimeSmokeTests.WebApiServer_StartsOnAFreePort_AndStops`、`RuntimeSmokeTests.OpcUaClient_LoadsAndReportsAnUnreachableServer_WithoutBindingErrors`。WPF 对象的测试全部在同一条 STA 线程上执行（`Support/UiThread`）。
- 基线（改动前，`test-affected.ps1 -All -Repeat 1 -NoBuild`）：Debug 与 Release 各 0 错误、2 警告（`Junevy.Communication.Test` 的 CS8618，删除旧工具后消失）；测试通过 7204、失败 2、跳过 8（30 个测试宿主）。两条失败与本期无关，已在基线里存在，如实记录：
  - `Junevy.Communication.OpcUa.Tests` `OpcUaDependencyInjectionTests.AddOpcUaClients_PassesTheContainerLoggerFactoryThrough`：日志断言未匹配（时序敏感）；
  - `Junevy.Communication.Channels.Tests` `SupervisorTests.Lost_DuringConnecting_IsHandledAfterConnect`（net472）：期望 1、实际 0（5 ms 内的竞争）。
- 本期后：`dotnet build Junevy.Communication.slnx --no-incremental`，Debug 与 Release 均 0 错误、2 警告（与基线相同的那 2 条，全部来自旧工具，本期未新增）；无 NU 警告。
- 第 Task 0 第 4 条的四项验证：
  1. Junevy.Controls 3.3.0 从本地源还原：通过（`D:\Documents\Nuget\Local` 为已登记的 local 源，包解析成功）。
  2. 应用引用全部协议包后没有 NU1605：通过（`TreatWarningsAsErrors` 下 NU1605 会导致构建失败，构建通过即无）。
  3. WPF 进程里启动 `WebApiServer`（`http://127.0.0.1:0`）并停止：通过（`RuntimeSmokeTests.WebApiServer_StartsOnAFreePort_AndStops`，测试进程为 UseWPF）。
  4. WPF 进程里加载 OPC UA 客户端程序集，没有绑定错误：通过（`RuntimeSmokeTests.OpcUaClient_LoadsAndReportsAnUnreachableServer_WithoutBindingErrors`：创建客户端并连接到不存在的端口，得到失败结果而不是异常）。
- 应用启动：用 `Start-Process` 启动 Debug 版，进程存活，窗口标题为 "Junevy 通讯调试助手"，随后结束进程。主题切换由上面的测试验证。截图未取得：本会话里屏幕捕获返回"句柄无效"，Task 21 的截图需要另想办法。
- 偏差与需要审阅者决定的事：
  1. 验收项"构建 0 错误、0 警告"：本期未新增警告，但 2 条既有警告仍在（来自旧工具）。第 2 节第 5 条写明这 2 条在删除旧工具（Task 8）后消失；DA-D15 的白名单不允许在 Task 0 改旧工具的代码，因此按原样保留。请审阅者确认这一解释。
  2. 第 4.2 节的目录把 `MainWindow` 列在 `Shell/`，同时又规定"全部 XAML 在 `Views/`"。本期采用后者：XAML 与代码后置放在 `Views/Shell/`，视图模型 `MainWindowViewModel` 放在 `Shell/`。
  3. 第 3 节 U2 列出的命名空间是 `ViewModels`、`Services`、`Sessions`，但第 4.2 节没有 `ViewModels`，而且 `Shell/` 里的 `ProtocolTreeItem : TreeMenuItem` 必须引用 Junevy.Controls。准备在 Task 2 的架构测试里把"不得引用视图相关程序集"的范围定为 `Sessions`、`Services`、`Workspace`、`Logging`、`Common`、`Protocols`，`Shell` 与 `Views` 不在其中。请审阅者确认。
- 对公开 API 的验证：未发现库的缺口（Task 0 只用了 `WebApiServer`、`OpcUaClient`）。

**Task 1（外壳：宿主、树、页签、会话基础的最小形式）**

- 做了什么：
  - 19 种页签种类 `ProtocolKind`，描述 `ProtocolDescriptor`（树标题、默认名前缀、图标码点），登记表 `ProtocolCatalog`（DA-D19 的唯一登记处）。
  - 左侧 `jv:TreeView`（`DisplayMode="Chevron"`）：节点为 `ProtocolTreeItem : TreeMenuItem`，分组节点的 `Descriptor` 为 null。双击叶节点经 `EventTrigger`（`ItemDoubleClick`，`EventArgsParameterPath="Item"`）调用 `OpenSessionCommand`；双击分组不做事。
  - 右侧 `jv:TabControl`：`ItemsSource=Sessions`，`CanRename="True"`，`ItemContainerStyle` 派生自 `DefaultTabControlItemStyle`，`Header` 与 `Title` 双向绑定（DA-D8）；关闭由 `Views/Behaviors/CloseTabToCommandBehavior` 设置 `Cancel = true` 并交给 `CloseSessionCommand`；空状态提示由 DataTrigger 控制。
  - `MainWindowViewModel`：开启、关闭页签（先释放，限时 5 秒；释放失败或超时写日志，仍然移除）；关闭选中页签时选中相邻页签；"关闭全部页签"经 `IConfirmService`（WPF 实现基于 `ConfirmDialogService`）确认；`ShutdownAsync` 并发释放全部会话，总时限 10 秒。
  - 会话：`SessionViewModel`（抽象，`Descriptor`、`Kind`、`Title`、`DisposeAsync`）；`PlaceholderSessionViewModel`，19 种都注册为键控服务（占位，Task 19 删除）；`SessionFactory` 按种类解析键控服务，`SessionNamer` 分配 "&lt;ShortName&gt; #n"。
  - 退出：`App` 的 `Closing` 处理器取消关闭，等待 `ShutdownAsync`，再调用 `Shutdown()`。这是事件处理器里的 `async void`，属于 U5 允许的位置。保存工作区留待 Task 3。
- 测试：`DebugAssistant.Tests` 共 38 条，全部通过（Task 1 新增 35 条：目录 20 条、视图模型 10 条、STA 的主窗口与页签 5 条）。覆盖计划第 Task 1 第 5 条的每一项。
- 偏差与需要审阅者决定的事：
  1. 测试的模拟方式（不是库的缺陷）：`RaiseEvent(MouseDoubleClick)` 在测试里不会派发到 `OnMouseDoubleClick`，因此测试直接调用页签容器自己的重写（反射）；Enter 与 Escape 的提交依赖 `Keyboard.Focus`，需要活动窗口，因此测试触发 `LostFocus`（库在这一刻经绑定写回 `Header`）。两者都覆盖了提交的代码路径，但没有覆盖真实的按键。
  2. 页签的 × 按钮的命令路径没有在测试中覆盖：`ButtonAutomationPeer` 调用没有触发关闭。测试改用控件公开的 `TabControl.CloseTab`，它与 × 按钮走同一个 `CloseTabInternal`。× 按钮需要在手动验收中确认。
  3. 图标从 iconfont 的已有字形中选取（`E600`–`E9AE` 的若干个，部分种类共用）。验收时如认为不合适再调整。
  4. U2 的范围问题与 Task 0 的第 3 条相同：`Shell/` 中的 `ProtocolTreeItem` 引用 Junevy.Controls，按计划允许；视图模型与服务层不引用。
  5. 截图未取得（同 Task 0）。

**Task 2（会话基础设施：基类、结果映射、日志管线、面板、服务、架构测试）**

- 做了什么：
  - `SessionViewModel` 基类（第 5.2 节）：`Title`、`Descriptor`、`Kind`、`State` 与 `StateText`、`IsEditable`、`Operations`（上限 500，最新在前）、`ConnectCommand` / `DisconnectCommand` 与各自的取消命令（取消只在进行中可用）、`ConnectCoreAsync` / `DisconnectCoreAsync` 钩子、`DisposeAsync`（先断开，再停止日志刷新并释放日志工厂）、`PostToUi`，以及页签私有的 `SessionLoggerFactory`（最低级别 Debug，挂 `SessionLogSink`）。异常不逃出界面（U7）：取消记为 `Cancelled`，其他异常记为 `Unspecified` 并写入页签日志。`CaptureSettings` / `ApplySettings` 留到 Task 3。
  - 结果映射（U6）：`OperationRecords` 把 `CommResult`、`CommResult<T>`、`WebApiResult<T>` 统一为 `OperationRecord`；错误种类的中文名称在 `CommErrorKindNames`（DA-D17）。`SecsResult` 的映射见偏差 1。
  - 日志（第 5.3 节，DA-D5）：`SessionLog`（待处理队列与可见集合各有上限 5000；`Append` 可从任何线程调用，`Flush` 只在界面线程；暂停；筛选；清空；统计在写入时计数，不受暂停与队列裁剪影响）；`SessionLogSink` / `SessionLogger` / `FrameLogParser`（识别库的四种 Debug 模板，判断截断）；`SessionLogFlusher`（`PeriodicTimer` 100 ms，有待处理条目时经 `IUiDispatcher` 派发刷新）；`LogPanelViewModel`（筛选、暂停、清空、导出为 .txt、统计文字）；`Views/Sessions/LogPanel.xaml`（DataGrid 虚拟化；HEX、ASCII、时间戳列由复选框控制可见性）。
  - 服务：`IUiDispatcher`（WPF 实现 `WpfUiDispatcher`）、`IFileDialogService`（`WpfFileDialogService`）、`INotificationService`（`WpfNotificationService` 基于 `MessageBarService`；App 启动时注册 `NotificationPresenter`）。`IConfirmService` 在 Task 1 已有。
  - 占位页签（`PlaceholderSessionView`）内嵌日志面板，绑定到页签自己的 `LogPanel`。
- 测试：`DebugAssistant.Tests` 共 91 条，全部通过（本期新增 53 条，其中包括 `[Theory]` 的各行）。
  - 日志模板锁定（`LibraryFrameLogTests`）：用库的真实 `TcpClientChannel` 与 `UdpChannel` 收发，页签得到方向、长度与十六进制都正确的报文；TCP 的 `RequestAsync` 产生一条 TX 和一条 RX；超过 256 字节的帧被标记为截断，长度为完整长度；UDP 的 TX / RX 分别带有 `to {Destination}` / `from {Remote}` 的端点。
  - 模板解析（`SessionLoggerTests`）：四种模板、不匹配的模板变成普通日志、异常摘要；后台刷新循环在有待处理条目时会派发刷新（真实计时，最长等待 5 秒）。
  - 日志（`SessionLogTests`、`LogPanelViewModelTests`）：环形缓冲的上限（待处理与可见两处）、暂停、筛选（报文与日志、最低级别）、清空保留统计、统计、变化通知、导出（写入文件并有成功通知；取消时什么也不写）、统计文字、HEX 与 ASCII 列（不可打印字节显示为 '.'，截断的帧显示说明）。
  - 结果映射（`OperationRecordsTests`）：每种 `CommErrorKind` 一行（12 行）；协议码十六进制；WebApi 显示远端业务码，否则显示 HTTP 状态码。
  - 会话（`SessionViewModelTests`）：连接成功的记录与命令可用性、连接抛出异常、取消、释放先断开、操作记录上限、`PostToUi`。
  - 架构（`LayeringRulesTests`）：`Sessions`、`Services`、`Logging`、`Workspace`、`Common`、`Protocols` 下的类型，包括嵌套类型与方法体中的 IL 引用，不得引用 PresentationFramework、PresentationCore、WindowsBase、Junevy.Controls、Microsoft.Xaml.Behaviors。**负控已做**：在 `Services/IThemeService.cs` 的方法体里临时写入 `new System.Windows.Window()`，测试失败并报告了 `IThemeService`；撤销后通过。
  - 登记（`RegistrationRulesTests`）：19 种种类都有描述、键控服务与 DataTemplate（参数类型的检查在 Task 3 加入）。
  - 视图（`SessionViewTests`）：占位页签的 XAML 能加载，日志面板绑定到页签自己的 `LogPanel`，面板里有 DataGrid。
- 偏差与需要审阅者决定的事：
  1. `SecsResult` 的映射没有单元测试：`SecsResult.Success` 与 `Failure` 是 internal，DA 无法构造。SECS 的映射（含拒绝原因码的十六进制）留到 Task 15，用真实的 HSMS 结果测试。
  2. WebApi 的协议码：`WebApiResult` 没有协议错误码，`StatusCode` 是 int。因此显示远端业务码，没有时显示 "HTTP 404" 这类十进制文字，而不是十六进制。请审阅者确认。
  3. 架构测试用的是 IL 扫描（`AssemblyReferenceScanner`，只依赖 `System.Reflection`），没有引入 NetArchTest：AGENTS 规定 NetArchTest 只出现在 Architecture.Tests。
  4. 套接字测试没有放入 `SocketTiming` 集合：`Tests/Junevy.Communication.Testing` 里没有这个集合的定义。本期的套接字测试都在回环地址上运行，并且各自释放。请审阅者决定是否需要。
  5. `TcpServer` 的端口不接受 0（摩擦 F-6），测试用 `FreePort` 先探测空闲端口。
  6. 日志面板的高度暂时固定为 260；计划第 6 节的 GridSplitter 可调高度留给页面布局的统一实现（Task 5 起，各协议页签共用）。
  7. 日志的时间戳使用创建条目时的本地时间。

**Task 3（工作区）**

- 做了什么：
  - 参数基类 `SessionSettings`（`[JsonPolymorphic]`，判别字段 `type`），目前只有 `PlaceholderSettings`；每种协议完成时在这里加一行 `[JsonDerivedType]`（DA-D19）。`SessionViewModel` 新增抽象成员 `CaptureSettings()` 与 `ApplySettings()`，占位页签实现为空参数。
  - `WorkspaceDocument`（`schemaVersion: 1`，页签列表，选中下标）与 `WorkspaceSession`（种类以文本保存，遇到未知种类时可以跳过）。
  - `WorkspaceStore`：读取时，损坏、为空或版本不符的文件改名为 `.bak` 保留，以空工作区启动，并返回说明；写入时先写 `workspace.json.tmp`，再替换原文件。默认位置是 `%LocalAppData%\Junevy.Communication\DebugAssistant\workspace.json`。
  - `WorkspaceService`：`Capture`、`Restore`（未知种类与参数不符的页签被跳过并说明；选中项随之重新计算）、单个页签的导入与导出（`.json`）。
  - 主窗口：启动时在 `App` 中调用 `RestoreWorkspace()`；退出时 `ShutdownAsync` 先保存工作区，再并发释放（DA-D9 的顺序）；工具栏新增"保存工作区""导入页签""导出当前页签"。
- 测试：`DebugAssistant.Tests` 共 109 条，全部通过（本期新增 18 条）。覆盖计划 Task 3 的每一条：保存与读取往返、损坏文件、版本不符、写入中途失败保持原文件、未知种类跳过、19 种的参数往返（`SingleSessionFile_RoundTripsEveryKind_WithItsSettingsType`）、密码字段标记 `[JsonIgnore]`（反射扫描，目前没有密码字段，因此这一条在各协议加入参数类型之前是空转的）、启动恢复、退出前保存、手动保存的通知、导入与导出的往返、导出命令的可用条件。测试不写用户的工作区（每个测试实例使用临时目录）。
- 偏差与需要审阅者决定的事：
  1. 版本不符的文件同样改名为 `.bak`（而不是只提示）。否则以空工作区启动后，退出时的保存会覆盖用户的数据。这是有意的选择，请审阅者确认。
  2. "写入中途失败"的测试通过占用临时文件名（一个同名目录）来制造失败。在 Windows 上抛出的是 `UnauthorizedAccessException`，测试接受任何异常，界面层两种都捕获。
  3. 参数类型的检查目前只覆盖占位参数；每种协议的 Task 会补充它的往返测试（计划要求的做法）。
  4. 导出的单页签文件与工作区文件使用同一格式（`schemaVersion` 加一个页签），导入时只取第一个页签。

**Task 4（通道参数编辑器与字节工具）**

- 做了什么：
  - 字节工具：`ByteInput`（`hex:` 与文本语法与库的 `ByteSequenceParser` 一致；文本编码可选 UTF-8、ASCII、GBK，UTF-8 直接交给库，其余按所选编码转换，转义规则相同）、`EncodingChoice`、`ChecksumKind` 与 `Checksums`（None、Sum8、Xor8、CRC-16/MODBUS、CRC-16/CCITT-FALSE、LRC；`Append` 把校验加在负载之后）。
  - DA-D12 的部件（`internal`）：`ConfigurableResponseMatcher`（前缀与回显两种规则）、`ConfigurableKeyExtractor`（宽度 1、2、4，两种字节序）、`ScriptedInitializer`（连接后发送，可选等待并比较应答）、`FramePreview`（用库的 `FrameCodecFactory` 切分一段字节，不连接）。
  - 编辑器（`Protocols/Channels/Editors`）：`FramingEditor`（六种模式，各自的参数面板；分隔符每行一个；切分预览命令）、`CorrelationEditor`（顺序、匹配规则、键字段）、`HeartbeatEditor`、`ReconnectEditor`、`TimeoutEditor`、`SocketEditor`（含 KeepAlive）、`TlsEditor`（证书只保存环境变量名）、`HandshakeEditor`，以及组合它们的 `ChannelSettingsEditor`（生成库的 `ChannelComponents`）。所有编辑器派生自 `ValidatedEditor`：每次属性变化后按 `[Range]` 重新校验（计划 U7）。
  - 视图：`Views/Channels/ChannelSettingsView.xaml`，分帧参数随模式显示，分组可折叠。
- 测试：`DebugAssistant.Tests` 共 177 条，全部通过（本期新增 68 条）。校验的已知向量：CRC-16/MODBUS 对 `01 03 00 00 00 01` 为 `84 0A`，CRC-16/CCITT-FALSE 对 "123456789" 为 `29B1`，Sum8、XOR8、LRC 各 3 条手算向量；匹配器与键提取器的边界（偏移越界、长度不足、非法参数）返回不匹配或失败，不抛异常；握手在真实回环连接上运行（成功、应答不符、只发送）；编辑器的映射逐字段表驱动；库拒绝的非法组合（二进制长度字段宽度 3）通过预览显示库的消息。
- 偏差与需要审阅者决定的事：
  1. CRC-16/CCITT-FALSE 的字节序：计划没有指定，这里采用高字节在前（XMODEM 类协议的常见写法）。请审阅者确认。
  2. `InternalsVisibleTo` 只向本应用的测试项目开放（`AssemblyInfo.cs`），因为 DA-D12 的部件是 `internal`；模拟器项目不加（DA-D14）。
  3. 库没有通道级的匹配器：`IResponseMatcher` 按请求传入（`RequestOptions.Matcher`）。因此 `CorrelationEditor.ToMatchRule()` 的结果由传输页在每次请求时使用，不进入 `ChannelComponents`。
  4. 握手的测试不用 `ScriptedClientChannel`（它不支持 `SendAsync`），改用真实的回环 `TcpServer` 与 `TcpClientChannel`，更接近实际路径。
  5. 分帧的 `IdleGap` 无法在预览中切分（需要时间），预览会说明这一点。
  6. `ObservableValidator` 不会在属性赋值时自动校验，因此 `ValidatedEditor` 在 `OnPropertyChanged` 中补上（见代码注释）。
  7. 对不以 `hex:` 开头的文本，编码选项只用于字节输入（预览、握手、心跳）；分隔符与标记由库按 UTF-8 解释（摩擦 F-7）。

**Task 5（TCP 客户端、UDP、串口页）**

- 做了什么：
  - 传输页的共同部分 `ChannelSessionViewModel`（第 7.13 节）：连接与断开（参数有校验错误时拒绝连接并提示）；发送、请求并等待应答、等待一帧；定时循环发送（间隔 10 至 3600000 毫秒，次数 0 至 1000000，停止、断开或释放后不再发送）；收到的帧（上限 200）；发送历史（上限 50）；统计每秒刷新，显示 `ConnectionStatistics` 的全部字段。子类只负责创建库的通道、映射参数与传输特有的目标。
  - TCP 客户端页：主机、端口、本地地址与端口（0 由系统选择），以及分帧、关联、心跳、重连、超时、套接字（含 KeepAlive）、TLS（证书只保存环境变量名）、握手。
  - UDP 页：定向（固定远端）与非定向（每次指定目标，`SendToAsync` / `RequestToAsync`）；`EnableBroadcast`、组播组、TTL、组播回环、地址复用、请求重试次数、数据报上限。非定向没有固定对端，内置心跳被库拒绝，页签用一行文字说明原因。
  - 串口页：端口列表可刷新，不预选、不自动打开；波特率、数据位、校验、停止位、流控、DTR、RTS、读写缓冲、OpenTimeout；默认分帧为空闲间隔（界面提示 USB 转串口的延迟）；启用心跳时必须填写期待的应答（库在构造期校验，界面说明）。
  - 参数的保存与导出：`TcpClientSettings`、`UdpSettings`、`SerialSettings` 登记在 `SessionSettings` 的多态列表里（DA-D19），`TcpClientSessionViewModel`、`UdpSessionViewModel`、`SerialSessionViewModel` 实现 `CaptureSettings` / `ApplySettings`。
  - 对 Task 4 之前的代码的修正（由本 Task 的测试发现）：
    1. 发送与请求命令允许同时在途（`AsyncRelayCommandOptions.AllowConcurrentExecutions`）。Keyed 关联的目的就是让请求交错，原来一个请求未完成时按钮不可用。
    2. 定时循环的开始与停止命令的可用状态随运行状态刷新（原来不刷新，运行中仍可再开一个循环，旧循环随之无法停止）。循环持有自己的取消源，只有仍是当前一次运行时才复位状态；循环中的其他异常记入操作记录，不逃出后台任务（U7）。删除了无用的 `_periodicTask` 字段与 `await Task.CompletedTask`。
    3. `ReceiveAsync` 收到的帧显示在帧列表中（原来只记录操作）；请求的帧列表原来没有上限，现在与接收共用同一个 `ShowReceived`。
    4. UDP 的参数补齐 `TargetHost`、`TargetPort`、`RequestRetryCount`、`MaxDatagramSize`（原来不保存，读回后非定向的目标变成默认值）。
- 测试：`DebugAssistant.Tests` 共 209 条，全部通过。`JUNEVY_SERIAL_PAIR=COM20,COM21` 时串口测试执行；未设置时跳过（已验证）。
  - TCP（`TcpClientPageTests`，15 条）：6 种分帧各收发一次，对库的 `TcpServer`，两端使用同一份分帧映射，回显与负载逐字节相同；Sequential 两个请求各得自己的应答；Matcher 跳过并保留不匹配的帧；Keyed 两个交错的请求：服务端收到两个请求后先回复键 2，过 300 毫秒再回复键 1，键 1 的请求在键 2 完成时仍未完成（按到达顺序配对时不成立）；接收一帧，且被 `ReceiveAsync` 取走的帧不再经 `FrameReceived` 重复显示（库的语义，已锁定）；握手成功与应答不符；心跳失败进入 Reconnecting，应答恢复后回到 Connected；统计文字包含全部字段；参数校验失败时拒绝连接且不创建通道。
  - UDP（`UdpPageTests`，5 条）：定向请求与应答；非定向 `SendToAsync` 投递到目标；非定向 `RequestToAsync` 取得应答；非定向使用内置心跳被拒绝并给出原因；广播（`BroadcastFact`）。
  - 串口（`SerialPageTests`，3 条）：端口列表不预选、不打开；缺少期待应答的心跳在打开端口之前被拒绝（用不存在的端口名证明，错误是心跳规则而不是"找不到端口"）；`SerialPairFact` 的真实请求与应答，COM20 ↔ COM21。
  - 定时循环（`ChannelPeriodicTests`，3 条）：按次数结束且之后不再发送；停止后不再发送，开始与停止命令的可用状态随之切换；断开后不再发送。
  - 参数往返（`ChannelSettingsRoundTripTests`，3 条）：TCP、UDP、串口的每一节都设为非默认值，经工作区文件写出、读回并应用到新页签后，重新导出的 JSON 相同。
  - 视图（`ChannelPageViewTests`，3 条）：三种传输页的 XAML 能加载，绑定到各自的视图模型，日志面板绑定到页签自己的 `LogPanel`。`SessionViewTests` 改用共享的 `VisualTree` 辅助方法。
  - 稳定性：`Protocols.Channels` 与 `Views` 共 75 条连续运行 3 次，每次全部通过。
- 偏差与需要审阅者决定的事：
  1. 工作区格式：UDP 参数补齐的字段没有升级 `schemaVersion`（尚无发布版本，Task 3 的工作区文件只在本机开发环境里出现过）。请审阅者确认；如需升级，再加版本迁移。
  2. 发送与请求同时在途：计划没有写明，这是 Keyed 关联（计划第 1 节）的需要。界面上两个请求可以同时等待，应答按键配对。
  3. 串口与 UDP 心跳的规则由库在构造期检查，消息只有英文（摩擦 F-8）；页签用一行中文说明同一规则。
  4. 统计文字比计划多显示最后收发时间与连接时刻（`LastSentAt`、`LastReceivedAt`、`ConnectedSince`），因为计划要求显示全部字段。
  5. 广播测试依赖本机环境：`BroadcastFactAttribute` 在发现测试时探测本机能否把广播投递到本机的监听端口，不能则跳过并写明原因。本机探测通过，测试执行，没有跳过。
  6. 连接前的校验（`ChannelSettingsEditor.IsValid`）是本 Task 新增的：计划 U7 要求输入校验，这里补上"连接时拒绝"。
  7. 完整解决方案构建（Debug 与 Release）与 `build/test-affected.ps1` 留到门槛 1（Task 6 之后）。本 Task 只构建并测试了调试助手的测试项目（Debug，0 错误、0 警告）。

### 33.4 基线与度量（执行者填写）

基线（改动前，提交 `fe82d79`），由 `build/metrics` 的脚本统计，生产代码范围为 Core、Channels、Tcp、Udp、Serial、Plc、Melsec 的 `*.cs`（213 个文件，排除 obj、bin）：

| 指标 | 基线 |
|---|---|
| 重复窗口（`dupscan.py 6`，各行窗口数之和） | 24（与 P2.5 结束时一致） |
| 超过 60 行的方法（`methodlen.py 61`） | 0 |
| 有效行（`effective_lines.py`）：Core 1103、Channels 4765、Tcp 2418、Udp 561、Serial 386、Plc 1283、Melsec 1247 | 合计 11763 |
| 构建警告（Debug 与 Release） | 2（均在旧工具 `Junevy.Communication.Test`） |
| 测试（`test-affected -All -Repeat 1`） | 通过 7204、失败 2（见 Task 0 的说明）、跳过 8 |

新项目的度量在 Task 21 填写。

## 34. 审阅者的验收清单（执行者据此自查）

- [ ] 第 0 节每条决策都已落实，偏差都写在第 33.3 节，并已得到审阅者同意。
- [ ] 第 7 节功能矩阵：逐行可以操作，并能在报文、日志或操作记录中观察到效果。
- [ ] 改名与关闭（DA-D8）在 19 种页签上都有效；工作区往返覆盖 19 种；密码不落盘。
- [ ] 模拟器：黄金向量逐字节一致；端到端矩阵全绿；串口只用了 COM20 / COM21。
- [ ] 库项目的生产代码 diff 为空（DA-D15）；没有新增 `InternalsVisibleTo`。
- [ ] 第 3 节 Q1–Q20 与 U1–U8；架构测试（U2、DA-D19）先做过一次负控。
- [ ] 构建 0 警告；`test-affected -Repeat 3` 三遍全绿，所有失败都有记录。
- [ ] 文档：Tools README、根 readme、设计文档第 3 节、CHANGELOG、Skills 检查、知识库（只写不提交，汇报中附 `git status` 与 `git diff --stat` 的输出）。
- [ ] 手动验收的截图齐全。

## 35. 验收记录

### 35.1 各 Task 验收（审阅者填写）

（空）
