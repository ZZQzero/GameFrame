---
name: gameframe-native-plugin
description: 诊断 GameFrame 的 Sqlite/MediaBackup 原生库加载问题，或按请求构建安装指定平台插件。适用于 DllNotFoundException、依赖库、ABI、PluginImporter 和原生产物校验；不用于纯托管代码修改。
---

# GameFrame 原生插件

先阅读 [插件布局](../../Tools/README.md)。路径相对于仓库根目录；本 skill 提供诊断和构建流程，不将诊断请求扩展为修改、安装或多平台重建。

Sqlite 的接口、构建与验证说明见 [Sqlite.md](../../Docs/Sqlite.md)，已有平台证据见 [SqliteValidation.md](../../Docs/SqliteValidation.md)。原生 ABI 协议和第三方声明仍位于模块内，从系统说明链接进入。

## 入口

| 模块 | 源码、构建脚本 | 安装产物 |
| --- | --- | --- |
| Sqlite | `Unity/Assets/Scripts/Runtime/Sqlite/Native~/build` | `Unity/Assets/Plugins/GameFrame/Native/Sqlite` |
| MediaBackup | `Unity/Assets/Scripts/Runtime/MediaBackup/Native~/build` | `Unity/Assets/Plugins/GameFrame/Native/MediaBackup` |

每个构建目录包含 `build.py`、`install.py`。原生导入名仍为 `uiframe_sqlite` / `uiframe_backup`，iOS 按实际桥接使用 `__Internal`；不随程序集改名修改 ABI 或库名。

## 加载问题诊断

1. 确认实际运行平台、CPU 架构、Editor/Player 环境，以及失败的库名或入口。`DllNotFoundException` 可能来自传递依赖，不能仅据此断言目标文件不存在。
2. 检查目标二进制、`.meta` 的平台/CPU/预加载设置以及 `artifact.json`、`build-manifest.json`。结合清单检查哈希、目标平台、ABI 和依赖构建 ID，不绕过安装器校验。
3. 用平台现有工具检查依赖和架构：macOS 可用 `file`、`lipo -info`、`otool -L` / `otool -l`、签名检查；Windows 检查 PE 架构、导出和 DLL 依赖；Android/iOS 按对应工具链检查。缺少工具时如实报告，不自动下载 SDK。
4. macOS 的 Backup 在 Player 中通过 `@loader_path` 查找相邻 Sqlite，Editor 布局还使用 `@loader_path/../../Sqlite/macOS`。确认实际 RPATH 和布局，不通过复制重复库或改名来掩盖依赖错误。
5. Unity 已加载的库替换后需要重启才能验证新二进制；在同一 Editor 进程里再次调用不能证明替换有效。结束诊断时给出证据和修复范围。

## 构建与安装

明确用户请求的平台和模块后，查看当前 `build.py --help`、`install.py --help` 及实现。Sqlite 支持的平台不一定都受 Backup 或安装器支持；例如不能把 Sqlite 的 `ios-simulator` 支持推广到整条安装流程。

- 构建传入明确的 `--cmake`、`--output`、`--target`；输出使用独立目录，不能把插件安装目录当作 CMake 输出目录。
- Android 需要匹配的 `--ndk`；非 Windows 主机交叉构建 Windows 需要 `--llvm-mingw`。只使用当前可用或已授权准备的工具链。
- 先构建并安装 Sqlite，再构建 MediaBackup；Backup 的 `--core-build` 必须指向这次对应的 Sqlite 构建目录。
- 两者目标一致，Backup 清单的 `sqlite_build_id` 必须与已安装 Sqlite 一致。Sqlite 更换后检查现有 Backup 是否需要同步重建，不报告部分更新为完整可用。
- 安装用 `python3 <模块>/Native~/build/install.py <构建目录>`，由安装器校验源码和产物并原子替换二进制。保留已有 `.meta` GUID，不手工修改哈希以绕过校验。
- Sanitizer 产物只用于对应验证，不安装到 Unity。修改源代码后重新构建，不能复用旧清单。
- `Tools/migrate_native_layout.py` 是特定历史迁移的核验工具，普通构建不执行 `--apply`。

## 验证与交付

查看构建清单的 `native_tests`，区分已运行、仅构建和未运行。重启后的 Unity 验证应覆盖对应 Sqlite/Media 的加载与生命周期测试；移动平台还需对应 Player/真机证据。

报告模块、平台、产物路径、清单校验与测试结果；若需要重启但尚未重启，明确标记加载验证未完成。不把单平台成功宣称为全部平台通过。
