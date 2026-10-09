---
name: luban-runtime-load
description: 在 GameFrame 中接入或验证 Luban 配置加载，使用 YooAsset 的 ResourceLoadManager 与热更 TableConfigManager。适用于生成表接入、配置读取和加载故障，不用于修改 Excel 数据。
---

# Luban: 运行时加载

先阅读 [项目上下文](../luban-project.md) 和 [启动说明](../../Docs/Launch.md)，检查现有 `TableConfigManager`、生成的 `Tables` 与 YooAsset 配置。

## 项目加载方式

```csharp
Game.Hotfix.TableConfig.TableConfigManager.Init(resources, token);
var languageRows = Game.Hotfix.TableConfig.TableConfigManager.Tables.TbLanguage.DataList;
```

- 一个 `Tables` 聚合所有表。
- 避免每张表静态全局单例（热更/测试困难）。
- GameEntry 已负责调用 Init，业务访问已有 Tables，不重复初始化。框架和资源由 Launch 准备。
- 加载回调复用 `resources.LoadConfigByte`；表名由生成代码维护，回调按 YooAsset 地址读取并释放 TextAsset 句柄。

## Agent 检查点

1. 当前输出为 `cs-bin` + `bin`，生成代码与 bytes 来自同一次生成。
2. `topModule` 为 `Game.Hotfix.TableConfig`，业务程序集为 `Game.Hotfix`，AOT 不引用生成表类型。
3. Package 和清单已就绪，配置资源已采集为 boot，地址与生成回调一致；不用 StreamingAssets/Addressables 替换现有加载链。
4. 语言服务在 Launch 提前初始化，GameEntry 用 AddTable 补入翻译并保留当前语言。
5. 当前脚本使用 `all`，分组是否符合发布需求需另外检查，不能假定服务端字段已排除。

改动后优先运行 `ResourceLoadManagerTests` 中配置与入口相关测试，确认成功读取及失败时的资源释放；不为排错添加目录回退或静默空表。
