# 网络模块使用

完整契约见 [NetworkDesign.md](NetworkDesign.md)，分阶段进度见 [NetworkImplementation.md](NetworkImplementation.md)，本地服务运行见 [DotNet/README.md](../DotNet/README.md)。当前实现桌面 `ClientWebSocket` 可靠通道；KCP/DTLS、WebGL 桥接及游戏世界会话仍待实现。

## 宿主和客户端

在 GameRuntimeConfig 设置 `NetworkOptions = new NetworkRuntimeOptions()`，随后 `await GameRuntime.InitAsync(config)`。null 表示关闭网络，不创建网络驱动或自动连服。网络使用既有持久根节点上的驱动，每帧按默认 2ms/128 条全局预算派发，各客户端每轮最多 16 条可靠工作。序列化、DTO 解码、订阅回调和业务 await 完成都在主线程。

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

发送 owner 在真正调用 WebSocket Send 前再次检查取消与 RPC deadline；即使后台 clock tick 尚未运行，已过期的排队请求也不再发送。心跳只允许一个未确认 nonce，Ping 发送完成起等待 Pong 最多 15 秒；控制包从入队起也受独立 15 秒发送期限约束，包含被慢业务发送阻塞和自身 Send 未返回的时间。客户端与服务端 watchdog 均保持独立运行，不通过更换 Ping 延长期限。

SendAsync 成功表示已编码并进入队列，不保证送达。调用返回后可以修改传入 DTO。队列不足抛 NetworkBackpressureException；未移交的可靠帧排队超过 250ms 时，RPC 以背压结束，普通 Send 已 admission 的消息则使连接故障；非零远端 StatusCode 抛 NetworkRemoteException，并保留未知错误码。WSS 的 TrySendUnreliable 抛能力不支持，不转为可靠发送。

每个 Push opcode 只允许一个处理器，Subscribe 返回的 IDisposable 注销自己，关闭再连接时订阅保留。处理器抛错会故障当前连接；状态/故障观察者抛错只停止该批通知并记录原异常。主线程通过 GetMetrics 查看实际帧缓冲、pending/堆大小、超时、取消、迟到响应及发送队列年龄告警。

运行中先停止业务，再 `await client.DisposeAsync()`，最后 `await GameRuntime.ShutdownAsync()`。CloseAsync 等待真实 IO 退出及缓冲释放，随后可重连；Dispose 注销订阅和宿主登记，不能复用。相同客户端的 Close/Dispose 幂等。故障后显式 await Close 才允许再次 Connect；新连接使用新 context/generation，旧工作不进入新连接。框架关闭前保持 Pump 驱动，不能先销毁持久根节点。

宿主 Pump 内部异常或网络 Runner/root 提前销毁、禁用时，宿主锁定首个故障、停止正常派发并拒绝新客户端/连接。所有客户端进入故障边界，已保留但未完成的响应也失败；清理只完成任务、释放帧与等待 IO，使用独立 PlayerLoop 路径，不依赖受损 Runner，也不执行 Push/DTO 解码。Close/Dispose/Shutdown 仍可等待完成；关闭某个客户端不能恢复整个宿主，须显式 Shutdown 后重新初始化。状态观察者收到原始异常，宿主边界记录一次；进程退出不启动等待流程。

当前采用精确容量数组，无空闲池；MaxIdlePoolBytes 因无缓存实际使用 0。发送、接收、控制分别有子预算，旧＋新扩容峰值及在途帧计入客户端/宿主预算，释放前清零整个容量。pending 按每条 512 字节固定 reservation 限制；该 reservation 仍需结合 Unity/IL2CPP 实际对象布局审计，不能将当前指标当作完整托管元数据或进程 RSS 验收。

ClientWebSocket 不开放底层 TCP socket buffer 设置与读取；当前没有伪造收/发 256 KiB 的设置成功值。该平台内部缓冲、证书/代理/IPv6/后台切换行为仍需平台验收。
