# C KCP / DTLS 底层实施方案

状态：C 原生库、C# worker/传输、.NET listener 和 macOS/Windows/Android/iOS 平台产物已落地；验收范围见实施记录。以 [NetworkDesign.md](NetworkDesign.md) 为总契约，本文件细化其第 12、16 节的落地步骤；实际进度与验证证据仍记录在 [NetworkImplementation.md](NetworkImplementation.md)。

当前范围是框架底层：原版 C KCP、mbedTLS、固定 C ABI、C# UDP 驱动、Unity/.NET 接入、构建安装及底层验收。游戏世界会话、角色权限、AOI、移动和恢复业务延后；WebGL WSS 延后，WebGL KCP 明确不支持。

文档职责固定：NetworkDesign 定义公开行为和参数；本文件定义内部实现边界与交付步骤；`Native/Kcp/ABI.md` 定义精确函数签名、结构布局和错误码；NetworkImplementation 只记录实际进度/验证。相同规则优先引用，不在各文档维护不同默认值。精确 API 与错误码已冻结在 Native/Kcp/ABI.md。

## 1. 总体结构与职责

```text
Unity Hotfix / .NET 应用
    │ 生成的 MemoryPack DTO、强类型路由
    ▼
现有消息层：NetworkClient / ConnectionContext / 帧、RPC、deadline、派发
    │ OwnedFrame；跨线程只交接有界队列
    ▼
C# KCP owner：非阻塞 UDP socket、单调时钟、连接轮转、关闭命令
    │ Cdecl P/Invoke；feed / tick / send / poll
    ▼
gameframe_kcp（C）
    ├─ gfnet：状态、配额、输入/输出队列、listener、错误码
    ├─ ikcp.c / ikcp.h：可靠消息、ACK、重传、拥塞控制
    └─ mbedTLS：DTLS 1.2 PSK、cookie、防重放、加密
```

| 层 | 负责 | 所有者 |
| --- | --- | --- |
| Hotfix / 服务应用 | DTO、业务订阅、取授权、显式连接/关闭 | 应用生命周期 |
| 现有消息核心 | GFNP 帧、Hello、RPC、取消/超时、队列和故障仲裁 | 每个 connection generation |
| C# KCP 驱动 | UDP socket、工作线程、native 调用、收发交接 | 客户端网络宿主；服务端 listener owner |
| C 包装层 | DTLS/KCP 状态、有界队列、原生统计、凭据索引 | 客户端独占 handle；服务端 listener |
| 第三方 C 源码 | KCP 算法、标准 DTLS 实现 | 由包装层创建和释放 |

C# 保留平台 socket 支持，C 层接收/输出完整 UDP datagram，无需另写 Windows、Android、iOS 的 socket 适配。C 内处理 KCP/DTLS 回调，使用托管主动轮询；不向 Hotfix 反向调用委托。

MemoryPack 和 Proto 生成工具继续在 C# 层工作。C 只承载应用帧字节，不生成 C 版 DTO，不解析业务对象。原生化带来跨调用复制、加密和调度成本，性能结果以实测为准；第一版不增加批量 ABI 或零复制接口。

## 2. 代码落点与现有实现改造

实际落点如下；队列/平台分配逻辑集中在 gfnet_memory.c，不拆出额外通用层：

```text
Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/
    KcpNative.cs                 P/Invoke、ABI 检查、错误映射
    KcpTransport.cs              每连接传输状态、native handle 和 socket
    KcpWorker.cs                 单 owner 线程、轮转、唤醒、命令交接

Native/Kcp/                     Unity/.NET 共用原生源码与构建入口
    CMakeLists.txt
    include/gfnet.h              唯一公开原生头文件
    src/gfnet_connection.c       单连接 DTLS/KCP 状态机
    src/gfnet_listener.c         cookie、凭据、服务端连接所有权
    src/gfnet_memory.c           分配头、配额、清零、统计
    third_party/kcp/             唯一 KCP c/h 与 LICENSE
    third_party/mbedtls.lock.json
    config/mbedtls_config.h
    ABI.md
    build/prepare_kcp.py         已存在：hash 校验及源码复制
    build/build.py              统一 CMake 入口
    build/install.py            平台产物与 Unity importer 元数据
    tests/                      保留已有回归，补 ABI/DTLS/listener 测试

DotNet/GameFrame.Server/
    KcpListenerHost.cs           UDP listener、native owner、强类型应用接口
```

KCP 唯一源码位于 `Native/Kcp/third_party/kcp`，迁移保留源码字节和原 GUID 记录；目前已直接合入 #467、分配失败处理与标准 `offsetof` 修正。构建引用经过 `prepare_kcp.py` 校验的逐字节副本，不再次打补丁。

`ConnectionContext` 现通过 IFrameTransport 接入两种传输，仅抽出连接、整帧收发、关闭和能力查询所需的内部传输边界，保留现有 pending/deadline、队列、Hello 和主线程派发。WSS 继续使用现有 IO owner，KCP 使用宿主单 worker；不复制整套 RPC 栈，也不让每个 KCP 连接再启动独立原生驱动线程。

Unity/.NET 共用 KcpNative、NetworkClock、帧/codec/预算源码；服务端的 GameFrame.Network.Core 只链接这些基础实现，不编入 KcpWorker、KcpTransport 或客户端 ConnectionContext/options。客户端代码仅由 .NET 客户端集成测试显式链接。Protocol/GameFrame.Protocol.projitems 只包含生成 DTO/catalog。Unity 的宿主/Pump 保留在 Runtime，服务端 GameFrame.Server 拥有自己的 listener 和应用 owner 生命周期。

复用客户端 ConnectionContext 不意味着让服务端使用客户端状态机；服务端保留自身收发方向、阶段权限与业务 owner，共用 native ABI、帧协议、生成 codec 及适用的纯 C# 辅助代码。

接入前修复两个已记录问题：远端有序 Close 不能屏蔽随后发现的解码/Push 故障；`CloseAsync` 必须先建立唯一关闭任务，再发布允许重入的 Closing 通知，旧 generation 的收尾不能清除新 context。补充可复现的顺序/重入测试，避免新传输继承同一问题。

## 3. C ABI

库名 `gameframe_kcp`，公开符号仅 `gfnet_*`。C 实现采用 C11，并为编译器差异提供最小导出/TLS 宏；不导出 KCP 或 mbedTLS 内部符号。

沿用 NetworkDesign 第 12.3 节的单连接函数：

| 操作 | 语义 |
| --- | --- |
| `gfnet_abi_version` | 加载时确认 ABI 主版本 |
| `gfnet_create` | 校验配置，同步复制 identity/PSK，失败时输出 handle 为 NULL |
| `gfnet_feed_udp` | 同步复制一个有界 UDP 包；不保存托管指针 |
| `gfnet_tick` | 推进 DTLS/输入和到期的 KCP update；提前调用不额外 flush |
| `gfnet_send_reliable/unreliable` | 只在传输 Established 时接受完整帧，接受成功返回 0 |
| `gfnet_poll_udp` | 返回并移除一个完整密文包；无包返回 0 |
| `gfnet_peek_frame/recv_frame` | 查看下一完整帧的通道/长度/原始 born，再复制并移除；ABI2 按16可靠/4不可靠消费轮转 |
| `gfnet_get_state/get_stats` | 读取状态与固定大小指标快照 |
| `gfnet_destroy` | 最后一次调用退出后，由 owner 销毁 |

服务端另提供 listener create/destroy、register/revoke grant、confirm/reject Hello binding、feed/poll UDP、tick、poll connection event、release connection。listener tick 统一推进其子连接；服务端子 handle 不再调用单连接 tick/feed/poll UDP，避免同一连接被重复驱动。子 handle 只用于应用帧 send/peek/recv 和状态/指标查询。注册、绑定、撤销和 release 同样由 listener owner 串行执行。

listener poll UDP 的元数据包含 peer、connectionId/generation 和原始入队时间，便于托管 in-flight 检查连接有效性及总队龄；未分配连接的 cookie 输出使用明确的无连接标识并受 listener 队龄/额度约束。不能仅凭相同 peer 判断包属于当前连接。

在实施第一步冻结 `Native/Kcp/ABI.md`，至少包括：

- Cdecl，`int32_t/uint32_t/uint64_t` 与 opaque handle；无 C `long`、C bool、变长结构、内部结构 Marshal 或隐式字符串编码。
- config/stats 首字段 `struct_size`、`abi_version`；明确全部字段偏移、对齐、保留字段必须为零及结构大小。输入缓冲指针只在同步调用内有效；事件可携带 listener 拥有的 opaque connection handle，但不携带借用的 payload/字符串指针。C 静态断言及 C# `Marshal.SizeOf/OffsetOf` 验证目标架构。
- peer 使用 addressFamily、16-byte address、u16 port 和明确的保留位；端口为主机序，IPv4 规范化填充，初版拒绝需 scope-id 的 IPv6 link-local 地址，不暴露 sockaddr。网络字节与 ABI 内存布局分别定义。
- 公开配置限定为已验证参数，不允许任意增大窗口/MTU绕过队列推导。新增不可兼容字段/行为提升 ABI 主版本，不静默兼容。
- 错误码已固定为：`-1 InvalidArgument`、`-2 BufferTooSmall`、`-3 Backpressure`、`-4 ProtocolError`、`-5 AllocationFailed`、`-6 InvalidState`、`-7 InternalError`；实现前与每个函数的成功返回表一起冻结。不要直接透传 ikcp/mbedTLS 错误整数。
- BufferTooSmall 不弹出队首；Backpressure 只表示尚未接受、未修改发送状态。可靠数据移交后的短写/分配失败，以及 KCP output 失败均锁定 Fatal，不能返回可重试背压。
- feed 原始 UDP 输入满额时返回 Backpressure，owner 丢弃新 datagram 并计数；可靠恢复交给 KCP，自身不重试 feed。该丢包边界与已被 KCP 视为发出的 output 丢失区别处理。
- Fatal 保留首个原因与 native 子原因用于诊断；随后禁止业务/input/update，仍允许读取状态/统计及 destroy/release。非法参数不允许造成越界。

ABI 还须逐项锁定 clock 的初始化/更新时间、每次 tick 的输入/输出预算、状态可调用函数矩阵和错误的影响范围。destroy 仅接受有效独占 handle 一次；托管关闭幂等由 owner 清空 handle/唯一收尾任务实现，不承诺已释放裸指针再次 destroy 安全。listener 自身错误影响整个 listener，子连接协议/配额/内部错误仅关闭该子连接。feed 的 InternalError 也可能来自临时握手 create/tick，不能按错误类别扩大到共享 socket；listener tick/poll 检查自身失败，已有 child 的终态通过事件处理。

单连接 native 状态为 `DtlsHandshaking → Established → PeerClosed/Fatal`。此处 Established 只代表 DTLS 完成；C# `ConnectAsync` 还须完成 GFNP Hello/hash/授权绑定，才发布 Connected。没有解密成功的业务数据不能进入应用队列。

Hello 之前可收发建立协议所需的可靠控制帧；服务端只有 confirm binding 成功后才能发送 ServerHello 并开放已授权的应用路由。AwaitingHello 仍计连接槽/内存，执行总 Connect、Hello 和 grant 剩余期限，不能在 DTLS 完成时重置。绑定前的业务/不可靠数据由网关在正文解码前拒绝。native 不解析 GFNP 或 MemoryPack，服务端 owner 的帧状态机负责阶段校验。

`ikcp_send` 成功返回接受的字节数；包装层必须核对等于输入长度才返回 0。所有输入/输出长度先验证符号、上限及加法溢出。过小接收缓冲不消费帧，原生返回值不被托管误当作有效长度。

当前 ABI 为2，C/C# 校验全部公开结构字段 offset/size；peek_frame 增加 enqueued_ms 输出，旧库明确拒绝。正常 Closed 停止推进/发送，排空已有完整可靠帧后 release；托管 handler/队列另由其 owner 排空。

## 4. 工作线程、时钟与 IO

客户端一个宿主 worker 管理所有 KCP 连接。Connect/send/close、取消和 deadline 通知经有界命令/发送队列交接；关闭请求占用预留终态槽，不能因普通队列满丢失。主线程不执行 native，不持锁等待 socket 或 native 退出。

每轮按连接轮转：先处理关闭/期限和已有 UDP output，再读取最多 64 个 UDP input，推进 DTLS/KCP，排出完整应用帧，最后发送最多 256 个 output。socket 不可写时保留一个托管 in-flight datagram，下轮先处理它，停止追加业务发送；该缓冲仍计连接/宿主预算。SendTo 必须发送完整 datagram，WouldBlock 以外的失败按 socket 所有者提交故障：客户端故障本连接，服务端共享 socket 故障整个 listener。陌生来源、已关闭/错误 generation 的连接数据不得继续投递。

应用发送仍受总业务/不可靠令牌桶、waitsnd 和队龄限制（NetworkDesign 第 10 节）。原始 UDP 队列属于 native 输入阶段，每连接默认 256 KiB/256 包；C# 仅保留有预算的接收暂存，不再增加同样大小的隐式队列。原生可靠完整帧直接在 KCP 接收队列 peek/recv，不额外复制为第二套可靠帧队列；不可靠明文使用有界队列，满额淘汰最旧并计数。转入托管可靠队列时满额故障连接，不可靠队列按既定丢弃语义处理。

listener 在按 peer 分发前先限制共享 socket 每轮读取数量/CPU 时间，分发后再执行每连接输入额度与到期 tick，不能为了找合法包无限读取陌生来源。local 已采用每轮最多256包/2ms；来源按IP每秒最多2048包/2MiB，有界表最多1024个IP，每秒清空，表满拒绝新来源，端口变化不重置限额。生产数值待容量验证，不把客户端每连接64包视为服务端全部防过载策略。

网络时钟继续用 `NetworkClock` 的单调毫秒，托管保留 64 位；native KCP tick 使用低 32 位，定时比较使用环绕安全差值，短周期期限均小于半个 uint32 周期。授权的长有效期在托管服务按 64 位期限管理，进入 native 的剩余期限需校验范围。不得用 UTC 或 Unity timeScale 推进传输。

native 是每连接 KCP 固定 10ms update 的唯一调度者，C# 负责按时唤醒并调用 tick。发送/关闭命令可以提前唤醒；当前取消与托管 deadline 在下一 owner 轮检查，但 native 只处理当前可推进的状态，不重置计划 KCP tick，也不提前调用额外 flush。晚到只按当前时间推进一次，不补跑遗漏 tick。每次实际 flush 之间最多处理 64 个 KCP datagram；无 flush 的 update 不恢复该额度。服务端采用一个 listener owner 起步，之后按实测连接容量分片。

唤醒使用明确的线程信号和有界等待；空闲连接不持续忙轮询。DTLS 的定时器回调读取 owner 当前 tick；握手重传由 mbedTLS 的状态机实现，握手总期限仍受 Connect deadline/grant deadline 约束。

create 完成后先以当前单调时间执行首次 tick，再开始输入/发送；DTLS 成功后创建 KCP 时也以当前时间初始化其计划 tick。不能把进程运行时长当成握手已经等待的时间。

当前初版有活跃连接时，线程事件等待最多 10ms，发送/关闭命令提前结束等待；UDP 和托管 deadline 在下一轮用当前单调时间检查，已过期工作不得因轮询延迟再次准入。等待时间与最近 deadline 取较小值的精细调度尚未实现，若实测证明 10ms 等待影响期限或延迟目标，再增加该调度。DTLS 定时器最晚在下一驱动点检查，ABI 第一版无需新增通用调度接口，也不为每包创建唤醒任务。没有活跃连接时等命令信号。挂起恢复先用 64 位时间检查期限；超过 uint32 有符号差值的有效范围时关闭旧连接，不能截断后把超长期停顿误认为正常 tick。轮询增加的本地接收等待纳入延迟报告，后续就绪唤醒优化以实测为依据。

## 5. DTLS 与 KCP 的内部交接

可靠路径是 `GFNP → KCP datagram → channel=1 → DTLS → UDP`；不可靠路径是 `GFNP → channel=2 → DTLS → UDP`。channel 在加密明文内部。KCP message mode，一个可靠通道；不可靠不改为可靠，GFNP Hello/Ping/Close 走可靠通道。

KCP output 回调在 C 内同步复制 datagram 到有界明文待加密队列，回调结束后才能使用该副本。DTLS 在后续明确阶段处理该队列，避免在 KCP/DTLS 接收栈内递归驱动同一个 SSL 上下文。明文队列本身计入 native 4 MiB 总额，不能只统计最终密文。

mbedTLS 的 WANT_READ/WANT_WRITE 是状态机等待条件：保留当前待写明文的相同地址/长度，按库约定续调，完成之前不能换成下一条，也不能再次 ikcp_send。DTLS BIO 的发送回调只在完整密文已复制到输出队列后报告成功；输入 BIO 从有界原始 UDP 队列读取。单个 SSL 上下文只由 owner 调用，不同 read/write 状态转换按锁定库版本的契约测试。

KCP 不使用 output 返回值可靠地撤销已产生的数据，因此任何输出分配失败或队列满必须记录 fatal latch。当前 native 调用返回后立刻停止该实例，不再向 socket 提交残留业务输出，然后释放；不能假装成功并丢掉可靠密文。

接收先 DTLS 验证/防重放，再检查 channel 与解密长度；可靠交给 KCP，完整消息经 peeksize 检查后复制给 C#，再走同一 FrameCodec/正文验证器。未经认证的坏 UDP 丢弃并计数；已认证的未知 channel、越界帧或非法 KCP 内容终止连接。mbedTLS 错误必须按所锁版本逐项区分可忽略坏记录、协议终止与内部错误，不把全部负值作为可继续运行的状态。

不能仅凭 `ikcp_input == 0` 判定 datagram 合法：调用前以固定 24-byte 头遍历并检查 conv、command、segment 长度、PUSH 分片上限及恰好耗尽正文；原版可能忽略不足一个头的尾随字节。只做当前 KCP wire 的边界检查，不重写 ACK/重传算法。完整消息 peeksize < 0 表示尚未组装完成，只有已确定越界/非法时才故障；畸形分片不得无限占用接收窗口。

传输参数以 NetworkDesign 第 12.2 节、额度/队龄以第 10 节为唯一默认值来源。关键边界为 UDP payload 1200、KCP MTU 1150、可靠 GFNP 32768、不可靠 GFNP 1150 bytes（GFNP 上限均含 32-byte 头）；native 密文 output 512 KiB/256 包、最长本地滞留 250ms。

上述最大 KCP 数据包密文为 `1150+1+37=1188` bytes；握手同样配置 MTU=1200，由库分片。最终 BIO output 再检查 1200 上限。窗口、整窗重传、最大 ACK 和 DTLS 中间副本均纳入压力测试，不用单包大小替代 flush 峰值。

## 6. 分配、所有权、关闭与诊断

| 额度 | 初版上限 |
| --- | --- |
| 每连接 native | 4 MiB，含分配头、KCP/DTLS、明文/密文队列和节点 |
| 每客户端托管＋native | 8 MiB |
| Unity 网络宿主 | 64 MiB，最多 4 个客户端 |
| 服务端 local listener | 64 established、16 handshake、256 grant、native 全局 256 MiB |

全局额度和单连接额度同时预留，扩容先计旧＋新峰值。`ikcp_allocator` 及 mbedTLS 分配钩子只初始化一次；每次 native 调用建立线程局部 owner，分配头存 owner/实际容量，free 按头归还。quota owner 必须活到最后一块内存释放，不把已释放 connection 指针作为残留分配的 owner。共享 cookie、凭据索引计 listener/宿主额度。PSA 全局初始化只执行一次；1024 个密钥槽元数据为固定静态存储，不计入动态 owned_bytes，其容量耗尽显式报 AllocationFailed。每个密钥材料和操作/复制缓冲仍按当前 native 调用的 connection/listener quota 分配、释放，PSA 全局状态不能持有已销毁 quota。

客户端在 create 前从 8 MiB 连接/64 MiB 宿主额度预留完整 4 MiB native allowance，托管分配只能使用剩余额度，destroy 完成后才归还该 allowance。C 分配器在 allowance 内逐项检查实际容量；这样无需反向调用 C# 也能保证跨层总上限，不依赖滞后的统计采样做 admission。诊断分别报告预留额度和实际使用，不将 4 MiB 预留当作已经分配，也不在额度中重复扣一次 native 实际值。服务端 native 全局 allocator 则直接同时检查 listener 和子连接实际额度。

mbedTLS 以独立静态依赖编入该插件，内部符号隐藏，避免污染其他原生插件的 allocator。PSA 的 external RNG 回调直接使用系统密码级随机源，无共享堆上 DRBG：Windows BCryptGenRandom，Apple CommonCrypto CCRandomGenerateBytes，Android API23 /dev/urandom，Linux getrandom。失败清零输出并终止创建/握手，不重试初始化或切换随机源。PSA 默认复制缓冲保留，不启用 EXCLUSIVE_BUFFERS，因为 TLS AEAD 使用原地输入/输出。PSK/敏感缓冲释放前使用不能被优化删除的 zeroize。

原生 feed 前后的托管复制与 native 源副本同时存在时分别记账，转移不是提前归还。原生 poll 后 socket WouldBlock 的托管副本仍属 in-flight。指标区分 native 活跃容量、托管容量、峰值和平台 socket 缓冲，不宣称预算等于进程 RSS。

主动关闭/故障沿用 NetworkDesign 第 6.2 节；普通远端关闭仍需先按可靠顺序处理关闭标记之前已验证的帧。客户端关闭独占 socket、解除 IO 和 worker 引用后，由 owner 排空缓冲并销毁 native。服务端关闭单个连接只移除 peer 分发资格、清理该连接待发包并 release 子 handle，保留共享 UDP socket；只有 listener host 整体关闭才关闭共享 socket并统一销毁全部子连接。托管 in-flight 包带 connectionId/generation，连接关闭后不得发送给复用槽位的新连接。

系统 Close 最多尽力发送 2 秒；Fatal 时不再发送残留业务数据。destroy 不负责无限等待网络，不由 finalizer 任意线程执行，不在进程退出增加等待拦截。listener destroy 只能由停止调用的 owner 执行，所有借用 handle 随之失效，不能随后继续 release。

native 统计提供分配当前/峰值、输入/输出包和字节、坏记录/重放丢弃、队列容量/年龄、KCP srtt/rto/waitsnd/窗口/重传/待发与在途字节。没有现成累计计数的项在已锁定 KCP 源码的对应路径加最小统计钩子并更新 hash，不根据 rto 猜测重传次数。C# 汇总业务有效字节、Ping RTT、RPC 阶段耗时与 worker 轮转耗时；主线程读取快照，不直接查询 native handle。

库不暴露的 DTLS 细分丢弃原因不能凭返回 0 推断；先核对锁定版本是否可观测，无法获取时标记该指标 unavailable 并在验收记录中列出差距。统计字段附能力标记，禁止用 0 伪装已测量且没有丢弃。

完整托管计账需审计现有 pending、取消注册、completion、队列节点与常驻目录；目前每 pending 512 字节 reservation 仍是待核验估算。不能因为原生分配清零就宣布完整 8 MiB/64 MiB 契约验收。

## 7. 服务端授权接口与业务范围

mbedTLS 已锁定为 4.2.0 stable，包含 TF-PSA-Crypto 1.2.0（上游 LTS 为 4.1，4.2 不标为 LTS），DTLS 1.2、`TLS_PSK_WITH_AES_128_GCM_SHA256`、cookie、防重放；禁用 CBC、匿名、会话恢复和 CID。版本/commit/archive SHA 在 third_party/mbedtls.lock.json，准备阶段核验 archive 与源码树；发布前继续审查安全公告，不以版本锁定代替安全维护。

应用负责认证与权限，框架提供 register/revoke、连接授权绑定和终态通知。凭据记录包含 grantId、identity、PSK、conv、ticket 摘要及单调到期时间；账号 epoch/目标世界合法性留在 .NET 应用授权入口，Runtime 不认识账号或角色。

listener 在未验证来源阶段只使用有额度的临时握手完成 cookie 验证，发出 HelloVerifyRequest 后释放，不为随机来源保存永久表项。验证后才占握手槽；PSK lookup 在 C 内查有界已注册凭据，不回调 C#。来源限流和握手调用/CPU 预算同样有界，不为每个陌生 IP 新建无界统计记录。

cookie 的 transport identity 使用规范编码的地址族/地址/端口，不直接 hash 带填充的 peer 结构体；注册/查找也使用相同规范 peer 表示。拒绝 NAT 地址迁移到旧会话，按既定策略显式重新授权建连。

DTLS Established 发布 grantId 与借用 handle，随后 GFNP Hello 校验 ticket/hash/conv 与同一授权，应用在 owner 上调用 confirm binding，原子绑定一次性 grant；校验失败调用 reject 并关闭。不接受客户端声明的任意 grantId 替换已验证 PSK 对应记录。不得在 PSK lookup 时直接消费 grant，否则一次握手内重传可能被误拒绝；也不得仅凭 PSK 成功开放全部应用路由。事件带 connectionId/generation，Closed 保存在每连接终态槽，应用确认并 release 后才能复用槽位；借用 handle 在 release 之后立即失效。

Established/Closed 使用连接槽中的有界待投递标记，避免普通事件队列满导致连接没有接管者。若同一连接在应用接管前已经 Closed，只交付终态及释放所需标识，不再交付可用 Established。绑定确认的期限/撤销/已消费检查在同一 owner 调用内完成；两个已通过 PSK 的握手竞争同一个 grant 时最多一个成功，失败连接立即终止，不回滚授权。

大厅必须得到 register 确认后才返回授权。撤销先停止新准入并终止相关连接；注册与撤销命令的账号 epoch 仲裁由服务应用串行提交，迟到注册不能复活旧 epoch。授权过期限制新建连，已绑定连接的存活由账号有效期/撤销策略决定，不把短时握手 ticket TTL 当成整场连接寿命。

底层阶段先用 native/C# 测试宿主注入受控凭据，覆盖 Echo、两个 channel、期限与撤销。需要实联时显式启用 `local-kcp-dtls`；local 应用策略可以通过既有授权路由接入专用测试目标，但不实现角色、入场或世界模拟。生产没有认证/授权 provider 仍拒绝启动；现有 `RequestWorldGrant` 在未接网关时仍返回 WorldUnavailable。业务接入时再配置真实目标校验。

## 8. 构建与 Unity/.NET 安装

一个 CMake 工程编译包装层、已核验 KCP 和锁定 mbedTLS。build.py 提供 `--platform`、`--arch`、`--configuration`、`--dry-run`，显式配置工具链与输出路径；install.py 接收构建目录并从清单选择平台，另支持 `--server-output`；构建目录在 Unity 外。首次构建、修改 C/依赖/ABI 或增加架构时运行，普通 Hotfix/C# 修改无需重编 native。

| 平台 | 产物 | 构建与安装要点 |
| --- | --- | --- |
| macOS | `libgameframe_kcp.dylib`，arm64/x86_64 或 universal | Apple Clang；确认导出、架构、签名、rpath，Editor/Player 分别测试 |
| Windows | `gameframe_kcp.dll`，x86_64 | 与 Sqlite 一样可在 macOS 用显式 LLVM-MinGW UCRT 工具链交叉构建；Windows MSVC CI 负责原生/UDP 执行验证；检查 PE、全部 ABI 导出及系统 DLL 依赖 |
| Android | `libgameframe_kcp.so`，arm64-v8a | 显式 NDK/CMake toolchain、最低 API、正确 Unity importer 架构 |
| iOS | `libgameframe_kcp.a`，device arm64 | Xcode device SDK、P/Invoke `__Internal`；实际验证最终链接与裁剪 |
| iOS Simulator | 独立 simulator 静态库 | 分开构建/安装，不与 device 同时错误链接；验证 Unity 对当前版本的选择方式 |
| .NET 服务端 | 对应部署 RID 的 dll/dylib/so | 同一 ABI，发布到服务目录；Linux 部署时新增 Linux 工具链与验证 |

Unity 安装到 `Unity/Assets/Plugins/GameFrame/Native/Kcp/<platform>`，生成正确平台/CPU importer 设置。保留已有 `.meta` GUID，不启用所有平台通用导入。第一次显式建立 KCP 时加载并验证 ABI；WSS-only 初始化无需插件。缺库/错架构/ABI 不符明确失败，不回退 WSS。WebGL 在进入 P/Invoke 和线程/socket 路径前拒绝。

脚本输出 artifact.json/build-manifest.json，记录 KCP 基线/当前 hash、本地修正、mbedTLS 版本/SHA、ABI、编译器/SDK/NDK、架构、配置、编译选项、二进制 hash 与相关验证；附 KCP/mbedTLS/TF-PSA-Crypto 许可证。不要复制 Sqlite 的库名、预加载策略或 ABI。替换已加载库后重启 Unity，sanitizer 产物只用于 native 验证。

构建前检查对应工具链版本与路径，缺失就报告具体依赖；实施前的机器环境快照放在实施记录，不作为设计契约。依赖准备是独立显式步骤，普通构建不自动访问浮动分支或升级工具链；lock 至少记录源码 URL、版本/commit、archive SHA-256 与 mbedTLS 配置 hash，hash 不符拒绝构建。插件升级需新安装包，Hotfix 无法替换 iOS 原生代码。

## 9. 实施顺序与退出条件

| 步骤 | 交付 | 必须通过后才能继续 |
| --- | --- | --- |
| 1：共用核心与依赖冻结 | 两个关闭问题修复；mbedTLS 补丁/SHA、gfnet.h/ABI.md、CMake 和 macOS 构建入口 | 既有 WSS 回归、故障/重入新用例；size/offset/导出/错误码测试 |
| 2：C 单连接闭环 | quota allocator、队列、DTLS/KCP、两个 channel | 不经 socket 的双端 datagram harness；错误 PSK/篡改/重放、MTU、逐点分配失败、全部释放；ASan/UBSan |
| 3：C listener | cookie、grant 注册/绑定/撤销、事件/release | 陌生来源不留永久状态、额度满/事件满、绑定前准入、双握手竞争、过期/复用、终态不丢 |
| 4：C# 与 .NET 接入 | owner worker、UDP、现有帧/RPC 栈、local 联调 | Connect/Hello/Echo/双通道、主线程停顿、取消/期限/队列满、关闭时 IO 在途、单连接关闭不影响共享 socket 的其他连接、撤销竞态 |
| 5：Unity 桌面与安装 | build/install、macOS/Windows 库、importer、指标 | Mono 实联、Player 脚本编译、IL2CPP 原生链接实跑分别记录，既有 WSS 回归 |
| 6：移动与平台验收 | Android/iOS 与独立 simulator 产物 | 真机加载/双通道、IPv6、前后台、弱网、49.7 天回绕模拟、长时间资源/容量验证 |

每步保留可评审代码与实际结果，不把 C harness、.NET 编译、Unity Editor、Player 脚本编译、IL2CPP 打包或真机验证相互替代。接入阶段至少验证满窗 flush、最大 ACK、慢 socket、丢包/乱序、tick 延迟、应用队列龄、各层额度及关闭后的活跃分配归零。

当前没有必须让用户再次选择的常规架构分支：已采用 C KCP＋mbedTLS、C# socket、单 owner、固定 ABI 和现有协议核心。实施中若锁定依赖不支持既定套件/分配钩子，或平台工具链无法支持必要能力，应停止对应步骤说明实际阻塞，再讨论设计变更；不静默更换传输或安全参数。


## 本轮流程补齐（2026-10-12）

可靠 admission、三路发送 FIFO 和单次可失败移交遵循总设计第10节。client/server 每轮首尾输出共用256包预算；listener 在 socket in-flight WouldBlock 时暂停新业务追加，不停止必要收包/tick/deadline。服务端 handler 显式携带不可变 connectionId/generation/grantId，授权注册期限从托管调用 admission 起算。

普通 DTLS EOF 与 WSS EOF 都让现有业务 owner 继续消费已接受可靠输入；物理传输结束后不再发送回复。故障或显式停机取消并等待 handler，handler 必须协作取消。通用 Connected/Closed 与连接终态接口已提供，世界业务绑定/结束处理由未来的业务 owner 接入，不在底层创建第二套会话管理。

指标新增帧/业务字节、Ping RTT、预留峰值、native allocations/input/plaintext/output队龄与能力字段。完整元数据、分阶段/分位耗时、DTLS 内部细分丢弃仍未完成。审查清单与最新证据见 [NetworkReview.md](NetworkReview.md)。

服务端已拆为 Core/Server/Protocol/Game.Server，KcpListenerHost 使用与 WSS 相同的强类型 ServerApplication，业务只收发 DTO，不持有 native handle/OwnedFrame。默认容量通过 KcpServerOptions 固定，完整 API/owner/关闭契约见 [服务端框架说明](../DotNet/GameFrame.Server/README.md)，本轮证据见 [服务端审查第8节](NetworkServerReview.md)。
