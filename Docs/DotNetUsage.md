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
| Failed / Stage=profile validation | 选择 local launch profile，或给可执行程序传--profile；当前没有生产入口 |
| Failed / Stage=configuration loading | 核对 Config/Bytes 或 GF_CONFIG_DIR，确认生成数据与服务端匹配 |
| Failed / Stage=local-wss 或 local-kcp-dtls | 按 ErrorType/StackTrace 定位阶段，核对该 profile 所需环境变量；入口不输出原始异常 Message |
| 数据库连接探测失败 | PostgreSQL 是否运行、端口/连接地址是否正确 |
| 账号表检查失败 | 对指定数据库显式执行初始 SQL，确认连接到了正确库和 schema |
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
application.Rpc(DiagnosticsRoutes.Echo, async (connection, request, cancellation) =>
{
    await Task.Delay((int)request.DelayMs, cancellation);
    return new EchoResponse { Text = request.Text };
});
```

此为局部注册示例，构造 host 前还须显式注册或 Reject 其余入站路由。RPC 返回 DTO，由框架编码 opcode/correlation、准入并释放帧。Notify 支持 NetSend/NetUnreliable；ServerRequestException 表示非零系统拒绝，业务拒绝返回 ResultCode。releaseResponse 用于同步编码尝试后清理敏感响应，请求在 handler 的 finally 清理。

ServerSession 派生类保存每连接应用状态，通过构造 ServerApplication 的 factory 创建。钩子线程、初始化阶段、权限复查、关闭重入约束见 [开发规范连接章节](DotNetDevelopment.md#3-启动连接与关闭)。context 提供 Id/Generation/GrantId/Transport/RemoteEndpoint/Cancellation/Completion，不向业务交付 native handle 或 OwnedFrame。

```csharp
await context.PushAsync(GomokuRoutes.GomokuRoomSnapshot, snapshot);
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

正式 C# 按业务目录生成 CommonMessages.g.cs、SessionMessages.g.cs、GomokuMessages.g.cs、DiagnosticsMessages.g.cs，并以 GameNetProtocol.cs 汇总 catalog/指纹，输出到 `Unity/Assets/Scripts/Hotfix/Network/Generated`；.NET Game.Protocol 通过既有通配引用编译同源源码。默认 generate/check 处理当前全部五个正式文件；--artifacts 额外处理 `Protocol/Generated/protocol.manifest.json`、protocol.sha256、golden-vectors.json，仅开发/CI 使用，不进入 Unity 资产、正式资源或运行时。

Unity 菜单 `Tools/GameFrame/Network/Generate Messages`、`Check Messages` 从 `DotNet/` 工作目录调用同一个生成器，均包含 `--artifacts`；环境与使用步骤见 [客户端协议维护](ClientNetworkUsage.md#7-协议与依赖维护)。生成器和 Roslyn 等依赖由外部 .NET 进程加载，不安装进 Unity Assets 或 Player。测试工程仍在 `Tools/NetworkProtocol.Tests`，CI 从新工程路径执行只读检查。

类型/枚举与模块路由分别使用 `Game.Protocol.Common`、`Game.Protocol.Session`、`Game.Protocol.Gomoku`、`Game.Protocol.Diagnostics`；路由为 SessionRoutes/GomokuRoutes/DiagnosticsRoutes，统一入口保持 Game.Protocol.GameNetProtocol。调用时导入实际使用的模块命名空间，不保留 GameNetRoutes 兼容层。

check 不写文件，缺失、字节差异、生成目录及其子目录内多余 C#返回非零。generate 不覆盖字节相同文件，也不自动删除多余 C#，移除过时 Unity 资产须同时处理.meta 并保留有效 GUID。生成 C#/辅助文件/NuGet 锁纳入版本库；CI 只 check，不自动 generate 掩盖漏生成。

2026-10-16 文件拆分验收：Release `NetworkProtocol.Tests` 176/176 通过、0 跳过，覆盖模块归属、跨模块引用、循环依赖、纯数据模块、无关产物字节/时间戳稳定及过时文件只读检查；`DotNet.sln` 七个工程编译 0 警告/0 错误，GomokuRoomTests、WssLifecycleTests、AccountLifecycleTests 合计 35/35 通过、0 跳过。`check --artifacts` 核验八个文件通过；manifest/hash/golden vectors 与拆分前逐字节一致，Game.Protocol 公开 API 对比 267 项一致。Unity 编译与回归证据见[客户端协议维护](ClientNetworkUsage.md#7-协议与依赖维护)。本次只验收设计第 23 节阶段 1，不表示后续命名、消息结构或预算方案已完成。

2026-10-16 模块命名与路由迁移验收（阶段 2）：Release `NetworkProtocol.Tests` 183/183、服务端 GomokuRoomTests/WssLifecycleTests/AccountLifecycleTests 35/35，均 0 跳过；`DotNet.sln` Release 编译 0 警告/0 错误，`check --artifacts` 八个文件通过。新增实际生成程序集用例覆盖跨模块同名 DTO/枚举/路由、路由与 catalog 两种首次访问顺序、跨模块深度/节点累计；同模块路由成员冲突包含双方完整标识和源文件。过时 C# 检查扩展到子目录，仍只读、不自动删除。42 条黄金向量按类型名映射后编码字节全部一致；manifest 的完整类型名、完整 Proto 路由标识及相应排序变化，使 hash 从 `117aaab69976c151f23fafc46ffb73a79e08ed26a9240bc18589df292c33fa06` 更新为 `5f5e26635a17c672c4362711816dc8826ec2d14d22024ab56c4416930bf45580`。字段、opcode、枚举值与显式预算未变，双端必须同步发布，不兼容旧 hash。Unity 与 Player 证据见客户端协议维护。

2026-10-16 五子棋专用请求验收（阶段 3）：七种请求已贯通 Proto、服务端与 Unity，会话业务 API、14 条路由的 opcode/名称及显式预算保持。Release `DotNet.sln` 编译 0 警告/0 错误；协议测试 198/198（增加项主要来自黄金向量）、房间/账号/WSS 生命周期组最终 36/36，均 0 跳过。WSS in-memory 组 28 项通过，覆盖六操作的有效字段映射、完整对局与取消后显式查询；该批过滤额外命中一个 PostgreSQL fixture 用例，因未配置数据库环境跳过，不能计作数据库验证。真实临时 PostgreSQL/WSS 的双人业务验证由 Unity PlayMode 和专用 Player 完成，见客户端说明。新增回执用例验证同操作的无关字段不改变身份、有效字段或操作改变则拒绝、Leave 忽略版本且容量释放。`check --artifacts` 八文件一致；向量由 42 条变为 57 条（删除旧两种请求的 6 条，新增七种请求的 21 条），36 条共同键的正文逐字节不变，Common/Session/Diagnostics 正式源码字节未变。指纹更新为 `8b03bf9272d287ac253f428f8ed0ff79924cd194f81722594cd035d2b01adde3`，双方必须同步生成、编译与发布，无旧请求或旧指纹兼容。该次验收范围为阶段 3；阶段 4、5 的后续实施与验证见下文。

2026-10-16 自动正文预算与 route 选项验收（阶段 4、5）：正式 service 统一使用 `option (game.net.route) = { opcode: ... kind: ... delivery: ... };`，删除独立路由选项与手写正文预算，不兼容旧写法。生成器从真实 MemoryPack Object/Explicit 布局推导编码长度和连续写入容量上界，含字符串预申请空间、数组/嵌套、可空分支，checked 溢出与通道越界在写产物前失败。类型边界覆盖零长度、ASCII/二字节/中文/代理对各合法重复长度、混合字节余数、null/空数组、最大数组、跨模块对象及可靠/不可靠正文 32736/1118 的边界与边界+1；使用真实 FrameMemoryBudget/BoundedFrameWriter 验证编码、解码与释放。复合选项缺失/重复/未知字段、非法值、旧选项、RPC 不可靠及 Push 响应类型错误均明确失败；字段重排/注释不改变输出。

Release `NetworkProtocol.Tests` 227/227，强化后的共享 NetworkMemoryPackTests 71/71 另行复验；服务端 GomokuRoomTests/WssLifecycleTests/AccountLifecycleTests/WssServerTests 合计 92/92，均 0 跳过，WSS 同时覆盖内存仓库与独立临时 PostgreSQL schema。DotNet.sln Release 编译 0 警告/0 错误，check 八文件一致，重复 generate 的内容与时间戳不变。57 条 golden vectors 与阶段 3 逐字节一致，manifest 仅路由预算变化，类型/枚举/路由标识/opcode 保持。新 hash 为 `d3fd187267c9a26b59f1554fbc92ba6f48812c2e94643cff2456c2aa08be9d2b`，双端需同步发布。

| 当前生成预算 | 请求 / 响应正文上限（字节） |
| --- | --- |
| Login / Register | 596 / 101、596 / 5 |
| ResumeLogin / Logout | 57 / 101、1 / 5 |
| Echo | 397 / 396 |
| Create / Join / Query 五子棋 | 9 / 3094、44 / 3094、9 / 3094 |
| Ready / Start / Move / Leave 五子棋 | 各 116 / 3094 |
| 五子棋两条快照 Push | 各 3080 |

这些数值是当前字段和 writer 行为推导的保守预算，不作为吞吐/GC/生产容量提升证据。未新增运行时分配层、注册框架或订阅容器；现有显式注册和业务订阅所有权足够清楚。Unity 与 Player 脚本证据见客户端协议维护；本轮未重跑原生 IL2CPP、KCP/native、正式发布、包后热更或其他平台。

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

Unity 客户端另有独立进程的 [macOS Release IL2CPP 性能入口](ClientNetworkUsage.md#network-player-performance)。其 Explicit `NetworkPlayerPerformanceHostTests.ServePerformance` 由现有 `Tools/network_player.py --mode performance` 启停，复用正式框架和现有 Echo/可选 GomokuSnapshot；不运行 Game.Server、数据库或正式房间逻辑，不增加生产路由。KCP 每连接独立短时随机grant，WSS 使用调用方提供且已受Player信任的localhost PFX；两端关闭/自有资源与会话错误均核验。具体参数、测量边界和证据集中在客户端说明，不用独立客户端的短时结果替代生产容量或本节服务端owner指标。

meter 为 `GameFrame.Server.Network.Kcp`，使用 MeterListener 或现有 .NET 指标采集器订阅。安装了 dotnet-counters 的环境可运行 `dotnet-counters monitor --process-id <PID> --counters GameFrame.Server.Network.Kcp`；该工具是额外诊断依赖，不是服务启动依赖。

| Histogram | 单位 | 采样范围 |
| --- | --- | --- |
| gameframe.kcp.owner.work | ms | 每轮命令、UDP、native tick、子连接推进与清理，排除 WaitOne 与 owner 指标回调 |
| gameframe.kcp.owner.interval | ms | 相邻 owner 循环开始的间隔，包含工作、等待与线程调度 |
| gameframe.kcp.owner.allocated | By | 每轮 owner 线程新增托管分配，排除 owner 指标回调、应用任务及 native 分配 |
| gameframe.kcp.send.queue | ms | 业务帧入发送队列至 native 接受，排除控制帧；不代表客户端收到 |
| gameframe.kcp.dispatch.queue | ms | 入站业务帧 native 时间戳至开始派发，包含初始化/应用排队；过期不可靠帧不采样 |

指标只在订阅启用时采样，按进程聚合，不含账号或连接标签。owner 工作/分配仍包含循环内 Admit/CheckDeadline 和发送排队指标回调的成本，采集器回调应保持轻量；另测进程 CPU/RSS、.NET GC/停顿与应用/数据库阶段。当前保留 10ms 轮询策略；性能调整需结合目标连接数、业务量和弱网样本，不能用单连接 loopback 数据推断生产容量。

<a id="server-logging"></a>

### 服务端日志（NLog）

两个 local profile 已统一使用 NLog 6.2.1 / NLog.Extensions.Logging 6.2.1。单一 `GameFrame.Logging` 工程负责业务无关的选项、元数据校验、NLog Provider、布局、有界异步输出与关闭，类型统一位于 `GameFrame.Logging` 命名空间；`Game.Server/Configuration/ServerLogging.cs` 读取部署值。网络/业务继续注入标准 `ILogger`，不依赖 NLog 类型。详见 [职责边界](DotNetDevelopment.md#server-logging-architecture)及 [执行进度](DotNetDevelopment.md#server-logging-plan)。

| 环境变量 | 默认值 | 行为 |
| --- | --- | --- |
| `GF_LOG_LEVEL` | `Information` | Trace/Debug/Information/Warning/Error/Critical/None，忽略大小写；None 禁用全部类别 |
| `GF_LOG_FORMAT` | `Text` | Text 或 Json，忽略大小写；Json 每条事件一行 |
| `GF_LOG_QUEUE_CAPACITY` | `4096` | 整数 1～65536；满额丢弃新事件，所有级别适用 |
| `DOTNET_ENVIRONMENT` | `Local` | 仅作为日志 Environment 字段，不改变 local/生产准入策略 |

全局和类别阈值取较严格者；应用将 Microsoft.AspNetCore/Npgsql 默认设为 Warning，其他类别沿用全局级别。级别/格式不做运行中热重载。实例 Service=Game.Server，InstanceId 为每次启动的新 GUID，Version 为程序集 informational version；元数据最多 128 字符且不含控制字符。无效配置在 profile/Tables/监听之前失败，以固定 stderr 诊断并退出 1，不切换备用输出。

沿用前文证书、数据库、grant 和 native 配置后可执行：

```sh
(cd DotNet && GF_LOG_FORMAT=Json GF_LOG_LEVEL=Information dotnet run --project Game.Server --no-launch-profile -- --profile local-wss)
(cd DotNet && GF_LOG_FORMAT=Json GF_LOG_QUEUE_CAPACITY=4096 dotnet run --project Game.Server --no-launch-profile -- --profile local-kcp-dtls)
```

JSON 外层包含 Timestamp（UTC）、Level、Category、Message、MessageTemplate、Service、Environment、InstanceId、Version 和存在时的 Exception；EventId/EventName、模板参数及 Scope 属性直接保留为结构化字段。文本包含公共实例字段、类别、渲染消息、属性/Scope JSON 与异常。日志调用必须使用固定模板和小型非敏感值，不传池化 DTO、可变数组或资源对象，不在业务锁/native owner 内等待输出或刷新。

| 宿主 EventId / EventName | 级别 | 事实与字段 |
| --- | --- | --- |
| 2000 / Starting | Information | profile 校验后开始初始化，含 Profile；不代表监听成功 |
| 2001 / Stopped | Information | 服务及外部资源释放后停止，含当时 DroppedCount；不代表全部输出已经刷新 |
| 2002 / Failed | Critical | 启动/运行失败，含 Stage、ErrorType、StackTrace |
| 2003 / KcpReady | Information | KCP/DTLS 已监听，含 Endpoint |
| 2004 / WssReady | Information | WSS 已启动，含 Port |

应用入口失败不再向进程抛出未处理异常，返回退出码 1；为避免第三方异常 Message 泄漏配置，仅记录阶段、类型与堆栈。模块内部仍按原错误契约传播异常。日志初始化失败、刷新/输出失败使用固定安全 stderr，后者也返回 1。网络故障出口现使用 [1000～1003 事件](DotNetDevelopment.md#server-network-fault-events)，关联连接/代际、Opcode/CorrelationId 与 Stage。Provider 只接收 ExceptionType/ExceptionStack，原异常保留给 Completion/调用方；Message、内链、Data 和自定义异常格式化不输出。业务/第三方日志、正常生命周期事件与洪峰成本仍待完善，测试标记不泄漏不等同于完成所有第三方内部安全审计。

应用拥有独立 NLog LogFactory 和 ILoggerFactory，不使用全局 LogManager。WSS 清除默认 Provider 并注册外部 Factory 实例，宿主不释放它；KCP listener 也只借用。关闭顺序为停止准入、join 网络/业务、释放宿主与业务/数据库/证书、记录停止、刷新并释放日志。重复 `DisposeAsync` 等待同一结果。

适配默认批次 200（应用取 min(容量, 200)），刷新等待 5 秒；这些参数也可在组合代码通过 `NLogOutputOptions` 显式设置。FlushTimeout 只约束刷新等待，不保证任意 Target.Dispose 的总时间。队列条数加在处理批次不等于精确内存预算，异常和字符串大小仍需限制；入队不代表写出或持久化，强杀/崩溃可能丢失日志。当前无自动文件输出、NLog.config 加载或默认指标导出器；文件 Target 可显式接入适配入口，轮转、保留与磁盘故障需另行配置验收。

`NLogLogging.DroppedCount` / `OutputErrorCount` 和 meter `GameFrame.Logging.NLog` 下的 `gameframe.logging.dropped`（event）/`gameframe.logging.output.errors`（error）提供计数，部署采集端自行订阅。Meter 名称是保留的指标标识，不是独立工程或命名空间。队列满和输出故障不递归写日志；适配保留首个输出异常，刷新成功后仍会报告它。输出故障不重新执行业务，也不改变业务已提交结果。

2026-10-14 本机 Release 样本：预热 1000 次后，100000 次被过滤的源生成 Debug 调用耗时 4.019ms，当前线程新增托管分配 0 字节；这是单线程调用路径样本，不能代表开启输出、异常格式化、RPC P99 或生产容量。慢 Target 测试验证队列满时生产者继续完成且丢弃计数可订阅；完整对照负载、资源峰值与性能门禁仍按 [性能要求](DotNetDevelopment.md#server-logging-performance)执行。

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

<a id="network-load"></a>

### 9.1 可重复的 WSS / KCP 负载入口

从仓库任意工作目录调用 `Tools/network_load.py`（路径按当前目录调整）。入口使用现有 NetworkServer.Tests 中显式选择的 SustainedEcho、生成 Echo 协议和客户端 ConnectionContext。默认WSS走真实Kestrel/TLS；KCP走真实UDP/DTLS、KcpListenerHost和共享客户端KcpWorker。每次重复独立建连、预热、排空、测量、排空、关闭并核验资源，不需要Unity或数据库。WSS使用每轮临时证书，仅本次客户端按指纹接受，不修改系统信任；KCP需已核验的宿主原生库，每连接即时注册独立随机单次grant，建连结束清零托管PSK/identity/ticket，关闭listener使其余授权失效，不保存或打印凭据。测试应用处理Echo、拒绝其他入站路由，不经过正式登录或房间业务。

```sh
python3 Tools/network_load.py
python3 Tools/network_load.py --connections 16 --rate 20 --warmup 10 --duration 60 --repeat 3
python3 Tools/network_load.py --connections 4 --duration 10 --json /absolute/path/report.json
python3 Tools/network_load.py --scenario slow-handler
python3 Tools/network_load.py --scenario slow-handler --slow-handler-ms 100 --rate 5 --duration 10
python3 Tools/network_load.py --scenario idle
python3 Tools/network_load.py --scenario idle --connections 64 --duration 30 --repeat 2
python3 Tools/network_load.py --scenario burst
python3 Tools/network_load.py --scenario burst --connections 16 --burst-size 8 --burst-interval-ms 250 --warmup 1 --duration 3 --repeat 2
python3 Tools/network_load.py --scenario close-storm
python3 Tools/network_load.py --scenario close-storm --close-mode server --connections 64 --warmup 1 --duration 3 --repeat 2
python3 Tools/network_load.py --scenario close-storm --close-requests 0 --connections 64 --duration 3
python3 Tools/network_load.py --transport kcp --native-library /absolute/path/libgameframe_kcp.dylib
python3 Tools/network_load.py --transport kcp --native-library /absolute/path/libgameframe_kcp.dylib --connections 16 --warmup 1 --duration 3 --repeat 2
python3 Tools/network_load.py --transport kcp --native-library /absolute/path/libgameframe_kcp.dylib --scenario idle --connections 64 --duration 12 --repeat 2
python3 Tools/network_load.py --transport kcp --native-library /absolute/path/libgameframe_kcp.dylib --scenario slow-handler
python3 Tools/network_load.py --transport kcp --native-library /absolute/path/libgameframe_kcp.dylib --scenario burst
python3 Tools/network_load.py --transport kcp --native-library /absolute/path/libgameframe_kcp.dylib --scenario close-storm --close-mode server
python3 Tools/network_load.py --transport kcp --native-library /absolute/path/libgameframe_kcp.dylib --network-profile mixed --network-seed 42 --rate 10 --warmup 1 --duration 3 --repeat 2
```

| 参数 | 默认值 / 范围 | 含义 |
| --- | --- | --- |
| --transport | wss / wss、kcp | 两者均支持全部五种场景；KCP最多64连接 |
| --native-library | 不自动选择库 | 仅KCP：原生库绝对/相对路径；也可用GF_KCP_NATIVE_LIBRARY。相邻需有artifact.json和build-manifest.json |
| --scenario | echo / echo、slow-handler、idle、burst、close-storm | 持续Echo、单慢处理器、空闲心跳、同相位突发或集中关闭 |
| --network-profile | direct / direct、loss、delay、jitter、reorder、mixed | 仅KCP；direct直连，其余通过有界UDP转发器在建连后注入 |
| --network-seed | 1 / 1～2147483647 | 仅KCP非direct；逐连接、逐方向的固定包规则种子，显式指定时必须同时选择注入profile |
| --close-mode | client / client、server | 仅close-storm：客户端主动关闭或服务端按reason=3通知关闭 |
| --close-requests | 8 / 0～128，且不超过inflight | 仅close-storm：持续测量排空后，每连接额外准备的在途请求数；0表示空闲关闭 |
| --burst-size | 8 / 1～1024 | 仅burst：每连接每批计划请求数 |
| --burst-interval-ms | 1000 / 1～60000 | 仅burst：批次间隔毫秒数 |
| --slow-handler-ms | slow-handler默认25、burst默认不延迟 / 显式1～30000 | 服务端client 0每条Echo的异步等待毫秒数，仅slow-handler或burst可用；slow-handler至少2个连接 |
| --connections | 4 / WSS 1～128、KCP 1～64 | 物理连接数 |
| --rate | Echo/慢场景/集中关闭20 / 1～10000；idle/burst为0 | 持续窗口每连接每秒计划请求数，不随响应变慢而降低；idle/burst只接受0 |
| --warmup / --duration | 2 / 0～300；10 / 1～3600，idle默认30且至少6 | 预热 / 正式窗口秒数，预热单独统计；idle预热也不发业务请求 |
| --repeat | 1 / 1～10 | 同参数重复次数，每轮独立建连 |
| --payload-bytes | 128 / 16～128 | ASCII Echo Text 的字节数，不含序列化/帧头；遵循现有协议128字符限制，idle结束后的探测也使用此值 |
| --inflight | 8 / 1～1024 | 每连接工具保留的最大在途任务数，总数不得超过8192；不修改客户端内部预算。idle窗口没有在途业务，结束探测每连接仅一次 |
| --request-timeout | 3 / 1～30 | 单个RPC超时秒数 |
| --pump-ms | Echo/慢场景/突发/集中关闭1、空闲20 / 1～100 | 每轮客户端Pump后的请求等待毫秒数，实际调度间隔可能更长；对照时保持一致 |
| --json | 默认不写文件 | 显式导出完整报告，父目录必须存在；覆盖指定文件 |

脚本固定 Release 和 locked restore，SDK 沿用 Tools/global.json；可用 DOTNET_EXECUTABLE 指定 dotnet。使用独立临时 runsettings/results 目录并在结束后清理。长负载是 Explicit 用例，普通 dotnet test 不执行；常规回归包含短时四客户端冒烟、分组统计、可控处理器等待隔离、慢组超时、真实空闲心跳/探测、突发负载，以及客户端/服务端集中关闭的空闲/在途与资源释放用例。

KCP入口在启动.NET前只读核验：宿主平台/架构、Release非sanitizer、ABI2、二进制hash、artifact与build清单一致、build ID、安装器同源的源码清单及维护的KCP源码指纹。不安装或替换插件，不生成Python字节码；缺库或不匹配返回2，不将原生测试跳过当作成功。报告Native保存ABI、build ID、二进制SHA256及源码核验状态；C#加载后再执行现有ABI校验。直接dotnet test仍沿用GF_KCP_NATIVE_LIBRARY约定，缺环境变量的原生集成用例明确跳过，运行证据须注明。

计划时槽按单调时钟和每连接固定速率计算，各连接同相位发起；不按请求完成后再发送。调度落后时只尝试最新到期时槽，旧时槽计 GeneratorLateSkipped，不突发补发；工具在途满计 InflightLimitSkipped，与网络背压区分。Planned 必须等于 Attempted + 两类 Skipped；Attempted 必须等于 Succeeded + Backpressure + TimedOut + Canceled + Failed，Unresolved 为0。所有成功回复核对 Text。出现跳过、请求失败、错误回复、服务端故障、关闭超时或资源未归零时返回非零，报告不会用较少的实际请求宣称负载已满足。为保证正常基线有效，参数不能超过所记录的协议/速率/资源预算；高负载主动触发背压时预期失败。

burst按固定批次时钟发压：每阶段在t=0发首批，窗口为[0,秒数)，每连接计划数为ceil(秒数×1000/间隔毫秒)×批大小，预热为0时不发预热批次。每轮Pump后用同一个时钟样本确定各连接到期批次，再依序调用各连接Request；单连接批内不await、不Pump，也不等待响应。此为同相位依序发起，不承诺多线程瞬时同时发送。错过的旧批次按请求数计GeneratorLateSkipped，不合并补发；在途满时该批剩余请求计InflightLimitSkipped，批内越过窗口截止的剩余请求计GeneratorLateSkipped。

报告SchemaVersion为7，burst额外提供BurstClientIssueLatency：每连接处理一个最新到期批次的工具发起耗时，包含逐条调用、在途/截止检查和跳过判断，包含未完整发出的批次；不含响应等待，也不是整波所有连接耗时或计划时刻到实际派发的延迟。该字段沿用有界直方图，其他场景为null。平均计划速率约为批大小×1000/间隔毫秒，但短窗口和阶段边界可能更密集：预热排空后测量重新从t=0发首批，不保证两阶段相邻批次相隔完整interval。还须考虑控制流量、瞬时队列和服务端默认限流，平均速率低不能保证负载健康。

负向示例：`--scenario burst --burst-size 16 --inflight 4 --warmup 0 --duration 1`验证工具在途跳过；`--scenario burst --connections 2 --burst-size 4 --slow-handler-ms 400 --request-timeout 1 --warmup 0 --duration 1`验证慢连接超时与正常连接隔离；`--scenario burst --connections 1 --burst-size 1024 --inflight 1024 --warmup 0 --duration 1`尝试触达默认预算。均预期返回1并保留报告。分别检查工具跳过、NetworkBackpressureException对应的Backpressure、超时和其他故障终态；超额突发也可能触发服务端限流/关闭，不能把所有失败都称为背压，或据此断言触发了唯一限额。工具允许更大的inflight仅用于观察真实预算，不放宽正式网络配置。

每轮输出参数、预热/测量统计、成功及失败延迟、采样峰值、关闭耗时和最终资源计数。控制台完整报告与可选JSON均包含版本/工作区是否有改动、SDK/runtime/OS/架构、协议指纹及完整客户端/服务端限额。WSS只调整测试host总连接、每IP和受限连接名额；KCP服务端使用完整默认选项，测试客户端共享worker的容量按连接数设置并写入报告，不修改Runtime默认宿主容量。包/字节速率和内存上限仍按报告执行（服务端每连接默认128包/秒、128KiB/秒，托管全局64MiB）。KCP还共享来源IP的2048 UDP包/秒、2MiB/秒入口预算，回环全部客户端共用127.0.0.1，ACK/握手/重传也会占入口包数；不能把应用请求速率当UDP包数，也不为大连接数放宽该默认预算。

测量口径：SuccessfulRps 是成功数除以发压窗口加排空耗时，CompletedDuringOfferRps 只统计窗口内观察完成的成功数；延迟从调用 Request 前到工具观察终态，包含编码、解码、Pump和轮询等待，不是纯网络RTT。分位数使用固定2048桶直方图，输出桶上界，桶宽不超过3.125%（最小1微秒）；无样本时分位数为null，不从平均值推算。成功/失败耗时分别统计，失败不丢弃。CPU、分配、GC和工作集包含同进程客户端、服务端、测试宿主和采集开销，统计窗口含测量及排空；队列/内存/工作集每约100ms采样，SampledPeak不是所有瞬时峰值，不是RSS硬预算。日志关闭；本入口不测NLog开销或Unity帧预算。

慢处理器场景复用现有 EchoRequest.DelayMs，仅测试host使用可取消的 Task.Delay；模拟异步IO等待，不代表CPU密集处理或线程池耗尽。每连接仍串行派发，慢连接后续请求会等待；其余连接照常发压。预热和测量分别给出 Normal/Slow 的计划数、全部终态、延迟及客户端pending/队列采样峰值；echo与未指定延迟的burst全部计入Normal，Slow为null；burst指定延迟时client 0单列Slow。总计合并两组请求数及直方图，总队列峰值按同一时刻独立采样，不累加两组峰值。进程CPU/GC/分配/工作集及模块自有内存仍只在总计报告，不归因到某一组；当前不提供服务端每连接队列深度，客户端pending不能当作该值。

两组各自的DrainSeconds截至本组最后观察到的请求终态，最低为0，OfferSeconds均为指定窗口；总计排空直到所有组结束。因此正常组SuccessfulRps不会因慢组独自排空而被额外摊薄，窗口内吞吐优先比较CompletedDuringOfferRps。慢组同样要求无跳过、无失败；有意超载返回1，即使正常组健康也不将整个负载判为通过。可分别运行相同连接数/速率/时长的echo和slow-handler作为对照，观察正常组分位延迟及每连接吞吐；不自动设定跨机器性能阈值。

例如 `--scenario slow-handler --slow-handler-ms 100 --rate 20 --inflight 2 --warmup 0 --duration 2` 可观察慢组在途满额跳过；`--scenario slow-handler --slow-handler-ms 1500 --rate 2 --request-timeout 1 --warmup 0 --duration 1` 可观察慢组超时。两者预期返回1，需结合Normal.Healthy、各类失败计账、ResourcesReleased及ServerErrors判断隔离和收尾。排空等待客户端请求终态；请求超时不承诺取消服务端已开始或排队的业务，预热发生超时时后续测量可能仍承接该积压，整轮依然判失败。测试延迟在连接关闭时由会话token取消。

空闲场景使用原有5秒GFNP心跳，不发业务请求，不修改框架定时或超时参数。默认预热2秒、测量30秒，测量至少6秒；每连接在测量窗口必须观测到双向各至少80字节的应用流量（空闲阶段只有40字节的Ping/Pong），且采样连接数始终完整、业务正文/接收业务帧增量为0、客户端业务队列/pending/deadline采样峰值为0。此为依据现有字节快照推断的双向控制活动，不新增逐Ping/Pong计数或RTT分位数；不能用最后RTT为0判定心跳未运行。预热窗口可以短于心跳周期，因此不要求它必有心跳活动。测量结束后，每连接并发发出一次Echo，核对正文与终态，单列PostIdleProbe，排除在空闲CPU/分配/GC、内存和队列统计之外；该探测延迟含默认20ms Pump间隔，不能当成默认Echo场景的延迟对照。

空闲窗口额外输出Idle对象：连接数采样最小值、客户端/服务端模块自有内存的开始/结束/采样最小最大值及变化、双向应用字节增量、控制活动连接数及业务空队列检查。WorkingSetStart/End/SampledPeak用于观察进程内存；ElapsedSeconds为实际采集时长，CpuCorePercent=进程CPU秒/ElapsedSeconds×100（100%等于一个逻辑核），AllocatedBytesPerSecond按同一时长计算。包括客户端、服务端、测试宿主、Pump轮询与约100ms快照的成本，不能直接当作服务端每连接成本。短窗口的起止变化和采样范围用于对照，不设置通用增长阈值，也不据此宣称无内存泄漏或长时稳定性。空闲Measurement中的业务计划数/成功数为0，延迟分位数为null；整轮Healthy还必须满足Idle检查、PostIdleProbe全部成功、无服务端故障及关闭资源归零，空计数不能自动通过。

KCP沿用上述五种场景的发压、请求计账、预热/排空、关闭、窗口外Echo和心跳判据。每阶段额外输出Kcp对象：客户端native实际内存起止/采样范围、native预约起止、waitsnd与native队列采样峰值、窗口内超时重传及socket字节增量。重传仅TimeoutRetransmits，不包括fast retransmit；ACK/握手等UDP流量不能当作业务帧/正文。native实际值不与OwnedBytes中的预约额度重复相加。采样约100ms，并非所有瞬时峰值。

Kcp对象同时通过现有MeterListener采集ServerOwnerWork/ServerOwnerInterval（毫秒）、ServerOwnerAllocated（字节）、ServerSendQueue/ServerDispatchQueue（毫秒）；存储固定有界直方图，不保留每轮历史。分配分位数为桶上界，最小桶尺度1字节；其他分位数规则同延迟。owner指标和排队指标的范围见第8节，进程CPU/分配仍含客户端、server、测试宿主与采集器；应用线程/采集回调未计入owner分配不代表进程没有该成本。只有本次阶段订阅期间的owner样本进入报告，预热和测量分别统计，空闲阶段业务排队样本应为空；这些指标按进程聚合，负载应独占执行，不能同时跑其他KCP host来做对照。

服务端未公开listener/native动态内存及原生连接槽快照，Kcp.ServerNativeOwnedBytes明确为null；KCP连接数来自本轮已发布ServerConnectionContext的Completion状态，是会话存活代理，不冒充原生槽计数。最终KcpCompletion核验客户端native实际/预约/native队列归零，以及共享worker与listener实际停止；服务端托管OwnedBytes另验归零。listener停止包括既有原生destroy成功及应用join，不能据此声称实测了服务端原生live bytes或完成native泄漏/soak验收。本入口不调用私有句柄、不扩展ABI来补造指标。

KCP非direct配置使用测试专用KcpLoadRelay：每连接一对稳定前后端UDP socket，服务端仍看到同一个回环来源IP，不绕过正式来源IP预算。转发器只处理不透明UDP字节，不解密、不生成或重试业务请求；可靠恢复仍由正式KCP/DTLS完成。全部连接建立后开始注入，覆盖预热、测量及窗口外空闲探测；关闭准备前停止新增注入，已排队的数据仍按原期限转发。该负载路径在建连和关闭阶段旁路注入；分阶段Hello/Close/断流另用下文fault入口验证。

| profile | 每方向、每连接的规则 |
| --- | --- |
| direct | 原有直连，不创建转发器 |
| loss | 按包序号及种子相位，每20包丢1包；为周期性丢包，不是随机5%概率 |
| delay | 每包额外延迟40ms，双向均应用 |
| jitter | 每包额外延迟20～60ms，固定整数序列产生抖动 |
| reorder | 每5包选1包额外延迟80ms，让后续包有机会先转发 |
| mixed | 延迟20～40ms、每20包丢1包、每5包选1个未丢弃包再延迟80ms |

种子为逐连接/逐方向包序列的起点，相同种子和相同到包序列得到相同决定；线程、socket、ACK合包与重传调度仍会改变实际到包序列，不承诺重复运行的包数、延迟或重传数完全相同。没有命中配置的丢包/实际乱序、没有观察到抖动变化或没有注入流量时，Relay.ProfileExercised=false，整轮失败；低流量短窗口可能需要延长时长，不将未触发当作验证通过。OutOfOrderPackets统计每条连接每方向实际转发时较此前最大序号落后的包，ReorderScheduledPackets仅表示计划延后，两者分开。

报告SchemaVersion7增加NetworkEmulation配置与每轮Relay结果；direct/WSS的Relay为null。Relay统计包含建连旁路、预热、测量、探测及关闭转发的完整生命周期，不能当作仅测量窗口的业务或重传数。记录接收/转发/主动丢弃、延迟/计划乱序/实际乱序、注入延迟范围、有界队列峰值、停机丢弃及socket/owner完成。共享队列硬上限4096包且正文合计4MiB，不含对象和socket内核缓冲；溢出丢新包并令整轮失败，不冒充配置的网络丢包。socket/owner故障原样使测试失败；客户端与worker停止后join转发器，再停止listener，避免向已关闭端点继续发送延迟包；清理剩余延迟UDP并单列DiscardedAtStop，最后socket已释放、队列包数/字节为0。接收包数必须等于转发+配置丢包+停机丢弃（健康运行无溢出）。关闭不依赖提前丢弃转发器队列来伪造完成。

弱网结果仍要求各业务请求按现有规则终结、无跳过/失败且最终资源释放；不增加允许超时的成功开关。CPU/分配/RSS包含转发器轮询与复制成本，原有Kcp统计仍仅表示正式客户端native和服务端meter，不把转发器成本归因于生产网络实现。此为软件UDP注入验证，不等同于真实WAN、带宽瓶颈、完全断网或所有DTLS重放/安全场景。

close-storm先执行普通Echo预热与持续测量并排空，再进入独立CloseStorm阶段，不将关闭探测请求混入持续窗口的吞吐/CPU/分配。每连接准备指定数量请求，测试处理器在可取消的门闩上等待；准备阶段最多等待min(10秒, RPC超时)，核验每条连接首个处理器均已进入、所有指定请求仍未完成，且PendingAtClose等于计划数，才记Prepared=true。close-requests=0时核验连接仍存活后直接关闭。测试门闩只在host中使用，不新增生产协议字段、延迟或框架接口。

client模式依序对全部客户端BeginClose，再释放测试门闩，让服务端按既有正常关闭语义排空已接收的可靠业务；尚未送达服务端的请求不承诺派发。server模式依序对所有会话RequestClose(3)，保持门闩等待，由连接取消信号结束处理器，并核验未继续派发排队业务。两者都先发起所有关闭，再通过Pump等待两端实际完成；是同一owner依序触发，不承诺瞬时同时关闭。服务端模式在收到关闭终态后调用客户端BeginClose完成本地释放。

CloseStorm的UnexpectedClientFaultDetails最多按连接保存一次安全诊断，记录异常类型/堆栈、关闭原因及native状态/错误码，不序列化异常Message、Data或凭据。CloseStorm单列PlannedRequests、AttemptedRequests、PendingAtClose、ClosedRequests、全部非预期终态及Unresolved；只把触发前仍未完成的请求在受控关闭后得到的预期关闭异常记为ClosedRequests。客户端模式使用现有BeginClose的精确NetworkException基类，并另验客户端无故障通知；服务端模式要求NetworkConnectionClosedException.ReasonCode=3，并核验两端关闭原因。成功响应、背压、超时、调用取消或其他异常不算预期关闭，分别保留计数。Prepared失败、请求计账不平、任一非预期结果/服务端故障、处理器未退出或10秒关闭等待期限内未join均判失败；不把受控关闭扩大为“允许失败”开关。持续窗口仍要求全部请求成功，最终仍要求连接/pending/deadline/队列及模块自有内存归零。

PrepareSeconds统计探测发起与准备屏障耗时；IssueSeconds统计遍历发起关闭的工具耗时；TotalSeconds从第一次关闭调用前计时直到两端全部被工具观察完成，包含IssueSeconds、Pump和轮询等待。ClientCloseLatency/ServerCloseLatency也从同一全局触发时刻起算，使用有界直方图，分别以客户端CloseTask与IoTask、服务端Completion实际完成为观察点，不是每连接独立调用耗时或纯传输关闭RTT。HandlersEntered为触发前已进入处理器数，HandlersStarted/Finished/Canceled为关闭阶段最终计数；服务端模式必须只派发每连接首条且全部由取消退出。Run.CloseSeconds沿用最终兜底清理及停止host的耗时，close-storm主体耗时单看CloseStorm.TotalSeconds。有限短时数据不承诺通用关闭性能阈值。

目前WSS/KCP均覆盖持续Echo、单慢处理器、空闲连接、突发及集中关闭，KCP另支持建连后的丢包、延迟、抖动、乱序及混合注入。吞吐与尾延迟暂作基线；分阶段握手/关闭丢包及断流另见fault入口，多房间业务、独立发压进程、生产容量与长时验收仍待扩展。比较不同连接数时逐次执行相同预热/时长/重复次数/Pump间隔的idle命令；比较优化前后时使用相同机器、参数与输出环境，保留各次重复结果，不用loopback数据推断在线人数。

<a id="network-fault"></a>

#### KCP 分阶段故障验证

同一入口新增 `--scenario fault`，只接受KCP。它是固定契约矩阵，不采集吞吐基线；只可组合 `--fault-case`、`--repeat`、原生库与JSON参数。连接数、速率、预热/时长、RPC超时、Pump、network-profile/seed、burst/close等负载参数显式传入时拒绝，避免无效参数看似生效。原Echo等场景仍使用上节负载报告。

```bash
python3 Tools/network_load.py --transport kcp --native-library /absolute/path/libgameframe_kcp.dylib --scenario fault --repeat 2 --json /tmp/kcp-fault.json
python3 Tools/network_load.py --transport kcp --native-library /absolute/path/libgameframe_kcp.dylib --scenario fault --fault-case reconnect
```

`--fault-case` 默认all，每轮按下表执行12项；单项可独立重复。每项独立host/worker/转发器，保留一条不受注入影响的连接，每500ms串行发起一次控制Echo（超时3秒），故障连接最终关闭后，显式用新grant、conv、端点和generation=2建立新连接并验证Echo。整个故障观察期间持续Pump，不提前停host代替对端自然故障；握手失败后显式撤销本次grant再join已发布会话。原生grant和客户端凭据按现有所有权清理，不复用旧授权、不自动重连或重发业务。

| case | 注入与预期 |
| --- | --- |
| dtls-loss | 连接前双向各丢首个UDP包；DTLS/KCP自行恢复，Hello与Echo成功 |
| dtls-cancel | 建连前双向100%丢包，确认收到并丢弃UDP后取消；Connect保留调用者取消token |
| dtls-timeout | 建连前双向100%丢包，不取消；按原有10秒期限失败 |
| hello-cancel | 测试factory暂停会话发布，已收到ClientHello后切断服务端→客户端，再发布ServerHello；取消保留调用者token |
| hello-timeout | 同上，不取消，按原有5秒GFNP Hello期限失败 |
| outage-recover | 已连接后双向100%丢包3秒；原RPC先保持在途，放行后在8秒请求期限内恢复成功，不发起替代请求 |
| outage-timeout | 持续双向100%丢包；使用30秒RPC期限，要求先到默认心跳期限并结束RPC，两端各自故障，正常连接保持可用 |
| close-client-loss / close-server-loss | 确认目标handler进入、RPC在途后，在关闭阶段双向断流300ms再恢复；本地关闭RPC按原契约结束，收到远端Close时保留reason=3 |
| close-client-blackhole / close-server-blackhole | 关闭阶段持续双向100%丢包；发起端仍有界释放，对端独立到心跳期限；未送达的关闭原因不能凭空出现在对端 |
| reconnect | 暂存旧连接服务端→客户端的UDP直到旧RPC超时1秒；旧客户端Close并释放后，由测试授权端撤销旧grant并等待旧服务端会话结束，再建立新generation、放行旧UDP，验证新Echo和旧资源状态 |

故障阶段控制只影响目标连接，转发器不解析加密正文。关闭300ms断流覆盖该阶段全部UDP，不能将包数解释为精确Close控制帧数。完全断网时，发起端约2秒尽力关闭与对端约20秒独立心跳失效是不同观察点；测试记录ClientIoMilliseconds/ServerCompletionMilliseconds，并用有余量的观察期限检测挂起，不把这两个耗时混为关闭RTT。默认传输、Hello、心跳、关闭期限及正式预算均不变。Hello测试的同步factory屏障仅用于制造确定的发布时序，不推荐业务在factory阻塞。

fault报告独立使用 `SchemaVersion=1`、`Scenario=kcp-fault`，包含所选case、原生产物证明、机器/版本信息、固定配置和每项结果；不是原负载Schema7的Runs结构。每项保留预期故障类型、可送达的关闭原因、控制Echo次数、故障及两端完成耗时、旧客户端/服务端释放、新代Echo、请求终结、会话Closed计数、listener/worker停止与资源归零。reconnect中的旧授权撤销可能使旧服务端以NetworkException结束，此结果只在已确认撤销的目标旧代接受；正常对照与新代关闭必须reason=1且无错误。故意注入的网络失败符合对应case契约才记Healthy=true；执行/断言/清理错误仍整轮失败并返回1，JSON保留ErrorType/Stack及独立清理错误类型，不输出异常Message/Data、grant或正文。

转发器新增逐方向故障丢包、HeldPackets及ReleasedHeldPackets计数，direct故障转发器以FaultExercised核验实际断流/暂存，持续profile仍以ProfileExercised核验，Healthy使用相应条件；暂存仍计入原4096包/4MiB共同上限；显式释放后发回原连接端点，不改投新连接。AccountingValid核验收到的包均归属转发、配置/故障丢弃、溢出、停机丢弃或当前队列。最终所有旧/新context的IO与Close已完成、pending/deadline/队列与native实际/预约/Waitsnd归零，控制/新连接随测试结束清理，host完成全部已发布会话Closed后才判资源释放。服务端native live bytes仍未直接测量。

`OldServerReleasedBeforeReconnect`表示新授权使用前旧服务端已发布会话的Completion已完成；握手阶段尚未发布会话时该条件也成立。`UnexpectedSessionResults`单独核验正常对照与新代的服务端关闭原因和错误、客户端状态错误，以及要求正常结束的目标旧代，必须为0。

这里执行的是同进程真实KCP客户端核心generation，不等同于Unity公开NetworkClient、IL2CPP、换网或跨进程世界恢复验证；公开客户端的Close→Connect生命周期由Unity测试单独覆盖。

Unity公开NetworkClient的实际故障验证另见[PlayMode入口](ClientNetworkUsage.md#unity-kcp-fault)：使用本测试工程的Explicit `KcpUnityFaultHostTests.ServeUnityFaults`作为独立临时伴随进程，仍复用原有有界UDP转发器和正式Echo，不修改Game.Server的生产/本地启动profile。不要通过fault CLI将.NET核心结果计为Unity结果。

### 9.2 已执行证据

以下按实际执行范围记录，各次验证独立，不用旧数量代表新验证：

2026-10-15 KCP分阶段故障矩阵：macOS ARM64 / SDK10.0.401 / .NET10.0.12 / Release，同进程真实UDP/DTLS，独立SchemaVersion=1。最终版本完整12项单轮全部通过，Healthy与ResourcesReleased均为true、UnexpectedSessionResults=0；每项保留正常对照并在故障后显式使用新授权、新端点和generation=2完成Echo，旧客户端与已发布旧服务端会话均在新连接前完成释放。

| 故障组 | 最终矩阵实际观察 |
| --- | --- |
| DTLS双向各丢首包 | 命中两个方向各1包丢弃，约3.21秒恢复并完成故障阶段 |
| DTLS取消 / 内部期限 | 确认丢包后取消并保留调用者token；不取消时约10,009ms失败 |
| GFNP Hello取消 / 内部期限 | 确认ClientHello后的单向断流命中，取消保留token；不取消时约5,075ms失败 |
| 双向断流3秒后恢复 | 原RPC恢复成功，服务端仅派发1次，故障阶段约3.60秒 |
| 持续完全断网 | 两端独立心跳故障，故障阶段约19,961ms，先于30秒RPC期限 |
| 客户端 / 服务端关闭时断流300ms | 两端均完成，观察到的IO/Completion约442～479ms，收到的关闭原因符合契约 |
| 客户端关闭时持续断网 | 客户端IO约2,002ms；服务端Completion约19,873ms |
| 服务端关闭时持续断网 | 服务端Completion约2,035ms；客户端IO约19,944ms |
| 旧UDP延后放行 | 旧客户端释放、撤销旧grant并等待旧服务端结束后建立新代，再向原端点释放19包旧UDP；新代Echo成功，旧资源仍归零 |

上述耗时含工具Pump/轮询和同进程调度，不作性能阈值或纯网络RTT。每项转发器FaultExercised与AccountingValid均为true、无溢出且最终队列归零；native实际/预约/Waitsnd、pending/deadline及模块自有内存释放，服务端native live bytes仍未直接测量。旧UDP仍投递原端点，本轮不包含同端点复用后的DTLS重放安全验收。

在最终完整矩阵之前，最终修正后的reconnect单项独立repeat=2均通过；CLI兼容验证中，WSS Echo 4连接/1秒80/80、KCP mixed 4连接/每连接10次每秒/预热1秒/测量3秒120/120，均保持Schema7并通过健康与释放检查，mixed实际profile命中；20种非法fault/负载参数组合均返回2。临时JSON、日志均清理。开发中曾因暂存包含ACK的全部旧服务端UDP，旧服务端留到host收尾而未满足严格的新连接前释放条件；修正测试为显式撤销旧授权并join旧会话，未修改生产Runtime或放宽正常对照关闭判据。首版repeat=2结果不作为最终版本重复矩阵证据。

首次最终合并回归180/181通过，唯一失败为新增relay统计用例将loss样本切为direct时仍继承ImpairedPackets=20；已将该样本修正为故障入口实际使用的旁路计数，保留未命中/溢出/未释放不可通过的断言。该问题不在传输实现或实际故障矩阵中。

修正后相同范围完整复跑181/181通过、0失败、0跳过，耗时3分34秒；覆盖NetworkLoadStatisticsTests/WssLoadTests、WssLifecycleTests、ServerClose/Heartbeat/ReceiveBudget/Logging、KcpLoopback/SourceBudget和ConnectionRaceTests，含新增7项短故障回归。两项Explicit入口不计入181项，完整故障矩阵另由CLI执行。最后为已验证非空的故障状态补充仅编译期的可空标注，Release测试工程及依赖编译0警告/0错误，未改变断言或运行行为；locked restore、Python AST、4份本轮相关C#的Tab/尾空白和git diff --check通过。

本轮只扩展测试转发器、故障用例、CLI和现有文档，未修改正式Runtime、Unity测试、协议、默认预算/期限或原生产物。现有macOS Release插件SHA256/build ID/维护源码指纹只读核验通过。未执行Unity公开NetworkClient弱网/重连、Editor或Player编译、IL2CPP、真机/其他平台、后台/换网/IPv6、独立进程容量及长时soak。

2026-10-15 补齐KCP慢处理器、突发、集中关闭及建连后弱网：macOS ARM64 / SDK10.0.401 / .NET10.0.12 / Release，SchemaVersion=7，真实loopback UDP/DTLS。原生产物与下一条Schema6记录相同，SHA256/build ID/维护源码只读核验通过，未重构建或替换插件，未改正式协议/native ABI/默认预算。

测试发现并修正两处客户端Runtime问题：成功销毁native后Waitsnd快照未清零，以及StopIo取消后迟到的发送/接收/CloseOutput入队可能抛普通传输停止错误、干扰有序关闭的取消语义。保留现有关闭仲裁、owner和资源所有权，仅在销毁成功分支清零当前值，并在三个入队入口先检查传入token；未取消的已停止传输仍报NetworkException。Waitsnd回归在旧实现稳定失败（残留2），修正后通过；取消定序测试旧实现3失败/3通过，修正后6/6通过，精确核验取消token并保留真实停止错误。最初64连接服务端关闭的第二轮业务/关闭均完成，但Waitsnd残留1令资源检查失败；空闲服务端关闭曾出现单连接非预期故障，不能以随后的偶然通过代替修复。最终重复验证见下文。

弱网CLI使用seed=42，Text128字节、inflight=8，非idle的Pump=1ms；各组均重复2轮。Echo和slow/close均预热1秒、测量3秒；burst每批4条、间隔500ms；idle无预热、测量6秒、Pump=20ms。下表为正式测量窗口，不含预热和关闭探测：

| 场景 / 连接数 / 每连接速率 | 每轮成功 / 计划 | RPC P99桶上界ms，两轮 | 实际注入证据（完整relay生命周期） |
| --- | --- | --- | --- |
| loss / 4 / 10次每秒 | 各120/120 | 79.872 / 88.064 | 各丢32包 |
| delay / 4 / 10次每秒 | 各120/120 | 135.168 / 147.456 | 固定双向额外40ms |
| jitter / 4 / 10次每秒 | 各120/120 | 167.936 / 176.128 | 20～60ms抖动；实际乱序36/40包 |
| reorder / 4 / 10次每秒 | 各120/120 | 217.088 / 212.992 | 实际乱序94/110包 |
| mixed / 4 / 10次每秒 | 各120/120 | 425.984 / 434.176 | 丢28/29包，实际乱序68/64包 |
| mixed / 16 / 5次每秒 | 各240/240 | 376.832 / 385.024 | 各丢70包，实际乱序80/89包 |
| mixed / 64 / 5次每秒 | 各960/960 | 409.600 / 417.792 | 各丢294包，实际乱序389/397包 |
| mixed slow-handler / 4 / 5次每秒 | 各60/60，正常45、慢15 | 401.408 / 516.096 | 各丢16包，慢组使用25ms异步等待 |
| mixed burst / 4 | 各96/96 | 417.792 / 393.216 | 各丢8包，实际乱序23/20包 |
| jitter idle / 4 | 窗口业务0，窗口外各4/4 | 不适用 | 双向心跳通过，抖动实际触发 |
| mixed close-storm client / 4 / 10次每秒 | 各120/120；关闭各32/32 | 540.672 / 425.984 | 主体关闭2066.574 / 2067.947ms |
| mixed close-storm server / 4 / 10次每秒 | 各120/120；关闭各32/32 | 425.984 / 417.792 | 主体关闭70.221 / 77.432ms |

以上正常弱网轮次均返回0、Healthy=true、资源释放，正式测量无工具跳过/失败/未决/服务端故障。每轮Relay.ProfileExercised=true、QueueOverflowDrops=0、包计账闭合、socket/owner停止且队列归零；64连接mixed队列峰值190/174包、34,057/32,053字节，未触上限。TimeoutRetransmits观察到非零，仍仅表示超时重传，不含fast retransmit。表内均为最终工具收尾顺序下的结果；同一组相同种子出现不同包数和延迟符合OS/ACK/重传调度口径。

首次jitter CLI因转发器在listener退出后继续发送延迟包而收到UDP Connection refused；已按依赖顺序调整为客户端/worker完成→join转发器→listener停止，未屏蔽socket异常。复验jitter及后续组合通过。关闭阶段旁路注入，剩余延迟包按原期限转发直到客户端完成；转发器停止时未发出的包单列DiscardedAtStop，不伪造已转发计数。

直连KCP扩展场景也已实跑：默认4连接20次/秒、预热2秒/测量10秒，slow-handler800/800（正常600、慢200）、burst320/320、client集中关闭持续800/800并结束32个在途请求，均通过。16连接慢处理器按20次/秒、64连接按5次/秒，均预热1秒/测量3秒/重复2轮，各轮960/960；burst每批4条、间隔1000ms，同样预热1秒/测量3秒/重复2轮，16连接各192/192、64连接各768/768。16连接两种关闭模式按5次/秒、预热1秒/测量3秒/重复2轮，各持续240/240并结束128/128个在途请求。相关短回归另外覆盖工具在途满额、发压不足、慢组超时和正常组隔离。

最终Runtime修正后，64连接直连关闭再验证如下，inflight=8、每连接5次/秒：

| 模式 / 关闭时请求数 | 预热 / 测量秒数 / 重复 | 每轮持续成功 / 计划 | 每轮预期关闭请求 | CloseStorm主体耗时ms |
| --- | --- | --- | --- | --- |
| client / 每连接8 | 1 / 3 / 2 | 各960/960 | 各512/512 | 2046.668 / 2037.903 |
| server / 每连接8 | 1 / 3 / 2 | 各960/960 | 各512/512 | 73.933 / 62.576 |
| client / 空闲0 | 0 / 1 / 2 | 各320/320 | 0，仍验两端各64连接完成 | 63.829 / 48.295 |
| server / 空闲0 | 0 / 1 / 10 | 各320/320 | 0，仍验两端各64连接完成 | 50.291～67.130 |

最终这16轮全部Healthy=true、UnexpectedClientFaults=0、服务端无故障、Waitsnd及native队列/实际/预约归零、ResourcesReleased=true。服务端空闲关闭10轮未复现旧的单连接非预期错误；该重复样本结合确定性取消入口测试构成本次证据，不能证明所有调度交错。客户端部分关闭耗时约2秒，符合当前尽力关闭/等待路径，未为获得毫秒数修改超时或协议。服务端native实时live bytes仍未直接测量，不将listener停止或托管额度归零当作原生泄漏/长时验收。

本轮新增CLI负向实跑（均无预热、测量1秒）：

| 负向场景 | 实际结果 | 退出码与收尾 |
| --- | --- | --- |
| KCP burst4连接，每批16条、inflight=4 | 计划64、尝试16且16成功，InflightLimitSkipped=48 | 返回1；无未决，资源释放 |
| KCP slow-handler2连接，各2次/秒、延迟1500ms、请求超时1秒 | 正常组2/2，慢组2次超时 | 返回1；正常组健康，无未决，资源释放 |
| KCP burst1连接，每批1024条、inflight=1024 | 尝试1024，成功104、背压792、超时128、服务端故障1，无工具跳过 | 返回1；所有终态完整计账、资源释放，数量受调度影响，不作容量阈值 |
| loss1连接、1次/秒、seed=1 | Echo1/1，但实际丢包0，Relay.ProfileExercised=false | 返回1；未命中配置不能当作通过，资源释放 |

WSS非direct profile/显式seed、KCP direct显式seed、未知profile、seed为0/负数/超Int32以及KCP65连接共8种非法组合均返回2。CLI从仓库外调用，带空格JSON路径、Schema7、Native核验记录、退出码与各轮计账/资源断言通过；临时报告未保留到仓库。

最终WSS兼容CLI复验：4连接、无预热，Echo1秒80/80、slow-handler按5次/秒1秒20/20、burst每250ms一批4条1秒64/64；idle6秒双向心跳与窗口外4/4探测通过；client/server集中关闭各持续80/80、关闭32/32，主体耗时12.063/10.558ms。六种命令均返回0、Schema7解析通过、Relay/Native/Kcp/KcpCompletion为null，资源归零。

最终客户端源码在Unity6000.3.19f1/macOS中Editor编译完成且无Console Error；NetworkLifecycleTests PlayMode11/11、独立临时local-kcp-dtls服务下真实UDP/DTLS PlayMode1/1，均零跳过，包含Close后Waitsnd归零断言。临时服务、grant、环境变量及工具设置已清理/恢复，详见[客户端证据](ClientNetworkUsage.md#验证与现有证据)。本轮未执行Player脚本编译、IL2CPP、真机/其他平台、弱网握手/关闭故障、完全断网、独立进程容量或长时soak；不将本机短时测试解释为生产容量或所有平台通过。

最终相关合并回归174/174通过、零跳过，覆盖NetworkLoadStatisticsTests/WssLoadTests、WssLifecycleTests、ServerClose/Heartbeat/ReceiveBudget/Logging、KcpLoopback/SourceBudget以及ConnectionRaceTests；包含五种弱网、空闲/在途两端关闭、正常/慢组隔离、取消/响应/关闭竞争和7项Runtime修复回归。Explicit负载矩阵由CLI单独执行，不混入174项计数。locked restore通过，测试工程及依赖Release编译0警告/错误；Python AST、差异及11份相关C#文件的Tab/尾空白检查通过。

2026-10-15 KCP多连接持续/空闲CLI实跑：macOS ARM64 / SDK10.0.401 / Release，SchemaVersion=6，真实loopback UDP/DTLS，同进程客户端、服务端与测试采集器。使用已有macOS universal非sanitizer ABI2原生产物，二进制SHA256=`0cffd5874206055c777a52e1b5ab5875074e7bc17ed108a8e3d26b038249befb`，build ID=`833aea2b337679b5a97e60691d3e7f9566710310b6739b34055b62436e1c876a`；双清单、二进制与当前维护源码指纹核验一致，未重构建/安装或替换插件。持续窗口Text为128字节、inflight=8、Pump=1ms：

| Echo连接数 / 每连接速率 | 预热 / 测量秒数 / 重复 | 每轮成功 / 计划 | RPC P99桶上界ms | owner工作P95桶上界ms | owner每轮分配P95桶上界By |
| --- | --- | --- | --- | --- | --- |
| 默认4 / 20次每秒 | 2 / 10 / 1 | 800/800 | 52.224 | 0.296 | 3,712 |
| 1 / 20次每秒 | 1 / 3 / 2 | 各60/60 | 各52.224 | 各0.152 | 1,440 / 1,408 |
| 16 / 20次每秒 | 1 / 3 / 2 | 各960/960 | 49.152 / 52.224 | 0.832 / 0.816 | 各12,288 |
| 64 / 5次每秒 | 1 / 3 / 2 | 各960/960 | 71.680 / 60.416 | 1.920 / 2.048 | 36,864 / 44,032 |

64连接采用每连接5次/秒，以保留回环共享来源IP的默认UDP入口限额；未放宽生产预算，不能将不同速率的数据直接解释为连接规模的性能差异。Echo owner循环间隔P95桶上界约11.264～13.056ms，RPC耗时包含KCP调度、客户端Pump与工具观察等待。客户端native实际内存在持续窗口约每连接7,940字节并有有限动态波动，64连接两轮采样峰值542,144/541,976字节；每连接4MiB的native预约另列，不能冒充实际堆分配。

空闲场景Pump=20ms、预热2秒；默认4连接测量30秒，其余每组测量12秒并重复2轮。CPU按一个逻辑核100%计算，分配为整个同进程窗口的KiB/s，非服务端独占成本：

| idle连接数 | CPU核占用% | 分配KiB/s | 客户端native实际起止By | owner工作P95桶上界ms | 窗口外Echo（每轮） |
| --- | --- | --- | --- | --- | --- |
| 默认4 | 4.696 | 314.417 | 31,760→31,760 | 0.078 | 4/4 |
| 1，两轮 | 4.097 / 3.111 | 175.130 / 182.003 | 各7,940→7,940 | 0.096 / 0.074 | 各1/1 |
| 16，两轮 | 8.859 / 7.039 | 883.263 / 911.214 | 各127,040→127,040 | 0.132 / 0.136 | 各16/16 |
| 64，两轮 | 21.304 / 19.874 | 3,504.489 / 3,515.700 | 508,160→508,160；508,422→508,160 | 0.184 / 0.200 | 各64/64 |

所有14轮正常KCP测量均Healthy=true、返回0，无跳过/失败/未决/服务端错误，客户端TimeoutRetransmits=0（不代表fast retransmit计数）。空闲双向控制活动覆盖全部连接，业务计数、业务队列/pending/deadline为0；owner循环间隔P95桶上界均11.264ms，64连接native实际采样峰值508,644/508,684字节。结束时worker/listener均实际停止，客户端native实际/预约/队列和可追踪托管资源归零。服务端native实时内存明确未直接测量，Completion会话存活代理不等同于native槽快照；短时内存样本不代替泄漏或长时验收。

CLI负向：2连接、40次/秒、Pump=100ms、无预热、测量1秒，计划80、尝试20且全部成功、工具调度跳过60、未决0，报告保留并按预期返回1，资源释放。13种非法组合/范围、缺库/缺清单、artifact hash不符、build ID漂移、二进制变化或维护源码指纹不符均返回2；所有篡改使用临时副本，安装产物不变。使用GF_KCP_NATIVE_LIBRARY指向带空格目录，1连接×5次/秒×1秒成功5/5、Native核验记录完整、资源释放。所有CLI均从仓库外调用，带空格JSON路径及SchemaVersion6解析通过，临时文件已清理。

原WSS入口短回归：4连接、无预热，Echo测量1秒80/80、slow-handler按5次/秒20/20、burst按每250ms一批4条64/64；idle测量6秒、双向控制活动及窗口外4/4 Echo通过；client/server集中关闭各持续80/80、CloseStorm.Healthy=true。均返回0、资源归零，报告Transport=wss且Native/Kcp/KcpCompletion为null。本轮只扩展测试工具及网络说明，未改正式运行时/协议/默认预算；未执行Unity/Player/IL2CPP、真机、其他平台、弱网/中断、独立进程容量或长时测试。

最终相关合并回归116/116通过、零跳过，覆盖NetworkLoadStatisticsTests/WssLoadTests、WssLifecycleTests、ServerClose/Heartbeat/ReceiveBudget/Logging及KcpLoopback/SourceBudgetTests；包含新增KCP真实Echo、空闲心跳/探测、发压不足及统计/参数共9项。两类fixture共用同一原生加载器，在同一测试进程中复验，避免重复注册DllImportResolver。Explicit负载矩阵由CLI独立执行，不计入116项普通回归。locked restore通过，测试工程及依赖Release编译0警告/错误，Python语法与差异/新源码空白检查通过。

2026-10-15 集中关闭CLI实跑：macOS ARM64 / SDK10.0.401 / Release，SchemaVersion=5。默认close-storm（4连接、20次/秒、预热2秒/测量10秒、每连接8个关闭探测请求）持续窗口800/800成功，触发前32个请求仍在途，随后32/32按预期关闭，两端各4条连接实际完成。以下扩展以20次/秒、无预热、测量1秒、每连接8个在途请求执行；两种关闭模式、每种连接数均重复2轮：

| 连接数 / 模式 | 每轮持续窗口成功数 | 每轮预期关闭请求 | 两端完成数（各端） | CloseStorm.TotalSeconds换算毫秒，两轮范围 |
| --- | --- | --- | --- | --- |
| 16 / client | 320/320 | 128/128 | 16 | 4.899～11.804 |
| 16 / server | 320/320 | 128/128 | 16 | 4.101～13.042 |
| 64 / client | 1280/1280 | 512/512 | 64 | 6.347～12.064 |
| 64 / server | 1280/1280 | 512/512 | 64 | 14.477～15.212 |

所有上述轮次Prepared=true、无跳过/非预期终态/服务端故障，Unresolved=0、整轮Healthy=true并返回0，关闭后连接、pending、deadline、队列与两端模块自有内存归零。服务端模式仅每连接首条handler进入，16/64条均由连接取消退出，其余排队业务未派发；客户端模式本次各有128/512条handler完成排空，不能据此承诺尚未送达服务端的请求也会派发。另以16连接、close-requests=0对两种模式各跑一轮，持续窗口均320/320、两端各16条连接完成，无探测请求也必须通过连接/关闭原因与资源检查。

负向以2连接、200次/秒、无预热、测量1秒触达默认准入限制，本次计划400、成功373、失败24、工具跳过3、服务端故障2；连接已不满足关闭准备条件，CloseStorm.Prepared=false且未发探测请求，整轮保留JSON并返回1，最终资源归零。此为失败保护验证，不是正常容量基线，终态数量受调度影响。11种非法close参数/模式组合均返回2；均从仓库外调用、带空格JSON路径解析通过，临时报告/日志已清理。关闭耗时包含同进程客户端/服务端、工具依序调用和Pump/轮询调度，不承诺通用阈值；未运行KCP、Unity/Player/IL2CPP、其他平台、弱网/中断、独立进程容量或长时验证，未修改正式协议、运行时代码或默认限额。

最终相关回归74/74通过、零跳过，覆盖统计/负载及既有WSS生命周期、关闭、心跳、接收预算和日志组；其中新增集中关闭相关13项。Explicit负载由上述CLI单独执行。测试工程及依赖Release编译0警告/错误，CLI的locked restore通过。原echo与slow-handler各以4连接、无预热、1秒复跑，均80/80；burst同条件32/32；idle以4连接、无预热、6秒复跑，心跳和窗口外4/4 Echo通过，均Healthy=true、资源归零且SchemaVersion5解析成功。Python语法及差异/新源码空白检查通过。

2026-10-15 突发负载CLI实跑：macOS ARM64 / SDK10.0.401 / Release，默认128字节Text，SchemaVersion=4，均从仓库外调用并核验带空格JSON路径。下表为测量窗口计数；预热另计。

| 场景与参数 | 实际测量结果 | 判定 |
| --- | --- | --- |
| 默认burst：4连接、每批8条、间隔1000ms、预热2秒、测量10秒 | 320/320成功，无跳过/失败 | 返回0，Healthy=true |
| 16连接、每批8条、间隔250ms、预热1秒、测量3秒、重复2轮 | 每轮1536/1536成功，无跳过/失败 | 返回0，两轮Healthy=true |
| 非整除窗口：4连接、每批3条、间隔333ms、无预热、测量1秒 | 每连接4批，总48/48成功 | 返回0，核验半开窗口批次数 |
| 工具在途满额：4连接、每批16条、间隔1000ms、inflight=4、无预热、测量1秒 | 计划64，成功16，InflightLimitSkipped=48，Backpressure=0 | 返回1，未满足计划负载 |
| 工具调度落后：2连接、每批2条、间隔10ms、Pump=100ms、无预热、测量1秒 | 计划400，成功40，GeneratorLateSkipped=360，无网络失败 | 返回1，未补发旧批次 |
| 单慢连接：2连接、每批4条、间隔1000ms、延迟400ms、RPC超时1秒、无预热、测量1秒 | 正常组4/4成功；慢组2成功、2超时 | 返回1，正常组健康 |
| 默认预算超额：1连接、每批1024条、间隔1000ms、inflight=1024、无预热、测量1秒 | 尝试1024，Backpressure=742、Failed=282、ServerErrors=1，无工具跳过 | 返回1，覆盖背压及故障关闭报告；数量受并发调度影响，不作为固定阈值 |

上述所有轮次均完整计账、Unresolved=0，关闭后连接、pending、deadline、队列与两端模块自有内存归零，ResourcesReleased=true。超额场景保留故障计数，不将“资源释放”当作负载通过，也不凭聚合数据断言唯一限额原因。11种非法CLI组合/范围均返回2；原Echo与slow-handler各以4连接、20次/秒、无预热、1秒复跑，均80/80；idle以4连接、无预热、6秒复跑，心跳检查和窗口外4/4 Echo通过。临时报告与日志均清理。

最终相关回归61/61通过、零跳过，覆盖NetworkLoadStatisticsTests、WssLoadTests及既有WSS生命周期/关闭/心跳/接收预算/日志组；Explicit负载用例由上述CLI单独执行。测试工程及依赖Release编译0警告/错误，CLI的locked restore通过，Python语法与差异空白检查通过。此为本机同进程WSS短时验证，未运行Unity/Player/IL2CPP、真机、KCP负载、其他平台、独立进程容量或长时测试；未修改正式协议、框架运行时代码或默认预算。

2026-10-15 空闲连接对照：macOS ARM64 / SDK10.0.401，预热2秒、测量12秒、Pump间隔20ms，每种连接数重复2轮。下表保留两轮范围；CPU按一个逻辑核100%计算，分配使用KiB/s。指标包含同进程客户端、服务端及工具采集，未扣除采样/测试宿主开销，不能作为服务端独占成本或生产容量。

| 连接数 | CPU核占用 | 分配 KiB/s | 各端OwnedBytes起止（字节） | 空闲后Echo（每轮） |
| --- | --- | --- | --- | --- |
| 1 | 4.52%～5.49% | 106.3～106.9 | 544→544 | 1/1 |
| 16 | 8.05%～11.52% | 398.9～417.6 | 8,704→8,704 | 16/16 |
| 64 | 13.73%～17.57% | 1,279.8～1,287.9 | 34,816→34,816 | 64/64 |

各端表示客户端汇总与服务端汇总分别取得该数值，均不含进程工作集。所有轮次连接数保持完整、双向控制活动覆盖全部连接，业务帧/正文、业务队列/pending/deadline均为0，关闭后可追踪资源归零。16/64连接客户端自有内存采样峰值分别9,808/35,920字节，期间可见短暂控制帧预约；服务端自有内存采样值与起止一致。64连接两轮GC分别为[2,1,0]和[2,0,0]，仍有真实分配成本；模块自有内存不增长不能解释或排除进程工作集增长，当前仅作有限窗口基线。

2026-10-14 底层核对覆盖 Runtime 网络与 Core/Server、GameRuntime/Launch/GameEntry 和登录接入、协议定义/生成工具/正式生成代码、Editor 网络工具、关联 .NET/Unity 测试、KCP 桥接与仓库内第三方 KCP 源码、构建安装脚本，以及依赖锁/程序集/原生 importer/五组安装清单和网络 CI 配置。主要实施缺口是客户端 RPC 阶段及子预算可见性，已直接补入既有快照；保留当前状态仲裁和关闭所有权，暂不调整传输参数、池化或调度模型。完整 CLR 元数据成本和发布平台门禁仍未完成。外部 mbedTLS/PSA、MemoryPack、平台 TLS/SDK 的实现内部未逐文件审计，只核对版本、接入配置、边界与运行证据，不将本轮核对称为所有第三方实现的完整安全审计。

本轮 ET 借鉴实施前的基线复核完整阅读了仓库清单内 254 项：五份网络文档、关联启动/五子棋/作用域/错误契约，Runtime 网络与 Core/Server/Logging、Game.Server、Hotfix 登录/房间/导航/UI 调用方，协议源/生成器/正式产物/向量，关联 .NET/Unity 测试、Editor 网络与程序集工具、KCP 桥接和仓库内 KCP 第三方源码/测试/构建安装脚本，以及配置、锁、程序集、入口/场景、五平台 importer/安装清单和网络 CI。另核对第 22 节列出的 11 个 ET 参考文件，并非 ET 全仓审计。外部 mbedTLS/PSA、MemoryPack、NLog、Unity/BCL/平台 TLS 实现内部未逐文件审计；仅核对其锁定版本、接入与运行边界。

主要发现是文档混写当前实现与未来世界候选、WSS/KCP 故障输出遗漏或重复、请求关联不足，以及原异常格式化可能暴露敏感信息。方案比较后选择在既有故障所有者处修正诊断，保留首因与关闭/资源流程；没有依据重建调度或新增通用管理器。现已实施四类故障事件及安全异常视图；其余业务/生命周期事件和洪峰成本仍待完成。

| 日期与范围 | 证据 | 限制 |
| --- | --- | --- |
| 2026-10-15 WSS空闲连接扩展，macOS ARM64 / SDK10.0.401 | Release编译0警告/错误、locked restore通过；相关统计/负载、WSS生命周期/关闭/心跳/接收预算/日志回归45/45、零跳过。默认idle（4连接、预热2秒/测量30秒）双向各1,920应用字节、业务计数和队列均为0，各端OwnedBytes为2,176→2,176字节，空闲后Echo4/4成功；1/16/64连接各12秒重复2轮的对照见上表。所有运行关闭后连接、pending、deadline、队列及模块自有内存归零，服务端错误为0。Echo与慢场景各160/160复验；慢组超时负向仍保留报告并返回1。5种非法空闲/速率参数均返回2，仓库外执行、带空格JSON路径及SchemaVersion3解析通过 | 仅测试工具及文档变更，未调整正式协议/运行时/心跳周期。CPU、分配和RSS包含工具及客户端成本；未执行Unity/Player/IL2CPP、真机、KCP、PG、其他平台、独立进程容量或长时/泄漏验收 |
| 2026-10-15 WSS单慢处理器扩展，macOS ARM64 / SDK10.0.401 | Release编译0警告/错误、locked restore通过；统计/负载与WSS生命周期、关闭、心跳、接收预算、日志相关回归37/37、零跳过。默认慢场景（4连接、20次/秒、25ms延迟、预热2秒/测量10秒）正常组600/600、慢组200/200，P99桶上界分别1.568ms/27.648ms；16连接、预热1秒/测量3秒重复2轮，每轮正常组900/900、慢组60/60。原Echo同参数4连接、预热1秒/测量3秒重复2轮均240/240。负向100ms延迟/20次每秒/在途2时慢组21成功、19跳过，正常组120/120；1500ms延迟/1秒超时时慢组2次超时、正常组6/6，均按预期返回1且JSON保留。所有运行未决数为0，关闭后连接、pending、deadline、队列与模块自有内存归零，服务端错误为0；5种CLI非法场景/延迟参数均返回2，仓库外执行和带空格JSON路径解析通过 | 仅测试工具及文档变更，复用既有协议/限额。异步等待隔离不代表CPU阻塞隔离；16连接正常组P99两轮为31.232ms/10.240ms，存在同进程调度波动，不据此承诺性能阈值。未执行Unity/Player/IL2CPP、KCP、PG、其他平台、独立进程容量或长时运行 |
| 2026-10-15 WSS可重复负载入口，macOS ARM64 / SDK10.0.401 | 相关回归29/29、零跳过，包含新增统计6项及真实四客户端冒烟1项；测试工程locked restore通过。4客户端×20次/秒×3秒重复2轮，各240/240；16客户端×20次/秒×5秒重复2轮，各1600/1600；默认参数从仓库外运行800/800。均无跳过/失败，关闭后连接、pending、deadline、队列及模块自有内存归零。JSON带空格路径导出/解析通过，CLI非法预算及超协议Text参数返回2；故意放慢发压循环的1000个计划时槽中39次成功、961次跳过，按预期返回1且资源释放；另以200次/秒触发默认入站限制，失败请求仍全部终结、报告保留且资源归零 | 测量为同进程loopback、日志关闭，含客户端和测试采集成本；负向运行仅核验发压不足/默认限额下的失败报告，不是服务端容量上限。未修改运行时/正式协议/默认容量；未执行KCP负载、PG、多房间、Unity/Player/IL2CPP、其他平台实跑、独立进程容量或soak |
| 2026-10-14 ET 借鉴基线与网络故障诊断，macOS ARM64 / SDK10.0.401 / PG18 | 八工程 Release 编译 0 警告/错误，解决方案及四测试工程 locked restore 通过；Server187/187、Framework13/13、Protocol167/167、Logging13/13，共380/380、零跳过；最后合并重复 catch 并移除未使用的清理参数后，WSS 生命周期/日志/关闭/心跳/接收预算相关组22/22复验通过；协议 check --artifacts 五文件一致。真实 WSS/KCP handler 故障仅一个主事件，关联连接/代际/Opcode/CorrelationId；独立 Closed 故障含首次清理失败有记录，Completion 保留原异常；NLog Text/JSON 不调用自定义异常格式化、不输出 Message/Inner/Data 中的敏感标记。五组原生安装产物的 binary/ABI/build ID/源码指纹核验通过 | Server 使用独立临时 PG 与现有 macOS Release native，临时数据库与进程已清理；初次新增日志用例受 fixture 历史捕获污染，清空后通过。未改客户端、协议、native ABI 或 Logging 实现；未执行 Unity/Player/IL2CPP/真机、其他平台、远端 CI、native 重构建/sanitizer、故障洪峰性能、生产容量或24h soak |
| 2026-10-14 KCP 单连接低负载复测，同上环境 | 16 次预热、128 次串行小 Echo；560 个 owner 样本：工作 P95=0.127ms、循环间隔 P95=12.143ms、owner 分配 P95=1,256By/轮；发送排队 P95=12ms、派发排队 P95=1ms、含客户端轮询/Pump 的 RPC RTT P95=61.423ms | 开启 MeterListener、NullLogger、正常 loopback 样本，包含采集开销；不是纯网络 RTT、故障日志成本或容量门禁，尚缺多连接/空闲/洪峰及GC、复制清零、锁成本对照，不据此决定池化或期限堆 |
| 2026-10-14 日志工程合并，macOS ARM64 / SDK10.0.401 | 日志选项与 NLog 实现合并为单一 GameFrame.Logging 工程，类型统一命名空间；八工程 Release 编译 0 警告/错误，解决方案及两个相关测试工程 locked restore 通过；Logging13/13、WssLifecycle/ServerLogging6/6，均零跳过；临时 publish 仅含 GameFrame.Logging.dll，依赖清单无旧适配程序集；发布目录进程验证 JSON 启动失败、None 过滤和无效日志配置退出行为，临时产物已清理 | 本次调整工程边界与引用，沿用原输出行为和 Meter 标识；未重跑完整 Server、PG/UDP/DTLS、Unity/Player/IL2CPP、其他平台或性能容量测试 |
| 2026-10-14 NLog 框架接入，macOS ARM64 / SDK10.0.401 | 九工程 Release 编译 0 警告/错误；解决方案及 Logging/NetworkServer.Tests locked restore 通过；Logging13/13，覆盖字段/模板/异常/UTC、并行 Scope、过滤、独立所有权、满队列、写入失败与刷新失败/超时；Server 全组 141 通过、41 跳过（共 182），新增真实 WSS host 释放后写日志用例另跑 1/1；八类早期失败进程均退出 1 且测试敏感标记未泄漏；真实 KCP 进程输出 Ready/Stopped，SIGINT 退出 0，凭据未输出 | 未配置 GF_POSTGRES_TEST_CONNECTION，PG 及依赖 PG 的 WssServerTests 跳过，不能计为数据库/完整 WSS 业务验收；KCP 使用已核验 hash 的现有 macOS native，未重构建/安装。临时 grant 和进程已清理；未执行 Unity/Player/IL2CPP、远端 CI、其他平台、全量敏感信息审查、文件部署、生产容量或 soak |
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


## 独立 IL2CPP Player 互通验证

2026-10-15，Unity6000.3.19f1/macOS ARM64 的独立 Development IL2CPP Player 实跑七组全部通过，Player 与伴随进程均退出0，资源和转发计账通过；构建/架构/裁剪/元数据及 ABI 故障根因见[客户端验证记录](ClientNetworkUsage.md#验证与现有证据)。共享 Peer/Grant 改用 fixed 缓冲区后，最终 .NET KcpLoopbackTests 28/28、零跳过，Release 编译/locked restore 通过。仍使用此前已核验的 macOS universal Release ABI2 native，未重新构建或安装插件。此结果不代表正式 Game.Server/PG、其他平台、Release Player 或容量验证。

macOS 入口与构建步骤见[客户端 Player 验证](ClientNetworkUsage.md#network-player)。`Tools/network_player.py` 复用现有 Explicit 伴随测试 host 和正式 Echo 路由，自动在独立进程启动临时 KCP listener/relay、注入一次性 grant，并等待 Player 完成及两端释放后退出；不用数据库，不启动或修改正式 Game.Server。普通 .NET 回归不启动此 host；.NET 编译通过只表示伴随端可用，实际 IL2CPP 结果由独立 Player 和退出码共同确认。

2026-10-15 另行完成正式 Launch/Host 旧包新增 DTO/路由实跑，详见[包后热更验收](ClientNetworkUsage.md#network-hotupdate-dto)。伴随服务只在系统临时目录编译，由同源生成器产出的新 catalog 注册一个 RPC handler，其余入站路由显式 Reject，可靠 Push 使用现有 ServerConnectionContext；复用 WssServer/DI HostedService 和正常 localhost TLS 校验，不需要账号或数据库。临时16条路由中的两条新增路由完成三次 RPC/三次 Push，服务端确认 Close reason1、无连接错误、owned bytes=0并退出0，Release编译0警告/0错误。验证后临时协议/组件移除，正式14条路由、Game.Server及框架实现均未修改；此项不代替正式账号业务、KCP或平台/容量验收。

## 五子棋联调（2026-10-14）

按上述 local-wss 步骤启动同一 Game.Server，两位玩家用不同账号登录。A 创建房间并告知 B 8 位号码，B 输入号码加入；双方准备后房主开始，黑方先行，服务端裁决落子。local-kcp-dtls 不承载房间业务。

GomokuOptions 定义开发限额：64 房间、128 个业务绑定、等待 10 分钟 / 对局 60 分钟 / 终局 60 秒。实际在线连接仍受 WssServerOptions 默认 32 连接、每 IP 4 连接限制。房间不持久化，服务重启失效。

相关测试：`(cd Tools && dotnet test NetworkServer.Tests --filter 'GomokuRoomTests|WssServerTests|AccountLifecycleTests')`；PostgreSQL 路径需 GF_POSTGRES_TEST_CONNECTION，测试只创建 / 删除独立 schema。2026-10-14 使用独立临时 PostgreSQL 最终复验 83/83，包括双 WSS 完整对局、已提交请求取消后查询、已撤销房主绑定的加入拦截。协议测试 200/200；验证过程与平台、容量待验收项见 [五子棋](Gomoku.md#10-已实施与验证边界)。

协议精简后的服务端复验：协议生成检查一致、协议工具 167/167；非 KCP 组 115 项通过（数据库环境条件跳过另行执行），临时 PostgreSQL 的数据库 / WSS 组 69/69，新增路由完整性用例后的账号生命周期组 18/18，真实原生 KCP / DTLS 回环 21/21。当前生产协议共 14 条可靠路由，世界消息未生成；双通道回环使用测试专用 catalog。Unity 使用生产 catalog 与 local-kcp-dtls 的 Hello / Echo / 释放另行实跑通过，详见 [五子棋验证](Gomoku.md#10-已实施与验证边界)。
