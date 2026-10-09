---
name: luban-add-table
description: Adds a new Luban config table (Excel + __tables__ registration + regenerate). Use when creating a new table, registering reward/item tables, or when the user asks to add a Luban table.
---

# Luban: 加一张表

先阅读 [项目上下文](../luban-project.md)，确认当前 Excel 输入、生成目标和输出目录。

## 规则

- Schema 是契约：先登记表，再填数据。
- Excel sheet 仅当 A1 以 `##` 开头才被识别。
- `read_schema_from_file=true` 时不要在 `__beans__` 重复定义同名 bean。

## 步骤

1. 参考相邻表结构，在 `Unity/Config/Excel` 新建 `xxx.xlsx`/`csv`：
   - `##var`：字段名
   - `##type`：类型（如 `int` / `string`）
   - 可选 `##group`：`c`/`s`
   - 其后为数据行
2. 在 `Unity/Config/Excel/__tables__.xlsx` 增加一行；字段以现有表头为准：
   - `full_name`：如 `TbReward`
   - `value_type`：如 `Reward`
   - `read_schema_from_file`：`true`（从表头推断字段）时常用
   - `input`：相对 dataDir 的文件名
   - `index`：主键字段（map 表）
3. 先校验数据，再运行项目 `gen.bat` / `gen.sh`，保持当前 `cs-bin` + `bin` 输出；macOS/Linux 从仓库根目录执行：

```bash
bash Unity/Config/gen.sh
```

4. 确认生成的 `Tables` 包含新表且对应 bytes 存在，检查 YooAsset 采集与地址，按需运行加载验证；不手工追加另一份表名列表。

## 常见失败

| 现象 | 处理 |
|------|------|
| 新文件无输出 | 是否写入 `__tables__` |
| sheet 被忽略 | A1 是否 `##` 开头 |
| bean 冲突 | 关闭重复的 `__beans__` 定义 |

## 工具边界

查现有表优先检查实际 Excel 和生成结构；只有当前环境提供 Luban MCP 后才使用 `ListTables` / `GetSchema` 等能力。编辑 Excel 时保留样式、数据类型和其他工作表。
