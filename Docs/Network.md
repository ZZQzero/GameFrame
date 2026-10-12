# 网络模块使用

完整契约见 [NetworkDesign.md](NetworkDesign.md)，分阶段进度见 [NetworkImplementation.md](NetworkImplementation.md)，本地服务运行见 [DotNet/README.md](../DotNet/README.md)。当前实现 WSS 可靠通道与 C KCP/DTLS 的可靠/不可靠双通道；游戏世界会话业务延后。用户确认第一版不实现 WebGL WSS，当前 WebGL 建连明确报平台不支持。

C 原生实现的职责、ABI、工作线程和平台构建安装方案见 [NetworkNativeDesign.md](NetworkNativeDesign.md)。原生使用/构建入口见 [原生说明](../Native/Kcp/README.md)，精确 ABI 见同目录 ABI.md。

## 宿主和客户端

当前开发范围为框架底层；游戏世界会话与角色/AOI/移动业务延后。生成的世界 DTO/路由保留为后续接入契约，不表示已实现世界服务，也不要求框架宿主持有游戏业务状态。

在 GameRuntimeConfig 设置 `NetworkOptions = new NetworkRuntimeOptions()`，随后 `await GameRuntime.InitAsync(config)`。null 表示关闭网络，不创建网络驱动或自动连服。网络使用既有持久根节点上的驱动，每帧按默认 2ms/128 条全局预算派发，各客户端每轮最多 16 个工作单元，Context 在持续消费中按16可靠/最多4不可靠轮转。序列化、DTO 解码、订阅回调和业务 await 完成都在主线程。

以下代码在已有异步业务方法内调用；Hotfix 使用生成的 GameNetRoutes/GameNetProtocol，不自行拼 opcode：

```csharp
var endpoint = new Uri("wss://localhost:7443/network");
var client = GameRuntime.Network.CreateClient(
    new NetworkClientOptions { TrustedWebSocketEndpoints = new[] { endpoint } },
    GameNetProtocol.Catalog);
await client.ConnectAsync(NetworkConnectOptions.WebSocket(endpoint), cancellationToken);
var response = await client.RequestAsync(GameNetRoutes.Login,
    new LoginRequest { Account = account, Password = password },
    TimeSpan.FromSeconds(10), cancellationToken);
// 业务检查 ResultCode 和成功载荷后，才提交已认证状态。
```

账号与密码由调用者提供，只在 local profile 接受；不要写入正式资源或日志。可信端点来自环境配置。正式 WSS 证书使用平台正常校验；Editor/Development 中显式 `allowDevelopmentWebSocket: true` 才允许 ws。现有本地服务只开放 HTTPS/WSS。

Connect 成功只代表传输与协议 Hello 完成，不代表已登录。Login 成功需检查 ResultCode、非零 accountId/authEpoch、16 字节 accountSessionId、32 字节 resumeToken 和有效 TTL；这些业务校验尚未封装为 GameNetworkSession。调用者保存该 RPC admission 的 NetworkClock 时间，deadline=admission+返回 TTL。Login 超时/取消需关闭该次 WSS；恢复失败不自动密码登录或重试。

## 请求、订阅与关闭

RequestAsync 的 timeout 从 admission 起计时；后台共享 deadline 堆负责超时，主线程卡帧不会延长有效期。响应在期限内到达后占有 pending，直到主线程校验和解码完成；此期间调用取消仍可获胜。超时/取消的迟到响应在头和长度校验后丢弃，不解码 DTO，不保留过期 ID 集合。所有 completion 仍在主线程交付。

发送 owner 在真正调用 WebSocket Send 前再次检查取消与 RPC deadline；即使后台 clock tick 尚未运行，已过期的排队请求也不再发送。心跳只允许一个未确认 nonce，Ping 发送完成起等待 Pong 最多 15 秒；控制包从入队起也受独立 15 秒发送期限约束，包含被慢业务发送阻塞和自身 Send 未返回的时间。匹配的 Pong 在 deadline 时刻或之后收到同样超时，不清除等待。客户端与服务端 watchdog 均保持独立运行，不通过更换 Ping 延长期限。

SendAsync 成功表示已编码并进入队列，不保证送达。调用返回后可以修改传入 DTO。队列不足抛 NetworkBackpressureException；未移交的可靠帧排队超过 250ms 时，RPC 以背压结束，普通 Send 已 admission 的消息则使连接故障；非零远端 StatusCode 抛 NetworkRemoteException，并保留未知错误码。WSS 的 TrySendUnreliable 抛能力不支持，不转为可靠发送。

每个 Push opcode 只允许一个处理器，Subscribe 返回的 IDisposable 注销自己，关闭再连接时订阅保留。处理器抛错会故障当前连接；状态/故障观察者抛错只停止该批通知并记录原异常。主线程通过 GetMetrics 查看实际帧缓冲、pending/堆大小、超时、取消、迟到响应及发送队列年龄告警。

运行中先停止业务，再 `await client.DisposeAsync()`，最后 `await GameRuntime.ShutdownAsync()`。CloseAsync 等待真实 IO 退出及缓冲释放，随后可重连；Dispose 注销订阅和宿主登记，不能复用。相同客户端的 Close/Dispose 幂等。传输释放异常不会跳过 IO 收尾、队列释放或 Dispose 注销；释放失败由任务传播，重复 Dispose 返回同一结果，不重试已摘除的资源。Shutdown 在发布共享任务后通知客户端，允许状态观察者重入；客户端收尾失败仍尝试停止 KCP worker。故障后显式 await Close 才允许再次 Connect；新连接使用新 context/generation，旧工作不进入新连接。框架关闭前保持 Pump 驱动，不能先销毁持久根节点。

宿主 Pump/KCP worker 内部异常或网络 Runner/root 提前销毁、禁用时，宿主锁定首个故障、停止正常派发并拒绝新客户端/连接。所有客户端进入故障边界，已保留但未完成的响应也失败；清理只完成任务、释放帧与等待 IO，使用独立 PlayerLoop 路径，不依赖受损 Runner，也不执行 Push/DTO 解码。Close/Dispose/Shutdown 仍可等待完成；关闭某个客户端不能恢复整个宿主，须显式 Shutdown 后重新初始化。状态观察者收到原始异常，宿主边界记录一次；进程退出不启动等待流程。

当前采用精确容量数组，无空闲池；MaxIdlePoolBytes 因无缓存实际使用 0。发送、接收、控制分别有子预算，旧＋新扩容峰值及在途帧计入客户端/宿主预算，释放前清零整个容量。每帧额外 512 字节保守预留 lease/队列/传输交接记录，pending 另按每条 512 字节 reservation 限制；该 reservation 仍需结合 Unity/IL2CPP 实际对象布局审计，不能将当前指标当作完整托管元数据或进程 RSS 验收。

ClientWebSocket 不开放底层 TCP socket buffer 设置与读取；当前没有伪造收/发 256 KiB 的设置成功值。该平台内部缓冲、证书/代理/IPv6/后台切换行为仍需平台验收。


## KCP / DTLS 调用

从可信配置取得 UDP endpoint，先按原生说明构建安装插件。调用者持有 ticket/identity/PSK，直到 Connect 完成不修改，后由会话 owner 清零：

```csharp
var client = GameRuntime.Network.CreateClient(
    new NetworkClientOptions { TrustedUdpEndpoints = new[] { endpoint } },
    GameNetProtocol.Catalog);
await client.ConnectAsync(NetworkConnectOptions.Kcp(endpoint, ticket, conv, identity, psk,
    grantDeadlineMilliseconds), cancellationToken);
using var subscription = client.Subscribe(GameNetRoutes.WorldSnapshot, OnSnapshot);
bool admitted = client.TrySendUnreliable(GameNetRoutes.MoveInput, input);
```

这里的世界路由只展示 API；当前没有世界业务 owner。账号授权由未来大厅/网关服务提供；local-kcp-dtls 仅以显式短时凭据验证 Echo，不实现账号登录或世界入场。授权注册、单次 Hello 绑定、竞争拒绝与撤销底层接口已实现。

KCP 后台 worker 与主线程 Pump 分工：worker 负责 UDP/native/期限，主线程完成 DTO、订阅与 await；关闭等待 owner destroy 后归还 native allowance。每客户端在 8MiB 总额内预留 NativeBytes（默认4MiB）；GetMetrics().Kcp 分开展示预留、实际/峰值、KCP窗口/RTT/RTO、待发/在途、明文/密文队列及实际 socket 发送字节。TimeoutRetransmits 仅超时重传，DTLS 细分坏记录计数不可用。OS socket buffer 请求各256KiB，并报告系统返回值。


KCP 使用 ABI2，原生库与 C# 必须同时更新。可靠 admission 在分配 RPC ID/pending 前按 token/window 缺口估算等待；不足时入口抛背压，真实发送仍重查期限。控制/可靠/不可靠各自 FIFO；一次 native 背压不会让不可靠帧占住可靠发送。TrySendUnreliable 的入口桶限制 admission，传输桶限制实际移交，均按应用帧字节数计费。

正常 EOF 会保留此前接收可靠帧的派发顺序，服务端继续完成已接受输入；回复已无法发送时释放。显式关闭/故障会取消处理并等待真实 owner 退出。业务 handler 必须响应取消，框架不强杀仍持有缓冲的任务。

GetMetrics 新增 Sent/ReceivedApplicationBytes（Hello 完成后的 GFNP 帧）、Sent/ReceivedBusinessBodyBytes（业务正文）、PingRoundTripMilliseconds（最近匹配 Pong，未测时0）和 PeakReservedBytes（含 reservation/native allowance 的峰值）。这些值不代表网络 wire bytes 或进程 RSS；完整对象布局、RPC阶段/分位耗时仍需验收。Kcp.DtlsDiscardDetailsAvailable=false 明确表示库未开放细分坏记录指标。完整本轮审查记录见 [NetworkReview.md](NetworkReview.md)。

.NET 服务端已按 Core/Server/Game.Protocol/Game.Server 拆分；通用框架通过强类型 ServerApplication 与业务组合，不依赖账号或游戏路由。应用会话、主动 Push、关闭和 host 生命周期契约见 [服务端框架 API](../DotNet/GameFrame.Server/README.md)。
