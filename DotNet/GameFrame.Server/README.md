# 服务端框架接入

`GameFrame.Server` 是业务无关的 .NET 10 类库，只依赖 `GameFrame.Network.Core` 与 ASP.NET Core。协议目录通过构造参数传入；框架不引用生成的游戏 DTO、账号服务或游戏宿主。`Game.Server` 是一个本地应用示例，运行方式见 [宿主说明](../README.md)。

## 类型化路由

业务组合入口引用框架和自己的生成协议，在启动前注册全部入站路由。以下示例使用现有 Echo；注册其他 RPC/Notify 或显式 `Reject` 后才能构造 host：

```csharp
var application = new ServerApplication(GameNetProtocol.Catalog);
application.Rpc(GameNetRoutes.Echo, async (connection, request, cancellation) =>
{
    await Task.Delay((int)request.DelayMs, cancellation);
    return new EchoResponse { Text = request.Text };
});
```

`Rpc` 返回响应 DTO，框架使用当前请求的 opcode/correlation 编码并转交发送 owner。`Notify` 分别接受可靠 `NetSend<T>` 和不可靠 `NetUnreliable<T>`。Push 路由无需注册入站处理器。host 构造时验证漏注册、重复注册、路由归属，然后冻结 application；不会反射扫描 handler 或自动安装游戏路由。WSS 上使用不可靠发送会明确失败，不能自动改成可靠。

handler 不接收原始帧、预算或借用 native handle。codec 在解码前验证字节形状，编码前验证对象与长度。`Rpc` 可提供 `releaseResponse` 回调，在同步编码尝试之后释放或清理敏感响应内容；请求 DTO 的敏感字段和业务资源由 handler 在 finally 中清理。编码与清理同时失败时保留编码首因，并报告清理错误。

`ServerRequestException(statusCode, closeReason)` 表示显式系统拒绝：RPC 返回空正文非零 status，可选择响应后关闭。普通业务拒绝应返回生成响应中的业务 ResultCode。致命异常结束该连接；WSS 尽力返回系统错误，KCP 直接关闭故障 child，两者都通过连接终态报告首因。不要把异常转成成功响应或自动重试。

## 应用会话与 owner

`ServerSession` 是应用自定义的每连接状态，账号/角色/世界绑定由业务派生类保存。框架不内置 accountId 或 worldSession。连接身份 Id/Generation/GrantId/Transport 与 RemoteEndpoint 由框架提供，端点读取返回副本。

| hook / 能力 | 执行与约束 |
| --- | --- |
| session factory | WSS 在 Hello 前、KCP 在 Hello 绑定后创建；不在 host 配额锁或 native owner 内执行 |
| `ConnectedAsync` | GFNP Hello 成功后，由独立应用派发任务先执行初始化，完成后才派发业务；传输收发、心跳与带原因关闭同时运行，可以排队 Push；耗时等待必须使用 connection.Cancellation |
| `Admit(header)` | IO/native owner，在 DTO 解码前准入；可与 ConnectedAsync 并行，必须快速、线程安全，不依赖尚未完成的初始化，不等待数据库/网络 |
| `CheckDispatch(header)` | 同连接应用 owner，紧邻解码和 handler 前复查权限；防止排队期间身份失效 |
| `CheckDeadline(now)` | ConnectedAsync 成功完成后，由 watchdog/native owner 调用，快速、线程安全；返回关闭原因或 null；初始化期间仍检查传输心跳期限 |
| `ClosedAsync(context, result)` | handler 退出后调用一次；即使 Hello/Connected 未成功，只要 session 已创建也负责清理；不能再发送或提升流量 |
| `RequiresRestrictedTraffic` | 应用选择初始受限阶段；WSS 使用独立容量/正文/速率额度，KCP 本身需 grant 绑定，使用 KcpServerOptions 的连接额度 |
| `EnableFullTraffic()` | 应用提交权限后解除受限标识，WSS 归还受限配额；连接结束后明确失败 |

同连接初始化与 handler 串行；初始化期间业务帧只进入有界队列，不解码或执行 handler，初始化失败则终止连接并释放队列。不同连接及应用共享状态需要业务自己的同步。KCP 回调运行在托管应用任务，native owner 仅推进 socket/handle、检查头部、准入和有界队列。WSS 使用独立收/发/派发 owner，每连接只为应用派发创建一个任务，不为每条消息创建 Task.Run。后台业务计算需要在异步提交前后检查取消与权限，不能把收包时的检查当作永久授权。

## Push、关闭与错误

`await context.PushAsync(reliableRoute, dto)` 同步编码并向有界可靠队列准入；成功只表示框架接受，后续传输失败通过 Completion 报告，不表示对端已收到。不可靠 Push 使用 `TryPushUnreliable`，容量不足返回 false；不可靠协商缺失明确抛错。路由必须属于该连接 catalog。

`Disconnect()` 请求立即停止。`CloseAsync()` 请求停止并等待 owner、handler、Closed 和资源释放；终态有错误时抛出首因。`RequestClose(reason)` 是外部会话所有者使用的有原因关闭接口，reason 非零且首次请求生效，重复调用不改写原因；它立即停止新 Push/权限提升，由 WSS/KCP 的发送 owner 尽力发送 GFNP Close，关闭发送/排空最多2秒，随后取消并 join handler。关闭丢弃尚未派发的业务及未发送的业务回复；已经交给传输的字节无法撤回。`CloseAsync(reason)` 请求同一关闭并等待最终释放；对端不确认或传输失败时仍通过 Completion 报告原因与错误，不保证 Close 一定送达。`Completion` 返回 `ServerCloseInfo`，包含首个关闭原因和错误，对端正常 Close 的原因也会保留，供外部 owner 观察。RPC/Notify handler、Connected/Closed hook 不得 await 自己的 CloseAsync/Completion，否则等待自身退出；内部有原因关闭用 RequestClose，需要立即中止时用 Disconnect；响应后关闭用 `CloseAfterResponse(reason)`。后者属于当前 handler 的终止决定，不能作为独立后台定时关闭接口。

正常物理 EOF 停止发送、排空已接受可靠工作，再关闭并清理；故障/显式停机取消 handler 并 join。处理器必须响应取消，框架不会超时后丢弃仍拥有资源的任务。取消回调或 Closed 抛错不会跳过其余释放；首因与额外清理错误分别保留/记录。连接完成在缓冲释放之后发布，WSS 同时已归还 host 配额。单连接业务失败不关闭共享 listener。

## host 与容量

WSS 由应用配置 Kestrel HTTPS、证书和路由，将 `WssServer.HandleAsync` 映射到路径；框架只接受 HTTPS WebSocket，拒绝 query/Authorization 凭据和未允许的 Origin。禁用应用压缩，保留证书正常验证。

```csharp
builder.Services.AddSingleton(provider => new WssServer(application, new WssServerOptions(),
    provider.GetRequiredService<ILogger<WssServer>>()));
builder.Services.AddHostedService(provider => provider.GetRequiredService<WssServer>());
await using WebApplication app = builder.Build();
WssServer network = app.Services.GetRequiredService<WssServer>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.Zero });
app.MapGet("/network", network.HandleAsync);
await app.RunAsync();
```

应用组合入口传入标准 ILogger；省略 logger 的独立类库调用仍使用 NullLogger，不能依靠 Kestrel 的 HTTP 日志代替网络 handler/清理错误日志。DI 创建的 WssServer 由 host 释放，账号服务与数据库在 host 结束后释放。KCP 独立入口同样持有 LoggerFactory 直到 listener 完成释放。

WssServer 的 hosted lifecycle `StoppingAsync` 在 Kestrel 等所有 hosted StopAsync 之前停止准入并 join 网络；只按注册顺序或等 RunAsync 返回再清理都不足以保证该顺序。重复 Dispose 等待同一停机结果。应用取消回调/Closed 清理故障会使停机失败；已经关闭的普通协议/业务故障从各连接 Completion 观察。

KCP 使用 `await using var host = new KcpListenerHost(endpoint, application, options)`；等待 Started 后读取 LocalEndpoint，通过 RegisterGrantAsync/RevokeGrantAsync 接入授权网关。TTL 从 Register 调用准入起算，排队不续期；框架只绑定 grant，不解释账号/角色。显式 Dispose 停止准入、取消并 join 应用工作、销毁 native/socket，重复调用返回一致结果。原生安装与 ABI 见 [Native/Kcp](../../Native/Kcp/README.md)。

WssServerOptions/KcpServerOptions 在构造时验证并复制，固定连接数、队列数/字节数、速率和托管/native 额度。WSS 的 Kestrel 连接/TLS 额度由应用与网络选项配套配置。多个 KCP listener 共用 native 进程预算与 PSA key slots；这些选项不允许突破已验证 ABI 限制。OwnedBytes 表示框架可控托管帧当前占用，不是进程 RSS，也不包含所有 CLR/TLS/内核资源。统一的队列/native/耗时诊断快照与生产容量验收仍待完成。

ABI 2 的单 listener 硬上限为：MaxConnections 1～64、MaxHandshakes 1～16、MaxGrants 1～256、NativeBytes 64 KiB～256 MiB。越界在 host 构造阶段按字段抛 ArgumentOutOfRangeException，不启动 native owner；合法边界仍须满足其他队列与内存组合约束。这些是容量上限，不表示设置到上限就已通过生产负载验收。

独立框架验证入口：`dotnet test Tools/NetworkFramework.Tests`，仅引用 Server/Core，使用独立 catalog；业务与实际双端 WSS/DTLS 验证在 `Tools/NetworkServer.Tests`。协议和 native API 不因程序集拆分而改变，Unity 仍按自己的目标编译同源代码。
