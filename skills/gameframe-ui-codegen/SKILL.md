---
name: gameframe-ui-codegen
description: 使用 GameFrame 项目的 UIFrame 为 uGUI Prefab 生成 Panel 或 Item 脚本，维护控件绑定，并在 Unity 编译后挂载脚本、回填及核验序列化引用。适用于新建 UI、已有面板增删绑定，以及生成后脚本未挂载或引用未回填的排查。
---

# GameFrame UI 代码生成与回填

交付保存到磁盘的 UI Prefab、业务 `.cs` 和框架生成的 `.Gen.cs`，确认生成宿主及每个目标控件的引用正确。

## 入口与范围

- 从当前工作区定位仓库根目录，Unity 项目位于 `Unity/`。先读根目录 `AGENTS.md`、[UI 用法](../../Docs/USAGE.md)第 2、10 节和[作用域说明](../../Docs/Scope.md)。通过本机 skills 软链接访问时先解析链接的真实目录，再解析文档相对路径。
- 业务脚本放在 `Unity/Assets/Scripts/Hotfix/<模块>/`，命名空间通常为 `Game.Hotfix`；沿用任务指定的 Prefab 路径、类名和模块目录。`HitfixPrefab` 是已有目录拼写，不擅自改成 `HotfixPrefab`。
- 使用现有 `GameFrame.UI.Editor.UIBindActions`，不手写或手改 `.Gen.cs`，不直接修改 `Library/UIFrameCodeGen/BindStore.asset`，不新增另一套生成器或运行时查找控件的替代机制。
- 只有本次任务需要通过 GameUI 打开新面板时，才补充 `GameUIRegistration` 和 YooAsset 采集配置；生成本身不会自动注册。Item 不注册到 GameUI。不因生成代码而切换启动首屏或接入未要求的业务服务。

## 操作前确认

1. 查看工作区差异，确定目标 Prefab、已有业务脚本、已有生成文件及具体绑定清单。清单注明宿主、节点和组件类型；绑定 GameObject 与绑定组件分别处理。
2. 使用 Unity MCP 时先读 `mcpforunity://instances`、`mcpforunity://project/info`、`mcpforunity://editor/state`、`mcpforunity://custom-tools` 和 `mcpforunity://editor/prefab-stage`，确认本项目连接、非播放态且没有编译或测试正在运行。工具名与参数以当前环境 schema 为准。
3. 记录当前场景、Prefab Stage 的未保存状态和会被工具更改的设置。目标正处于 Prefab Stage 时使用 Stage 对象；不要同时修改磁盘上的另一份实例，或覆盖用户未保存的修改。
4. 先检查当前 `UIBindActions.cs`、`UICompileHook.cs` 的相关入口签名，再调用内部 Editor API。MCP 自动化示例见 [references/mcp-workflow.md](references/mcp-workflow.md)，只读取需要的阶段。没有可用的 Unity 执行工具时使用现有 Inspector 流程，明确尚未执行的部分。

## 首次生成

1. 先保存 UI Prefab，保证 GUID 和节点身份已经建立；不要对临时克隆对象发起生成。创建布局和 TMP 输入框时沿用项目字体与控件体系。
2. Inspector 中使用“生成脚本”，或调用 `TryGenerate(root, className, folder, namespaceName, isItem, out error)`。Panel 生成 `UIPanel<UINone>`，类名会规范化并补 `Panel`；Item 生成 `UIItem`。修改为其他参数类型属于后续业务代码编辑。
3. 检查返回值和错误。调用成功仅代表任务已排队。等待 Unity 编译与 Domain Reload，检查 Console，再重新加载 Prefab，确认生成组件已经挂载。不要在同一次调用中立即绑定新类型。
4. 恢复本次任务改变的 `UIFrame.Gen.Folder` / `UIFrame.Gen.Namespace` EditorPrefs；原键不存在时删除新增键。示例使用 `finally` 恢复，不依赖跨 Domain Reload 保存对象引用。

## 维护绑定并写入

- 从已挂载组件取得准确宿主。嵌套 Item 和外层 Panel 分开维护，明确传入目标宿主，避免默认向上查找绑到另一层。
- 新增组件使用 `TryAddComponent(component, host, out error)`；新增 GameObject 使用 `TryAddGameObject(component, host, out error)`。这一步只记录绑定；随后调用 `TryWriteGen(host, out error)` 才会写入 `.Gen.cs` 并排队回填。
- 已有面板先检查 Inspector 的绑定列表和未写入操作；需要从生成文件重建列表时调用 `TryRefreshBinds(host, out error)`。刷新不会执行回填；它保留待写入新增项，但会从生成文件补回仍存在的字段。
- 删除绑定使用 Inspector 的 `×`，或取得 `GetState(host)` 后按字段名定位索引，再调用 `RemoveBind(state, index)`。紧接着写入生成文件；不要先刷新，否则尚未从生成文件删除的字段会回来。
- 读取实际生成字段名后再修改业务 `.cs`，不要猜测字段命名。按钮监听放 `OnCreate`，清理登记到 `LifetimeScope`，每次显示的数据放 `OnOpen`；行为以系统文档为准。
- 同一批绑定调用遇到错误立即停止后续依赖操作。已记录的绑定可能仍在 Store 中；修正后先检查列表，再显式继续，不重复追加或把“已经绑定”当作新绑定成功。

## 编译后回填

1. `TryWriteGen` 成功后等待新一轮编译和 Domain Reload，检查 Console，再读取 Prefab。内部 API 已 ImportAsset，不需要每一步都强制刷新；文件系统编辑脚本后按需刷新并请求编译。
2. 正常情况下 `UICompileHook` 在编译完成后挂载和回填。如果编辑器已经空闲、没有错误，但任务仍等待，可以检查待办记录，再显式调用一次现有 `ProcessJobs`。它处理整个 Store；存在其他任务的待办时先辨明归属，不把全局处理当作仅作用于当前面板的操作。
3. `ProcessJobs` 遇到实际挂载或回填失败会清除失败任务的等待标记并抛错。修正类名、节点或组件后，通过对应生成/写入入口显式重新排队；不循环调用 ProcessJobs 或手动改 Pending 标记重试。
4. 在 Prefab Stage 完成时保存目标 Prefab，再从磁盘重新加载验证。不要用全局 `AssetDatabase.SaveAssets()` 保存无关的用户资产。

## 验收与排查

- 磁盘上的宿主完整类型名、继承类型正确；生成文件中所需字段存在，类型和名称匹配。
- 使用 `SerializedObject` 逐个读取**本次绑定字段**，比较引用是否恰好等于指定节点的指定组件或 GameObject；仅检查非空不足以发现绑错对象。
- 如 Store 记录存在，确认目标 `PendingAttach`、`PendingAssign` 均已结束。刷新列表不能替代编译与回填验证。
- Store 丢失时，先用 `TryRefreshBinds` 从 `.Gen.cs` 和 Prefab 现有引用恢复。引用也为空时需要显式重新绑定；不能清空生成字段来绕过保护。
- 节点移动或改名时先保留 `.meta` 与 GUID，检查 LocalFileId 和路径。重复名称节点须准确定位实际对象；路径由生成器记录，不自行拼造 `v2:` 编码，也不靠同名猜测回填。
- 若接入了资源加载，核对采集目录 GUID、注册地址及实际采集结果。代码生成成功不代表 YooAsset 能加载该 Prefab。
- 按 [gameframe-validate](../gameframe-validate/SKILL.md) 选择编译、交互与回归验证。纯 skill 或文档修改无需运行整套 Unity 测试。截图只放系统临时目录，验证后清理本次文件。
- 最后报告 Prefab、业务脚本与生成文件位置、实际完成的回填字段、验证结果及未验证项。存在编译或回填阻塞时明确缺失步骤，不声称交付完成。
