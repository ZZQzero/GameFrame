---
name: luban-excel-fill
description: Explains and fills Luban Excel config tables by header conventions. Use when designers fill sheets, ##var/##type/##group rows, nested cells, or polymorphism columns.
---

# Luban: Excel 填表

先阅读 [项目上下文](../luban-project.md)，修改 `Unity/Config/Excel` 下的源表，不编辑生成的 bytes 或 C# 代码。

## 最小约定

| 行 | 含义 |
|----|------|
| `##var` / `##` | 字段名 |
| `##type` | 类型 |
| `##group` | `c`/`s`/`e`；空=跟随默认 |
| `##` 注释行 | 中文说明，不进逻辑 |
| `#` 开头列名 | 注释列，不导出 |

- A1 必须以 `##` 开头，否则整张 sheet 忽略。
- 空值和字符串按现有样表与字段类型处理，不未经确认把空白单元格批量改为 `""`。
- 枚举可填名字或 alias。

## 分组提醒

- `c`：客户端可见
- `s`：仅服务器
- 填错会导致缺字段或敏感数据下发
- 当前生成脚本使用包含 `c/s/e` 的 `all`，不能仅凭 `s` 标记断言字段不会进入当前输出。

## 复杂结构

- 嵌套 / 列表：遵循程序给定的分列或多行样表，不要自创分隔符
- 多态：按样表填具体类型名；拿不准先问程序或查 schema-json

## Agent 行为

1. 改数值前确认列的 `##type` 与 group。
2. 不擅自改 `##type` / 主键列语义。
3. 生成失败时保留行列信息，用 `luban-generate-debug` 排查。
4. 保留原有样式、公式和无关工作表；校验数据后按请求生成配套代码与 bytes。

不要为数据修改擅自改变 Schema、生成目标或分组策略。
