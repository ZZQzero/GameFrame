# 本地网络联调服务

.NET SDK 固定 10.0.401，目标 net10.0。现有 DotNet 工程已接入 ASP.NET Core/Kestrel，共享 `Protocol/GameFrame.Protocol.projitems` 的 MemoryPack DTO、catalog 和协议基础源码。

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
(cd DotNet && dotnet run -- --profile local-wss)
```

入口为 `wss://localhost:7443/network`，可按配置修改端口。开发证书必须由开发者显式生成并在客户端信任，证书 SAN 应覆盖 localhost。程序不会安装证书、修改系统信任或忽略证书校验。启动前检查私钥及有效期；客户端仍执行正常域名、证书链和有效期校验。WebSocket 禁用应用压缩，不接收 query 或 Authorization header 中的凭据。

未指定 profile、生产 profile、未配置认证或缺少 local 必填项时拒绝启动。另有显式 `local-kcp-dtls` 底层联调入口，见下节。这个 profile 是联调服务，不能作为生产账号系统；本地密码摘要只用于固定测试账号比对，不是生产密码存储方案。

## 当前行为

ClientHello → ServerHello 后还未登录。10 秒内只能发送 Login/ResumeLogin 或控制消息；首个 Hello 期限 5 秒。协议 hash 不符在提交账号凭据前 Reject。

实现 Login、ResumeLogin、Logout、Echo；所有业务路由使用生成的 `GameNetRoutes`。本地服务只配置一个账号（accountId=1），新 Login 撤销旧账号 epoch/大厅连接。Resume 原子消费并轮换 32 字节恢复凭据，保留账号会话 ID/epoch；仅替换大厅绑定。服务端仅保留恢复凭据 SHA-256 摘要。账号期限最多 24 小时，TTL 使用服务端单调时钟；恢复不会延长账号期限。

Logout 先撤销账号及恢复凭据，再发送成功响应并关闭。登录失败统一返回 InvalidCredentials，再关闭。Echo 仅注册在本地服务中，delayMs 接受 0～5000；系统错误响应为空正文，客户端不能把本地超时/取消视为服务端未执行。

世界服务尚未接入大厅授权：RequestWorldGrant 返回 WorldUnavailable，其余世界请求返回权限错误，不创建世界或签发无效密钥。未经认证的这些请求在 DTO 解码前拒绝。

每连接一个收循环、一个发循环和有界业务 owner 队列；账号提交在账号 gate 内串行，不持锁等待网络 IO。应用心跳每 5 秒且只有一个待确认 nonce，Ping 发送完成后 15 秒内必须收到匹配 Pong；控制包从 admission 起也受独立 15 秒发送期限约束，包含排队和 Send 未返回。watchdog 不等待 Ping 的慢发送，发送 owner 移交前再次检查控制期限。Hello/登录期限和心跳不依赖 Unity 帧。

local 默认容量在 `Network/ServerSettings.cs`：32 个连接、每 IP 4 个，其中未登录总计 16 个/每 IP 2 个；未登录每秒 5 包/16 KiB，账号与 IP 登录每秒 5 次。认证后每秒 128 包/128 KiB；可靠接收 128 条/2 MiB，业务发送 128 条/64 KiB，控制 64 条/16 KiB。缓冲按实际数组容量计账，包含在途发送、接收扩容的旧＋新峰值，释放前清零全部容量。全局 64 MiB、每连接 8 MiB。TLS/Kestrel 内部空间与内核缓冲不包含在可控制缓冲额度中。

## 验证

```sh
(cd Tools && dotnet test NetworkServer.Tests)
```

测试使用实际 Kestrel HTTPS 和 ClientWebSocket；测试进程只信任该次生成的精确临时证书，没有系统信任变更或生产证书豁免。包含认证、原子恢复/顶号/注销、恶意帧、Origin、分片及实际客户端核心的 Pump/超时/取消/关闭测试。它不代表 Unity IL2CPP 或真机 TLS 已通过。

另以内部可控 WebSocket/时钟测试 RPC 截止边界、迟到恶意 DTO、控制优先、慢发送控制期限、Pong 起算点和双方 watchdog；生产仍使用标准 ClientWebSocket/Kestrel 与 Stopwatch，不开放证书绕过或业务故障注入接口。


## 本地 KCP / DTLS

先按 [原生说明](../Unity/Assets/Scripts/Runtime/Network/Transport/Kcp/Native~/README.md) 构建匹配 RID 的库，并用 install.py 的 `--server-output` 安装到服务可执行程序旁。`KcpListenerHost` 在一个 owner 线程管理共享 UDP socket/所有子 handle，应用 handler 在独立有界队列消费已校验帧；关闭 child 不关闭共享 socket。内部 RegisterGrantAsync/RevokeGrantAsync 是后续授权服务接入点；生产 provider 未配置仍拒绝启动。

`GF_LOCAL_KCP_GRANT` 指向受控临时 JSON 文件，字段 `Conv`（非零 u32）、`Identity`（16 bytes hex）、`Psk`（32 bytes hex）、`Ticket`（1～1024 bytes hex）。必须由开发者显式生成随机值、限制文件访问权限，并向测试客户端提供同一凭据；不写入版本库/安装包/日志。`GF_LOCAL_KCP_PORT` 默认7450，仅监听127.0.0.1。

```sh
(cd DotNet && dotnet run -- --profile local-kcp-dtls)
```

配置 grant 的签发 TTL 固定30秒，完成 DTLS 后还必须通过 GFNP Hello/hash/ticket 单次绑定，只有绑定成功才返回 ServerHello。local 只执行 Echo；世界/账号业务未安装，不把预配置调试凭据当成生产授权。Ctrl+C 关闭 listener并使其所有grant失效，等待应用任务和原生资源释放；已经过期/释放的grant不再重复撤销。

local listener 上限64 established、16 handshake、256 grant，native实际全局256MiB；托管缓冲64MiB/每连接8MiB。共享 socket 每轮最多读取256包且读取阶段最多2ms；来源按IP每秒最多2048包/2MiB，最多1024个IP记录，每秒清空，表满拒绝新来源；端口不影响限额。Ping的Pong期限从native接受Ping后开始，控制排队另受15秒期限约束。每连接实际KCP输入受native64包/flush限制，认证后的应用帧每秒128包/128KiB。可靠接收128条/2MiB、不可靠64条/64KiB（淘汰最旧/超过250ms丢弃）、发送128条/64KiB；这些是本地容量，未做生产容量验收。

本机原生实联测试需要显式指定已验证的 host library：

```sh
GF_KCP_NATIVE_LIBRARY=/absolute/path/libgameframe_kcp.dylib dotnet test Tools/NetworkServer.Tests --no-restore --filter KcpLoopbackTests
```

未设置变量时该组明确 skipped，不算原生验收。Unity PlayMode `NetworkKcpLoopbackTests` 使用临时 `GF_KCP_LOOPBACK_CREDENTIALS` 文件（上述字段再加 `Port`）连接已启动的 local listener，默认不自动启动服务或保留测试凭据。

当前native ABI为2，更新库与托管必须同批安装。KcpListenerHost handler参数为不可变KcpConnectionInfo（Id、Generation、GrantId）、OwnedFrame、发送预算与CancellationToken；不将借用native handle交给异步业务。RegisterGrantAsync从调用admission起算TTL，排队不续期。

普通EOF让既有业务owner完成已接受可靠输入，物理EOF之后的回复直接释放；故障/显式停机取消并等待handler，handler必须响应取消。listener业务可靠发送128条、不可靠64条分别FIFO，共享64KiB预算；控制独立16KiB。输出首尾flush共用每轮256包预算，socket WouldBlock期间保留原包并暂停追加业务。
