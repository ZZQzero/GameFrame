# 网络模块实施记录

设计契约见 [NetworkDesign.md](NetworkDesign.md)。本记录区分已实现代码、已执行验证和待实现项，不将协议单元测试视为完整网络验收。

最新状态见文末“PostgreSQL 注册登录与正常服务启动”。当前服务启动不再要求预置玩家账号，数据库接入位于应用层；新增 Register 的双端 MemoryPack 协议已生成，账号与哈希持久化、会话按账号隔离。历史版本和数量保留在原记录；当前证据与边界见 [ServerAccounts.md](ServerAccounts.md) 和 [服务端审查第 10 节](NetworkServerReview.md#10-postgresql-账号接入与启动流程调整2026-10-12)。

## 初次底层实现记录（2026-10-12）

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

代码入口：`Runtime/Network/Transport/Kcp`，原生源码、ABI 与构建入口在仓库公共目录 `Native/Kcp`；服务端框架在 `DotNet/GameFrame.Server/KcpListenerHost.cs`，联调应用在 `DotNet/Game.Server/LocalKcpService.cs`。公开调用方式见 [Network.md](Network.md)，工具命令见 [原生 README](../Native/Kcp/README.md)。自动生成消息没有新增第二套手写协议。

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
- 当时的 `DotNet/DotNet.csproj`（现已拆分，见文末）：通过 `Protocol/GameFrame.Protocol.projitems` 编译相同生成源码与纯协议基础代码，MemoryPack 1.21.4 依赖锁文件已保存。入口现已实现显式 local-wss Kestrel 服务，见下方 P1 进度。
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
- KCP 分配安全修正与 `Native/Kcp/build/prepare_kcp.py`：最初通过精确补丁应用到构建副本，后按用户要求直接合入 `ikcp.c`/`ikcp.h`，删除补丁文件与应用逻辑；源码准备改为当前 SHA 校验与逐字节复制，保留 CRLF 和 GUID。ACK/segment/发送分配失败返回而非 abort/assert/空指针，另修复上游队列偏移宏的 UBSan 问题。失败后的部分状态必须由未来 gfnet 锁 fatal 再销毁，不重试。临时 source manifest 记录上游基线与已合入修正，不代表 native 插件构建清单，DTLS/gfnet 尚未实现。

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

按用户要求，分配失败检查与标准 `offsetof` 修正已直接写入 `Native/Kcp/third_party/kcp/ikcp.c`、`ikcp.h`，与先前直接合入的 #467 共用同一份源码。源码字节与原安全补丁准备后的结果一致，CRLF 与两个 `.meta` GUID 保留。删除独立 patch 及构建时应用逻辑；`prepare_kcp.py` 简化为当前源码 hash 校验、逐字节复制与来源清单，拒绝漂移、Unity 内输出和覆盖。

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

本节记录本次全量复核修改前的状态；P0/P1与阶段表同样保留当时记录，当前修正与验证以文末为准。

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


## 底层实施方案逐项复核（2026-10-12）

对照 NetworkNativeDesign 第 1～9 节及其引用的 NetworkDesign 第 6、10、12、17、19 节，结论：核心传输可联调，方案并未全部实现/验收。本次是源码与交付审查，没有修改运行时代码或重新构建安装插件。此前“产物已落地”不能作为全部契约完成的证明。

### 核对范围与状态

| 方案 | 核对实现 | 结论 |
| --- | --- | --- |
| 1～2：分层、落点、同源核心 | IFrameTransport、WebSocketTransport、ConnectionContext、NetworkClient/Runtime、GameFrame.Protocol.projitems | 已实现：C 不解析 DTO，C# socket，单客户端宿主 worker，服务端独立 listener owner，共用生成协议/帧核心 |
| 3：ABI/状态/错误/借用 handle | gfnet.h、ABI.md、KcpNative.cs、gfnet_connection/listener.c | 主要实现；完整字段 offset 验证和 DTLS 负错误分类仍不充分；服务端正常 DTLS Close 的派发顺序有缺口 |
| 4：IO/线程/期限/额度 | KcpWorker/Transport、KcpListenerHost、ConnectionContext/NetworkOptions | 主要实现；client KCP admission 等待估算、业务发送优先级与 server WouldBlock 后暂停业务发送未实现；精细 deadline 等待调度明确延后 |
| 5：DTLS/KCP/两个通道 | mbedtls_config.h、gfnet_connection.c、KCP 唯一 c/h | 加密、MTU、message mode、WANT 待写地址保留、Fatal、KCP wire 校验已实现；native 接收通道公平性未实现 |
| 6：分配/关闭/诊断 | gfnet_memory.c、OwnedFrame、ConnectionContext、NetworkDiagnostics、各 owner 收尾 | native 配额和清零、跨层预留、帧/pending 保守 reservation 已实现；完整托管计账、诊断和正常远端关闭顺序未全部完成 |
| 7：授权与应用边界 | gfnet_listener.c、KcpListenerHost、LocalKcpService、AccountSessions/ServerConnection/Program | register/revoke、cookie、ticket 一次绑定/竞争/终态槽已实现；应用 handler 尚无授权连接上下文/终态回调；账号到世界授权接入按范围延后 |
| 8：构建安装 | CMakeLists、prepare_kcp/mbedtls、build/install、verify_windows、windows-llvm-mingw、平台 metadata、network.yml | 当前五种已安装目标的源码/清单/产物 hash 均匹配；Windows 26 导出验证通过；Linux 构建入口/CI 有代码但未本地执行；发布/真机证据未完成 |
| 9：退出条件 | gfnet_regression、allocation_safety/fastack 及验证脚本、KcpLoopback/SourceBudget、客户端/服务端时序、Unity lifecycle/loopback 测试 | 有回归证据，但不足以完成全部退出条件：详见下方矩阵 |

原生维护源码同时核对 gfnet_internal.h、mbedTLS lock；客户端辅助同时核对 NetworkConnectOptions、NetworkException、Frame/ControlCodec、PendingDeadlineHeap、协议 catalog/contract 与预算 writer。测试及构建脚本按入口和实际断言核对，第三方 mbedTLS 重点核对配置、分配钩子及 DTLS 错误处理路径，不宣称逐行审计整个上游库。

### 实际行为缺口，按优先级排列

1. **P1：服务端正常 DTLS 关闭丢弃已接收可靠帧。** `KcpListenerHost.Run` 在 DriveChild 前调用 PollEvents，Closed 分支直接 Retire/release；Retire 取消业务 owner，native 剩余帧和已入队但未执行的可靠帧没有按关闭标记顺序交付。临时 C 双端探针复现 `Closed kind=2 reason=0` 时仍可 peek 到可靠 32-byte 帧，release 随后将其释放。GFNP Control Close 有另一条排队/等待路径，不能据此认为 DTLS close_notify 同样满足正常断线 FIFO。对应 KcpListenerHost.cs:266、383，gfnet_connection.c:248。
2. **P2：native 入口可能使不可靠接收饥饿。** gfnet_peek_frame 永远优先完整可靠帧，只有可靠为空才返回不可靠；client Pump/服务端 Dispatch 的“16 条可靠后处理一次不可靠”发生在转入托管队列之后，无法保证 native 中的不可靠帧被转移。临时真实 DTLS 双端探针同时保留32条可靠帧和1条不可靠帧，复现必须先消费全部32条可靠帧才选中 channel2。持续可靠输入下，无最长等待保证；在预算内淘汰/积压不等于公平派发。应在不复制第二套可靠队列的前提下，让 peek/header/recv 保持一致的通道选择。对应 gfnet_connection.c:376。
3. **P2：KCP 发送 admission 只检查条数/容量。** CheckSendCapacity 没有按应用待发字节、native pending/inflight、可用速率及 RPC 剩余期限估算等待；当前只能接受后在移交前以队龄/timeout 结束。与总设计第10节的“预计超250ms/剩余deadline则入口拒绝”有差异。对应 ConnectionContext.cs:496。
4. **P2：可靠发送优先级未实现。** 可靠和不可靠业务共用 sends FIFO，只有 controls 单独优先；已排队不可靠帧在等待自身额度/总额度时可阻塞后面的可靠 RPC。client admission 与传输各自有不可靠 token bucket，前者是额外入口限流，文档尚未明确该两级策略；不能把它当成已实现可靠业务优先。对应 ConnectionContext.cs:505、532，KcpTransport.cs:354。
5. **P2：服务端 socket WouldBlock 后仍追加业务发送。** Run 保留 outputLength，但继续遍历 DriveChild；后者每child最多移交64条业务/控制帧，没有 pending socket output 的业务准入条件。client ProcessSend 有 outputLength 判断，服务端未对应实现。虽然 native 队列/队龄仍有限，阻塞期间继续堆积会增加压力，未满足底层方案第4节的暂停追加业务规则。对应 KcpListenerHost.cs:226、267、521，KcpTransport.cs:359。
6. **P2：诊断只实现部分。** 尚无 Ping RTT、业务有效字节、Connect/Hello 和 RPC 分阶段耗时、worker 轮转耗时/迟延、host Pump 用时及托管峰值完整快照。native 有 input_count、output_oldest_ms、allocations、capabilities 等字段，但公开 KcpMetricsSnapshot 未完整暴露；DTLS bad-MAC/replay/cookie 细分计数在 ABI 文档中声明不可用，公开快照没有逐指标能力标记。TimeoutRetransmits 只涵盖超时重传，不能当全部重传。对应 NetworkDiagnostics.cs:33、100。
7. **P2：完整托管计账尚未完成。** 帧/pending 每项512bytes是保守 reservation，常驻 Dictionary/Queue/Channel、任务/取消注册、来源表/命令委托、订阅和驱动对象未逐项核验；DTO 本身属于业务/序列化分配，也需与网络可控预算明确划界。native allowance 和源码统计正确不意味着8MiB/64MiB总成本契约已验收。对应 OwnedFrame.cs:14、ConnectionContext.cs:99 与各 owner 的常驻容器。
8. **接业务前需要补的框架接口。** 服务端 handler 当前参数只有 OwnedFrame、预算和token，应用不能通过该入口得到grantId/connectionId/generation，也没有应用级绑定/关闭回调。底层 native event 齐全，但仅在 host 内部消费；正式业务接入应以明确的连接上下文和生命周期通知连接授权所有者，不把账号/世界逻辑写进 C 或 Runtime。对应 KcpListenerHost.cs:74、728。

完整字段 offset 守卫目前仅明确检查 config 的两个指针偏移，其余以size为主；DTLS read/handshake 除等待/正常关闭等路径之外的负值统一转 ProtocolError，内部错误仍保存native_error但分类未逐项审核。这两项属于ABI/错误契约补强，不能用已有错误PSK/篡改测试推定全部覆盖。

### 验收与当前证据的差距

- 单连接：现有测试覆盖32KiB可靠、1150bytes不可靠、错误PSK、篡改/重放、未知channel、KCP尾随、配额/create与send失败、早tick/output队龄、owner和低32位回绕。没有完整逐点 DTLS握手/read/write/output/listener 分配故障、WANT状态转换组合矩阵。
- listener：cookie陌生来源清理、grant双握手竞争/单次绑定、撤销/release、共享socket与来源表边界已有测试；grant/握手/连接表全满、终态覆盖、过期/重复注册/错误ticket/旧generation、慢socket等完整矩阵仍缺。
- `test_max_ack_and_window` 实际验证128个ACK、排队256segments和至少3个ACK包，并未逼近方案推导的3008 ACK＋128满MSS整窗重传的组合flush。不能仅凭测试名宣布最大flush峰值已验收。无flush时输入额度不恢复、持续丢包/乱序/拥塞/限速和双通道公平性仍需专项测试。
- 客户端与服务端实联具备Hello/Echo、两通道、主线程停顿、撤销与收尾；没有真实socket WouldBlock/短写注入和完整KCP取消/满额/在途Close矩阵。Unity现有实联仅macOS Editor Hello/Echo，未覆盖全平台/双通道真机。
- Windows DLL仅交叉构建/PE检查，未执行Windows原生或UDP；MSVC/Windows、Linux远程CI未运行。Android/iOS/simulator只有产物和清单。IL2CPP最终链接、裁剪/formatter、IPv6/后台/弱网、容量及24小时soak仍未完成；这些属于验收缺口，不是要求补世界业务。
- 精细deadline等待已经在底层方案明确延后，但总设计第6.1节仍写“最近deadline/取消信号可提前唤醒”；现有KcpWorker最多等10ms，取消主要由下一轮观察token/CheckClock。两份文档尚需统一为同一实际承诺。

本轮新增证据：五组安装清单全部匹配当前维护源码/build_id/二进制hash；Windows导出26项通过；复跑macOS arm64 ASan/UBSan native harness1303个检查，live native bytes=0。两个临时探针只用于验证上述行为，编译产物与源码均在系统临时目录结束后删除。1303是断言检查次数，不能理解为1303个独立场景。本轮不修改Unity状态，不将前一轮Unity/Player验证当作本轮新执行结果。

其他时序差异：client/server 每轮首尾各调用一次最多256包的 FlushOutput，没有共享该轮剩余额度，理论合计512包，与总设计“每轮每连接最多256个输出”不完全一致。主线程目前是每client每轮最多16个工作单元、Context在16个可靠工作后取1个不可靠，尚非总设计第9.2节的16可靠＋最多4不可靠配额。native 不可靠packet的born也没有通过应用帧ABI透传；客户端转入托管队列重新设置AdmittedAt，native滞留时间不包含在托管UnreliableMaxAge中，需明确并落实整体时效边界。

本轮 .NET KcpLoopbackTests＋KcpSourceBudgetTests 实际执行8/8通过，无跳过。这证明现有覆盖场景可用；上述探针与源码对照揭示的未覆盖缺口仍然存在。

## 本次全量复核后修改（2026-10-12）

本节为最新状态，前述缺口与 ABI1 记录保留为历史。逐文件清单、调用链、方案比较及上游审计边界见 [NetworkReview.md](NetworkReview.md)。

采用现有 owner 和生命周期调整：正常 DTLS/WSS EOF 不立即取消已接受可靠消息；按 FIFO 消费，再释放 native/托管所有权。物理 EOF 后不发送回复。WSS 的唯一发送 owner 执行 CloseOutput，客户端接收 owner 等待标准 close ACK，最多2秒后 Abort；显式关闭/故障仍取消并等待实际 IO。

客户端与 listener 分离控制/可靠/不可靠 FIFO；native 未接受时保留队首，不重复扣 token，不可靠背压不占住可靠 RPC。可靠 admission 在分配 correlation/pending 前估计 token/window 等待。native/托管派发按16可靠/4不可靠轮转；peek/header/BufferTooSmall不推进。ABI升级到2透传不可靠原始 born，有效期包含 native 等待和托管迁移，支持 uint32 回绕。

client/server 首尾 flush 共享每轮256包预算，socket保留WouldBlock包时停止追加业务；grant注册deadline从调用准入起算，owner只使用剩余TTL；临时密钥在转换/登记失败时也清零。handler增加不可变connectionId/generation/grantId。C静态断言与C#验证完整公开结构offset/size。DTLS错误区分已知远端协议、本地内部及分配失败，保留首因和完整native子码。

listener feed 返回的 InternalError 可来自临时握手 create/tick，不能升级为共享 socket 故障。临时握手失败立即释放，已有 child 终态由原生事件处理；listener tick/poll 仍检查自身失败。新增错误凭据握手后已有连接 Echo 与新 grant/连接仍可使用的实际 UDP 回归；该回归验证握手隔离，不代表已注入全部 DTLS 内部失败分支。

指标新增GFNP帧字节/业务正文、Ping RTT、计账预留峰值、native分配/input/plaintext/output队龄与能力字段；完整元数据、RPC分阶段/分位、worker/Pump用时和DTLS内部细分丢弃仍未完成。不将reservation当成RSS或实际对象布局。固定10ms轮询承诺同步到总设计。

### 本轮实际验证

- .NET网络最终全套59/59通过、0跳过：含真实UDP正常EOF排空、授权身份、可靠准入不分配pending/ID及不可靠桶阻塞时可靠RPC继续完成。新增WS物理EOF立刻停止准入/排队发送，在途Send因EOF失败不覆盖此前保留响应；错误凭据握手不影响已有连接/后续准入；既有WSS认证/心跳回归通过。
- 协议/生成测试178/178通过，check --artifacts通过；没有修改proto或生成文件。
- macOS arm64 ASan/UBSan native harness2294次REQUIRE检查通过，native live bytes=0；macOS universal Release ctest通过。新增实际DTLS公平消费、born/uint32回绕、真实close_notify后可靠帧可读取及DTLS首因/错误分类验证。断言次数不是独立场景数。
- 独立KCP分配安全75/75、准备后ACK/调度和独立ACK16/16＋tick4/4通过；临时构建自动清理。
- ABI2 macOS universal、Windows x64、Android arm64、iOS device arm64、simulator arm64重新构建安装；源码/hash/build_id、五组产物hash及原meta逐字节一致性核验通过。Windows26导出/系统DLL依赖匹配，回归exe只构建，尚未Windows执行。移动库只有构建与清单证据。
- Unity6000.3.19f1已重启为新进程加载ABI2；最终Editor编译与Console错误0，网络EditMode143/143、生命周期PlayMode11/11、实际UDP Hello/Echo/释放1/1通过，关闭后托管/native预留归零。StandaloneOSX Player脚本编译43个程序集通过；不代表原生IL2CPP链接/运行。
- 测试结束保持Launch.unity、未播放且无未保存修改，恢复EditorSettings原文件与内存选项；临时MCP重连脚本、凭据、验证日志、Player脚本输出均清理。Tools/Artifacts中的工具链与原生构建目录保留。
- 尚未执行远程CI、IL2CPP最终链接/裁剪/设备、IPv6/代理/后台/弱网、生产容量及24h soak。世界业务与WebGL WSS按确认延后。

## mbedTLS 4.2.0 升级（2026-10-12）

按用户要求将实际构建依赖从 3.6.7 更新到 4.2.0，包含 TF-PSA-Crypto 1.2.0。上游将 4.1 标为 LTS，4.2.0 是 stable；锁文件与当前设计不将 4.2 标为 LTS。官方发布 tar.bz2 的 SHA-256 为 `2bed9d713b4668f76553b097e72b8aa30bc8f112a940d7ae228d524bbde6ffea`，依赖 commit、TF-PSA commit 和安全公告检查日期均保存于锁。普通构建继续离线核验依赖，不浮动下载。

升级覆盖依赖锁/准备工具、CMake、TLS/PSA 配置、Windows 线程适配、native memory/connection/listener、回归、构建许可证合并和五个平台安装产物。C ABI2 与 GFNP 应用协议保持现有契约；不加入业务重试、回滚或传输降级。

- 4.x 强制 PSA：移除每连接 entropy/CTR-DRBG 和 SSL RNG 配置；cookie setup 使用新签名，ticket SHA-256 使用 psa_hash_compute。TLS／Crypto 配置分别维护，仍仅 DTLS1.2 + PSK AES128-GCM、cookie、防重放，不启用证书、RSA/ECC、TLS1.3或会话恢复。
- PSA 一次初始化，1024 个固定共享密钥槽元数据采用静态存储并受库锁保护，不计入动态 owned_bytes。密钥材料、操作与默认复制缓冲沿当前调用 quota 分配；释放按分配头归还。默认复制缓冲保留，避免改变 TLS 原地 AEAD 的约定。槽耗尽显式报 AllocationFailed，释放后可重新使用。
- external RNG 使用 Windows BCryptGenRandom、Apple CommonCrypto CCRandomGenerateBytes、Android API23 /dev/urandom、Linux getrandom；无共享堆上随机数状态。失败清零输出、返回 insufficient entropy 并终止创建/握手。不存在自动切换随机源。
- 新版错误码直接按单一 SSL／PSA 原值分类，删除3.x高低层掩码；配额/PSA内存失败归 AllocationFailed，已知远端协议失败归 ProtocolError，熵源/配置/内部失败归 InternalError，首因保留。
- Windows 使用新版9个 mutex/condition callbacks；修正 LLVM-MinGW 会忽略 __declspec(thread) 的问题：MSVC 用该声明，MinGW 用 C11 _Thread_local。已核验 DLL 的 gfnet_current_quota 地址位于 PE TLS 区域，不共享为普通全局变量。
- 官方归档147个相对链接统一展开为普通文件，Windows准备无需管理员/Developer Mode。5070个文件逐个与已校验的官方提取树内容比较一致；新临时目录再次准备得到相同 source_tree_sha256。构建/安装沿现有源码和二进制锁校验，增加独立 TF-PSA-Crypto 许可证；iOS合并新的 libtfpsacrypto.a。

本次实际验证（均使用4.2.0，不能以此前3.6.7结果替代）：

| 验证 | 结果与边界 |
| --- | --- |
| macOS arm64 Debug ASan/UBSan native | 8328次 REQUIRE 检查通过，sanitizer无错误，释放后动态live bytes=0；新增600字节随机输出/失败清零、SHA-256已知向量、4线程64对同时存活的DTLS双端、1024槽耗尽/释放/重新分配，保留全部已有回归。线程等待次数可能随调度变化，检查数量以本次输出为准。 |
| macOS universal Release native | ctest通过，8328次检查、live bytes=0；arm64宿主实跑，x86_64切片已编译。 |
| Windows x64 | LLVM-MinGW UCRT生成DLL和回归exe；完整26个ABI导出、PE TLS及系统DLL依赖通过；未在Windows执行exe，MSVC CI工作流未远程执行。 |
| Android arm64 / iOS device arm64 / simulator arm64 | NDK/Xcode编译、静态合并和安装器校验通过；未在设备/模拟器运行，simulator archive仍按契约默认禁用。 |
| 五平台安装产物 | 当前维护源码hash、二进制hash、build_id、4.2.0/TF1.2.0锁、许可证一致；保留全部既有插件.meta及GUID，未安装sanitizer产物。 |
| .NET网络全套 | 显式加载本次macOS Release库，59/59通过、0跳过，包含真实UDP/DTLS。 |
| 重启后Unity | Editor进程31934已退出，新验证进程43805；Unity编译完成/Console错误0，NetworkLifecycleTests 11/11、NativeUdpHelloEchoAndRelease 1/1通过；使用当前安装的macOS库和同版本.NET本地服务。 |
| 清理与恢复 | 本地服务Ctrl+C退出码0；恢复原环境变量、EditorSettings文件字节及Launch.unity/未播放/无场景改动；临时Editor桥接脚本、目录/meta和凭据清理。 |

协议生成代码未修改，未重复178个协议测试；此前Player脚本43程序集结果为历史。本次未执行IL2CPP原生链接与Player运行、Windows/Linux宿主执行、远程CI、移动真机/模拟器、IPv6/后台/换网和长期容量验收。这些不以源码、编译或Editor联调结果代替。世界业务与WebGL WSS继续按已确认范围延后。


## 2026-10-12 原生源码公共目录迁移

原 `Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~` 整体迁到仓库公共 `Native/Kcp`，原 `Unity/Assets/Plugins/GameFrame/Kcp` 的唯一 KCP 源码与许可证迁到 `Native/Kcp/third_party/kcp`。C/header/CMake/config/依赖锁逐文件 SHA 与迁移前一致；原第三方 `.meta` 字节/GUID 在公共目录保留为迁移记录，不再由 Unity 导入。未保留旧目录副本、链接或旧入口。

C# P/Invoke 与 worker 留在 Unity Runtime；Unity 原生库仍安装到 `Unity/Assets/Plugins/GameFrame/Native/Kcp/<platform>`。统一构建器、准备器、安装器和安全测试改为读取公共目录，构建/依赖准备禁止向 Unity 或维护源码目录输出。CI 路径过滤/命令、ABI 链接、原生设计及服务端发布说明同步更新。服务端部署对应平台二进制、清单和许可证即可，无需复制源码或安装 Unity。

本轮实际验证：

- 30 个公共维护文件均未被 Git 忽略；所有 Python 语法/CLI 帮助、六个平台 dry-run、源码/输出保护、网络 Markdown 本地链接检查通过。旧路径引用与差异空白检查通过。
- 独立 KCP ACK 回归 16/16、调度边界 4/4；分配失败入口 assert/NDEBUG/ASan+UBSan 共 75/75，含 ACK/回绕和源码完整性检查，sanitizer 无错误。
- 使用现有锁定 mbedTLS 4.2.0 + TF-PSA-Crypto 1.2.0 依赖，从全新 `Tools/Artifacts/NetworkNative/shared-<platform>-<arch>-release` 目录构建并安装 macOS universal、Windows x86_64（LLVM-MinGW）、Android arm64（NDK 27.2.12479018/API23）、iOS arm64 与 simulator arm64。每个产物都由正常构建重新生成清单，安装后的清单、源码 SHA、build_id、二进制 hash 验证通过；所有既有 Unity importer `.meta` 字节保持一致，模拟器产物继续默认禁用。
- macOS Release `gfnet_regression` 8,326 次检查通过，live native bytes=0；Windows PE/导出/系统依赖检查通过，回归 exe 已编译但未执行。
- 发布 .NET Release 到系统临时目录，通过 `install.py --server-output` 安装新 macOS 库；未配置自定义 native resolver 的真实 `DotNet.dll --profile local-kcp-dtls` 从程序旁加载库、注册临时凭据并启动 UDP listener，Ctrl+C 正常关闭、退出码 0。临时凭据/发布目录验证后删除。
- 指向该新安装库的 `Tools/NetworkServer.Tests` 59/59 通过，0 skipped，包含真实 KCP/DTLS UDP 与 WSS 测试。

本次范围是公共源码与构建目录迁移；未改变原生 ABI、协议或服务端业务分层。未运行 Linux/Windows 平台实测或远端 CI，未在重启后的 Unity 执行 Editor/Player/IL2CPP 与移动设备验收；这些不能由交叉编译或 .NET/macOS 回归替代。服务端框架分层审查与后续建议继续见 [NetworkServerReview.md](NetworkServerReview.md)。

## 服务端程序集与应用边界拆分（2026-10-12）

按服务端全量审查实施 Core → Server / Game.Protocol → Game.Server 的四工程边界，保留一个业务进程。Core/Server 不引用游戏协议或账号；Protocol.projitems 只包含双端同源生成 C#。客户端 ConnectionContext、worker/options 不再编入服务端基础工程；Unity Runtime 与 Hotfix 的程序集名、生成 namespace 和 wire/hash 未变。

ServerApplication 冻结强类型 RPC/Notify/显式拒绝注册，ServerConnectionContext 提供主动可靠/不可靠 Push、外部 CloseAsync、响应后关闭、不可变身份与终态；ServerSession 接入头部准入、派发复查、期限和 Connected/Closed。账号、本地认证/登录限流/恢复/顶号/注销与 Echo 移入 Game.Server。框架拥有帧、预算、编码和请求关联，不把 OwnedFrame/native handle 交给业务。

WssServer 通过 hosted lifecycle 的 StoppingAsync 在 Kestrel 停止前 join 网络；取消回调和 Closed 异常仍完成资源/配额释放，重复停止返回一致结果。KCP 统一可靠 correlation 规则，主动 Push/公开 Close 隔离旧连接，CloseAfterResponse 等回复交给 native 后再发送可靠 Close。连接结束与受限流量提升串行，正常 EOF 保留既有可靠工作；单连接故障不扩大为 listener 故障。网络选项在 host 构造时验证/复制；完整诊断快照与生产容量验收仍待完成。

本轮验证：四工程 locked restore/Release build 0 警告错误；协议178/178、独立框架10/10、应用/真实 WSS/DTLS 61/61（0跳过），生成 check --artifacts 退出0。实际发布 Game.Server、安装已有 macOS 原生库，从程序旁默认加载并启动 listener，Ctrl+C 退出0。Unity 6000.3.19f1 Editor 错误0、相关 EditMode143/143，StandaloneOSX Player脚本编译43程序集成功；原场景/设置保持。独立框架测试加入 CI，尚未远程执行。

本轮不重新构建 native、未执行 Windows/Linux/移动设备、IL2CPP 原生链接裁剪或生产负载；世界业务与 WebGL 延后。详情、历史问题状态及逐文件核对见 [服务端审查第8节](NetworkServerReview.md#8-已实施的服务端拆分2026-10-12)。公开接入见 [Server API](../DotNet/GameFrame.Server/README.md)，启动/部署改为 dotnet run --project Game.Server 与 Game.Server.dll，见 [DotNet README](../DotNet/README.md)。


## 应用启动封装与登录功能目录（2026-10-12）

Program 仅验证 --profile 并选择 LocalWssService/LocalKcpService；单参数对直接校验，移除通用参数遍历循环。两个应用启动入口分别持有配置、证书/凭据、应用与网络资源；WSS host 建立释放所有权后配置路由，保留 hosted lifecycle 先 join 网络的停止顺序。

Game.Server/Login 集中 LoginHandlers、LoginSession、AccountSessions、LocalAccountSettings，负责登录/恢复/注销、限流、权限/期限与敏感数据清理。LobbyApplication 仅组合登录模块、本地 Echo 和世界路由的显式拒绝。网络框架、生成协议、native ABI 与 Unity 编译边界未变；目录与流程见 DotNet/README.md。

本次新增验证：Release build 0 警告/错误；网络61/61，0跳过；真实进程参数/缺失配置6/6拒绝；实际 local-wss TLS/WebSocket 升级后带活动连接 Ctrl+C 约23ms、退出0；临时发布 local-kcp-dtls 默认加载已安装macOS原生库、注册grant并启动，Ctrl+C约11ms、退出0。临时证书/凭据/发布目录已删除，没有更改系统信任。没有重新执行 Unity、原生构建或其他平台实跑。范围及源码核对见 NetworkServerReview.md 第9节。


## IDE 本地启动配置补齐（2026-10-12）

用户直接运行 Debug Game.Server 时未提供 --profile，触发生产认证未配置的拒绝；先前入口验证使用显式 local 参数，未覆盖 IDE 默认项目运行配置。新增 Game.Server/Properties/launchSettings.json，分别提供 local-wss/local-kcp-dtls commandLineArgs；不包含凭据，Debug 和发布程序均不会因无参数自动启用本地认证。无参数错误增加启动指引。DotNet README 补充 Rider/Visual Studio profile、普通项目运行参数、必填环境变量和直接可执行文件不读取 launchSettings 的区别。本机 Rider 原有 Game.Server 配置的空程序参数已改为 --profile local-wss；其他本机设置未变，IDE 可能需重新载入以读取磁盘改动。

本次证据：Debug build 0警告/错误；使用用户给出的 Debug apphost 路径复现无参数拒绝；两个 dotnet run --launch-profile 均正确传参、缺失配置时明确拒绝；临时配置下 Debug local-wss 实际 TLS 验证/WebSocket 升级成功，活动连接 Ctrl+C 退出0；既有 ProductionWithoutAuthenticationProviderRefusesStartup 测试1/1通过。临时证书、凭据和探针目录已清理。没有在 IDE 界面实点运行，没有重复执行整套网络/Unity/native测试。本机真实账号、密码和PFX仍需开发者显式配置，未创建默认测试凭据或更改系统证书信任。


## PostgreSQL 注册登录与正常服务启动（2026-10-12）

用户确定 PostgreSQL 后完成应用层 Npgsql 10.0.3 接入。移除 LocalAccountSettings 与 GF_LOCAL_ACCOUNT/PASSWORD 的启动依赖，LoginOptions 仅保留登录策略。LocalWssService 校验证书、GF_DATABASE_CONNECTION 和账号表后启动监听；显式 SQL 初始化文件与 local compose 入口交付，启动不自动迁移或创建玩家。数据库错误停止初始化，生产 hosting 仍需单独配置。

Proto 增加 RegisterRequest/可靠 Register(opcode1004)，ResultCode 增加 AccountAlreadyExists，重新生成双端 DTO/catalog 与开发辅助产物，协议 hash 随之更新。注册只创建账号，成功后客户端再 Login；数据库唯一约束保证并发重复只成功一次。密码采用16字节随机盐、PBKDF2-SHA256 600000次与32字节哈希，SQL全部参数化。认证并发最多4、池最多16；网络/native owner 不等待数据库。

AccountSessions 从固定单账号改为有界多账号状态：每个账号一个当前会话，顶号/恢复轮换/注销仅影响该账号。数据库原子分配递增 authEpoch，进程内拒绝过期异步结果；账号重启可登录，内存 Resume 凭据不跨重启保留。生产多实例仲裁与世界业务继续延后，未新增重试、密码回退、数据库回退或自动重建。

本次验证：四工程 Release build 0警告/错误，solution及NetworkServer.Tests locked restore通过；生成 check --artifacts退出0；协议181/181、独立框架10/10、服务端94/94，0跳过（含两套WSS仓库测试、真实PostgreSQL及已安装macOS DTLS）。PostgreSQL数据库测试验证落库及服务重建后登录、并发重名、不同账号隔离、乱序epoch、SQL字面量、取消与错误传播；存储替身只用于测试，不是运行时降级。

实际 Debug Game.Server 入口在临时PostgreSQL数据库上启动，未设置玩家账号环境变量；真实TLS/GFNP Hello→Register→Login→Echo通过，活动连接Ctrl+C退出0。服务重启后同账号再次Login/Echo成功、accountId保留、authEpoch递增，正常退出0。临时数据库、证书和探针文件清理，系统证书信任未修改。

Unity 6000.3.19f1 Editor脚本编译及Console错误0；网络相关EditMode146/146；StandaloneOSX Player脚本编译43程序集通过。Launch场景、播放状态、目标平台和序列化EditorSettings无变更，Player临时输出已清理。为验证安装了本机PostgreSQL18工具，只运行独立临时cluster，未启用brew开机服务；测试cluster停止后删除。compose与CI配置未实际运行，Windows/Linux/移动设备、IL2CPP原生构建与生产容量/多实例未验收。
