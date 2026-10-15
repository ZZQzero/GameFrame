# 客户端网络开发规范

适用于 `Unity/Assets/Scripts/Runtime/Network`、Hotfix 网络调用及关联配置、协议、插件与测试。使用步骤见 [客户端网络使用说明](ClientNetworkUsage.md)；共享 GFNP/协议生成/原生 ABI 以 [服务端全局开发规范](DotNetDevelopment.md) 为唯一维护入口，不在客户端维护第二套参数或协议格式。

总体设计与验收契约保留在 [NetworkDesign](NetworkDesign.md)，本文说明客户端开发规则和当前实现边界。当前框架支持 WSS 可靠和 C KCP/DTLS 双通道，第一版不实现 WebGL WSS。GameNetworkSession、世界/角色/AOI/移动/恢复处理器未实现；未使用的世界 Proto/生成路由已删除，当前 catalog 为账号、Echo 与五子棋共 14 条可靠路由；世界内容只保留文档候选契约。设计变化同步更新 NetworkDesign，其他改动更新四份规范和说明，不新增重复的实施或审查记录。

## 1. 职责与程序集

| 层 | 维护内容与边界 |
| --- | --- |
| GameFrame.Runtime / GameFrame.Network | NetworkRuntime/Client/Context、传输、帧/RPC/队列/期限/诊断，业务无关 |
| Game.AOT | 安装包启动、DLL 与资源准备；不引用 Hotfix 或初始化账号/世界业务 |
| Game.Hotfix | 游戏网络配置、生成 DTO/catalog、业务状态、凭据、订阅与显式恢复 |
| Protocol / DotNet/NetworkProtocol | 唯一.proto 真源和双端同源生成，不在 Unity 另写 DTO/opcode |
| Native/Kcp | 双端唯一 C 源码、依赖锁和构建；Unity Plugins 目录只放安装产物 |

Runtime 不引用 Hotfix/Editor；Hotfix 通过冻结 NetProtocolCatalog 提供 codec，系统控制包固定编码不依赖业务 DTO。不要复制第二套 RPC、计时器或事件总线，不让 Runtime 修改 Transform 或维护账号、背包、房间和世界状态。

客户端、共享 Core 与服务端网络框架统一使用 `GameFrame.Network`。Connection、Protocol、Transport、Kcp 等目录只用于组织源码，不再各建子命名空间；程序集及依赖边界保持独立。双端生成的 DTO、枚举、路由与 catalog 统一使用 `Game.Protocol`，客户端仍在 Game.Hotfix 程序集中编译，服务端由 Game.Protocol 工程链接同源文件。游戏会话与玩法继续使用各自的业务命名空间。

GameRuntime 可选拥有 NetworkRuntime，GameEntry 只初始化热更业务；默认 NetworkOptions=null，保持离线，不自动建客户端/连服。驱动使用已有持久根，DLL 装载后由 GameEntry prefab Awake 进入业务，不加反射入口或 Launch→Hotfix 调用。遵循 [启动](Launch.md)、[作用域](Scope.md)、[错误契约](ErrorContract.md) 和 [全局规则](../AGENTS.md)。

C#按 4 空格/Allman、分支与循环展开、Async 后缀及明确资源归属维护；生成/第三方不手工改格式。修改生成配置/模板后重新生成。审查必须先建清单和调用链并完整阅读指定范围的实现、调用/依赖、配置、生成工具/产物、测试/构建；不能抽样或用测试代替源码审查，持续记录未读/受阻项。

## 2. 线程与派发

全部公开业务 API、状态/指标读取、订阅和释放在 Unity 主线程；DTO 编码/校验、解码、Push/状态观察者与业务任务完成也在主线程。不得 Wait/Result 阻塞主线程。取消回调可在后台运行，但只仲裁状态并唤醒，不调用 Unity 或业务。

WSS 每连接一个收任务、一个发任务，不并发调用两个 WebSocket Send/Receive。IO 做字节、头部、Hello/control 与响应到达仲裁，业务进入有界 Pump 队列。KCP 由宿主单 worker 管理 UDP/native/期限，handle 只在 owner 线程调用；不得逐包 Task.Run 或逐包 PlayerLoop 回调。

主线程 Pump 默认 2ms/128 个工作单元，各客户端每轮最多 16 个单元；Context 持续消费按 16 可靠/最多 4 不可靠轮转。同连接可靠响应/Push 按到达顺序，不能给 RPC 提优先级；不同通道无顺序承诺。控制 Ping/Pong 不进业务队列。

时间预算在消息之间检查，不能中断一个回调；包含 schema、解码、回调、同步 continuation 和清零，单条仍可能超预算。Pump 之外发送编码成本另计每帧总网络成本；不在回调阻塞 IO 或大批创建场景对象。

实际交付 Push 先在 Pump 统一校验正文，不依赖是否订阅。非法正文产生 MessageFault 并 Faulted；合法未订阅消息不创建 DTO，订阅消息只解码，不重复 shape 扫描。迟到 RPC、过期不可靠帧与终态清理按各自丢弃路径，不解码。

## 3. 状态、连接代际与资源

```text
Idle → Connecting → Handshaking → Connected
Connecting/Handshaking/Connected → Faulted（失败）
连接中/Connected/Faulted → Closing → Idle（释放完成）
Dispose完成 → Disposed（不再复用）
```

每次连接创建独立 ConnectionContext/generation，拥有 socket、CTS、IO 任务、native、pending、队列与首因；异步循环只捕获该 context，不读取下次连接替换的字段。generation 用于本地隔离，不是鉴权。故障拒绝新工作，必须 await Close 后再 Connect，旧工作不得进入新 generation，计数耗尽拒绝继续。

Connect 完成意味着 TLS/DTLS+GFNP Hello 完成，不代表登录或世界 Ready；KCP 虽绑定 grant，业务仍需自己确认阶段。Connect 调用取消只作用于本次建连，成功解除注册；会话 CTS 不能代替显式异步关闭。

Close/Dispose/Shutdown 先发布唯一共享任务再通知可重入观察者；旧 context 收尾不能清除新 context。关闭拒绝 admission、结束未决请求、停止 IO/socket、等待 owner 实际退出、排空缓冲、销毁 native 后完成。系统 Close/标准 WebSocket 关闭确认最多尽力 2 秒后 Abort，Abort 后仍 join，不把超时当已释放。

普通远端 EOF 把断线标记排在此前可靠帧之后，已完成结果不改写；主动关闭/故障停止后续业务，已保留响应也可失败。解码/Push 故障可覆盖尚未交付的有序断线结果，不能因此继续执行后续状态流。

运行中 Hotfix 停止输入/恢复任务→取消/结束业务→释放订阅并 await 客户端 Dispose→GameRuntime.ShutdownAsync 等待网络→再释放 Timer/root/YooAsset。不得先销毁/禁用 Runner。进程退出保持简单，不新增等待拦截。

宿主 Pump/worker 不变量破坏或 Runner/root 提前销毁、禁用，锁首因并使全部客户端失败、拒绝新客户端/连接。独立 PlayerLoop 清理不依赖受损 Runner，不解码/执行 Push；Close/Dispose/Shutdown 仍可完成。关闭单客户端不恢复宿主，显式 Shutdown 后重新初始化。

清理失败仍尝试其他独立资源；保留首因，资源不自动重试。native destroy 未确认成功不归还 allowance 或报告零。Dispose 即使 Close 失败也注销订阅/宿主登记，重复调用得到同一结果；Shutdown 仍尝试 worker 收尾。

## 4. RPC 期限与取消

每个连接的 correlationId 从 1 单调递增，u64 用尽后终止连接，不能绕回复用。一个 RPC 有以下状态：

```text
Created → Queued → TransportAccepted → ResponseReserved → Completed
   └────────────── 任一未完成状态 → Canceled / TimedOut / Failed
```

登记 pending、预留发送额度、分配 correlationId 和入队是一次 admission 操作；其中任一步失败，不留挂起任务或半个队列项。超时从 admission 开始，包含本地发送等待；timeout 必须大于 0，默认业务配置建议 5 秒，每个调用可以显式指定。排队预算耗尽直接 `NetworkBackpressureException`，不伪装成超时。

响应头完成校验且完整帧到达时，由 IO 层在 pending 锁内仲裁：若此时 `now >= deadline`，超时赢；否则转为 ResponseReserved 并移除 deadline 节点。这样主线程派发积压不会被计算成服务端响应超时。ResponseReserved 仍占用 pending 与接收额度，只有主线程完成解码/取消才能释放。

取消与响应完成以 pending 同一原子状态转换决胜：ResponseReserved 后、Completed 前仍允许调用者取消；排队中的帧保留缓冲所有权，到达主线程时检查状态并释放，不再完成原任务。完成后来的取消不改写结果。每个 accepted RPC 最多产生一个终态、一个完成记录；取消注册注销、deadline 删除、额度归还必须在所有终态执行。

pending 锁只保护状态转换、索引与额度转移；锁内摘取待清理资源，锁外注销/释放取消注册。任务完成、UniTask continuation、业务/观察者回调均在主线程且在锁外执行；锁内不 await、不做 IO、不调用业务代码。取消回调只争夺终态并唤醒驱动，不能同步等待主线程。取消注册可能立即调用回调，登记时也必须遵守同一边界，避免持锁 Dispose 注册与取消回调互相等待。

pending 查找表可以在终态仲裁时摘除，但 admission 槽位和完成记录预算必须保留到主线程消费完成记录后才归还。否则主线程停顿期间反复取消/创建请求，仍会产生无界的已终止完成记录。512 上限包含正在等待与已终态但尚未交付的请求。

正常传输断线事件在已验证的可靠帧之后排入主线程队列，不能抢在其之前覆盖已到达的响应。协议故障、缓冲耗尽、宿主故障无法继续派发时，所有尚未 Completed 的请求以该故障结束，ResponseReserved 不等于业务成功。已确定结果永不改写。

响应必须同时匹配 context、correlationId、opcode 和预期类型。晚到响应只记录计数，不需要长期维护“过期 ID 集合”。未知服务端请求不由客户端自动执行。Runtime 只支持客户端发起 RPC；若将来需要双向 RPC，必须升级此契约与安全模型，不能复用 Push 偷做。

## 5. 错误与观察者边界

| 错误来源 | 策略 |
| --- | --- |
| 本地非法参数、重复订阅、无效状态、DTO 出站约束失败 | 当前调用抛异常，不断开其他正常连接 |
| 出站队列满 | 当前 admission 失败；已接受工作继续 |
| 尚未移交传输的帧排队超时限 | RPC 以背压或已到期的 deadline 结束；普通 SendAsync 已完成 admission，后续失败使连接 Faulted；不可靠帧丢弃 |
| 显式请求取消 | 当前请求取消；不自动断线或重发 |
| RPC deadline | 当前请求 `NetworkRequestTimeoutException`；连接继续，结果可能未知 |
| 非 0 Response StatusCode | 当前请求 `NetworkRemoteException`，保留远端码，不自动重试 |
| 可靠接收溢出、非法帧、DTO 解码失败、原生分配失败、网络 IO 错误 | 当前连接 Faulted；所有未确定工作失败 |
| 业务 Push 处理器抛错 | 当前连接进入派发故障并 Faulted；未执行的后续消息丢弃并释放，其他连接继续 |
| 状态/故障观察者抛错 | 该次通知批次停止并在主线程边界记录，不能覆盖原网络结果；不重新通知 |
| 宿主 Pump/工作线程内部不变量破坏 | 宿主停止驱动，所有客户端失败，允许最终 Shutdown |

Push 处理器的较大失败边界是有意选择：第一版每个连接承载一条有序业务状态流，处理器抛错意味着该流的连续提交已中断，Runtime 无法证明后续消息仍可安全应用。因此不继续执行同连接的依赖消息，也不自动重新调用处理器。需要互不依赖的故障域应使用不同客户端，或由业务处理器在自身明确可处理的范围内返回正常业务结果。

无人 await 的后台任务不能静默 fault。IO 捕获原异常交给连接终止，不重复写多份日志；最外层记录一次结构化故障。`ObserveMessageFault` 携带 opcode、generation 和原异常，供 Hotfix 使会话离开 Ready；故障观察者不是恢复失败处理器的机会。

## 6. 预算、背压与内存

配置真源为 NetworkRuntimeOptions/NetworkClientOptions；选项在创建时校验并快照，可信端点数组复制。队列同时限制条数和容量，各子预算共享总额，不能求和获得额外额度。

| 客户端项目 | 当前默认 |
| --- | --- |
| 宿主 | 最多 4 客户端，64MiB；Pump2ms/128 单元 |
| 每客户端总额 | 8MiB |
| 可靠发送 | 64KiB/128 条，最大队龄 250ms |
| 可靠接收 | 2MiB/1024 条，满额 Faulted，队龄 250ms 告警不丢消息 |
| 不可靠收/发 | 各 64KiB/64 条；接收淘汰最旧，250ms 过期在解码前丢弃 |
| 控制队列 | 16KiB/64 条，满额 Faulted |
| Pending | 512 个/256KiB 保守 reservation，含已终态未主线程交付工作 |
| KCP native | 默认 4MiB，在连接/宿主总额中先预留，实际 destroy 后归还 |
| KCP 业务发送 | 128KiB/s、burst32KiB；不可靠另 32KiB/s、burst8KiB 且消耗总桶 |
| 空闲缓冲池 | 当前未实现，实际 0；MaxIdlePoolBytes 配置默认 8MiB 不表示缓存已启用 |

当前每次分配精确容量 byte[]/lease，无空闲池，发送按路由最大正文容量分配并清零全容量，尚有逐包分配。每帧/每 pending 额外 512 字节是保守预约，不是实测 CLR/IL2CPP 布局；常驻容器、任务/取消/完成记录、外部 TLS/内核、业务保留 DTO 需单独测量。OwnedBytes、native 预留、native 实际、RSS 分别展示，不声称当前完整内存或零 GC 验收已完成。

扩容先预留旧+新峰值；帧传输交接不提前归还。调用期间只读 DTO，入队前编码完成，返回后调用者可修改；入站 DTO 独占不池化，业务可持有，字节 finally 释放并清零实际容量。原生 segment/DTLS 副本有自己的 quota 直到 ACK 或销毁。

KCP 可靠 admission 在分配 correlation/pending 之前以应用 token 与 waitsnd 窗口缺口估计等待，超过队龄/RPC 剩余期限入口拒绝；真实移交再检查取消、deadline、队龄。控制/可靠/不可靠各 FIFO，背压不弹出队首或扣 token。WSS 无带宽预测，但同样检查容量/队龄；不可靠满额 Try 返回 false，过期丢弃，不能自动换可靠路由。

KCP ACK/重传/探测/DTLS 握手不经业务桶，wire 带宽可高于业务速率。burst 必须容纳协商最大帧。原生输出/MTU/owner/tick 及 ABI 完整规则见 [共享C规范](DotNetDevelopment.md#7-共享-c-kcpdtls-实现规则) 和 [ABI2](DotNetDevelopment.md#8-共享原生-abi-2)。

## 7. 安全、认证与未来业务会话

端点必须来自可信环境配置；正式 WSS 使用 wss 并正常验证证书，不信任任意返回的 URL/IP/端口。Editor/Development 显式 allowDevelopmentWebSocket 才能 ws，正式构建禁用；本地服务仍只开放 WSS。WebGL 当前建连明确 PlatformNotSupported，不进入桌面 socket/native 路径。

本地 Editor 可通过 `LocalDevelopmentCertificate` 显式配置 localhost 公开开发证书。路径及 SHA-256 指纹按项目保存到本机 EditorPrefs；不把私钥或证书信任配置放进运行资源。播放前验证路径、有效期、DNS 名及固定指纹，失败阻止本次播放；BeforeSceneLoad 在业务初始化前将证书追加到 Mono TLS 信任锚，保留已有锚和默认验证器。停止播放、域重载及退出 Editor 恢复原设置；没有配置时不改变 TLS。工具仅存在于 GameFrame.Editor，不影响正式 Player，证书轮换须显式重新选择。

KCP 凭据通过未来已认证 WSS 授权取得：ticket、非零 conv、16 字节 identity、32 字节 PSK 与有效单调 deadline；当前 local 仅注入受控测试 grant。调用者在 Connect 完成前不修改数组，结束后清零临时副本；native 自己复制并清零密钥。PSK 不进安装包/版本库/日志，认证不可用 conv 或 generation 代替。

Connect 后显式 Login/Resume，独立 Register 可选；Login 自动创建不存在的账号。先检查 ResultCode，再校验成功 accountId/authEpoch 非零、accountSessionId 恰好 16 字节、resumeToken 恰好 32 字节及 TTL，才提交 Authenticated。TTL=RPC admission 单调时间+返回剩余时间，不从响应收到时续期，UTC 只显示/诊断。失败载荷不当成功，结构验证器不能代替权限语义。

心跳单调时间：间隔 5 秒、一个未确认 nonce，移交后 15 秒 Pong 期限、控制入队后独立 15 秒发送期限；匹配 Pong 必须严格早于 deadline。总 Connect10 秒、KCP 不超过 grant 期限。TimeScale=0 不影响后台期限，不能用 Time.time 或依赖帧的 Timer 替代 IO 时钟；系统挂起后不承诺线程持续保活，恢复显式检查状态。

当前 `GameLoginSession` 是 Hotfix 的普通实例，拥有一个 WSS 大厅客户端、私有认证响应、只读 GameUser、任务与生命周期 CTS。GameEntry 只创建/注入/停止它，LoginPanel 通过 LoginPanelArgs 借用；后续业务通过构造参数或面板参数接收同一个实例，不查询入口或新增静态 Current。只有校验成功结果/TTL 且连接仍有效后，才原子提交用户与凭据；密码不进入 GameUser。面板关闭只取消该次操作，成功关闭面板不注销会话。

登录、恢复、读取/清除本地账号与注销串行准入，重复操作拒绝；失败、超时或取消等待 Close 清理后释放操作资格。DisposeAsync 先发布共享关闭任务、取消并等待在途操作，再释放观察者/客户端和清零凭据，重复释放等待同一任务。断线/顶号/注销使 CurrentUser 为 null。明确替换、到期或撤销时，凭据条件清理排在当前账号操作之后，完成后通知 GameFlow 回到登录；普通断线保留恢复资格。条件清理只比较已保存令牌的 SHA-256 摘要，不持有原令牌，不删除随后保存的新凭据。账号操作完成只释放自己的操作占位，不能提前释放后续清理的资格。当前通过 ResumeLoginAsync 提供玩家点击触发的恢复，不自动重连。RememberedLoginStore 使用 SQLite 保存账号元数据、系统安全存储保存恢复令牌，密码不持久化；只读用户快照不能作为权限依据，每次业务准入检查 IsAuthenticated。

认证失效通知负责清空会话，UI 导航还需满足 GameFlow 运行、GameRuntime 运行及 GameUI 已初始化。退出时 Launch 与 GameEntry 回调顺序不固定；框架已经关闭或开始关闭时，不再执行登录/主界面导航，异步打开完成后也需复核生命周期。不能靠调整框架关闭顺序或吞掉 GameUI 未初始化异常处理退出。

账号认证成功后由 Hotfix 的 GameFlow 通过 GameScene 加载 Main，配置相机 Stack 并成功打开主面板后才关闭登录面板。GameEntry 创建流程并注入借用的 GameLoginSession，不持有导航规则；流程跟踪主场景导航任务，主动停止时等待它收尾，再由 GameEntry 释放账号会话与配置，具体职责见 [启动与业务流程](Launch.md#hotfix-业务流程)；场景加载不能取消，完成后核对同一 GameUser 实例与生命周期，旧账号结果不能进入新账号业务。场景失败由面板报告，保留认证供玩家显式再次进入，不自动重试或重复 Login；Main 已加载时复用，断线后在当前场景显示登录。

后续 GameNetworkSession 放 Hotfix，拥有大厅、独占 KCP 世界客户端、账号/世界 epoch、凭据、CTS 与分别的状态机；不由临时 UI 面板创建销毁。一个 opcode 一个权威订阅，先更新业务模型再经已有 EventSystem 通知 UI。OpenScope/LifetimeScope 可拥有订阅和请求取消，但同步 Dispose 不能 await 连接关闭。

| 业务场景（待实现） | 所有权与恢复约束 |
| --- | --- |
| 世界复用 WSS | world 借用 lobby，只有大厅 owner 释放物理客户端；LeaveWorld 确认停止对应流后才释放世界订阅 |
| KCP 世界独立断线 | 世界离开 Ready→Close 旧世界→现有大厅取新 grant→新 KCP→RestoreWorld 校验后 Ready，不隐式关闭大厅 |
| 大厅独立断线 | 停大厅/授权 RPC；正常 KCP 可继续，账号撤销/到期仍由服务器执行；显式新 WSS+Resume |
| 全断或共享 WSS 断线 | 先清理→认证大厅→需要时新 grant/世界恢复 |
| Logout/封禁/到期/顶号 | 停全部业务，清理连接、授权、凭据，旧 epoch 不再提交 |

恢复操作绑定账号/世界恢复序号/context generation，迟到结果不能覆盖新账号/角色。Login 超时/取消关闭该次 WSS；Resume 拒绝不自动密码登录。共享 WSS 的 Enter/Leave 超时/取消造成世界状态不确定时关闭共享流，不能先注销订阅继续用大厅。StopSession 保留主异常并尝试全部本地清理，不重复 LeaveWorld 或自动回滚入场。

预留世界契约：worldSessionId/epoch 隔离重绑；实体 entityId+incarnation；可靠 eventVersion 连续，缺号停流显式恢复；快照独立绝对状态、每包最多 16 实体、缺席不删除，未知实体/旧 incarnation 丢弃不无界缓存；入场/恢复基线最多 64 实体、超过明确拒绝。tick/sequence 按 u32 半周期环绕比较、每实体判断；恢复推进 worldEpoch，重置输入/快照序号，基线之后只应用更高连续成员事件。插值/预测/校正归 Hotfix，不能下沉 Runtime。

购买/结算等结果不确定通过原 commandId 查询或在服务端明确幂等契约下显式重发，不能生成新 ID 再扣一次；位置输入用 tick/序号去重和过期，不用持久经济幂等表。大型快照/资源走 HTTPS 或明确有总量/hash/deadline 的业务分块，不让 Runtime 自动聚合无限消息。

## 8. MemoryPack、HybridCLR 与平台验收

共享消息只能改.proto，再运行工具同步双端；字段/枚举/类型/上限/hash 规则见 [共享协议规范](DotNetDevelopment.md#6-共享-protomemorypack-编写规则)，命令见 [生成说明](DotNetUsage.md#协议生成与验证)。不要手工写网络 MemoryPack DTO、UnityEngine 字段、Nullable 数值、自定义 formatter/回调或启用 VersionTolerant 兼容。

Editor 的 Generate Messages/Check Messages 菜单调用 `DotNet/NetworkProtocol` 外部进程，异步排空 stdout/stderr，并以退出码判定成功。工具执行时禁止重复启动，保持域重载与资产刷新锁的成对释放；具体 SDK 定位与本机设置见客户端使用说明。生成器 DLL 及 Roslyn 依赖不导入 Unity 程序集。

Unity MemoryPack 固定 1.21.4、Core 为.NET Standard2.1，Generator 保留 RoslynAnalyzer 标签且不作为普通 Editor/Player 插件加载。NuGetForUnity/UPM 及配置、依赖 meta/GUID 同步维护，恢复步骤见客户端使用说明，不用.NET10 DLL 替换 Unity 依赖。

原生 P/Invoke/传输/帧/缓冲保持 Runtime/AOT 可达，DTO 与游戏路由可在 Hotfix；使用生成 codec/catalog 与 HybridCLR 既有 AOT 补充/裁剪流程，不靠 MakeGenericType 反射发现未知类型。新热更 DLL 重新编译，不改名旧 DLL。系统 wire/nativeABI 变化可能需要新安装包；Hotfix 无法替换 iOS native。

验收覆盖：Editor 编译、相关 EditMode/PlayMode、各发布目标 Player 脚本、对应 native 导出/ABI/链接、实际 IL2CPP 客户端与服务器互通、移动真机弱网/后台/IPv6/代理/换网、24h soak。必须测试“发布安装包后热更新加入全新 DTO/路由”并双向序列化/RPC/Push/关闭；Editor 或 Player 脚本成功不代表此项通过。

性能在最低目标设备测完整每帧网络成本 P95≤2ms，另报 P99/峰值、分配、GC、清零、最长 handler 和 worker 轮转。当前 2ms 只是 Pump 调度预算；帧/控制预热后无逐包分配是待达到目标，DTO/字符串/数组仍独立统计。先测最大容量发送、统计采样与锁成本再决定优化，不提前换 ArrayPool.Shared、批量 ABI 或零复制。

2026-10-14 已补充当前连接的成功 RPC 编码、完整响应等待、主线程等待、解码与 admission 到解码完成汇总，并公开各子预算当前预约值。复用既有 pending/响应保留/Pump/预算所有权，统计在同步 continuation 前完成；不增加逐请求历史、指标管理器或业务状态。采样定义与失败排除范围见 [关闭与诊断](ClientNetworkUsage.md#6-关闭与诊断)。

当前剩余工作：完整托管元数据预约、全部发送/Push/Pump 与失败路径耗时、分位采样与统一诊断、池化/性能测量、精细期限调度和全部平台/容量验收。World/GameNetworkSession 与 WebGL WSS 继续延后。既有平台证据见 [客户端使用说明](ClientNetworkUsage.md#验证与现有证据)，以日期/版本/实际执行范围为准。

2026-10-14 实施优先级：按 [NetworkDesign 第 18.1 节](NetworkDesign.md#181-当前优先路线底层框架完善)先完善现有网络底层、诊断和验证。当前五子棋保持既有业务接入，双连接世界会话、世界恢复、移动同步、插值/预测与 AOI 在实际多人玩法需要时扩展；不为将来玩法预先增加 Runtime 业务状态、通用调度器或管理层。


### 五子棋客户端接入（2026-10-14）

GameEntry 持有 GomokuRoomSession，借用同一个 GameLoginSession.LobbyClient；GameFlow 为 MainPanelArgs 注入入口回调。入口 / 等待共用 GomokuLobbyPanel，棋盘用 GomokuOnlinePanel；面板只拥有 OpenScope 订阅 / 取消，不关闭 WSS。

每个 Push opcode 唯一订阅，RPC / Push 更新同一模型；基线前只保留匹配 admissionCommandId 的一份快照。关闭成员保留最近关闭版本，迟到旧 RPC 不复活成员。取消 / 超时后停冲突操作，玩家显式 Query，不自动重发。重复版本与基线重放不重复播放音效。停止先取消 UI / 等待导航与房间任务，再释放房间订阅，最后释放账号。

当前五子棋房间实现通过业务会话借用账号 WSS，公开只读房间和棋局视图；面板每次操作固定 OpenScope token 与会话。实时世界恢复表是候选设计，世界授权 / 入场 / 移动 / AOI 消息未保留在生产生成产物中。当前 14 条路由全部可靠，不可靠生产正文上限为 0；KCP 双通道能力由独立测试协议验证。
