# Launch 启动入口

`Assets/Scenes/Launch.unity` 是 Build Settings 中的首场景。
根节点 `Launch` 挂载 `GameFrame.AOT.Launch`，启动后保留到进程退出。

启动顺序：读取 GlobalConfig → 初始化 YooAsset → 获取版本与清单 → 下载 boot 资源 →
加载 AOT 元数据与 Hotfix DLL → 实例化 GameEntry prefab → 读取 Luban 配置 → 启动 GameRuntime。
Launch 只负责资源准备与实例化，GameEntry 在 Awake 中自行初始化，完成后 IsReady 为 true。
任一步失败直接抛出并终止后续启动，不重试、不回退旧资源、不自动回滚。

`GameFrame.AOT` 引用框架、UniTask、YooAsset、HybridCLR 和 Luban.Runtime，不引用 Hotfix。
`GameFrame.Hotfix.GameEntry` 直接继承 MonoBehaviour，挂在
`Assets/Prefab/LoadPrefab/GameEntry.prefab` 根节点，由 Unity 调用生命周期方法。
Launch 没有单例或对外接口，也不调用 Hotfix 入口。DLL 必须在加载 prefab 前装载。

`TableConfigManager.Init` 使用 `new Tables(resources.LoadConfigByte)` 同类的加载回调，
表名由生成代码维护。业务通过 `GameFrame.Hotfix.TableConfig.TableConfigManager.Tables` 访问配置。
语言表由 GameEntry 转成框架使用的配置，再启动 GameRuntime。

## 启动配置与资源服务

Launch 引用 `Assets/Resource/GlobalConfig.asset`，该 ScriptableObject 随首场景进入安装包，
不依赖待更新的资源包。可通过 `GameFrame/Global Config` 创建不同配置资产并绑定到 Launch。

- `PlayMode` 明确选择 EditorSimulateMode、OfflinePlayMode、HostPlayMode 或 WebPlayMode。
  模式与平台不兼容时直接报错，不自动切换模式。
- `PackageName`、`ServerUrl`、`BootTag`、`GameEntryLocation` 控制启动资源。
- `HotUpdateAssemblies`、`AotMetadataAssemblies` 填写不含 `.dll` 的程序集名。
  热更 DLL 按列表顺序加载，被依赖的程序集在前；元数据始终在热更 DLL 前加载。

`ResourceLoadManager` 由 Launch 创建，通过 Instance 供 GameEntry 使用。
初始化包、更新清单与按标签下载分开执行，
不会在启动时下载整个包。配置、DLL、元数据与入口 prefab 都需采集为 `boot`；
当前 `Assets/Config` 与 `Assets/Prefab/LoadPrefab` 已带该标签。

- `LoadConfigByte` / `LoadBytes` / `LoadBytesAsync` 读取后释放 TextAsset 句柄。
- `LoadAssetAsync<T>` 返回 AssetHandle，成功后由调用方负责 Release；不能只保存资源对象。
- `LoadGameObjectAsync` 保留实例的句柄；调用 `ReleaseGameObjectAsync` 销毁实例后才释放。
- `ContainsLocation` 查询可选资源地址；必需资源加载失败仍抛出异常。
- `UnloadUnusedAssetsAsync` 回收未引用的内存资源，不清理磁盘缓存。
- 下载进度、总数量及字节数由服务提供，下载 UI 由 Launch/场景负责。

## 编辑器

默认选择 `EditorSimulateMode`，使用 `DefaultPackage` 采集设置模拟资源包，
并使用编辑器已经编译的 Hotfix 程序集。直接播放 Launch 场景即可验证启动。
资源包按文件名（去掉最后一个扩展名）寻址，当前配置采集目录是 `Assets/Config`。

Timer、Pool、Scene、Language 和 UI 会启动；Input / Audio 在 GameEntry prefab 的 Inspector
绑定配置后才启用。GameEntry 在 Awake 中自动启动。
场景尚未配置业务首界面或内容场景，启动完成后停留在 Launch，后续导航由业务接入。

## 正式包

- 联机运行时，将 `Server Url` 配置成当前平台、当前资源包的完整 HTTP(S) 目录地址，
  该目录需提供 YooAsset 构建输出的版本文件、清单和 Bundle。默认不使用下载重试。
- 离线运行时选择 OfflinePlayMode，需先构建并复制资源包到 StreamingAssets。
- WebGL 选择 WebPlayMode 并配置 HTTP(S) 资源服务器目录。
- `GameFrame.Hotfix` 已登记在 HybridCLR 热更新程序集列表中。
  使用 HybridCLR 构建工具生成对应平台的 Hotfix DLL，将其复制成
  `Assets/Config/Code/GameFrame.Hotfix.dll.bytes`，并随资源包发布；默认地址为 `GameFrame.Hotfix.dll`。
- 正式包统一使用 IL2CPP，按 HybridCLR 生成结果准备裁剪后的 AOT 补充元数据 DLL，
  也作为 `.dll.bytes` 资源发布，在 `AotMetadataAssemblies` 填写不含 `.dll` 的程序集名。
  元数据在 Hotfix DLL 前加载，生成、构建和发布顺序遵循 HybridCLR 工具要求。
- 正式包必须选择平台支持的模式，不能使用 EditorSimulateMode。

目前没有配置资源服务器或生成正式平台 DLL；编辑器模拟启动不依赖它们。

## 退出

存档、配置写入等业务操作完成后，直接退出：

```csharp
UnityEngine.Application.Quit();
```

正式包不拦截退出，也不等待初始化或异步关闭，不逐项释放资源包；进程结束后由系统回收资源。
入口按每个进程启动一次设计，不通过销毁 GameEntry 来重启框架。

运行期间需要主动关闭时，先停止业务任务、释放业务持有的资源，再显式等待
`GameRuntime.ShutdownAsync()`；随后清空配置和事件，最后按需调用
`ResourceLoadManager.Instance.ShutdownAsync()`。这些接口不属于进程退出流程。

Editor 与正式包共用 GameEntry.OnApplicationQuit / OnDestroy 的简单清理：
发起框架关闭、清空配置和事件，不等待异步完成。两次生命周期回调不会重复清理。
Editor 停止播放时，资源系统由 YooAsset 自己的 Editor 驱动关闭。
GameEntry 创建之前启动失败时，不执行额外的退出回滚；正式包退出由系统回收资源，
Editor 重新播放使用默认 Domain Reload 重置静态状态。
