# Launch 启动入口

`Assets/Scenes/Launch.unity` 是 Build Settings 中的首场景。
根节点 `Launch` 挂载 `Game.AOT.Launch`，启动后保留到进程退出。

启动顺序：初始化语言服务与事件派发器 → 初始化 UI → 绑定主相机 Stack → 初始化 YooAsset → 获取版本与清单 →
GameUI.SetPackage → 下载 boot 资源 → 启动其余框架系统 →
加载 AOT 元数据与 Hotfix DLL → 实例化 GameEntry prefab → 读取热更配置并补入翻译 → 启动热更业务。
Launch 负责框架初始化、选择相机和准备资源。GameEntry 在 Awake 中初始化 Hotfix 配置，
并调用 `GameUIRegistration.RegisterAll()` 统一注册业务面板，完成后 IsReady 为 true，
再在 Start 中打开木鱼主面板。Launch 启动 GameRuntime 时 EnableUI 为 false，语言服务也不重复初始化。
任一步失败直接抛出并终止后续启动，不重试、不回退旧资源、不自动回滚。

`Game.AOT` 程序集使用 `Game.AOT` 命名空间，引用框架、UniTask、YooAsset、HybridCLR 和 Luban.Runtime，不引用 Hotfix。
`Game.Hotfix` 程序集使用 `Game.Hotfix` 命名空间。`Game.Hotfix.GameEntry` 直接继承 MonoBehaviour，挂在
`Assets/Prefab/LoadPrefab/GameEntry.prefab` 根节点，由 Unity 调用生命周期方法。
Launch 没有单例或对外接口，也不调用 Hotfix 入口。DLL 必须在加载 prefab 前装载。

`TableConfigManager.Init` 使用 `new Tables(resources.LoadConfigByte)` 同类的加载回调，
表名由生成代码维护。生成配置通过 Luban 的 topModule 统一到 `Game.Hotfix.TableConfig`。
业务通过 `Game.Hotfix.TableConfig.TableConfigManager.Tables` 访问配置。
语言服务在 Launch 中先用空表初始化，读取已保存的语言设置。
GameEntry 将热更语言表转换后通过 LanguageManager.AddTable 补入，保留当前语言并刷新已注册的文本。

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

## 编辑器

默认选择 `EditorSimulateMode`，使用 `DefaultPackage` 采集设置模拟资源包，
并使用编辑器已经编译的 Hotfix 程序集。直接播放 Launch 场景即可验证启动。
资源包按文件名（去掉最后一个扩展名）寻址，当前配置采集目录是 `Assets/Config/Excel` 与
`Assets/Config/Code`。Excel 按目录打包；Code 使用 `PackSeparately`，每个 DLL 单独打包，
更新 Hotfix 时不使未变更的 AOT DLL Bundle 一起更新。新增其他配置目录时需添加对应采集器。

语言服务、事件派发器和 UI 在资源初始化前启动；Timer、Pool、Scene 在 boot 下载完成后由 Launch 启动。
Input / Audio 在 Launch 的 Inspector 绑定配置后才启用。GameEntry 在 Awake 中自动初始化热更配置和业务。
启动完成后在 Launch 场景打开 `WoodenFishMainPanel`，无需额外切换内容场景。
`DefaultPackage` 的 `WoodenFish` 组按文件名采集 `Assets/Prefab/WoodenFish` 和
`Assets/ArtRes/WoodenFish/WoodenFishHit.wav`，标签为 `boot`。图片与字体通过 prefab 依赖采集。
Launch 绑定 `Assets/Config/Audio/WoodenFishAudioConfig.asset`，木鱼音效在框架初始化时常驻预加载。
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

运行期间需要主动关闭时，先停止业务任务、释放业务持有的资源，
调用 `GameUI.Shutdown()`，再等待 `GameRuntime.ShutdownAsync()`；随后关闭语言服务、清空配置和事件，最后按需调用
`ResourceLoadManager.Instance.ShutdownAsync()`。这些接口不属于进程退出流程。

Launch.OnApplicationQuit / OnDestroy 负责关闭 UI、发起其余框架系统关闭、关闭语言服务并清空事件；不等待异步完成。
GameEntry.OnApplicationQuit / OnDestroy 仅清空自己初始化的热更配置，不关闭框架。
Editor 与正式包共用这些简单清理，两次生命周期回调不会重复清理。
Editor 停止播放时，资源系统由 YooAsset 自己的 Editor 驱动关闭。
GameEntry 创建之前启动失败时，不执行额外的退出回滚；正式包退出由系统回收资源，
Editor 重新播放使用默认 Domain Reload 重置静态状态。
