# 木鱼小游戏

播放 `Unity/Assets/Scenes/Launch.unity`。GameEntry 初始化成功后自动打开
`Assets/Prefab/WoodenFish/WoodenFishMain.prefab`。编辑器可使用 GlobalConfig 的
EditorSimulateMode；OfflinePlayMode 需要先构建含新增资源的内置资源包。

业务面板注册集中在 `Assets/Scripts/Hotfix/GameUIRegistration.cs`。
新增面板时在 `RegisterAll()` 中填写面板类型、YooAsset 地址、UIGroup 和缓存选项，
GameEntry 初始化时统一调用；打开面板的时机由各业务流程决定。

## 输入与计数

`FishImg` 的 Image 开启 Raycast Target，挂载业务组件 `WoodenFishHitTarget`，通过
`IPointerDownHandler` 在鼠标左键或触摸按下时敲击，不需要 Button。
每次按下立即累计一次功德、触发音效并产生独立的“功德+1”，没有点击冷却或动画锁。
右键、禁用的点击组件，以及暂停或关闭的面板不计数。

`MeritCount` 是当前面板实例的内存计数，`ClickNum` 显示累计值。关闭后缓存重开保留计数；
销毁面板或结束运行后不保存。`DesTip` 对应生成字段 `num`，是隐藏的飘字模板，
`clickNum` 是累计值文字。生成字段通过 UI 编辑器维护，不手改 `.Gen.cs`。

## 表现参数

动画使用 PrimeTween 1.4.11，业务实现位于 `WoodenFishMainPanel.cs`。
快速连点时停止上一轮木鱼和棒子动画，恢复基准姿态并重新敲击；计数、音效和飘字继续独立触发。
木棍旋转和木鱼缩放由同一条 Sequence 并行敲下，再并行回弹。
`StickPivot` 是棒子的旋转支点，与 `FishImg` 同级，避免木鱼缩放使木棍变形或移动。
锚点与 `FishImg` 一致，为左上角，位置为 `(889, -793)`，
待机旋转为 72°。木棍待机时悬在木鱼上方，敲下时旋转到 100°，棍头接触木鱼顶部中央。
`Crabstick` 的枢轴靠近手柄端。

| Inspector 参数 | 初始值 | 用途 |
| --- | --- | --- |
| Strike Angle | 28° | 敲击旋转角度 |
| Hit Scale | (1.06, 0.94, 1) | 木鱼受击压缩 |
| Strike Settings | 0.055 秒 / OutQuad | 敲下 |
| Return Settings | 0.15 秒 / OutBack | 回弹 |
| Float Height | 110 | 飘字上升距离 |
| Float Start Offset | 20 | 从木鱼顶部上方开始 |
| Horizontal Spread | 50 | 左右随机偏移范围 |
| Float Settings | 0.7 秒 / OutQuad | 上升 |
| Fade Settings | 0.35 秒 / Linear | 结束前淡出 |

动画使用不受 Time.timeScale 影响的时间。飘字复用现有 `ManagedObjectPool`，预热 8 个，
最多保留 64 个空闲对象；同时显示的数量不由该值限制。暂停、关闭、销毁时停止本面板的动画，
回收活动飘字并恢复姿态；销毁时释放对象池和点击订阅。

## 音效与采集

`GameAudioIds.WoodenFishHit` 对应 `sfx.woodenfish.hit`，地址为 `WoodenFishHit`。
`Assets/Config/Audio/GameAudioConfig.asset` 配置 Sfx 总线、Resident 预加载、
无冷却、最多 16 条同音效并行，超限停止最早一条再播放；全局声道上限 32。
当前 WAV 是合成的短木质敲击音，可替换同路径资源并保留 `.meta`。

YooAsset `DefaultPackage/WoodenFish` 采集 prefab 目录和音效，AddressByFileName、
PackSeparately、CollectAll、boot 标签。图片与字体作为 prefab 依赖，无需额外配置地址。
AudioRuntimeConfig 和 AudioMixer 由 Launch 场景引用，随场景进入安装包。

PrimeTween skill 从 unitygame 迁入 `skills/unity-primetween`，版本和路径已适配本项目。
