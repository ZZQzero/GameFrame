# GameFrame 项目开发规则

本规则适用于本仓库的开发工作，代码格式要求适用于维护的 C# 代码，包括 AOT、Runtime、Hotfix、Editor 和 Tests。
第三方源码（包括 LoopScroll）、Packages 和自动生成代码不参与格式统一；生成代码需要调整时修改生成配置或模板后重新生成。

## 项目目标与决策原则

- GameFrame 是 Unity 游戏框架，通用能力保持业务无关，游戏配置与业务代码放在 `Game.Hotfix`。
- 优先保证正确性，其次保持实现简单；性能优化需要明确的瓶颈或成本依据。
- 优先使用现有模块，不为单一需求新增通用层、管理器或转发接口。
- 不主动添加兼容旧接口、自动重试、失败回滚或静默降级；有明确需求时再实现。
- 删除代码前检查调用方、资源所有权和生命周期，不能仅因代码复杂或名称老旧就判定冗余。
- 先核对实际代码、配置和调用关系，再选择修改方案；已有文档与实现不一致时说明差异，不凭猜测扩大修改。
- 用户明确要求的行为优先于本文中的项目惯例；新的设计决定应同步更新对应文档。

## 项目地图与程序集边界

以下路径均相对于仓库根目录，Unity 项目根目录是 `Unity/`。

| 路径 | 职责 |
| --- | --- |
| `Unity/Assets/Scripts/Runtime` | 通用框架；程序集为 `GameFrame.Runtime`、`GameFrame.Sqlite`，命名空间使用 `GameFrame.*` |
| `Unity/Assets/Scripts/AOT` | 安装包内的启动和资源准备；程序集与命名空间为 `Game.AOT` |
| `Unity/Assets/Scripts/Hotfix` | 热更配置和游戏业务；程序集与根命名空间为 `Game.Hotfix` |
| `Unity/Assets/Scripts/Editor` | 编辑器工具；`GameFrame.Editor` 仅用于 Editor，普通工具菜单统一在 `Tools/GameFrame` 下 |
| `Unity/Assets/Scripts/Tests` | `EditMode`、`PlayMode` 测试，分别对应两个测试程序集 |
| `Unity/Config` | Luban 配置、Excel 输入和生成脚本 |
| `Unity/Assets/Config` | YooAsset 采集的配置数据等资源；不是 Luban 的 Excel 输入目录 |
| `Unity/Assets/Plugins/GameFrame` | 平台插件和原生库安装产物 |
| `Docs` | 各系统的使用说明、API 与行为契约，另含启动及跨模块说明 |
| `Tools` | 项目维护脚本与原生插件布局说明 |
| `skills` | 仓库内的验证、原生插件和 Luban 工作流说明 |

- Runtime 不依赖 Editor、AOT 或 Hotfix；AOT 不引用 Hotfix，Hotfix 可以引用 AOT 和 Runtime。
- Runtime 中的平台适配或必要的编辑器条件编译应保持用途明确；不要引入对 `GameFrame.Editor` 的程序集依赖。
- `AssemblyInfo.cs` 中的 `InternalsVisibleTo` 用于声明内部 API 的访问范围，不因为有 asmdef 就直接删除。

## 系统文档

`Docs/` 中的文档是对应系统的说明。修改模块前先阅读相关文档，确认公开接口、使用方式、生命周期和错误语义；接口、配置或行为变化后同步更新对应说明。只读取当前任务涉及的文档。

常用入口：UI 为 `Docs/USAGE.md`，启动为 `Docs/Launch.md`；Timer、Input、Audio、Pool、Scene、EventSystem、Fsm、RedDot 分别有同名系统文档。Media/备份参考 `Docs/Gallery.md` 与 `Docs/GalleryDesign.md`；Sqlite 的说明为 `Docs/Sqlite.md`、`Docs/SqliteDesign.md` 和 `Docs/SqliteValidation.md`。原生 ABI 协议、第三方声明和示例说明保留在 Sqlite 模块内，与对应源码配套。

`Docs/Scope.md`、`Docs/ErrorContract.md` 说明跨模块约定。Skill 负责开发和验证流程，通过引用这些系统说明获取行为契约，不另外复制一套系统文档。文档与实现冲突时先说明差异，再按用户要求确认或修正行为。

## 启动与生命周期

- 修改启动相关代码前阅读 [启动流程](Docs/Launch.md)；Launch 管理框架与资源，GameEntry 只初始化热更配置与业务。
- 语言服务、事件派发器和 UI 在 YooAsset 初始化前准备；主相机 Stack 由 Launch 显式配置，清单就绪后绑定 UI Package。
- 热更 DLL 在 GameEntry prefab 实例化前装载；业务入口由 prefab 的 `Awake` 调用，不增加反射入口调用或 Launch 对外访问接口。
- 正式包使用 IL2CPP；Editor 的模拟加载与正式包的 DLL、AOT 元数据加载按当前流程分别验证。
- 资源句柄、事件订阅、计时器和异步任务明确归属；面板生命周期参考 [作用域说明](Docs/Scope.md)。
- 区分运行中主动关闭与进程退出：前者按模块契约释放资源并等待必要操作，后者保持简单，不增加退出拦截和等待流程。
- 异常和资源释放遵循 [错误处理契约](Docs/ErrorContract.md)；不把业务失败伪装成取消或成功，也不将某模块的故障策略推广到所有系统。

## 资产迁移与代码生成

- 移动或重命名 Unity 资产时同时处理 `.meta` 并保留 GUID，检查场景、prefab 和配置引用。
- 修改程序集名或命名空间时检查 asmdef/asmref、`InternalsVisibleTo`、序列化类型标识、HybridCLR 登记、DLL 加载列表、资源地址、测试和文档。
- 程序集改名后需要重新生成对应的热更 DLL；不要仅重命名旧 DLL 来替代重新编译。
- Luban 输入位于 `Unity/Config/Excel`，配置为 `Unity/Config/luban.conf`；代码输出为 `Unity/Assets/Scripts/Hotfix/TableConfig/Generated`，数据输出为 `Unity/Assets/Config/Excel/Bytes`。
- Luban 表类型使用 `Game.Hotfix.TableConfig`；配置加载复用现有 `TableConfigManager` 和 `ResourceLoadManager`，不另外维护一份表名列表。
- 不将手写代码放入生成目录，不手动修改生成文件，也不修改 Unity 缓存来代替修复源文件。
- Sqlite/Backup 的原生库名和 ABI 不是程序集名，不因框架改名顺带修改；插件构建和安装遵循 [原生插件说明](Tools/README.md)。

## 格式

- 使用 4 个空格缩进，不使用 Tab。
- 使用 Allman 大括号风格：左大括号单独换行，右大括号也单独占一行。
- `if`、`else if`、`else` 必须使用大括号，即使只有一条语句，或只是 `return`、`throw`、`continue`、`break`。
- 禁止将条件和执行语句写在同一行，禁止省略条件分支的大括号。
- `for`、`foreach`、`while`、`do` 和带语句体的 `using`、`lock` 同样使用换行大括号；允许使用 `using var` 声明。
- `try`、`catch`、`finally` 的语句体必须展开，不能写成单行代码块。
- 方法、类型和命名空间的代码块遵循相同规则。简单属性和单一表达式方法允许使用 `=>`。
- 控制流关键字与括号之间留一个空格，例如 `if (condition)`；方法调用名和括号之间不留空格。
- 每行一条语句，相关逻辑之间使用空行，避免将多段逻辑压缩到一行。

正确示例：

```csharp
if (config == null)
{
    throw new ArgumentNullException(nameof(config));
}
else if (!config.Enabled)
{
    return;
}
else
{
    Initialize(config);
}
```

禁止的写法：

```csharp
if (config == null) throw new ArgumentNullException(nameof(config));

if (!config.Enabled)
    return;

if (config.Enabled) { Initialize(config); }
```

## 命名与实现

- 类型、方法、属性和事件使用 PascalCase；参数、局部变量使用 camelCase。私有字段沿用所在模块的命名方式，避免无关重命名。
- 异步方法使用 `Async` 后缀，资源句柄在生命周期结束时释放，取消信号向下传递。
- 启动失败停止后续初始化，不继续执行依赖失败步骤的业务。
- 只在能够处理错误或必须执行清理时使用异常处理；不吞掉异常，不堆砌重复校验和转发层。
- 注释解释约束、意图和必要的行为，不重复描述显而易见的代码。

## 修改与检查

- 新增代码和修改涉及的代码块遵循上述规则；全仓库格式化作为单独任务处理。
- 格式调整保持行为不变，不混入无关逻辑修改，保留用户已有改动。
- 提交前检查差异、空白和大括号；逻辑变更按影响范围进行编译或测试。
- 所有游戏、框架和编辑器工具的开发、调试与验证，默认不向 `output/` 或仓库其他目录生成、保留仅用于检查的截图、预览图片等文件；只有用户明确要求导出或保留时，才将它们作为交付产物保存。
- 优先通过工具直接查看运行效果；确实需要截图验证时，使用系统临时目录，并在验证结束后清理本次任务创建的临时文件，不删除用户已有文件。

## 验证入口与结果说明

- Unity 行为以实际编译、测试和场景运行结果为依据；使用 Unity MCP 时先确认编辑器状态，脚本编译完成后检查 Console 错误。
- EditMode 测试位于 `Unity/Assets/Scripts/Tests/EditMode`，程序集为 `GameFrame.Tests.EditMode`；PlayMode 对应 `GameFrame.Tests.PlayMode`。按影响选择已有测试，必要时再全量运行，不为简单格式修改新增测试。
- 程序集边界或条件编译变更需检查 Player 脚本编译；启动流程变更还需验证 `Unity/Assets/Scenes/Launch.unity`、GameEntry 就绪和相机 Stack。
- 记录测试前的场景和 EditorSettings，结束后恢复工具造成的变化，保留用户设置与已有修改。
- macOS/Linux 从仓库根目录运行 `bash Unity/Config/gen.sh`；Windows 在 `Unity/Config` 目录运行 `gen.bat`。生成依赖 .NET 8，Unity/Hub 启动环境可能与终端 PATH 不同；优先使用脚本现有的路径配置。
- 原生插件的 `build.py`、`install.py` 位于各模块的 `Native~/build`，执行前查看参数和目标平台；先构建安装 Sqlite，再让 MediaBackup 使用同一核心产物。替换已加载的原生库后需重启 Unity 才能有效验证。
- `python3 Tools/migrate_native_layout.py` 用于核验既有迁移产物，不是通用构建入口；不要为普通构建调用其 `--apply`。
- 汇报明确说明改了什么、验证了什么及未验证部分；区分源码检查、Editor 编译、Player 脚本编译、原生 IL2CPP 构建和真机验证，不将其中一项当作其余项完成。

## 项目 Skills

任务符合下列用途时，先阅读对应 `SKILL.md` 再执行；只读取需要的工作流。项目 skill 不扩大用户授权范围，用户当前要求优先。路径和能力以实际配置为准，不假定示例中的工具已安装。

| 任务 | Skill |
| --- | --- |
| 修改后检查、Unity 回归测试、启动验证 | [gameframe-validate](skills/gameframe-validate/SKILL.md) |
| PrimeTween 动画、UI 缩放旋转与飘字 | [unity-primetween](skills/unity-primetween/SKILL.md) |
| Sqlite/Backup 原生库加载诊断、构建安装 | [gameframe-native-plugin](skills/gameframe-native-plugin/SKILL.md) |
| 新增 Luban 配置表 | [luban-add-table](skills/luban-add-table/SKILL.md) |
| 修改 Excel 配置数据 | [luban-excel-fill](skills/luban-excel-fill/SKILL.md) |
| Luban 生成失败与环境排查 | [luban-generate-debug](skills/luban-generate-debug/SKILL.md) |
| 配置表运行时加载 | [luban-runtime-load](skills/luban-runtime-load/SKILL.md) |
| 表结构、Bean、枚举和分组设计 | [luban-schema-design](skills/luban-schema-design/SKILL.md) |
| 引用、范围等生成期校验 | [luban-validator](skills/luban-validator/SKILL.md) |
