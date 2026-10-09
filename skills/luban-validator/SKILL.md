---
name: luban-validator
description: Writes Luban field validators (ref, range, path, size, set, regex, non-default). Use when adding validation rules on types or Excel ##type cells.
---

# Luban: 校验器

先阅读 [项目上下文](../luban-project.md)。校验在生成期执行，语法以当前 Luban 版本和现有字段为准；先使用无输出的 `--strict` 校验确认规则。

## 常用写法

| 能力 | 示例 |
|------|------|
| 非默认 | `int!`、`int?!` |
| 引用 | `int#ref=item.TbItem` |
| 引用可跳过 0 | `int#ref=item.TbItem?` |
| 范围 | `int#range=[1,100]` |
| 路径 | `string#path=unity` + `-x pathValidator.rootDir=...` |
| 集合大小 | `(list#size=4),int` |
| 允许值 | `int#set=1;2;3` |
| 正则 | `string#regex=^[a-z]+$` |

写在 XML `type="..."` 或 Excel `##type`。

## 步骤

1. 从 `Unity/Config/Excel/__tables__.xlsx` 和实际 Schema 确认被引用表全名；额外 schema 工具或 MCP 需先确认可用。
2. 改字段类型字符串，保留原有 group/注释。
3. 从仓库根目录运行 `dotnet Unity/Config/Luban/Luban.dll --conf Unity/Config/luban.conf -t all -f --strict --errorFormat json`，保留字段定位和失败原因；规则变化应验证受约束的数据和相邻有效数据。

## 注意

- 容器约束加在容器上：`(list#size=n),T`
- 可空为 null 时多数引用类校验会跳过
- 不要用削弱校验的方式「修好」坏数据
