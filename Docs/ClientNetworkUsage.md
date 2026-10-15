# 客户端网络使用说明

客户端 API 在 `GameFrame.Network`，游戏消息、枚举和生成路由统一在 `Game.Protocol`。开发/线程/错误与所有权规则见 [客户端网络开发规范](ClientNetworkDevelopment.md)；本地服务、PostgreSQL、证书与测试凭据准备见 [服务端使用说明](DotNetUsage.md)。本文示例位于已有 Hotfix 主线程异步业务方法内，变量由业务提供，不表示当前已实现 GameNetworkSession 或世界服务。

## 1. 启用网络并创建客户端

在框架初始化的 GameRuntimeConfig 设置网络选项：

```csharp
NetworkOptions = new NetworkRuntimeOptions()
```

随后走现有 GameRuntime.InitAsync 与 GameEntry 启动流程。框架默认 null 禁用网络；当前项目 Launch 显式启用，GameEntry 创建 GameLoginSession，但直到玩家点击登录才连接。未启用时访问 GameRuntime.Network 抛状态异常。不要在登录按钮里重复初始化 GameRuntime 或新建持久根。

业务会话持有客户端，端点来自可信环境配置：

```csharp
using System;
using GameFrame;
using GameFrame.Network;
using Game.Protocol;

var endpoint = new Uri("wss://localhost:7443/network");
NetworkClient client = GameRuntime.Network.CreateClient(
    new NetworkClientOptions
    {
        TrustedWebSocketEndpoints = new[] { endpoint }
    },
    GameNetProtocol.Catalog);
await client.ConnectAsync(NetworkConnectOptions.WebSocket(endpoint), cancellationToken);
```

Connect 完成仅代表传输和 Hello 成功。全部 API、State/Generation、订阅和 GetMetrics 在主线程；不能 Task.Run 调用或 Wait/Result 等待。首次建立 KCP 才加载原生库，WSS-only 不要求插件；WebGL 当前不支持建连。

## 2. 注册、登录、恢复与注销

项目启动后打开现有 LoginPanel。GameEntry prefab 的 `Login Endpoint` 默认 `wss://localhost:7443/network`，先按服务端说明准备 PostgreSQL、账号表与可信证书，启动 local-wss，再播放 Launch。正式环境替换为可信 WSS 地址；不关闭证书校验。

如果 Editor 提示 `UNITYTLS_X509VERIFY_FLAG_NOT_TRUSTED`，通过 `Tools/GameFrame/Network/Set Local Development Certificate` 选择服务端 PFX 对应的公开 `.cer` 或 `.pem`。文件保留在项目外；仅接受有效期内、DNS 名为 localhost 且无私钥的证书。工具保存本机路径/指纹，每次播放初始化自动加载，停止后恢复 TLS 信任，不需要改 Launch 或 GameEntry。`Show Local Development Certificate` 查看当前配置，`Clear Local Development Certificate` 清除。文件删除、过期或内容更换会阻止播放并报告原因；轮换证书后重新选择。其他开发者各自选择自己的证书，正式玩家无需配置。

从已有服务端 PFX 导出公开 DER 证书（第一条命令交互输入 PFX 密码）：

```sh
openssl pkcs12 -in /absolute/path/localhost.pfx -clcerts -nokeys -out /absolute/path/localhost.pem
openssl x509 -in /absolute/path/localhost.pem -outform DER -out /absolute/path/localhost.cer
```

不要将 PFX 或私钥导入 Unity；正式服务使用对应域名的可信 CA 证书，Editor 工具不进入 Player。

登录按钮校验账号（1～64 个 UTF-16 字符，无首尾空白/控制字符）、密码（8～128，不裁剪），禁用控件并提交一次 Login。已有账号验密，不存在则建号并认证；错误密码显示拒绝，失败收尾清空密码并恢复输入。成功后由 Hotfix 的 GameFlow 通过 GameScene 以 Single 模式加载、激活 Main，绑定 Main 的主相机与持久 UI 相机，打开 `MainPanel` 成功后关闭登录面板。主界面提供五子棋、木鱼和设置按钮；五子棋可选择本地或联机房间，木鱼使用本地玩法。退出游戏或设置后返回上一个窗口，账号连接继续存活。

进入 Main 或打开主界面失败时登录面板保留，已认证会话仍有效；账号和密码输入保持禁用，玩家再次点击确定只进入主场景，不重复认证。断线后清空认证，关闭场景组的主界面、游戏及加载请求，再重新打开登录面板；Main 已加载时重新登录复用该场景。加载期间认证失效或业务停止，迟到的场景完成结果不打开主界面；GameEntry 的主动 StopAsync 先停止 GameFlow 并等待已发起的主场景导航收尾，再释放账号会话与配置。流程只借用会话；面板通过参数和成功回调接入，职责与多场景扩展见 [业务流程](Launch.md#hotfix-业务流程)。

会话是普通实例，由入口创建、注入、释放，后续业务接收同一个实例：

```csharp
await GameUI.Push<LoginPanel, LoginPanelArgs>(new LoginPanelArgs(session, onLoggedIn)); // onLoggedIn: Func<string, UniTask>
GameUser user = session.CurrentUser; // 未登录、断线或到期时为 null。
if (user == null)
{
    return;
}
ulong accountId = user.AccountId;
// session.LobbyClient 是已认证客户端的借用引用，业务不关闭或释放它。
```

GameUser 只包含账号 ID、输入账号名、认证 epoch 和单调到期时间；私有 sessionId/token 不暴露给 UI，密码不持久化。GameEntry 默认启用 Remember Login，SQLite 记录每个服务端最近登录的账号，系统安全存储保存恢复凭据。断线、顶号或显式注销清空内存认证并回到登录，不自动重连。`await session.LogoutAsync(token)` 请求远端注销并清理本地状态；生命周期结束使用 `await session.DisposeAsync()`。运行中关闭整个应用先 `await entry.StopAsync()`，再关闭 GameUI、GameRuntime 和资源服务。

停止播放或进程退出时，Unity 不保证 Launch 和 GameEntry 的清理顺序。此时网络关闭仍清空认证；GameFlow 仅在流程运行、GameRuntime 运行且 GameUI 已初始化时执行界面切换，异步打开完成后再次检查，避免在 UI 销毁后返回登录。

下面展示底层 RPC，已有 GameLoginSession 的业务直接调用它，不再重复维护一份认证状态。独立注册仍可选择 Register，再 Login：

```csharp
OperationResponse registered = await client.RequestAsync(
    GameNetRoutes.Register,
    new RegisterRequest { Account = account, Password = password },
    TimeSpan.FromSeconds(5),
    cancellationToken);

if (registered.ResultCode != ResultCode.Success)
{
    // 由业务显示结果并结束本次注册流程；不自动重放。
    return;
}
```

Register 成功仍未登录，不延长服务器 10 秒登录期限。账号/密码约束及重名处理见服务端说明；密码是玩家输入，不是服务器启动配置，也不能硬编码到正式资源。

```csharp
long admission = NetworkClock.GetMilliseconds();
LoginResponse login = await client.RequestAsync(
    GameNetRoutes.Login,
    new LoginRequest { Account = account, Password = password },
    TimeSpan.FromSeconds(5),
    cancellationToken);

if (login.ResultCode != ResultCode.Success)
{
    // 由业务处理登录拒绝；不读取失败响应的成功载荷。
    return;
}

if (login.AccountId == 0 || login.AuthEpoch == 0 ||
    login.AccountSessionId == null || login.AccountSessionId.Length != 16 ||
    login.ResumeToken == null || login.ResumeToken.Length != 32 ||
    login.AccountRemainingTtlMs == 0 || login.AccountRemainingTtlMs > 86400000 ||
    login.ResumeRemainingTtlMs == 0 ||
    login.ResumeRemainingTtlMs > login.AccountRemainingTtlMs)
{
    throw new InvalidOperationException("Invalid successful login payload.");
}

long accountDeadline = checked(admission + login.AccountRemainingTtlMs);
long resumeDeadline = checked(admission + login.ResumeRemainingTtlMs);
if (NetworkClock.GetMilliseconds() >= resumeDeadline)
{
    throw new InvalidOperationException("Login credentials expired before delivery.");
}
// 此时业务才能保存账号/epoch/token/deadline并提交Authenticated。
```

示例 timeout 为单次 RPC 上限，服务器登录硬期限不会因新 RPC 重置。GameLoginSession 先等待 Connect（上限 10 秒），随后提交 Login（上限 5 秒），校验成功载荷/TTL。LoginAsync / ResumeLoginAsync 返回 GameLoginResult：ResultCode 表示服务端认证结果，RememberedLoginError 单独报告账号记忆失败；认证成功但保存失败仍保留有效会话，不自动重试、不写明文后备。登录面板清空密码并直接进入 Main，在主界面状态文本提示“本次未能记住账号，下次登录需输入密码”，不增加一次确认。主动取消、认证或连接失败仍 await Close，不能认为服务端一定未建号或未认证，清理失败保留主因。响应凭据在保存尝试后清零，不随已认证会话长期保留。完整世界 GameNetworkSession 仍延后。

登录后用 local Echo 确认实际往返：

```csharp
EchoResponse echo = await client.RequestAsync(
    GameNetRoutes.Echo,
    new EchoRequest { Text = "hello", DelayMs = 0 },
    TimeSpan.FromSeconds(5),
    cancellationToken);
```

`await session.ResumeLoginAsync(token)` 使用已保存的账号恢复登录。登录面板打开时只读取本地记录；有效凭据隐藏密码输入框，玩家点击“确定登录”才连接 WSS。恢复先读取并校验本地凭据，再关闭旧连接并建连；连接成功后、提交 ResumeLogin 前，验证凭据未被替换并删除系统存储中的旧令牌。建连失败或消耗前取消保留免密资格，面板允许玩家再次点击；成功消耗后，无论请求拒绝、超时、取消或响应丢失都不复用旧令牌，保留账号名并恢复密码输入。成功响应校验账号 ID、载荷及剩余期限，尝试保存轮换后的新令牌；保存失败单独报告，不撤销已经成功的认证。登录面板继续进入 Main，由 MainPanel 的状态文本提示下次需输入密码；进入场景失败后再次点击仍携带此提示，不重复认证。恢复不延长服务端账号会话，默认最长 24 小时；服务器重启、顶号、注销或过期后需重新输入密码。

本地数据库位于 `Application.persistentDataPath/Game/login.sqlite`，application_id=1195789390、schema=1。仅含 endpoint、account_name、account_id、expires_utc_ms；无密码、sessionId 或 token。令牌按应用标识、端点与固定用途命名空间隔离，不依赖数据库绝对路径；测试或独立账号配置可显式传入 credentialNamespace。旧路径键不自动迁移，升级到新键后首次需输入密码。平台使用 macOS/iOS Keychain、Windows Credential Manager、Android Keystore AES-GCM（密文位于 noBackupFilesDir）。安全存储失败明确报错，不写明文后备。SQLite 与安全存储不跨库原子提交：写库失败删除本次安全凭据，二者不匹配时要求密码登录；既有库格式错误不覆盖重建。ReadCredentialAsync 只读取凭据，ConsumeAsync 在提交前消耗读到的同一凭据，替换或过期返回 false，执行错误抛出；消耗仅删除系统存储令牌，不为此重开 SQLite 修改过期字段，CanResume 同时要求元数据与系统凭据匹配。

“切换账号”调用 ForgetRememberedAccountAsync，清除该端点的本地记录与令牌并恢复空输入框。显式 Logout 分别尝试本地清除和远端注销，本地失败不跳过远端请求，最后清空认证并关闭连接。本地清除不受调用者取消影响；远端 RPC 仍遵守调用者取消和超时。任一步执行失败均传播首个原异常，其他错误单独记录；本地失败但远端成功时明确记录“远端已注销，但本地账号清除失败”，两者失败时记录远端注销未确认。明确收到服务器 Close2（替换）、Close4（到期）或 Close7（撤销）时，立即清空认证，等待当前账号操作后删除匹配的恢复令牌，再通知返回登录；账号元数据保留。会话只保存成功持久化令牌的 SHA-256 摘要用于条件删除，迟到关闭不能删除新令牌；保存响应期间收到明确撤销也执行条件清理。普通网络断线、未知关闭原因及 Dispose/正常关闭保留下次登录所需记录。GameEntry 的 Remember Login 可关闭此能力，适用于不支持系统安全存储的平台或不需要记忆账号的业务。

已登录时显式 Logout：

```csharp
OperationResponse result = await client.RequestAsync(
    GameNetRoutes.Logout,
    new LogoutRequest(),
    TimeSpan.FromSeconds(5),
    cancellationToken);
```

业务检查 ResultCode，停止输入与恢复任务、清零凭据并释放客户端；RPC 失败仍尝试本地清理并报告“远端注销未确认”。Logout 成功后服务器 Close1，顶号/Resume 替换 Close2，到期 4、凭据拒绝 5、登录等待超时 6、撤销 7。不通过本地清理自动撤销已提交业务。

## 3. 请求、通知、订阅与观察者

| API | 返回含义 |
| --- | --- |
| RequestAsync(route, dto, timeout, token) | 匹配响应通过校验并主线程解码；业务结果仍检查 ResultCode |
| SendAsync(NetSend, dto, token) | 已同步编码、进入可靠队列；取消只在 admission 前生效，不保证送达/提交 |
| TrySendUnreliable(NetUnreliable, dto) | true 本地准入，false 容量/速率不足；参数/状态/能力错误仍抛异常 |
| Subscribe(NetPush, handler) | 每 opcode 一个处理器；返回句柄只注销自身，可跨 Close/重连保留 |
| ObserveState / ObserveMessageFault | 主线程通知，返回可释放句柄；不是自动恢复接口 |
| CloseAsync / DisposeAsync | 前者等待释放后可重连；后者注销订阅/宿主登记且不能复用 |

RPC 期限从本地 admission 起算，后台 deadline 不会因主线程卡帧延长；期限内保留响应后，到主线程完成前取消仍可赢。服务器控制关闭通过 NetworkConnectionClosedException.ReasonCode 保留原始 ushort 原因（包括未知值），在状态观察者的 Error 上传递；业务自行解释。非零 StatusCode 抛 NetworkRemoteException，未知码保留；超时抛 NetworkRequestTimeoutException，本地准入或队列容量不足抛 NetworkBackpressureException；服务端过载 status1 仍抛 NetworkRemoteException。超时/取消/断线后的业务结果可能未知，不自动重发。

当前五子棋 Push 使用示例：

```csharp
using IDisposable subscription = client.Subscribe(
    GameNetRoutes.GomokuRoomSnapshot,
    snapshot =>
    {
        // 先按epoch/version更新业务模型，再通知UI。
    });
using IDisposable stateObserver = client.ObserveState(change =>
{
    // 业务使对应会话离开Ready/Authenticated，保存原错误。
});
using IDisposable faultObserver = client.ObserveMessageFault(fault =>
{
    // 可以记录opcode/generation；不输出正文或凭据。
});
```

五子棋房间会话持有 GomokuRoomSnapshot / GomokuRoomClosed 两类可靠 Push 的订阅，面板借用只读模型。只有订阅持有期间有效；将句柄保存到会话或既有 OpenScope/LifetimeScope，异步方法返回后需继续监听时不能使用局部 using。Push 抛错 Fault 当前连接，后续业务丢弃；观察者抛错只停止该批并记录，不改网络首因。未订阅 Push 也校验正文，合法未订阅消息不创建 DTO。

WSS 不支持不可靠 API。当前生产 catalog 只有可靠路由；底层 KCP 的可靠 / 不可靠能力通过独立测试路由验证，后续玩法需要时再定义输入和状态协议，不自动转换通道。

<a id="原生插件安装"></a>

## 4. 原生插件安装

所有构建命令、平台参数与依赖准备集中在 [服务端使用说明原生章节](DotNetUsage.md#原生构建与部署)，双方共用同一 Native/Kcp 源码，不复制到 DotNet 或 Unity 第二份。Unity 安装省略--server-output：

```sh
python3 Native/Kcp/build/install.py Tools/Artifacts/NetworkNative/macos-release
```

安装到 `Unity/Assets/Plugins/GameFrame/Native/Kcp/<platform>`，库名 gameframe_kcp，iOS P/Invoke 用__Internal；平台选择和 ABI 以安装器为准。已加载库替换后重启 Unity，不用刷新缓存冒充新二进制。缺库/错误架构/ABI 明确失败，不回退 WSS。

Simulator archive 独立构建并默认禁用，测试模拟器时显式选择 simulator、禁用 device archive，避免重复链接。Android/iOS 产物存在不代表 IL2CPP 链接/真机通过；新 native/ABI/架构需要新安装包，不能用 Hotfix DLL 替换 native 代码。

## 5. KCP/DTLS 调用

从可信配置取得 UDP 端点，业务或受控测试提供票据；当前没有世界授权 RPC 或世界玩法协议，不能执行真实取票→世界业务流程。底层联调先启动服务端 local-kcp-dtls，按服务端说明共享短时随机凭据，在其 30 秒内完成连接：

```csharp
var endpoint = new Uri("udp://127.0.0.1:7450");
NetworkClient client = GameRuntime.Network.CreateClient(
    new NetworkClientOptions
    {
        TrustedUdpEndpoints = new[] { endpoint }
    },
    GameNetProtocol.Catalog);
await client.ConnectAsync(
    NetworkConnectOptions.Kcp(endpoint, ticket, conv, identity, psk, grantDeadlineMilliseconds),
    cancellationToken);
EchoResponse echo = await client.RequestAsync(
    GameNetRoutes.Echo,
    new EchoRequest { Text = "udp", DelayMs = 0 },
    TimeSpan.FromSeconds(5),
    cancellationToken);
```

ticket 为 1～1024 字节、conv 非零、identity16 字节、PSK32 字节，deadline 是 NetworkClock 同源单调时间。未来授权 RPC 返回 TTL 时，deadline 从 RPCadmission+TTL 算起；本地 grant 从服务端注册计时，不能在客户端收到文件时假造新的 30 秒。调用者在 Connect 结束前不修改凭据数组，使用后清零；同一 grant 不能绑定第二个连接，过期显式重新准备联调凭据。

不可靠输入能力使用 `TrySendUnreliable(NetUnreliable<T>, dto)`，返回 false 表示本地拒绝，不保证对端收到。生产协议未定义此类路由，local KCP 只开放 Echo。

## 6. 关闭与诊断

会话结束先停止业务/恢复任务并取消请求、释放订阅，再：

```csharp
await client.DisposeAsync();
```

需要复用同一客户端时 await CloseAsync，再显式 Connect。整个框架由上层生命周期执行 `await GameRuntime.ShutdownAsync()`，不能在普通面板关闭时销毁网络宿主或 root；运行中结束先让网络释放，之后释放 Timer/root/资源。故障后不直接重连，必须先 Close。

主线程调用 GetMetrics，关注 State/Generation、OwnedBytes/PeakReservedBytes、pending/堆/队列条数、timeout/cancel/late、不可靠丢弃、发送队龄告警、应用帧/正文 bytes 与 PingRoundTripMilliseconds。Kcp 快照区分 native 预留与实际/峰值、waitsnd/srtt/rto/窗口、明文/密文队列、实际 socket 字节。TimeoutRetransmits 只含超时重传，不含 fast retransmit；DtlsDiscardDetailsAvailable=false 表示细分坏记录不可用。 KCP关闭会先取消关联IO；此后迟到的发送/接收/关闭入队保留取消语义，避免把远端Close原因覆盖成普通传输停止。native销毁确认成功后，Waitsnd与其他当前队列/占用值归零；累计计数和峰值保留。销毁失败仍保留未归还的额度与诊断，不以终态冒充资源已释放。

`GetMetrics().Rpc` 按当前连接汇总成功解码的 RPC。每个阶段提供 Count、LastMilliseconds、TotalMilliseconds、MaximumMilliseconds、AverageMilliseconds，单位为毫秒，使用 Stopwatch 高精度单调时间；没有样本时 Count=0。阶段定义如下：

| 字段 | 测量范围 |
| --- | --- |
| Encoding | 请求帧分配、出站对象校验与编码；只随成功响应计入汇总 |
| ResponseWait | admission 到 IO 保留完整响应，包含本地发送排队、传输和服务端处理 |
| MainThreadWait | IO 保留响应到主线程开始解码，包含派发等待和取消注册注销 |
| Decoding | 响应正文校验和 DTO 解码 |
| Completion | admission 到解码完成，等于 ResponseWait + MainThreadWait + Decoding；不含 Encoding |

这些值不是纯网络 RTT。统计在任务完成前提交，不包含业务 continuation 和帧清零；整个 Pump 的成本仍包含这些工作。非零传输 StatusCode、取消、超时、连接失败、迟到响应与解码失败不进入成功样本；DTO 内的业务结果码不由 Runtime 判定。汇总为固定空间，不保存历史或提供 P95/P99；采样成本也计入实际框架开销。Close 后保留最后一代快照，下次 Connect 使用全新汇总。

`GetMetrics().Memory` 展示 ReliableSendReservedBytes、ReliableReceiveReservedBytes、ControlReservedBytes、UnreliableSendReservedBytes、UnreliableReceiveReservedBytes、PendingReservedBytes。帧额度包含数组容量与每帧 512 字节预约，Pending 包含已经终态但尚未交付的请求。发送在途、接收正在组帧也占额度：WSS 空闲连接会保留接收固定头的 32+512 字节，不是泄漏。握手临时帧和 KCP native allowance 还占连接总额，后者见 Kcp.NativeReservedBytes；子预算由不同 IO 工作推进，读取不是所有子项的原子加总。真正释放完成后的可控额度才归零。

OwnedBytes 不是 RSS、所有 CLR 对象或 TLS/内核内存；每帧/每 pending512 字节是保守 reservation。当前无空闲缓冲池，MaxIdlePoolBytes 实际 0，仍有逐包分配。KCP 请求 socket 收/发各 256KiB 并报告系统实际值，ClientWebSocket 不开放底层 buffer 设置，不能声称其设置成功。

`GameRuntime.Network.GetMetrics()` 返回宿主生命周期的固定空间累计快照，主线程读取，Shutdown 后保留。它与上述客户端成功 RPC 指标的范围不同：

| 字段 | 测量范围 |
| --- | --- |
| Pump | 每次最外层 Pump 的同步工作，包括派发、成功/取消/超时/失败交付、同步 continuation、帧释放清零，以及宿主故障清理每轮同步工作；不含异步等待 |
| Outbound / OutboundOutsidePump | RequestAsync 的同步准备、SendAsync、TrySendUnreliable 的状态检查、校验、编码和准入，包含拒绝路径；后者只计调用开始时不在 Pump 中的发送，不含等待响应 |
| Dispatch | 实际做工作的 PumpOne，包含控制/关闭通知和任务交付；空探测不累计，处理过程抛错仍计时 |
| PushDecoding / PushHandlers | 已订阅 Push 的 DecodeValidated 和同步 handler 分别计时；正文形状校验位于派发流程，包含在 Dispatch/Pump 中；未订阅消息不会产生 DTO 解码样本 |
| OutboundRejected / PushDecodeFailures / PushHandlerFailures | 同步准入抛错或 TrySend=false，以及对应 Push 阶段抛错次数；已接受请求后来超时不属于准入拒绝 |
| ClientCount / OwnedBytes / PeakReservedBytes | 宿主当前客户端数、可控自有额度和生命周期预约峰值；不是进程内存 |

每项耗时沿用 NetworkDurationSnapshot 的毫秒口径。窗口主线程成本取 `ΔPump.TotalMilliseconds + ΔOutboundOutsidePump.TotalMilliseconds`；其他分项与它有包含关系，不能相加。同步回调里再次发包已归入 Pump；在回调内部读取快照时，尚未返回的 Pump/handler 还未提交。Connect/Close 的 Pump 外同步工作、网络 IO 线程、异步延后的业务工作和观察快照自身开销另计，不能称此值为整个应用的网络 CPU 时间。Runtime 不保留逐帧历史，不采集 GC 或计算分位数。

## 7. 协议与依赖维护

修改协议直接编辑仓库根 Protocol/**/*.proto，按 [统一生成步骤](DotNetUsage.md#协议生成与验证) 重新生成两个正式 C#文件；生成目录不放手写代码。客户端.NET 不能只更新一端。manifest/hash/vector 在 Protocol/Generated 仅用于开发验证，不进入 Unity 资源，运行时只用 catalog 内嵌指纹。

Unity 编辑模式下使用 `Tools/GameFrame/Network/Generate Messages` 生成，使用 `Check Messages` 只读检查。这两个菜单异步启动 `DotNet/NetworkProtocol`，包含 `--artifacts`，同步处理正式 C# 与开发辅助产物；Console 显示成功结果或完整退出码/stdout/stderr。执行期间禁止重复启动，暂缓自动刷新和域重载；进程退出后恢复，生成成功才显式刷新资产。字节未变化的生成文件不重写，已有 `.meta`/GUID 保留。

工具需要 `DotNet/global.json` 锁定的 .NET SDK 10.0.401，与 Luban 的 .NET 8 分开。自动从 `DOTNET_ROOT`、Unity 进程的 PATH、平台常见安装目录及用户 `.dotnet` 目录定位 dotnet。Unity Hub/Finder 未继承终端 PATH 时，可通过 `Tools/GameFrame/Network/Set .NET Executable` 选择 dotnet 可执行文件；路径保存在本机 EditorPrefs，不写入项目。显式配置无效或 SDK 缺失时报告失败，不自动切换版本或重试。无需向 Unity 拷贝生成器 DLL 或 Roslyn 依赖。

Unity 依赖固定 MemoryPack1.21.4，NuGetForUnity4.5.0 与 MemoryPack.Unity Git tag 在 Packages/manifest.json，包和 meta 在 Assets/Packages，packages.config/NuGet.config 及 UPM 锁同步维护。查看用 NuGet > Manage NuGet Packages，恢复用 NuGet > Restore Packages；保留 Generator 的 RoslynAnalyzer 标签和导入排除设置，不将.NET10 运行时 DLL 换入 Unity。

<a id="验证与现有证据"></a>

## 8. 验证与现有证据

.NET同进程KCP核心的DTLS/Hello取消与期限、3秒/持续断流、两端关闭丢包及显式新grant/generation验证，使用[故障入口](DotNetUsage.md#network-fault)。该入口与下述Unity公开客户端测试分层记录，不用其中一层替代另一层或设备验收。

<a id="unity-kcp-fault"></a>

### Unity 公开客户端故障验证

PlayMode入口为 `GameFrame.Tests.NetworkKcpFaultTests`，包含10项真实UDP/DTLS用例。测试调用公开 `NetworkClient.ConnectAsync/RequestAsync/CloseAsync`、状态订阅及指标，由实际 `NetworkRuntimeDriver.Update` 派发；不直接创建ConnectionContext或手动调用Pump。每项独立启动一个.NET测试伴随进程，复用已有KcpListenerHost、正式Echo路由和有界UDP转发器，另保留一条每500ms串行Echo的正常连接。无数据库、正式服务器配置、场景或玩法依赖。

准备时先从仓库根目录编译伴随测试工程：

```sh
(cd Tools && dotnet build NetworkServer.Tests/NetworkServer.Tests.csproj -c Release -p:RestoreLockedMode=true)
```

在**Unity编辑器进程**中显式设置以下临时环境变量，再通过Test Runner或Unity MCP运行上述测试类；也可以在启动Unity前从终端传入。已打开的编辑器不会继承随后在终端设置的变量。

| 变量 | 值与约束 |
| --- | --- |
| GF_UNITY_KCP_FAULT_TESTS | `1`，未显式启用则跳过；跳过不算故障验收通过 |
| GF_UNITY_KCP_FAULT_DOTNET | 绝对dotnet可执行路径，须支持Tools锁定的SDK10.0.401；macOS本次为`/usr/local/share/dotnet/dotnet` |
| GF_KCP_NATIVE_LIBRARY | 当前宿主平台已核验的Release ABI2原生库绝对路径，须与Unity当前安装/加载产物匹配；macOS为仓库`Unity/Assets/Plugins/GameFrame/Native/Kcp/macOS/libgameframe_kcp.dylib` |

测试启动伴随进程时使用`--no-build --no-restore`，因此先编译不可省略；不自动下载SDK、重构建/替换插件或切换版本。伴随入口是.NET测试工程中的Explicit `KcpUnityFaultHostTests.ServeUnityFaults`，普通.NET回归不运行它，也不把它作为生产服务。原生只读校验口径见[服务端负载入口](DotNetUsage.md#network-load)，替换已加载库后须先重启Unity。

| 用例组 | 实际核验 |
| --- | --- |
| DTLS取消、内部期限（2项） | 建连前断流且确认丢包命中；调用取消保留精确token；未取消时到默认10秒期限，显式Close后资源归零 |
| Hello取消、内部期限（2项） | 测试factory在ClientHello后切断服务端回复，随后发布；取消保留token，未取消时到默认5秒Hello期限 |
| 3秒断流恢复 | 原8秒期限RPC保持在途，放行后成功且目标handler只执行1次，无业务重试 |
| 持续断网 | 原30秒RPC先因默认心跳故障结束，状态首因与请求异常为同一实例，正常连接保持可用 |
| 同一个客户端Close后Connect | 暂存旧服务端UDP，旧RPC超时、公开Close完成、显式撤销旧grant并等待旧服务端结束后，用新grant/端点建立generation2；放行旧UDP后新Echo成功，后续事件均属新代且统计重置 |
| 主线程暂停6秒 | 故意阻塞测试主线程，实际Update/Pump暂停；后台IO仍有双向控制流量，3条响应在2秒期限内到达并保留，恢复后按序在主线程交付；使用测试专用PendingCount=3核验未交付响应仍占准入额度，不改默认容量 |
| 宿主耗时诊断 | 同步拒绝发送计数，Push 解码/handler 分项，回调内再次发包不重复归入 Pump 外，handler 抛错和故障关闭后统计保留 |
| 响应保留后取消 | 主线程暂停期间确认响应已到达、deadline已移除，由后台取消；恢复前任务不完成、仍占pending额度；恢复后保留调用者token，释放帧和额度，后续Echo成功 |

主线程停顿是测试注入，不建议业务阻塞主线程或禁用网络Driver来模拟暂停；禁用Driver属于宿主故障，既有NetworkLifecycleTests单独覆盖。内部建连/Hello期限当前可能返回内部token的OperationCanceledException或NetworkException；用例按阶段、未取消的调用者token及默认耗时区分，不能把内部期限错误显示为用户主动取消。

每次测试的临时目录仅用于顺序控制命令与一次性grant，不是游戏网络协议；macOS/Linux目录仅当前用户可访问，服务端写入文件先设置仅当前用户读写，凭据读取后删除、临时数组结束后清零，不写日志。命令串行确认，握手factory屏障、命令/进程/清理等待有观察期限；观察超时判失败，不能当作资源已释放。测试结束依次停止探测、Close/Dispose客户端、Shutdown并核验宿主自有预算、join转发器与服务端、等待伴随进程成功退出，再删除临时目录；失败时仍尝试独立清理，只终止本次拥有的进程。正常对照与新代服务端须reason=1且无Error。

关闭后检查owned/pending/deadline、可靠队列、native实际/预约/Waitsnd及native队列归零，两端发布会话均完成、转发计账闭合且无溢出；服务端native live bytes仍不可直接测量。旧UDP发送到原端点，不能据此声称已验证同端点复用的DTLS重放攻击。结束后恢复测试前的场景、EditorSettings和上述环境变量；不保留临时凭据或报告。

本入口仅在Unity Editor下编译和运行；Player/IL2CPP、真机/其他Editor平台、系统后台挂起/换网/IPv6、带宽/重复包、多连接容量和长时soak分别验收。

<a id="network-player"></a>

### macOS IL2CPP 独立 Player 验证

入口分两步，使用 Unity 已安装的 macOS IL2CPP / HybridCLR 工具链和项目现有 Release ABI2 KCP 插件：

1. 当前平台选择 StandaloneOSX、Scripting Backend 为 IL2CPP，先保存场景；执行 `Tools/GameFrame/Network/Build macOS IL2CPP Validation Player`，选择一个空输出目录。工具生成独立临时场景，执行 HybridCLR Generate/All 和 Development IL2CPP 完整构建，输出 `NetworkValidation.app` 及构建结果标记。失败会保留 `build-failed.txt`，不允许用失败产物验收。
2. 从仓库根目录运行：

```sh
python3 -B Tools/network_player.py --player /absolute/output/NetworkValidation.app
```

支持 `--dotnet /absolute/path/to/dotnet`；默认查找 PATH。需要留存证据时显式添加 `--json /absolute/path/result.json`，默认只向终端输出且删除临时运行资料。工具先 locked restore / Release 编译伴随测试工程，核验 native 二进制、双清单及维护源码指纹，再检查 app 内 KCP 插件 hash 一致。打包后对本地验证 app 重新执行 ad-hoc 签名，运行脚本先核验完整签名，再按宿主架构显式选择 arm64 或 x86_64 启动，避免将 universal 构建误当作两个架构都实跑。实际 Player 清除外部 DYLD 和测试 native 路径覆盖，使用包内插件；不重建或替换 native。

验证场景只包含 AOT 验证组件、相机与灯光。组件加载本次编译的 `Game.Hotfix.dll`，并加载 HybridCLR 生成清单要求的、本次最终构建裁剪后的 AOT 补充元数据，然后由 Hotfix 验证代码调用正式 catalog / MemoryPack codec / NetworkClient；不复制 DTO、不修改 Launch、GameEntry、登录、玩法或正常资源包。验证源码位于 `Tests/Player`，asmref 分别归入 Game.AOT / Game.Hotfix，正确性入口受 `UNITY_EDITOR || DEVELOPMENT_BUILD` 限制；性能入口另受专用 `GAMEFRAME_NETWORK_PERFORMANCE` 限制，普通正式 Player 不含这些入口。反射只用于这个独立验证场景的热更入口。

七组检查覆盖 DTLS/GFNP Hello 与 native 分配、中文 UTF-8 Echo、精确调用者 token 取消和迟到响应、RPC 超时和后续 Echo、已有服务端在途请求时 Close 与队列/native 归零、同一客户端新 grant / 新代重连和第二次关闭、Dispose 与运行时/worker Shutdown。状态与任务回调必须在 Unity 主线程，关闭前后的服务端会话均需 reason=1 且无 Error，最终伴随进程退出0且资源/转发计账闭合。

伴随服务复用 Explicit `KcpUnityFaultHostTests.ServeUnityFaults`，由 Python 启动和停止；Player 不启动外部进程。一次性凭据通过权限700的本机临时目录交接，读取后删除并清零数组，不加入安装包、命令行或日志。运行超时、异常退出或任一断言失败均返回非0；失败仍终止并等待本次拥有的进程，未确认的关闭不当作验收成功。

构建后恢复场景、Build Settings 场景列表、Development 开关、构建路径及 `UNITY_IL2CPP_PATH`，删除临时场景。HybridCLRData / HybridCLRGenerate 是正常可再生成缓存，本入口会更新其 Development 构建产物；制作正式包仍须按正式平台/模式重新生成。不会覆盖 `Assets/Config/Code`、YooAsset 资源包或正式 GlobalConfig。

此入口验证现有协议的独立 Development Player 链路；正式 Launch/YooAsset 全链、发布包裁剪等级、新增 DTO 的包后热更、WSS、移动设备、弱网/后台/换网/IPv6、容量和长时运行分别验收。

2026-10-14 命名空间统一复验（Unity 6000.3.19f1 / macOS）：Editor 编译通过；网络相关 EditMode135/135，网络生命周期 / 五子棋 / 真实 WSS 登录及对局 PlayMode45/45，现有原生 KCP / DTLS 的 Hello / Echo / 释放 PlayMode1/1，均零跳过。StandaloneOSX Release Player 脚本与 Hotfix DLL 编译通过，已更新 `Assets/Config/Code/Game.Hotfix.dll.bytes` 并保留 GUID。场景、测试设置、测试环境变量和动态字体缓存恢复，独立临时服务 / 数据库 / 证书 / grant 清理。本次未构建或部署 YooAsset 资源包，未验实际 IL2CPP、新 DTO 包后热更或真机；此前旧协议资源包发布前须重新构建。

在对应 Unity 编辑器使用现有测试入口，步骤遵循 [gameframe-validate](../skills/gameframe-validate/SKILL.md)：

| 变更 | 优先运行 |
| --- | --- |
| 共享协议/codec/生成产物 | NetworkProtocolTests、NetworkMemoryPackTests EditMode，.NET Protocol.Tests 及 check --artifacts |
| 协议编辑器工具 | NetworkProtocolToolTests EditMode（macOS/Linux 进程管道与错误路径）、实际 Generate/Check 菜单 |
| 网络宿主、订阅、关闭和异步生命周期 | NetworkLifecycleTests PlayMode 及相关.NET 连接/框架回归 |
| 登录面板、账号会话与入口 | GameLoginSessionTests、ResourceLoadManagerTests PlayMode，实际 Launch 登录；联调用例使用 GF_LOGIN_TEST_ENDPOINT 指向已准备的 WSS/PostgreSQL 服务，未配置时明确跳过；入口用例在 Awake 前注入端点，不修改正式 prefab |
| KCP/socket/native | native 回归、.NET KcpLoopbackTests、Unity NetworkKcpLoopbackTests |
| 程序集/平台条件 | Editor 编译、实际目标 Player 脚本；native 链接另做 IL2CPP 包与实跑 |

Unity 实际 UDP 测试使用 GF_KCP_LOOPBACK_CREDENTIALS 临时 JSON（服务端 grant 字段另加 Port），连接已运行 local listener，不自动启动服务或保留凭据。记录测试前场景/EditorSettings，结束恢复，不操作用户正在运行的服务/数据库。替换已加载 native 后先重启 Unity。

Unity TLS 在部分 Editor 平台不继承 `dotnet dev-certs` 的信任。登录联调测试可额外设置 `GF_LOGIN_TEST_CERTIFICATE` 为已核验开发证书的公开 DER 文件；测试仅在执行期间设置 Mono TLS 信任锚，保留证书链、域名和有效期校验，结束恢复原值，不修改正式客户端或系统证书存储。实际环境应为 Unity/目标平台准备可信服务证书。

<a id="network-player-performance"></a>

### macOS Release Player 性能基线

先保存场景，选择 StandaloneOSX / IL2CPP，再执行 `Tools/GameFrame/Network/Build macOS Release Performance Player`，选择空目录。工具临时启用 `GAMEFRAME_NETWORK_PERFORMANCE`、关闭 Development、选宿主原生架构，执行 HybridCLR Generate/All 和完整 IL2CPP 构建；恢复场景、defines、架构和原设置。它与上面的 Development 正确性入口输出相同 app 名，运行工具检查构建标记，禁止混用。独立场景仅初始化 Network，不加载登录、UI、数据库或正式资源包；补充元数据使用最终构建清单，不据此扩大 GlobalConfig 的正式三个元数据配置。

```sh
python3 -B Tools/network_player.py --mode performance \
  --player /absolute/output/NetworkValidation.app \
  --transport kcp --scenario echo --connections 4 --rate 240 \
  --duration 15 --warmup 3 --text-length 128 --unicode --push --push-moves 225
```

WSS 使用 `--transport wss --certificate /absolute/path/localhost.pfx`，密码从环境变量 `GF_PERFORMANCE_CERTIFICATE_PASSWORD` 传给伴随服务，不进入 Player。证书必须已受该 Player 信任且匹配 localhost；工具不修改信任或跳过校验。KCP 使用现有共享 native 和每连接独立随机 grant。两种传输都由独立 .NET Explicit `NetworkPlayerPerformanceHostTests.ServePerformance` 提供现有 Echo 与可选 GomokuSnapshot Push，不创建正式房间、不新增协议或生产 handler。

| 参数 | 范围与用途 |
| --- | --- |
| scenario | idle、echo、timeout、cancel、backpressure；后三者需要实际命中对应结果，否则失败 |
| connections / rate | 1～4连接；rate 是所有连接合计提供速率，0～10000次/秒，非 idle 必须大于0；idle 强制0 |
| duration / warmup | 测量1～120秒、预热1～30秒；rate×duration≤100000；超过工具在途或每帧128次发送额度计入 HarnessLimited，不掩盖为成功 |
| concurrency / pending-limit | 各1～512；前者是工具总在途上限，后者是每连接 pending 额度，仅作用于验证客户端 |
| text-length / unicode | 0～128个字符；默认32个ASCII，Unicode改为同长度中文，区分字符数和线上字节数 |
| push / push-moves | 每次 Echo 可发送一个现有可靠快照，Moves 为0～225；用于解码/数组分配成本，不表示玩法承载能力 |
| json / dotnet | 可选证据输出绝对路径和SDK可执行路径；默认不保留运行文件，SDK沿用Tools的10.0.401 |

Player 以 `-batchmode -nographics` 运行，锁定目标60fps/vSync=0，预热、排空后测量；不包含实际画面渲染成本。timeout/cancel 使用100ms异步服务处理、客户端30ms期限/20ms取消，不向服务端发送取消协议；延迟响应正常计入迟到响应。backpressure 通过有限 pending 与提供速率制造准入拒绝；每种场景结束都对所有连接执行窗口外 Echo，验证仍可使用，再 Close/Dispose/Shutdown；两端 owned/pending/deadline/native 可追踪资源归零且正常会话 reason1/Error=null 才成功。

复验空闲使用 `--scenario idle --connections 4`；期限/取消使用 `--scenario timeout` 或 `cancel`、`--rate 2`。本轮客户端准入背压采用单连接 `--scenario backpressure --rate 2000 --pending-limit 1`，避免已接受流量超过默认每连接128包/秒的服务端输入限额。工具不放宽该限额；更大pending或速率可能先触发远端策略，出现非预期超时/断线仍判失败，不能吞掉以完成测试。失败报告附带伴随服务的安全测试诊断，不输出grant或正文。

报告口径：

- FrameNetwork 是相邻 LastPostLateUpdate 观察点间的 `ΔPump + ΔOutboundOutsidePump`，含计时探针和同步业务回调；首个样本可能是部分帧。FrameInterval 是观察点墙钟间隔，不能据此归因于 GC。两者及成功 RPC 延迟使用固定上限数组计算最近秩 P95/P99/最大值，排序在测量窗外。
- Offered/Successful/Backpressure/Timeouts/Cancellations/RpcSuccess 以测量窗发起请求为队列，包含它们排空期间的完成；SuccessfulPerSecond 是这些成功数除以窗口秒数，不能冒充严格窗口内完成吞吐。Pushes 仅计测量窗实际交付。HarnessLimited 非零时只能比较实际完成的负载，不能称完整达到提供速率。
- Pump/Outbound/Dispatch/Push 阶段 Count/Total/Mean 为测量窗差值；LifetimeMaximumMilliseconds 和 PeakReservedBytes 为截至测量窗结束的生命周期峰值，包含预热及预热后的排空；LifetimeLateResponses 截至测量请求排空完成，包含预热和两次排空，不能当作窗口独立峰值或计数。PeakPending/SendQueue/ReceiveQueue 为各连接在帧末采样的最大值，非原子精确全局高水位。
- MainThreadAllocatedBytes 是整个 Unity 主线程含工具采样的分配量，包含 DTO/字符串/数组，不代表网络独占分配；先用已知分配探测接口，Available=false 时不能把0解释为零分配。GcCollections 分别报告0/1/2代原值差，不加总。ManagedHeapBytes 是 GC.GetTotalMemory(false)，不是RSS。
- ProcessSamples 每秒由外部 ps 记录 Player RSS 和ps CPU 平滑统计，MeasurementWindow 标记所在阶段；不是网络线程独占CPU或精确GC停顿。OwnedBytes/native实际与预约分别报告，服务端 native live bytes 仍不可用。重复同参数时比较波动，单次峰值不作跨机器门槛。

分配与 GC 归因使用同一负载的独立采样包：菜单 `Tools/GameFrame/Network/Build macOS Allocation Profiling Player`，运行时将 `--mode performance` 改为 `--mode profiling`。此包保留 `GAMEFRAME_NETWORK_PERFORMANCE` 并启用 Development，默认不启用全局 Profiler，也不启用自动连接或 Deep Profiling；只有显式分配栈参数会开启短时全局采集。构建标记、命令模式与 Player 的 `Debug.isDebugBuild` 必须一致。正确性包、Release 性能包和 Development 采样包不能混用，Development 的耗时不能代替 Release 门槛。

两种负载包均在窗口外以 1 MiB 数组和一次主动 `GC.Collect()` 校验 `ProfilerRecorder`，再初始化网络、预热和排空；主动 GC 不属于测量窗口。报告中的 `FrameAllocations` 是 Unity 的 `GC Allocated In Frame` 字节计数，包含引擎、工具和负载，不称为网络独占或主线程精确字节数；`MainThreadGcCollect` 是仅限当前主线程的 `GC.Collect` 时间标记，单位纳秒，不称为全部 GC 工作或精确的 stop-the-world 停顿。`Available` 只代表指标存在，`CalibrationPassed` 才代表已知探针得到有效值；Development 采样包的分配校验失败直接拒绝负载，Release 未提供该指标时明确报告不可用。

Recorder 使用65536个固定容量帧槽、不循环覆盖，按帧求和，容量耗尽报错；开始和结束位于 LastPostLateUpdate，丢弃首个部分帧，Stop仍可能提交末尾部分帧。`Frames/Total/MaximumPerFrame/SampleCount` 是记录器返回样本的统计，与请求 cohort 和墙钟窗口存在帧边界差异；GC marker 的 SampleCount 不等同 CollectionCount，多个增量切片或嵌套标记不能推断完整回收次数。读取/数组转换与文件写出在窗口外，记录器自身开销仍存在。先以相同连接数/速率/字符串对比 Echo、0手 Push、225手 Push，再以相同pending对比低速正常与高频背压；跨构建数据分别列出，不相减制造精确归因。

已核对6000.3.19f1本机 IL2CPP 源码：`icalls/mscorlib/System/GC.cpp` 的 `GetAllocatedBytesForCurrentThread` 为 `IL2CPP_NOT_IMPLEMENTED_ICALL` 后返回0；`gc/BoehmGC.cpp` 的 `GetCollectionCount(generation)` 忽略 generation，返回同一个 `GC_get_gc_no()`。因此主线程API探针失败不是网络零分配，三个代数计数相同也不是执行了三次独立回收。没有为取得数据修改引擎、补充native统计钩子或绕过计数器校验。

此入口建立本机 Release IL2CPP 的有限窗口基线。最低移动设备每帧P95≤2ms、真实业务、其他架构/平台、弱网/后台、完整容量和24h soak 仍独立验收；本机低耗时不能证明这些目标已达成。当前没有据此新增池化、调度器或修改生产预算。

需要调用栈时，在profiling命令增加 `--duration 3 --allocation-trace /absolute/temp/echo.raw`；父目录必须存在，目标文件必须尚不存在。仅允许profiling、测量窗最多10秒。预热排空后，Player以256MiB Profiler缓冲开启二进制日志和分配栈，在 `GameFrame.Network.TraceCalibration` 内分配1MiB数组，再用 `GameFrame.Network.MeasurementBegin/End` 标记墙钟窗口；窗口结束下一帧停止并恢复设置。原始文件在进程成功退出、双端收尾通过后导出，临时运行目录仍清理；普通运行不生成raw文件。此采集有显著开销，不能引用其耗时作为Release性能。

在空闲且停止采集的Editor调用 `GameFrame.Editor.NetworkAllocationTraceAnalysis.Analyze(rawPath, reportPath)`，reportPath也须为新文件。工具通过Unity官方RawFrameDataView读取所有记录线程，在标记时间内按线程名、单次大小、完整栈分组，输出次数、字节、首个已解析托管方法及缺栈/未解析统计；先要求已知数组至少1MiB带栈记录及有效窗口，失败不产出成功报告。最多4096帧、每帧256线程、65536个分组，容量耗尽拒绝；已有Profiler历史先保存、结束后恢复，原来无历史则清除本次导入。解析量是记录中的GC.Alloc，包含引擎和夹具；不能保证未注册/未记录线程覆盖或无丢样。`UnresolvedStackBytes`表示栈中存在任一未解析native地址，`WithoutManagedFrameBytes`表示整栈缺少带程序集标识的托管方法。原始记录检查后按需清理，不提交到仓库。

#### 2026-10-15 实跑基线

环境：Apple M5 / macOS ARM64，Unity6000.3.19f1 / HybridCLR，SDK10.0.401；独立 Release IL2CPP、Minimal stripping、目标60fps、batchmode/nographics。完整原生构建0警告/0错误，11个最终构建清单元数据，仅用于独立验证包；正式GlobalConfig保持原有三个元数据。每轮预热3秒、测量15秒，之后排空、逐连接Echo并关闭；WSS使用已有受信localhost开发证书，未改信任。14轮正向运行全部退出0，HarnessLimited=0，两端所有发布会话正常关闭且可追踪资源归零。

idle-1/4表示连接数；echo-small为单连接总20次/秒、ASCII32；echo-push为4连接合计240次/秒、中文128字符、每Echo一个225手快照，两种传输各重复2轮。backpressure为单连接总2000次/秒、pending=1；timeout/cancel为单连接2次/秒。下列耗时均为毫秒，成功数按测量窗发起请求队列统计，不包含预热和窗口外探测；GC为CollectionCount(0)差值，不能与其他代相加，也不是GC停顿时间。

| 运行 | 请求结果 | 帧网络P95 | P99 | 最大值 | 成功RPC P95 | GC0差值 |
| --- | --- | --- | --- | --- | --- | --- |
| kcp-idle-1 | 0/0成功 | 0.0023 | 0.0040 | 0.0138 | 无成功样本 | 2 |
| kcp-idle-4 | 0/0成功 | 0.0059 | 0.0086 | 0.0192 | 无成功样本 | 8 |
| kcp-echo-small | 299/299成功 | 0.0725 | 0.1187 | 0.3479 | 66.640 | 7 |
| kcp-echo-push-1 | 3596/3596成功 | 1.6675 | 2.2357 | 3.1989 | 66.916 | 179 |
| kcp-echo-push-2 | 3599/3599成功 | 1.6397 | 2.1367 | 5.6987 | 67.165 | 178 |
| kcp-backpressure | 289/29999成功，29710背压 | 4.7504 | 5.2149 | 5.8062 | 66.628 | 293 |
| kcp-timeout | 0/29成功，29超时 | 0.0687 | 0.4338 | 0.5284 | 无成功样本 | 2 |
| kcp-cancel | 0/29成功，29取消 | 0.0679 | 0.4071 | 0.4541 | 无成功样本 | 2 |
| wss-idle-4 | 0/0成功 | 0.0033 | 0.0039 | 0.0122 | 无成功样本 | 7 |
| wss-echo-push-1 | 3599/3599成功 | 1.3493 | 1.7372 | 2.1422 | 17.221 | 1291 |
| wss-echo-push-2 | 3595/3595成功 | 1.4928 | 2.7321 | 2.9631 | 17.479 | 1290 |
| wss-backpressure | 895/29998成功，29103背压 | 2.9655 | 3.5934 | 4.2599 | 17.231 | 418 |
| wss-timeout | 0/29成功，29超时 | 0.0538 | 0.4537 | 0.5167 | 无成功样本 | 7 |
| wss-cancel | 0/29成功，29取消 | 0.0567 | 0.4295 | 0.4963 | 无成功样本 | 7 |

主线程分配接口在14轮Release Player中均未通过已知分配探测，Available=false；因此本轮没有可用的主线程bytes/s证据，不把0视为零分配。相同重载参数的KCP两轮P95约1.64～1.67ms、WSS约1.35～1.49ms，P99和峰值存在波动；刻意高频背压路径P95超过2ms。桌面短时结果不能满足最低移动设备门禁，也不能据此声明零GC或生产容量。先保留当前调度/帧所有权，后续重点取得有效分配/GC停顿归因，再比较逐包缓冲、DTO/数组及拒绝路径成本，不因GC计数高就直接引入池化。

| 采样范围 | 框架OwnedBytes最小～最大 | 客户端native实际最小～最大 | Player RSS最小～最大MiB |
| --- | --- | --- | --- |
| kcp-idle-4 | 16793092～16793092 | 31760～31760 | 136.61～143.17 |
| kcp-echo-push-1 | 16793092～16797700 | 31824～34601 | 147.44～152.55 |
| kcp-echo-push-2 | 16793092～16797700 | 31760～34601 | 88.03～148.78 |
| wss-idle-4 | 2176～2176 | 0～0 | 153.47～196.84 |
| wss-echo-push-1 | 2176～2176 | 0～0 | 181.27～199.89 |
| wss-echo-push-2 | 2176～2176 | 0～0 | 150.06～199.41 |

OwnedBytes为预约，包含每条KCP连接4MiB native额度；native实际和RSS另列。WSS四条空闲接收头合计2176字节是正常在途预约。上述均为有限频率采样，不是精确高水位；RSS有明显启动/运行波动，本轮不宣称长时无泄漏或给出精确常驻内存结论。服务端native live bytes仍不可用。

负向复验：单连接KCP、rate=2000、pending=8先触发默认每连接128包/秒的服务端输入策略，伴随服务明确记录NetworkProtocolException / `Authenticated input rate exceeded local policy.`，Player出现非预期RPC期限错误并返回1；短窗口复现同一原因。随后仅用已有pending=1参数隔离本地准入拒绝，未改生产限额、期限、协议或加入吞错/重试。负向失败不列为14轮成功样本，也不把失败时的兜底终止当作正常资源释放证据。

相关验证：NetworkKcpFaultTests 10项加NetworkLifecycleTests 11项合并21/21 PlayMode通过、0失败/0跳过，包括Push内再次发包不重复计时、拒绝发送和Push异常收尾；.NET伴随工程locked restore与Release编译0警告/0错误，Python参数越界/样本容量/零速率负向4项返回2。普通StandaloneOSX Release Player脚本43程序集编译通过，Game.Hotfix DLL已重新编译并拷贝；通过Cecil检查普通Game.Hotfix/Game.AOT均不包含Validation入口，三个正式元数据文件哈希未变；网络变更收尾检查时Editor Console无Error/Warning。此前的引擎退出allocator诊断未由此任务归因或修复，不宣称引擎日志问题已解决。场景、编译宏、架构和测试设置恢复，临时证书副本/构建/进程与结果清理；正式YooAsset资源包未更新，发布新Runtime前仍需按正式平台重新生成并构建。

#### 2026-10-15 分配与热点对照

本轮使用同一台 Apple M5 / macOS ARM64、Unity6000.3.19f1 / HybridCLR、Minimal stripping和原native产物；每轮预热2秒、测量10秒，中文128字符。idle4/Echo/Push使用4连接，Echo与Push合计240次/秒；low1使用单连接2次/秒、pending=1，reject1使用单连接2000次/秒、pending=1。Release和Development profiling分别完整构建；Release构建0警告/0错误，Development构建0错误、3条TextMeshPro大方法独立生成C++文件的编译提示。未启用Deep Profiling或自动连接Profiler，WSS沿用已核验既有localhost证书，未改信任。

Release耗时对照，单位毫秒；分配/GC时间计数器在此包均未通过探针，未使用其0值。

| 负载 | 成功/提供 | 背压 | 帧网络P95 | P99 | 最大值 |
| --- | --- | --- | --- | --- | --- |
| kcp-idle4 | 0/0 | 0 | 0.0044 | 0.0054 | 0.0097 |
| kcp-echo4 | 2397/2397 | 0 | 0.3423 | 1.3829 | 1.5401 |
| kcp-push0 | 2396/2396 | 0 | 0.4008 | 1.4938 | 2.3842 |
| kcp-push225 | 2396/2396 | 0 | 1.8970 | 2.3037 | 3.1810 |
| kcp-low1 | 19/19 | 0 | 0.0620 | 0.0892 | 0.1304 |
| kcp-reject1 | 188/19998 | 19810 | 3.4012 | 4.1147 | 4.6988 |
| wss-idle4 | 0/0 | 0 | 0.0072 | 0.0109 | 0.0139 |
| wss-echo4 | 2398/2398 | 0 | 0.4691 | 2.1496 | 2.9674 |
| wss-push0 | 2396/2396 | 0 | 1.4272 | 3.0335 | 3.4022 |
| wss-push225 | 2396/2396 | 0 | 2.3835 | 3.3923 | 3.6576 |
| wss-low1 | 19/19 | 0 | 0.0046 | 0.1686 | 0.1934 |
| wss-reject1 | 596/19998 | 19402 | 3.0282 | 3.4699 | 3.7071 |

以下为独立Development采样包结果，分配包含引擎/夹具/负载；MiB=1048576字节，速率以墙钟测量秒数归一，仅用于同口径对照，存在上述帧边界差异。每轮1MiB探针实测1060850字节，GC时间探针均非零。GC时间是主线程标记，不能代表整个进程GC停顿。

| 负载 | 记录帧槽 | 分配MiB | MiB/s | GC0差值 | 主线程GC标记累计ms | 最大帧GC标记ms |
| --- | --- | --- | --- | --- | --- | --- |
| kcp-idle4 | 596 | 2.05 | 0.21 | 5 | 1.426 | 0.678 |
| kcp-echo4 | 596 | 18.98 | 1.90 | 43 | 16.580 | 1.253 |
| kcp-push0 | 596 | 22.73 | 2.27 | 50 | 10.518 | 1.481 |
| kcp-push225 | 596 | 49.80 | 4.97 | 118 | 60.979 | 1.990 |
| kcp-low1 | 599 | 0.62 | 0.06 | 2 | 1.817 | 1.016 |
| kcp-reject1 | 593 | 74.12 | 7.41 | 186 | 75.468 | 1.074 |
| wss-idle4 | 583 | 2.03 | 0.20 | 5 | 1.958 | 1.143 |
| wss-echo4 | 599 | 258.17 | 25.80 | 476 | 16.273 | 2.400 |
| wss-push0 | 597 | 346.48 | 34.65 | 636 | 13.896 | 2.517 |
| wss-push225 | 597 | 450.94 | 45.03 | 845 | 86.050 | 2.584 |
| wss-low1 | 599 | 2.64 | 0.26 | 6 | 0.770 | 0.770 |
| wss-reject1 | 600 | 135.71 | 13.55 | 308 | 69.506 | 0.993 |

对照与源码核验支持以下结论：

- 225手相对0手快照的主线程增量主要出现在Push派发；其中解码已有单独计时，校验与释放等尚未进一步分离。Release的KCP/WSS PushDecoding累计约240.5/271.2ms，占对应主线程网络总量约50.2%/47.3%；测试handler累计仅约3.2/4.1ms。生成的GomokuMoveData是独立引用DTO，快照还有Moves数组及线上正文；本轮夹具未保留快照，仍观察到随225手增加的分配。不能据此把DTO归池或取消正文校验，业务允许保留DTO的所有权契约仍需保持。
- reject1的Release网络总耗时约99%位于OutboundOutsidePump；源码中RPC在pending额度检查前已进行编码、帧分配和Pending/TaskCompletionSource创建，拒绝后还会释放清零并交付异常。Development采样证实提供请求大量被拒绝时仍有明显分配，但尚未逐项分离异常、编码和对象成本。优先控制调用方在途与提供速率，再独立评估提前检查准入；提前检查会改变无效正文与背压同时出现时的错误先后，不能在未明确契约和回归前直接挪动判断。
- 同负载下WSS的帧分配计数明显高于KCP，且大量CollectionCount没有对应的主线程GC标记。此结果要求继续检查后台WSS/TLS/运行库路径，不能把进程回收次数换算成主线程停顿，也不能将全部差额归到GameFrame的OwnedFrame。框架的WSS固定头验证/正文扩容本身存在分配，但本轮未取得各调用栈的分配字节占比；具体传输库内部热点仍待分配栈验证。
- 保留现有正式Runtime、协议、默认预算和DTO所有权。当前证据已定位负载敏感路径，尚不足以选择池化、零复制或更换调度模型；本轮只补采样与验证工具。桌面短窗口仍不代表最低移动设备P95门禁、生产容量或24h稳定性。

关键负载复测：Release完整快照的KCP帧P95/P99/最大值为1.7969/2.3271/2.7284ms，WSS为2.8022/3.5274/3.9154ms；两轮范围说明耗时有波动，不能只引用较低一次。Development中KCP纯Echo分配19902576→19935634字节、完整快照52219944→52155302字节；WSS纯Echo270705873→270623103字节、完整快照472842463→472329106字节，四组差异均小于0.2%，支持本轮分配差异可重复，但仍不是调用栈级归因。

本轮共30轮实跑（Release14、Development profiling16），全部Player/伴随进程退出0、HarnessLimited=0，测量请求结果计账一致，窗口外Echo正常，两端可追踪资源归零；16轮Development的分配/GC探针均通过。新增Python模式隔离2项、参数越界/容量/零速率4项负向检查均拒绝；伴随.NET工程locked restore/Release编译0警告/0错误。普通StandaloneOSX Release Player脚本43程序集编译通过，Cecil核验Game.AOT/Game.Hotfix均没有Validation类型；Editor编译/Console无Error/Warning。本轮仅改Tests/Player、构建与启动验证脚本和文档，未修改正式网络源码，也未重跑既有故障PlayMode组或做全模块源码审查。引擎既有退出allocator诊断不属于已解决项。场景、defines、架构、EditorSettings及构建引起的字体缓存已恢复；正式Hotfix资源和三个元数据文件哈希与测试前一致，HybridCLR编译输出已恢复为普通Release，未更新YooAsset发布包。临时Player、证书副本、报告与本轮进程已清理。


#### 2026-10-15 WSS 分配栈归因

沿用上述M5、Unity6000.3.19f1、macOS ARM64 Development IL2CPP/HybridCLR/Minimal，重新完整构建0警告/0错误。每轮4连接、中文128字符、预热2秒、测量3秒，非空闲负载合计240次/秒。以下为raw标记窗口的所有已记录线程GC.Alloc，与前面的10秒帧槽统计口径不同。

| 负载 | 成功请求 | GC.Alloc字节 | TLS缓冲重建字节 | 占比 | FrameMemoryBudget直接分配字节 |
| --- | --- | --- | --- | --- | --- |
| KCP Echo | 716 | 5978898 | 0 | 0% | 1218916 |
| WSS Echo | 716 | 81110211 | 71382528 | 88.01% | 1327468 |
| KCP 225手Push | 717 | 15557235 | 0 | 0% | 3441788 |
| WSS 225手Push | 716 | 141415014 | 118730176 | 83.96% | 3677380 |
| WSS Echo复测 | 716 | 81046667 | 71382528 | 88.07% | 与首轮同路径 |
| WSS idle | 0 | 879760 | 527168 | 59.92% | 含控制流量，不作业务逐包值 |

6份raw均记录到1048608字节的已知数组及非空调用栈，窗口分配没有空栈；native栈仍含未解析地址，不声称完整符号化。WSS Echo首轮只有196730字节（约0.24%）缺少可解析托管方法，约98%分配发生在Scripting Threads。核心栈为 `WebSocketFrameTransport.ReceiveAsync → ManagedWebSocket.ReceiveAsyncPrivate/EnsureBufferContainsAsync → Stream.ReadAsync → MobileAuthenticatedStream.StartOperation`；发送对应 `ManagedWebSocket.SendFrameFallbackAsync → Stream.WriteAsync → StartOperation`。直接分配处首个托管帧是StartOperation的MoveNext，内联方法通过同包生成C++交叉核验。

生成的System.cpp确认：MobileAuthenticatedStream的读/写缓冲InitialSize分别为16500/16384；`BufferOffsetSize2.Reset()`每次执行 `new byte[InitialSize]`，StartOperation在读写开始及finally各Reset一次。记录的单次16532/16416字节与数组及本包数组头吻合，这是分配量，不是正文大小或GC后活内存。WSS Echo记录到2880次读数组、1448次写数组，包含控制流量和窗口边界。GameFrame的32字节头验证后再读正文会增加上层Receive调用，但TLS内部缓冲不由OwnedFrame池或ClientWebSocket普通收发缓冲设置控制。

方案比较：池化OwnedFrame只涉及本轮WSS约1.6%～2.6%的直接分配，不能解决主要差额；反射替换TLS私有字段、修改Unity缓存/运行库或按消息大小加特殊分支不采用。接收暂存的后续评估见下节：应用层Receive调用减少不等于TLS读取减少，本次候选方案没有收益，保持正式Runtime和证书校验。

本轮另执行WSS/KCP Echo各一轮不开raw的计数对照，合计8轮Player负载全部Passed、HarnessLimited=0、窗口外Echo正常、双端退出0、可追踪资源归零。一次额外复测在伴随工程构建时遇到MSBuild子节点提前退出，未进入Player；随后串行、关闭构建节点复用后完成最终复测，不计为负载成功。5项参数负向检查均拒绝，已有输出未覆盖；实际Profiler历史保存/恢复及无历史清理通过。普通Release Player脚本43程序集及Hotfix重新编译通过，验证类型排除；Editor最终无Error/Warning。场景、设置和正式DLL/元数据资源保持，测试输出恢复普通Release；临时app/证书副本/raw/报告与自有进程清理。未重跑故障PlayMode、重打Release原生包或验移动设备；完整GC停顿、其他平台、容量/soak仍待验收，本次定向源码核对不是全模块审查。

#### 2026-10-15 WSS 接收暂存评估：不采用

沿用上述M5/macOS ARM64、Unity6000.3.19f1、HybridCLR/Minimal环境，分别完整构建原实现和临时4KiB接收暂存候选的Development profiling Player，两包均0警告/0错误。复用现有入口，4连接、中文128字符、总240次/秒、预热2秒、测量3秒；伴随服务在四轮负载中均使用原实现，只有候选Player的接收流程不同。候选只在固定4KiB范围内暂存，头部及路由校验通过后才分配实际帧长度，保留旧+新预算、EndOfMessage校验、全容量清零和finally释放，不引入池、消息特例或运行库修改。

| 负载/实现 | 成功RPC | Push | raw GC.Alloc字节 | TLS缓冲重建字节 | 总分配/成功RPC（字节，四舍五入） |
| --- | --- | --- | --- | --- | --- |
| Echo / 原实现 | 716 | 0 | 81121184 | 71382528 | 113298 |
| Echo / 4KiB暂存 | 719 | 0 | 84252845 | 71679408 | 117181 |
| 225手Push / 原实现 | 716 | 716 | 141386114 | 118730176 | 197467 |
| 225手Push / 4KiB暂存 | 716 | 716 | 146928483 | 118730176 | 205207 |

raw包含引擎、夹具、控制流量及墙钟窗口边界；请求数有差异，因此同时列出成功数与归一结果，不将全部字节称为网络独占成本或精确业务逐包成本。Echo/完整快照归一总分配分别增加约3.43%/3.92%；Echo归一TLS分配约99696→99693字节，差异约-0.003%，完整快照TLS分配完全相同。读数组次数为2880→2892、5744→5744，写数组为1448→1454、1448→1448，与请求/控制数量相符，未获得主要TLS分配下降。四连接等待下一帧的预约由2176增加到18432字节，即每连接544→4608；框架直接分配分别1327468→4287543、3677380→9529540字节。

原因通过同包只读生成C++核对：`ManagedWebSocket`构造时已有125字节内部接收缓冲；`EnsureBufferContainsAsync`向剩余缓冲读入WebSocket头和部分应用正文，`ReceiveAsyncPrivate`先消费内部数据，再按用户缓冲及剩余payload读取TLS。当前32字节GFNP头已经在这份内部数据中；合并应用层头/正文Receive调用并没有消除取得剩余正文的TLS读取。每条WebSocket消息本身的读取边界及TLS操作重建缓冲仍存在，连接复用暂存也不能仅凭上层调用次数推断收益。

四轮均Passed、HarnessLimited=0、请求结果一致，窗口外Echo正常，Player/伴随进程退出0且可追踪资源归零；四份raw的已知数组带栈校验均为1048608字节，没有空分配栈，仍存在未解析native地址。候选源码的独立.NET框架回归13/13通过，但这不代表完整分片/故障/平台验收。候选已因分配和预约成本增加被淘汰，正式接收源码逐字节恢复，不保留新接口、预算或测试分支；没有继续构建Release性能对照或扩大故障矩阵，不引用Development耗时证明Release收益。恢复后的ServerReceiveBudgetTests/ConnectionRaceTests共33/33通过、零跳过，Release编译无警告/错误；普通Release脚本/Hotfix重新编译，输出43程序集，Cecil确认Game.AOT/Game.Hotfix的Validation类型均为0。Editor最终Console无Error/Warning，Launch、ProjectSettings、正式DLL/元数据资源哈希与本轮开始一致，defines为空、Development=false；Profiler历史恢复，临时Player/raw/报告/证书副本和密码及自有进程清理。当前任务只更新上述评估和对应进度文档。WSS暂存不再作为当前必做优化；其他Unity版本或传输方案需有新证据再评估。

<a id="network-hotupdate-dto"></a>

### 安装包发布后新增 DTO / 路由

2026-10-15 已通过 macOS ARM64 Release / Minimal stripping 的实际包后热更验证。使用正式 Launch、HostPlayMode 和 ResourceLoadManager；更新在重新启动旧 Player 时加载，不是在运行中的进程里替换已经加载的程序集。

复验必须保持先后顺序，避免把重新打包误认为热更新成功：

1. 新 DTO 和验证组件尚不存在时，执行正式 HybridCLR 生成、Release IL2CPP 构建，拷贝最终裁剪元数据，构建基线资源并签名。先启动旧包，确认基线清单和 GameEntry 就绪，再冻结整包文件哈希及三个元数据文件。
2. 原生构建完成后才加入临时 `.proto`，通过现有 NetworkProtocol 生成 DTO/catalog。临时 Hotfix 组件挂在更新后的 GameEntry prefab，等待正式入口就绪后停止业务流程，再借用已初始化的 NetworkRuntime 验证；Launch/GameEntry 源码不加入测试分支。
3. 仅执行 `CompileAndCopyHotUpdateDlls` 和 YooAsset 资源构建，发布新版本文件、清单与资源到本机 HTTP 服务。此阶段不再执行 GenerateAll、AOT 桥接生成、BuildPlayer 或替换补充元数据，也不改旧 `.app` 的 StreamingAssets 或重新签名。
4. 用同一个旧包重新启动，经 Host 模式下载更新；服务端编译同源新 catalog。验证双向正文、可靠 Push/RPC 顺序、主线程、Close/Dispose/Shutdown，并比对运行前后整包及元数据哈希。结束后恢复协议、prefab、DLL 资源、场景与设置，删除临时组件/协议、构建、缓存和服务。

本轮临时协议包含全新 HotupdateNode、HotupdatePayload、HotupdateRequest、HotupdateResponse、HotupdatePush，以及 RPC1920 / Push1921；临时 catalog 为16条路由，正式协议恢复为14条。三轮请求覆盖中文/Unicode、嵌套 DTO/DTO 数组、uint 数组及最大值、二进制数组、空数组、可空引用有值/null。独立 .NET 服务逐字段校验请求后编码 Push 和响应；双方 catalog 指纹一致。

HTTP 日志和 YooAsset 构建报告对应：更新仅下载 Game.Hotfix 的96138字节 bundle 和 GameEntry prefab 的2552字节 bundle，另请求新版 version/hash/manifest。旧 `.app` 的53个文件内容哈希全部不变；三个元数据保持原安装包构建的 mscorlib、MemoryPack.Core、UniTask，未增加其他程序集。Hotfix DLL SHA-256 从 `504f29115d91d20c0403ef140526a5120e42b02fbfe293e8303bc5f305a6bf2a` 变为 `2e0387048d1eeba44685f31be1e028c7e7f4f42ad929318d801a4d39ee0988ac`，新 catalog 为 `a88d233a431ce9d6404efad5625ab68e38c185829a49b51c0fca8e2cfa94128d`。这些指纹属于已移除的测试夹具，不是正式发布协议。

以下按实际执行范围记录，各次验证独立，Editor 与 Player 脚本编译不能替代 IL2CPP 或真机验证：

| 日期/实际范围 | 结果 | 不证明的部分 |
| --- | --- | --- |
| 2026-10-15 旧安装包新增 DTO/路由，macOS ARM64 Release IL2CPP / Unity6000.3.19f1 / HybridCLR / SDK10.0.401 | 基线原生构建0警告/0错误，Minimal stripping，基线 Host 启动通过；之后才生成五个新DTO和两条新路由，仅重新编译Hotfix/构建资源。正式Launch从本机HTTP获取hotupdate-new-dto，旧包签名与全部53个文件哈希保持不变，补充元数据仍为原构建的三个文件。真实WSS正常证书校验下，三轮RPC和三次Push全部通过，主线程顺序为push1/rpc1/push2/rpc2/push3/rpc3；Close后pending/deadline/接收与owned bytes归零，Dispose与Runtime Shutdown完成。Player和.NET服务均退出0；服务端Close reason1、Error=null、owned bytes=0、无Warning/Error；恢复源码后的Editor编译/Console检查通过 | 临时组件只负责此次自动验收，未加入正式业务或保留测试路由；账号持久化关闭，未重验登录/五子棋/KCP。仅本机HTTP资源服务与localhost WSS，不代表公网CDN/TLS资源发布、移动设备/其他平台或裁剪等级、进程内DLL替换、系统wire/native ABI更新、弱网/容量/soak。Player仍有退出IL2CPP allocator诊断，未归因或宣称已解决；仓库StreamingAssets未更新 |
| 2026-10-15 正式 Launch 资源链，macOS ARM64 Release IL2CPP / Unity6000.3.19f1 / HybridCLR / SDK10.0.401 | 最终完整 Release 构建0警告/0错误，Minimal stripping，签名验证通过；实际 Launch 经 OfflinePlayMode 加载全新 YooAsset 包、三个同构建裁剪元数据及 Game.Hotfix，再由 prefab Awake 进入业务。真实 Player 通过系统输入完成 WSS 登录→Main→五子棋→加入.NET对端创建的房间→准备，双方交替9手，权威快照 version15、Finished/BlackVictory/FiveInRow、两名成员均在；Player主动离房后version16只剩对端，对端离房关闭房间。最终Player无托管异常并输出GameFrame Shutdown完成，伴随端退出0、owned bytes=0，最终整局服务端无新增Warning/Error。Editor编译/Console无错误警告，DLL逐字节匹配本次Release来源；临时诊断源码、prefab、字体、架构/设置恢复，测试进程/数据库/证书副本和构建清理 | 初轮仅mscorlib在LoginResponse反序列化失败；加入MemoryPack.Core后落子音效的Forget<AudioPlayResult>仍缺少UniTask泛型实现，最终只补这两个元数据资源。实跑使用一个Unity Player和一个.NET对端，账号持久化显式关闭以保护已有凭据，正常证书校验未改；UI操作结果由协议快照和日志核验，未做截图视觉验收。未验Host/CDN更新、账号持久化、两个Unity Player、新DTO包后热更、其他架构/裁剪等级/平台、移动真机或容量/soak；本轮未重验KCP。引擎日志仍有启动sandbox extension诊断与退出IL2CPP allocator诊断，未归因或宣称已解决。仓库StreamingAssets保留原产物，发布前须重新构建 |
| 2026-10-15 独立 macOS ARM64 IL2CPP Player，Unity6000.3.19f1 / HybridCLR / SDK10.0.401 | 最终 ARM64 Development 完整 IL2CPP 构建成功，Minimal stripping，11个同平台裁剪 AOT 元数据 DLL，构建0警告/0错误；实际加载本次 Game.Hotfix.dll，七组检查全部通过：DTLS/Hello/native分配、UTF-8 Echo/主线程、精确token取消/迟到响应、RPC超时/迟到响应、在途Close/队列释放、同客户端新grant/generation1→2、Dispose/Shutdown。Player与.NET伴随进程退出0，CompanionReleased=true，服务端Close reason1且无Error，资源与转发计账通过。包内native SHA与已核验安装产物一致，签名验证通过。共享fixed缓冲区最终源码.NET KcpLoopbackTests 28/28、Unity断流恢复1项+生命周期11项共12/12，均零跳过；场景/架构/测试环境/设置恢复，临时产物清理 | 初轮旧ByValArray声明在实际Player的Marshal.OffsetOf校验失败；解析global-metadata.dat及生成C++确认字段封送大小0而C++数组布局正确。Pack=4尝试无效并撤回，最终改共享fixed byte声明，保留全部结构校验，无平台分支/校验拆分或跳过。未改native产物、协议或默认期限。仅实跑ARM64/Development/Minimal；不代表x86_64、Release/其他裁剪等级、正式Launch/YooAsset、新DTO包后热更、WSS、移动设备、其他平台、Player弱网/后台/IPv6或容量/soak；服务端native live bytes仍未直接测量 |
| 2026-10-15 Unity公开客户端故障验证，Unity6000.3.19f1/macOS ARM64 | 最终源码Editor编译完成，无编译错误/警告；新增NetworkKcpFaultTests 9项与既有NetworkLifecycleTests 11项最终合并20/20通过、0失败、0跳过，约69.37秒；9个独立临时.NET伴随进程均确认退出0，两端资源与转发计账通过。先前整组20/20及3秒断流冒烟、补强双向控制流量后的暂停单项1/1不重复计为新用例；收尾误删命名空间造成一次编译失败及0用例的初始化超时，已恢复引用并以上述最终整组复验。伴随测试工程locked restore及Release编译0警告/0错误，新C#空白与git diff --check通过；Launch场景、EditorSettings及临时环境变量恢复，服务进程/目录/凭据清理 | 正式Runtime/协议/默认期限及插件未改。现有macOS Release ABI2插件SHA256=`0cffd5874206055c777a52e1b5ab5875074e7bc17ed108a8e3d26b038249befb`、build ID=`833aea2b337679b5a97e60691d3e7f9566710310b6739b34055b62436e1c876a`与双清单/维护源码指纹只读核验通过；服务端native live bytes仍不可直接测量。生命周期组两条Driver故意失效日志均由既有LogAssert.Expect接收；其余无非预期Console错误。未执行Player脚本编译、IL2CPP、真机/其他平台、系统后台挂起/换网/IPv6、容量或长时soak |
| 2026-10-15 KCP关闭快照与取消入队，Unity6000.3.19f1/macOS | 最终源码Editor编译完成，无Console Error；NetworkLifecycleTests PlayMode11/11，独立临时local-kcp-dtls服务下NativeUdpHelloEchoAndRelease PlayMode1/1，均零跳过；新增Close后Waitsnd=0断言，native/托管预约释放；.NET的未ACK销毁和三种入口取消/非取消共7项red/green证据见服务端说明 | 保留已有native产物；临时随机grant、服务进程、测试环境变量、Launch场景和EditorSettings恢复/清理。此次未执行Player脚本编译、IL2CPP或真机；Unity用例是直连UDP/DTLS，弱网注入只在.NET测试工具执行，不能当作Unity弱网/其他平台验收 |
| 2026-10-14 RPC/子预算诊断，Unity6000.3.19f1/macOS | Editor 编译无 Console 错误；网络 Buffer/Protocol/MemoryPack EditMode147/147，生命周期 PlayMode11/11；独立本地 KCP 服务下真实 UDP/DTLS PlayMode1/1，覆盖 Hello/Echo、公开 RPC 汇总、Close 后保留统计及帧/native 预约归零；StandaloneOSX Player 脚本43程序集通过 | 首次组合 PlayMode 出现 TearDown 空引用，域重载后生命周期复验通过；KCP 首轮无凭据，最终使用临时随机 grant 实跑通过。环境变量、Launch 场景与记录的 EditorSettings 恢复，临时进程/凭据/Player 输出清理；未重建插件、打 IL2CPP 包或验新热更 DTO/其他平台/设备/弱网/容量/soak。RPC 汇总只涵盖成功解码样本，不是全部发送、Push 或每帧成本 |
| 2026-10-13 登录成功切 Main，Unity6000.3.19f1/macOS | Editor 编译通过；账号、资源、场景操作、GameRuntime、网络生命周期及根节点 PlayMode 73/73，零跳过；覆盖 Main 激活/相机 Stack、断线后复用 Main、加载中认证失效、主动 Stop 等待加载、进入失败后显式再次进入且不重复 Login；实际 Launch 按钮登录后仅 Main 场景加载，GameEntry/会话/UI 根保留，Stack 恰好一个 UI 相机；保持登录停止播放无新增 Console 错误；最终 StandaloneOSX Player 脚本 43 程序集通过 | 首轮停止用例发现导航任务登记晚于加载开始回调，改为使用 AsyncLazy 先登记再执行后整组通过；独立临时 WSS/PostgreSQL 联调，恢复测试端点、场景、EditorSettings、环境和字体变化并清理临时服务。未构建正式资源包、IL2CPP 或验证真机/其他平台 |
| 2026-10-13 登录后退出顺序修复，Unity6000.3.19f1/macOS | Editor 编译通过；账号、资源、GameRuntime、网络生命周期与根节点 PlayMode 55/55，零跳过；新增真实登录用例覆盖正常断线回登录、UI 先关闭及框架先关闭且 UI 尚存两种顺序；实际 Launch 从按钮登录进入五子棋后停止播放，Console 无新增错误；StandaloneOSX Player 脚本 43 程序集通过 | 使用独立临时 WSS/PostgreSQL，不修改玩家数据库；验证后恢复端点、场景、EditorSettings、环境变量及字体变化并清理临时服务。首轮两个新用例误把普通初始化日志作为未预期日志，修正测试日志检查后整组通过。未构建 IL2CPP 或验证真机/其他平台 |
| 2026-10-13 Editor 开发证书配置，Unity6000.3.19f1/macOS | Editor 编译及实际菜单对应配置成功；播放初始化自动追加已核验 localhost 公开证书，实际 WSS TLS/Hello 成功，关闭后 ownedBytes=0；默认验证回调未变，错误 TLS 目标仍被拒绝；正常 Domain Reload 与关闭 Domain Reload 均检查自动应用/停止恢复；无效路径和指纹更换拒绝且保留原配置；StandaloneOSX Player 脚本 43 程序集通过，GameFrame.Editor 排除 | 只验证本机 Mono TLS，不是其他 Editor 平台、IL2CPP 或真机验收；握手验证未提交玩家账号，不修改现有服务器/数据库；本机证书配置按用户要求保留 |
| 2026-10-13 登录接入，Unity6000.3.19f1/macOS | Editor 编译通过；真实 Game.Server/WSS/PG 登录测试 6/6、生命周期与资源回归 32/32、UI/根节点 14/14，均零跳过；实际 Launch 首次建号→主界面、服务停止→清空用户并回到登录、保持登录时停止播放均通过，Console 无新增错误，Camera Stack 恰好一个 UI 相机；最终 StandaloneOSX Player 脚本 43 程序集通过 | 首轮 3 项受 Unity 不信任已有 localhost 开发证书阻断；测试临时设置已核验公开证书为进程信任锚后复验通过，证书校验未关闭。发现并修正退出时 Timer 驱动误报，运行中禁用仍使等待失败；验证后恢复场景/EditorSettings、字体自动生成内容、证书与环境设置，停止独立临时服务/数据库。未构建 IL2CPP、正式热更包或验证真机/其他平台 |
| 2026-10-13 协议生成入口，Unity6000.3.19f1/macOS | Editor 编译通过；既有协议/MemoryPack144项、新工具3项分别通过；Generate/Check 菜单均实跑成功、运行时菜单禁用；StandaloneOSX Player 脚本43程序集通过，未包含 Editor 工具 | 进程测试覆盖空格路径、大量双管道输出和非零退出；未实跑 Windows/Linux 菜单，未构建 IL2CPP；Launch 场景及 EditorSettings 保持原值 |
| 2026-10-13 Unity6000.3.19f1 | Editor 编译；网络协议/MemoryPack EditMode144/144；生命周期 PlayMode11/11 | 首次 PlayMode 在 TearDown/运行器空引用中断，恢复状态后单项与整组复跑通过；未改运行器或削弱断言 |
| 2026-10-12 重启后 Unity Mono | 新 mbedTLS4.2/ABI2 实际 UDP Hello/Echo/关闭通过，自有缓冲与 native 预留归零 | 非 IL2CPP、移动真机或 Windows 执行 |
| 2026-10-12 StandaloneOSX | Player 脚本编译 43 程序集通过 | 非最终 native 链接、裁剪或 Player 运行 |

待验收：其他架构/裁剪等级的正式 Launch/Release IL2CPP 与新 DTO 包后热更、公网资源/CDN发布、移动设备/模拟器、Windows/Linux 平台实跑、Player IPv6/代理/后台/换网/弱网、完整元数据与尚未覆盖的建连/关闭/后台和失败细分诊断、分配剩余细项与完整GC停顿归因、目标设备性能容量/24h soak。macOS ARM64 Release/Minimal 的正式启动与包后新增协议已有上述实跑证据；WebGL WSS 和世界业务延后，不以当前结果声称全部平台完成。


## 五子棋房间（2026-10-14）

登录 → Main → 五子棋入口。创建者看到 8 位号码，另一位玩家输入号码加入；双方准备后房主开始。联网悔棋 / 重开暂不提供，本地双人保留现有操作。结果未确认时点击“查询状态”，退出成功返回入口；断线不恢复席位。

相关 PlayMode 验证 29/29，使用独立临时 PostgreSQL / localhost WSS / 公开测试证书，覆盖账号、本地棋盘 / 主面板、两个客户端联机模型与 UI。Editor 编译与 Prefab 13 个准确绑定已核验。Player 脚本、正式 DLL / 资源包、实际 IL2CPP / 新 DTO 热更可达性分别验收，不以 Editor 结果代替；详见 [五子棋](Gomoku.md#10-已实施与验证边界)。

StandaloneOSX Release Player 脚本和 Hotfix DLL 已编译，`Assets/Config/Code/Game.Hotfix.dll.bytes` 已更新；资源包输出 `Bundles/StandaloneOSX/DefaultPackage/gomoku-20261014-validated`，构建验证通过，尚未部署。协议相关 EditMode 162/162。

协议精简后的复验：当前 14 条可靠路由；协议 EditMode 135/135，相关 PlayMode 合计 48 项通过（含真实 WSS 房间与生产协议 KCP / DTLS Echo）。StandaloneOSX Release Player 脚本与 Hotfix DLL 已重新编译；前述资源包仍是精简前产物，发布时需重新构建。具体范围与平台边界见 [五子棋验证](Gomoku.md#10-已实施与验证边界)。

2026-10-14 设置与账号记忆验证：Unity 6000.3.19f1/macOS Editor 编译通过；SettingsPanelTests、MainPanelTests、RememberedLoginStoreTests、RememberedLoginIntegrationTests、GameLoginSessionTests、ResourceLoadManagerTests 共 38/38 PlayMode 通过，零跳过。使用独立临时 WSS/PostgreSQL 与显式公开证书信任，覆盖重新创建客户端后免密登录、连续令牌轮换、恢复拒绝后手动密码登录、切换账号、注销、SQLite 元数据及 macOS Keychain 实际读写删除。最初未配置环境的联调用例跳过，以及临时服务尚未监听造成的失败，均在服务就绪后重新执行通过。StandaloneOSX Release Player 脚本编译通过（含 Game.Hotfix 与 GameFrame.Sqlite）；Android Java 桥编译、iOS Objective-C++ 桥 SDK 语法检查通过。移动端/Windows 安全存储实际运行、完整 IL2CPP 链接和真机验证尚未执行。

2026-10-14 登录流程优化验证：Unity 6000.3.19f1/macOS Editor 编译通过；上述六类相关 PlayMode 分批覆盖 47 个不同用例（GameLoginSession 12、MainPanel 2、SettingsPanel 1、RememberedLoginStore 7、RememberedLoginIntegration 9、ResourceLoadManager 16），最终全部通过、零跳过。新增覆盖建连失败保留免密及面板重试入口、凭据替换/取消/删除失败、密码登录及恢复登录保存失败仍保留认证、SQLite 保存失败、注销本地失败仍远端撤销、取消远端仍清理本地、双失败保留同一首因异常。使用独立临时 WSS/PostgreSQL，macOS Keychain 读写删除通过。首轮 7 个启动用例缺少临时证书信任，测试夹具补齐 GF_LOGIN_TEST_CERTIFICATE 后通过；双失败用例按 Unity 日志的内层异常格式修正预期后通过。StandaloneOSX Release Player 脚本编译通过，共 43 个程序集，包含 Game.Hotfix 与 GameFrame.Sqlite。临时服务、数据库、证书与编译产物已清理，场景、编辑器设置和测试造成的字体缓存变化已恢复；未更新正式热更 DLL/资源包，未执行完整 IL2CPP、Windows 或移动端实跑。


2026-10-14 登录失败流程与凭据标识精简验证：Unity 6000.3.19f1/macOS Editor 编译通过；账号、记忆存储/联调、Main 与资源导航共 53 个不同 PlayMode 用例分两批通过（50+3），0 失败、0 跳过。覆盖移动数据库与显式命名空间隔离、顶号等待在途读取后条件清理、迟到关闭保护新令牌、保存响应期间被顶号、普通断线保留恢复资格、记忆失败单次点击进入并传递提示、Main 缓存重开清除旧提示，以及既有恢复/注销/切场回归。使用独立临时 PostgreSQL、真实 Game.Server/WSS 和临时公开证书信任，macOS Keychain 既有用例通过。.NET ConnectionRaceTests 30/30，包含关闭原因 2/4/7/未知值的结构化传播。StandaloneOSX Player 脚本编译 43 个程序集通过；验证结束恢复 Launch 场景、EditorSettings、测试环境变量和字体图集，并清理本次临时服务、数据库、证书和编译产物。未更新正式热更 DLL/资源包，未执行 IL2CPP 或其他平台/真机验证。
