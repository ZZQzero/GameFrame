# 网络模块实施记录

设计契约见 [NetworkDesign.md](NetworkDesign.md)。本记录区分已实现代码、已执行验证和待实现项，不将协议单元测试视为完整网络验收。

## P0/P2 当前进度

2026-10-11 已实现以下独立部分：

- `Runtime/Network/Protocol/FrameCodec.cs`：V1 32 字节固定头、小端读写、分片头预检、精确消息长度、方向、CorrelationId、系统错误响应与通道边界。
- `Runtime/Network/Protocol/ControlCodec.cs`：六种系统控制包的正文编码与解析，WSS 空 ticket、KCP 1～1024 字节 ticket，Hello hash/限额/能力/心跳校验及 catalog 最大正文的协商检查。解析借用输入 Span，不向业务交付借用缓冲。
- `Runtime/Network/ProtocolContracts/NetProtocolContracts.cs`：字符串/数组限额属性和固定硬预算。属性构造校验不替代后续生成工具的完整 schema 分析。
- `Runtime/Network/NetworkException.cs`：协议、背压、请求超时与远端系统错误类型；未知非零远端码保留原值。
- `Runtime/Network/NetworkOptions.cs` 与 `NetworkConnectOptions.cs`：宿主/连接预算、可信端点、连接参数与 grant 单调期限校验；WSS 已执行帧/队列/pending 配额，KCP 专用配额随 P3/P4 接入。
- `Protocol/Common`、`Session`、`World`、`Diagnostics` 与生成的 `Hotfix/Network/Generated/GameNetMessages.cs`：18 个显式顺序 MemoryPack DTO、1 个 int32 ResultCode 枚举、14 个路由。账号与世界/联调协议分域；授权改名为 RequestWorldGrant。WorldEntityEntered/Left 明确区分实体成员事件，实体带 incarnation，位置按毫米量化；MoveInput 使用 inputSequence/clientTick，并增加世界会话与实体代际。WSS 可靠输入/快照与 KCP 不可靠路由分别登记，复用 DTO/codec。
- `Runtime/Network/Messaging/NetProtocolCatalog.cs`：不可变强类型路由、codec 与 catalog，验证重复 opcode、目录路由归属与最大正文。
- `Runtime/Network/ProtocolContracts/NetWireReader.cs` 与生成的验证器：反序列化前检查对象布局、数组/字符串长度、UTF-8、深度/节点预算与尾随字节；序列化前检查 DTO 约束。
- `Tools/NetworkProtocol`：以 Proto 为唯一手写源，使用 protobuf-net.Reflection 3.2.52 解析、生成 C# DTO/枚举，使用 Roslyn 4.14.0 和 MemoryPack.Generator 1.21.4 编译 DTO 与完整生成代码，分析实际成员并核对序列化信息；输出强类型 codec/catalog，可选导出规范 manifest、SHA-256 与 54 组默认/普通/最大值向量；`check` 校验生成产物一致性。支持顶层 int32 枚举与枚举数组，入站/出站检查声明值集合，定义纳入 hash；拒绝别名、Flags、Nullable<T>、递归、缺少限额、重复 opcode、自定义 formatter 与序列化回调。目前限于顶层非泛型 DTO 和一维非交错数组。
- `DotNet/DotNet.csproj`：通过 `Protocol/GameFrame.Protocol.projitems` 编译相同生成源码与纯协议基础代码，MemoryPack 1.21.4 依赖锁文件已保存。入口现已实现显式 local-wss Kestrel 服务，见下方 P1 进度。
- `Tools/NetworkProtocol.Tests`：链接相同 Runtime 源码与协议测试，使用 .NET 10；Tools/DotNet 分别锁定 SDK 10.0.401，测试依赖锁文件纳入版本管理，不改变 Luban 的 .NET 8 环境。

协议工具清理：命令入口与 MemoryPack 代码生成实现已拆分，后者命名为 `MemoryPackProtocolGenerator`；连接配置和帧控制代码移至测试工程编译，生成工具只链接协议契约、异常与 catalog。参数快照测试移至独立的 `NetworkOptionsTests`，生成产物与协议格式不变。这里的代码生成实现不代表 DotNet 服务端已实现。

辅助产物已移至仓库根 `Protocol/Generated/`。默认 generate/check 只处理两个正式 C# 文件，`--artifacts` 才处理 manifest/hash/vector；正式代码生成在内存中计算规范清单与 hash，不生成测试向量。两种模式的 C# 输出一致，运行时指纹内嵌于 catalog，不读取辅助文件。Unity/.NET 向量测试均读取外部 `golden-vectors.json`。

## 设计对照与本次继续实施（P1）

2026-10-11 完整核对 Runtime、Hotfix、生成工具、共享协议和 DotNet 后，按设计阶段记录实际差距：

| 阶段 | 当前状态 | 仍缺少的工作 |
| --- | --- | --- |
| P0 | 固定帧、控制、选项、异常、协议同源已实现 | 实际交互的全部阶段/故障矩阵仍需补足 |
| P1 | 桌面 WSS 客户端与实际 local-wss 服务已实现，登录/恢复/Echo/注销可联调；已补宿主故障与驱动丢失收尾 | 完整元数据计账审计、剩余故障注入/容量矩阵、Unity 原生 TLS 实联验收 |
| P2 | Proto→MemoryPack、强类型路由、验证器、hash/check、跨端向量、有界 writer 与 CI 工作流已实现 | 完整生成负例/字段差异报告、远程 CI 实际执行、HybridCLR/AOT formatter 验证 |
| P3 | KCP #467、安全补丁、精确源码准备/manifest、KCP 全部分配逐点注入已实现 | mbedTLS 版本/SHA 锁定与分配钩子、gfnet ABI/fatal/listener、native 构建安装 |
| P4 | 未实现 | KCP/DTLS transport、worker、两通道、grant/撤销链路 |
| P5 | GameRuntime 可选网络配置/宿主所有权已实现 | GameNetworkSession、角色权限、世界 owner、入场/离场/恢复、AOI/移动校验 |
| P6 | 当前 Editor/Player 脚本编译可验证 | WebGL .jslib、IL2CPP/设备、代理/IPv6/后台、容量/soak/发布 |

本次新增：

- `Runtime/Network/NetworkRuntime.cs`、`NetworkClient.cs`、`Connection/ConnectionContext.cs`：每次连接独立 context/generation，主线程业务 API、DTO 编解码与任务完成；持久根上的 Pump，各客户端每轮 16 条可靠工作，全局条数/时间预算。故障拒绝 admission，Close/Dispose 等待 IO 引用解除和队列释放后才允许复用或注销；订阅可跨 Close 保留。
- `Transport/OwnedFrame.cs`：精确容量数组、父/子缓冲预算、有界 IBufferWriter；在途缓冲与头扩容旧＋新峰值计账，释放清零全部容量，不保留空闲池。`MaxIdlePoolBytes` 当前实际使用为 0。
- `Transport/WebSocketFrameTransport.cs`：使用平台成熟 WebSocket 实现，头齐全先校验阶段、路由和控制包精确长度再申请正文，按 remaining capacity 收分片；拒绝文本、截断、尾随/拼接、未结束的声明边界。生产无证书忽略钩子。
- `Connection/PendingDeadlineHeap.cs`：共享可删除 deadline 堆，admission 起计时；IO 在截止前保留响应并摘除堆节点，调用取消可在主线程完成前获胜；完成槽保留至主线程交付。未知/迟到响应不解码、不建立过期 ID 集合；响应/Push 按可靠到达顺序派发，远端关闭标记在此前可靠帧之后。
- 有界发送/接收/控制队列，控制优先；显式取消阻止未移交请求，WSS 不提供不可靠能力。排队年龄超限的 RPC 以背压结束，普通 Send 的后续失败故障连接；指标计入帧数、队列/堆/pending、取消/超时/迟到、实际帧缓冲与年龄告警。
- `DotNet` 已从 Hello World 转为 Web SDK/Kestrel：显式 localhost HTTPS local-wss，认证配置和证书必填；没有生产认证 provider 就拒绝生产启动。Origin 精确 allowlist，不从 query/header 接受账号凭据；Hello hash 不符先 Reject；5 秒 Hello、10 秒登录期限、阶段白名单、连接/IP/账号/字节包数额度、控制心跳均执行。一个接收 owner、发送 owner、业务 owner；账号提交不持锁等待 IO。
- local Login/ResumeLogin/Logout/Echo：单本地账号，Login 顶号推进 epoch；Resume 原子轮换摘要凭据、保持账号会话/epoch；Logout 先撤销再响应/关闭。期限最长 24h，恢复不续期。未安装世界网关时 Grant 返回 WorldUnavailable，不签发无效票据。
- `GameRuntimeConfig.NetworkOptions` 默认 null；`GameRuntime.Network` 未启用时明确异常。网络共享既有 root，Shutdown 在 Timer/root 释放前等待网络关闭，不修改 Launch 的业务入口，也不自动连服。新增使用说明 `Docs/Network.md` 和服务说明 `DotNet/README.md`。
- 宿主 Pump 异常或 Runner/root 提前销毁、禁用会锁定故障、停止正常派发，所有客户端失败并拒绝新连接；独立 PlayerLoop 清理不依赖 Runner，不解码 DTO/执行 Push。保留响应也失败，Close/Dispose/Shutdown 等待实际 IO 退出仍可完成；故障只在宿主边界记录一次。另修复 Connected 观察者主动 Close 后 Connect 仍误报成功的竞态。
- 补齐控制发送的 15 秒期限，队列与 in-flight 均受约束；服务端 Ping admission 不再 await 慢 Send 而阻塞 watchdog，发送 owner 成功后提交 Pong deadline。客户端发送 owner 在 WS Send 前重查请求取消/截止时间，不依赖下一次 clock tick；在期限上或之后到达的响应同时计入 timeout/late，且不解码。
- `.github/workflows/network.yml` 对相关改动执行 locked restore、协议漂移检查、.NET 协议/WSS 回归与独立 KCP native 回归；不自动 generate 掩盖差异。工作流已写入源码，本地对应命令验证；尚未推送或宣称远程 CI 已执行。
- KCP 构建副本安全补丁及 `Native~/build/prepare_kcp.py`：精确行号/上下文、源/补丁/结果 SHA 校验，保留原 CRLF 资产与 GUID；ACK/segment/发送分配失败返回而非 abort/assert/空指针，另修复上游队列偏移宏的 UBSan 问题。失败后的部分状态必须由未来 gfnet 锁 fatal 再销毁，不重试。临时 source manifest 不代表 native 插件构建清单，DTLS/gfnet 尚未实现。

当前资源指标可靠覆盖拥有的帧数组，pending 采用每条 512 字节固定 reservation；CLR/Unity/IL2CPP 的完整 pending、取消注册、completion、队列元数据布局及常驻目录/观察者仍需审计，不把当前指标宣称为完整托管内存验收。ClientWebSocket 不暴露底层 TCP socket buffers 的设置/真实值，当前未伪造 256 KiB 设置成功；平台内存仍待实测。

设计中的生产账号/角色/世界服务尚不存在。已就下一步是否先实现 local 内存联调世界向用户询问；当前未自行添加角色权限或世界模拟策略。P1/P2 的完整验收与 P3～P6 均不能因以下通过用例而标为完成。

世界协议已按 NetworkDesign 第 14 节补齐两种通道、实体代际和独立量化快照。当前示例入场/恢复返回最多 64 个实体的完整 AOI 基线，超过明确拒绝；快照每包最多 16 个实体，缺席不表示删除。成功恢复推进 worldEpoch 并重置输入/快照序号，可靠 eventVersion 保持连续。以上是已生成的协议与业务契约，实际 AOI、恢复处理、世界权限校验与 GameNetworkSession 仍待实现；不以字段与文档代替服务端功能。

## 已确认的业务输入

用户确认第一版本地联调使用账号＋密码，只在显式 local profile 启用；生产未配置认证时拒绝启动。生产账号校验服务与部署域名/证书尚未确定。

## 已解决：可空数值编码

核对 MemoryPack 1.21.4 的 [NullableFormatter](https://github.com/Cysharp/MemoryPack/blob/1.21.4/src/MemoryPack.Core/Formatters/NullableFormatter.cs) 后发现：对不包含引用的 T，Nullable<T> 通过 DangerousWriteUnmanaged 原样写入内存，包含布局填充，而不是固定的 HasValue 字节加紧密排列的 Value。

在 .NET 10.0.401/macOS arm64、NuGet MemoryPack 1.21.4 下，用相同 `int?` 值 42 验证：

```text
初始编码：                   010000002A000000
仅改填充字节后的同值编码：   01AABBCC2A000000
null int?：                  0000000000000000
long? 42：                   01000000000000002A00000000000000
```

复现通过 Unsafe.As 修改 Nullable<int> 中 Value 之前的三个填充字节，数值仍为 42。该实验说明填充参与线上格式；不是声称所有普通构造都会产生非零填充，也不是已经证实所有发布平台布局不同。

用户确认将 Nullable<T> 移出 V1 白名单，改用显式 HasValue＋Value 或可空 DTO；设计第 8.1 节已同步修改。协议工具必须拒绝 Nullable<T>，不能自行增加 Formatter 或静默按平台布局解码。

## 实际验证

2026-10-11：

- .NET 10 测试 178/178 通过，包含 73 个协议/参数用例、67 个 MemoryPack/catalog 用例与 38 个工具工程用例；默认与 `--artifacts` 的 C# 一致性及默认模式不写辅助文件检查通过，`check --artifacts` 通过。
- Unity 6000.3.19f1 Editor 编译通过，新增 Console 错误为 0；EditMode `GameFrame.Tests.NetworkProtocolTests` 与 `NetworkMemoryPackTests` 共 140/140 通过。
- Unity 的 MemoryPack.Core .NET Standard 版本与 .NET 10 对 54 组向量输出字节一致；枚举固定四字节小端与未知值拒绝、枚举别名/可空/收窄拒绝、枚举定义改变 hash、WSS/KCP DTO codec 复用、恶意数组/字符串声明、无效 UTF-8/字符计数、根/元素空值、尾随字节、catalog 初始化与 hash 复制检查通过。
- DotNet .NET 10 项目编译通过，包含同源生成协议；不代表服务端功能已完成。
- StandaloneOSX Player 脚本编译通过，共 43 个程序集，包含 GameFrame.Runtime 与 Game.Hotfix。仅验证脚本编译，不代表原生 IL2CPP 打包或真机通过。
- 用例包含设计发布的 Ping 小端向量、截断/尾随/拼帧、未知头字段、声明长度溢出、32 KiB/1150 字节边界、方向/握手限制、WSS/KCP ticket、协商 hash/路由上限、控制正文保留位，以及 seed=467 的 4096 次随机短包。
- 测试前后均为 `Assets/Scenes/Launch.unity`、未播放；场景未修改。EditorSettings 的 enterPlayModeOptionsEnabled=false、enterPlayModeOptions=None 保持原值。
- 此项为协议阶段历史验证；本次 P1 已新增 PlayMode 和实际 Kestrel/WSS 测试，见下节。原生插件/UDP、IL2CPP、真机、24 小时 soak、容量仍未验证。

从仓库根目录执行已有测试：

```sh
(cd Tools && dotnet restore NetworkProtocol.Tests --locked-mode)
(cd Tools && dotnet run --project NetworkProtocol -- generate --artifacts)
(cd Tools && dotnet run --project NetworkProtocol -- check --artifacts)
(cd Tools && dotnet test NetworkProtocol.Tests --no-restore)
```

Unity Test Runner 选择 `GameFrame.Tests.NetworkProtocolTests` 与 `GameFrame.Tests.NetworkMemoryPackTests`；不能将空测试结果算作通过。


## P1 本次实际验证

- `Tools/NetworkServer.Tests` 实际 Kestrel HTTPS/ClientWebSocket 20/20 通过：登录/Echo/注销、单次 Resume/轮换、顶号旧 epoch/凭据拒绝、登录失败响应先于 Close、未登录恶意 DTO 拒绝、登录期限不被 Ping 延长、hash/Origin/证书错误、逐字节分片、超长/文本拒绝；相同客户端核心验证主线程 Pump 才完成、后台 deadline、迟到丢弃、保留响应后取消、pending 占位、显式关闭/实际 IO 退出及帧预算归零、拒绝响应后的对端关闭确认、无生产认证拒绝启动。
- Unity EditMode 网络组 143/143 通过（原协议/MemoryPack 140 加 3 个缓冲预算/清零/writer 用例）。
- Unity PlayMode 31/31 通过（NetworkLifecycleTests、GameRuntimeTests、LoopAndRootTests）：离线不开网络、网络 root 所有权、宿主关闭/Dispose、客户端配额释放、单 Push owner/旧句柄隔离、主线程限制、可信地址 admission。PlayMode 未向真实 WSS 登录，不能代替 Unity TLS/IL2CPP 验收。
- 测试临时 TLS 证书只在测试客户端信任本次精确证书，没有修改系统信任。测试前后场景 Launch.unity 未保存改动；恢复测试引起的字体 atlas 和 EditorSettings 文件变化，保留编辑器测试前的运行设置。
- 本次最终 Editor 编译与 Console 检查通过（0 错误）；StandaloneOSX Player 脚本编译通过，43 个程序集，临时输出已清理。此项不代表原生 IL2CPP 构建或真机。
- 原有 .NET 协议/生成测试 178/178 通过，`check --artifacts` 通过；新增 .NET 集成测试 20/20 通过。新工程锁文件已保存。

## 本次继续补齐与验证

- .NET 真实 WSS 回归新增宿主故障覆盖 reserved/远端正常关闭标记，以及 Connected 观察者关闭时的 Connect 结果；实际 Kestrel/WSS 23/23 通过。原协议/生成测试 178/178 与 `check --artifacts` 通过。
- Unity NetworkLifecycleTests 8/8 通过，新增删除 Runner（含连接中等待）、禁用 root、全客户端故障、首个异常保留、禁止 admission、Close/Shutdown 不挂起、真实帧预算归零；既有离线/配额/订阅/线程用例通过。
- macOS arm64：KCP 精确补丁防漂移、75/75 分配成功/逐点失败用例通过（assert/NDEBUG/ASan+UBSan），所有失败实例释放到零；准备后源码 ACK 24/24、tick 边界 3/3 通过，无 sanitizer 错误。原 C/H 资产字节与 GUID 未变，临时产物自动删除。未生成/安装 gfnet，也未验证 DTLS。
- 可控 WebSocket/单调时钟测试覆盖 RPC 截止前/等于/后、reserved 后跨 deadline、迟到恶意 DTO 不解码、未移交过期 RPC 不发送、控制优先、慢业务/控制发送的 15 秒硬期限、匹配外 Pong 与从发送完成起算 Pong deadline；服务端同样验证 watchdog 不被 Ping 发送等待挂住。计时输入由测试控制，guard 只限制测试实际等待。最终 Tools/NetworkServer.Tests 37/37 通过：23 个实际 Kestrel/WSS、10 个可控客户端边界、4 个可控服务端心跳用例。
- 最终 Unity 6000.3.19f1 Editor 编译通过、Console 错误 0；StandaloneOSX Player 脚本编译通过（43 个程序集）。场景仍为 Launch.unity、未播放、无未保存修改；恢复测试引起的 EditorSettings 文件与原内存设置，所有本次临时编译/验证目录清理。未执行 IL2CPP、设备或远程 CI。
