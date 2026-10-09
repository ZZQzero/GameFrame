# GameFrame 的 Luban 项目上下文

本文件供 Luban skill 按需读取，路径相对于仓库根目录；执行前仍以当前脚本和 `luban.conf` 为准。

| 用途 | 路径或设置 |
| --- | --- |
| 配置与运行入口 | `Unity/Config/luban.conf`、`Unity/Config/gen.sh`、`Unity/Config/gen.bat` |
| Excel 输入与 Schema | `Unity/Config/Excel`，包括 `__tables__.xlsx`、`__beans__.xlsx`、`__enums__.xlsx` |
| 生成代码 | `Unity/Assets/Scripts/Hotfix/TableConfig/Generated` |
| 生成数据 | `Unity/Assets/Config/Excel/Bytes` |
| 生成格式与目标 | 当前脚本使用 `-t all -c cs-bin -d bin` |
| 生成命名空间 | `Game.Hotfix.TableConfig` |
| 加载入口 | `TableConfigManager.Init(ResourceLoadManager, token)`，由 GameEntry 调用 |

macOS/Linux 从仓库根目录运行 `bash Unity/Config/gen.sh`；Windows 在 `Unity/Config` 目录运行 `gen.bat`。依赖 .NET 8；Unity/Hub 的 PATH 可能不同，`gen.sh` 支持 `DOTNET_EXECUTABLE` 指定可执行文件。

只校验数据而不写生成资产时，从仓库根目录执行当前主 CLI：

```sh
dotnet Unity/Config/Luban/Luban.dll --conf Unity/Config/luban.conf -t all -f --strict --errorFormat json
```

环境找不到 `dotnet` 时先检查可执行文件路径。此命令不指定代码或数据输出目标，不会重新生成资产。

如需验证重新生成的输出而不覆盖 Unity 资产，`gen.sh` 支持 `LUBAN_OUTPUT_CODE_DIR`、`LUBAN_OUTPUT_DATA_DIR`，应指向本次独立的临时目录；对比输出后再决定授权范围内的修改。

当前 `all` 目标包含 `c/s/e`，不能假定已经排除服务端字段。发布分组需要单独检查需求、脚本和配置，不为填表或排错顺带改变生成目标。

保持生成代码和数据同步；不手改生成目录，也不维护额外的表名列表。需要 Unity 加载验证时阅读 [启动说明](../Docs/Launch.md)，配置以 YooAsset 地址加载，TextAsset 句柄由现有 ResourceLoadManager 释放。

只有发现当前环境提供 Luban MCP 或 `Luban.Agent.dll` 后才能使用这些额外能力；没有时检查实际 Excel、配置和主 CLI 输出，不依赖不存在的工具。
