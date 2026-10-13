# 服务端使用说明

服务端是 `DotNet/Game.Server`，.NET SDK 10.0.401/net10.0；业务、数据库与框架分层见 [全局开发规范](DotNetDevelopment.md)。本文从仓库根目录执行命令。客户端实际调用见 [客户端使用说明](ClientNetworkUsage.md)。

当前提供两个显式 local profile：local-wss 用于 PostgreSQL 注册/登录/恢复/注销/Echo；local-kcp-dtls 只验证受控授权的 Hello/Echo。两者只监听本机。当前生产 hosting、跨进程账号会话和游戏世界业务尚未接入，不能把本地示例直接当作生产服务。

## 1. 准备与启动 PostgreSQL

Npgsql 10.0.3 已在 [Game.Server.csproj](../DotNet/Game.Server/Game.Server.csproj) 引用，由 NuGet restore 获取和发布，无需手动复制数据库驱动 DLL；PostgreSQL 数据库服务本身需要安装或远程部署。

本机 macOS 使用已安装的 Homebrew PostgreSQL 18：

```sh
export PATH="/opt/homebrew/opt/postgresql@18/bin:$PATH"
pg_ctl -D /opt/homebrew/var/postgresql@18 -l /opt/homebrew/var/postgresql@18/server.log start
```

首次准备角色、库和表：

```sh
createuser -h 127.0.0.1 --pwprompt gameframe
createdb -h 127.0.0.1 --owner=gameframe gameframe
psql -h 127.0.0.1 -U gameframe -d gameframe -v ON_ERROR_STOP=1 -f DotNet/Game.Server/Database/001_accounts.sql
```

createuser/createdb 使用有相应管理权限的本地角色；其他机器按实际管理员连接配置。数据库密码在终端输入或通过 .pgpass/PGPASSFILE 提供。初始化 SQL 仅执行一次，重复明确失败，不自动删除重建已有表；再次联调只启动数据库。

已有数据库也可直接用 psql 执行 [初始 SQL](../DotNet/Game.Server/Database/001_accounts.sql)。Docker 开发机可选择：

```sh
export GF_POSTGRES_PASSWORD='自行设置的数据库密码'
docker compose -f DotNet/compose.yaml up -d --wait
docker compose -f DotNet/compose.yaml exec -T postgres psql -U gameframe -d gameframe -v ON_ERROR_STOP=1 < DotNet/Game.Server/Database/001_accounts.sql
```

compose 端口只发布到 127.0.0.1，数据保存在命名卷。停止用 `docker compose -f DotNet/compose.yaml stop`，不删除数据卷。

跨平台维护入口是 [Tools/postgres.py](../Tools/postgres.py)，需要 Python3 和 PostgreSQL 命令工具。可通过 PATH 或 `--pg-bin` 指定工具目录：

```sh
python3 Tools/postgres.py --pg-bin /opt/homebrew/opt/postgresql@18/bin start --data-dir /opt/homebrew/var/postgresql@18
python3 Tools/postgres.py --pg-bin /opt/homebrew/opt/postgresql@18/bin status --data-dir /opt/homebrew/var/postgresql@18
python3 Tools/postgres.py --pg-bin /opt/homebrew/opt/postgresql@18/bin stop --data-dir /opt/homebrew/var/postgresql@18
```

首次建库/初始化的维护命令如下；先创建 gameframe 角色，`--user` 改为实际有建库权限的管理员：

```sh
python3 Tools/postgres.py create-db --user your_admin --database gameframe --owner gameframe
python3 Tools/postgres.py init-schema --user gameframe --database gameframe
```

可追加 --host/--port；密码用 .pgpass/PGPASSFILE/PGPASSWORD，工具不接受密码命令行参数。start/stop 最多等待 30 秒，stop 使用 fast 模式关闭连接，status 未运行返回 3。它只执行显式命令，不由游戏服务器自动调用、启动数据库或配置开机服务。

正式部署由独立 schema-owner 初始化，应用角色只授予 public schema USAGE、accounts SELECT/INSERT/UPDATE、accounts_id_seq USAGE；远程连接使用可信证书和 `SSL Mode=VerifyFull`。数据库角色密码用于连接数据库，与玩家密码不同。

## 2. WSS 证书与环境配置

已有本地 PFX 可继续使用，不必每次重新生成。需要新开发证书时显式执行：

```sh
dotnet dev-certs https --export-path /absolute/path/localhost.pfx --password "$GF_LOCAL_CERTIFICATE_PASSWORD"
dotnet dev-certs https --trust
```

先设置自己的 GF_LOCAL_CERTIFICATE_PASSWORD；--trust 是开发者主动信任开发证书的操作，不由服务代办。证书须有私钥、在有效期内、SAN 覆盖 localhost，客户端执行正常校验。不要将私钥、数据库密码或本机运行配置提交到版本库。

| 环境变量 | 含义 |
| --- | --- |
| GF_DATABASE_CONNECTION | Npgsql 连接字符串，例如 Host=127.0.0.1;Port=5432;Database=gameframe;Username=gameframe;Password=自己的数据库密码 |
| GF_LOCAL_CERTIFICATE | 带私钥 localhost PFX 绝对路径 |
| GF_LOCAL_CERTIFICATE_PASSWORD | 可选 PFX 密码，按证书实际配置 |
| GF_LOCAL_PORT | 可选，默认 7443 |
| GF_LOCAL_ORIGINS | 可选，逗号分隔的精确 Origin 允许列表；默认拒绝所有带 Origin 的连接 |

玩家账号不属于启动配置，不再设置 GF_LOCAL_ACCOUNT/PASSWORD。服务只检查证书、数据库及账号表，然后监听等待客户端注册/登录。

## 3. 构建、运行与停止

```sh
(cd DotNet && dotnet restore --locked-mode)
(cd DotNet && dotnet build DotNet.sln -c Release --no-restore)
dotnet run --project DotNet/Game.Server --launch-profile local-wss
```

或使用显式程序参数：

```sh
(cd DotNet && dotnet run --project Game.Server --no-launch-profile -- --profile local-wss)
```

Rider/Visual Studio 选择 [launchSettings.json](../DotNet/Game.Server/Properties/launchSettings.json) 中 local-wss，填写环境变量后直接运行。普通“.NET 项目”配置不一定读取 launchSettings，应显式填 `--profile local-wss`。IDE 从桌面启动不一定继承终端 export；在自己的 IDE 运行配置设置变量。

直接启动编译/发布的可执行程序不会读取 launchSettings，仍需参数和环境变量，例如：

```sh
DotNet/Game.Server/bin/Debug/net10.0/Game.Server --profile local-wss
```

成功后监听 `https://localhost:7443`，网络路径是 `wss://localhost:7443/network`。浏览器打开 `/` 或 favicon.ico 得到 404 是正常的：当前只映射 WebSocket 网络入口，没有首页；普通 HTTP 访问/network 也不能替代 TLS+WebSocket+GFNP 客户端。

终端 Ctrl+C 正常停止并等待网络/业务释放；IDE 使用停止按钮终止本次运行。停止游戏服务不会停止 PostgreSQL；需要结束开发数据库时显式执行上一节 stop。不要删除数据库文件。

| 启动/连接错误 | 检查项 |
| --- | --- |
| Production hosting is not configured | 选择 local launch profile，或给可执行程序传--profile；当前没有生产入口 |
| Required local configuration is missing | 在实际运行进程环境填写报错变量，只有程序参数不够 |
| Failed to connect / Connection refused 127.0.0.1:5432 | PostgreSQL 是否运行、端口/连接地址是否正确 |
| relation accounts does not exist / 表检查失败 | 对指定数据库显式执行初始 SQL，确认连接到了正确库和 schema |
| HTTPS 证书不可信或域名错误 | 客户端信任对应开发证书，连接 localhost，证书有私钥且有效；不使用忽略校验绕过 |
| 协议 Reject/hash 不同 | 重新生成并同步客户端/服务端，检查--artifacts；不兼容混连 |
| KCP 缺库/架构/ABI 错误 | 匹配平台库安装到实际执行目录；不能靠项目根目录中的库替代部署 |

## 4. 客户端注册、登录与恢复

```text
TLS + ClientHello(hash, 空ticket) → ServerHello（仍未登录）
→ Register（可选，成功仍未登录）→ Login → 已认证Echo
断线后显式新建WSS → ResumeLogin → 轮换恢复token、替换大厅绑定
Logout → 撤销会话/token → 成功响应 → Close1
```

首次 Hello 期限 5 秒，Hello 成功后登录等待 10 秒；Ping、Register 不续期。先等待注册响应再登录，同连接最多一个认证请求执行。已识别越阶段请求在 DTO 解码前拒绝。

账号区分大小写，最多 64 个 UTF-16 字符，拒绝空白/首尾空白/控制字符；密码 8～128、不裁剪。Register 重复返回 AccountAlreadyExists，无效输入 InvalidInput。凭据错误统一 InvalidCredentials 并 Close5。认证过载用系统 status1；具体策略与并发提交规则见 [开发规范账号章节](DotNetDevelopment.md#4-数据库与账号业务)。

不同账号独立，新 Login 仅顶掉同账号旧会话，旧绑定 Close2；Resume 保留账号会话 ID/epoch/到期时间并一次性轮换 token。到期 Close4、撤销 Close7。重启保留数据库账号/哈希/epoch，内存 Resume 失效；需显式重新登录。当前没有删号、改密/找回、第三方登录或封禁业务。

Echo 只在 local 注册，DelayMs=0～5000。RequestWorldGrant 当前返回 WorldUnavailable，其余世界路由显式拒绝；没有真实世界、角色或网关授权闭环。注册提交后响应丢失、RPC 超时或取消都可能结果未确认，不自动重放。

### 本地容量

| 项目 | WSS 默认 / KCP listener 默认 |
| --- | --- |
| 连接 | WSS32、每 IP4；受限总 16、每 IP2 / KCP64 established、16 handshake、256 grant |
| 应用输入 | WSS 受限 5 包/16KiB 每秒，Body2048；正常 128 包/128KiB 每秒 / KCP 正常 128 包/128KiB 每秒 |
| 托管接收 | 可靠 128 条/2MiB / 可靠 128 条/2MiB，不可靠 64 条/64KiB |
| 业务发送 | 128 条/64KiB / 可靠 128、不可靠 64 分别 FIFO，共享 64KiB |
| 控制/托管总额 | 64 条/16KiB；单连接 8MiB、全局 64MiB |
| KCP 原生 | 单连接 4MiB、listener256MiB；来源 IP 每秒 2048 包/2MiB、记录 1024 个 |

数值是上限，不预分配全部容量，不是生产人数承诺；选项真源为 WssServerOptions/KcpServerOptions/LoginOptions。应用心跳 5 秒，一个未确认 nonce；移交后 15 秒 Pong 期限，控制排队/发送另限 15 秒。停止后在 Completion 观察关闭原因与首因。

## 5. 框架接入 API

业务组合使用 GameFrame.Server.Network 和生成的 Game.Hotfix.Network.Generated/Protocol 类型：

```csharp
var application = new ServerApplication(GameNetProtocol.Catalog);
application.Rpc(GameNetRoutes.Echo, async (connection, request, cancellation) =>
{
    await Task.Delay((int)request.DelayMs, cancellation);
    return new EchoResponse { Text = request.Text };
});
```

此为局部注册示例，构造 host 前还须显式注册或 Reject 其余入站路由。RPC 返回 DTO，由框架编码 opcode/correlation、准入并释放帧。Notify 支持 NetSend/NetUnreliable；ServerRequestException 表示非零系统拒绝，业务拒绝返回 ResultCode。releaseResponse 用于同步编码尝试后清理敏感响应，请求在 handler 的 finally 清理。

ServerSession 派生类保存每连接应用状态，通过构造 ServerApplication 的 factory 创建。钩子线程、初始化阶段、权限复查、关闭重入约束见 [开发规范连接章节](DotNetDevelopment.md#3-启动连接与关闭)。context 提供 Id/Generation/GrantId/Transport/RemoteEndpoint/Cancellation/Completion，不向业务交付 native handle 或 OwnedFrame。

```csharp
await context.PushAsync(GameNetRoutes.WorldSnapshotReliable, snapshot);
bool admitted = context.TryPushUnreliable(GameNetRoutes.WorldSnapshot, snapshot);
context.RequestClose(2);
```

这是未来世界 Push 接口示例，当前 local 不发送世界快照；不可靠调用要求 KCP 协商支持。Push 成功只表示本地准入，后续错误通过 Completion 报告。外部 owner 可 await context.CloseAsync(reason)，handler 内部用 RequestClose/CloseAfterResponse，不能等待自身 Completion。

WSS 由应用配置 HTTPS、Kestrel 连接/TLS 限额及 WebSocket 管线，将 network.HandleAsync 映射/network。WssServer 必须通过 DI 同一实例注册 HostedService 并注入 ILogger；可参考 [LocalWssService](../DotNet/Game.Server/LocalWssService.cs)，不另写停机顺序。

应用数据库接入：

```csharp
await using var database = new PostgresDatabase(connectionString);
await database.ValidateAsync(cancellation);
var repository = new PostgresAccountRepository(database.DataSource);
await repository.ValidateAsync(cancellation);
```

这些内部应用类型在 Game.Server 中调用。账号 CRUD 接口为 CreateAsync/FindAsync/AdvanceEpochAsync，密码哈希/认证用 AccountService，不让处理器绕过它直接创建玩家。后续角色等仓库借用同一 DataSource；完整事务和资源规则见开发规范。

<a id="协议生成与验证"></a>

## 6. 协议生成与验证

输入是 `Protocol/Common`、`Session`、`World`、`Diagnostics` 下的.proto；字段/路由规则见 [共享协议规范](DotNetDevelopment.md#6-共享-protomemorypack-编写规则)。修改.proto 后：

```sh
(cd Tools && dotnet restore NetworkProtocol --locked-mode)
(cd Tools && dotnet run --project NetworkProtocol -- generate --artifacts)
(cd Tools && dotnet run --project NetworkProtocol -- check --artifacts)
(cd Tools && dotnet test NetworkProtocol.Tests)
(cd DotNet && dotnet build DotNet.sln)
```

正式 C#只生成 GameNetMessages.cs/GameNetProtocol.cs 到 `Unity/Assets/Scripts/Hotfix/Network/Generated`，.NET Game.Protocol 链接同一源码。默认 generate/check 只处理这两个文件；--artifacts 额外处理 `Protocol/Generated/protocol.manifest.json`、protocol.sha256、golden-vectors.json，仅开发/CI 使用，不进入 Unity 资产、正式资源或运行时。

check 不写文件，缺失、字节差异、多余 C#返回非零。generate 不覆盖字节相同文件，也不自动删除多余 C#，移除过时 Unity 资产须同时处理.meta 并保留有效 GUID。生成 C#/辅助文件/NuGet 锁纳入版本库；CI 只 check，不自动 generate 掩盖漏生成。

<a id="原生构建与部署"></a>

## 7. 原生构建与部署

双方共用 `Native/Kcp`，服务端部署只要匹配 RID 的库、清单和许可证，无需 KCP 源码或 Unity。依赖准备是一次显式步骤，输出在 Unity 和维护源码目录外：

```sh
python3 Native/Kcp/build/prepare_mbedtls.py --output Tools/Artifacts/NetworkNative/mbedtls-4.2.0
python3 Native/Kcp/build/build.py --platform macos --arch universal --configuration Release --output Tools/Artifacts/NetworkNative/macos-release --mbedtls Tools/Artifacts/NetworkNative/mbedtls-4.2.0 --tests
```

公共参数为--platform/--arch/--configuration/--output/--mbedtls；可用--cmake 指定 CMake，--dry-run 只显示计划。各平台独立 output，工具链如下：

| 平台 | 构建参数 / 工具链 | 产物 |
| --- | --- | --- |
| macOS | --platform macos --arch arm64/x86_64/universal；Apple Clang | libgameframe_kcp.dylib |
| Windows | --platform windows --arch x86_64；MSVC，或 macOS 追加--llvm-mingw /path/to/llvm-mingw-ucrt | gameframe_kcp.dll |
| Linux | --platform linux --arch arm64/x86_64；匹配宿主编译器 | libgameframe_kcp.so |
| Android | --platform android --arch arm64 --ndk /path/to/ndk；默认 API23 | libgameframe_kcp.so |
| iOS | --platform ios --arch arm64；Xcode device SDK | libgameframe_kcp.a |
| iOS Simulator | --platform ios-simulator --arch arm64 或 x86_64；对应 SDK | 独立 simulator archive |

--tests 仅在匹配宿主执行；Windows 交叉构建只编译回归 exe，built=true/executed=false。--sanitizers --tests 仅用于验证，安装器拒绝 sanitizer 库。Windows 校验 x64 PE、完整 gfnet 导出及系统依赖，MSVC 静态 CRT。移动平台仍需最终 Unity 链接与设备运行。

服务端先 publish，再安装到实际执行目录：

```sh
(cd DotNet && dotnet publish Game.Server -c Release -o artifacts/server)
python3 Native/Kcp/build/install.py Tools/Artifacts/NetworkNative/macos-release --server-output DotNet/artifacts/server
dotnet DotNet/artifacts/server/Game.Server.dll --profile local-kcp-dtls
```

本例仍须下一节 grant 配置。也可安装到 DotNet/Game.Server/bin/Debug/net10.0 用于 dotnet run；库放项目根不会自动到执行目录。清单、artifact 和许可证随库发布，不手改 hash 绕过安装器。Unity 安装同一产物省略--server-output，详见 [客户端使用说明](ClientNetworkUsage.md#原生插件安装)。

## 8. 本地 KCP/DTLS 与授权接口

配置 `GF_LOCAL_KCP_GRANT` 为受控 JSON 绝对路径，Conv 非零 u32、Identity 为 16 字节 hex、Psk 为 32 字节 hex、Ticket 为 1～1024 字节 hex；开发者显式生成随机凭据，限制文件权限，向测试客户端提供同一值，不提交或打印。端口 GF_LOCAL_KCP_PORT 默认 7450，仅 127.0.0.1：

```sh
dotnet run --project DotNet/Game.Server --launch-profile local-kcp-dtls
```

grant 注册后 30 秒内完成 DTLS+GFNP Hello 单次绑定，过期不自动注册续期；Ctrl+C 使全部 grant 失效并释放 listener。它只开放 Echo，没有账号登录、世界入场和取票 HTTP 接口；不要把预置调试 grant 当正式授权。

实际应用可创建 KcpListenerHost 并等待 Started，读取 LocalEndpoint，再 await RegisterGrantAsync(conv, identity, psk, ticket, ttlMs)，取得 grantId；显式 RevokeGrantAsync(grantId)撤销。TTL 从调用准入起算，排队不续期。应用 await using 拥有 host，Stopped 报告 owner 终态；授权业务在网关注册确认后才能回复客户端。ABI/handle 规则见 [共享原生规范](DotNetDevelopment.md#8-共享原生-abi-2)。

<a id="验证与现有证据"></a>

## 9. 验证与现有证据

```sh
(cd Tools && dotnet test NetworkFramework.Tests -c Release)
(cd Tools && dotnet test NetworkProtocol.Tests -c Release)
GF_POSTGRES_TEST_CONNECTION='Host=127.0.0.1;Database=独立测试库;Username=测试角色;Password=测试密码' GF_KCP_NATIVE_LIBRARY=/absolute/path/libgameframe_kcp.dylib dotnet test Tools/NetworkServer.Tests -c Release
python3 Native/Kcp/tests/verify_fastack.py
python3 Native/Kcp/tests/verify_allocation_safety.py
```

PG 测试角色需能创建 schema，测试创建随机 schema 并只删除自身 schema。未设数据库/native 变量时对应测试明确跳过；不能将跳过作为 PG/DTLS 验收。框架测试不引用游戏协议，服务端测试含真实 Kestrel/WSS、客户端核心、账号、数据库与 UDP。native 脚本在临时目录验证 hash、ACK 对照、分配失败和 sanitizer，不安装插件；只有工具链不支持时显式--no-sanitizers，并注明未验。

以下为既有实际结果，不是本次文档整理重跑：

| 日期与范围 | 证据 | 限制 |
| --- | --- | --- |
| 2026-10-13 .NET Release | 四工程 0 警告/错误；Server136、Framework13、Protocol182 共 331 项全通过、0 跳过；check --artifacts 通过 | 独立 PG18 和 macOS native，不能代表其他平台 |
| 2026-10-12 mbedTLS4.2 迁移后 native | macOS Release/ASan/UBSan 回归通过，动态 live bytes 归零；五组已安装库来源/二进制 hash 一致 | Windows 仅交叉编译/PE，Android/iOS 仅构建安装 |
| 2026-10-12 实际进程 | WSS 注册→登录→Echo、重启后账号仍可登录；发布目录加载 KCP 库/监听/关闭，Ctrl+C 退出 0 | 本地实跑，非生产多实例/容量验收 |

尚未执行或完成：Windows/Linux 服务端平台实跑、远端 CI、生产部署与多实例、完整元数据/诊断、容量与 24h soak。世界业务与 WebGL 按用户确认延后；其他客户端平台边界见客户端说明。后续更新本表时注明平台、版本和实际范围，不用旧数量代表新验证。
