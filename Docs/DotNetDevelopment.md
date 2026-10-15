# 服务端 .NET 全局开发规范

适用于 `DotNet/` 的框架、协议、业务、数据库、启动与部署代码，以及双方共用的协议生成工具和 `Native/Kcp`。修改这些部分前阅读本文；运行与接入步骤见 [服务端使用说明](DotNetUsage.md)。Unity 侧的线程与会话规范见 [客户端网络开发规范](ClientNetworkDevelopment.md)，调用方法见 [客户端使用说明](ClientNetworkUsage.md)。所有路径相对仓库根目录。

保留 [NetworkDesign](NetworkDesign.md) 作为总体设计与验收契约，另外维护上述四份开发规范和使用说明。本文集中维护 .NET 开发规则及共享 GFNP、Proto→MemoryPack、原生 ABI 的实现细节，客户端规范引用公共契约；使用说明记录操作步骤与实际验证范围。设计决定变化同步更新 NetworkDesign，接口、配置或生命周期变化更新对应规范和说明，不另增重复的实施或审查文档。历史过程由 Git 保留，当前问题和验收范围更新对应章节。服务端日志完善见 [执行计划](#server-logging-plan)。

## 1. 全局原则与代码要求

- 正确性优先，保持实现简单；性能修改须有分配、吞吐或延迟测量依据。先比较局部修复和职责、流程调整，优先复用现有机制，不为单个功能增加通用管理器、Actor 平台或转发层。
- 全量审查是强制要求：先建立指定范围的文件清单和调用关系，再完整阅读全部实现、调用方、依赖、接口、配置、生成工具、生成代码、测试与构建安装脚本。第三方代码在范围内也须覆盖；搜索、抽样、只看差异或测试通过不能代替源码阅读。记录已读、未读与受阻项，未完成时不得声称完整审查。
- 从职责分层、状态转换、正常/失败路径、并发与取消、期限、资源所有权、关闭顺序和平台差异分析整个流程。汇报区分源码审查、编译、测试、平台实跑和容量验收。
- 不主动增加旧接口兼容、自动重试、失败回滚、静默降级或密码登录兜底。协议内部的 KCP/DTLS 重传不等于重新执行业务请求。
- C# 使用 4 空格、Allman 大括号，每行一条语句；if/else、循环、lock/using、try/catch/finally 必须展开。类型、方法和属性 PascalCase，参数/局部变量 camelCase，异步方法带 Async 后缀。遵循仓库 [AGENTS.md](../AGENTS.md)，第三方与生成代码不做手工格式统一。
- 不吞掉异常，不将业务失败伪装为成功或取消。只在能够处理或必须清理时捕获异常，保留首因，额外清理故障单独报告。遵循 [错误契约](ErrorContract.md)。
- 配置和初始化失败立即停止后续步骤；不创建默认玩家、自动建库/迁移、回退内存仓库或偷偷启用 local。删除实现前检查调用方、资源归属及生命周期。
- 保留用户已有修改、服务进程、数据库和环境设置。验证临时文件放系统临时目录并清理；不得为测试清空开发数据库或修改全局证书信任。

## 2. 工程、功能目录与依赖边界

SDK 由 `DotNet/global.json`、`Tools/global.json` 锁定为 10.0.401，工程目标 `net10.0`。Unity 共用源码保持 Unity 支持的语言/API，不把 .NET 10 DLL 放入 Unity；ConfigGenerator 使用 SDK 10，调用的 Luban 仍需要 .NET 8 运行时。

| 工程/目录 | 职责 | 依赖边界 |
| --- | --- | --- |
| `DotNet/GameFrame.Network.Core` | 同源 GFNP/control、路由/codec、缓冲预算、时钟与 native ABI | 不引用 Unity DLL、游戏协议、客户端 worker 或账号业务 |
| `DotNet/GameFrame.Server` | WSS/KCP host、连接与队列、强类型派发、Push、关闭 | 只引用 Core 与 ASP.NET Core，不引用 Game.Protocol/Game.Server/Npgsql |
| `DotNet/GameFrame.Logging` | 日志选项、实例元数据、NLog Provider、布局、有界异步输出、诊断与释放 | 引用标准 Logging 与 NLog；不引用网络/协议/业务，不使用全局 LogManager |
| `DotNet/Game.Protocol` | 编译双端同源的生成 DTO/catalog | 引用 Core、MemoryPack，导入 `Protocol/GameFrame.Protocol.projitems` |
| `DotNet/Game.Config` | 服务端 Luban 生成表与固定版本的二进制运行库 | 不依赖 Unity、网络框架或游戏协议 |
| `DotNet/ConfigGenerator` | Luban 离线生成与产物检查，提供 IDE 启动项 | 仅启动固定版本 Luban 进程，不引用 Game.Config 或其他运行时工程 |
| `DotNet/Game.Server` | 单业务可执行入口、应用组合、认证、数据库、配置加载、部署 | 引用 Server、Protocol、Config 与日志适配；玩家与玩法规则在这里实现 |
| `Protocol`、`DotNet/NetworkProtocol` | 唯一手写 IDL 与离线生成/检查工具 | 不新增第二套手写 DTO/opcode，不依赖已有生成产物才能生成 |
| `Native/Kcp` | 双端唯一 C 源码、依赖锁、ABI、构建安装与回归 | C 处理字节、KCP/DTLS，不解析 MemoryPack 游戏 DTO |

`DotNet/NetworkProtocol` 加入服务端解决方案，提供 Generate/Check IDE 启动项；Unity Editor 菜单通过外部 .NET 进程调用同一工具。生成器只依赖 Core 和生成/编译库，不引用 Game.Protocol，保持缺失既有生成文件时仍能运行。协议源、正式生成文件与共享源码引用位置保持一致，详见双方使用说明。

Core、客户端 Runtime 网络和 Server 网络统一使用 `GameFrame.Network`，目录分类不对应独立子命名空间。程序集名称、共享源码链接与依赖边界保持独立；同名空间不代表业务可以访问内部类型。双端生成 DTO、枚举、GameNetRoutes 和 GameNetProtocol 统一使用 `Game.Protocol`；生成器与游戏业务保留各自命名空间。协议完整类型名参与 manifest 和指纹，命名空间变更后必须重新生成并同步更新两端。

诊断 meter 的 `GameFrame.Server.Network.Kcp` 是独立指标标识，不是 C# 命名空间；本次保留该标识及既有采集配置。

`Game.Server/Program.cs` 验证 profile、加载游戏配置并选择启动入口；`LocalWssService`、`LocalKcpService` 持有各自配置和资源。`LobbyApplication` 只显式组合 `LoginModule`、`EchoModule`、可选 `GomokuModule`，不实现业务处理。按功能建目录：`Login/` 放认证用例、仓库、账号会话与处理器，`Sessions/` 放大厅连接会话和只读身份，`Diagnostics/` 放大厅 Echo，`Gomoku/` 放两人房间和权威棋局；后续背包、房间沿用此方式。通用数据库连接池在 `Game.Server/Database/`，不是网络框架能力。不为尚未实现的网关、世界、账号再创建多个可执行项目。

处理器通过 `ServerApplication` 显式注册强类型 RPC/Notify 或 Reject。构造 host 前全部入站路由必须有明确处理/拒绝决定；host 校验漏注册、重复注册、目录归属后冻结应用。Push 不注册入站 handler。不用反射扫描处理器或运行时按类型名找 opcode。

模块只承担显式注册与必要的依赖组合，不引入通用模块基类、扫描或消息中转层。Request/Response 配对由生成的 `NetRpc<TRequest,TResponse>` 保证；不要求每个请求单独建类。当前四条认证入口保留在 `LoginHandlers`，负责协议适配、敏感 DTO 清理和回复后关闭；复杂用例再按实际职责拆处理器。

大厅业务通过 `LobbySession.RequireIdentity()` 取得 `AccountIdentity(AccountId, AuthEpoch, BindingId)`。身份由 `AccountSessions.Binding` 提供，不包含恢复凭据，不另存一份登录标志。入口检查与外部 IO 后复查不能替代状态提交处的仲裁：身份快照不是永久授权，角色归属/房间成员等权限由所属模块检查，共享状态由该业务同步，持久化副作用使用该业务的事务/幂等。普通 EOF 排空期间仍可使用有效账号身份；LobbySession 已进入 closing、连接取消或绑定被顶号、到期、撤销时拒绝身份访问。

## 3. 启动、连接与关闭

```text
应用日志初始化 → profile 校验 → Luban Tables 加载 → 证书检查 → 数据库 SELECT 1 → 账号表检查
→ 账号服务/应用组合 → 网络与 Kestrel → 监听等待客户端
客户端 TLS/DTLS → GFNP Hello → 应用连接初始化 → 按阶段派发业务
停止准入 → join 网络与业务 → 停止 HTTP host → 释放账号/数据库/证书
→ 最终停止日志 → 刷新并释放应用日志
```

服务启动验证运行依赖，不验证玩家账号、角色或其他玩家数据。认证仅由客户端 Register/Login/ResumeLogin 触发；框架不认识账号。生产监听、容量与跨进程会话未配置时，当前入口明确拒绝生产启动。

WSS session factory 在 Hello 前创建，KCP 在 ClientHello 绑定后由应用任务创建；不在 host 配额锁或 native owner 中调用业务 factory。KCP session 安全发布后才排队 ServerHello，确保回复前已有可用的 Admit 策略；factory 保持短小，不承担异步初始化。ConnectedAsync 是每连接应用派发任务的首个阶段，ServerHello 不等待它完成。native owner 在 factory/初始化期间继续接收并处理控制帧；Hello 后接收入站业务有界队列，初始化成功后才解码/派发，失败结束连接。每连接一个应用派发任务，不按包 Task.Run。

| 会话 hook / 能力 | 执行约束 |
| --- | --- |
| ConnectedAsync | 应用 owner；可排队 Push，耗时等待使用 connection.Cancellation |
| Admit | IO/native owner，在解码前检查头部；可与初始化并行，快速且线程安全，不依赖未完成初始化，不等待外部 IO |
| CheckDispatch | 同连接应用 owner，紧邻解码/handler 前复查权限；收包时允许不代表派发时仍允许 |
| CheckDeadline | 初始化成功后由 watchdog/native owner 调用；快速且线程安全，返回原因或 null；初始化期间传输心跳仍运行 |
| ClosedAsync | 业务退出后执行一次；session 已创建即需清理，即使 Hello/Connected 失败；不再发送或提升权限 |
| RequiresRestrictedTraffic | 业务选择初始受限阶段，框架配额不解释身份 |
| EnableFullTraffic | 业务提交权限后提升；结束连接不能迟到提升 |

同连接初始化/handler 串行；跨连接共享状态由业务同步。锁只保护状态提交与所有权交接，锁内不 await、不做网络/数据库 IO、不执行用户回调。异步返回后重新检查取消、会话 epoch 和操作资格，不能从 IO 回调直接修改世界/房间状态。

正常物理 EOF 保留已接收可靠工作顺序，由既有业务 owner 排空；传输结束后的回复释放。故障/显式停机停止派发并取消、join handler。应用必须协作取消；框架不会超时后遗弃仍持资源的任务。单连接故障不扩大为共享 listener/socket 故障。

RequestClose(reason) 用于外部会话 owner：首次非零原因生效，立即停止新业务/权限提升，发送 owner 尽力发送 Close，最多 2 秒后取消并 join；排队未派发/未发送业务丢弃。CloseAsync(reason) 等待同一释放结果。CloseAfterResponse 只属于当前 handler 的“回复后关闭”决定，不能作为后台定时关闭接口。Connected/Closed/handler 不得 await 自己的 CloseAsync 或 Completion，否则等待自身退出。立即中止用 Disconnect。

取消回调、Closed 或 transport 清理失败仍尝试其他独立资源，保留首因与关闭原因；Completion 在缓冲释放后发布，WSS 此时已归还 host 配额。重复 Dispose/停机等待同一结果，不重试已摘除资源。

WssServer 作为 hosted lifecycle service，在所有 hosted StopAsync（包括 Kestrel）之前的 StoppingAsync 停止准入并 join 网络，不能只依赖 DI 注册顺序或 RunAsync 返回后清理。DI 拥有 WSS 实例；KCP 应用拥有 listener，并让 ILoggerFactory 晚于 listener 释放。实际入口注入标准 ILogger，不靠 HTTP 日志代替 handler/清理错误日志。

### Luban 游戏配置

Excel 与 Schema 唯一来源仍为 `Unity/Config/Excel`。`ConfigGenerator` 提供 Generate Server、Generate All、Check IDE 启动项；gen.sh/gen.bat 与 Unity 菜单统一调用该工具。双端生成依次执行 Luban 的 client/server target，均使用 `cs-bin + bin --strict`；客户端命名空间为 `Game.Hotfix.TableConfig`，服务端为 `Game.Config.TableConfig`。表/字段通过 `c/s/e` 分组控制，空表分组遵循 luban.conf 的 default group，不能将服务端专用配置写到客户端输出。工具 `--target all` 表示双端分别生成，Luban 自身的 all target 仅用于全组检查。

Check 在系统临时目录生成并逐文件比较正式代码/数据，忽略 `.meta/.uid`，不写正式资产；临时目录在成功、失败与取消后清理。生成进程同时读取 stdout/stderr，任一目标失败停止后续目标并原样返回退出码；取消终止并等待子进程。双端生成不提供失败回滚。命令、输出变量与环境要求见 [使用说明](DotNetUsage.md#luban-配置生成与读取)。

`Game.Config/Generated`、`Game.Config/Bytes` 只放生成产物；手写运行库放 `Game.Config/Luban`，来源与许可证固定在目录说明中，与当前 Unity 包相同。不得访问 Unity Library/PackageCache 构建，也不将生成器依赖带入服务端运行时。当前生成器保持 Luban 5.0.0，升级时同时验证两端代码、数据与运行库。

`Game.Server/Configuration/TableConfigLoader` 用生成的 `Tables(loader)` 读取 `.bytes`，表名与引用解析由生成代码维护。两个 local profile 均在日志初始化后、运行依赖与监听创建前加载；加载器失败原样传播到应用入口，入口记录安全的阶段/异常类型/堆栈并退出 1，不输出可能包含配置值的异常 Message。默认从 `AppContext.BaseDirectory/Config/Bytes` 读取，`GF_CONFIG_DIR` 可覆盖；相对路径也基于执行目录。WSS 宿主注册同一个 `Tables` 为 DI 单例，手动组合的业务可直接通过构造参数接收它。当前 KCP local 仅验证加载，尚无配置消费者；有真实玩法时在应用组合处传入该实例。表对象和集合按只读方式使用，不修改共享表或给每张表创建静态单例。

数据和 Luban 许可证通过 Game.Config 项目项随构建、发布复制。生成产物纳入 Git；服务器启动不运行生成器、不更新表文件、不做配置热重载。数据更新需生成并部署匹配的数据后重启，Schema 变化需同时重新生成代码和数据、编译发布。

## 4. 数据库与账号业务

PostgreSQL 是账号主库，Npgsql 10.0.3 通过 NuGet 引用在 Game.Server，不将数据库 DLL 或玩家仓库下沉到 Core/Server。PostgresDatabase 由应用持有一个 NpgsqlDataSource 连接池，业务仓库借用 DataSource，不释放池、每请求建池或跨请求共享 connection/transaction。

SELECT 1 检查与账号表检查在监听前分别完成；SQL 初始化由部署者显式执行。单 SQL 的 command/reader 用 await using；多 SQL 原子操作独占一个连接与事务，command 绑定同一 transaction，成功显式 CommitAsync，最后释放。SQL 全部参数化，结果映射和事务归业务仓库，服务依赖仓库接口。复用 Npgsql，不创建通用 CRUD 转发类。所有异步数据库操作传递取消；异常原样传播，不当作账号不存在或密码错误。

当前池最大 16、连接/命令超时 5 秒，默认 search_path=public、关闭详细错误。仓库已有 CreateAsync/FindAsync/AdvanceEpochAsync；删号、改密/找回尚未实现，不提前增加无调用方接口。游戏服务释放连接池不会停止 PostgreSQL 进程，进程由显式维护工具、Docker 或系统服务管理。

`AuthenticationService` 是注册、登录、恢复和注销的统一用例入口，持有同一个 `AuthenticationRateLimiter` 与全局认证在途门禁；IP 限额在连接准入执行，账号限额在注册/登录用例执行。`AccountSessions` 是账号会话的唯一状态所有者；业务层返回 `AuthenticationStatus`/`AuthenticationResult` 和 `AccountSessionEndReason`，不构造协议 DTO、不引用 `ServerRequestException` 或网络关闭码。Handler 映射业务结果，`LobbySession` 将会话结束原因映射到连接关闭原因。数据库/内部故障继续原样传播。

`AuthenticationResult` 的凭据数组由调用方拥有：直接业务调用者用 `Dispose()` 清零；Handler 成功映射时将数组所有权交给响应，交接失败清理结果，框架编码完成或编码失败后调用 `LoginHandlers.ClearResponse`。不复制 token、不让只读身份引用敏感数组，也不能在编码前清空成功响应。账号会话只保留自身会话 ID 与 token 摘要。

账号规则：

- Login 查询账号：存在则校验密码，不存在则生成盐/哈希并创建、立即认证；同名并发由数据库唯一约束裁决，创建失败的一方读取已有账号并校验其密码，不能覆盖。查询/建号/校验/会话提交共用一次全局在途准入，不在认证内部再次调用 RegisterAsync。创建后的取消、登录失败或响应丢失不删除已提交账号，也不自动重试。
- 独立 Register 成功不自动登录，不延长 10 秒登录期限。账号区分大小写，不裁剪/归一化；拒绝空白、首尾空白和控制字符，最多 64 个 UTF-16 字符。密码 8～128，不裁剪/归一化。
- 密码仅保存随机 16 字节盐、PBKDF2-SHA256 600000 次的 32 字节哈希，比较为固定时间；不保存原文。请求/响应敏感 DTO 及临时数组 finally 清理；.NET string 无法保证原地清零。
- 每账号最多一个活跃账号会话，账号之间独立。密码通过后先登记有界在途操作，再在数据库原子分配递增 authEpoch，回到账号状态内检查 revision/LatestEpoch 后提交。Logout/到期使撤销前已登记结果失去资格，迟到结果不能复活会话；不回滚已分配 epoch，不无限保留历史账号墓碑。
- 新 Login 替换同账号旧绑定；Resume 保留 accountSessionId/authEpoch/到期时间，原子消费并轮换 32 字节 token，只保存 SHA-256 摘要。旧 token 只成功一次，响应丢失不自动复用或退回密码登录。
- 会话最长 24 小时，内存会话/在途账号状态默认上限 1024，仅在有会话或在途引用时保留。进程重启后账号/哈希/epoch 保留，活跃会话与 Resume 凭据失效。当前不支持多大厅实例共同仲裁；数据库递增 epoch 不能代替跨进程撤销。
- 每 IP/账号每秒认证最多 5 次，限流表各 128 条，满额拒绝新键；认证计算/数据库/提交全局最多 4 个在途，不新增无界等待队列。
- 过载使用空正文 status1：Login 回复后 Close1；Register 的全局/账号限额拒绝释放处理中资格，客户端可在原登录期限内显式再次请求；IP 准入限额拒绝并关闭。认证 handler finally 结束 loginInFlight。
- 绑定保留终止原因，即使索引已清理：顶号/Resume 替换为 2、到期 4、撤销 7。Logout 自身撤销后回复并 Close1，凭据拒绝统一 InvalidCredentials 后 Close5。
- SQL 提交和网络响应不是同一事务。断线/超时/取消不证明注册或操作未提交；购买/结算等后续业务以账号+命令类型+commandId 原子去重，绑定内容摘要并提供结果查询，不靠 Runtime 重发。

## 5. 共享 GFNP V1 契约

所有多字节整数为 little-endian；只接受完整消息；不启用应用压缩。WSS 的一个完整 Binary message、KCP message mode 的一条消息、不可靠 UDP 的一条加密明文，各承载一个应用帧。WebSocket 分片不等于应用分包，不再添加旧协议的 payloadLen 前缀。

### 5.1 固定头：32 字节

| 偏移 | 字节 | 字段 | 约束 |
| --- | --- | --- | --- |
| 0 | 4 | Magic | ASCII `GFNP`，按 u32 读取为 `0x504E4647` |
| 4 | 2 | WireVersion | u16，固定 1 |
| 6 | 2 | HeaderBytes | u16，固定 32 |
| 8 | 1 | Kind | 1=Request、2=Response、3=Push、4=Notify、5=Control |
| 9 | 1 | Flags | 固定 0；不能偷偷用来开启压缩或新编码 |
| 10 | 2 | Reserved | 固定 0 |
| 12 | 4 | Opcode | u32；1～999 系统保留，业务从 1000 起 |
| 16 | 8 | CorrelationId | u64；Request/Response 非 0，其余必须为 0 |
| 24 | 4 | BodyLength | u32；等于完整消息长度减 32，先验证再切片/分配 |
| 28 | 4 | StatusCode | i32；只允许 Response 使用非 0 系统错误码 |

Request 与 Response 使用同一个路由 opcode；路由定义同时绑定请求/响应类型，不靠类型名称查找。Push 是服务端到客户端，Notify 是客户端到服务端。业务 opcode 全局唯一，路由元数据声明可靠/不可靠，不允许发送端临时改变。

可靠消息总长默认最多 **32768 字节（32 KiB，含头）**，Body 最大 32736 字节。不可靠消息总长最多 **1150 字节（含头）**，Body 最大 1118 字节。每种 DTO 另有更小的业务上限。禁用不可靠能力的连接，其上限为 0。

帧必须恰好用完输入，不允许尾随数据、拼接多帧、负长度的重新解释、整数溢出、未知 kind/flags/保留位、错误方向或路由类型。连接握手前只接受系统控制消息。未知 opcode/错误通道/错误响应类型属于协议故障；已完成/取消的旧 correlation 响应在长度与头校验后丢弃计数，不反序列化。

Response `StatusCode=0` 时 Body 是响应 MemoryPack DTO；非 0 时 Body 必须为空。系统错误码固定：1=服务端过载拒绝执行，2=路由权限拒绝，3=服务端执行故障；未知非 0 值按通用远端故障传播。库存不足等业务结果放进响应 DTO，不混用系统状态码。无 RPC 的推送/通知错误由各业务契约定义，不发送伪成功响应。

### 5.2 系统控制包

控制包使用固定二进制结构；不是 MemoryPack DTO，避免协议不一致时必须先反序列化新业务类型。所有控制包只走可靠通道，`CorrelationId=0`、`StatusCode=0`，结构随 WireVersion 冻结。

| Opcode | 方向 | Body 精确布局 |
| --- | --- | --- |
| 1 ClientHello | C→S | hash[32]、reliableLimit u32、unreliableLimit u32、features u32、ticketLength u16、reserved u16=0、ticket[ticketLength]；总长 48+ticketLength |
| 2 ServerHello | S→C | hash[32]、sessionId[16]、reliableLimit u32、unreliableLimit u32、features u32、heartbeatMs u32；共 64 字节 |
| 3 Ping | 双向 | nonce u64，共 8 字节 |
| 4 Pong | 双向 | 原样回显 nonce u64，共 8 字节 |
| 5 Reject | S→C | reason u16、reserved u16=0、expectedHash[32]；共 36 字节 |
| 6 Close | 双向 | reason u16、reserved u16=0；共 4 字节 |

features 仅 bit0=支持不可靠，其他位必须为 0；WSS 必须为 0。ClientHello 的 ticket 规则按 transport 固定：WSS `ticketLength=0`，Body 恰好 48 字节；KCP `ticketLength=1～1024`，内容是已登录大厅签发的不透明连接票据。WSS 不在 Hello 中发送账号凭据，KCP 不允许匿名 Hello；错误的 ticket 长度/transport 组合直接拒绝。

Reject reason：1=协议不符、2=授权无效、3=人数/资源限制。Close reason：1=主动关闭、2=被顶号、3=维护、4=会话到期、5=认证失败/越权、6=登录等待超时、7=账号会话撤销。未列出的 reason 记录原值并作为拒绝/关闭处理，不改变正文解析。

ServerHello 的 hash 必须完全相等；双方限额取客户端提议与服务端策略的较小值，且不得超过 V1 硬上限，可靠限额至少 2048。不可靠只允许双方都支持时启用，非 0 限额至少 256。服务端心跳间隔第一版必须为 5000ms。sessionId 是当前传输会话的诊断标识，不是账号凭据；与业务 accountSessionId 分开。

Hello 前只允许所需控制包。当前 WSS 示例在 Hello 后仍未登录，只允许 Register/Login/ResumeLogin 与控制消息；local-kcp-dtls 绑定后只开放 Echo。未来世界应用须在 EnterWorld/RestoreWorld 成功后开放世界指令，该世界协议尚未生成，不能把传输可用当作已实现业务。重复 Hello、协商越界、绕过应用阶段均拒绝。

客户端还须检查协商可靠上限足够容纳 catalog 中所有可靠路由声明的最大 Body+32；启用不可靠时检查其所有不可靠路由。不能接受一个无法兑现已登记路由上限的连接。WSS 允许目录包含不可靠路由，但对应能力不可用，业务不能调用这些路由。服务端按同一目录与协商限额校验输出。

固定头测试向量：Control/Ping，nonce=`0x0102030405060708`，总长 40 字节，下面各行连续拼接；这是帧测试，不是 MemoryPack Body：

```text
47 46 4e 50 01 00 20 00
05 00 00 00 03 00 00 00
00 00 00 00 00 00 00 00
08 00 00 00 00 00 00 00
08 07 06 05 04 03 02 01
```

## 6. 共享 Proto→MemoryPack 编写规则

唯一手写源是 `Protocol/**/*.proto`，Proto 只是 IDL；线上正文是 MemoryPack 1.21.4 UTF-8 Object/Explicit 布局，不使用 Protobuf Binary、MessagePack 或 VersionTolerant。正式产物、生成命令见 [服务端使用说明](DotNetUsage.md#协议生成与验证)。

### 6.1 消息与字段

```proto
syntax = "proto3";
package game.protocol.example;
option csharp_namespace = "Game.Protocol";
import "Common/net_options.proto";

message ExampleRequest {
  uint64 command_id = 1;
  string text = 2 [(game.net.max_utf16) = 64, (game.net.max_utf8) = 256];
  repeated uint32 ids = 3 [(game.net.max_count) = 16];
}

message ExampleResponse {
  string text = 1 [(game.net.max_utf16) = 64, (game.net.max_utf8) = 256];
}
```

字段从 1 连续编号，`= n` 生成 `[MemoryPackOrder(n - 1)]`；声明顺序、注释、空白与文件遍历顺序不改变协议。字段名 snake_case 转 PascalCase，检查转换后名称冲突。每个文件显式声明 package 与 csharp_namespace；跨文件类型通过 import 和完整 Proto 类型名解析。当前 catalog 的 codec/校验器名称要求 DTO 与枚举简单类名全目录唯一，冲突时报错。

| Proto | MemoryPack C# 类型/约束 |
| --- | --- |
| int32/sint32/sfixed32、uint32/fixed32 | int、uint；在线固定宽度，不使用 Proto varint |
| int64/sint64/sfixed64、uint64/fixed64 | long、ulong |
| bool、float、double | 对应 C# 标量 |
| string | string；必须同时声明 max_utf16 和 max_utf8 |
| bytes | byte[]；必须声明 max_count |
| repeated T | T[]；必须声明 max_count；string 元素仍需字符串限额 |
| 顶层 message 类型 | 对应 DTO class；不允许递归 |
| 顶层 enum 类型 | `enum : int`；固定 4 字节小端，支持枚举数组，拒绝未声明值 |
| int32 + scalar="int8"/"int16" | sbyte/short；显式选择 MemoryPack 1/2 字节宽度 |
| uint32 + scalar="uint8"/"uint16" | byte/ushort |

所有引用默认非空：string 初始化为空字符串，数组初始化为空数组，DTO 字段初始化为新对象。缺省/空值语义通过 `(game.net.nullable)=true` 声明；引用数组元素通过 `(game.net.element_nullable)=true` 声明。数值可空禁止，使用显式 bool HasValue + Value 或可空 DTO；不使用 Proto optional 来隐式生成 Nullable<T>。

长度选项是**上限**，不是固定长度要求；sessionId、hash 等必须精确 16/32 字节的业务语义仍由会话处理器校验。字符串上限按 UTF-16 code units 和 UTF-8 bytes 分别计算。

枚举必须先声明零值，成员名称建议直接使用 PascalCase；工具仍检查转换后的名称冲突。禁止别名、Flags、嵌套枚举、可空枚举和枚举宽度收窄。枚举定义随 manifest 纳入 hash；增加结果码也要求两端同步生成。`ResultCode.Success=0` 表示业务成功，其余值只说明业务拒绝；帧头 StatusCode 继续表示系统错误，不能混用。失败响应允许成功载荷保持默认空值，业务处理器仅在成功后使用和校验载荷。

字段注释定义业务约束：成功登录的 accountSessionId/resumeToken 分别恰好 16/32 字节；恢复保留账号会话与 authEpoch 并轮换凭据。未来授权协议成功后还须校验非零 ID/conv、端点、ticket、PSK 与限额，授权身份由服务端连接绑定提供。UTC 到期时间用于显示/诊断，TTL 以 RPC admission 单调时间起算，不从响应收到时重新续期。上述权限与语义校验属于实际 Hotfix/.NET 处理器，生成的结构验证器不替代它们。

### 6.2 路由

```proto
service ExampleService {
  rpc Example(ExampleRequest) returns (ExampleResponse) {
    option (game.net.opcode) = 3000;
    option (game.net.route_kind) = ROUTE_KIND_RPC;
    option (game.net.delivery) = DELIVERY_RELIABLE;
    option (game.net.max_body) = 1024;
    option (game.net.response_max_body) = 1024;
  }
}
```

route_kind 为 RPC、SERVER_PUSH 或 CLIENT_SEND。RPC 必须可靠且声明响应正文上限；Push/Notify 返回 google.protobuf.Empty（需 import google/protobuf/empty.proto），该类型只占位，不编码；它们不能声明非零响应正文上限。通道必须显式选择 DELIVERY_RELIABLE/DELIVERY_UNRELIABLE，不根据 opcode 数字位推断方向，不在运行时自动转换通道。

opcode ≥1000 且在整个目录唯一，生成路由名称也全目录唯一。RPC 响应复用请求 opcode/correlationId；系统 1～999 保留在 Runtime 中，不写进业务 Proto。路由预算必须足够容纳所有声明字段的最大值，同时不超过 V1 通道预算。

当前生产协议分为 Common、Session、Gomoku、Diagnostics，共 14 条路由，全部可靠；最大可靠正文 4096 字节、不可靠正文 0。保留 Register/Login/ResumeLogin/Logout、Echo 与五子棋命令/推送；删除尚无玩法实现的世界授权、入场/恢复/退出、移动、AOI 协议及专属结果码。删除后重新生成 DTO、catalog、manifest、hash 与 golden vectors，两端必须同步更新，不保留旧指纹兼容。

底层 KCP 双通道测试使用测试专用 catalog 与独立指纹，不为测试保留生产业务占位。实时世界的字段、AOI 与输入序号是 [总体设计](NetworkDesign.md) 中的候选契约，后续随实际玩法重新定义并验证。

### 6.3 验证器、指纹与演进

禁止未知选项/类型、漏 import、缺限额、编号空洞/重复、名称冲突、递归、map、oneof、嵌套 message/enum、枚举别名、streaming RPC 及 Nullable<T>；不能默默忽略字段。不要手写 formatter、MemoryPack 回调、UnityEngine 类型、DateTimeOffset/ValueTuple 或布局直拷结构作为网络协议。

MemoryPack 反序列化前生成 schema-aware 无分配字节验证器，检查成员数/null、数组/字符串声明、剩余长度、UTF-8、尾随字节、checked 运算、深度与节点；不能先分配再校验。V1 硬上限为每字符串 256 UTF-16/1024 UTF-8、每数组 512 项、深度 8、每帧 DTO 节点 2048；字段必须显式声明并可收紧，放宽需要修改硬预算和协议指纹并重新审核。出站先验证对象，再用有上限 IBufferWriter 编码；验证器不修改 DTO。

工具先内存生成 DTO，用 MemoryPack 自己的 Roslyn generator 编译并分析实际序列化成员/serialization-info，再编译完整输出后写文件。缺少旧生成文件仍能 generate；不因旧产物损坏无法修复。

manifest 在内存规范化为 UTF-8 无 BOM/LF、固定键/路由顺序，SHA-256 内嵌 catalog。输入包含 wire/control/MemoryPack 版本、UTF-8 策略、路由、成员顺序/类型/可空性、枚举定义及所有长度/深度限制，不包含时间、路径、空白、git hash。默认 generate/check 只处理正式 C#，--artifacts 才处理开发 manifest/hash/golden-vectors；运行时不加载这些辅助文件。

同源不能替代 Unity/.NET 字节向量与 IL2CPP 验证。Proto 编号只映射 MemoryPack 成员顺序，没有 Protobuf 字段演进兼容能力；任何 hash 变化同步生成发布双方，不手改生成目录或复制旧 DLL 改名。CI 只 check，不 generate 掩盖漂移。

## 7. 共享 C KCP/DTLS 实现规则

唯一 C 源码在 `Native/Kcp`，KcpNative.cs 由双方同源编译；C# 负责平台 UDP/socket、时钟、队列与生命周期，C 负责 KCP、DTLS、有界 allocator 和 listener。一个 Unity 宿主 KcpWorker 管理其客户端，服务端每 listener 一个 owner；handle 只能由 create 线程访问和销毁，跨线程通过有界队列。服务端不复用客户端 ConnectionContext/worker。

可靠路径为 GFNP→KCP message mode→加密明文 channel1→DTLS→UDP；不可靠路径为 GFNP→channel2→同一 DTLS→UDP。C 内部 BIO/output 回调复制进入有界队列，C# 主动 feed/poll，不反向调用 Hotfix 委托。WANT_READ/WANT_WRITE 保留同一待写缓冲续调，不重发整个应用帧、不递归 SSL。

| 参数 | 固定起点 |
| --- | --- |
| UDP payload / KCP MTU | 1200 / 1150 字节；MSS=1126 |
| KCP mode / 窗口 | stream=0，snd_wnd/rcv_wnd=128/128 |
| nodelay/interval/resend/nc | 1/10ms/2/0，保留拥塞控制 |
| rx_minrto/dead_link/waitsnd | 30ms/20/最多 512 segment |
| 原始输入暂存 | 256 KiB/256 datagram；满额丢新包 |
| 密文输出 | 512 KiB/256 datagram，最长滞留 250ms |
| 单连接 native 动态额度 | 默认 4 MiB，合法 64 KiB～4 MiB |

AES-GCM 记录开销 37 字节，最大密文 payload=1150+1+37=1188，不超过 1200；握手也受 1200 限制，由库分片。满窗+最大 ACK 一次 flush 可超过 128 KiB，不能按单包或缩小输出队列推断安全。调整 MTU/窗口需重算 flush 峰值并重做双端压力测试。

native 固定 10ms 安排 KCP update；提前 tick 不额外 flush，晚到仅用当前时间推进一次，不补跑。ikcp_check 返回当前时刻不意味着同轮无限重入 update，不应用 PR #48。托管命令可提前唤醒，期限/UDP/DTLS timer 最晚下轮检查；精细最近 deadline 调度尚未实现。

单次 tick 原始输入最多 64 包，两次实际 flush 间最多 64 个 KCP datagram，无 flush 不能重置预算；每轮首尾输出共享 256 包预算。控制/可靠/不可靠发送分别 FIFO；socket WouldBlock 保留原包与 born，暂停追加业务，不停止必要收包/tick/deadline。各接收阶段按 16 可靠/最多 4 不可靠消费轮转，peek/header/BufferTooSmall 不推进。

仅解密后的合法数据进入 KCP；input 前遍历 24 字节 segment 头，验证 conv、command、length、PUSH 分片与无尾随字节。未知认证后 channel/非法 KCP/越界帧终止当前连接，未经认证坏记录按库行为丢弃；不能因任意陌生 UDP 踢掉正常连接。短写、局部分配失败或 output 失败锁 Fatal，不重试原应用帧。

依赖固定 mbedTLS **4.2.0 stable** + TF-PSA-Crypto **1.2.0**（上游 LTS 是 4.1，4.2 不标 LTS）。版本/commit/archive/tree SHA 见 [依赖锁](../Native/Kcp/third_party/mbedtls.lock.json)，TLS/PSA 配置分别在 config/mbedtls_config.h、crypto_config.h。DTLS1.2、TLS_PSK_WITH_AES_128_GCM_SHA256、cookie、anti-replay，禁用 CBC/匿名/会话恢复/CID；PSK 模式没有前向保密。正常构建离线校验，不下载浮动版本；升级重新核对安全公告、配置、ABI 及平台验证。

PSA 一次初始化、固定 1024 共享密钥槽受锁保护，槽元数据是静态存储，不算动态 owned_bytes；槽耗尽报 AllocationFailed。密钥/操作/复制缓冲归当前 connection/listener quota，分配头记录 owner 和容量，free 按头归还，quota 活到最后一块释放。KCP/mbedTLS allocator 仅安装一次，不运行中替换。保留 PSA 默认复制缓冲，不启用与 TLS 原地 AEAD 冲突的 EXCLUSIVE_BUFFERS。

系统 CSPRNG：Windows BCryptGenRandom、Apple CCRandomGenerateBytes、Android API23 /dev/urandom、Linux getrandom。失败清零输出并终止创建/握手，不自动切源。PSK/敏感 native 缓冲用不可优化掉的 zeroize，托管帧清零整个实际容量。

KCP 上游基线 `b1a7a2101dcbb96017681a500d6b82bbe5a88766`。PR #467 的 ACK 时间戳比较 >=0、分配失败检查与标准 offsetof 均已直接合入 c/h。prepare_kcp.py 只校验并逐字节复制，不另存/应用补丁；维护源码保留 CRLF。

| KCP 分配路径 | 失败与约束 |
| --- | --- |
| create 控制块/buffer | NULL；失败释放已创建部分 |
| setmtu | -2；保留旧 buffer/MTU |
| send 普通分片/stream 尾替换 | -2；可能已有部分状态 |
| input ACK 创建/扩容、接收 segment | -4；保留可释放状态 |

包装层锁 Fatal 后销毁，不继续 update/发 ACK 或重试。源码 hash 以准备脚本与构建清单为准；回归中的旧 ACK 比较副本仅是对照，不声称原始未修改上游。

listener 授权状态为 Issued→Bound/Expired/Revoked。cookie 规范编码 peer 地址族/地址/端口，不 hash 填充；cookie 通过后才占握手槽。PSK 查找不消费 grant，GFNP Hello 确认在 owner 内检查 id/generation、conv、ticket 摘要、期限并一次绑定，竞争握手最多一个成功。DTLS Established 不等于应用 Connected。Granted TTL≤30 秒，握手+Hello 总期限≤10 秒，DTLS 后 Hello≤5 秒且不重置原期限。

event 借用 child handle，listener 是唯一销毁者；Closed 保存在每连接终态槽，覆盖尚未交付 Established。普通 Closed 先排空完整可靠帧，故障停止派发，最终 release 后借用 handle 立即失效。单 child 关闭保留共享 UDP socket。poll 输出携带 id/generation/born；尚未发布 Established 的有效握手输出也要投递，旧实例输出不能发送给复用槽位。临时 feed 拒绝包括 InternalError 不能凭类别扩大为 listener 故障，listener tick/poll 自身失败仍传播。

构建与安装使用同一 build.py/install.py；Windows 可显式 LLVM-MinGW 交叉编译或 MSVC，Linux 用对应宿主工具链，移动用 NDK/Xcode。清单记录 ABI、生产源码/配置、依赖、工具链、架构、二进制 hash 和验证；安装器保留 GUID 并拒绝漂移、错误 PE/导出/依赖及 sanitizer 产物。当前指纹还包含测试/安装工具，拆分运行时输入与验证记录只是后续建议，未实施。许可证随二进制发布；不手改清单绕过检查。

## 8. 共享原生 ABI 2

唯一函数声明是 [gfnet.h](../Native/Kcp/include/gfnet.h)，C# 声明位于 [KcpNative.cs](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/KcpNative.cs)。仅支持 64-bit，Cdecl，默认自然对齐 8；固定宽度整数，禁止 C long/bool/隐式字符串。库名 `gameframe_kcp`，iOS 为 `__Internal`；公开导出仅 `gfnet_*`。不把内部 KCP/mbedTLS 结构暴露给托管。

### 结构布局

下表偏移均为 bytes，数组内元素连续；所有保留位为 0，所有带 size/version 的输入或输出结构先填实际 size 和 version=2。错误布局返回 InvalidArgument，不按旧长度静默兼容。

| 结构 / size | 字段偏移 |
| --- | --- |
| config / 64 | size0 version4 role8 conv12 native_limit16 reliable_limit20 unreliable_limit24 reserved28 identity_ptr32 identity_length40 reserved_identity44 psk_ptr48 psk_length56 reserved_psk60 |
| stats / 168 | size0 version4 state8 capabilities12 fatal_code16 native_error20 owned_bytes24 peak_bytes32 allocations40 udp_received_bytes48 udp_polled_bytes56 udp_received_packets64 udp_polled_packets72 raw_dropped80 unreliable_dropped88 input_count96 output_count100 output_bytes104 output_oldest_ms108 waitsnd112 srtt116 rto120 snd_wnd124 rcv_wnd128 remote_wnd132 plaintext_count136 plaintext_bytes140 pending_reliable_bytes144 inflight_reliable_bytes152 timeout_retransmits160 |
| peer / 24 | address_family0 address[16]4 port20 reserved22 |
| listener_config / 32 | size0 version4 native_limit8 connection_limit12 max_connections16 max_handshakes20 max_grants24 reserved28 |
| grant / 104 | size0 version4 conv8 remaining_ttl_ms12 grant_id16 identity[16]24 psk[32]40 ticket_digest[32]72 |
| event / 72 | size0 version4 kind8 reason12 connection_id16 generation24 grant_id32 connection_ptr40 peer48 |
| udp_metadata / 64 | size0 version4 enqueued_ms8 reserved12 connection_id16 generation24 peer32 reserved_tail[2]56 |

peer family 为 4/6；address 为网络序字节，IPv4 使用前 4 bytes，后 12 bytes 必须为 0；port 为主机序。初版拒绝 IPv6 link-local/scoped 地址。托管 `Marshal.SizeOf/OffsetOf` 与 C 静态断言共同校验布局。

Config create 同步复制 16-byte identity、32-byte PSK。role=1 client / 2 standalone server；standalone server 用于底层 harness，实际多客户端服务使用 listener。全部输入/输出 byte buffer 只在调用期间有效。grant 注册复制全部数据；ticket_digest 为 SHA-256，原始票据不保存到 native grant 表。

### 成功与错误返回

- create/feed/tick/send/stats/state/destroy 及 listener 注册、绑定、关闭、release、destroy：成功 0。
- peek_frame：有帧 1，并输出 channel=1 reliable / 2 unreliable 和完整 GFNP 长度、enqueued_ms；不可靠为原始入队时刻，可靠为当前 native 时间；无帧 0。
- peek_frame_header：有帧返回 32，仅复制固定帧头，不消费；无帧 0。
- recv_frame/poll_udp/listener_poll_udp：成功返回完整 bytes 数，无数据 0；BufferTooSmall 不消费队首。
- listener_poll_event：有事件 1，无事件 0。kind=1 DTLS Established；kind=2 Closed，reason 为首个 gfnet 错误的正值，普通关闭为 0。Closed 覆盖尚未投递的 Established。
- `-1 InvalidArgument`、`-2 BufferTooSmall`、`-3 Backpressure`、`-4 ProtocolError`、`-5 AllocationFailed`、`-6 InvalidState`、`-7 InternalError`。

Backpressure 表示未接受该输入/发送；feed 队列满丢新 UDP，业务 send 未复制前可等待。KCP send 接受长度不足、分配失败、output 入队失败锁定 Fatal，不重试整个应用帧。可靠发送成功返回 0 前必须核对 `ikcp_send` 接受长度等于帧长。

输出必须使用 1200-byte datagram 缓冲。应用可靠帧 32～reliable_limit，不可靠帧 32～unreliable_limit。peek_header 在帧小于 32 时锁定 ProtocolError；C# 校验固定头和通道/路由/声明长度后才分配正文，recv 长度必须与固定头一致。

可靠/不可靠采用 16/4 消费轮转，各通道内部 FIFO。只有 recv 成功推进轮转，重复 peek/header 和 BufferTooSmall 保持同一队首。不可靠 enqueued_ms 在托管迁移后仍保留，不能重置有效期。ABI2 改变 peek_frame 签名，原生和托管必须一起更新；不静默兼容 ABI1。

mbedTLS 4.2 的单一 SSL／PSA 错误原值保留在 native_error，不再拆分或组合高低层错误。配额分配失败、SSL_ALLOC_FAILED、PSA_ERROR_INSUFFICIENT_MEMORY 优先归 AllocationFailed；已知远端记录/握手协议失败归 ProtocolError；配置、内部和未识别失败归 InternalError，保留首因。细分坏 MAC/replay/cookie 丢弃计数仍不可观测。

### 状态、时钟与所有权

状态 Handshaking1 → Established2 → PeerClosed3/Fatal4。Established 仅代表 DTLS；GFNP Hello/hash/ticket 绑定完成后才对应用发布 Connected。Fatal 保留首因和 mbedTLS 子因，禁止继续 feed/tick/send/peek/recv/poll UDP；stats/state 和销毁可用。PeerClosed 允许读已排队完整帧，feed/send 返回 InvalidState；tick 不再推进（返回 0），poll 可读取旧输出但托管 owner 停止投递它们。普通 Closed 不立即 release，先排空已收到的完整可靠帧；故障关闭不继续业务派发。非法参数/错 owner 不自动改状态。

全部 handle 操作必须由 create 线程调用。有效 exclusive handle 只 destroy 一次；已释放指针不可再次调用任何 API。listener 是所有子 handle 的唯一销毁者；event.connection 仅在 release/listener destroy 前有效，子 handle 只允许应用 send/peek/header/recv/stats/state，不允许独立 feed/tick/poll UDP/destroy。

首次 tick 初始化 32-bit 单调毫秒，后续差值必须非负并小于 2^31，允许低位回绕。托管在截断前以 64-bit 检查长时间挂起；固定 10ms update 只由 native 排程，提前 tick 不额外 flush，晚到不补跑。每次实际 flush 之间最多处理 64 个 KCP datagram；单次 tick 的原始输入最多 64。输出队列 256 包/512KiB、最长 250ms；原生所有分配含头计连接 quota，参数上限见本文第 7 节。

### listener 约定

listener register/revoke/feed/tick/event/poll/bind/close/release 均由 listener owner 串行执行。feed 以完整 peer 和 datagram 为单位；cookie 检验前只有临时握手，输出 cookie 包受共享有界队列约束。验证 cookie 后才占 handshake 槽。PSK 校验查询 Issued grant，不消费；confirm_binding 校验当前 id/generation、未过期 Issued、conv 及 ticket SHA-256，原子 Bound 并终止竞争握手。

feed 返回值可能来自临时握手的 create/tick（包括 InternalError），不能仅凭错误类别认定 listener 整体故障。临时握手失败在该次调用中释放；已有子连接故障通过 Closed event 交付。托管 owner 不将单次 feed 拒绝升级为共享 socket 故障；listener tick/poll 等 owner 操作仍检查并传播自身失败，包括共享 cookie 队列分配失败。

Issued TTL 最多 30000ms，重复注册相同 grant 不延长期限。Bound 由应用撤销/连接关闭管理；绑定/撤销清零原始 PSK。DTLS 握手及 Hello 总期限不超过 10s，Established 后 Hello 最多 5s 且不延长原始期限。

poll UDP 携带 id/generation/birth；cookie id/generation=0。`listener_get_connection_state` 校验在途包是否仍属活跃实例，也覆盖尚未 Established 的握手；不能因为应用尚未收到 Established event 就丢掉其握手输出。WouldBlock 时保留原始 birth，失效实例丢弃输出，超过队龄关闭对应子连接。关闭/release 一个 child 不影响共享 UDP socket。

终态事件固定存在各子连接槽内，不因普通队列满丢失；release 只接受 PeerClosed/Fatal。release 与 listener_destroy 同步释放 native 所有分配；托管应用任务/缓冲仍须由应用 owner 等待并释放。

### 指标语义

owned/peak 包含 native allocation header 与 quota、KCP、DTLS 和各阶段队列实际容量；pending/inflight_reliable_bytes 为 snd_queue/snd_buf 的有效 payload，不是额外分配额度。udp_sent 字段在旧 C 字段命名中代表 **poll 到托管**，实际 socket 成功发送由 C# 单独累计。timeout_retransmits 是 KCP xmit 的超时重传次数，不含 fast retransmit；不得展示为全部重传。

capabilities=1 只表示基础 KCP/队列/内存统计。锁定 mbedTLS 未开放细分 bad-MAC/replay/cookie 计数，当前明确不可用，不能以 0 冒充已测得。原生额度预留与实际使用分开展示，不重复扣额度。

## 9. 安全、容量、诊断与发布验收

WSS 仅 HTTPS/WebSocket，平台正常验证域名、证书链与有效期；不从 URL/query/Authorization/cookie 认证，不忽略证书、强制 IPv4 或自行实现握手。Origin 精确 allowlist 不是身份验证；禁用应用压缩。协议哈希不同在提交账号凭据前 Reject，不猜测兼容。

网络选项构造时校验并复制，不运行中改容量。KCP ABI2 硬上限：Connections1～64、Handshakes1～16、Grants1～256、NativeBytes64KiB～256MiB；越界构造时报字段，其他组合约束仍适用。多个 listener 共享 native 进程/PSA 资源，不能每 listener 无限复制上限。WSS/Kestrel 连接、TLS 额度需配套；业务在框架限额之外限制认证、数据库和玩法并发。

OwnedBytes 包含可控帧实际容量与保守 reservation，不是精确托管堆或 RSS；CLR/平台 TLS/内核/业务保留 DTO 另测。队列同时限制条数/字节，扩容预留旧+新峰值；完成槽保留到消费，不以先摘索引规避上限。native 实际/峰值包含分配头与队列；native 预留和实际值分开展示，不能重复计费。

指标区分 wire、应用帧、正文、Ping RTT、RPC 处理与排队；日志只记录 opcode、长度、correlation、代际、时间/码，不输出密码、token、ticket、PSK、完整正文。未知 DTLS 细分丢弃明确 unavailable，不用 0 伪装测量。业务标签有界，不按每账号生成无界指标。

KcpListenerHost 通过 System.Diagnostics.Metrics 的 `GameFrame.Server.Network.Kcp` meter 输出 owner 工作耗时、循环间隔、当前 owner 线程托管分配与业务发送/派发排队耗时。仅启用的 instrument 才采样，没有连接或账号标签；观测回调在记录线程同步执行，需快速且不抛异常。分配指标不包含应用任务、native、内核或完整进程内存，不能代替 GC/RSS 测量。指标名、单位与实际采样范围见 [服务端使用说明](DotNetUsage.md#kcp-性能测量)。

验证要求：

- 编译/locked restore、独立框架 catalog 测试、协议/schema/golden vectors/check、真实 WSS、数据库、UDP/DTLS 分别验证。缺少 PG/native 变量而跳过的测试不算通过该能力验收。
- 回归覆盖期限边界、取消/响应/关闭重入、初始化阻塞、解码前权限、未订阅 Push、队列满/扩容、握手竞争/撤销、普通 EOF 顺序、分配失败与首因/清理；纯逻辑用可控时钟，真实 socket 不能被 mock 替代。
- native 验证 size/offset/导出、owner、MTU/满窗/ACK、WANT、Fatal、随机源/PSA 槽与 quota、丢包/重复/乱序和 49.7 天回绕，匹配平台执行 ASan/UBSan 及分配归零检查。
- 发布平台必须执行真实服务进程、对应 native 与客户端互通；Windows 交叉编译、PE 检查、CI 配置不等于 Windows 执行，移动 archive 存在不等于最终链接或真机通过。
- 生产报告给出目标 shard 连接数、峰值 RPC、owner/DB 等待、AOI 热点、上下行、CPU/内存/GC、tick/P99 及至少 30%余量，并执行 24 小时 soak。弱网至少 RTT20/100/200ms、抖动 0/30/100ms、丢包 1/5/10%、重复/乱序 5%、3 秒断流、32KiB/s 带宽和 100%丢包到超时。
- 同步协议发布先准备服务/安装包/热更，维护窗口终止或隔离旧入口，设置最低版本后准入新 hash；不支持新旧消息混连，不实现客户端自动版本回退。

### 当前范围与待完成项（2026-10-13）

已实现：五个运行时工程与离线协议/配置工具分层、Luban 双端生成和服务端加载、配置 IDE 启动项与 CI 只读产物检查、强类型框架接口、显式业务模块组合与统一认证用例/连接身份入口、WSS/KCP 底层、Proto→MemoryPack、PostgreSQL 账号、local 启动、原生构建安装、KCP owner/业务队列测量和主要生命周期回归；已修复 WSS/KCP 慢 Connected 阻塞传输、WSS 最小接收额度遗漏扩容峰值、未订阅 Push 漏校验、KCP 容量越界，以及账号迟到 epoch/认证收尾/关闭原因/logger 问题。

客户端 ConnectionContext 已补成功 RPC 分阶段汇总与各子预算快照，独立 .NET 测试链接同一源码验证；服务端沿用既有 KCP owner/发送与派发排队 meter。具体客户端采样范围见 [客户端使用说明](ClientNetworkUsage.md#6-关闭与诊断)，不能据此声称服务端所有阶段或失败路径都已有测量。

未完成：生产 hosting/跨进程账号仲裁，账号到世界 grant 注册及撤销链路、世界/角色/AOI/移动/恢复业务，完整元数据计账、服务端 RPC 分阶段/分位耗时与统一诊断、客户端全部主线程成本、精细期限调度、帧池化/零逐包分配目标、构建输入指纹拆分、生产容量与 soak。先补测量再决定池化、编码长度预计算、采样或锁/调度调整。

2026-10-14 实施优先级：先按 [NetworkDesign 第 18.1 节](NetworkDesign.md#181-当前优先路线底层框架完善)完善通用底层，建立完整源码与验证基线，再落实资源/生命周期缺口、诊断、测量和平台验收。当前仅五子棋，不提前建设实时世界、AOI、跨进程账号/位置路由或世界授权链；上述业务待实际多人玩法需要时扩展。保留强类型应用接口与业务程序集边界，具体改动由实际缺口决定。

未来世界接入必须从认证 WSS 绑定取得身份，目标网关注册确认后才返回短时单连接 PSK/ticket；串行仲裁注册/绑定/注销，迟到注册不得复活旧 epoch。账号撤销停止相关网关准入后才能确认 Logout，已提交撤销不回滚。世界/房间由自身逻辑 owner 串行 tick、指令和 AOI，数据库结果回到 owner 检查 epoch 后提交；这些是开发约束，当前没有世界处理器或多实例支持。

截至 2026-10-13 已有验证证据见 [服务端使用说明](DotNetUsage.md#验证与现有证据) 和 [客户端使用说明](ClientNetworkUsage.md#验证与现有证据)。后续只更新实际执行范围，不累计历史测试数量冒充本轮全量验证。


### 五子棋房间业务（2026-10-14）

LocalWssService 创建 / 释放 GomokuRooms，ApplicationStopping 停止准入并中止未终局房间；网络 join / Closed 清理在房间 DisposeAsync 前完成。未注入业务的宿主显式拒绝 3000～3006，KCP / 实时世界占位继续保持原契约。

短锁拥有集合、成员索引与结果缓存，提交时再持有 AccountSessions.Binding.CommitGate；Revoke 使用同一绑定锁。顺序为房间集合 → 绑定，房间不获取账号索引锁，不在锁内执行 IO / 编码 / continuation。LobbySession 的顶号与 Closed 交接旧 BindingId，旧解绑不能移除新成员。

每绑定递增 commandId + 操作 / 内容 SHA-256 去重，最大已见 ID 活到解绑；缓存淘汰后拒绝旧 ID，Query 仍返回当前成员状态。RPC 适配位于 GomokuModule，只链接纯 GomokuGame.cs，不引用 Unity / Hotfix DLL。限额与测试见 [五子棋](Gomoku.md#联机实施计划与当前实现2026-10-14)。

<a id="server-logging-plan"></a>

## 10. 服务端日志完善执行计划（2026-10-14）

本节维护框架日志设计、执行步骤与实际进度。2026-10-14 已实现统一的 `GameFrame.Logging` 工程，内部直接接入 NLog，并完成两个 local profile 的应用级接入；完整网络事件治理、业务事件和生产性能验收仍待执行。日志独立于网络和游戏业务，由应用组合并管理实例生命周期。ETFramework 仅作参考，取舍以 GameFrame 的职责、生命周期和部署需求为依据；.NET/NLog 依赖不进入 Unity 共用源码。完成范围和待办见第 10.7 节，接入完成不等于全计划完成。

### 10.1 当前基线与范围

| 接入点 | 当前已确认情况 | 本轮工作 |
| --- | --- | --- |
| `Game.Server/Program.cs` | 已在 profile/Tables 前创建日志，失败记录安全诊断并返回 1，最终释放日志 | 补充各依赖故障的真实部署验证 |
| `Game.Server/LocalWssService.cs` | 宿主借用外部 factory，移除默认输出；宿主外资源释放时日志仍存活 | 已验证真实 WSS host 释放后可写日志；真实数据库启动故障待验 |
| `Game.Server/LocalKcpService.cs` | 借用应用 factory；凭据读取前已有日志，就绪使用源生成事件 | 已验证真实进程监听、SIGINT 停止与最终日志；生产压测待补 |
| `GameFrame.Server/ServerConnection.cs` | 已记录 WSS 连接、handler 和部分清理故障 | 完整核对各任务和收尾路径的记录边界，补齐请求与连接字段 |
| `GameFrame.Server/KcpListenerHost.cs` | 已注入 logger，部分事件有连接 ID/代际，具备 owner/队列 meter | 区分 child、listener、handler 和清理故障，统一关联与级别 |
| `Game.Server/Login`、`Sessions`、`Gomoku`、`Database` | 使用显式业务组合；当前没有统一业务日志约定 | 由业务所有者记录认证、绑定和房间关键事件，仓库传播基础设施异常 |

本次针对新增日志工程、应用组合与受影响测试完成源码核对，不声称已完成网络和业务日志的全量审查。继续 L0/L2/L3 前建立相关文件和调用关系清单，完整阅读实现、调用方、选项、资源所有者、测试、依赖、配置与构建入口。搜索用于定位，清单和待验证项维护在本节，不另建历史审查报告。

首轮覆盖现有 local-wss/local-kcp-dtls、框架日志和现有账号/五子棋业务。生产 hosting 仍按既有约束拒绝启动；完整分布式追踪、日志检索平台、持久化审计流水和游戏行为回放不属于首轮交付。每包日志、逐落子 Information 日志和所有成功 RPC 的默认日志也不属于目标。

### 10.2 已选方案与取舍

- 沿用 `ILogger<T>`/`ILoggerFactory` 作为模块间日志契约。通用配置校验、实例上下文接入及初始化/释放规则属于框架日志模块；`Game.Server` 提供部署值并选择 Provider。Core 不增加日志库依赖，账号/房间字段不下沉到网络或日志框架。
- 已采用 NLog ConsoleTarget：默认文本，可显式切换单行 JSON；两种格式使用相同事件、结构化字段和 Scope。NLog 专属配置在日志工程内，应用不同时启用内置 Console Provider。
- 对比“分别给 WSS/KCP 补几个日志点”和“统一应用日志生命周期”，采用后者，解决早期启动与宿主外清理缺少覆盖的问题；不改变现有网络停止/join 顺序。
- 应用拥有 `NLogLogging`，WSS DI 和手动组合的 KCP/业务借用其 Factory。外部实例注册不转移释放责任；已验证 WebApplication 释放后仍可写入。应用最后调用 `DisposeAsync`，重复调用等待同一任务。
- 已接入 NLog 有界异步批量输出，不额外创建自研队列。文件输出保留扩展入口，当前部署只启用 Console；接入与门禁见 [性能与 NLog 接入](#server-logging-performance)。不复制 ET 的静态单例、Fiber 分文件或 All/Error 双份输出。
- 高频日志使用固定模板与结构化参数；需要时使用 `LoggerMessage`，昂贵诊断参数先检查 `IsEnabled`。级别由启动配置决定，当前不做运行中热重载；不用 `Conditional("DEBUG")` 移除日志能力，不默认捕获调用位置或主动创建 StackTrace。

<a id="server-logging-architecture"></a>

#### 框架职责与依赖方向

`DotNet/GameFrame.Logging` 是单一服务端日志工程，命名空间为 `GameFrame.Logging`，同时包含配置校验和 NLog 接入。当前只有一种输出实现，不再单独维护 NLog 适配程序集；职责通过标准 ILogger 接口、模块依赖和应用所有权划分。非网络服务也可以复用该工程，不预建其他 Provider 工程或自定义日志接口。

| 层次 | 职责与放置位置 | 对外边界 |
| --- | --- | --- |
| 日志契约 | 标准 `ILogger`、`ILogger<T>`、`ILoggerFactory`、`EventId` 与 Scope | 模块依赖稳定标准接口，不重复定义 `ILog`、`ILogSink` 或逐级转发 API |
| 日志模块 | `GameFrame.Logging`：选项校验、实例元数据、NLog Provider、布局、AsyncTargetWrapper、诊断和异步释放 | 依赖标准 Logging 与 NLog，不依赖 ASP.NET Core、网络或业务；不解析游戏环境变量，不新增第二层队列 |
| 模块事件 | 网络事件在 `GameFrame.Server`，认证/房间事件在 `Game.Server` 各自领域 | 事件语义和字段由状态所有者定义，日志模块不引用这些类型、扫描业务事件或订阅业务事件总线 |
| 应用组合 | `Game.Server`：读取部署配置、提供 Service/Environment/Version/InstanceId、注册选定输出并注入模块 | 应用拥有本次进程日志实例，业务只借用 logger；应用不实现通用队列、写盘或日志框架 |

依赖方向为 `Game.Server → GameFrame.Logging → 标准 Logging + NLog`、`Game.Server → GameFrame.Server`。网络和业务事件使用标准 ILogger 接口，只有应用组合及日志配置使用方需要引用 `GameFrame.Logging`。日志与网络工程之间没有相互引用；共享网络 Core 保持现有无日志依赖边界。纯协议/编解码错误由拥有执行边界的调用方记录。

输出格式、级别和容量是配置，连接关闭与业务失败策略仍由原模块决定。更换 Provider 只调整日志工程与应用组合，不要求修改 handler、协议 DTO、网络状态机或业务事件定义。LoggingOptions 保留元数据和级别，NLogOutputOptions 保留 NLog 输出选项；两者同属日志工程，不复制第三方完整配置模型。

日志实例显式初始化、可被测试替换，所有权由应用保留；不通过全局静态 Current/ServiceLocator 查找 logger，不在取得 logger 时隐式读取配置或启动线程，不让网络模块初始化/关闭全局日志。事件测试可注入标准捕获 Provider；独立 `Tools/Logging.Tests` 验证真实 NLog 适配，无需数据库和网络监听。不新增统一 Write 转发器、自研调度器或业务感知的日志管理器。

<a id="server-network-fault-events"></a>

### 10.3 字段、事件与级别

下表规定目标逻辑字段；当前已实现公共/实例字段与宿主事件，其余按 L2/L3 推进。文本和 JSON 的实际映射见使用说明。消息模板、EventId 和字段含义保持一致，不能把插值后的文本当作结构化参数。

| 范围 | 字段 | 规则 |
| --- | --- | --- |
| 公共 | Timestamp、Level、Category、EventId、EventName | 时间使用带时区的 UTC；EventId/名称稳定，模板修改不复用成另一个事件 |
| 应用 | Service、Environment、InstanceId、Version、Profile | InstanceId 区分进程启动实例，不依赖可复用的 PID；profile 校验前允许未指定 |
| 连接 | Transport、ConnectionId、Generation | WSS/KCP 使用同一含义；握手前尚无连接标识时省略，不伪造有效 ID |
| 请求 | Opcode、CorrelationId、BodyLength | CorrelationId 与实例、传输、连接、代际组合定位；Notify 不伪造 RPC ID |
| 结果 | StatusCode、ResultCode、CloseReason、DurationMs、Stage | 系统码、业务码和关闭原因分开；耗时用单调时钟，Stage 说明起止边界 |
| 业务 | AccountId、AuthEpoch、BindingId、RoomId、CommandId | 仅由对应业务提供；按需记录，不默认加入所有事件或指标标签 |
| 故障 | ErrorType、原异常的安全诊断信息 | 保留故障定位能力；异常 Message、InnerException、Data 都不能绕过敏感信息规则 |

网络事件声明在 `GameFrame.Server` 的内部诊断代码中，日志模块自身的配置/输出诊断放在 `GameFrame.Logging` 或实际适配层，业务事件在 `Game.Server` 按领域声明。通用日志模块只维护公共字段语义，不集中依赖各模块的事件类。网络事件 ID 使用 1000～1999，宿主事件 2000～2999，账号事件 3000～3999，五子棋事件 4000～4999；这些 ID 与协议 opcode、状态码无关。事件以 Category + EventId/名称识别，不要求未来所有独立模块共享一个全局整数注册表。首次实施时登记具体事件，不预先创建无调用方事件。只有语义相同、字段一致的事件才能被 WSS/KCP 共用。

| 级别 | 使用范围 |
| --- | --- |
| Trace/Debug | 显式启用的请求完成、连接变化与详细诊断；仍禁止敏感正文 |
| Information | 启动就绪、正常停止、成功建立/撤销账号绑定、房间创建/开始/结束等关键事件 |
| Warning | 异常协议、超时、过载等需要关注的运行事件；高频同类事件限频汇总 |
| Error | 未预期的 handler、外部依赖和独立清理故障 |
| Critical | 启动失败或共享 listener/宿主故障导致服务必须终止；不把单连接错误提升为全服故障 |

凭据错误、非法落子、房间已满、正常 EOF 和已请求的协作取消不写 Error 堆栈。凭据拒绝、限流和协议拒绝次数以指标聚合，必要日志只携带原因和计数；不为每个攻击来源创建无限限频状态。具体事件级别依据现有错误语义确定，不能仅按异常类型决定严重程度。

2026-10-14 已实施网络故障事件，声明位于 `GameFrame.Server/NetworkLog.cs`，复用现有连接首因与终态，不增加日志管理器、异常去重表或逐包 Scope。字段为 Transport、ConnectionId、Generation、Stage、Opcode、CorrelationId、CloseReason、ExceptionType、ExceptionStack；没有对应请求时 Opcode/CorrelationId 为 0，共享 listener 的连接标识为 0，均表示不适用。

| EventId / EventName | 责任边界与级别 |
| --- | --- |
| 1000 / NetworkConnectionFailure | WSS/KCP 首个连接故障；协议/背压/传输/非协作超时为 Warning，未预期应用故障为 Error |
| 1001 / NetworkConnectionCleanupFailure | 独立的响应释放、取消、Closed、资源收尾或 join 故障，Error；清理是首因时仍记录，并保留原错误传播 |
| 1002 / NetworkListenerFailure | KCP 共享 socket/native owner 失败，Critical；不对每个受影响 child 重复记录主故障 |
| 1003 / NetworkListenerCleanupFailure | KCP listener 独立清理失败，Error，包含首个清理故障 |

请求失败在帧仍归派发者所有时固定 Opcode/CorrelationId；关闭传播不重复打印主故障，独立清理仍单独记录。Provider 仅收到异常类型与直接从异常堆栈帧生成的字符串（不含源文件信息）；不接收原 Exception、Message、InnerException、Data、可重写 StackTrace 或 ToString。`Completion.Error` 和关闭/释放失败仍保留原异常对象与既有语义。过滤关闭时不创建堆栈字符串。此规则只覆盖这些网络故障出口，业务/第三方事件与故障洪峰成本仍按后续门禁验收。

### 10.4 异常记录、Scope 与所有权

1. 为启动、请求、连接、listener 和最终清理分别指定记录责任方。Repository/Service 传播原异常；已经在请求边界处理并记录的主异常，连接关闭和宿主层不再打印同一堆栈。为此优先使用已有首因/终态所有权，不新增全局异常去重表。
2. 原失败状态、Completion 和退出码独立于日志维护。写过日志不代表故障处理完成；日志丢弃也不能改写业务结果、关闭原因或异常传播。
3. 清理产生的不同异常分别报告，带 Stage 和相同连接/实例字段。若清理是首个故障，应作为主故障记录并传播；不能只在“已有主异常”分支记录清理错误。
4. Scope 只覆盖当前逻辑操作并及时释放；WSS 并行发送/接收/派发和 KCP owner/应用任务都要核对上下文传播。KCP owner 轮转 child 时只在当前 child 处理范围建立上下文，禁止把一个 child 的 Scope 留给后续连接。跨队列交接显式携带必要的非敏感值，不能假定 ExecutionContext 会随消息自动传播。
5. 在日志异步入队之前固定字段快照，不传入池化 DTO、OwnedFrame、可变凭据数组或依赖已释放对象的延迟格式化回调。业务提交时在锁内取得最小事件值，在锁外写日志；不在账号/房间状态锁内调用 Provider。
6. 密码、盐/哈希、恢复凭据及 AccountSessionId、ticket、PSK、证书密码、连接串、SQL 参数、完整消息正文不输出；默认不输出账号名和完整远端 IP。异常文本和第三方诊断一并核对。需要裁剪或脱敏时只改变日志视图，原异常及其传播保持原样，不以一层笼统文本替换业务故障。

### 10.5 实施步骤与退出条件

以下保留完整完善路线。L1 与 NLog 输出基础已完成；本轮完成仓库网络实现及关联调用边界的源码基线并实施首批 L2 故障事件，L3 业务事件仍未实施。外部库实现内部审计与生产洪峰测量尚未完成。每步形成可评审差异并更新进度，不能通过新增日志掩盖状态与资源问题。

| 步骤 | 修改范围与具体动作 | 退出条件 |
| --- | --- | --- |
| L0：建立基线 | 完整阅读日志相关实现和调用方；绘制启动/关闭资源顺序及异常传播关系；登记现有事件、遗漏、重复与敏感输出；比较局部修改和生命周期调整 | 清单闭合，每条主失败与独立清理失败有记录责任方，具体事件表可用于实现与测试 |
| L1：建立框架模块并统一接入 | 在单一 `GameFrame.Logging` 中实现选项校验与 NLog 接入，复用标准 ILogger；在 `Game.Server` 读取部署配置并选择输出；确定格式、类别过滤、实例字段和配置来源；提前创建应用拥有的 factory，两个 profile 借用 | 日志模块不依赖网络/业务，调用方不依赖 NLog API；模块可独立验证；参数、Tables、证书、数据库和 grant 读取失败均有应用日志，失败终止后续步骤并以非零状态退出 |
| L2：统一框架事件 | 修改 `ServerConnection`、`WssServer`、`KcpListenerHost` 的现有记录点；补齐首因/清理/终态路径、连接/请求字段和操作范围；不改变帧协议与准入语义 | WSS/KCP 同类事件语义一致，主故障只记录一次，独立清理错误完整可见，并发连接字段不串线 |
| L3：接入业务事件 | 按需将 logger 注入 `LoginModule`/`LoginHandlers`、账号会话、`LobbySession` 和 `GomokuRooms`；由状态所有者记录提交事实，处理器记录适配结果；不为日志新增业务转发层 | 登录/恢复/注销、顶号/到期/撤销、房间开始/结束可关联；正常拒绝级别正确；一次业务事实不被多层重复声明 |
| L4：容量与输出验证 | 配置 NLog Console 的有界异步队列与满额策略，核对 Provider 真实排空/释放语义；验证文本/JSON、级别、Scope 和日志关闭顺序；补充相应行为测试 | 慢输出/洪峰下资源有界且网络 owner 不等待输出；丢弃可观察，正常关闭有排空证据，失败/超时边界如实报告 |
| L5：回归与交付 | 运行相关测试和真实服务进程，记录日志开启前后成本及平台范围；更新实际配置、启动示例、事件字段和证据 | 首轮检查项通过；未验证平台、容量和第三方行为明确列出，使用说明与实现一致 |

L1 配置沿用标准类别过滤，local 初始默认 `Information`，`Microsoft.AspNetCore`/`Npgsql` 的常规输出默认 `Warning`；业务 Debug 由配置显式启用。无效格式、级别和容量应在监听前失败。配置不能改变生产准入或启用数据库敏感日志。WSS host 不得另外注册第二套相同 Console Provider。

L3 的“状态提交”与“响应送达”严格区分：登录绑定完成不代表客户端收到 token，房间命令提交不代表 Push 已送达；断线与请求取消也不证明数据库未提交。经济审计或棋谱若以后需要持久化，使用领域数据模型与事务，不能用普通异步日志代替。

当前默认队列长度为 4096 条，满额丢弃新日志，不让生产者等待控制台。已验证队列满时的丢弃行为，尚非生产容量结论；Error/Critical 同样可能丢弃。丢弃由 Provider 通知计数并经 Meter 暴露，不向同一已满队列写诊断。队列另有正在处理的批次，条数上限不是精确字节上限；事件应只携带小型非敏感值，不持有正文或资源对象。完整事件/异常大小审查及资源峰值测量待 L0/L2/L3/L4 完成。不自研写盘线程或队列。

正常关闭顺序为“停止准入 → 按既有契约 join 网络/handler/业务 → 释放业务、数据库、证书等资源并记录收尾 → 输出最终停止结果 → 排空并释放日志”。排空时限及未完成结果按实际 Provider 能力配置和验证；不能超时后宣称全部落盘，也不能遗弃仍拥有应用资源的任务。进程强杀、崩溃和系统关机不增加退出拦截与无限等待承诺。日志配置尚未成功时，入口用固定、无敏感信息的 stderr 启动诊断并终止；这只是早期失败出口，不切换运行模式或隐式启用备用 Provider。

### 10.6 验证矩阵与执行入口

优先扩展 `Tools/NetworkServer.Tests/WssLifecycleTests.cs` 的现有日志捕获方式及 `KcpLoopbackTests.cs` 的 logger 测试，验证事件、结构化状态、异常和 Scope；不只断言日志条数或自然语言文本，也不把 Provider mock 当作真实 Console/文件行为证据。

| 场景 | 验收证据 |
| --- | --- |
| 框架边界、独立接入与 Provider 替换 | 单一日志工程不引用 Server/Core、游戏协议/业务，NLog 接入集中在日志工程；独立日志测试无需网络/数据库；注入捕获 Provider 不修改业务事件 |
| 无效 profile/日志配置、损坏 Tables、证书与数据库初始化失败、grant 文件读取失败 | 应用事件或日志初始化前的固定 stderr 可见；不监听、不继续初始化、不泄漏敏感值；进程非零退出 |
| WSS/KCP handler 未预期失败 | EventId、级别、Opcode/CorrelationId 和连接/代际一致；主异常堆栈只在责任边界记录一次 |
| handler 失败同时取消回调/Closed/Dispose 失败 | 首因不被替换，独立清理事件完整，资源仍按既有契约释放 |
| 正常 EOF、主动关闭、协作取消和预期业务拒绝 | 无误报 Error/Critical，不改变排空、回复后关闭或绑定撤销结果 |
| 两连接并行请求、KCP owner 轮转、旧代际晚到 | 上下文不串线；相同 CorrelationId 能靠连接组合定位；Scope 在异步交接后仍正确 |
| 登录成功/失败、Resume、顶号、到期、注销与房间提交 | 事件反映真实状态和身份代际，日志不宣称网络送达或取消回滚 |
| 敏感测试标记进入 DTO、异常及第三方错误 | 文本、JSON、Scope、异常链与 stderr 均不出现密码/凭据/连接串等标记 |
| 文本/JSON、类别过滤和关闭 Debug | JSON 逐行可解析，字段类型/模板正确，无重复 Provider；过滤后不计算昂贵诊断参数 |
| 受控慢输出、队列满和持续拒绝洪峰 | 记录吞吐、RPC/owner 延迟、分配/GC、RSS、丢弃与限频情况；生产者无同步输出等待、状态数量有界 |
| 宿主外资源收尾、正常停止与输出不可用 | 日志晚于应用依赖释放；最终事件与排空结果有证据；输出故障不伪装为服务正常停止或修改业务结果 |

相关命令从仓库根目录执行，使用锁定 SDK；配置 PG/native 的步骤见 [验证与现有证据](DotNetUsage.md#验证与现有证据)：

```sh
(cd DotNet && dotnet restore DotNet.sln --locked-mode)
(cd Tools && dotnet restore NetworkServer.Tests --locked-mode)
(cd Tools && dotnet restore Logging.Tests --locked-mode)
(cd DotNet && dotnet build DotNet.sln -c Release --no-restore)
(cd Tools && dotnet test NetworkServer.Tests -c Release --no-restore)
(cd Tools && dotnet test Logging.Tests -c Release --no-restore)
```

新增依赖时显式更新对应锁文件后再 locked restore。SDK、项目和测试实际依赖需要的其他还原按现有使用说明执行，不能通过删除锁文件避开检查。测试缺少 `GF_POSTGRES_TEST_CONNECTION` 或 `GF_KCP_NATIVE_LIBRARY` 而跳过时，分别保留数据库/UDP/DTLS 待验收项。真实进程验证覆盖两个 local profile 的启动、故障和正常停止；性能至少对比日志关闭、默认级别和受控洪峰，报告具体负载/平台/配置，不从 loopback 推断生产容量。

验证只读用户现有配置，使用独立测试数据库/schema和临时路径；不输出真实密钥，不重置数据库，不替换已加载 native。临时日志和检查产物在验证结束后清理。仅服务端日志变更无需重复 Unity/Player/IL2CPP 或原生构建；若实施扩大到共享源码、协议或 ABI，再按实际影响追加对应门禁。

### 10.7 可选文件输出与进度记录

只有部署需要应用直接落盘时再实施文件输出：选择一个 Provider，明确日志根目录/实例隔离、按时间或大小滚动、保留期限与总容量、队列溢出/丢弃可见性，以及刷新/关闭的真实语义。验证不可写目录、运行中磁盘满、轮转/清理和进程重启，不静默退回 Console；也不因为日志写入失败自动重执行业务。部署采集器负责的行为不伪装成应用能力。不按连接/玩家创建文件，是否另存 Error 由实际查询需要决定。这是后续扩展，不阻塞首轮 Console 方案的完成。

| 交付项 | 当前状态 | 完成时补充的证据 |
| --- | --- | --- |
| L0 完整源码/调用关系基线 | 已完整阅读仓库网络清单及关联接入/测试/构建边界；外部依赖仅核对锁与接入，不声明内部完整安全审计 | 本轮覆盖与限制见服务端使用说明；保留现有状态/资源流程，修正故障出口与文档现状 |
| L1 框架模块、统一配置与应用所有权 | 已合并为单一 GameFrame.Logging 工程并接入两个 profile | 独立日志测试、真实 WSS host 释放后写入、早期失败进程和 KCP 启停验证；真实 PG 启动故障尚未验 |
| L2 框架事件与上下文 | 已实施上述四类故障事件与脱敏，正常生命周期/性能事件和洪峰治理仍待完善 | 真实 WSS/KCP 故障关联、首因与独立清理、原异常传播、NLog 文本/JSON 测试；本轮结果见使用说明 |
| L3 业务事件 | 待实施 | 认证/撤销/房间事件及脱敏验证 |
| L4 有界输出与排空 | 基础已实现并验证；端到端压测待执行 | 文本/JSON、过滤、并行 Scope、满队列、写入错误、刷新失败/超时；生产容量/洪峰待验 |
| L5 首轮交付 | NLog 接入阶段已验证，不代表全路线完成 | 具体构建/测试/进程证据见使用说明；其他平台、PG 和生产压测待验 |
| 日志性能基线与门禁 | 已验证禁用源生成调用分配与慢输出隔离；完整基线待测 | 默认输出/洪峰/慢输出的端到端吞吐、延迟、CPU/RSS 与 GC 对比 |
| NLog Provider 接入 | 已接入 NLog/NLog.Extensions.Logging 6.2.1，更新依赖锁和 CI 入口 | 字段/过滤/所有权、输出故障与刷新验证；未声明比原 Console 更快 |
| 应用直接落盘 | 可选，待部署需求明确 | 适配支持显式 Target；文件轮转、容量、磁盘故障尚未配置/验收 |

当前在既有日志工程与应用组合基础上补充网络故障事件，业务事件和完整性能治理仍属后续范围。环境变量、事件及验证限制见 [服务端日志使用说明](DotNetUsage.md#server-logging)。

<a id="server-logging-performance"></a>

### 10.8 性能约束与已接入的 NLog

日志性能作为 L1～L5 的实现约束与验收内容。更换 Provider 不能自动证明性能提升；异步输出主要隔离输出 IO，参数求值、结构化状态构造、Scope、异常处理、部分布局渲染与入队仍可能发生在调用线程。Console 重定向到采集器时同样可能变慢，不能将本机终端显示速度作为性能基线。

调用侧执行以下约束：

- 高频事件优先使用源生成 `LoggerMessage` 方法，避免反复解析模板及通用参数数组/装箱成本；低频启动和清理事件不为形式统一增加复杂封装。
- 昂贵参数在 `IsEnabled` 之后计算；关闭 Debug 时不做插值、DTO 序列化、集合展开、字符串拼接或额外堆栈采集。源生成日志不能避免调用前的参数表达式求值。
- Scope 只保留关联所需的少量字段，避免逐包创建字典、长生命周期保存业务对象和重复附加同一组字段。作用域传播和字段快照都计入成本，不要求形式上的“全链零 GC”。
- 不在网络/native owner 或业务锁内执行同步输出、Flush 或队列等待，不为每条日志创建 Task/Task.Run。异步输出使用 Provider 的有界队列和批量写入，正常运行不逐条强制刷新。
- 正常高频行为使用 Metrics；预期拒绝使用聚合或受限的汇总事件。重复故障先按主因/终态消除重复记录，再按事件语义确定限频；不能把首个故障与不同的清理故障一并隐藏。

L4 测量同时覆盖调用侧与端到端行为，Release 构建、预热、固定业务负载与固定输出环境后重复采样。下表各组沿用同一事件模板和字段，分别记录配置、连接数、RPC 并发、事件速率、消息/异常大小、测试时长和输出介质；每组记录尝试/接受/丢弃/实际输出条数，队列积压单独列出，不能以大量丢弃换取更快数据。

| 对照组 | 重点记录 |
| --- | --- |
| 日志禁用 | 业务吞吐、RPC P95/P99、owner 耗时、CPU、RSS、托管分配与 GC，作为基线 |
| 调用保留但级别过滤 | 每次调用耗时/分配、参数计算及 Scope 成本，确认禁用路径不执行昂贵诊断 |
| 默认文本 / JSON 输出 | 调用耗时与端到端变化、格式化/编码成本、输出吞吐和队列积压；文本/JSON 分开比较 |
| 故障洪峰 / 慢输出 | 异常格式化、限频、队列峰值、丢弃数量、资源上界和 RPC/owner 尾延迟，确认无 IO 背压传入业务线程 |
| 正常关闭 | 排空时间、最终输出/丢弃数量与超时结果；入队成功不当作写出或持久化成功 |

测试前登记目标负载、允许的吞吐/尾延迟回归与内存预算，执行后按这些目标判定，不能仅报告“日志很快”或事后放宽标准。先定位成本来自调用侧、Scope/格式化、队列还是输出介质，再决定减少事件、调整批次/容量或更换 Provider；性能不达标时不能仅扩大队列转移内存压力。

当前接入路径为：

```text
框架与业务 ILogger<T>
        ↓
应用拥有的 ILoggerFactory
        ↓
GameFrame.Logging（内部接入 NLog.Extensions.Logging 6.2.1）
        ↓
AsyncTargetWrapper（有界、Discard、批量）→ 输出错误观察 → ConsoleTarget
```

- `GameFrame.Logging` 引用 Microsoft.Extensions.Logging 10.0.12、NLog 和 NLog.Extensions.Logging 6.2.1，包含 LoggingOptions、NLogOutputOptions 和 NLogLogging。Core/Server 没有直接 NLog 引用，事件调用保持标准接口。
- `NLogLogging.Create` 接收通用与输出选项，显式创建独立 LogFactory/ILoggerFactory；不用全局 LogManager，不自动读取 NLog.config 或游戏环境变量。默认 ConsoleTarget，也可由组合端提供独占 TargetWithLayout；factory 管理该 Target 的布局和释放。
- `LoggingOptions` 包含 Service、Environment、InstanceId、Version、MinimumLevel 和 CategoryLevels；元数据为 1～128 字符且无控制字符，按字面值渲染，不解析成 layout 表达式。类别阈值取全局与类别设置中较严格者，None 禁用全部类别；过滤在标准 ILoggerFactory 层生效。
- `NLogOutputOptions` 包含 Text/Json、QueueCapacity（默认 4096，1～65536）、BatchSize（默认 200，不超过容量）、FlushTimeout（默认 5 秒，大于 0 且不超过 30 秒）。无效选项在输出初始化前失败；Game.Server 提供部署值与默认类别策略。
- 单层 AsyncTargetWrapper 满额丢弃新事件，生产者不等待输出；批量转发保留 Target 批量能力。队列只约束待写事件数，另有在处理批次及事件/异常大小，不能宣称精确字节上限。Error/Critical 同样可能丢弃；调用线程仍承担状态捕获与必要布局工作，不默认启用 callsite 或完整对象展开。
- DroppedCount、OutputErrorCount 和 meter `GameFrame.Logging.NLog` 的 `gameframe.logging.dropped` / `gameframe.logging.output.errors` 提供无递归诊断。Meter 名称沿用既有指标标识，不是程序集或命名空间。输出观察包装只计数并保留首个输出错误，不重试、不新建队列；当前没有默认指标导出器，部署方需订阅。
- `DisposeAsync` 刷新后释放 ILoggerFactory、NLog factory 和 meter，重复调用等待同一结果。刷新失败、超时或已观察到的输出错误会失败；应用用固定安全 stderr 报告并返回 1。FlushTimeout 约束刷新等待，不是任意 Target.Dispose 的总时限，不能描述成保证 5 秒内退出。Console 写出也不代表磁盘持久化。
- 已验证模板、EventId/名称、结构化属性、并行异步 Scope、文本/JSON、过滤、字面元数据、工厂隔离、满队列、输出错误、刷新失败和超时。禁用源生成事件的本机成本样本见使用说明；完整端到端性能对照、敏感信息全量审查与文件部署故障仍按本节门禁继续执行。
