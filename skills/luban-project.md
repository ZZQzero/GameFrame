# GameFrame 的 Luban 项目上下文

本文件供 Luban skill 按需读取，路径相对于仓库根目录；执行前仍以当前脚本和 `luban.conf` 为准。

| 用途 | 路径或设置 |
| --- | --- |
| 配置与运行入口 | `Unity/Config/luban.conf`、`DotNet/ConfigGenerator`；gen.sh/gen.bat 与 Unity 菜单调用同一工具 |
| Excel 输入与 Schema | `Unity/Config/Excel`，包括 `__tables__.xlsx`、`__beans__.xlsx`、`__enums__.xlsx` |
| 生成代码 | `Unity/Assets/Scripts/Hotfix/TableConfig/Generated` |
| 生成数据 | `Unity/Assets/Config/Excel/Bytes` |
| 服务端代码与数据 | `DotNet/Game.Config/Generated`、`DotNet/Game.Config/Bytes` |
| 生成格式与目标 | 工具依次使用 `-t client` / `-t server`，均为 `-c cs-bin -d bin --strict` |
| 生成命名空间 | 客户端 `Game.Hotfix.TableConfig`；服务端 `Game.Config.TableConfig` |
| 加载入口 | `TableConfigManager.Init(ResourceLoadManager, token)`，由 GameEntry 调用 |

macOS/Linux 从仓库根目录运行 `bash Unity/Config/gen.sh`；Windows 在 `Unity/Config` 目录运行 `gen.bat`。统一入口需要 SDK 10.0.401 和 .NET 8 运行时（Luban）；Unity/Hub 的 PATH 可能不同，两个脚本支持 `DOTNET_EXECUTABLE` 指定可执行文件。IDE 选择 ConfigGenerator 的 Generate Server、Generate All 或 Check 启动项。

只读检查代码与数据是否需要重新生成：

```sh
(cd DotNet && dotnet run --project ConfigGenerator --launch-profile Check)
```

Check 在系统临时目录生成两端产物并比较正式目录，忽略 `.meta/.uid`，差异返回 1；成功、失败或取消均清理临时输出，不写正式资产。工具不引用 Game.Config，可在既有生成产物缺失时独立运行。

只校验数据而不写生成资产时，从仓库根目录执行当前主 CLI：

```sh
dotnet Unity/Config/Luban/Luban.dll --conf Unity/Config/luban.conf -t all -f --strict --errorFormat json
```

环境找不到 `dotnet` 时先检查可执行文件路径。此命令不指定代码或数据输出目标，不会重新生成资产。

如需验证重新生成的输出而不覆盖 Unity 资产，两个脚本支持客户端 `LUBAN_OUTPUT_CODE_DIR`、`LUBAN_OUTPUT_DATA_DIR` 和服务端 `LUBAN_SERVER_OUTPUT_CODE_DIR`、`LUBAN_SERVER_OUTPUT_DATA_DIR`，四个变量应全部指向本次独立的临时目录；对比输出后再决定授权范围内的修改。

默认生成入口分别导出 client（c）和 server（s）；ConfigGenerator 的 `--target all` 表示分别生成这两个目标，Luban 自身的 `all`（c/s/e）仅用于全组检查，不是发布目标。空表分组遵循 luban.conf 的 default group；空字段分组对所有目标可见。分组变更仍需检查两端实际生成内容，不为填表或排错顺带改变生成目标。

服务端由 `Game.Server.Configuration.TableConfigLoader` 在监听前加载 Tables，默认读取 `AppContext.BaseDirectory/Config/Bytes`，支持 `GF_CONFIG_DIR`；源码与数据随生成同步维护，详情见 [服务端说明](../Docs/DotNetUsage.md)。

保持生成代码和数据同步；不手改生成目录，也不维护额外的表名列表。需要 Unity 加载验证时阅读 [启动说明](../Docs/Launch.md)，配置以 YooAsset 地址加载，TextAsset 句柄由现有 ResourceLoadManager 释放。

只有发现当前环境提供 Luban MCP 或 `Luban.Agent.dll` 后才能使用这些额外能力；没有时检查实际 Excel、配置和主 CLI 输出，不依赖不存在的工具。
