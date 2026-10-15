# Launch 启动入口

`Assets/Scenes/Launch.unity` 是 Build Settings 中的首场景。
根节点 `Launch` 挂载 `Game.AOT.Launch`，启动后保留到进程退出。

启动顺序：初始化语言服务与事件派发器 → 初始化 UI → 绑定主相机 Stack → 初始化 YooAsset → 获取版本与清单 →
GameUI.SetPackage → 下载 boot 资源 → 启动其余框架系统 →
加载 AOT 元数据与 Hotfix DLL → 实例化 GameEntry prefab → 读取热更配置并补入翻译 → 启动热更业务。
Launch 负责框架初始化、选择相机和准备资源。GameEntry 在 Awake 中初始化 Hotfix 配置，
并调用 `GameUIRegistration.RegisterAll()` 统一注册业务面板，创建 Hotfix 的 `GameLoginSession` 并注入 `GameFlow`，完成后 IsReady 为 true。
Start 调用 `GameFlow.StartAsync()` 启动业务流程；流程打开登录面板，登录成功后通过 GameScene 以 Single 模式加载并激活 Main，再显示 `MainPanel`。主界面提供五子棋、木鱼和设置入口。
GameEntry 只组合、调用和释放业务对象；GameFlow 决定登录与主界面的导航，GameLoginSession 持有用户信息、凭据和连接。
Launch 启动 GameRuntime 时 EnableUI 为 false，语言服务也不重复初始化，并通过 NetworkOptions 启用网络宿主。
任一步失败直接抛出并终止后续启动，不重试、不回退旧资源、不自动回滚。

`Game.AOT` 程序集使用 `Game.AOT` 命名空间，引用框架、UniTask、YooAsset、HybridCLR 和 Luban.Runtime，不引用 Hotfix。
`Game.Hotfix` 程序集使用 `Game.Hotfix` 命名空间。`Game.Hotfix.GameEntry` 直接继承 MonoBehaviour，挂在
`Assets/Prefab/LoadPrefab/GameEntry.prefab` 根节点，由 Unity 调用生命周期方法。
Launch 没有单例或对外接口，也不调用 Hotfix 入口。DLL 必须在加载 prefab 前装载。

`TableConfigManager.Init` 使用 `new Tables(resources.LoadConfigByte)` 同类的加载回调，
表名由生成代码维护。生成配置通过 Luban 的 topModule 统一到 `Game.Hotfix.TableConfig`。
业务通过 `Game.Hotfix.TableConfig.TableConfigManager.Tables` 访问配置。
`Unity/Config/gen.sh/gen.bat` 与 Unity ExcelExporter 菜单统一通过 `DotNet/ConfigGenerator` 分别生成 client（c）和 server（s）配置；IDE 可选择 Generate Server、Generate All、Check。Unity 保持现有代码/数据输出与 YooAsset 加载链，服务端独立输出到 `DotNet/Game.Config`，见 [服务端配置接入](DotNetUsage.md)。
语言服务在 Launch 中先用空表初始化，读取已保存的语言设置。
GameEntry 将热更语言表和 SettingsLocalization 通过 LanguageManager.AddTable 补入，保留当前语言并刷新已注册的文本；AudioPreferences.ApplySaved 将本地音量设置应用到已初始化的音频系统。设置业务见 [Settings.md](Settings.md)。

## Hotfix 业务流程

`GameFlow` 是 `Game.Hotfix` 的普通实例，由 GameEntry 创建、启动和停止，不是静态入口或 MonoBehaviour。
GameEntry 拥有账号会话；流程和面板只借用它，不释放连接。LoginPanel 继续通过 `LoginPanelArgs.LoggedIn` 回调请求进入主界面，不查询 GameEntry。

- `StartAsync()` 订阅认证失效并打开登录面板；同一实例只能启动一次，停止后不能重启。
- 流程协调 Main 加载、相机 Stack 和面板打开；完成后仍核对当前 GameUser 与运行状态，旧账号或已停止流程不能继续导航。
- `StopAsync()` 先禁用导航、解绑认证事件并关闭场景组 UI，再等待正在执行的主场景导航；重复停止等待同一任务，不释放借用的会话。
- GameEntry 的 `StopAsync()` 先等待流程停止，即使流程清理失败也继续释放会话与配置，最后传播首个错误。
- 场景加载仍由 GameScene 执行，面板仍由 GameUI 管理；流程不另建场景加载器、UI 注册表或自动重试队列。

后续进入房间或独立战斗场景时，在 Hotfix 增加明确的业务进入/离开流程，统一处理切换准入、业务资源清理、场景加载和进入后的初始化。
流程复杂后再按房间、对局拆分；不要求一个业务阶段对应一个 Unity 场景。当前五子棋和木鱼仍只通过 UI 栈打开、退出。
未来复用已加载的多场景时必须区分已加载与当前活动场景，显式选择激活或加载，不能仅沿用当前 Main 的 `IsLoaded` 判断。

2026-10-14 验证：Unity 6000.3.19f1/macOS Editor 编译通过；资源启动、账号会话、主界面、GameRuntime、场景操作与 UI 根节点 PlayMode 66/66，零跳过，使用独立临时 WSS/PostgreSQL。
新增覆盖流程停止只释放导航而保留借用的账号会话，以及旧 Main 加载期间重新登录新账号；既有断线、切场停止、显式重试与游戏退出缓存行为通过。
实际播放 Launch 时 GameEntry、GameFlow、登录面板和 191 条语言配置就绪，只有一个 UI 根节点和一个 UI 相机 Stack 引用，停止播放后无 Console 错误。
首轮测试在用例开始前受 Test Runner 域重载异常中断，重新启动后整组通过；已恢复场景、EditorSettings、证书偏好、测试环境变量和字体纹理，并清理临时服务、数据库与测试场景。
本次未执行 Player 脚本编译、正式热更 DLL/资源包构建、IL2CPP 或真机验证。

2026-10-14 设置与账号记忆接入补验：实际 Launch 播放时 GameEntry.IsReady、LoginPanel 和设置翻译就绪，UI 根节点与相机 Stack 引用各一个，Console 无新增错误。相关 PlayMode 38/38、StandaloneOSX Release Player 脚本编译通过；移动端原生桥静态编译检查通过，完整 IL2CPP/真机尚未验证。测试后恢复场景、播放状态、编辑器偏好和动态字体，清理临时服务、数据库、证书及预览；账号持久化契约见客户端网络使用说明。

## 启动配置与资源服务

Launch 引用 `Assets/Resources/GlobalConfig.asset`，该 ScriptableObject 随首场景进入安装包，
不依赖待更新的资源包。可通过 `GameFrame/Global Config` 创建不同配置资产并绑定到 Launch。

- `PlayMode` 明确选择 EditorSimulateMode、OfflinePlayMode、HostPlayMode 或 WebPlayMode。
  模式与平台不兼容时直接报错，不自动切换模式。
- `PackageName`、`ServerUrl`、`BootTag`、`GameEntryLocation` 控制启动资源。
- `HotUpdateAssemblies`、`AotMetadataAssemblies` 填写不含 `.dll` 的程序集名。
  热更 DLL 按列表顺序加载，被依赖的程序集在前；元数据始终在热更 DLL 前加载。

`ResourceLoadManager` 由 Launch 创建，通过 Instance 供 GameEntry 使用。
初始化包、更新清单与按标签下载分开执行，
不会在启动时下载整个包。配置、DLL、元数据与入口 prefab 都需采集为 `boot`；
当前 `Assets/Config/Excel`、`Assets/Config/Code` 与 `Assets/Prefab/LoadPrefab` 已带该标签。

- `LoadConfigByte` / `LoadBytes` / `LoadBytesAsync` 读取后释放 TextAsset 句柄。
- `LoadAssetAsync<T>` 返回 AssetHandle，成功后由调用方负责 Release；不能只保存资源对象。
- `LoadGameObjectAsync` 保留实例的句柄；调用 `ReleaseGameObjectAsync` 销毁实例后才释放。
- `ContainsLocation` 查询可选资源地址；必需资源加载失败仍抛出异常。
- `UnloadUnusedAssetsAsync` 回收未引用的内存资源，不清理磁盘缓存。
- 下载进度、总数量及字节数由服务提供，下载 UI 由 Launch/场景负责。

## 包内运行时日志

日志采集与界面分离。Launch.Awake 初始化 `GameFrame.Diagnostics.RuntimeLogCapture`，早于资源准备与 Hotfix 加载；Launch 清理时退订并释放其缓存引用。Launch 场景不挂日志界面 prefab。采集开关为 Launch 的 Log Mode，默认 Always（Release 也采集），可选 EditorAndDevelopment / Disabled。

- `RuntimeLogCapture` 仅负责 Unity 日志订阅与缓存生命周期；`RuntimeLogBuffer` 是线程安全的有界缓存，不依赖界面。首次打开前、关闭进缓存后、销毁面板后均持续采集，打开界面不会新建或清空缓存。停止采集后，已在执行的后台回调仍可安全写完原缓存。
- 缓存默认最多 1000 条、消息与堆栈共 2097152 个字符，单条 65536 个字符；超限移除旧条目，单条过长时标明截断。堆栈内容取决于 Unity 原有设置。
- GameEntry 在业务就绪后通过 `GameUI.Tips<RuntimeLogEntryPanel>()` 显示轻量 LOG 入口；点击后通过 `GameUI.Guide<RuntimeLogPanel, RuntimeLogBuffer>(RuntimeLogCapture.Buffer)` 加载日志面板，参数传入既有缓存。两者注册为 Persistent 组，切场关闭 Scene 组不影响它们；Guide 不暂停业务窗口。
- prefab 为 `Assets/Prefab/Debug/RuntimeLogEntryPanel.prefab` 与 `RuntimeDebugConsole.prefab`，经 YooAsset 的 Debug 组按文件名寻址并带 boot 标签。面板及入口使用既有 UI Canvas、相机、EventSystem；按钮和视图引用使用 UIFrame 生成绑定，位于 `Hotfix/Diagnostics`。面板关闭统一走 CloseSelf，不自行销毁或管理全局采集。
- `RuntimeDebugConsole` 仅展示传入的缓存；`RuntimeLogList` 复用既有 LoopVerticalScrollRect 与本地行模板。上半区显示日志，下半区显示选中条目的时间、级别、完整消息和可用堆栈；点击行同时复制该条。Copy all logs 复制全部缓存，不受搜索和级别筛选影响。Clear 是用户显式清空缓存的操作；关闭面板不清空。
- Follow 追踪最新日志，拖动或滚动列表会取消 Follow。刷新使用 unscaledTime；timeScale=0 仍可查看。面板打开期间的已选记录即使被缓存淘汰也保留到下次选择、清空或关闭。

采集无需资源系统就绪，但通过 GameUI 打开界面需要资源包、清单和面板资源可用；资源初始化失败时可能无法打开查看。此功能不能替代原生崩溃、主线程卡死或 UI 输入故障诊断。

2026-10-15 拆分后验证：Editor 编译无错误；EditMode 日志缓存 4/4、PlayMode 日志面板与循环列表/UI 根节点 17/17 通过。测试通过真实 GameUI/YooAsset 模拟加载覆盖首次打开前后台线程采集、缓存关闭后继续采集、销毁后重开保留历史、采集关闭与重启，以及筛选、复制、清空和行复用。实际播放 Launch，GameEntry 就绪且只显示入口，首次点击前日志面板未创建；入口与日志行的 UI 射线点击、完整详情与剪切板、关闭后继续采集均通过，运行期间无错误或布局警告，只有一份 UI Canvas 和 EventSystem。StandaloneOSX 非 Development Player 脚本编译通过，并重新编译拷贝热更 DLL。本次拆分未重新构建原生 IL2CPP 安装包或实跑 Android/iOS，不能沿用拆分前独立 Overlay prefab 的原生包证据。

## 编辑器

默认选择 `EditorSimulateMode`，使用 `DefaultPackage` 采集设置模拟资源包，
并使用编辑器已经编译的 Hotfix 程序集。直接播放 Launch 场景即可验证启动。
资源包按文件名（去掉最后一个扩展名）寻址，当前配置采集目录是 `Assets/Config/Excel` 与
`Assets/Config/Code`。Excel 按目录打包；Code 使用 `PackSeparately`，每个 DLL 单独打包，
更新 Hotfix 时不使未变更的 AOT DLL Bundle 一起更新。新增其他配置目录时需添加对应采集器。

语言服务、事件派发器和 UI 在资源初始化前启动；Timer、Pool、Scene 在 boot 下载完成后由 Launch 启动。
Input / Audio 在 Launch 的 Inspector 绑定配置后才启用。GameEntry 在 Awake 中自动初始化热更配置和业务。
启动完成后在 Launch 场景打开 `LoginPanel`，首次输入账号和密码后点击登录才连接 WSS；不存在的账号由服务端创建并登录。启用 Remember Login 时，再次打开会读取 SQLite 账号元数据和安全存储凭据，有效时点击确定执行 ResumeLogin，失效则要求密码；不自动连接。
成功后通过 `GameScene.LoadAsync("Main", LoadSceneMode.Single)` 进入 `Assets/Scenes/MainScene/Main.unity`，
重新将持久 UI 相机绑定到 Main 的主相机，打开 `MainPanel` 成功后关闭登录面板。
主界面位于 `Assets/Prefab/HitfixPrefab/MainPanel.prefab`，通过注入的导航回调打开 `GomokuLobbyPanel` 或通过窗口栈打开 `WoodenFishMainPanel`；五子棋入口再选择本地棋盘或创建 / 加入联机房间。本地游戏“退出”关闭当前面板并恢复上一个窗口，保留缓存中的棋局和功德计数；联机退出先等待服务器确认。
Launch 和其子节点 GameEntry 跨切场保留，账号会话与框架不重新初始化；内置 Launch 场景由 Single 加载卸载。
连接失效后在当前 Main 场景打开登录面板，重新登录复用已加载的 Main，不重新加载 Launch。
场景加载失败保留已认证会话与登录面板，玩家再次点击确定只重新执行进入主场景，不重复登录或自动重试。
Main 场景采集组标记为 `boot`，在启动资源准备阶段下载；Main 不加入 Build Settings，也不把 Launch 采集进资源包。
GameEntry prefab 的 `Login Endpoint` 是可信环境端点，当前开发值为 `wss://localhost:7443/network`，生产资源需替换为实际服务地址。
本地开发证书可在 `Tools/GameFrame/Network/Set Local Development Certificate` 配置；Editor 在 BeforeSceneLoad 读取本机配置并启用信任，先于 Launch/GameEntry 初始化，停止播放后恢复。工具不进入 Player，也不为入口增加 Editor 依赖，详见 [客户端网络使用说明](ClientNetworkUsage.md)。
`DefaultPackage` 的 `Gomoku` 组采集 `Assets/Prefab/Gomoku`、`Assets/ArtRes/Gomoku/Image` 和 `Assets/ArtRes/Gomoku/Audio`，
标签为 `gomoku;boot`。字体通过 prefab 依赖采集，本地对局和配置见 [Gomoku.md](Gomoku.md)。
断线或主动停止业务时关闭 `UIGroup.Scene`，包括主界面、游戏、登录以及正在加载的面板；断线随后重新打开登录面板。
`WoodenFish` 组采集 prefab 与 Audio 目录，标签为 `fish;boot`，登录后可以直接从主界面打开。
Launch 绑定 `Assets/Config/Audio/GameAudioConfig.asset`，棋子落盘声和木鱼音效在框架初始化时常驻预加载。
玩法与可调参数见 [WoodenFish.md](WoodenFish.md)。

最早显示的启动界面应直接放在 Launch 场景或引用安装包内置 prefab，不依赖待更新资源或 Hotfix。
UI 根节点创建不依赖 YooAsset，但通过 GameUI 加载面板仍需要 Package 就绪。
热更语言表在 GameEntry 加载后才可用，最早的启动界面翻译需要使用场景或安装包内置数据。
Launch 在清单加载成功后调用 GameUI.SetPackage；UIFrameRoot 只创建 UI 相机，Stack 由 Launch 显式配置。

## 正式包

- 联机运行时，将 `Server Url` 配置成当前平台、当前资源包的完整 HTTP(S) 目录地址，
  该目录需提供 YooAsset 构建输出的版本文件、清单和 Bundle。默认不使用下载重试。
- 离线运行时选择 OfflinePlayMode，需先构建并复制资源包到 StreamingAssets。
- WebGL 选择 WebPlayMode 并配置 HTTP(S) 资源服务器目录。
- `Game.Hotfix` 已登记在 HybridCLR 热更新程序集列表中。
  使用 HybridCLR 构建工具生成对应平台的 Hotfix DLL，再用下述 DLL 拷贝工具复制成
  `Assets/Config/Code/Game.Hotfix.dll.bytes`，并随资源包发布；默认地址为 `Game.Hotfix.dll`。
- 正式包统一使用 IL2CPP，按 HybridCLR 生成结果准备裁剪后的 AOT 补充元数据 DLL，
  也作为 `.dll.bytes` 资源发布，在 `AotMetadataAssemblies` 填写不含 `.dll` 的程序集名。
  元数据在 Hotfix DLL 前加载，生成、构建和发布顺序遵循 HybridCLR 工具要求。
- 正式包必须选择平台支持的模式，不能使用 EditorSimulateMode。

资源服务器需要单独配置；编辑器模拟启动不依赖正式平台 DLL。

### DLL 拷贝工具

`Tools/GameFrame/Loader` 提供三个菜单：

- `CopyAOTDlls`：拷贝已有的 AOT 补充元数据。
- `CompileAndCopyHotUpdateDlls`：直接调用 HybridCLR 的 `CompileDllCommand.CompileDllActiveBuildTargetRelease()`，
  以当前平台的 Release 配置编译，再拷贝 Hotfix DLL；输出目录使用 HybridCLR 设置。
- `CompileAndCopyHotUpdateAndAOTDlls`：同样调用 HybridCLR 的当前平台 Release 编译，
  再拷贝 Hotfix DLL 与已有的 AOT 补充元数据。

工具按路径读取 `Assets/Resources/GlobalConfig.asset` 的 `AotMetadataAssemblies` 与
`HotUpdateAssemblies`，程序集名不含 `.dll`；移动配置或使用另一份配置时需调整
`AssemblyTool.GlobalConfigPath`，确保与 Launch 绑定的配置一致。

源目录通过 HybridCLR Settings 获取，按当前 `activeBuildTarget` 选择平台：

- 热更 DLL：默认 `HybridCLRData/HotUpdateDlls/<平台>`。
- 裁剪后的 AOT DLL：默认 `HybridCLRData/AssembliesPostIl2CppStrip/<平台>`。
- 两者均输出为 `Assets/Config/Code/<程序集名>.dll.bytes`，资源地址为 `<程序集名>.dll`，
  由 `Assets/Config/Code` 采集规则标记为 `boot`，每个 DLL 单独打包。

日常更新 Hotfix 可直接执行 `CompileAndCopyHotUpdateDlls`，然后构建 YooAsset 资源包。
需要同时准备 AOT 元数据时，按 HybridCLR 流程生成当前平台的热更 DLL 与裁剪后的 AOT DLL，
结合项目实际需要填写 `AotMetadataAssemblies`，再执行 `CompileAndCopyHotUpdateAndAOTDlls`，最后构建资源包。
`CopyAOTDlls` 不触发编译；两个编译并拷贝菜单只进行 Release Player 脚本编译，不生成裁剪后的 AOT DLL。

工具在写入前检查全部源 DLL，缺少文件或热更程序集未登记到 HybridCLR 时抛出错误。
AOT 列表为空时不拷贝 AOT DLL，并在完成日志中说明。
覆盖已有 `.bytes` 时保留 `.meta` 和 GUID，不清空目录；移除程序集后需手动删除对应的旧资源。
切换平台后应重新生成、拷贝 DLL，再构建资源包；输出目录不按平台分开。

### AOT 元数据的选择与实跑

`AOTGenericReferences.PatchedAOTAssemblyList` 是泛型分析得到的关联程序集清单，不能直接当作每个程序集都必须补充元数据的证明。
已有具体 AOT 实现或可复用的泛型共享代码可能已能执行；缺少相应实现、需要 HybridCLR 解释泛型方法时，才需要对应程序集的补充元数据。
当前 GlobalConfig 保留原有 `mscorlib`，并依据实际 Release Player 故障补入 `MemoryPack.Core` 与 `UniTask`：

- 只有 mscorlib 时，正式 WSS 登录在 `MemoryPackSerializer.Deserialize<LoginResponse>(ReadOnlySpan<byte>, options)` 失败，报告 MemoryPack.Core 的 AOT 泛型方法未实例化。
- 补入 MemoryPack.Core 后，房间加入、准备与开局可以执行；落子音效调用 `UniTaskExtensions.Forget<AudioPlayResult>` 时仍失败，报告 UniTask 的 AOT 泛型方法未实例化。失败发生在刷新回调中，可使推送派发断开或当前操作进入待查询状态。
- 补入 UniTask 后，最终未带临时诊断代码的 Release 包完成 Launch、YooAsset、DLL/元数据加载、WSS 登录、Main、加入房间、准备、9 手联机对局、五连结束和主动离房，未出现托管异常。配置与具体限制见 [网络验证证据](ClientNetworkUsage.md#8-验证与现有证据)。

上述三项是当前已验证配置，不代表已证明所有业务的最小必需集合。其他生成出的程序集暂不批量加入；新增泛型调用、业务或发布目标后按实际路径重新验证。
2026-10-15 进一步使用同一三个元数据文件完成包后新增协议验证：先构建不含新类型的 ARM64 Release/Minimal 安装包，再仅发布新 Hotfix DLL 与 prefab 的 YooAsset 更新。旧包经正式 Host 启动下载更新，全新嵌套 DTO 的双向序列化、三轮 RPC/Push 和网络关闭通过，整包文件及元数据哈希均未改变。此结果验证本次新类型组合，无需再批量加入其他生成的元数据；复验顺序与限制见 [包后新增协议验收](ClientNetworkUsage.md#network-hotupdate-dto)。
三个 `.dll.bytes` 均由拷贝工具从本次 StandaloneOSX Release 最终构建的裁剪程序集生成，不能用 Development 测试入口一次加载全部关联程序集的结果代替正式配置验收。
本次资源包与 Player 在系统临时目录构建并清理，仓库已有 StreamingAssets 未更新；发布离线包仍需按上述步骤重新构建、复制资源并验证。

### 生成产物与 Git

以下可重新生成的目录及对应生成资产的 `.meta` 由 `Unity/.gitignore` 忽略：

- `HybridCLRData/`：HybridCLR 安装的本地工具链、仓库副本、DLL 与构建缓存。
- `Assets/HybridCLRGenerate/`：生成的 `link.xml` 和 AOT 泛型引用代码。
- `Bundles/`：YooAsset AB 包构建产物。

`Assets/Config/Code/` 中拷贝后的 DLL、`Assets/StreamingAssets/` 中的内置资源及其 `.meta`
保留在版本控制中。`ProjectSettings/HybridCLRSettings.asset`、YooAsset 设置与采集配置、
程序集定义及源码仍需提交。
新检出项目后，使用 HybridCLR Installer 安装工具链；需要更新 DLL 或资源包时，执行当前平台的
HybridCLR 生成流程、拷贝 DLL、构建 YooAsset 资源包，离线包还需将构建产物复制到 StreamingAssets。
忽略规则不会自动取消已跟踪文件的跟踪；取消跟踪仅修改 Git 索引，不删除本地构建产物。

## 退出

存档、配置写入等业务操作完成后，直接退出：

```csharp
UnityEngine.Application.Quit();
```

正式包不拦截退出，也不等待初始化或异步关闭，不逐项释放资源包；进程结束后由系统回收资源。
入口按每个进程启动一次设计，不通过销毁 GameEntry 来重启框架。

运行期间需要主动关闭时，先 `await entry.StopAsync()` 停止业务并等待登录请求、连接与凭据释放，再释放其他业务资源，
调用 `GameUI.Shutdown()`，再等待 `GameRuntime.ShutdownAsync()`；随后关闭语言服务、清空配置和事件，最后按需调用
`ResourceLoadManager.Instance.ShutdownAsync()`。这些接口不属于进程退出流程。

Launch.OnApplicationQuit / OnDestroy 负责关闭 UI、发起其余框架系统关闭、关闭语言服务并清空事件；不等待异步完成。
GameEntry.OnApplicationQuit / OnDestroy 发起幂等 StopAsync，先停止 GameFlow 并等待其主场景导航，再清理会话及自己初始化的热更配置；生命周期回调不等待、不关闭框架。
Editor 与正式包共用这些简单清理，两次生命周期回调不会重复清理。
Unity 不保证 Launch 与 GameEntry 退出回调的顺序；业务 UI 导航要求 GameFlow 运行、GameRuntime 运行且 GameUI 已初始化，异步打开完成后再次核对。框架关闭触发的认证失效仍清空用户与凭据，但不再打开登录或主界面。
Editor 停止播放时，资源系统由 YooAsset 自己的 Editor 驱动关闭。
GameEntry 创建之前启动失败时，不执行额外的退出回滚；正式包退出由系统回收资源，
Editor 重新播放使用默认 Domain Reload 重置静态状态。


网络是 `GameRuntimeConfig.NetworkOptions` 控制的可选模块，框架默认 null；项目 Launch 显式启用，GameEntry 创建账号客户端，点击登录才连服。启用时驱动挂在既有持久根上；Hotfix 先停止业务并 await 客户端 Dispose，框架 Shutdown 再等待网络宿主关闭，最后释放 Timer/root 与资源包。网络业务初始化不增加 Launch 反射入口。API 与当前平台范围见 [客户端网络使用说明](ClientNetworkUsage.md)。


五子棋联机复用登录 WSS：GameEntry 创建 GomokuRoomSession 并注入 GameFlow，MainPanel 通过 MainPanelArgs 回调打开入口。停止时 GameFlow 关闭 Scene 组、等待主场景 / 五子棋入口打开，随后 GameEntry 等待房间任务与订阅释放，再释放账号。缓存面板不持有独立连接，普通关面板不等于已退出服务端房间。
