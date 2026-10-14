# 服务端使用说明

服务端是 `DotNet/Game.Server`，.NET SDK 10.0.401/net10.0；业务、数据库与框架分层见 [全局开发规范](DotNetDevelopment.md)。本文从仓库根目录执行命令。客户端实际调用见 [客户端使用说明](ClientNetworkUsage.md)。

当前提供两个显式 local profile：local-wss 用于 PostgreSQL 注册/登录/恢复/注销/Echo 和两人五子棋；local-kcp-dtls 只验证受控授权的 Hello/Echo。两者只监听本机。当前生产 hosting、跨进程账号会话和游戏世界业务尚未接入，不能把本地示例直接当作生产服务。

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
| GF_CONFIG_DIR | 可选，两种 profile 通用；默认执行目录下 Config/Bytes，相对路径基于 AppContext.BaseDirectory |
| GF_LOCAL_ORIGINS | 可选，逗号分隔的精确 Origin 允许列表；默认拒绝所有带 Origin 的连接 |

玩家账号不属于启动配置，不再设置 GF_LOCAL_ACCOUNT/PASSWORD。服务先加载游戏配置，再检查证书、数据库及账号表，然后监听等待客户端注册/登录。

### Luban 配置生成与读取

两端共用 `Unity/Config/Excel` 和 `luban.conf`，统一通过解决方案中的 `DotNet/ConfigGenerator` 调用现有 Luban 5.0.0 可执行文件。工具不引用 Game.Config，既有生成产物缺失时也能独立生成；不将 Luban 源码工程或生成器依赖带入游戏服务器。

Rider/Visual Studio 选择 ConfigGenerator 的以下启动项，然后直接运行：

| 启动项 | 行为 |
| --- | --- |
| Generate Server | 只生成服务端代码和数据 |
| Generate All | 依次生成客户端、服务端代码和数据 |
| Check | 临时生成双端产物，与正式目录比较，不修改正式文件 |

对应命令：

```sh
(cd DotNet && dotnet run --project ConfigGenerator --launch-profile "Generate Server")
(cd DotNet && dotnet run --project ConfigGenerator --launch-profile "Generate All")
(cd DotNet && dotnet run --project ConfigGenerator --launch-profile Check)
```

IDE 不读取 launchSettings 时，显式填写 `generate --target server`、`generate --target all` 或 `check --target all`。CLI 为 `generate|check [--target client|server|all] [--root repository-path]`，默认双端；工具的 `all` 表示分别执行 client/server，不是 Luban 的全组 all target。根目录默认从工具所在目录向上定位，不依赖工作目录；工具复制到仓库外时使用 `--root`。

macOS/Linux 从仓库根目录也可运行：

```sh
bash Unity/Config/gen.sh
```

Windows 可在任意目录运行 `Unity\Config\gen.bat`。Unity 菜单 `Tools/GameFrame/Excel/ExcelExporter` 调用同一脚本；脚本从 DotNet 工作目录运行 ConfigGenerator 的双端生成命令。Windows 脚本无 pause，失败退出码会传回编辑器。统一入口需要 SDK 10.0.401 和 .NET 8 运行时（用于固定版本 Luban）；Unity/Hub 的 PATH 可能不同，必要时设置 `DOTNET_EXECUTABLE` 为绝对路径。工具优先使用该变量，其次 DOTNET_HOST_PATH，否则使用当前 .NET 运行时对应的 host，不自动切换版本或重试。

| target | 命名空间 | 代码输出 | 数据输出 |
| --- | --- | --- | --- |
| client（c） | Game.Hotfix.TableConfig | Unity/Assets/Scripts/Hotfix/TableConfig/Generated | Unity/Assets/Config/Excel/Bytes |
| server（s） | Game.Config.TableConfig | DotNet/Game.Config/Generated | DotNet/Game.Config/Bytes |

两次生成均启用 `--strict`；任一步失败即停止后续目标并保留 Luban 退出码。Generate All 的两个目标顺序写入，失败不会回滚已生成目标。输出目录只放生成内容，Luban 默认清理过时文件并保留 `.meta`；删除 Unity 表资产时还须检查对应 `.meta`。运行库固定在 `Game.Config/Luban`，不从 Unity 缓存引用。当前共享表为 GlobalConst、AIRobot，Language、GameScene 仅客户端导出。

Check 比较文件集合与二进制内容，忽略 `.meta/.uid`；缺失、多余或变化的产物返回 1，参数错误返回 2，Ctrl+C 返回 130。成功、失败或取消均清理临时输出；取消时先终止并等待 Luban 子进程退出。`.github/workflows/network.yml` 在配置输入、生成器、Unity 配置工具或产物变更时执行双端 Check 与 ConfigGenerator.Tests；CI 同时安装 global.json 固定的 SDK 和 .NET 8，不通过重新生成正式产物掩盖漂移。

服务端通过 `Game.Server.Configuration.TableConfigLoader.Load` 构造一个 `Game.Config.TableConfig.Tables`，使用生成器维护的表名读取文件并解析引用。两个 local profile 都在监听前完成加载，缺失或损坏文件终止启动。WSS 的业务服务可注入该 Tables 单例，或在应用组合处通过构造参数接收同一个实例；不重新加载、维护表名清单或修改其集合。

`dotnet build/publish Game.Server` 自动复制服务端数据到执行目录的 `Config/Bytes`，许可证到 `Licenses/Luban/LICENSE`，运行时不需要 Excel、生成器或 Unity。默认目录与当前工作目录无关；`GF_CONFIG_DIR` 可指定外部目录。第一版通过重启应用配置更新：仅数据变化部署匹配 bytes，表结构变化则同时生成代码/数据并重新编译发布。

常规只读核验使用 Check。需要自定义输出时可设置 `LUBAN_OUTPUT_CODE_DIR`、`LUBAN_OUTPUT_DATA_DIR`、`LUBAN_SERVER_OUTPUT_CODE_DIR`、`LUBAN_SERVER_OUTPUT_DATA_DIR`，相对路径基于 Unity/Config；仅所选目标的变量生效。验证双端生成时四个变量应全部指向独立临时目录，否则仍会写未覆盖的正式产物。加载与部署回归测试：

```sh
(cd Tools && dotnet test NetworkServer.Tests --filter 'TableConfigTests|WssLifecycleTests')
```

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
→ Login（已有账号验密；不存在则建号）→ 保存账号会话 → 已认证Echo
断线后显式新建WSS → ResumeLogin → 轮换恢复token、替换大厅绑定
Logout → 撤销会话/token → 成功响应 → Close1
```

首次 Hello 期限 5 秒，Hello 成功后登录等待 10 秒；Ping、Register 不续期。同连接最多一个认证请求执行。已识别越阶段请求在 DTO 解码前拒绝。Unity 登录按钮只提交 Login，不需要先查账号或 Register。独立 Register 仍可用，成功后需另发 Login。

首次 Login 使用参数化查询和唯一账号索引；并发创建时读取最终账号并验证保存的密码。建号与会话创建分开提交，超时/取消后账号可能已存在，下一次显式 Login 使用相同密码即可；不会回滚账号、覆盖密码或自动重放。数据库错误作为系统失败传播。

账号区分大小写，最多 64 个 UTF-16 字符，拒绝空白/首尾空白/控制字符；密码 8～128、不裁剪。Register 重复返回 AccountAlreadyExists，无效输入 InvalidInput。凭据错误统一 InvalidCredentials 并 Close5。认证过载用系统 status1；具体策略与并发提交规则见 [开发规范账号章节](DotNetDevelopment.md#4-数据库与账号业务)。

不同账号独立，新 Login 仅顶掉同账号旧会话，旧绑定 Close2；Resume 保留账号会话 ID/epoch/到期时间并一次性轮换 token。到期 Close4、撤销 Close7。重启保留数据库账号/哈希/epoch，内存 Resume 失效；需显式重新登录。当前没有删号、改密/找回、第三方登录或封禁业务。

Echo 只在 local 注册，DelayMs=0～5000。世界授权、入场、恢复、移动与 AOI 消息未使用，已从生产协议及应用占位注册中删除；未来随实际玩法定义。注册提交后响应丢失、RPC 超时或取消都可能结果未确认，不自动重放。

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

WSS 的 ReceiveQueueBytes 最小为 33,824 字节：最大帧 32,768 + 初始帧头 32 + 两份帧记录 reservation（各 512）。接收正文扩容时旧、新缓冲同时占用额度，33,280 只能容纳最终帧，构造选项时即拒绝。单连接与全局额度还须容纳并行发送、控制和排队工作，最小接收额度不是完整连接容量建议。

## 5. 框架接入 API

业务组合使用 `GameFrame.Network` 框架类型和 `Game.Protocol` 生成消息、枚举及路由：

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
await context.PushAsync(GameNetRoutes.GomokuRoomSnapshot, snapshot);
context.RequestClose(2);
```

这是当前五子棋可靠 Push 接口；不可靠 Push 需要业务先定义路由并由 KCP 协商支持。Push 成功只表示本地准入，后续错误通过 Completion 报告。外部 owner 可 await context.CloseAsync(reason)，handler 内部用 RequestClose/CloseAfterResponse，不能等待自身 Completion。

WSS 由应用配置 HTTPS、Kestrel 连接/TLS 限额及 WebSocket 管线，将 network.HandleAsync 映射/network。WssServer 必须通过 DI 同一实例注册 HostedService 并注入 ILogger；可参考 [LocalWssService](../DotNet/Game.Server/LocalWssService.cs)，不另写停机顺序。

应用数据库接入：

```csharp
await using var database = new PostgresDatabase(connectionString);
await database.ValidateAsync(cancellation);
var repository = new PostgresAccountRepository(database.DataSource);
await repository.ValidateAsync(cancellation);
```

这些内部应用类型在 Game.Server 中调用。账号 CRUD 接口为 CreateAsync/FindAsync/AdvanceEpochAsync，注册、登录、恢复和注销用 AuthenticationService，不让处理器绕过它直接创建玩家。后续角色等仓库借用同一 DataSource；完整事务和资源规则见开发规范。

大厅显式组合示例（repository 为已验证的账号仓库）：

```csharp
var options = new LoginOptions();
using var accounts = new AccountSessions(options);
using var authentication = new AuthenticationService(repository, accounts, options);
var login = new LoginModule(accounts, authentication, options);
var application = new LobbyApplication(login).Application;
```

`LobbyApplication` 内逐项调用 Login/Echo/Gomoku 模块的 Register。认证请求入口在 `Login/LoginHandlers.cs`，完整用例在 `Login/AuthenticationService.cs`，连接准入与身份绑定在 `Sessions/LobbySession.cs`。新增业务在自己的模块中显式注册生成路由，再加入大厅组合；不改登录处理器或框架分发，不使用反射。所有模块借用宿主持有的资源，网络停止并 join handler 后才释放认证服务、账号会话和数据库。

大厅 Handler 从 `((LobbySession)context.Session).RequireIdentity()` 取得只读账号 ID、认证代次和绑定 ID。等待外部 IO 后复查绑定，业务状态提交处仍须执行所属业务的并发仲裁与权限检查；不要将身份快照当永久授权，也不要把客户端携带的账号 ID 当作当前用户。大厅 Echo 已在延迟前后执行身份检查。独立 KCP local harness 保留自己的无账号传输测试契约，不使用 LobbySession。

<a id="协议生成与验证"></a>

## 6. 协议生成与验证

输入是 `Protocol/Common`、`Session`、`Gomoku`、`Diagnostics` 下的.proto；字段/路由规则见 [共享协议规范](DotNetDevelopment.md#6-共享-protomemorypack-编写规则)。修改.proto 后：

`DotNet/NetworkProtocol` 已加入 `DotNet.sln`，是独立的开发工具，不由 Game.Server 运行时引用。在 IDE 中将 NetworkProtocol 设为启动工程，选择 `Generate` 后点击运行即可生成消息；选择 `Check` 检查产物是否与协议源一致。两个启动项均包含 `--artifacts`。命令行也可以运行 `(cd DotNet && dotnet run --project NetworkProtocol --launch-profile Generate)` 或 `--launch-profile Check`。

```sh
(cd DotNet && dotnet restore NetworkProtocol --locked-mode)
(cd DotNet && dotnet run --project NetworkProtocol --no-launch-profile -- generate --artifacts)
(cd DotNet && dotnet run --project NetworkProtocol --no-launch-profile -- check --artifacts)
(cd Tools && dotnet test NetworkProtocol.Tests)
(cd DotNet && dotnet build DotNet.sln)
```

正式 C#只生成 GameNetMessages.cs/GameNetProtocol.cs 到 `Unity/Assets/Scripts/Hotfix/Network/Generated`，.NET Game.Protocol 链接同一源码。默认 generate/check 只处理这两个文件；--artifacts 额外处理 `Protocol/Generated/protocol.manifest.json`、protocol.sha256、golden-vectors.json，仅开发/CI 使用，不进入 Unity 资产、正式资源或运行时。

Unity 菜单 `Tools/GameFrame/Network/Generate Messages`、`Check Messages` 从 `DotNet/` 工作目录调用同一个生成器，均包含 `--artifacts`；环境与使用步骤见 [客户端协议维护](ClientNetworkUsage.md#7-协议与依赖维护)。生成器和 Roslyn 等依赖由外部 .NET 进程加载，不安装进 Unity Assets 或 Player。测试工程仍在 `Tools/NetworkProtocol.Tests`，CI 从新工程路径执行只读检查。

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

### KCP 性能测量

meter 为 `GameFrame.Server.Network.Kcp`，使用 MeterListener 或现有 .NET 指标采集器订阅。安装了 dotnet-counters 的环境可运行 `dotnet-counters monitor --process-id <PID> --counters GameFrame.Server.Network.Kcp`；该工具是额外诊断依赖，不是服务启动依赖。

| Histogram | 单位 | 采样范围 |
| --- | --- | --- |
| gameframe.kcp.owner.work | ms | 每轮命令、UDP、native tick、子连接推进与清理，排除 WaitOne 与 owner 指标回调 |
| gameframe.kcp.owner.interval | ms | 相邻 owner 循环开始的间隔，包含工作、等待与线程调度 |
| gameframe.kcp.owner.allocated | By | 每轮 owner 线程新增托管分配，排除 owner 指标回调、应用任务及 native 分配 |
| gameframe.kcp.send.queue | ms | 业务帧入发送队列至 native 接受，排除控制帧；不代表客户端收到 |
| gameframe.kcp.dispatch.queue | ms | 入站业务帧 native 时间戳至开始派发，包含初始化/应用排队；过期不可靠帧不采样 |

指标只在订阅启用时采样，按进程聚合，不含账号或连接标签。owner 工作/分配仍包含循环内 Admit/CheckDeadline 和发送排队指标回调的成本，采集器回调应保持轻量；另测进程 CPU/RSS、.NET GC/停顿与应用/数据库阶段。当前保留 10ms 轮询策略；性能调整需结合目标连接数、业务量和弱网样本，不能用单连接 loopback 数据推断生产容量。

<a id="验证与现有证据"></a>

## 9. 验证与现有证据

2026-10-14 命名空间统一复验（macOS / .NET SDK 10.0.401）：七工程 Release 编译 0 警告 / 错误；协议 generate/check --artifacts 五文件一致；Protocol167/167、Framework13/13、Server178/178，均零跳过。服务端测试使用独立临时 PostgreSQL 与已核验 hash 的现有 macOS Release KCP 原生库，覆盖真实 WSS / 数据库 / UDP / DTLS。黄金向量只修改完整类型名称，消息字节不变；协议指纹已更新。未执行远端 CI、其他平台、IL2CPP、容量或长时运行验收。同步发布需重新构建资源包，不能复用此前旧协议资源包。

```sh
(cd Tools && dotnet test NetworkFramework.Tests -c Release)
(cd Tools && dotnet test NetworkProtocol.Tests -c Release)
(cd Tools && dotnet test ConfigGenerator.Tests -c Release)
GF_POSTGRES_TEST_CONNECTION='Host=127.0.0.1;Database=独立测试库;Username=测试角色;Password=测试密码' GF_KCP_NATIVE_LIBRARY=/absolute/path/libgameframe_kcp.dylib dotnet test Tools/NetworkServer.Tests -c Release
python3 Native/Kcp/tests/verify_fastack.py
python3 Native/Kcp/tests/verify_allocation_safety.py
```

PG 测试角色需能创建 schema，测试创建随机 schema 并只删除自身 schema。未设数据库/native 变量时对应测试明确跳过；不能将跳过作为 PG/DTLS 验收。框架测试不引用游戏协议，服务端测试含真实 Kestrel/WSS、客户端核心、账号、数据库与 UDP。native 脚本在临时目录验证 hash、ACK 对照、分配失败和 sanitizer，不安装插件；只有工具链不支持时显式--no-sanitizers，并注明未验。

以下按实际执行范围记录，各次验证独立，不用旧数量代表新验证：

2026-10-14 底层核对覆盖 Runtime 网络与 Core/Server、GameRuntime/Launch/GameEntry 和登录接入、协议定义/生成工具/正式生成代码、Editor 网络工具、关联 .NET/Unity 测试、KCP 桥接与仓库内第三方 KCP 源码、构建安装脚本，以及依赖锁/程序集/原生 importer/五组安装清单和网络 CI 配置。主要实施缺口是客户端 RPC 阶段及子预算可见性，已直接补入既有快照；保留当前状态仲裁和关闭所有权，暂不调整传输参数、池化或调度模型。完整 CLR 元数据成本和发布平台门禁仍未完成。外部 mbedTLS/PSA、MemoryPack、平台 TLS/SDK 的实现内部未逐文件审计，只核对版本、接入配置、边界与运行证据，不将本轮核对称为所有第三方实现的完整安全审计。

| 日期与范围 | 证据 | 限制 |
| --- | --- | --- |
| 2026-10-14 底层诊断与回归，macOS/.NET SDK10.0.401、PG18 | 最终 Server163/163、Framework13/13、Protocol182/182，均 0 跳过；三个测试工程 locked restore、协议 check --artifacts 五文件一致；独立临时 PG 与现有 macOS Release native 验证真实 WSS/数据库/UDP/DTLS；新增可控时钟测试覆盖分阶段耗时、同步 continuation 排除、连接汇总重置、保留响应后取消及在途额度、远端错误/期限/解码失败样本排除；KCP ACK16/16、调度4/4和分配75项（assertions/NDEBUG/ASan+UBSan）通过；五组安装产物的 binary/build ID/生产源码指纹核验通过 | 首轮新增远端错误用例错误携带正文，被现有 GFNP 规则拒绝；改为协议规定的空正文后整组复验通过。没有修改 wire/native ABI、重构建或替换插件；算法 sanitizer 不是完整 gfnet/DTLS sanitizer。临时库/进程已清理；Unity 证据见客户端表。未验其他平台实跑、远端 CI、生产容量/soak、IL2CPP/新热更 DTO |
| 2026-10-13 大厅业务模块与认证职责整理，macOS/.NET 10.0.401 | 七工程 Release 编译 0 警告/错误；Server160/160、Framework13/13、0 跳过，使用临时独立 PostgreSQL18.6 与已核验源码/二进制 manifest 的现有 macOS Release native；新增身份与凭据清理独立、异步 Echo 期间顶号/注销拒绝、断线恢复与注销凭据撤销用例；既有真实 WSS/PG、单次 token、epoch 仲裁、过载关闭/注册重试、普通 EOF 排空及 UDP/DTLS 回归通过；协议 check --artifacts 五文件一致 | 本轮未改协议、客户端或通用框架；未运行 Unity/Player/IL2CPP、独立 Game.Server 进程联调、其他平台、远端 CI、native 重构建/sanitizer 或容量验收；临时 PG 与文件已清理 |
| 2026-10-13 Login 自动建号与客户端接入，macOS/.NET 10.0.401 | 七工程 Release 编译 0 警告/错误；四测试工程 locked restore 通过；Server156、Framework13、Protocol182、ConfigGenerator15 共 366/366、0 跳过，使用独立 PG18.6 与现有 macOS Release native；新增真实 PG 首次建号、同名并发同/不同密码、保存密码复验与无效/取消输入测试，真实 WSS 一次 Login 后 Echo；协议 check --artifacts 通过；实际 Game.Server + Unity 登录/断线闭环通过 | 本地单实例；未验证生产部署、远端 CI、其他服务端平台、容量/soak；Unity 证据及临时开发证书信任范围见客户端说明 |
| 2026-10-13 服务端传输修正与测量，macOS/.NET 10.0.401 | 七工程 Release 编译 0 警告/错误，解决方案与四测试工程 locked restore 通过；Server150、Framework13、Protocol182、ConfigGenerator15 共 360/360、0 跳过，使用独立 PG18 与本机 Release native；真实 UDP/DTLS 验证 21.5 秒初始化期间心跳/准入、业务 FIFO、factory 发布、失败/主动关闭取消与正常关闭排空，完成后 owned 归零；WSS33,824 最小额度接收最大帧及双 lease 清理通过；协议 check --artifacts 与 Luban check --target all 通过；CI YAML 解析、路径/检查入口与 setup-dotnet 双版本安装语义核对通过 | 未执行远端 CI、Unity/Player/IL2CPP/真机、Windows/Linux 服务端实跑、新 native 构建/sanitizer、容量或 soak；新增指标不是完整服务端诊断 |
| 2026-10-13 KCP 单连接低负载样本，同上环境 | 16 次 Echo 预热后 128 次串行小消息；owner 工作 P95=0.244ms、循环间隔 P95=11.262ms、owner 分配 P95=1,256By/轮、发送排队 P95=11ms、派发排队 P95=1ms；含客户端 Pump 的 RPC RTT P95=57.581ms | 开启 MeterListener 的 loopback 功能样本，非容量/生产延迟门禁；包含采集开销，不代表纯框架分配或 WAN 性能；保留现有调度与内存策略 |
| 2026-10-13 配置生成统一入口，macOS/.NET 10.0.401、Unity 6000.3.19f1 | 七工程 Release 编译 0 警告/错误；解决方案及 ConfigGenerator.Tests locked restore 通过；工具测试 15/15、0 跳过（双管道、退出码、取消与清理）；dotnet run 三个 launch profile 成功；脚本从其他工作目录向带空格路径导出 9+5 个 C#、4+2 个 bytes，与正式产物一致；真实 Check 成功及缺失/多余/变化失败返回 1，文件内容与 mtime 不变；空产物 checkout 可生成服务端配置；Unity ExcelExporter 成功，相关 EditMode 1/1、Console 无错误，场景/设置保持原值 | 未实测 Rider/Visual Studio UI、Windows/Linux 脚本或服务端、Player/IL2CPP/真机；未重跑 PG/native、运行时 PlayMode 或远端 CI |
| 2026-10-13 Luban 双端接入，macOS/.NET 10.0.401、Unity 6000.3.19f1 | 六工程 Release 编译 0 警告/错误；locked restore 通过；Config/WssLifecycle10/10；双端临时生成 9+5 个 C#、4+2 个 bytes 与正式产物一致；发布目录数据/许可证一致；两个 profile 在不同工作目录加载成功，缺配置先于网络依赖失败；Unity Editor 编译、ExcelExporter 菜单双端生成成功、EditMode5/5、配置 PlayMode9/9 | PlayMode 首次受到前一轮 EditMode 的 Test Runner 状态残留影响，重新加载脚本域后通过；场景、运行设置恢复；未重跑 PG/native、Windows 脚本实跑、Player/IL2CPP/真机或远端 CI |
| 2026-10-13 协议生成入口迁移，macOS/.NET 10.0.401 | 五工程 Release 编译 0 警告/错误；Protocol182/182、0 跳过；Generate/Check launch profile 均成功处理 5 文件；锁定依赖还原通过 | 生成 C# 仅路径注释变化，manifest/hash/vector 未变；本轮未重跑 WSS/PG/native，未执行远端 CI |
| 2026-10-13 .NET Release | 四工程 0 警告/错误；Server136、Framework13、Protocol182 共 331 项全通过、0 跳过；check --artifacts 通过 | 独立 PG18 和 macOS native，不能代表其他平台 |
| 2026-10-12 mbedTLS4.2 迁移后 native | macOS Release/ASan/UBSan 回归通过，动态 live bytes 归零；五组已安装库来源/二进制 hash 一致 | Windows 仅交叉编译/PE，Android/iOS 仅构建安装 |
| 2026-10-12 实际进程 | WSS 注册→登录→Echo、重启后账号仍可登录；发布目录加载 KCP 库/监听/关闭，Ctrl+C 退出 0 | 本地实跑，非生产多实例/容量验收 |

尚未执行或完成：Windows/Linux 服务端平台实跑、远端 CI、生产部署与多实例、完整元数据/诊断、容量与 24h soak。世界业务与 WebGL 按用户确认延后；其他客户端平台边界见客户端说明。后续更新本表时注明平台、版本和实际范围，不用旧数量代表新验证。


## 五子棋联调（2026-10-14）

按上述 local-wss 步骤启动同一 Game.Server，两位玩家用不同账号登录。A 创建房间并告知 B 8 位号码，B 输入号码加入；双方准备后房主开始，黑方先行，服务端裁决落子。local-kcp-dtls 不承载房间业务。

GomokuOptions 定义开发限额：64 房间、128 个业务绑定、等待 10 分钟 / 对局 60 分钟 / 终局 60 秒。实际在线连接仍受 WssServerOptions 默认 32 连接、每 IP 4 连接限制。房间不持久化，服务重启失效。

相关测试：`(cd Tools && dotnet test NetworkServer.Tests --filter 'GomokuRoomTests|WssServerTests|AccountLifecycleTests')`；PostgreSQL 路径需 GF_POSTGRES_TEST_CONNECTION，测试只创建 / 删除独立 schema。2026-10-14 使用独立临时 PostgreSQL 最终复验 83/83，包括双 WSS 完整对局、已提交请求取消后查询、已撤销房主绑定的加入拦截。协议测试 200/200；验证过程与平台、容量待验收项见 [五子棋](Gomoku.md#10-已实施与验证边界)。

协议精简后的服务端复验：协议生成检查一致、协议工具 167/167；非 KCP 组 115 项通过（数据库环境条件跳过另行执行），临时 PostgreSQL 的数据库 / WSS 组 69/69，新增路由完整性用例后的账号生命周期组 18/18，真实原生 KCP / DTLS 回环 21/21。当前生产协议共 14 条可靠路由，世界消息未生成；双通道回环使用测试专用 catalog。Unity 使用生产 catalog 与 local-kcp-dtls 的 Hello / Echo / 释放另行实跑通过，详见 [五子棋验证](Gomoku.md#10-已实施与验证边界)。
