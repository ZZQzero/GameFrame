# 网络模块实施记录

设计契约见 [NetworkDesign.md](NetworkDesign.md)。本记录区分已实现代码、已执行验证和待实现项，不将协议单元测试视为完整网络验收。

## 当前底层实现（2026-10-12）

按 C KCP/DTLS 方案完成现有工具链支持的底层实现，Windows 按用户确认交给 CI。游戏世界会话业务、WebGL WSS 延后；本记录不将构建成功视为全部平台验收。

| 部分 | 实际交付 | 剩余验收或范围 |
| --- | --- | --- |
| 共用连接核心 | 修复远端有序 Close 遮蔽派发故障、Closing 重入和旧收尾覆盖新连接；WSS/KCP 复用帧、RPC 和 deadline | WSS 客户端完整关闭确认、全故障/容量矩阵 |
| C gfnet | 固定 ABI 1、DTLS1.2 PSK AES128-GCM、cookie/防重放、双 channel、KCP 固定 tick、有界 allocator/队列、fatal latch | 完整弱网、长时间和所有平台内存压力 |
| listener | 有界凭据注册/撤销、单次 ticket 摘要绑定、握手竞争、终态事件/release；关闭 child 保留共享 socket | 生产授权 provider、账号 epoch 仲裁由业务接入 |
| C# 客户端 | 宿主单 KcpWorker、非阻塞 UDP、头校验后正文分配、独立不可靠队列、取消/deadline 移交前重查、owner 销毁后归还 allowance | Unity IL2CPP 与设备运行 |
| .NET host | KcpListenerHost 单 owner 和有界业务队列、local-kcp-dtls Echo、来源/认证后输入限流、独立心跳期限 | 不签发生产/世界凭据；RequestWorldGrant 仍为 WorldUnavailable |
| 构建安装 | mbedTLS 3.6.7 SHA/tree 锁、CMake、build/install、源码/工具链/产物清单、许可证及 Unity importer | Windows/Linux CI 已配置但尚未远程执行 |
| 现有平台产物 | macOS universal、Android arm64、iOS device arm64、独立 simulator arm64 已构建安装 | 移动产物仅构建/清单验证；simulator 默认禁用以避免重复链接 |
| 计账和诊断 | native 实际/峰值精确容量；客户端 native allowance 预留；帧/pending 各额外 512 字节保守 reservation；KCP/socket 快照 | CLR/Mono/IL2CPP 完整元数据审计、第17节全部阶段指标、平台内部缓冲和 RSS |

代码入口：`Runtime/Network/Transport/Kcp`，原生源码与 ABI 在其 `Native~`；服务端入口在 `DotNet/Network/KcpListenerHost.cs`、`LocalKcpService.cs`。公开调用方式见 [Network.md](Network.md)，工具命令见 [原生 README](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/README.md)。自动生成消息没有新增第二套手写协议。

原生分配器计入真实分配头/容量，释放前清零；KCP 部分发送、分配或 output 失败锁 Fatal 后销毁。listener UDP 输出带 connectionId/generation，托管移交前检查，包括尚未发布 Established 的握手子连接。DTLS Established 不等于应用 Connected；GFNP Hello/hash/ticket 绑定成功后才提交 ServerHello。

本地来源策略：共享 socket 每轮读取最多 256 包/2ms；按 IP（不按端口）每秒最多 2048 datagram/2MiB，最多记录 1024 个 IP，每秒清空固定窗口，表满拒绝新增来源、不驱逐已有来源。认证后仍每连接 128 帧/128KiB 每秒。这些仅是联调策略，未知来源/碰撞流量和 NAT 聚合仍须容量测试；不宣称为生产抗攻击验收。Ping 排队独立受15秒控制期限约束，Pong 的15秒期限从 native 接受 Ping 开始；匹配但过期的 Pong 不能延长存活。

诊断没有伪造库不可观测项：TimeoutRetransmits 仅为 KCP 超时重传，不包含快速重传；DTLS 细分坏记录/重放计数标记不可用。C# 帧 reservation 为保守准入计账，不是精确对象布局。

### 本次验证

- macOS arm64 native harness：1303 个 REQUIRE 检查通过，ASan/UBSan 无错误，释放后 native live bytes=0；macOS universal Release 的 native ctest 通过。覆盖 ABI、错误 PSK、篡改/重放、非法加密 channel/KCP 尾随字节、最大帧/窗口/ACK、owner 线程、提前 tick、输出队龄、分配注入/部分 send、cookie、竞争绑定/撤销和终态释放。
- .NET 网络测试 46/46 通过：既有 WSS/连接/心跳 39 个、真实 UDP 5 个和来源限流 2 个；UDP 覆盖 Hello/Echo/释放、child关闭隔离、暂停Pump跨心跳周期/deadline、撤销后新grant及同一DTLS上的不可靠Notify/Push。最后的不可靠资源竞争与心跳修正另复跑对应 UDP 组5/5。
- Unity 6000.3.19f1 Editor 编译通过、Console 错误0；协议/MemoryPack EditMode 140/140，帧缓冲 EditMode 3/3，连接生命周期 PlayMode 10/10，Unity Mono 实际 UDP Hello/Echo/close 1/1。
- StandaloneOSX Player 脚本编译通过，43 个程序集；不代表 IL2CPP 原生链接实跑。macOS 库含 arm64/x86_64，动态依赖仅系统 libSystem，公开导出仅 gfnet_*；Android/iOS/simulator 使用现有 NDK/Xcode 构建并经安装器验证清单。四份已安装产物的build_id/源码/hash核验通过，sanitizer与篡改清单安装均拒绝。
- local-kcp-dtls实联服务在grant已过期后Ctrl+C退出码0；关闭由listener整体释放，不重复撤销已释放grant。测试后恢复Launch.unity/未播放/无场景改动、EditorSettings原设置，临时凭据/编译目录和日志清理。
- Windows 使用 network.yml 的 windows-latest/MSVC job，另有 Linux/macOS host job，各自执行 native 和实际UDP测试并发布产物。工作流未推送/远程执行；未在本机生成 Windows DLL。
- 未执行：最终 IL2CPP Player 构建/裁剪与运行、移动真机、IPv6/代理/后台/换网、24h soak、生产容量、完整元数据审计及全部细分诊断。世界账号/角色/AOI/恢复业务和 WebGL WSS 为用户确认延后项。

以下2026-10-11条目保留为历史记录；当前状态以上述2026-10-12实施和验证为准。

2026-10-11 按用户选择整理 [C KCP / DTLS 底层实施方案](NetworkNativeDesign.md)：C 负责 KCP/mbedTLS 与有界内存，C# 负责 UDP/owner 驱动并复用现有协议/RPC；明确 listener Hello 绑定、原生 allowance、各平台构建安装和分步验收。此项仅为设计交付，未新增 gfnet/DTLS 实现或生成安装产物，游戏世界业务与 WebGL WSS 延后。

同日文档复核补齐：服务端共享 UDP socket 的单连接关闭边界、Hello 绑定前准入、native 唯一 tick 调度、队列交接与分片预检、事件接管/终态槽、裸 handle 销毁范围和不可观测指标标记。合并 NetworkDesign 中重复的 KCP 修正说明，参数继续以总设计为准。仅修订文档并检查链接/空白，未执行新的编译或运行验收。

## P0/P2 历史进度（2026-10-11）

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

| 阶段 | 当时状态（2026-10-11） | 仍缺少的工作 |
| --- | --- | --- |
| P0 | 固定帧、控制、选项、异常、协议同源已实现 | 实际交互的全部阶段/故障矩阵仍需补足 |
| P1 | 桌面 WSS 客户端与实际 local-wss 服务已实现，登录/恢复/Echo/注销可联调；已补宿主故障与驱动丢失收尾 | 远端关闭与派发故障仲裁、Close 回调重入、完整元数据计账和诊断指标、剩余故障注入/容量矩阵、Unity 原生 TLS 实联验收 |
| P2 | Proto→MemoryPack、强类型路由、验证器、hash/check、跨端向量、有界 writer 与 CI 工作流已实现 | 完整生成负例/字段差异报告、远程 CI 实际执行、HybridCLR/AOT formatter 验证 |
| P3 | KCP #467 与分配安全修正均已直接合入源码，hash 校验复制/manifest、KCP 全部分配逐点注入已实现 | mbedTLS 版本/SHA 锁定与分配钩子、gfnet ABI/fatal/listener、native 构建安装 |
| P4 | 未实现 | KCP/DTLS transport、worker、两通道、grant/撤销链路 |
| P5 | GameRuntime 可选网络配置/宿主所有权已实现；用户确认当前只做框架底层 | GameNetworkSession、角色权限、世界 owner、入场/离场/恢复、AOI/移动校验延后，不阻塞当前底层实施 |
| P6 | 当前 Editor/Player 脚本编译可验证；WebGL WSS 已确认延后 | IL2CPP/设备、代理/IPv6/后台、容量/soak/发布；第一版不包含 WebGL WSS |

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
- KCP 分配安全修正与 `Native~/build/prepare_kcp.py`：最初通过精确补丁应用到构建副本，后按用户要求直接合入 `ikcp.c`/`ikcp.h`，删除补丁文件与应用逻辑；源码准备改为当前 SHA 校验与逐字节复制，保留 CRLF 和 GUID。ACK/segment/发送分配失败返回而非 abort/assert/空指针，另修复上游队列偏移宏的 UBSan 问题。失败后的部分状态必须由未来 gfnet 锁 fatal 再销毁，不重试。临时 source manifest 记录上游基线与已合入修正，不代表 native 插件构建清单，DTLS/gfnet 尚未实现。

当前资源指标可靠覆盖拥有的帧数组，pending 采用每条 512 字节固定 reservation；CLR/Unity/IL2CPP 的完整 pending、取消注册、completion、队列元数据布局及常驻目录/观察者仍需审计，不把当前指标宣称为完整托管内存验收。ClientWebSocket 不暴露底层 TCP socket buffers 的设置/真实值，当前未伪造 256 KiB 设置成功；平台内存仍待实测。

### 再次代码审查（2026-10-11）

用户确认第一版不实现 WebGL WSS，设计与使用说明已同步，现有平台不支持分支保留。以下为当时审查发现的问题；前两项已在2026-10-12修复并新增回归，其余验收缺口保留：

- 已用两个临时可控客户端用例复现：可靠消息和远端系统 Close 在主线程 Pump 前到达，`ConnectionContext.Fail` 的 `!orderly` 条件会阻止后续 Push/解码故障提交。第一条 Push 抛异常后第二条 Push 仍执行；第一条保留响应解码失败后第二条响应仍成功，违反第 9.3 节故障边界。现已修复，回归通过；当时两个补充用例均失败，源测试文件逐字节恢复，临时结果已删除。
- 源码审查发现 `NetworkClient.CloseAsync` 在 `BeginClose` 同步通知 Closing 后才保存 `closeTask`，Closing 观察者重入 Close 时可创建多份 `FinishCloseAsync`。这些收尾都会无条件清空 `context`；其中一个 Close 完成后发起的新连接存在被另一份旧收尾清空的风险。现已先发布唯一关闭任务并检查收尾归属，Unity 重入回归通过。
- WSS 客户端收到系统 Close 后直接 Abort；主动 CloseOutput 后也未等待对端标准关闭确认。当前符合尽力发送及有界 Abort 的最小清理要求，但完整 WebSocket 关闭握手仍待补齐，不能将服务端的关闭确认测试当作客户端握手已完成。
- 当前指标尚未覆盖设计第 17 节的 WSS 应用帧字节、业务有效字节、Ping RTT、内存峰值和分阶段 RPC 耗时。帧缓冲预算通过不代表全部托管元数据计账完成。

此次复跑既有 WSS/连接测试 37/37、协议/生成测试 178/178 通过，`check --artifacts` 通过；上述两个临时补充用例暴露其未覆盖的故障顺序。未修改网络业务源码，未执行新的 Unity、IL2CPP 或设备验证。

设计中的生产账号/角色/世界服务尚不存在。用户已确认当前只实现框架底层，游戏世界会话业务延后，先前 local 内存联调世界的问题不再作为当前工作的待确定输入。底层可以使用受控凭据和 Echo 测试传输与授权生命周期，不实现角色权限或世界模拟策略。P1/P2 的完整验收与 P3/P4/P6 均不能因以下通过用例而标为完成。

世界协议已按 NetworkDesign 第 14 节补齐两种通道、实体代际和独立量化快照。当前示例入场/恢复返回最多 64 个实体的完整 AOI 基线，超过明确拒绝；快照每包最多 16 个实体，缺席不表示删除。成功恢复推进 worldEpoch 并重置输入/快照序号，可靠 eventVersion 保持连续。以上是已生成的协议与业务契约，实际 AOI、恢复处理、世界权限校验与 GameNetworkSession 仍待实现；不以字段与文档代替服务端功能。

## 已确认的业务输入

用户确认第一版本地联调使用账号＋密码，只在显式 local profile 启用；生产未配置认证时拒绝启动。生产账号校验服务与部署域名/证书尚未确定。

## KCP 修正直接合入源码（2026-10-11）

按用户要求，分配失败检查与标准 `offsetof` 修正已直接写入 `Unity/Assets/Plugins/GameFrame/Kcp/ikcp.c`、`ikcp.h`，与先前直接合入的 #467 共用同一份源码。源码字节与原安全补丁准备后的结果一致，CRLF 与两个 `.meta` GUID 保留。删除独立 patch 及构建时应用逻辑；`prepare_kcp.py` 简化为当前源码 hash 校验、逐字节复制与来源清单，拒绝漂移、Unity 内输出和覆盖。

两个原生入口在 macOS arm64 复跑通过：分配成功/逐点失败 75/75（assert/NDEBUG/ASan+UBSan）、ACK 24/24、tick 3/3；独立 ACK 修正回归 16/16、tick 4/4。ACK 对照只还原时间戳比较，保留相同分配修正，不再冒充原始上游源码。ASan/UBSan 无错误，存活分配归零，临时产物已清理。源码 hash/copy/漂移拒绝及元数据保留检查通过；未构建安装插件或执行 DTLS/Unity/IL2CPP 验收。

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


## C 原生传输落地与代码审查（2026-10-12）

本节为当前状态；前面的 P0/P1 与阶段表保留当时记录，不表示 KCP 现在尚未实现。

### 当前框架范围

- C ABI 1、mbedTLS 3.6.7 DTLS 1.2 PSK/AES128-GCM、cookie/防重放、配额分配器、可靠 KCP 与不可靠 channel 已实现。KCP #467 与分配安全修正已直接写入唯一 c/h 源码；build 校验后复制，不再应用独立 patch。
- listener 具备有限 grant/握手/连接表、一次性 ticket 绑定、撤销、id/generation 与终态事件；C# 客户端使用单宿主 worker、非阻塞 UDP 和原生主动轮询。.NET KcpListenerHost 独占共享 UDP socket 与子连接 native handle。
- 两种传输通过内部 IFrameTransport 复用 ConnectionContext 的 GFNP/Hello、pending/deadline、DTO 验证和主线程派发；local-kcp-dtls 仅提供显式临时 grant 与 Echo，不宣称提供账号到世界的授权业务闭环。
- macOS universal、Windows x64、Android arm64、iOS device arm64、iOS simulator arm64 已构建安装。Simulator 默认禁用，device/simulator 不能同时链接。安装器保留既有 GUID，校验当前源码、清单和二进制；WSS-only 不加载 KCP。

### Windows 与 Sqlite 的构建方式统一

复核 Sqlite 的 build.py、windows-llvm-mingw.cmake、verify_windows.py 和安装清单后，KCP 增加同样的显式 --llvm-mingw 交叉构建入口及独立 PE 检查器。当前使用 LLVM-MinGW 20260922 UCRT macOS universal，官方下载 archive 的 SHA-256 为 `52e5f5a7b131021d0c39a37a38fa380a1da7885cd04bd61afd0cd4ecfb8bc1f3`。

实际产物为 `Unity/Assets/Plugins/GameFrame/Native/Kcp/Windows/x86_64/gameframe_kcp.dll`：PE32+ x64、26 个 gfnet.h 声明的导出全部匹配，仅依赖 KERNEL32、bcrypt 与 Windows UCRT 系统 DLL。mbedTLS/KCP 静态链接；MSVC 配置使用静态 CRT。Windows 回归 exe 已交叉编译，清单记录 built=true、executed=false；不能在 macOS 把它算作执行通过。Windows/MSVC、Linux、macOS 原生与 UDP CI 已配置，远程 CI 尚未执行。

构建发布还修正了 multi-config 构建后的陈旧根目录 DLL 风险：每次重新复制本次 Release/Debug 产物；安装前先校验既有 metadata，再替换库，不因错误 GUID 先覆盖有效二进制。所有现有目标已按当前维护源码重新生成清单并安装。

### 本轮已修正的问题

| 问题 | 修正后的行为 |
| --- | --- |
| transport Dispose 抛异常可跳过 ioStopped 与缓冲释放，Close 永久等待；重复调用 Dispose | IO 最终收尾分别尝试 Dispose 和 StoppedTask，再释放队列并完成关闭；资源不重试，清理失败由 Close 传播 |
| 原生最终统计失败可跳过 destroy；destroy 失败却归还 allowance/报告零 | 统计和 destroy 分别尝试；保留最后有效统计，只有确认 destroy 成功才归还 native 预留并报告释放 |
| Client Close 失败可跳过订阅注销/宿主登记释放 | Dispose 仍解除订阅与登记，完成共享失败任务；重复 Dispose 保留同一结果 |
| Shutdown 在同步状态回调之后才建立共享任务；客户端失败跳过 worker shutdown | 先发布唯一任务再开始停机；始终尝试客户端与 worker 两部分清理 |
| worker 内部故障只终止后台线程 | Runtime Pump 将共享 worker 内部故障纳入已有宿主故障边界，拒绝新工作并完成已接受任务 |
| deadline 时刻的匹配 Pong 能抢先清除等待 | 客户端、WSS 服务端与 KCP listener 均以严格早于 deadline 为成功条件；Ping 间隔从实际发送接受时起算 |
| listener 的业务取消回调抛错会跳过其余清理；native destroy 返回值未检查 | 取消前登记 retired 所有权；最终清理尝试其余子连接、native、命令和应用任务，保留主异常；检查 destroy 返回值 |
| 每 10ms 复制连接列表分配新数组 | worker 使用固定容量快照数组并清空旧引用；listener 在仅 owner 修改的 child 表上直接遍历 |
| listener 控制输出只受整体预算限制 | Hello/Ping/Pong 使用独立 16KiB ControlMemory 与 64 项队列限额 |

### 结合设计保留的流程与后续工作

1. 保留“GameRuntime 持有宿主、业务持有客户端”的所有权。Launch/GameEntry 不添加网络反射入口，也不自动连接；未来 GameNetworkSession 放在 Hotfix，通过公开强类型 API 组合业务流程。账号/世界状态不下沉到 Runtime 或 C。
2. 保留 proto 唯一真源和双端编译同一生成 C#。生成工具的反射只在离线生成阶段分析 MemoryPack 布局，运行时使用生成 codec/catalog；不存在运行时按字符串反射派发消息的必要。固定 GFNP 控制包与业务 DTO 分开，避免协议工具承担传输状态。
3. 保留 C# socket + C KCP/DTLS、单 owner、主动轮询 ABI。让 C 再管理各平台 socket 或反向调用 Hotfix 会增加平台与生命周期成本，目前没有实测依据。当前 worker 的 10ms UDP 检查、native→Task→Context 的交接延迟应纳入压测；RPC 结果以 IO 层保留响应的时刻判定，不能按 OS 收包时间宣称成功。
4. 继续以关闭完成后的资源计数、原异常和主线程业务完成作为正确性标准。通道额度是共享预算下的上限，不能将各子预算求和当成额外可用容量；native 预留与实际使用分别展示，不能只用 RSS 判泄漏。
5. 完整托管 metadata/容器/任务成本计账、全部诊断项（包括派发时间、分位延迟和 DTLS 细分失败）、WSS 完整 close handshake 与更多确定性故障/容量矩阵仍需补齐。当前帧/pending/native 有界不等于已满足设计中的全部内存和诊断门禁。
6. IL2CPP 最终链接/裁剪、移动真机、IPv6/后台/代理/弱网、24h soak 与容量证据仍未完成。当前原生库可供接入和验证，不能标成所有平台发布验收通过。WebGL WSS 与世界业务按用户确认延后。

### 本轮验证证据

- Windows x64 DLL 交叉编译、PE/26 项导出/系统依赖检查与 Unity 安装通过；Windows exe 仅构建未运行，远程 CI 未运行。
- 现有 macOS/Android/iOS/device/simulator 产物按当前源码清单重新构建安装。macOS universal 的新 dylib SHA-256 与原服务端/Editor 使用的产物一致（c9562fe935029500a0a9f0073eba46a5a1a0d7cc1bc6322cf7b7534111677cd2），本轮未改变其二进制字节；替换为不同二进制时仍须重启 Unity。
- macOS arm64 native Debug＋ASan/UBSan 回归：1303 个检查通过，live native bytes=0；universal Release ctest 同样通过。
- Tools/NetworkServer.Tests：50/50 通过，无跳过，包含实际 WSS、可控时序、KCP 真实 UDP、撤销、共享 listener 和取消回调异常收尾；清理异常不阻止完成，重复 Dispose 保留失败，不重试。
- Unity NetworkLifecycleTests：11/11 通过，新增 Disposed 回调重入 Runtime Shutdown，包含空闲 KCP worker 收尾。Unity NativeUdpHelloEchoAndRelease：1/1 通过，实际连接 .NET local-kcp-dtls 并验证关闭后托管/native 预留归零。
- 生成协议 check --artifacts 通过。Unity Editor 编译通过，Console 错误0；StandaloneOSX Player 脚本编译通过，43 个程序集。未执行原生 IL2CPP 打包、Windows/移动设备、远程 CI 或长时间 soak。
- 恢复测试前的 EditorSettings 文件与内存设置；Launch.unity 未播放、无未保存修改。临时凭据文件、编译目录与本轮验证日志已清理；原生构建产物/工具链保留在忽略的 Tools/Artifacts 下。
