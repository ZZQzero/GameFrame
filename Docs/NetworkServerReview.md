# .NET 服务端完整审查与分层建议

审查日期：2026-10-12。拆分前结论：代码可以完成本地 WSS 登录和 KCP/DTLS 联调，但 `DotNet` 仍是框架与联调应用混合的单一程序集。通用服务端框架尚未形成可独立引用的边界。优先调整职责、公开接口和生命周期，再考虑吞吐优化。

第 1～6 节记录拆分前的全量审查与证据；链接已指向迁移后的维护路径，不表示历史问题仍存在。按用户授权已实施四工程分层，原固定账号 LocalAccountSettings 已由第 10 节的 LoginOptions/AccountService/PostgreSQL 仓库替代；历史源码链接指向当前维护文件。现行 API 见 [服务端框架](../DotNet/GameFrame.Server/README.md)，完成项与当前验证见文末第 8 节。世界业务与 WebGL WSS 继续延后。

## 1. 覆盖范围与调用关系

完整阅读 `DotNet` 的全部维护源码、工程、配置、依赖锁与使用说明；继续阅读服务端直接共用的网络源码、所有 Proto/生成 C#、开发协议产物、生成器、相关测试、原生包装源码、KCP c/h、构建安装脚本和网络 CI。逐文件清单见文末。

核对 [NetworkDesign](NetworkDesign.md)、[原生方案](NetworkNativeDesign.md)、[使用说明](Network.md)、[错误契约](ErrorContract.md)、[作用域](Scope.md)，并阅读历史实施与审查记录。历史缺口以当前源码重新判断，不把已修复项重复列为当前问题。

上游 mbedTLS/TF-PSA-Crypto、MemoryPack、Roslyn、protobuf-net 的完整实现不属于本次逐文件源码清单；审查覆盖它们在项目中的配置、版本锁、调用、生成与错误/分配集成边界，不宣称完成上游密码库安全审计。忽略的 IDE、bin/obj、工具链和构建缓存不作为项目实现文件。已安装二进制不等于源码，平台实跑边界见第 6 节。

拆分前执行链（保留历史分析；当前链见第 9 节）：

```text
DotNet/Program
  ├─ local-wss → Kestrel → LocalWssService
  │                         ├─ ServerSettings（容量＋账号配置）
  │                         ├─ AccountSessions（单本地账号）
  │                         └─ ServerConnection
  │                              ├─ GFNP Hello/心跳/帧/队列
  │                              └─ Login/Resume/Logout/Echo/Grant 分支
  └─ local-kcp-dtls → LocalKcpService → KcpListenerHost
                                       ├─ UDP/native owner → gfnet → DTLS/KCP
                                       └─ 应用 Dispatch → OwnedFrame 回复

Protocol/*.proto → NetworkProtocol 工具 → MemoryPack DTO/codec/catalog
  ├─ Unity Game.Hotfix 编译生成 C#
  └─ DotNet 通过 projitems 编译相同 C# 与网络共享源码
```

审查同时覆盖握手、登录前后、恢复/顶号、请求排队与派发、背压、取消、期限、正常 EOF、协议故障、应用异常、单连接隔离、共享 listener 故障、关闭和最终释放，以及不同平台构建入口。

## 2. 主要问题

### P1：WSS 连接实现直接依赖游戏业务

[ServerConnection.cs](../DotNet/GameFrame.Server/ServerConnection.cs) 持有 `AccountSessions`，直接使用 `GameNetProtocol` 和生成的 Login/Resume/Logout/Echo/RequestWorldGrant 路由；`DispatchAsync` 同时处理编解码、账号提交、凭据清理、游戏结果码和传输关闭。接收阶段及心跳也直接检查账号绑定。新增应用协议需要修改通用连接类。

[LocalWssService.cs](../DotNet/GameFrame.Server/WssServer.cs) 自行创建账号服务，IP 表混合连接容量、未认证数量与登录尝试额度；网络时钟通过 `AccountSessions.Now` 获取。[ServerSettings.cs](../DotNet/Game.Server/Login/LoginOptions.cs) 的构造必须提供本地账号密码，同时承载队列、证书入口所用期限、账号 TTL 和登录策略。

建议：传输连接只持有协议目录、网络参数和应用连接入口；Login/Resume、绑定/epoch、账号 TTL、阶段权限、顶号/注销及敏感 DTO 清理归应用。保留头部预检、未认证限额和反序列化前拒绝能力，通过显式应用策略提供，不删除这些约束。时钟直接复用现有 `NetworkClock`。

`AccountSessions` 当前只保存一个本地账号，成功响应的 `AccountId=1`。这符合 local profile，应该作为应用示例迁移，不应包装成框架的生产账号管理器。local 配置与生产未配置认证时拒绝启动的行为保留在应用组合入口。

### P1：KCP 尚无完整应用连接接口

[KcpListenerHost.cs](../DotNet/GameFrame.Server/KcpListenerHost.cs) 已注入 catalog 和处理器，也已提供 connectionId/generation/grantId；这些是正确的基础。但 host、连接信息和处理器参数均是程序集内部类型，处理器收到 `OwnedFrame` 和 `FrameMemoryBudget`，自行解码、分配、写帧和处理失败释放。

当前应用接口只有“一条输入返回零或一条输出”。没有应用级 Connected/Closed 通知，也没有独立于输入的强类型 Push 或按连接关闭接口。上层只能借助内部访问或反射取得更多能力；现有测试中的反射是测试注入手段，不能成为应用接入方案。

建议：业务只接收 DTO 和应用连接上下文；框架拥有字节缓冲、correlation、响应编码和发送队列。提供可靠 Push、显式不可靠 TryPush、CloseAsync、终态原因以及连接生命周期通知。grant 注册/撤销作为受控网关接口，账号/世界验证由上层完成，C 与框架不解释账号身份。

### P1：真实入口的 WSS 停机顺序与测试不同

[Program.cs](../DotNet/Game.Server/Program.cs) 在 `await app.RunAsync()` 返回后才因 `await using` 清理 `LocalWssService`。Kestrel 停机先等待仍在执行的 WebSocket HTTP 请求，而这些请求仍由网络连接 owner 驱动，容易等到 host 超时或连接自身期限。

临时真实 Kestrel/WSS 探针按相同顺序建立一条等待 Hello 的连接，设置 host shutdown timeout=2 秒：`app.StopAsync` 耗时 **2014ms**；随后网络 Dispose 完成，连接/帧预算归零。这证明停机顺序会消耗超时，未测量默认配置下的具体等待时长。现有 `WssServerTests` 先 Dispose 网络，再 Stop HTTP host，因此没有覆盖入口顺序。

建议：在 host 生命周期的停止阶段停止网络准入、取消连接并等待 owner 退出，再等待 HTTP 请求排空；使用共享停机任务支持重入和一致错误结果。不能在取消回调中同步等待异步清理，也不新增进程退出拦截流程。

### P2：KCP 原始回复边界允许错误的 RPC 关联

`KcpListenerHost.DispatchAsync` 检查回复帧格式与目录，但未检查 Response 的 opcode/correlationId 是否对应当前 Request。`FrameCodec.ReadFrame` 返回的实际头被丢弃，之后检查的是可单独修改的 `reply.Header`，字节头和元数据也缺少一致性验证。

临时实际 DTLS/UDP 探针让 Echo 处理器返回 `request.correlationId + 1`：服务端接受并发出；客户端保持 Connected，将回复计为迟到/未知响应，原请求超时（`LateResponses=1`、`RequestTimeouts=1`）。两侧关闭后预算为零。它是本地处理器错误未被框架边界拦截，不是外部绕过 DTLS 的漏洞。

局部方案是补头一致性和请求关联检查；更好的结构是让 RPC 处理器只返回 `TResponse`，由框架根据当前请求构造响应，Push 通过独立接口发送。保留内部帧接口时仍应验证字节头、目录和响应关联。KCP 没有 WSS 的请求 correlation 严格递增检查，应统一可靠 Request 的协议规则；这不等于实现业务幂等或重放交易。

### P2：共享源码清单混合协议、客户端传输和服务端需求

[GameFrame.Protocol.projitems](../Protocol/GameFrame.Protocol.projitems) 在 `IncludeProtocolRuntime=true` 时连同 `KcpTransport`、`KcpWorker`、WebSocket 客户端传输与连接配置一起编入服务端。它不是只有协议的依赖边界。测试又引用可执行工程，通过 friend assembly 使用内部 API。

建议拆分纯网络核心、客户端实现、服务端实现和生成的游戏协议清单。复用 wire/codec/预算/native ABI，不复用客户端 ConnectionContext 的收发方向和生命周期。内部 codec/catalog 能力通过受控程序集边界供框架使用，不把全部 internal 类型改成 public。

生成路由命名空间在 [MemoryPackProtocolGenerator.cs](../Tools/NetworkProtocol/MemoryPackProtocolGenerator.cs) 硬编码为 `Game.Hotfix.Network.Generated`，DTO 的 namespace 来自 Proto。现在服务端编译的是纯 C#，没有引用 Unity 热更 DLL；问题是所有权命名和工程边界。建议以后使用独立 `Game.Protocol` 协议项目，并让生成器统一配置路由 namespace。完整类型名进入协议指纹，修改必须更新 Proto/模板、重新生成并同步客户端/服务端，不能手改生成文件或只替换 namespace 字符串。

### P2：WSS 收尾未形成统一故障契约

`LocalWssService` 在连接构造前增配额，构造位于最终清理范围之外；`connection.Finish()` 失败会跳过 host 登记/配额释放。`ServerConnection.Stop` 先 Cancel 再 Abort，Cancel 回调抛错会跳过 Abort；`RunAsync` 的 finally 首先 Stop，失败会跳过 join/Drain/Detach。`LocalWssService.DisposeAsync` 没有共享任务，某个 Stop 抛错可跳过其他连接和账号清理。Finished 总是成功完成，应用无法据此获知终态首因或清理失败。

这些异常分支来自源码检查，本轮未逐分支注入。当前 WSS 没有向外部应用暴露 lifetime token，所以不能宣称已经复现业务取消回调故障。拆层后会新增应用处理器，必须同步确定契约，不能把现状当成健全的公开生命周期。KCP 已有取消回调抛错仍继续清理并传播失败的实现与测试，可以沿用其原则。

建议：准入使用明确 lease，完整所有权转交之后才登记连接；清理分别尝试停止、join、缓冲释放、应用 detach 和 host 配额归还，保留主异常；唯一关闭任务报告一致结果。属于清理责任，不能实现成自动重试或状态回滚。

### P2：配置与指标尚不能支撑独立框架定容

KCP 的 64 连接/16 握手/256 grant、native 256MiB 与多项队列/来源限额写在 host 内。WSS 与 KCP 对外只提供零散状态，没有统一的服务端连接/队列/native/派发/失败快照。

建议提供启动时验证并固定的网络容量配置和有界指标快照，local profile 显式选择本地默认值。native/managed 预算是不同归属，不能简单相加、按客户端额度预分配或把 reservation 当 RSS。PSA 1024 key slots 和 native 进程级预算在多个 listener 间共享，定容必须考虑这一限制；本轮不扩大未经验证的 ABI 参数。

## 3. 推荐工程边界

保留一个进程、一个业务可执行入口，通过程序集引用落实分层：

| 工程 | 职责 | 依赖 |
| --- | --- | --- |
| `GameFrame.Network.Core` | GFNP/control、路由/codec 契约、帧预算、native ABI 及共享基础 | 基础库，不引用游戏协议/Unity/业务 |
| `GameFrame.Server` | WSS/KCP 服务端、Hello/心跳、连接与队列 owner、强类型注册/回复/Push/关闭、生命周期 | Core；WSS 所需 ASP.NET 支持 |
| `Game.Protocol` | Proto 生成的 MemoryPack DTO、路由与 catalog | Core、MemoryPack |
| `Game.Server` | Program、部署/local 配置、账号示例、应用会话与处理器、认证/授权组合 | Server、Protocol |

框架不引用 `Game.Protocol` 或 `Game.Server`。客户端和服务端继续编译同一生成源码；Unity 继续编译适配 Unity 的同源 C#，不引用 net10.0 服务端 DLL。协议是否独立 Unity asmdef 需要同时落实热更 DLL、HybridCLR/AOT formatter 和测试引用，不将程序集移动视为已完成这些验收。

单纯把目录改名、提取 IAccountService，或给 `ServerConnection` 增加更多 opcode 分支，都不能消除业务依赖。立即引入 Actor 框架、多进程账号/网关/世界服务或通用会话管理器，也没有当前规模和业务需求依据。

## 4. 接口与执行流程

复用生成的 `NetRpc<TRequest,TResponse>`、`NetSend<T>`、`NetUnreliable<T>` 和 `NetPush<T>`。应用在启动时显式注册处理器，启动后冻结，检查重复/漏注册和路由归属；不通过运行时反射扫描业务类型。

建议应用接入面包括：创建每连接应用状态、Connected/Closed 通知、头部阶段准入、typed RPC/Notify 处理、可靠 Push、不可靠 TryPush 和 CloseAsync。上下文提供不可变 connectionId/generation/transport、取消信号和网络发送能力；不内置 accountId、角色或 worldSession。可靠 Push 成功表示准入，后续故障通过连接终态报告，不声称客户端已经收到。

接收流程固定为：传输/固定头与目录检查 → 应用阶段/权限/限额检查 → 有界队列 → schema 校验与 DTO 解码 → 应用 owner 执行 → 框架验证/编码回复 → 发送 owner 移交。账号权限在异步业务提交后需要重查，不能只在收包时检查。网络 owner 不等待数据库，不直接变更应用业务状态；应用共享账号状态需要自己的串行提交或现有锁约束，不与 native owner 混用。

同连接已接受应用工作与生命周期通知有明确顺序。正常物理 EOF 停止发送并排空已接受可靠工作，然后发布 Closed；协议/应用致命失败取消后续工作并 join。单连接应用故障仅结束该连接，共享 socket/owner 故障才结束 listener。终态通知最多一次；通知失败仍尝试释放其余资源并报告错误。关闭完成必须包含处理器退出和缓冲释放，不先公布成功再后台释放。

账号、恢复、顶号、注销和 grant 到世界的关联由 `Game.Server` 组合这些能力。没有世界 owner 时沿用明确不可用结果，不签发虚假授权，不添加自动重连、回滚或通道降级。

## 5. 实施顺序与验收

1. 抽离纯 Core 和生成 Protocol 的编译边界，处理内部 API/friend 清单，保持现有 wire 行为。
2. 提供最小强类型服务端注册与连接上下文；先将 KCP raw handler 包装/替换为 typed 接口，补 Push/Close/终态。
3. 将 WSS 的 Login/Resume/Logout/Echo/Grant、AccountSessions 和 local 认证配置移入 Game.Server；两种传输共享应用契约，分别保留各自正确的 IO owner。
4. 把网络停止接入 host 生命周期；统一停止/异常清理/终态原因和容量配置，同步文档。
5. 增加独立框架测试：没有任何游戏协议/账号仍能启动测试服务；第二套生成 catalog 可编译运行；typed RPC 回复关联、主动 Push、旧 generation、应用取消异常、EOF 顺序及真实入口停机。
6. 迁移现有登录/恢复/注销用例为应用集成测试，保留真实 DTLS/UDP 与协议向量检查。涉及 Unity 程序集或协议 namespace 时执行 Editor/Player 脚本及 HybridCLR/IL2CPP 验证。

本轮同时发现 NetworkDesign 第 18 节仍称 local-kcp-dtls 和原生脚本“尚未实现”，与当前源码及实施记录不一致。本次仅更正该进度表述；新的分层建议仍需实施后才写为现行架构。

## 6. 本轮实际验证

使用项目 `gameframe-validate` 流程选择与此次审查相关的检查，没有修改运行时代码或 Unity 状态。

| 验证 | 本轮结果 |
| --- | --- |
| 服务端/连接测试 | `dotnet test NetworkServer.Tests --no-restore`：59/59，0 跳过；显式指定升级后的 macOS universal Release 原生库，KCP 实际执行 |
| 协议/生成器测试 | `dotnet test NetworkProtocol.Tests --no-restore`：178/178；负例测试打印的漂移/Obsolete 提示属于预期断言，不是仓库实际漂移 |
| 生成产物一致性 | `dotnet run --project NetworkProtocol --no-restore -- check --artifacts`：退出码 0 |
| WSS 停机探针 | 实际 Kestrel/ClientWebSocket；2 秒 host timeout 被消耗（2014ms），最终 connections=0、owned_bytes=0 |
| KCP 错误关联探针 | 实际 DTLS/UDP，错误 correlation 回复被服务端接受，客户端 late=1/timeout=1；关闭后 client/server owned=0 |
| 差异检查 | 文档差异与空白检查；本轮不改协议、生成器或运行时代码 |

探针位于系统临时目录，验证后清理。测试通过只证明覆盖场景，不证明新的分层已实现，也不能代替源码审查。

未在本轮执行 Unity 编译/测试、原生重新构建、Windows/Linux 宿主实跑、IL2CPP 链接/裁剪、移动设备、远程 CI、生产负载与长期 soak；此前实施记录中的证据不计为本轮新验证。完整托管元数据计账、服务端完整诊断、全故障/容量矩阵仍属于后续框架验收。

## 7. 逐文件审查清单

以下清单记录本轮完整读取的项目文件；本次范围内无剩余未读项。文档与源码逐项核对，测试断言、生成代码和第三方 KCP 未以名称或已有测试结果代替阅读。清单只声明第 1 节限定的项目源码与依赖集成范围。

共 116 个源码、工程、配置、生成产物、测试、构建/安装脚本与文档文件：

- [x] [.github/workflows/network.yml](../.github/workflows/network.yml)
- [x] [Docs/ErrorContract.md](../Docs/ErrorContract.md)
- [x] [Docs/Network.md](../Docs/Network.md)
- [x] [Docs/NetworkDesign.md](../Docs/NetworkDesign.md)
- [x] [Docs/NetworkImplementation.md](../Docs/NetworkImplementation.md)
- [x] [Docs/NetworkNativeDesign.md](../Docs/NetworkNativeDesign.md)
- [x] [Docs/NetworkReview.md](../Docs/NetworkReview.md)
- [x] [Docs/Scope.md](../Docs/Scope.md)
- [x] [DotNet/Game.Server/AssemblyInfo.cs](../DotNet/Game.Server/AssemblyInfo.cs)
- [x] [DotNet/Game.Server/Game.Server.csproj](../DotNet/Game.Server/Game.Server.csproj)
- [x] [DotNet/DotNet.sln](../DotNet/DotNet.sln)
- [x] [DotNet/Game.Server/Login/AccountSessions.cs](../DotNet/Game.Server/Login/AccountSessions.cs)
- [x] [DotNet/GameFrame.Server/KcpListenerHost.cs](../DotNet/GameFrame.Server/KcpListenerHost.cs)
- [x] [DotNet/Game.Server/LocalKcpService.cs](../DotNet/Game.Server/LocalKcpService.cs)
- [x] [DotNet/GameFrame.Server/WssServer.cs](../DotNet/GameFrame.Server/WssServer.cs)
- [x] [DotNet/GameFrame.Server/ServerConnection.cs](../DotNet/GameFrame.Server/ServerConnection.cs)
- [x] [DotNet/Game.Server/Login/LoginOptions.cs](../DotNet/Game.Server/Login/LoginOptions.cs)
- [x] [DotNet/Game.Server/Program.cs](../DotNet/Game.Server/Program.cs)
- [x] [DotNet/README.md](../DotNet/README.md)
- [x] [DotNet/global.json](../DotNet/global.json)
- [x] [DotNet/Game.Server/packages.lock.json](../DotNet/Game.Server/packages.lock.json)
- [x] [Protocol/Common/ResultProto.proto](../Protocol/Common/ResultProto.proto)
- [x] [Protocol/Common/net_options.proto](../Protocol/Common/net_options.proto)
- [x] [Protocol/Diagnostics/DiagnosticsProto.proto](../Protocol/Diagnostics/DiagnosticsProto.proto)
- [x] [Protocol/Diagnostics/DiagnosticsService.proto](../Protocol/Diagnostics/DiagnosticsService.proto)
- [x] [Protocol/GameFrame.Protocol.projitems](../Protocol/GameFrame.Protocol.projitems)
- [x] [Protocol/Generated/golden-vectors.json](../Protocol/Generated/golden-vectors.json)
- [x] [Protocol/Generated/protocol.manifest.json](../Protocol/Generated/protocol.manifest.json)
- [x] [Protocol/Generated/protocol.sha256](../Protocol/Generated/protocol.sha256)
- [x] [Protocol/README.md](../Protocol/README.md)
- [x] [Protocol/Session/SessionProto.proto](../Protocol/Session/SessionProto.proto)
- [x] [Protocol/Session/SessionService.proto](../Protocol/Session/SessionService.proto)
- [x] [Protocol/World/WorldProto.proto](../Protocol/World/WorldProto.proto)
- [x] [Protocol/World/WorldService.proto](../Protocol/World/WorldService.proto)
- [x] [Tools/NetworkProtocol.Tests/GeneratorTests.cs](../Tools/NetworkProtocol.Tests/GeneratorTests.cs)
- [x] [Tools/NetworkProtocol.Tests/NetworkOptionsTests.cs](../Tools/NetworkProtocol.Tests/NetworkOptionsTests.cs)
- [x] [Tools/NetworkProtocol.Tests/NetworkProtocol.Tests.csproj](../Tools/NetworkProtocol.Tests/NetworkProtocol.Tests.csproj)
- [x] [Tools/NetworkProtocol.Tests/ProtoGenerationTests.cs](../Tools/NetworkProtocol.Tests/ProtoGenerationTests.cs)
- [x] [Tools/NetworkProtocol.Tests/packages.lock.json](../Tools/NetworkProtocol.Tests/packages.lock.json)
- [x] [Tools/NetworkProtocol/AssemblyInfo.cs](../Tools/NetworkProtocol/AssemblyInfo.cs)
- [x] [Tools/NetworkProtocol/MemoryPackProtocolGenerator.cs](../Tools/NetworkProtocol/MemoryPackProtocolGenerator.cs)
- [x] [Tools/NetworkProtocol/NetworkProtocol.csproj](../Tools/NetworkProtocol/NetworkProtocol.csproj)
- [x] [Tools/NetworkProtocol/Program.cs](../Tools/NetworkProtocol/Program.cs)
- [x] [Tools/NetworkProtocol/ProtoProtocolGenerator.cs](../Tools/NetworkProtocol/ProtoProtocolGenerator.cs)
- [x] [Tools/NetworkProtocol/ProtocolRouteDefinition.cs](../Tools/NetworkProtocol/ProtocolRouteDefinition.cs)
- [x] [Tools/NetworkProtocol/ProtocolSamples.cs](../Tools/NetworkProtocol/ProtocolSamples.cs)
- [x] [Tools/NetworkProtocol/packages.lock.json](../Tools/NetworkProtocol/packages.lock.json)
- [x] [Tools/NetworkServer.Tests/ConnectionRaceTests.cs](../Tools/NetworkServer.Tests/ConnectionRaceTests.cs)
- [x] [Tools/NetworkServer.Tests/KcpLoopbackTests.cs](../Tools/NetworkServer.Tests/KcpLoopbackTests.cs)
- [x] [Tools/NetworkServer.Tests/KcpSourceBudgetTests.cs](../Tools/NetworkServer.Tests/KcpSourceBudgetTests.cs)
- [x] [Tools/NetworkServer.Tests/NetworkServer.Tests.csproj](../Tools/NetworkServer.Tests/NetworkServer.Tests.csproj)
- [x] [Tools/NetworkServer.Tests/ServerHeartbeatTests.cs](../Tools/NetworkServer.Tests/ServerHeartbeatTests.cs)
- [x] [Tools/NetworkServer.Tests/WssServerTests.cs](../Tools/NetworkServer.Tests/WssServerTests.cs)
- [x] [Tools/NetworkServer.Tests/packages.lock.json](../Tools/NetworkServer.Tests/packages.lock.json)
- [x] [Tools/README.md](../Tools/README.md)
- [x] [Tools/global.json](../Tools/global.json)
- [x] [Native/Kcp/third_party/kcp/LICENSE](../Native/Kcp/third_party/kcp/LICENSE)
- [x] [Native/Kcp/third_party/kcp/ikcp.c](../Native/Kcp/third_party/kcp/ikcp.c)
- [x] [Native/Kcp/third_party/kcp/ikcp.h](../Native/Kcp/third_party/kcp/ikcp.h)
- [x] [Unity/Assets/Scripts/Hotfix/Game.Hotfix.asmdef](../Unity/Assets/Scripts/Hotfix/Game.Hotfix.asmdef)
- [x] [Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetMessages.cs](../Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetMessages.cs)
- [x] [Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetProtocol.cs](../Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetProtocol.cs)
- [x] [Unity/Assets/Scripts/Runtime/AssemblyInfo.cs](../Unity/Assets/Scripts/Runtime/AssemblyInfo.cs)
- [x] [Unity/Assets/Scripts/Runtime/GameFrame.Runtime.asmdef](../Unity/Assets/Scripts/Runtime/GameFrame.Runtime.asmdef)
- [x] [Unity/Assets/Scripts/Runtime/Lifecycle/GameRuntime.cs](../Unity/Assets/Scripts/Runtime/Lifecycle/GameRuntime.cs)
- [x] [Unity/Assets/Scripts/Runtime/Lifecycle/GameRuntimeConfig.cs](../Unity/Assets/Scripts/Runtime/Lifecycle/GameRuntimeConfig.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Connection/ConnectionContext.cs](../Unity/Assets/Scripts/Runtime/Network/Connection/ConnectionContext.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Connection/PendingDeadlineHeap.cs](../Unity/Assets/Scripts/Runtime/Network/Connection/PendingDeadlineHeap.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Messaging/NetProtocolCatalog.cs](../Unity/Assets/Scripts/Runtime/Network/Messaging/NetProtocolCatalog.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkClient.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkClient.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkConnectOptions.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkConnectOptions.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkDiagnostics.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkDiagnostics.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkException.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkException.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkOptions.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkOptions.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkRuntime.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkRuntime.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Protocol/ControlCodec.cs](../Unity/Assets/Scripts/Runtime/Network/Protocol/ControlCodec.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Protocol/FrameCodec.cs](../Unity/Assets/Scripts/Runtime/Network/Protocol/FrameCodec.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetProtocolContracts.cs](../Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetProtocolContracts.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetWireReader.cs](../Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetWireReader.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/IFrameTransport.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/IFrameTransport.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpNative.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpNative.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpTransport.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpTransport.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpWorker.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpWorker.cs)
- [x] [Native/Kcp/ABI.md](../Native/Kcp/ABI.md)
- [x] [Native/Kcp/CMakeLists.txt](../Native/Kcp/CMakeLists.txt)
- [x] [Native/Kcp/README.md](../Native/Kcp/README.md)
- [x] [Native/Kcp/build/build.py](../Native/Kcp/build/build.py)
- [x] [Native/Kcp/build/install.py](../Native/Kcp/build/install.py)
- [x] [Native/Kcp/build/prepare_kcp.py](../Native/Kcp/build/prepare_kcp.py)
- [x] [Native/Kcp/build/prepare_mbedtls.py](../Native/Kcp/build/prepare_mbedtls.py)
- [x] [Native/Kcp/build/verify_windows.py](../Native/Kcp/build/verify_windows.py)
- [x] [Native/Kcp/build/windows-llvm-mingw.cmake](../Native/Kcp/build/windows-llvm-mingw.cmake)
- [x] [Native/Kcp/config/crypto_config.h](../Native/Kcp/config/crypto_config.h)
- [x] [Native/Kcp/config/mbedtls_config.h](../Native/Kcp/config/mbedtls_config.h)
- [x] [Native/Kcp/config/threading_alt.h](../Native/Kcp/config/threading_alt.h)
- [x] [Native/Kcp/include/gfnet.h](../Native/Kcp/include/gfnet.h)
- [x] [Native/Kcp/src/gfnet_connection.c](../Native/Kcp/src/gfnet_connection.c)
- [x] [Native/Kcp/src/gfnet_internal.h](../Native/Kcp/src/gfnet_internal.h)
- [x] [Native/Kcp/src/gfnet_listener.c](../Native/Kcp/src/gfnet_listener.c)
- [x] [Native/Kcp/src/gfnet_memory.c](../Native/Kcp/src/gfnet_memory.c)
- [x] [Native/Kcp/tests/allocation_safety.c](../Native/Kcp/tests/allocation_safety.c)
- [x] [Native/Kcp/tests/fastack_regression.cpp](../Native/Kcp/tests/fastack_regression.cpp)
- [x] [Native/Kcp/tests/gfnet_regression.c](../Native/Kcp/tests/gfnet_regression.c)
- [x] [Native/Kcp/tests/verify_allocation_safety.py](../Native/Kcp/tests/verify_allocation_safety.py)
- [x] [Native/Kcp/tests/verify_fastack.py](../Native/Kcp/tests/verify_fastack.py)
- [x] [Native/Kcp/third_party/mbedtls.lock.json](../Native/Kcp/third_party/mbedtls.lock.json)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/OwnedFrame.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/OwnedFrame.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketFrameTransport.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketFrameTransport.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketTransport.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketTransport.cs)
- [x] [Unity/Assets/Scripts/Tests/EditMode/GameFrame.Tests.EditMode.asmdef](../Unity/Assets/Scripts/Tests/EditMode/GameFrame.Tests.EditMode.asmdef)
- [x] [Unity/Assets/Scripts/Tests/EditMode/Network/NetworkBufferTests.cs](../Unity/Assets/Scripts/Tests/EditMode/Network/NetworkBufferTests.cs)
- [x] [Unity/Assets/Scripts/Tests/EditMode/Network/NetworkMemoryPackTests.cs](../Unity/Assets/Scripts/Tests/EditMode/Network/NetworkMemoryPackTests.cs)
- [x] [Unity/Assets/Scripts/Tests/EditMode/Network/NetworkProtocolTests.cs](../Unity/Assets/Scripts/Tests/EditMode/Network/NetworkProtocolTests.cs)
- [x] [Unity/Assets/Scripts/Tests/PlayMode/GameFrame.Tests.PlayMode.asmdef](../Unity/Assets/Scripts/Tests/PlayMode/GameFrame.Tests.PlayMode.asmdef)
- [x] [Unity/Assets/Scripts/Tests/PlayMode/NetworkKcpLoopbackTests.cs](../Unity/Assets/Scripts/Tests/PlayMode/NetworkKcpLoopbackTests.cs)
- [x] [Unity/Assets/Scripts/Tests/PlayMode/NetworkLifecycleTests.cs](../Unity/Assets/Scripts/Tests/PlayMode/NetworkLifecycleTests.cs)

原生目录迁移说明：本记录中的源码清单与链接已更新为公共 `Native/Kcp` 路径；先前审查和运行证据保留其原有时间与范围，迁移后的验证另见 NetworkImplementation.md。

## 8. 已实施的服务端拆分（2026-10-12）

本节描述本次修改后的代码，第 1～7 节保留拆分前全量审查与历史证据。当前依赖方向为：

```text
Game.Server ──→ GameFrame.Server ──→ GameFrame.Network.Core
     └────────→ Game.Protocol ─────→ GameFrame.Network.Core
                              └───→ MemoryPack

GameFrame.Network.Core / GameFrame.Server 均不引用游戏协议或业务宿主
Protocol/*.proto → 生成 C# → Unity Game.Hotfix 与 .NET Game.Protocol 同源编译
```

### 主要问题的处理

| 原审查项 | 当前结果 |
| --- | --- |
| WSS 框架直接依赖账号和游戏路由 | 已移除。LobbyApplication 与 Login/ 下的 LoginHandlers、LoginSession、AccountSessions、LocalAccountSettings 位于 Game.Server；连接只使用 catalog、容量与应用接口 |
| KCP 内部 raw handler，缺少主动发送/关闭 | 已替换为公开 ServerApplication/ServerConnectionContext/ServerSession；DTO RPC/Notify、主动可靠/不可靠 Push、Disconnect/CloseAsync、Connected/Closed 和终态均可接入 |
| 真实 WSS 入口关闭顺序 | WssServer 实现 IHostedLifecycleService，StoppingAsync 在所有 hosted StopAsync 前停止准入并 join 网络；不依赖注册顺序 |
| 业务自行构造错误 opcode/correlation 的回复 | RPC 只返回 DTO，关联与字节头由框架从当前请求构造；KCP 增加可靠 Request correlation 严格递增规则 |
| 协议源码清单混入客户端实现 | Core 只链接 wire/codec/预算/native ABI/clock 等基础；projitems 只列生成协议，客户端实现只由客户端集成测试显式链接 |
| 清理异常跳过 join/资源/配额释放 | 清理步骤继续执行并保留首因；完成包含 handler/Closed/资源释放和 WSS 配额归还，重复停止使用一致结果 |
| 容量写死、诊断不完整 | WssServerOptions/KcpServerOptions 构造时验证并固定；完整服务端队列/native/派发/失败快照和生产容量验收仍待完成 |
| 生成 namespace 与 Unity 工程所有权 | .NET 已有独立 Game.Protocol 项目；生成的 namespace 和 Unity Hotfix 编译落点保留，协议指纹不变；本轮未迁移 Unity 协议程序集 |

关闭接口按 owner 使用：外部 owner 可 await CloseAsync/Completion，handler 或生命周期 hook 用 Disconnect/CloseAfterResponse，不能等待自己的退出。KCP 响应后关闭先将回复交给 native，再排队可靠 GFNP Close；继续排队的后续业务不再派发。受限配额提升与连接结束串行，结束上下文不能再影响 host 配额；端点只返回副本。正常 EOF 保留已接受可靠工作，故障/显式停机取消并 join，单 child 故障不扩大为 listener 故障。

登录、恢复、顶号、注销和本地 Echo 的行为保持在应用层。注销先进入应用关闭阶段，避免账号撤销触发 watchdog 抢先关闭而遮蔽响应；凭据响应在编码后清理，登录提交后的连接失败也清理已产生响应。没有添加自动重连、业务重试、失败回滚或通道降级。

### 本次验证证据与边界

| 检查 | 当前结果 |
| --- | --- |
| 依赖与 Release 构建 | solution、三个测试工程 locked restore；四工程 Release build：0 警告、0 错误；独立框架测试断言程序集不引用 Game.*，Core 不含客户端 worker |
| 协议/生成器 | 178/178，通过；check --artifacts 退出码 0，生成 C#、manifest/hash/向量不变 |
| 独立框架 | 10/10，通过；独立 catalog，无账号/游戏工程依赖；RPC/Notify/主动 Push、解码前拒绝、结束上下文/对端关闭原因、取消回调/Closed/响应清理故障、启动注册校验 |
| 应用与网络集成 | 61/61，通过，0 跳过；实际 Kestrel/WSS、账号登录/恢复/注销、client pending/期限及真实 macOS DTLS/UDP；含主动 Push/公开 Close/旧连接隔离与响应后关闭；最终清理首因/日志修正后再跑 KCP 12/12 |
| host 停止 | 独立真实 Kestrel 测试使用 2 秒 timeout，停止耗时 <1500ms；连接/托管帧归零 |
| 真实发布入口 | Release 发布到系统临时目录，安装已有匹配 macOS 原生库；Game.Server.dll 无自定义 native resolver 启动 listener，Ctrl+C 退出码 0 |
| Unity Editor | 6000.3.19f1，Console 错误 0；网络 buffer/protocol/MemoryPack EditMode 143/143 |
| Unity Player 脚本 | StandaloneOSX 编译通过，43 个程序集；Launch 场景、播放状态、EditorSettings 和目标平台保持原值 |
| CI | 新独立框架测试已接入 network.yml 的路径筛选、locked restore 和 test；未推送或远程执行 |

本次没有修改 native C/ABI、重新构建五平台原生库，也没有执行 Windows/Linux 实跑、IL2CPP 原生链接/裁剪、移动设备、长时间弱网/容量矩阵；既有原生构建证据不计为本次新验证。世界业务与 WebGL 继续延后。测试通过不能替代第 7 节全量源码审查，项目集成审查也不能宣称完成上游密码库安全审计。

### 拆分后维护文件核对清单

在先前 116 文件清单基础上，完整核对当前 DotNet 源码、工程/依赖锁、组合入口、选项、接口、生命周期和说明，以及本次变化的共享编译边界与测试。当前服务端维护文件如下；没有未读或受阻的服务端实施文件：

- [x] [DotNet/Directory.Build.props](../DotNet/Directory.Build.props)
- [x] [DotNet/DotNet.sln](../DotNet/DotNet.sln)
- [x] [DotNet/Game.Protocol/Game.Protocol.csproj](../DotNet/Game.Protocol/Game.Protocol.csproj)
- [x] [DotNet/Game.Protocol/packages.lock.json](../DotNet/Game.Protocol/packages.lock.json)
- [x] [DotNet/Game.Server/Login/AccountSessions.cs](../DotNet/Game.Server/Login/AccountSessions.cs)
- [x] [DotNet/Game.Server/AssemblyInfo.cs](../DotNet/Game.Server/AssemblyInfo.cs)
- [x] [DotNet/Game.Server/Game.Server.csproj](../DotNet/Game.Server/Game.Server.csproj)
- [x] [DotNet/Game.Server/LobbyApplication.cs](../DotNet/Game.Server/LobbyApplication.cs)
- [x] [DotNet/Game.Server/Login/LoginHandlers.cs](../DotNet/Game.Server/Login/LoginHandlers.cs)
- [x] [DotNet/Game.Server/Login/LoginSession.cs](../DotNet/Game.Server/Login/LoginSession.cs)
- [x] [DotNet/Game.Server/LocalEnvironment.cs](../DotNet/Game.Server/LocalEnvironment.cs)
- [x] [DotNet/Game.Server/LocalWssService.cs](../DotNet/Game.Server/LocalWssService.cs)
- [x] [DotNet/Game.Server/Login/LoginOptions.cs](../DotNet/Game.Server/Login/LoginOptions.cs)
- [x] [DotNet/Game.Server/LocalKcpService.cs](../DotNet/Game.Server/LocalKcpService.cs)
- [x] [DotNet/Game.Server/Program.cs](../DotNet/Game.Server/Program.cs)
- [x] [DotNet/Game.Server/packages.lock.json](../DotNet/Game.Server/packages.lock.json)
- [x] [DotNet/GameFrame.Network.Core/AssemblyInfo.cs](../DotNet/GameFrame.Network.Core/AssemblyInfo.cs)
- [x] [DotNet/GameFrame.Network.Core/GameFrame.Network.Core.csproj](../DotNet/GameFrame.Network.Core/GameFrame.Network.Core.csproj)
- [x] [DotNet/GameFrame.Network.Core/packages.lock.json](../DotNet/GameFrame.Network.Core/packages.lock.json)
- [x] [DotNet/GameFrame.Server/AssemblyInfo.cs](../DotNet/GameFrame.Server/AssemblyInfo.cs)
- [x] [DotNet/GameFrame.Server/GameFrame.Server.csproj](../DotNet/GameFrame.Server/GameFrame.Server.csproj)
- [x] [DotNet/GameFrame.Server/KcpListenerHost.cs](../DotNet/GameFrame.Server/KcpListenerHost.cs)
- [x] [DotNet/GameFrame.Server/KcpServerOptions.cs](../DotNet/GameFrame.Server/KcpServerOptions.cs)
- [x] [DotNet/GameFrame.Server/README.md](../DotNet/GameFrame.Server/README.md)
- [x] [DotNet/GameFrame.Server/ServerApplication.cs](../DotNet/GameFrame.Server/ServerApplication.cs)
- [x] [DotNet/GameFrame.Server/ServerConnection.cs](../DotNet/GameFrame.Server/ServerConnection.cs)
- [x] [DotNet/GameFrame.Server/WssServer.cs](../DotNet/GameFrame.Server/WssServer.cs)
- [x] [DotNet/GameFrame.Server/WssServerOptions.cs](../DotNet/GameFrame.Server/WssServerOptions.cs)
- [x] [DotNet/GameFrame.Server/packages.lock.json](../DotNet/GameFrame.Server/packages.lock.json)
- [x] [DotNet/README.md](../DotNet/README.md)
- [x] [DotNet/global.json](../DotNet/global.json)
- [x] [Tools/NetworkFramework.Tests/FrameworkBoundaryTests.cs](../Tools/NetworkFramework.Tests/FrameworkBoundaryTests.cs)
- [x] [Tools/NetworkFramework.Tests/NetworkFramework.Tests.csproj](../Tools/NetworkFramework.Tests/NetworkFramework.Tests.csproj)
- [x] [Tools/NetworkFramework.Tests/packages.lock.json](../Tools/NetworkFramework.Tests/packages.lock.json)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkClock.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkClock.cs)

## 9. 应用启动与登录目录调整（2026-10-12）

本次在第 8 节分层基础上整理应用流程。Program 原 for 循环用于遍历并验证命令行参数；当前只支持一个 --profile 参数对，改为直接检查参数形状并选择启动入口。LocalWssService 封装配置、证书、账号、应用、HTTPS host 和路由配置，持有相应资源；LocalKcpService 自行读取所需 local 配置。共享环境读取仅限应用中的 LocalEnvironment。

Login/ 下完整保留本地账号配置、AccountSessions、LoginHandlers 和 LoginSession。处理器管理登录/恢复/注销、IP 限流和敏感 DTO 清理；连接会话负责阶段准入、权限复查、登录期限、绑定和关闭解绑。LobbyApplication 显式组合登录模块、本地 Echo 与尚未接入的世界路由。登录逻辑未移入框架，没有新增反射扫描、兼容接口、自动重试或回滚。

```text
Program → LocalWssService → LobbyApplication → LoginHandlers / LoginSession
                         ├→ Login/AccountSessions / LocalAccountSettings
                         └→ WssServer hosted lifecycle → Kestrel HTTPS /network
Program → LocalKcpService → KcpListenerHost → 公共 native 库
```

源码核对覆盖 Game.Server 全部应用源码与工程、迁移后的调用方及相关测试引用，并对照既有框架生命周期契约。配置/初始化失败停止后续步骤；证书无效时释放证书；HTTP host 创建后即建立 await using 所有权，再配置管线；host 停机 join 网络后才释放账号配置与证书。Login 的准入、派发复查、顶号、恢复轮换、注销响应优先和敏感数据清理保留。第 8 节框架全量审查仍是框架依据，本节不表示重新执行上游库或全平台安全审计。此调整范围没有未读或受阻的应用实施文件。

新增验证证据：四工程 Release build 0 警告、0 错误；NetworkServer.Tests 61/61，0 跳过，使用已安装 macOS 原生库完成 WSS/DTLS 集成。6 组真实进程参数/缺失配置探针均拒绝错误配置。实际 local-wss 入口使用临时 localhost SAN 证书，正常 TLS 验证及 WebSocket 升级后，在连接等待 Hello 时 Ctrl+C，停机约 23ms、退出码 0。Release 临时发布经 install.py 安装已有匹配库，无自定义 native resolver 的 local-kcp-dtls 入口注册临时 grant、启动 listener，Ctrl+C 约 11ms、退出码 0。停机耗时仅为本机单次探针证据，不作为性能保证。

临时证书、凭据与发布目录均清理，未修改系统证书信任。本次只调整 .NET 应用，不重复执行 Unity、原生构建、Windows/Linux/移动设备或 IL2CPP 验证；这些边界仍见第 8 节。


## 10. PostgreSQL 账号接入与启动流程调整（2026-10-12）

原固定测试账号要求开发者在启动阶段配置玩家凭据，只支持一个账号。局部将环境变量改成默认值仍不能满足客户端正常注册登录，也会把测试身份混入正式流程；本次调整为证书/数据库初始化与玩家请求分离。账号数据库选择PostgreSQL，应用AccountService只依赖IAccountRepository；PostgresAccountRepository拥有参数化数据访问，网络Core/Server没有数据库依赖。LocalAccountSettings删除，LoginOptions只保留策略。

审查/核对范围为当前Game.Server全部应用实现、接口、工程/依赖锁和运行配置，账号表SQL、local compose，注册/结果码Proto与双端生成调用边界；相关WSS/Heartbeat/KCP应用注册、数据库测试、协议目录/向量测试与CI配置逐项核对。重点核对无玩家配置启动、注册未登录、登录前拒绝、异步数据库/密码计算、取消后的会话提交、每账号顶号与恢复、旧epoch异步结果、Logout/Detach和网络停止后释放账号/连接池。本次不是重新审计第三方数据库/密码库或复审全部native源码；第7～8节框架完整审查记录继续保留。

当前链为 Program→LocalWssService→数据库/证书校验→LobbyApplication→LoginHandlers→AccountService→IAccountRepository/PostgreSQL；认证结果回到AccountSessions在锁内提交，锁外关闭旧绑定。注册增加生成路由1004，成功不提升权限或延长期限。账号持久化、不同账号会话独立，单进程内存会话最多1024；服务重启需要显式重新登录，多实例仲裁尚未实施。密码PBKDF2-SHA256600000次、随机盐和固定时间比较；认证最多4个在途，不使用无界排队或运行时内存回退。

源码核对未发现此范围内未完成的接线或受阻项；生产hosting/容量、跨进程会话、密码找回/封禁/第三方登录以及世界授权仍是明确未实施范围。实际验证分别为：Release build0警告错误；协议181/181、框架10/10、服务端94/94（含真实PostgreSQL/WSS/macOS DTLS，0跳过）；实际入口Register/Login/Echo、重启持久账号与递增epoch、Ctrl+C退出0；Unity Editor错误0、相关EditMode146/146、StandaloneOSX Player脚本43程序集。Docker/CI远程运行、其他平台和IL2CPP未执行。源码核对与这些执行证据不能相互替代。

新增/替换维护文件：

- [x] [LoginOptions.cs](../DotNet/Game.Server/Login/LoginOptions.cs)
- [x] [IAccountRepository.cs](../DotNet/Game.Server/Login/IAccountRepository.cs)
- [x] [AccountService.cs](../DotNet/Game.Server/Login/AccountService.cs)
- [x] [PasswordHasher.cs](../DotNet/Game.Server/Login/PasswordHasher.cs)
- [x] [PostgresAccountRepository.cs](../DotNet/Game.Server/Login/PostgresAccountRepository.cs)
- [x] [001_accounts.sql](../DotNet/Game.Server/Database/001_accounts.sql)
- [x] [compose.yaml](../DotNet/compose.yaml)
- [x] [PostgresAccountTests.cs](../Tools/NetworkServer.Tests/PostgresAccountTests.cs)
- [x] [PostgresTestDatabase.cs](../Tools/NetworkServer.Tests/PostgresTestDatabase.cs)
- [x] [TestAccountRepository.cs](../Tools/NetworkServer.Tests/TestAccountRepository.cs)
- [x] [ServerAccounts.md](ServerAccounts.md)


## 11. 当前服务端全量审查：启动与账号生命周期（2026-10-12）

本次用户要求检查服务端是否还保留“启动即验证玩家信息/其他玩家数据”等旧联调逻辑，并检查不合理流程。本节记录当前源码状态；前面的实施与通过记录不能证明本次发现的边界已修复。本轮没有修改服务端、协议或原生运行时代码，只补充本审查记录。

### 范围、调用链与启动结论

本轮完整阅读 143 个仓库文件，阅读时共 26130 行；完整清单见下文。覆盖 DotNet 四工程全部维护代码、接口、配置、依赖锁与说明，直接编入服务器或回归工程的共享网络代码及调用方、账号 SQL/数据库工具、全部 .NET 网络/协议/框架测试及其链接的 Unity 协议测试、proto/生成代码/生成工具、公共 native 与仓库内 KCP 全文、构建安装及 CI 脚本和相关设计文档。没有未读或受阻的范围内仓库文件。

上游 Npgsql、ASP.NET Core、MemoryPack、Roslyn、protobuf-net、mbedTLS/TF-PSA 算法实现不作为本次第三方全源码安全审计对象；覆盖其锁定版本、配置、仓库内接入与错误/资源边界。Unity 独立启动/UI/场景业务没有纳入“当前服务器”范围；共享客户端实现已读用于核对服务器和测试调用，不据此声称完成整个 Unity 项目的审查。

当前调用链：

```text
Program
  → LocalWssService
    → 证书与运行配置
    → PostgresDatabase.ValidateAsync：SELECT 1
    → PostgresAccountRepository.ValidateAsync：SELECT ... FROM accounts LIMIT 0
    → AccountService / AccountSessions / LobbyApplication
    → WssServer → Kestrel /network → ServerConnection
      → LoginSession 准入/派发复查/期限
      → 强类型 Register/Login/ResumeLogin/Logout handler
        → AccountService → IAccountRepository/PostgreSQL
        → AccountSessions 会话提交
  → LocalKcpService（独立传输联调入口）
    → 显式临时 grant → KcpListenerHost → gfnet → DTLS/KCP
```

**未发现启动时校验、加载、创建或预置玩家账号/密码/角色/其他玩家数据的旧逻辑。** SELECT 1 检查连接；LIMIT 0 检查账号表列是否可访问，不取玩家记录。玩家注册与密码认证只由客户端 RPC 触发。固定账号初始化只在测试夹具中存在，生产入口没有调用测试替身。

仍然存在且范围明确的开发行为：

- local-kcp-dtls 启动时从 GF_LOCAL_KCP_GRANT 文件登记一个 30 秒传输 grant，只监听 loopback、提供 Echo；没有玩家认证、数据库或世界业务。这不是正常账号到世界的授权入口。
- local WSS/Echo 支持最大 5 秒的显式请求延时；它属于联调诊断。
- 世界授权/入场业务尚未实现，路由明确拒绝或返回 WorldUnavailable，未伪装成成功。
- Program 只提供显式 local profile；当前拒绝的是未配置的生产 hosting，并非服务器启动时需要玩家密码。没有自动业务重试、回滚、密码回退、存储回退或传输降级。

### 已确认的问题与方案比较

| 优先级 | 问题、触发与后果 | 建议 |
| --- | --- | --- |
| P1 | AccountService 在异步数据库中分配 epoch，随后 handler 才提交 AccountSessions。后者只与当前存活 Session 比较；Logout/过期清理会删除比较依据。较新 epoch=2 会话注销后，迟到 epoch=1 结果可重新创建成功会话，恢复本应失效的旧登录。位置：AccountService.cs:67、LoginHandlers.cs:119、AccountSessions.cs:52/Remove。 | 不只给活跃 Session 加一个判断。无界保存每个历史账号最大 epoch 会引入新的常驻状态；一次数据库查询也不能消除查询与提交间的竞争。推荐由现有账号会话所有者管理有界的每账号操作生命周期，让 epoch 分配与登录提交、Resume/Logout/撤销按该账号协调；异步结果提交时校验操作资格，记录只在活跃会话或在途操作仍需要时保留，按引用/完成状态回收。不同账号不共用等待数据库的全局锁。 |
| P2 | AccountService 并发 4 满额、AccountSessions 容量满额、账号限频均把过载写成 StatusCode=3，设计第7.1节规定过载=1。尤其 Login 并发满额时既不清除 loginInFlight，也不安排关闭：本次请求收到错误，下一次认证被拒绝，空闲则等原 10 秒期限。Register 的 finally 会释放资格，Login 没有对应收尾。 | 统一“认证请求完成后的阶段转换”。按现有失败登录结束本次连接的契约，推荐过载返回 status1 后通过发送 owner 关闭；Register 若明确允许同连接再次请求，则释放其资格但不延长原登录期限。不要让部分异常路径留在“处理中”，也不加自动重试。 |
| P2 | Login/Resume 替换旧绑定调用 connection.Disconnect，直接取消/Abort，没有发送设计的 Close reason2。LoginSession.CheckDeadline 对过期/撤销统一返回5，与设计的4/7不符。客户端因此难以正确区分顶号、到期与认证失败。 | 会话所有者保留终止原因，框架发送 owner 接受外部有原因的关闭命令，有界发送 Close 后释放。不要在账号锁中等待网络，也不要将仅在 handler 回复后消费的 CloseAfterResponse 当作外部关闭入口。这个调整应统一 WSS/KCP 框架关闭语义，无需业务补偿计时器。 |
| P2 | 实际 LocalWssService 和 LocalKcpService 构造网络 host 时未传 ILogger，框架默认为 NullLogger。数据库/handler/清理异常虽被框架捕获并按协议处理，具体失败日志在当前服务入口被丢弃；现有 Kestrel HTTP 日志不能解释这些异常。 | 在应用组合入口把现有 host ILoggerFactory/ILogger 传入网络框架；KCP 联调入口同样配置标准 logging。沿用框架已有日志边界，不新增日志管理系统，不记录密码/token/连接串。 |
| P3 | NetworkDesign 第18节仍引用删除的 DotNet.csproj；第4节及 DotNet README 部分“生产认证/provider 未配置”措辞与当前 Program 的 hosting 门禁不一致。 | 更新当前使用示例与现行认证/hosting 的区分，保留标明历史的实施记录。 |

按整体流程核对，服务端 Core/Server 未引用账号、数据库或游戏 DTO；账号/注册/恢复/顶号/注销均在 Game.Server。分层方向正确，以上问题集中在账号操作提交、失败收尾、关闭原因和启动组合接线，建议沿现有所有者修正，而非增加新的通用管理层。

### 本轮执行证据

- 四工程 locked restore 与 Release build 成功：0 警告、0 错误。
- 生成器 check --artifacts 退出0。
- 协议/生成测试 181/181、独立框架测试 10/10、服务端测试 97/97，通过且0跳过；服务端包含真实 PostgreSQL、Kestrel/WSS 和 macOS KCP/DTLS。
- 数据库测试使用本轮独立临时 PostgreSQL18 cluster/随机端口，结束已停止并删除；未使用或修改开发账号数据库。DTLS 显式使用 shared-macos-universal-release 的 mbedTLS4.2.0 库，逐项核对清单中维护源码 hash 与库 hash 匹配；未重新构建 native。
- 临时 friend-assembly 探针直接调用当前实现，确定性复现两项：epoch2 Login→Logout→epoch1 Login 输出 Success、epoch1、current=True；4 个阻塞仓库操作占满认证配额后，第5个 Login 经真实 ServerApplication dispatcher 输出 status3、scheduledClose0、nextLoginAllowed=False、nextLoginClose5。探针帧预留最终为0。这是会话/应用派发复现，不冒充完整客户端端到端竞争实跑。
- 关闭原因及 logger 问题依据完整源码/调用链核验，本轮没有新增针对它们的线级关闭/日志探针。既有测试全部通过不覆盖上述两种新复现顺序，不能据此判定它们正确。
- 没有执行新的 Unity Editor/Player/IL2CPP、Windows/Linux/移动设备、远端CI、生产多实例或容量/长时间测试；用户正在运行的 Rider 服务、开发 PostgreSQL 和 Unity 设置未操作。临时探针与测试 cluster/日志结束后清理。

### 本轮完整阅读清单

下列勾选仅代表源码/文档阅读与调用核对，不代表对应平台已经实跑。

- [x] [.github/workflows/network.yml](../.github/workflows/network.yml)
- [x] [.gitignore](../.gitignore)
- [x] [Docs/ErrorContract.md](../Docs/ErrorContract.md)
- [x] [Docs/Network.md](../Docs/Network.md)
- [x] [Docs/NetworkDesign.md](../Docs/NetworkDesign.md)
- [x] [Docs/NetworkImplementation.md](../Docs/NetworkImplementation.md)
- [x] [Docs/NetworkNativeDesign.md](../Docs/NetworkNativeDesign.md)
- [x] [Docs/NetworkReview.md](../Docs/NetworkReview.md)
- [x] [Docs/NetworkServerReview.md](../Docs/NetworkServerReview.md)
- [x] [Docs/Scope.md](../Docs/Scope.md)
- [x] [Docs/ServerAccounts.md](../Docs/ServerAccounts.md)
- [x] [DotNet/Directory.Build.props](../DotNet/Directory.Build.props)
- [x] [DotNet/DotNet.sln](../DotNet/DotNet.sln)
- [x] [DotNet/Game.Protocol/Game.Protocol.csproj](../DotNet/Game.Protocol/Game.Protocol.csproj)
- [x] [DotNet/Game.Protocol/packages.lock.json](../DotNet/Game.Protocol/packages.lock.json)
- [x] [DotNet/Game.Server/AssemblyInfo.cs](../DotNet/Game.Server/AssemblyInfo.cs)
- [x] [DotNet/Game.Server/Database/001_accounts.sql](../DotNet/Game.Server/Database/001_accounts.sql)
- [x] [DotNet/Game.Server/Database/PostgresDatabase.cs](../DotNet/Game.Server/Database/PostgresDatabase.cs)
- [x] [DotNet/Game.Server/Game.Server.csproj](../DotNet/Game.Server/Game.Server.csproj)
- [x] [DotNet/Game.Server/LobbyApplication.cs](../DotNet/Game.Server/LobbyApplication.cs)
- [x] [DotNet/Game.Server/LocalEnvironment.cs](../DotNet/Game.Server/LocalEnvironment.cs)
- [x] [DotNet/Game.Server/LocalKcpService.cs](../DotNet/Game.Server/LocalKcpService.cs)
- [x] [DotNet/Game.Server/LocalWssService.cs](../DotNet/Game.Server/LocalWssService.cs)
- [x] [DotNet/Game.Server/Login/AccountService.cs](../DotNet/Game.Server/Login/AccountService.cs)
- [x] [DotNet/Game.Server/Login/AccountSessions.cs](../DotNet/Game.Server/Login/AccountSessions.cs)
- [x] [DotNet/Game.Server/Login/IAccountRepository.cs](../DotNet/Game.Server/Login/IAccountRepository.cs)
- [x] [DotNet/Game.Server/Login/LoginHandlers.cs](../DotNet/Game.Server/Login/LoginHandlers.cs)
- [x] [DotNet/Game.Server/Login/LoginOptions.cs](../DotNet/Game.Server/Login/LoginOptions.cs)
- [x] [DotNet/Game.Server/Login/LoginSession.cs](../DotNet/Game.Server/Login/LoginSession.cs)
- [x] [DotNet/Game.Server/Login/PasswordHasher.cs](../DotNet/Game.Server/Login/PasswordHasher.cs)
- [x] [DotNet/Game.Server/Login/PostgresAccountRepository.cs](../DotNet/Game.Server/Login/PostgresAccountRepository.cs)
- [x] [DotNet/Game.Server/Program.cs](../DotNet/Game.Server/Program.cs)
- [x] [DotNet/Game.Server/Properties/launchSettings.json](../DotNet/Game.Server/Properties/launchSettings.json)
- [x] [DotNet/Game.Server/packages.lock.json](../DotNet/Game.Server/packages.lock.json)
- [x] [DotNet/GameFrame.Network.Core/AssemblyInfo.cs](../DotNet/GameFrame.Network.Core/AssemblyInfo.cs)
- [x] [DotNet/GameFrame.Network.Core/GameFrame.Network.Core.csproj](../DotNet/GameFrame.Network.Core/GameFrame.Network.Core.csproj)
- [x] [DotNet/GameFrame.Network.Core/packages.lock.json](../DotNet/GameFrame.Network.Core/packages.lock.json)
- [x] [DotNet/GameFrame.Server/AssemblyInfo.cs](../DotNet/GameFrame.Server/AssemblyInfo.cs)
- [x] [DotNet/GameFrame.Server/GameFrame.Server.csproj](../DotNet/GameFrame.Server/GameFrame.Server.csproj)
- [x] [DotNet/GameFrame.Server/KcpListenerHost.cs](../DotNet/GameFrame.Server/KcpListenerHost.cs)
- [x] [DotNet/GameFrame.Server/KcpServerOptions.cs](../DotNet/GameFrame.Server/KcpServerOptions.cs)
- [x] [DotNet/GameFrame.Server/README.md](../DotNet/GameFrame.Server/README.md)
- [x] [DotNet/GameFrame.Server/ServerApplication.cs](../DotNet/GameFrame.Server/ServerApplication.cs)
- [x] [DotNet/GameFrame.Server/ServerConnection.cs](../DotNet/GameFrame.Server/ServerConnection.cs)
- [x] [DotNet/GameFrame.Server/WssServer.cs](../DotNet/GameFrame.Server/WssServer.cs)
- [x] [DotNet/GameFrame.Server/WssServerOptions.cs](../DotNet/GameFrame.Server/WssServerOptions.cs)
- [x] [DotNet/GameFrame.Server/packages.lock.json](../DotNet/GameFrame.Server/packages.lock.json)
- [x] [DotNet/README.md](../DotNet/README.md)
- [x] [DotNet/compose.yaml](../DotNet/compose.yaml)
- [x] [DotNet/global.json](../DotNet/global.json)
- [x] [Native/Kcp/ABI.md](../Native/Kcp/ABI.md)
- [x] [Native/Kcp/CMakeLists.txt](../Native/Kcp/CMakeLists.txt)
- [x] [Native/Kcp/README.md](../Native/Kcp/README.md)
- [x] [Native/Kcp/build/build.py](../Native/Kcp/build/build.py)
- [x] [Native/Kcp/build/install.py](../Native/Kcp/build/install.py)
- [x] [Native/Kcp/build/prepare_kcp.py](../Native/Kcp/build/prepare_kcp.py)
- [x] [Native/Kcp/build/prepare_mbedtls.py](../Native/Kcp/build/prepare_mbedtls.py)
- [x] [Native/Kcp/build/verify_windows.py](../Native/Kcp/build/verify_windows.py)
- [x] [Native/Kcp/build/windows-llvm-mingw.cmake](../Native/Kcp/build/windows-llvm-mingw.cmake)
- [x] [Native/Kcp/config/crypto_config.h](../Native/Kcp/config/crypto_config.h)
- [x] [Native/Kcp/config/mbedtls_config.h](../Native/Kcp/config/mbedtls_config.h)
- [x] [Native/Kcp/config/threading_alt.h](../Native/Kcp/config/threading_alt.h)
- [x] [Native/Kcp/include/gfnet.h](../Native/Kcp/include/gfnet.h)
- [x] [Native/Kcp/src/gfnet_connection.c](../Native/Kcp/src/gfnet_connection.c)
- [x] [Native/Kcp/src/gfnet_internal.h](../Native/Kcp/src/gfnet_internal.h)
- [x] [Native/Kcp/src/gfnet_listener.c](../Native/Kcp/src/gfnet_listener.c)
- [x] [Native/Kcp/src/gfnet_memory.c](../Native/Kcp/src/gfnet_memory.c)
- [x] [Native/Kcp/tests/allocation_safety.c](../Native/Kcp/tests/allocation_safety.c)
- [x] [Native/Kcp/tests/fastack_regression.cpp](../Native/Kcp/tests/fastack_regression.cpp)
- [x] [Native/Kcp/tests/gfnet_regression.c](../Native/Kcp/tests/gfnet_regression.c)
- [x] [Native/Kcp/tests/verify_allocation_safety.py](../Native/Kcp/tests/verify_allocation_safety.py)
- [x] [Native/Kcp/tests/verify_fastack.py](../Native/Kcp/tests/verify_fastack.py)
- [x] [Native/Kcp/third_party/kcp/LICENSE](../Native/Kcp/third_party/kcp/LICENSE)
- [x] [Native/Kcp/third_party/kcp/ikcp.c](../Native/Kcp/third_party/kcp/ikcp.c)
- [x] [Native/Kcp/third_party/kcp/ikcp.h](../Native/Kcp/third_party/kcp/ikcp.h)
- [x] [Native/Kcp/third_party/mbedtls.lock.json](../Native/Kcp/third_party/mbedtls.lock.json)
- [x] [Protocol/Common/ResultProto.proto](../Protocol/Common/ResultProto.proto)
- [x] [Protocol/Common/net_options.proto](../Protocol/Common/net_options.proto)
- [x] [Protocol/Diagnostics/DiagnosticsProto.proto](../Protocol/Diagnostics/DiagnosticsProto.proto)
- [x] [Protocol/Diagnostics/DiagnosticsService.proto](../Protocol/Diagnostics/DiagnosticsService.proto)
- [x] [Protocol/GameFrame.Protocol.projitems](../Protocol/GameFrame.Protocol.projitems)
- [x] [Protocol/Generated/golden-vectors.json](../Protocol/Generated/golden-vectors.json)
- [x] [Protocol/Generated/protocol.manifest.json](../Protocol/Generated/protocol.manifest.json)
- [x] [Protocol/Generated/protocol.sha256](../Protocol/Generated/protocol.sha256)
- [x] [Protocol/README.md](../Protocol/README.md)
- [x] [Protocol/Session/SessionProto.proto](../Protocol/Session/SessionProto.proto)
- [x] [Protocol/Session/SessionService.proto](../Protocol/Session/SessionService.proto)
- [x] [Protocol/World/WorldProto.proto](../Protocol/World/WorldProto.proto)
- [x] [Protocol/World/WorldService.proto](../Protocol/World/WorldService.proto)
- [x] [Tools/NetworkFramework.Tests/FrameworkBoundaryTests.cs](../Tools/NetworkFramework.Tests/FrameworkBoundaryTests.cs)
- [x] [Tools/NetworkFramework.Tests/NetworkFramework.Tests.csproj](../Tools/NetworkFramework.Tests/NetworkFramework.Tests.csproj)
- [x] [Tools/NetworkFramework.Tests/packages.lock.json](../Tools/NetworkFramework.Tests/packages.lock.json)
- [x] [Tools/NetworkProtocol.Tests/GeneratorTests.cs](../Tools/NetworkProtocol.Tests/GeneratorTests.cs)
- [x] [Tools/NetworkProtocol.Tests/NetworkOptionsTests.cs](../Tools/NetworkProtocol.Tests/NetworkOptionsTests.cs)
- [x] [Tools/NetworkProtocol.Tests/NetworkProtocol.Tests.csproj](../Tools/NetworkProtocol.Tests/NetworkProtocol.Tests.csproj)
- [x] [Tools/NetworkProtocol.Tests/ProtoGenerationTests.cs](../Tools/NetworkProtocol.Tests/ProtoGenerationTests.cs)
- [x] [Tools/NetworkProtocol.Tests/packages.lock.json](../Tools/NetworkProtocol.Tests/packages.lock.json)
- [x] [Tools/NetworkProtocol/AssemblyInfo.cs](../Tools/NetworkProtocol/AssemblyInfo.cs)
- [x] [Tools/NetworkProtocol/MemoryPackProtocolGenerator.cs](../Tools/NetworkProtocol/MemoryPackProtocolGenerator.cs)
- [x] [Tools/NetworkProtocol/NetworkProtocol.csproj](../Tools/NetworkProtocol/NetworkProtocol.csproj)
- [x] [Tools/NetworkProtocol/Program.cs](../Tools/NetworkProtocol/Program.cs)
- [x] [Tools/NetworkProtocol/ProtoProtocolGenerator.cs](../Tools/NetworkProtocol/ProtoProtocolGenerator.cs)
- [x] [Tools/NetworkProtocol/ProtocolRouteDefinition.cs](../Tools/NetworkProtocol/ProtocolRouteDefinition.cs)
- [x] [Tools/NetworkProtocol/ProtocolSamples.cs](../Tools/NetworkProtocol/ProtocolSamples.cs)
- [x] [Tools/NetworkProtocol/packages.lock.json](../Tools/NetworkProtocol/packages.lock.json)
- [x] [Tools/NetworkServer.Tests/ConnectionRaceTests.cs](../Tools/NetworkServer.Tests/ConnectionRaceTests.cs)
- [x] [Tools/NetworkServer.Tests/KcpLoopbackTests.cs](../Tools/NetworkServer.Tests/KcpLoopbackTests.cs)
- [x] [Tools/NetworkServer.Tests/KcpSourceBudgetTests.cs](../Tools/NetworkServer.Tests/KcpSourceBudgetTests.cs)
- [x] [Tools/NetworkServer.Tests/NetworkServer.Tests.csproj](../Tools/NetworkServer.Tests/NetworkServer.Tests.csproj)
- [x] [Tools/NetworkServer.Tests/PostgresAccountTests.cs](../Tools/NetworkServer.Tests/PostgresAccountTests.cs)
- [x] [Tools/NetworkServer.Tests/PostgresTestDatabase.cs](../Tools/NetworkServer.Tests/PostgresTestDatabase.cs)
- [x] [Tools/NetworkServer.Tests/ServerHeartbeatTests.cs](../Tools/NetworkServer.Tests/ServerHeartbeatTests.cs)
- [x] [Tools/NetworkServer.Tests/TestAccountRepository.cs](../Tools/NetworkServer.Tests/TestAccountRepository.cs)
- [x] [Tools/NetworkServer.Tests/WssServerTests.cs](../Tools/NetworkServer.Tests/WssServerTests.cs)
- [x] [Tools/NetworkServer.Tests/packages.lock.json](../Tools/NetworkServer.Tests/packages.lock.json)
- [x] [Tools/README.md](../Tools/README.md)
- [x] [Tools/global.json](../Tools/global.json)
- [x] [Tools/postgres.py](../Tools/postgres.py)
- [x] [Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetMessages.cs](../Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetMessages.cs)
- [x] [Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetProtocol.cs](../Unity/Assets/Scripts/Hotfix/Network/Generated/GameNetProtocol.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Connection/ConnectionContext.cs](../Unity/Assets/Scripts/Runtime/Network/Connection/ConnectionContext.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Connection/PendingDeadlineHeap.cs](../Unity/Assets/Scripts/Runtime/Network/Connection/PendingDeadlineHeap.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Messaging/NetProtocolCatalog.cs](../Unity/Assets/Scripts/Runtime/Network/Messaging/NetProtocolCatalog.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkClient.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkClient.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkClock.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkClock.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkConnectOptions.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkConnectOptions.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkDiagnostics.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkDiagnostics.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkException.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkException.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkOptions.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkOptions.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/NetworkRuntime.cs](../Unity/Assets/Scripts/Runtime/Network/NetworkRuntime.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Protocol/ControlCodec.cs](../Unity/Assets/Scripts/Runtime/Network/Protocol/ControlCodec.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Protocol/FrameCodec.cs](../Unity/Assets/Scripts/Runtime/Network/Protocol/FrameCodec.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetProtocolContracts.cs](../Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetProtocolContracts.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetWireReader.cs](../Unity/Assets/Scripts/Runtime/Network/ProtocolContracts/NetWireReader.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/IFrameTransport.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/IFrameTransport.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpNative.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpNative.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpTransport.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpTransport.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpWorker.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpWorker.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/OwnedFrame.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/OwnedFrame.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketFrameTransport.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketFrameTransport.cs)
- [x] [Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketTransport.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/WebSocketTransport.cs)
- [x] [Unity/Assets/Scripts/Tests/EditMode/Network/NetworkMemoryPackTests.cs](../Unity/Assets/Scripts/Tests/EditMode/Network/NetworkMemoryPackTests.cs)
- [x] [Unity/Assets/Scripts/Tests/EditMode/Network/NetworkProtocolTests.cs](../Unity/Assets/Scripts/Tests/EditMode/Network/NetworkProtocolTests.cs)

## 12. 第11节四项问题修复（2026-10-12）

本轮按用户授权修复第11节四项主要问题。源码核对沿用第11节完整文件清单与调用链，逐项复查本轮变更及相关调用、取消、释放、关闭和启动组合；历史复现记录保留，以下描述修复后的行为。

### 已实现与方案取舍

| 问题 | 最终实现 |
| --- | --- |
| 迟到 epoch 在注销/清理后重新建立会话 | AccountService 密码校验成功后调用 AccountSessions.LoginAsync：先在有界 AccountState 登记操作，再异步分配数据库 epoch，最后检查 revision/LatestEpoch 并提交。Logout/到期使撤销前已登记的操作失效；状态仅在仍有会话或在途操作时保留，finally 回收。不同账号不持锁等待 IO；没有新增历史账号永久记录、数据库复查或代际回滚。 |
| 认证过载 status 与阶段收尾错误 | 全局4并发、账号状态容量、IP/账号认证限额使用空正文 status1。所有认证 handler 的 finally 清除敏感字段并结束 loginInFlight。Login 过载响应后 Close1；Register 全局/账号过载释放资格，客户端可在原登录期限内显式再次请求。IP 准入过载拒绝后关闭；不增加重试队列。 |
| 顶号/到期/撤销的关闭原因丢失 | LoginSession 持有绑定记录，索引删除不删除其原因。Login/Resume 替换旧绑定使用 RequestClose(2)，期限使用4，撤销使用7，Logout 自身响应后1，凭据拒绝5。ServerConnectionContext 新增外部 RequestClose(reason)/CloseAsync(reason)，首次关闭原因保留；WSS/KCP 发送 owner 发送 Close，不等待阻塞 handler 才开始，丢弃待派发/未发送业务，有界结束传输后取消并 join。正常 EOF 仍排空已准入工作。已经关闭的绑定不能迟到提升权限；实际业务异常仍保留，不将任意 NetworkException 吞成正常关闭。 |
| 实际服务入口使用 NullLogger | LocalWssService 由 host DI 创建 WssServer 并注入 ILogger<WssServer>；host 拥有网络生命周期，停止/join 后才释放账号服务与数据库。LocalKcpService 持有标准 LoggerFactory 并注入 listener logger，工厂晚于 listener 释放。沿用既有 handler/连接/清理日志，不新增日志系统，不记录请求正文和凭据。 |

方案采用现有账号/连接所有者的生命周期协调。给原比较增加当前 Session 判断仍会在删除后失效；永久最大 epoch 表会扩大常驻内存；账号锁内等待网络会耦合两种生命周期。外部关闭独立交给发送 owner，响应后关闭保留给当前 handler，两种触发共用传输释放流程。

### 本轮源码覆盖

- [x] Login 全部实现：AccountService、AccountSessions、IAccountRepository、LoginHandlers、LoginSession，以及未改动的 LoginOptions、PasswordHasher、PostgresAccountRepository 的接口/取消调用关系。
- [x] LobbyApplication、LocalWssService、LocalKcpService；核对 Program、LocalEnvironment、数据库检查、配置与 host/服务/证书的关闭顺序。启动检查仍只验证运行配置、证书、数据库和账号表结构，玩家校验仅由客户端请求触发。
- [x] ServerApplication/ServerConnectionContext、ServerConnection、KcpListenerHost，及 WssServer 的配额/host 停机调用、网络选项和共享客户端的 Close 接收语义。检查短锁顺序、在途响应所有权、关闭期间 Push/权限提升、排队业务、peer close、native child retirement、取消与 Completion 发布。
- [x] 三个新增测试文件 AccountLifecycleTests、ServerCloseTests、WssLifecycleTests，以及更新后的 KcpLoopbackTests、PostgresAccountTests、ServerHeartbeatTests、WssServerTests 与仓库/数据库测试辅助代码；源码和构建引用均核对。
- [x] NetworkDesign、ServerAccounts、DotNet README、GameFrame.Server README、实施记录和验证入口。同步关闭 API、认证并发状态、logger 所有权；修正设计第18节已删除的 DotNet.csproj 示例及 hosting 门禁措辞。

本轮未修改 proto/生成产物、数据库 schema、原生源码/ABI、Unity 脚本、已有本机配置或 CI。上述生成工具/第三方/native 的第11节全量审查记录仍适用；没有未读或受阻的修复路径。

### 验证证据

- 新增23个回归用例；已有顶号/Resume WSS 用例增加 GFNP Close2 与 WebSocket close 确认断言，两组仓库均执行。
- AccountLifecycleTests：乱序提交、注销与到期清理后迟到结果、pending 容量、失败/取消/Dispose 回收、其他账号独立推进、Resume 轮换、过载收尾、IP 限额、撤销原因和迟到 Bind 权限。
- 实际 Kestrel/WSS：过载 status1 先于 Close1；Register 在同连接显式再次请求成功；到期 Close4、撤销 Close7；实际 LocalWssService 的 DI/管线记录原 handler 异常与 logger category，正文未进入日志。
- 实际 macOS KCP/DTLS：阻塞 handler 期间外部 Close2，重复 Close 不改原因，未派发请求不执行，新连接继续 Echo；handler 异常通过注入 logger 记录；关闭后 listener/客户端自有缓冲归零。WSS 可控 socket 同时验证关闭与 Push/权限提升的并发边界。
- Release 四工程编译0警告/错误；完整服务端120/120、独立框架10/10、协议/生成181/181，均0跳过；生成 check --artifacts 退出0。初次完整服务端回归119/120，新增模拟 socket 用例过早确认关闭导致断言落在取消后；调整其确认时序后完整复跑通过，不改变运行时取消语义。
- 数据库使用独立临时 PostgreSQL18 cluster/随机端口，测试创建各自 schema；运行结束已停止并删除 cluster。原生使用已有 shared-macos-universal-release 的 mbedTLS4.2.0 验证库，没有重新构建。
- 最后关闭故障路径补充保留已请求原因后，重新编译并复跑 ServerCloseTests、KcpLoopbackTests、ServerHeartbeatTests、WssLifecycleTests，共28/28通过、0跳过；涵盖正常 EOF、响应后关闭、外部关闭、顶号原因、原生 child 释放与 logger。随后收紧 WSS 收/发 owner 对已请求关闭的即时业务检查，重新编译并复跑受影响 WSS 关闭/watchdog/启动组合13/13通过、0跳过。
- 未执行新的 Unity Editor/Player/IL2CPP、Windows/Linux/移动设备、远程 CI、生产多实例或容量验收。没有操作用户正在运行的 Rider 服务、开发 PostgreSQL、证书信任或 Unity 设置。
