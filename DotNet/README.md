# 本地网络联调服务

.NET SDK 固定 10.0.401，目标 net10.0。解决方案保留一个业务进程，按程序集区分框架与应用：

| 工程 | 职责与依赖 |
| --- | --- |
| `GameFrame.Network.Core` | 同源 GFNP/control、路由/codec 契约、帧预算、native ABI；不引用游戏协议、业务或 Unity DLL |
| `GameFrame.Server` | WSS/KCP listener、连接 owner、类型化派发/Push/关闭；只引用 Core 和 ASP.NET Core |
| `Game.Protocol` | 通过 `Protocol/GameFrame.Protocol.projitems` 编译双端同一生成协议；引用 Core、MemoryPack |
| `Game.Server` | 唯一可执行入口，local 配置、账号示例、应用会话与路由；引用 Server、Protocol |

业务接入方法与生命周期契约见 [服务端框架 API](GameFrame.Server/README.md)。框架不编译客户端 worker/options/ConnectionContext，也不持有 AccountSessions 或游戏 opcode 分支。Unity 仍编译同源 Runtime/Hotfix C#，不加载 net10.0 DLL。

## 应用目录与初始化流程

`Game.Server` 按业务功能组织代码，登录相关实现集中在 `Login/`：

```text
Game.Server/
  Program.cs                 校验 --profile 并选择启动入口
  LocalEnvironment.cs        读取 local 必填配置与校验端口
  LocalWssService.cs         创建账号服务、应用、网络及 HTTPS host，配置路由并运行
  LocalKcpService.cs         读取临时 grant，创建并运行底层联调 listener
  LobbyApplication.cs        组合登录模块与本地 Echo，明确拒绝未接入的世界路由
  Login/
    LoginHandlers.cs         注册 Login/ResumeLogin/Logout，IP 登录限流及敏感 DTO 清理
    LoginSession.cs          每连接登录状态、准入、权限复查、期限与关闭解绑
    AccountSessions.cs       账号凭据、恢复轮换、顶号与注销
    LocalAccountSettings.cs  本地账号配置及登录策略
```

`Program` 不创建证书、账号或传输资源。两个启动入口负责各自资源的创建与释放；WSS 先校验配置，再创建账号与应用、网络、HTTP host，配置管线后运行。停止时先由 hosted lifecycle join 网络，再停止 HTTP host，最后释放账号与证书。初始化失败立即停止，已创建资源按所有权释放。后续业务模块沿用功能目录，在应用组合入口显式注册，不通过反射扫描处理器。

## 启动

WSS 使用显式 `local-wss` profile；仅监听 localhost，不使用默认账号或密码。先配置环境变量：

| 变量 | 用途 |
| --- | --- |
| `GF_LOCAL_ACCOUNT` | 本地测试账号，最多 64 个 UTF-16 字符 |
| `GF_LOCAL_PASSWORD` | 本地测试密码，最多 128 个 UTF-16 字符 |
| `GF_LOCAL_CERTIFICATE` | 带私钥的 HTTPS `.pfx` 文件路径 |
| `GF_LOCAL_CERTIFICATE_PASSWORD` | 可选 PFX 密码 |
| `GF_LOCAL_PORT` | 可选端口，默认 7443 |
| `GF_LOCAL_ORIGINS` | 可选浏览器 Origin 精确允许列表，逗号分隔；默认拒绝所有带 Origin 的连接 |

从仓库根目录执行：

```sh
(cd DotNet && dotnet restore --locked-mode)
(cd DotNet && dotnet run --project Game.Server -- --profile local-wss)
```

项目提供 `Game.Server/Properties/launchSettings.json`，包含 `local-wss` 与 `local-kcp-dtls` 两个本地启动配置；它们自动传入对应的 `--profile`，不包含账号、密码或证书。也可执行 `dotnet run --project DotNet/Game.Server --launch-profile local-wss`。在 Rider/Visual Studio 中选择相应的 launch profile，并在本机运行配置中填写上述 `GF_LOCAL_*` 环境变量。已存在的普通“.NET 项目”运行配置可能不会自动使用 launchSettings，此时需在程序参数中显式填写 `--profile local-wss`。只有参数、没有账号/密码/证书仍会拒绝启动；IDE 从桌面启动时也不一定继承终端中 export 的变量。

直接运行 `bin/Debug/net10.0/Game.Server` 或发布后的可执行程序不会读取 launchSettings，必须自行传入 `--profile` 并配置环境变量。省略参数保持生产启动限制，不因 Debug 构建自动启用测试认证。

入口为 `wss://localhost:7443/network`，可按配置修改端口。开发证书必须由开发者显式生成并在客户端信任，证书 SAN 应覆盖 localhost。程序不会安装证书、修改系统信任或忽略证书校验。启动前检查私钥及有效期；客户端仍执行正常域名、证书链和有效期校验。WebSocket 禁用应用压缩，不接收 query 或 Authorization header 中的凭据。

未指定 profile、生产 profile、未配置认证或缺少 local 必填项时拒绝启动。另有显式 `local-kcp-dtls` 底层联调入口，见下节。这个 profile 是联调服务，不能作为生产账号系统；本地密码摘要只用于固定测试账号比对，不是生产密码存储方案。

## 当前行为

ClientHello → ServerHello 后还未登录。10 秒内只能发送 Login/ResumeLogin 或控制消息；首个 Hello 期限 5 秒。协议 hash 不符在提交账号凭据前 Reject。

实现 Login、ResumeLogin、Logout、Echo；所有业务路由使用生成的 `GameNetRoutes`。本地服务只配置一个账号（accountId=1），新 Login 撤销旧账号 epoch/大厅连接。Resume 原子消费并轮换 32 字节恢复凭据，保留账号会话 ID/epoch；仅替换大厅绑定。服务端仅保留恢复凭据 SHA-256 摘要。账号期限最多 24 小时，TTL 使用服务端单调时钟；恢复不会延长账号期限。

Logout 先撤销账号及恢复凭据，再发送成功响应并关闭。登录失败统一返回 InvalidCredentials，再关闭。Echo 仅注册在本地服务中，delayMs 接受 0～5000；系统错误响应为空正文，客户端不能把本地超时/取消视为服务端未执行。

世界服务尚未接入大厅授权：RequestWorldGrant 返回 WorldUnavailable，其余世界请求返回权限错误，不创建世界或签发无效密钥。未经认证的这些请求在 DTO 解码前拒绝。

每连接一个收循环、一个发循环和有界业务 owner 队列；账号提交在账号 gate 内串行，不持锁等待网络 IO。应用心跳每 5 秒且只有一个待确认 nonce，Ping 发送完成后 15 秒内必须收到匹配 Pong；控制包从 admission 起也受独立 15 秒发送期限约束，包含排队和 Send 未返回。watchdog 不等待 Ping 的慢发送，发送 owner 移交前再次检查控制期限。Hello/登录期限和心跳不依赖 Unity 帧。

local 默认网络容量在 `GameFrame.Server/WssServerOptions.cs`，账号 TTL、登录期限和登录尝试限额单独位于 `Game.Server/Login/LocalAccountSettings.cs`：32 个连接、每 IP 4 个，其中未登录总计 16 个/每 IP 2 个；未登录每秒 5 包/16 KiB，账号与 IP 登录每秒 5 次。认证后每秒 128 包/128 KiB；可靠接收 128 条/2 MiB，业务发送 128 条/64 KiB，控制 64 条/16 KiB。缓冲按实际数组容量计账，包含在途发送、接收扩容的旧＋新峰值，释放前清零全部容量。全局 64 MiB、每连接 8 MiB。TLS/Kestrel 内部空间与内核缓冲不包含在可控制缓冲额度中。

WssServer 注册为 hosted lifecycle service，在所有 hosted StopAsync（包括 Kestrel）之前的 StoppingAsync 阶段停止准入、取消并 join 连接，随后 HTTP host 排空请求。应用会话退出后归还连接配额，再发布连接终态；重复关闭等待同一结果。

## 验证

```sh
(cd Tools && dotnet test NetworkServer.Tests)
(cd Tools && dotnet test NetworkFramework.Tests)
```

测试使用实际 Kestrel HTTPS 和 ClientWebSocket；测试进程只信任该次生成的精确临时证书，没有系统信任变更或生产证书豁免。包含认证、原子恢复/顶号/注销、恶意帧、Origin、分片及实际客户端核心的 Pump/超时/取消/关闭测试。它不代表 Unity IL2CPP 或真机 TLS 已通过。

另以内部可控 WebSocket/时钟测试 RPC 截止边界、迟到恶意 DTO、控制优先、慢发送控制期限、Pong 起算点和双方 watchdog；生产仍使用标准 ClientWebSocket/Kestrel 与 Stopwatch，不开放证书绕过或业务故障注入接口。


## 本地 KCP / DTLS

先按 [原生说明](../Native/Kcp/README.md) 构建匹配 RID 的库，并用 install.py 的 `--server-output` 安装到服务可执行程序旁。`KcpListenerHost` 在一个 owner 线程管理共享 UDP socket/所有子 handle，应用 handler 在独立有界队列消费已校验 DTO；关闭 child 不关闭共享 socket。公开 RegisterGrantAsync/RevokeGrantAsync 是后续授权服务接入点；生产 provider 未配置仍拒绝启动。

源码与构建入口统一在仓库 `Native/Kcp`；服务端部署只需要对应平台的原生库和随附清单/许可证，无需复制 KCP C 源码，也无需安装 Unity。先发布 .NET，再安装原生库（下面以 macOS universal、已准备的锁定依赖为例；各目标使用独立构建目录）：

```sh
python3 Native/Kcp/build/build.py --platform macos --arch universal --configuration Release --output Tools/Artifacts/NetworkNative/macos-release --mbedtls Tools/Artifacts/NetworkNative/mbedtls-4.2.0 --tests
(cd DotNet && dotnet publish Game.Server -c Release -o artifacts/server)
python3 Native/Kcp/build/install.py Tools/Artifacts/NetworkNative/macos-release --server-output DotNet/artifacts/server
dotnet DotNet/artifacts/server/Game.Server.dll --profile local-kcp-dtls
```

也可以显式安装到 `dotnet run` 实际执行目录（`DotNet/Game.Server/bin/Debug/net10.0`），然后使用下方启动命令；库放在项目根目录不会自动部署到执行目录。

`GF_LOCAL_KCP_GRANT` 指向受控临时 JSON 文件，字段 `Conv`（非零 u32）、`Identity`（16 bytes hex）、`Psk`（32 bytes hex）、`Ticket`（1～1024 bytes hex）。必须由开发者显式生成随机值、限制文件访问权限，并向测试客户端提供同一凭据；不写入版本库/安装包/日志。`GF_LOCAL_KCP_PORT` 默认7450，仅监听127.0.0.1。

```sh
(cd DotNet && dotnet run --project Game.Server -- --profile local-kcp-dtls)
```

配置 grant 的签发 TTL 固定30秒，完成 DTLS 后还必须通过 GFNP Hello/hash/ticket 单次绑定，只有绑定成功才返回 ServerHello。local 只执行 Echo；世界/账号业务未安装，不把预配置调试凭据当成生产授权。Ctrl+C 关闭 listener并使其所有grant失效，等待应用任务和原生资源释放；已经过期/释放的grant不再重复撤销。

local listener 上限64 established、16 handshake、256 grant，native实际全局256MiB；托管缓冲64MiB/每连接8MiB。共享 socket 每轮最多读取256包且读取阶段最多2ms；来源按IP每秒最多2048包/2MiB，最多1024个IP记录，每秒清空，表满拒绝新来源；端口不影响限额。Ping的Pong期限从native接受Ping后开始，控制排队另受15秒期限约束。每连接实际KCP输入受native64包/flush限制，认证后的应用帧每秒128包/128KiB。可靠接收128条/2MiB、不可靠64条/64KiB（淘汰最旧/超过250ms丢弃）、发送128条/64KiB；这些是本地容量，未做生产容量验收。

本机原生实联测试需要显式指定已验证的 host library：

```sh
GF_KCP_NATIVE_LIBRARY=/absolute/path/libgameframe_kcp.dylib dotnet test Tools/NetworkServer.Tests --no-restore --filter KcpLoopbackTests
```

未设置变量时该组明确 skipped，不算原生验收。Unity PlayMode `NetworkKcpLoopbackTests` 使用临时 `GF_KCP_LOOPBACK_CREDENTIALS` 文件（上述字段再加 `Port`）连接已启动的 local listener，默认不自动启动服务或保留测试凭据。

当前 native ABI 为 2，更新库与托管必须同批安装。KcpListenerHost 使用 ServerApplication 类型化处理器，参数为 ServerConnectionContext、请求 DTO 与 CancellationToken，回复只返回 DTO。框架构造 opcode/correlation 与帧并负责预算/释放，业务不取得原生 handle、OwnedFrame 或发送预算。RegisterGrantAsync 从调用 admission 起算 TTL，排队不续期。网络容量由 KcpServerOptions 在构造时校验和固定；多个 listener 共用进程级 native/PSA 限制，不能把额度当作预分配或 RSS。

普通EOF让既有业务owner完成已接受可靠输入，物理EOF之后的回复直接释放；故障/显式停机取消并等待handler，handler必须响应取消。listener业务可靠发送128条、不可靠64条分别FIFO，共享64KiB预算；控制独立16KiB。输出首尾flush共用每轮256包预算，socket WouldBlock期间保留原包并暂停追加业务。
