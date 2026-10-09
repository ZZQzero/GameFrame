---
name: luban-schema-design
description: Designs Luban schema (beans, enums, polymorphism, collections, groups). Use when modeling config structures, inheritance, nested beans, or choosing Excel vs XML schema.
---

# Luban: Schema 设计

先阅读 [项目上下文](../luban-project.md)，以 `Unity/Config/Excel` 现有 Schema 和 `luban.conf` 为基础设计；不要把通用示例目录直接用于本项目。

## 原则

- 程序维护 Schema；策划填 Data。
- 复杂 GamePlay（技能/行为树）优先 OOP 继承/多态，而不是塞字符串。
- 服务端字段按需求使用 `s` group，并检查实际生成目标；当前 `all` 包含 `s`，仅标记 group 不能保证不下发。

## 选型

| 需求 | 建议 |
|------|------|
| 扁平行表 | Excel + `read_schema_from_file` |
| 多模块/复用 bean | XML `Defines/*.xml` 或 `__beans__` |
| 多态配置 | 抽象 bean + 子类；Excel 填类型名/别名 |
| 一对多嵌套 | `list,Bean` / 多行 / sep 紧凑格式 |

## 类型要点

- 容器：`list,T` / `map,K,V`；**元素不可写 `list,int?`**
- 可空：`T?`；多态非空必须给具体子类
- 引用：`int#ref=module.TbX`
- 字段名沿用现有 Schema 约定，不为风格偏好批量改名

## 检查清单

1. 主键与 `mode`（map/list/one）是否匹配
2. group 是否覆盖 client/server 需求
3. 多态子类是否都已定义且可区分
4. 用实际 Schema、无输出数据校验和生成类型复核结构；额外 schema 输出目标或 MCP `GetSchema` 需先确认可用。

Schema 变更后重新生成 C# 和 bytes，并检查受影响的业务读取；不要手改生成类型以绕过 Schema 问题。
