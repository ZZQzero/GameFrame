# PostgreSQL 账号与登录

服务端账号业务位于 `DotNet/Game.Server/Login`，通过 Npgsql 10.0.3 访问 PostgreSQL。`GameFrame.Server` 和 `GameFrame.Network.Core` 不依赖数据库、玩家身份或游戏协议。数据库只保存账号名、带盐密码哈希、认证代际和创建时间；活跃连接、恢复凭据摘要和账号会话由当前应用进程拥有。

## 数据库模块与业务仓库

`DotNet/Game.Server/Database/PostgresDatabase.cs` 统一持有 Npgsql 连接池，负责连接选项、连通性检查和异步释放。它不依赖账号、游戏协议或网络，当前作为应用的数据库基础设施保留在 Game.Server。应用启动显式创建一个实例，让账号及后续业务仓库共享 `DataSource`；每个请求不创建新连接池。

```csharp
await using var database = new PostgresDatabase(connectionString);
await database.ValidateAsync(cancellation);
var accounts = new PostgresAccountRepository(database.DataSource);
await accounts.ValidateAsync(cancellation);
```

`PostgresDatabase.ValidateAsync` 仅执行 SELECT 1，确认连接与查询可用；账号仓库的 ValidateAsync 另外检查账号表。两项检查都在网络监听前完成。启动失败停止初始化，已创建的池按 await using 释放；停止时先 join 网络/业务，再释放账号服务和连接池，最后释放证书。释放连接池不会停止 PostgreSQL 数据库进程。

CRUD 使用业务仓库的明确方法。当前账号仓库提供 CreateAsync、FindAsync、AdvanceEpochAsync；账号删除和改密属于后续业务，未增加没有调用方的接口。新增角色等业务时，创建自己的接口和 PostgreSQL 仓库，注入同一个 DataSource。业务服务只依赖仓库接口；参数化 SQL、查询结果映射和事务放在仓库内。

DataSource 由应用拥有，仓库只借用，不能释放共享池。单条 SQL 使用 DataSource.CreateCommand，并 await using 释放 command/reader。多条 SQL 的原子操作通过 DataSource.OpenConnectionAsync 获取本次操作自己的连接，再 BeginTransactionAsync；相关 command 绑定同一 connection/transaction，成功后显式 CommitAsync。connection、transaction 都由该操作释放，不跨并发请求共享。取消信号传递到每个数据库异步操作，数据库异常保持传播。

直接复用 Npgsql 的查询、执行和事务 API，不另造通用 CRUD 转发层。当前池最大16连接、连接/命令超时5秒、禁用详细错误、默认 search_path 为 public；测试使用独立随机 schema。基础设施不自动执行迁移、创建账号、启动数据库进程或重试操作。

## 准备数据库

开发与生产使用 PostgreSQL；当前验证使用 PostgreSQL 18。可使用已有 PostgreSQL，或在安装 Docker 的开发机从仓库根目录执行：

```sh
export GF_POSTGRES_PASSWORD='自行设置的数据库密码'
docker compose -f DotNet/compose.yaml up -d --wait
docker compose -f DotNet/compose.yaml exec -T postgres psql -U gameframe -d gameframe -v ON_ERROR_STOP=1 < DotNet/Game.Server/Database/001_accounts.sql
```

compose 只将数据库端口发布到 127.0.0.1；数据在命名卷中持久化。已有数据库使用 `psql -v ON_ERROR_STOP=1 -f DotNet/Game.Server/Database/001_accounts.sql`，由部署者配置数据库连接。脚本在一个事务内创建初始账号表，执行一次；重复执行明确失败，不自动删除或重建已有表。服务启动检查数据库与表可查询，不自动创建数据库、执行迁移、注册玩家或回退到内存仓库。

本机 macOS 已安装 Homebrew PostgreSQL 18，可在终端显式启动开发数据库，不需要 Docker 或开机服务：

```sh
export PATH="/opt/homebrew/opt/postgresql@18/bin:$PATH"
pg_ctl -D /opt/homebrew/var/postgresql@18 -l /opt/homebrew/var/postgresql@18/server.log start
createuser -h 127.0.0.1 --pwprompt gameframe
createdb -h 127.0.0.1 --owner=gameframe gameframe
psql -h 127.0.0.1 -U gameframe -d gameframe -v ON_ERROR_STOP=1 -f DotNet/Game.Server/Database/001_accounts.sql
```

创建角色、数据库和表的三条命令仅用于首次准备；再次联调只启动既有数据库。`--pwprompt` 在终端输入数据库角色密码，不将密码写进命令历史。停止可执行 `pg_ctl -D /opt/homebrew/var/postgresql@18 stop`，保留数据库文件供下次使用。测试使用的是另一个临时 cluster，不会创建或覆盖这个开发库中的玩家数据。

仓库还提供跨平台维护入口 `Tools/postgres.py`，需要 Python 3 和 PostgreSQL 的 pg_ctl/createdb/psql。通过 PATH 或 `--pg-bin` 指定工具目录。macOS 本机可从仓库根目录执行：

```sh
python3 Tools/postgres.py --pg-bin /opt/homebrew/opt/postgresql@18/bin start --data-dir /opt/homebrew/var/postgresql@18
python3 Tools/postgres.py --pg-bin /opt/homebrew/opt/postgresql@18/bin status --data-dir /opt/homebrew/var/postgresql@18
# 首次准备：先用上文 createuser 创建 gameframe 角色，再创建库、初始化表。
python3 Tools/postgres.py --pg-bin /opt/homebrew/opt/postgresql@18/bin create-db --user zzq --database gameframe --owner gameframe
python3 Tools/postgres.py --pg-bin /opt/homebrew/opt/postgresql@18/bin init-schema --user gameframe --database gameframe
python3 Tools/postgres.py --pg-bin /opt/homebrew/opt/postgresql@18/bin stop --data-dir /opt/homebrew/var/postgresql@18
```

create-db 的 `--user` 是有建库权限的现有角色，示例 zzq 是本机 Homebrew cluster 的初始化角色，其他机器需改成自己的管理员角色。init-schema 的角色需有建表权限。密码通过 .pgpass、PGPASSFILE 或 PGPASSWORD 提供；工具不接受密码命令行参数，也不将凭据写入仓库。init-schema 只对明确指定的数据库执行同一初始 SQL，已有表时明确失败，不自动重建或删除数据。create-db 同样不会覆盖已有库。start/stop 最多等待30秒，stop 使用 PostgreSQL 的 fast 模式结束当前数据库连接；status 直接返回 pg_ctl 状态码，未运行时返回3。工具只执行显式命令，不由游戏服务器调用，也不配置开机启动；正式部署仍由 Docker 或系统服务管理进程。

本地示例 compose 使用一个数据库所有者角色方便开发。正式部署应由独立 schema-owner 执行脚本，应用角色只授予 public schema USAGE、accounts 表 SELECT/INSERT/UPDATE 和 accounts_id_seq 的 USAGE。远程数据库连接使用可信证书与 `SSL Mode=VerifyFull`；不得将本地配置当作生产部署配置。

## 启动与客户端流程

在 Rider 的 Game.Server 运行配置中设置程序参数 `--profile local-wss`，以及这些环境变量：

| 变量 | 含义 |
| --- | --- |
| `GF_DATABASE_CONNECTION` | 例如 `Host=127.0.0.1;Port=5432;Database=gameframe;Username=gameframe;Password=自行设置的数据库密码` |
| `GF_LOCAL_CERTIFICATE` | 包含私钥且在有效期内的 localhost HTTPS PFX 绝对路径 |
| `GF_LOCAL_CERTIFICATE_PASSWORD` | 可选的 PFX 密码 |
| `GF_LOCAL_PORT` | 可选的 WSS 端口，默认 7443 |

数据库角色密码用于服务器连接数据库；玩家账号密码不参与启动，也不再读取 `GF_LOCAL_ACCOUNT/PASSWORD`。证书仍需客户端正常信任，服务不会安装证书或跳过校验。命令行也可执行 `dotnet run --project DotNet/Game.Server --launch-profile local-wss`。启动错误停止后续初始化；账号服务、网络和连接池按资源所有权释放。

执行流程：

```text
配置与证书校验 → PostgreSQL/账号表检查 → 创建应用与网络 → 监听 WSS
客户端 TLS + GFNP Hello → Register → Login → 已认证业务
                                  └→ 注册成功仍未登录
断线后显式 ResumeLogin → 原子轮换凭据并替换大厅绑定
Logout → 撤销该账号会话与恢复凭据 → 响应 → 关闭连接
```

`Register` 是新增的可靠 WSS RPC，opcode 1004，返回 OperationResponse；成功返回 Success，重复账号返回 AccountAlreadyExists，无效输入返回 InvalidInput。它不签发登录凭据、不提高连接权限、不延长原来的10秒登录期限。客户端按顺序等待注册响应再发 Login；同一未登录连接最多一个认证请求在执行。注册提交后响应丢失属于结果不确定，客户端显式查询/登录处理，不自动重放注册。

账号名区分大小写，使用数据库 COLLATE C 唯一约束，不隐式转换大小写或做 Unicode 归一化；拒绝空白、首尾空白和控制字符，最多64个UTF-16字符。密码长度8～128，既不裁剪也不归一化。PBKDF2-SHA256 固定600000次，随机16字节盐、32字节哈希；原文不存数据库或日志，校验使用固定时间比较。当前没有密码修改/找回、封禁或第三方登录业务，后续增加时应扩展账号服务及必要协议。

## 会话、并发与失败

不同账号的会话独立。同一账号新 Login 在密码校验成功后，先在现有 AccountSessions 登记在途操作，再异步调用数据库原子分配递增 authEpoch，最后在同一账号状态内校验操作资格并提交。不同账号不持锁等待数据库；较新的提交通过 epoch 仲裁，Logout/到期撤销通过 revision 使撤销前已登记的在途结果失去提交资格。密码校验在撤销之后才完成的请求可以登记新的登录操作；已分配 epoch 的旧结果不能因活跃会话被删除而重新成功。提交后替换旧大厅绑定，并在账号锁外请求 Close reason2。代际分配不会因服务重启回退；分配成功但尚未建立会话的失败不回滚代际。Resume 不分配新 epoch，保留会话ID/有效期，原子消费并轮换随机32字节恢复凭据；仅保存摘要。注销不删除账号。

账号会话仍是单应用进程内存状态，账号状态最多1024个，包含首次登录在途操作预留的槽位；新登录时回收过期会话，退出时清零凭据。每账号状态仅在仍有会话或在途操作时保留，操作的 finally 归还引用，注销后不会无界保留历史账号。连接持有独立绑定记录，即使会话索引被清理也保留终止原因：顶号/Resume 替换为2、到期为4、撤销为7；Logout 自身仍按响应后 Close1 结束。服务重启后账号和密码哈希保留，活跃会话与 Resume 凭据失效，客户端需显式重新登录。这个实现不支持多个大厅实例共同仲裁同账号会话；持久化 epoch 分配本身不能替代跨进程撤销和会话所有权。生产多实例接入需先设计这部分。

注册、登录受每IP/账号每秒5次限制；两张限流表分别最多128条，满时拒绝新键，不无界增长。密码计算、数据库认证与会话提交全局最多4个在途操作，满时明确返回空正文 StatusCode=1，不另建等待队列。账号容量与 IP/账号认证限频也使用 status1；Login 过载先回复再 Close1，认证请求均在 finally 释放 loginInFlight。Register 的全局/账号限额拒绝释放认证资格，允许客户端在原登录期限内显式再次请求；IP 限额在传输准入处拒绝并关闭。密码计算使用工作线程；数据库异步访问，不在网络/native owner 中等待，不持会话锁等待IO。运行连接池最多16条，连接与命令超时5秒；登录整体仍受连接的10秒期限约束。

凭据错误统一返回 InvalidCredentials 并响应后 Close5。外部顶号与应用期限由 connection.RequestClose(reason) 交给传输发送 owner，有界发送 Close 后停止和 join；不会等待当前业务 handler 完成才发送，也不会继续派发已失效的排队业务。正常传输 EOF 仍按原契约排空已准入工作。数据库错误、表结构错误、连接失败和取消按原失败语义传播，不伪装成凭据错误、注册成功或自动重试。数据库提交与网络响应不是同一事务：客户端超时、断线或取消不证明注册未落库。实际 WSS 启动通过 host DI 注入 ILogger<WssServer>，KCP 联调入口持有标准 ILoggerFactory 并注入 listener logger；handler、数据库及清理故障从现有框架日志边界记录，日志不包含请求正文、密码或恢复凭据。请求/响应敏感 DTO 和临时字节缓冲按既有清理契约处理；.NET string 不能保证原地清零。

## 验证

```sh
GF_POSTGRES_TEST_CONNECTION='Host=127.0.0.1;Database=独立测试库;Username=测试角色;Password=测试密码' dotnet test Tools/NetworkServer.Tests
```

测试角色需能创建 schema。测试每次创建随机独立 schema，执行同一 SQL 脚本并仅删除自身 schema，不清空用户表。WSS 测试分别使用存储替身与真实 PostgreSQL；未设置变量时 PostgreSQL 部分明确 skipped，不算数据库验收。KCP 实联另需 GF_KCP_NATIVE_LIBRARY。CI 的 protocol-and-wss job 提供独立 PostgreSQL service。

2026-10-12 数据库模块提取验证：Release 解决方案编译零警告/错误；独立临时 PostgreSQL 18 上的 PostgresAccountTests 与 WssServerTests 共59项全部通过、零 skipped，包含连接探测不依赖/创建账号表、取消传播与共享池释放。维护工具实际验证 start/status/create-db/init-schema/stop，以及重复建库、重复初始化和缺失数据库的失败退出码。实际 Game.Server 使用已保存开发证书连接临时数据库启动，系统 curl 正常验证 HTTPS，Ctrl+C 退出码0。临时 cluster 已停止并清理；未创建本机长期开发库，未执行 Windows/Linux 平台实跑或生产部署验证。本次仅修改 .NET 数据库基础设施与维护工具，不涉及 Unity 脚本。

协议从 Proto 生成，注册路由和新增结果码改变协议 hash，Unity 与 .NET 必须同时使用本次生成产物。协议/源码检查、数据库测试、Editor 编译与平台实跑分别记录在 NetworkImplementation.md；本说明不表示生产部署、Docker 实跑或多实例/容量验收已经完成。
