---
name: luban-generate-debug
description: Diagnoses Luban generation and validation failures. Use when gen.bat fails, schema/data errors appear, or the user pastes Luban logs / --errorFormat json reports.
---

# Luban: 生成失败排查

## 优先动作

1. 阅读 [项目上下文](../luban-project.md)，确认失败来自 dotnet/脚本环境还是 Luban 本身。`dotnet: command not found` 先检查可执行文件和 Unity/Hub 的 PATH，不先改资源目录。
2. 用当前主 CLI 做无输出校验，解析 JSON 报告的 `ok`、`errors[]` 和进程退出码；诊断请求只确认原因，修复须符合用户请求。
3. 看 `category`：`schema` / `data` / `validation` / `codegen` / `cli`。有 `file` + `location` + `fieldPath` 时定位对应单元格/字段。

## 排查顺序

1. **CLI**：`--conf` 路径、`-t` 是否存在、`-c`/`-d` 是否匹配。
2. **Schema**：目标 group、表 value_type、继承关系。
3. **Data**：类型解析、枚举名、必填、分隔符。
4. **Validation**：`ref` / `range` / `path`（path 需 `pathValidator.rootDir`）。
5. **Codegen**：关键字冲突、非法标识符。

## 有用命令

```bash
# 从仓库根目录校验，不生成代码或数据
dotnet Unity/Config/Luban/Luban.dll --conf Unity/Config/luban.conf -t all -f --strict --errorFormat json

# 检查当前版本支持的参数
dotnet Unity/Config/Luban/Luban.dll --help
```

只校验不涵盖 codegen；若失败属于生成阶段，按原目标复现到独立临时输出目录。Agent CLI、MCP 和额外代码目标只能在确认可用后使用。

## 常见坑

- 未登记 `__tables__`
- Sheet 无 `##` 头
- 引用 ID 不存在（`#ref=`）
- 输出目录被清掉了手写文件（目录选错）

## 原则

修好数据或按程序意图改 schema；禁止为通过生成而削弱校验（除非用户明确要求且说明风险）。
