---
name: gameframe-validate
description: 验证 GameFrame 的 Unity 代码变更，选择相关测试、检查程序集和 Player 脚本编译，并验证 Launch 启动。适用于修改后的检查、回归测试和启动故障验证；纯文档或格式修改不触发完整测试。
---

# GameFrame 变更验证

仓库根目录由当前项目确定，Unity 项目位于 `Unity/`。先阅读根目录 `AGENTS.md` 和 `Docs/` 中对应系统的说明，用其接口与行为契约确定预期；涉及启动时阅读 [Launch.md](../../Docs/Launch.md)，涉及 Sqlite 时阅读 [Sqlite.md](../../Docs/Sqlite.md) 和 [验证记录](../../Docs/SqliteValidation.md)。原生协议等源码配套文档从系统说明的链接查阅，不把现有实现直接当作正确行为的唯一标准。

## 选择验证范围

| 变更 | 优先验证 |
| --- | --- |
| 文档、代码格式 | 差异、路径、格式检查 |
| Editor 工具、纯逻辑、Sqlite/Media 契约 | 对应 EditMode 测试及相邻正常路径 |
| UI、相机、帧驱动、异步生命周期 | 对应 PlayMode 测试 |
| Launch、GameEntry、资源加载 | `ResourceLoadManagerTests`、相关 `GameRuntimeTests` / `LoopAndRootTests`，必要时实际 Launch 场景 |
| 程序集、命名空间、平台条件编译 | Editor 编译、引用与序列化检查、当前目标的 Player 脚本编译 |

用户要求全量时运行 EditMode 与 PlayMode 两套测试；否则按实际依赖确定范围。测试类名称以现有源码为准，不根据示例新增同名测试。

## Unity 执行

1. 使用已配置的 Unity MCP 能力时，先读编辑器状态和实例信息，确认连接到本项目、没有编译或测试正在运行。使用当前工具 schema，不硬编码地址、会话 ID 或旧参数。
2. 记录当前场景、播放状态和可能被测试工具改变的 EditorSettings。场景有未保存的用户修改时保留它们，不直接切换或覆盖场景。
3. 从文件系统修改脚本后，按需请求资源刷新和编译；等待编译、Domain Reload 完成，再检查 Console。编译失败时先处理或报告错误，不继续运行依赖新代码的测试。
4. 使用 `run_tests` 启动任务，保存返回的 job ID，通过 `get_test_job` 等待结果。PlayMode 初始化可设置 `init_timeout: 120000`，等待参数以工具支持的范围为准。不同时启动两轮测试。
5. 失败时查看完整测试名、断言、堆栈和前置状态。区分业务回归、测试隔离、资源/原生库和环境问题。只有修复已获授权时才修改源码，不为通过测试削弱断言。
6. 无论成功或失败，都恢复本次验证造成的场景、播放状态和设置变化。恢复测试前的值，不假定用户默认启用了 Domain Reload。

MCP 不可用时使用当前环境可用的 Unity 验证方式；不能用普通 .NET 编译替代 Unity 编译，也不能把源码检查报告成测试通过。

## Launch 验证

实际场景为 `Unity/Assets/Scenes/Launch.unity`，以当前绑定的 GlobalConfig 为准，不为验证擅自改变加载模式。

- 语言服务和 UI 先于 YooAsset 准备，UI Camera 被显式加入主相机 Stack 且不重复添加。
- 清单就绪后 UI 绑定 Package；boot 下载和框架初始化成功后，才加载 DLL 和实例化 GameEntry。
- GameEntry 的 `IsReady`、配置表和当前语言符合预期，补入翻译不重置语言，也不重复创建 UI 根节点。
- 启动和停止时检查新增 Console 错误；不要将预期日志、历史错误或测试主动触发的异常直接归为新回归。

Editor 使用已编译的 Hotfix 程序集，不能据此证明正式 DLL/AOT 元数据加载正确。

## 输出

报告验证范围、通过/失败数量、失败原因和未执行项。清楚区分 Editor 编译、Player 脚本编译、原生 IL2CPP 打包与真机验证；记录实际目标平台。通过相关检查后结束验证，除非新变化或失败说明需要扩大范围。
