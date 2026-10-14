---
name: gameframe-feature-development
description: 在 GameFrame 中开发或扩展 Hotfix 功能模块时，按需求选择并组合 Runtime 的 UI、Timer、Event、FSM、Input、Audio、Pool、Scene、Network、Sqlite 等现有系统，处理启动依赖、资源归属与生命周期，并完成相关验证。适用于小游戏、背包、任务、商店、房间、设置、图库等业务功能的框架接入。
---

# GameFrame 功能模块开发

交付使用现有系统的业务功能；依据实际需求选择最少的系统组合，不要求每个模块接入全部系统，也不要求固定的 Model / Service / Controller 文件结构。

## 定位与选用

1. 从工作区定位 GameFrame 仓库，Unity 项目根为 `Unity/`，系统源码位于 `Unity/Assets/Scripts/Runtime/`。读取根目录 [AGENTS.md](../../AGENTS.md)，检查当前差异和目标模块的已有调用方。通过本机 skill 软链接进入时先解析真实目录，再解析相对链接。
2. 先确定功能的进入与退出方式、业务状态存放位置、是否需要服务器或持久化，以及隐藏后是否继续运行。信息可从现有业务推断时直接推进；缺失信息会改变行为时再澄清。
3. 根据下表选择系统，只阅读选中系统的说明及 [作用域说明](../../Docs/Scope.md)、[错误处理契约](../../Docs/ErrorContract.md)。表中的接口是定位入口，不是完整签名；实现前阅读当前入口、参数类型及相关调用方，核对初始化条件、线程、取消和释放规则。
4. 复用附近业务的接入方式。文档中的旧命名或示例与当前源码不一致时明确指出差异，以当前程序集和签名定位；行为契约冲突需确认或修正，不能悄悄改变行为。

## 功能需求与系统入口

源码路径相对于 `Unity/Assets/Scripts/Runtime/`。

| 功能需求 | 系统与源码入口 | 使用边界与说明 |
| --- | --- | --- |
| 页面、弹窗、HUD、提示 | `UI/Core/GameUI.cs`、`UIPanel`、`UIItem`；`GameFrame.UI` | 页面用 Push，结果弹窗用 Popup，常驻显示用 Hud；显示层与 UIGroup 分开选择。[UI 用法](../../Docs/USAGE.md)第 2、6 节 |
| 大量重复列表项 | `UI/LoopScroll/UILoopScrollBase.cs`、`UILoopScrollMultiBase` | 用现有数据提供和池注入机制，不为每个 Cell 广播全局事件。[UI 用法](../../Docs/USAGE.md)第 9 节 |
| 图标与资源图片 | `UI/Load/UIImageLoader.cs` | 绑定 OpenScope，保留显示期间的句柄；一般资源加载另见下文的 AOT 资源服务。[UI 用法](../../Docs/USAGE.md)第 2 节 |
| 冷却、倒计时、延迟、周期任务 | `Timer/GameTimer.cs`、`TimerScheduler`；`GameFrame.Timing` | 先选 Scaled / Unscaled / Realtime / Simulation。倒计时保存 Deadline，Timer 负责唤醒；确定性模拟使用独立调度器。[Timer](../../Docs/Timer.md)第 4、5、12、13、18 节 |
| 模块间业务变化通知 | `Event/EventSystem.cs`；`GameFrame.Event` | 直接调用用于明确的业务操作，事件用于广播变化；主线程 Publish，跨线程 Post 需要 Dispatcher，订阅需释放。[EventSystem](../../Docs/EventSystem.md) |
| 多阶段流程、角色或对局状态 | `Fsm/Fsm.cs`、`FsmGraph`；`GameFrame.Fsm` | 每个实体持有 Fsm，共享图和无实体可变字段的状态逻辑；业务自己 Tick、Change、Stop。简单状态不必强行建 FSM。[Fsm](../../Docs/Fsm.md) |
| 键鼠、手柄输入及玩法输入锁 | `Input/GameInput.cs`；`GameFrame.Input` | 复用配置与动作；UI 锁和玩法暂停是两套状态，嵌套锁按栈收尾。普通 uGUI 按钮不必新增 Gameplay Action。[Input](../../Docs/Input.md) |
| 音效、BGM、音量设置 | `Audio/GameAudio.cs`、`AudioRuntimeConfig`；`GameFrame.Audio` | 使用配置中的业务音频 ID / location，BGM 用专用接口；取消未开始的播放与停止已播放声音分别处理。[Audio](../../Docs/Audio.md) |
| 特效、弹道、飘字、列表 Item 复用 | `Pooling/GamePool.cs`、`GameObjectPoolService`、`ManagedObjectPool`；`GameFrame.Pooling` | 面板缓存交给 GameUI；多实例复用才选池，核对取还和借出对象归属，UI 销毁需先于池关闭。[Pool](../../Docs/Pool.md) |
| 切场、加场、预加载与激活 | `Scene/GameScene.cs`；`GameFrame.Scene` | UI 切换无需切 Unity 场景。切场统一走 GameScene，区分已加载与已激活；Shutdown 不代替卸场。[Scene](../../Docs/Scene.md) |
| 功能入口未读数量 | `UI/RedDot/RedDot.cs`、`RedDotView`；`GameFrame.UI` | 业务保存真实状态并设置叶节点数量，父节点自动汇总，不能用红点计数充当背包 / 邮件数据。[RedDot](../../Docs/RedDot.md) |
| 文案翻译与语言字号 | `L10n/LanguageManager.cs`、`LocalizedText`、`LanguageResponsiveText`；`GameFrame.Localization` | 向已就绪语言服务补表，不重复 Init 或重置当前语言；读取现有语言配置和文本组件。[启动说明](../../Docs/Launch.md)、[字体](../../Docs/Fonts.md) |
| 横竖屏与安全区 | `Display/ScreenOrientationManager.cs`、`SafeAreaFitter`；`GameFrame.Display` | 方向由显式请求改变，安全区加在内容节点而非全屏背景；有临时方向切换时明确恢复责任。[UI 用法](../../Docs/USAGE.md)第 4、5 节 |
| 服务端请求、推送、账号会话 | `Network/NetworkRuntime.cs`、`NetworkClient`；`GameFrame.Network` | 通过 GameRuntime.Network 创建客户端；现有账号功能借用 GameLoginSession，接入生成的强类型路由 / DTO，不重复维护认证或实现自动重连。[客户端规范](../../Docs/ClientNetworkDevelopment.md)、[客户端用法](../../Docs/ClientNetworkUsage.md) |
| 本地存档、背包事务、持久数据 | `Sqlite/SqliteDatabase.cs`、`SqliteRuntime`；`GameFrame.Sqlite` | 独立程序集；业务定义 schema、账号归属与迁移策略，事务提交后才更新业务内存。数据库映射不得访问 Unity。[Sqlite](../../Docs/Sqlite.md)、[业务示例](../../Unity/Assets/Scripts/Runtime/Sqlite/Samples~/README.md) |
| 系统选图、图片预览与导出 | `Media/GameGallery.cs`、`GameImageReader`、`GameImageDirectory`；`GameFrame.Media` | 选择结果、纹理、导出文件有各自所有权；清 UI 引用后再释放，不需要 GameUI 初始化。[Gallery](../../Docs/Gallery.md) |
| 大型图库分页与缩略图缓存 | `Media/ImageLibraryIndex.cs`、`ImageThumbnailCache`、`MediaStorage/LibraryRepository.cs` | 从索引与缓存公开入口接入，不绕过它们操作内部仓库；避免全量读图库，保留纹理租约至停止显示。[Gallery](../../Docs/Gallery.md)、[图库设计](../../Docs/GalleryDesign.md) |
| 图片备份与后台上传 | `MediaBackup/ImageBackupService.cs`、`AutomaticImageBackup`；`GameFrame.Media.Backup` | 业务持有服务，备份 HTTP / 系统后台传输不等同于游戏 WSS / KCP 网络；先核对账号、协议和平台限制。[Gallery](../../Docs/Gallery.md)、[图库设计](../../Docs/GalleryDesign.md) |
| 框架启动与主动关闭 | `Lifecycle/GameRuntime.cs`、`GameRuntimeConfig`；`GameFrame` | 功能借用已启动的系统；需要增加能力时在已有启动配置接入，不能在页面里再次启动框架。[启动说明](../../Docs/Launch.md) |

Runtime 以外仍有现成入口：

- YooAsset 包、资源下载和一般资源句柄使用 `AOT/ResourceLoadManager.cs`，按 [启动说明](../../Docs/Launch.md) 核对地址、采集标签、依赖和释放责任。Runtime 不反向引用 AOT。
- 游戏配置通过 Hotfix 的 `TableConfigManager` 读取，涉及配置接入时使用 [luban-runtime-load](../luban-runtime-load/SKILL.md)；新增表再选对应 Luban skill，不额外实现配置加载器。
- UI 绑定生成与回填使用 [gameframe-ui-codegen](../gameframe-ui-codegen/SKILL.md)；Tween 动画使用 [unity-primetween](../unity-primetween/SKILL.md)，无需另建动画系统。

## 启动与业务归属

- 游戏逻辑、业务 ID、状态、配置和进入 / 离开流程放在 `Game.Hotfix`；Runtime 保持业务无关。沿用当前模块结构，需要独立业务流程时使用普通实例组合现有系统，不增加只做转发的全局管理器。
- 阅读当前 `AOT/Launch.cs`、`Runtime/Lifecycle/GameRuntimeConfig.cs` 和 `Hotfix/GameEntry.cs`；涉及导航时继续读取现有业务流程（如 `GameFlow`）。Launch 先准备语言、事件 Dispatcher、UI 与资源，再启动其余选中系统；GameEntry 组合热更业务，功能模块不重复初始化这些系统。
- 通过当前配置核对 Timer、Pool、Scene 是否启用，Input / Audio 配置是否绑定，NetworkOptions 是否提供。只有功能确需且现有配置缺少时才调整启动配置，不照抄通用 GameRuntime 示例覆盖项目分阶段启动。
- Fsm、独立 TimerScheduler、数据库、图库缓存与备份服务按实例明确创建和关闭责任；GameRuntime 不自动接管它们。业务借用 Package、默认池和共享会话时不自行关闭。
- 只读取目标系统需要的配置，不为接入功能扩展框架公共接口；确需改 Runtime 时先说明现有入口不足之处，并同步对应契约文档。

## 跨系统生命周期

给本次新增的资源明确记录“由谁持有、活到何时、怎么结束”，按实际情况写入代码或模块说明，不必为简单模块另建生命周期文档。

- 面板长期按钮监听登记在 OnCreate / LifetimeScope；每次打开的订阅、Timer、请求放入 OnOpen / OpenScope。缓存重开不会再次执行 OnCreate，防止重复注册。
- Pause / Resume 不结束 OpenScope。功能隐藏时需停止输入、音效或更新，就在对应暂停 / 恢复钩子中明确处理，不能假定 Push 另一个窗口已取消旧请求。
- 异步操作传所属周期的 Token；await 后核对结果仍属于当前打开周期或业务会话，防止旧请求覆盖新页面。不支持取消的操作（如现有 GameScene 加载）要等待收尾并阻止失效结果继续导航，不虚构 CancellationToken 重载。
- 非面板订阅使用 Owner 或 EventHandle，在对应退出点退订；与 Unity 的 EventSystem 重名时使用 `GameFrame.Event.EventSystem` 别名。共享总线不能在单个模块退出时 ClearAll。
- Timer Owner 不随 Unity 对象销毁自动释放，先取消所属 Timer 再释放 Owner。对象池借出对象、音频句柄、图片租约和 YooAsset 句柄分别按各自契约归还 / 释放，不能用一个 Destroy 替代。
- 作用域 Register 只登记同步清理；数据库和服务的异步 CloseAsync / ShutdownAsync 应由业务退出流程显式 await，不登记为无人等待的 async 清理回调。
- 模块退出只关闭自己拥有的资源，不关闭全局 GameRuntime、SqliteRuntime、Package 或其它模块。主动关闭整个业务时先停止请求和流程、关闭业务持有的实例，再按已有宿主顺序关闭框架和资源包；不把单模块退出变成全局重置。
- 区分业务拒绝、主动取消和执行错误，按选中模块契约传播；只有能处理错误或需要清理时捕获异常，不用自动重试、默认值或假成功掩盖失败。

## 常见组合的判断

这些是选择示例，不是强制依赖清单：

- 本地小游戏：UI + 需要的 Audio / Input；对局复杂时加 Fsm，有计时规则时加 Timer。同场景返回主界面走 UI 栈；只有独立世界内容才选 Scene。
- 背包 / 商店：UI + 列表，数据由业务维护；共享数据变化可用 Event，入口未读才用 RedDot，需要持久事务才用 Sqlite，需要服务端裁决才用 Network。
- 房间 / 联机对局：Network + 业务状态与流程，按场景内容选择 Scene；定时器不能成为服务器状态最终裁决，网络系统已有请求超时不另叠通用 Timer 超时层。
- 设置：UI + 实际设置项对应的 Audio / Input / Localization / Display；小型设置沿用已有保存方式，不因存在 Sqlite 就建立数据库。
- 图库：UI + Media；大型列表使用索引分页、缩略图缓存与 LoopScroll，明确 UI 与图片租约的释放顺序。确需备份才接 MediaBackup。

## 验证与交付

- 使用 [gameframe-validate](../gameframe-validate/SKILL.md) 按实际变更选择验证；纯 skill / 文档变更只检查结构和链接，不运行整套 Unity 测试。涉及 UI 生成或原生库时再读取对应专用 skill。
- 核验选中系统的配置、注册地址、资源采集与编译；按影响验证进入、使用、退出、缓存重开、在途关闭及相邻错误路径。已有测试可覆盖时不重复新增。
- Unity 验证先检查 Editor 状态，结束恢复场景与设置；临时截图只放系统临时目录并清理。网络服务、原生插件或设备环境缺失时记录未验证范围，不能用源码检查代替实跑。
- 最终简要说明功能位置、选用哪些系统及原因、关键资源归属、实际验证结果和未验证部分。
