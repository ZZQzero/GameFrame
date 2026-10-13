# 客户端网络使用说明

客户端 API 在 `GameFrame.Network`，游戏使用生成的 `Game.Hotfix.Network.Generated` 路由和 `Game.Hotfix.Network.Protocol` DTO。开发/线程/错误与所有权规则见 [客户端网络开发规范](ClientNetworkDevelopment.md)；本地服务、PostgreSQL、证书与测试凭据准备见 [服务端使用说明](DotNetUsage.md)。本文示例位于已有 Hotfix 主线程异步业务方法内，变量由业务提供，不表示当前已实现 GameNetworkSession 或世界服务。

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
using Game.Hotfix.Network.Generated;
using Game.Hotfix.Network.Protocol;

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

登录按钮校验账号（1～64 个 UTF-16 字符，无首尾空白/控制字符）、密码（8～128，不裁剪），禁用控件并提交一次 Login。已有账号验密，不存在则建号并认证；错误密码显示拒绝，失败收尾清空密码并恢复输入。成功后通过 GameScene 以 Single 模式加载、激活 Main，绑定 Main 的主相机与持久 UI 相机，再关闭登录面板并打开现有五子棋主界面，账号连接继续存活；五子棋仍是本地玩法，没有新增服务器对局。

进入 Main 失败时登录面板保留，已认证会话仍有效；账号和密码输入保持禁用，玩家再次点击确定只进入主场景，不重复认证。断线后清空认证并重新打开登录面板；Main 已加载时重新登录复用该场景。加载期间认证失效或业务停止，迟到的场景完成结果不打开五子棋；主动 StopAsync 等待已发起的主场景导航收尾。

会话是普通实例，由入口创建、注入、释放，后续业务接收同一个实例：

```csharp
await GameUI.Push<LoginPanel, LoginPanelArgs>(new LoginPanelArgs(session, onLoggedIn));
GameUser user = session.CurrentUser; // 未登录、断线或到期时为 null。
if (user == null)
{
    return;
}
ulong accountId = user.AccountId;
// session.LobbyClient 是已认证客户端的借用引用，业务不关闭或释放它。
```

GameUser 只包含账号 ID、输入账号名、认证 epoch 和单调到期时间；私有 sessionId/token 不暴露给 UI，不持久化密码或凭据。断线、顶号或显式注销清空认证并回到登录，无自动重连或 Resume。`await session.LogoutAsync(token)` 请求远端注销并清理本地状态；生命周期结束使用 `await session.DisposeAsync()`。运行中关闭整个应用先 `await entry.StopAsync()`，再关闭 GameUI、GameRuntime 和资源服务。

停止播放或进程退出时，Unity 不保证 Launch 和 GameEntry 的清理顺序。此时网络关闭仍清空认证；入口仅在业务就绪、GameRuntime 运行且 GameUI 已初始化时执行界面切换，异步打开完成后再次检查，避免在 UI 销毁后返回登录。

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

示例 timeout 为单次 RPC 上限，服务器登录硬期限不会因新 RPC 重置。GameLoginSession 先等待 Connect（上限 10 秒），随后立即提交 Login（上限 5 秒），校验成功载荷/TTL；失败、超时或取消先 await Close，不能认为服务端一定未建号或未认证，清理失败保留主因。完整世界 GameNetworkSession 仍延后。

登录后用 local Echo 确认实际往返：

```csharp
EchoResponse echo = await client.RequestAsync(
    GameNetRoutes.Echo,
    new EchoRequest { Text = "hello", DelayMs = 0 },
    TimeSpan.FromSeconds(5),
    cancellationToken);
```

底层 Resume RPC 已提供，当前 GameLoginSession 不暴露恢复操作。后续显式恢复时先完成旧客户端 Close，再连接 WSS，用保存的 accountSessionId/resumeToken 调用 GameNetRoutes.ResumeLogin。检查 ResultCode 及与 Login 相同的成功载荷/期限，成功原子替换 token；旧 token 只成功一次，响应丢失不自动复用或密码登录。服务器重启后 Resume 失效，玩家显式重新 Login。

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

RPC 期限从本地 admission 起算，后台 deadline 不会因主线程卡帧延长；期限内保留响应后，到主线程完成前取消仍可赢。非零 StatusCode 抛 NetworkRemoteException，未知码保留；超时抛 NetworkRequestTimeoutException，本地准入或队列容量不足抛 NetworkBackpressureException；服务端过载 status1 仍抛 NetworkRemoteException。超时/取消/断线后的业务结果可能未知，不自动重发。

未来世界 Push 使用示例：

```csharp
using IDisposable subscription = client.Subscribe(
    GameNetRoutes.WorldSnapshotReliable,
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

实际 local 没有世界 Push，此例用于接入自己实现的服务端。只有订阅持有期间有效；将句柄保存到会话或既有 OpenScope/LifetimeScope，异步方法返回后需继续监听时不能使用局部 using。Push 抛错 Fault 当前连接，后续业务丢弃；观察者抛错只停止该批并记录，不改网络首因。未订阅 Push 也校验正文，合法未订阅消息不创建 DTO。

WSS 不支持不可靠 API，不能把 WorldSnapshot/MoveInput 自动改通道；未来 WSS 世界用 WorldSnapshotReliable/MoveInputReliable，KCP 用 WorldSnapshot/MoveInput，两组独立 opcode 共用 DTO。

<a id="原生插件安装"></a>

## 4. 原生插件安装

所有构建命令、平台参数与依赖准备集中在 [服务端使用说明原生章节](DotNetUsage.md#原生构建与部署)，双方共用同一 Native/Kcp 源码，不复制到 DotNet 或 Unity 第二份。Unity 安装省略--server-output：

```sh
python3 Native/Kcp/build/install.py Tools/Artifacts/NetworkNative/macos-release
```

安装到 `Unity/Assets/Plugins/GameFrame/Native/Kcp/<platform>`，库名 gameframe_kcp，iOS P/Invoke 用__Internal；平台选择和 ABI 以安装器为准。已加载库替换后重启 Unity，不用刷新缓存冒充新二进制。缺库/错误架构/ABI 明确失败，不回退 WSS。

Simulator archive 独立构建并默认禁用，测试模拟器时显式选择 simulator、禁用 device archive，避免重复链接。Android/iOS 产物存在不代表 IL2CPP 链接/真机通过；新 native/ABI/架构需要新安装包，不能用 Hotfix DLL 替换 native 代码。

## 5. KCP/DTLS 调用

从可信配置取得 UDP 端点，业务或受控测试提供票据；当前大厅 RequestWorldGrant 返回 WorldUnavailable，不能执行真实取票→世界业务流程。底层联调先启动服务端 local-kcp-dtls，按服务端说明共享短时随机凭据，在其 30 秒内完成连接：

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

未来不可靠输入调用 `client.TrySendUnreliable(GameNetRoutes.MoveInput, input)`，返回 false 表示本地拒绝；并不保证对端收到。当前 local 未安装 MoveInput handler，这个调用只展示 API，不作为联调成功判据。

## 6. 关闭与诊断

会话结束先停止业务/恢复任务并取消请求、释放订阅，再：

```csharp
await client.DisposeAsync();
```

需要复用同一客户端时 await CloseAsync，再显式 Connect。整个框架由上层生命周期执行 `await GameRuntime.ShutdownAsync()`，不能在普通面板关闭时销毁网络宿主或 root；运行中结束先让网络释放，之后释放 Timer/root/资源。故障后不直接重连，必须先 Close。

主线程调用 GetMetrics，关注 State/Generation、OwnedBytes/PeakReservedBytes、pending/堆/队列条数、timeout/cancel/late、不可靠丢弃、发送队龄告警、应用帧/正文 bytes 与 PingRoundTripMilliseconds。Kcp 快照区分 native 预留与实际/峰值、waitsnd/srtt/rto/窗口、明文/密文队列、实际 socket 字节。TimeoutRetransmits 只含超时重传，不含 fast retransmit；DtlsDiscardDetailsAvailable=false 表示细分坏记录不可用。

OwnedBytes 不是 RSS、所有 CLR 对象或 TLS/内核内存；每帧/每 pending512 字节是保守 reservation。当前无空闲缓冲池，MaxIdlePoolBytes 实际 0，仍有逐包分配。KCP 请求 socket 收/发各 256KiB 并报告系统实际值，ClientWebSocket 不开放底层 buffer 设置，不能声称其设置成功。

## 7. 协议与依赖维护

修改协议直接编辑仓库根 Protocol/**/*.proto，按 [统一生成步骤](DotNetUsage.md#协议生成与验证) 重新生成两个正式 C#文件；生成目录不放手写代码。客户端.NET 不能只更新一端。manifest/hash/vector 在 Protocol/Generated 仅用于开发验证，不进入 Unity 资源，运行时只用 catalog 内嵌指纹。

Unity 编辑模式下使用 `Tools/GameFrame/Network/Generate Messages` 生成，使用 `Check Messages` 只读检查。这两个菜单异步启动 `DotNet/NetworkProtocol`，包含 `--artifacts`，同步处理正式 C# 与开发辅助产物；Console 显示成功结果或完整退出码/stdout/stderr。执行期间禁止重复启动，暂缓自动刷新和域重载；进程退出后恢复，生成成功才显式刷新资产。字节未变化的生成文件不重写，已有 `.meta`/GUID 保留。

工具需要 `DotNet/global.json` 锁定的 .NET SDK 10.0.401，与 Luban 的 .NET 8 分开。自动从 `DOTNET_ROOT`、Unity 进程的 PATH、平台常见安装目录及用户 `.dotnet` 目录定位 dotnet。Unity Hub/Finder 未继承终端 PATH 时，可通过 `Tools/GameFrame/Network/Set .NET Executable` 选择 dotnet 可执行文件；路径保存在本机 EditorPrefs，不写入项目。显式配置无效或 SDK 缺失时报告失败，不自动切换版本或重试。无需向 Unity 拷贝生成器 DLL 或 Roslyn 依赖。

Unity 依赖固定 MemoryPack1.21.4，NuGetForUnity4.5.0 与 MemoryPack.Unity Git tag 在 Packages/manifest.json，包和 meta 在 Assets/Packages，packages.config/NuGet.config 及 UPM 锁同步维护。查看用 NuGet > Manage NuGet Packages，恢复用 NuGet > Restore Packages；保留 Generator 的 RoslynAnalyzer 标签和导入排除设置，不将.NET10 运行时 DLL 换入 Unity。

<a id="验证与现有证据"></a>

## 8. 验证与现有证据

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

以下按实际执行范围记录，各次验证独立，Editor 与 Player 脚本编译不能替代 IL2CPP 或真机验证：

| 日期/实际范围 | 结果 | 不证明的部分 |
| --- | --- | --- |
| 2026-10-13 登录成功切 Main，Unity6000.3.19f1/macOS | Editor 编译通过；账号、资源、场景操作、GameRuntime、网络生命周期及根节点 PlayMode 73/73，零跳过；覆盖 Main 激活/相机 Stack、断线后复用 Main、加载中认证失效、主动 Stop 等待加载、进入失败后显式再次进入且不重复 Login；实际 Launch 按钮登录后仅 Main 场景加载，GameEntry/会话/UI 根保留，Stack 恰好一个 UI 相机；保持登录停止播放无新增 Console 错误；最终 StandaloneOSX Player 脚本 43 程序集通过 | 首轮停止用例发现导航任务登记晚于加载开始回调，改为使用 AsyncLazy 先登记再执行后整组通过；独立临时 WSS/PostgreSQL 联调，恢复测试端点、场景、EditorSettings、环境和字体变化并清理临时服务。未构建正式资源包、IL2CPP 或验证真机/其他平台 |
| 2026-10-13 登录后退出顺序修复，Unity6000.3.19f1/macOS | Editor 编译通过；账号、资源、GameRuntime、网络生命周期与根节点 PlayMode 55/55，零跳过；新增真实登录用例覆盖正常断线回登录、UI 先关闭及框架先关闭且 UI 尚存两种顺序；实际 Launch 从按钮登录进入五子棋后停止播放，Console 无新增错误；StandaloneOSX Player 脚本 43 程序集通过 | 使用独立临时 WSS/PostgreSQL，不修改玩家数据库；验证后恢复端点、场景、EditorSettings、环境变量及字体变化并清理临时服务。首轮两个新用例误把普通初始化日志作为未预期日志，修正测试日志检查后整组通过。未构建 IL2CPP 或验证真机/其他平台 |
| 2026-10-13 Editor 开发证书配置，Unity6000.3.19f1/macOS | Editor 编译及实际菜单对应配置成功；播放初始化自动追加已核验 localhost 公开证书，实际 WSS TLS/Hello 成功，关闭后 ownedBytes=0；默认验证回调未变，错误 TLS 目标仍被拒绝；正常 Domain Reload 与关闭 Domain Reload 均检查自动应用/停止恢复；无效路径和指纹更换拒绝且保留原配置；StandaloneOSX Player 脚本 43 程序集通过，GameFrame.Editor 排除 | 只验证本机 Mono TLS，不是其他 Editor 平台、IL2CPP 或真机验收；握手验证未提交玩家账号，不修改现有服务器/数据库；本机证书配置按用户要求保留 |
| 2026-10-13 登录接入，Unity6000.3.19f1/macOS | Editor 编译通过；真实 Game.Server/WSS/PG 登录测试 6/6、生命周期与资源回归 32/32、UI/根节点 14/14，均零跳过；实际 Launch 首次建号→主界面、服务停止→清空用户并回到登录、保持登录时停止播放均通过，Console 无新增错误，Camera Stack 恰好一个 UI 相机；最终 StandaloneOSX Player 脚本 43 程序集通过 | 首轮 3 项受 Unity 不信任已有 localhost 开发证书阻断；测试临时设置已核验公开证书为进程信任锚后复验通过，证书校验未关闭。发现并修正退出时 Timer 驱动误报，运行中禁用仍使等待失败；验证后恢复场景/EditorSettings、字体自动生成内容、证书与环境设置，停止独立临时服务/数据库。未构建 IL2CPP、正式热更包或验证真机/其他平台 |
| 2026-10-13 协议生成入口，Unity6000.3.19f1/macOS | Editor 编译通过；既有协议/MemoryPack144项、新工具3项分别通过；Generate/Check 菜单均实跑成功、运行时菜单禁用；StandaloneOSX Player 脚本43程序集通过，未包含 Editor 工具 | 进程测试覆盖空格路径、大量双管道输出和非零退出；未实跑 Windows/Linux 菜单，未构建 IL2CPP；Launch 场景及 EditorSettings 保持原值 |
| 2026-10-13 Unity6000.3.19f1 | Editor 编译；网络协议/MemoryPack EditMode144/144；生命周期 PlayMode11/11 | 首次 PlayMode 在 TearDown/运行器空引用中断，恢复状态后单项与整组复跑通过；未改运行器或削弱断言 |
| 2026-10-12 重启后 Unity Mono | 新 mbedTLS4.2/ABI2 实际 UDP Hello/Echo/关闭通过，自有缓冲与 native 预留归零 | 非 IL2CPP、移动真机或 Windows 执行 |
| 2026-10-12 StandaloneOSX | Player 脚本编译 43 程序集通过 | 非最终 native 链接、裁剪或 Player 运行 |

待验收：实际 IL2CPP 包/新热更 DTO、移动设备/模拟器、Windows/Linux 平台实跑、IPv6/代理/后台/换网/弱网、完整元数据与分阶段诊断、性能容量/24h soak。WebGL WSS 和世界业务延后，不以现有单元/Editor 证据声称全部完成。
