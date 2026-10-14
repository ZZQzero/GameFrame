# 设置界面

主界面点击“设置”打开 `Assets/Prefab/HitfixPrefab/SettingsPanel.prefab`，注册地址为 `SettingsPanel`，属于 Scene 组并使用窗口缓存。返回关闭设置并恢复主界面；认证失效时由 GameFlow 关闭整个 Scene 组。

业务代码位于 `Assets/Scripts/Hotfix/Settings`。控件使用 UIFrame 生成与回填，按钮和滑条监听在 OnCreate 注册到 LifetimeScope，语言变化监听属于 OpenScope。OnOpen 从已保存偏好刷新控件，不重复注册监听。

| 设置 | 接入系统 | 本地保存 |
| --- | --- | --- |
| 总音量 | GameAudio master | `game.audio.master`，默认 1 |
| 音乐音量 | GameAudio Bgm bus | `game.audio.music`，默认 1 |
| 音效音量 | GameAudio Sfx、Ui bus | `game.audio.sfx`，默认 1 |
| 音效开关 | 关闭时 Sfx、Ui 音量设为 0，重新开启恢复原值 | `game.audio.sfxEnabled`，默认开启 |
| 中英文 | LanguageManager | 沿用 `game.language` |

音量范围为 0～1，调整立即生效。音量偏好使用 PlayerPrefs，返回时 Save；GameEntry 在框架就绪后应用已保存音量。未启用音频时禁用音量控件并显示原因，仍可切换语言或返回。

设置文本由 SettingsLocalization 补入已初始化的语言服务；当前语言切换覆盖已接入 LanguageManager 的文本，其他界面既有硬编码文本需要各业务后续接入。设置页提供中文与英文选择，不改变现有多语言框架。

账号记忆属于 GameLoginSession，不由设置页保存密码或持有数据库。免密登录、切换账号、安全存储和令牌失效规则见 [客户端网络使用说明](ClientNetworkUsage.md#2-注册登录恢复与注销)。

验证入口为 PlayMode 的 SettingsPanelTests，覆盖主界面入口、实际音频总线、静音后恢复原音量、中英文文本、关闭/重新加载后的持久化和返回导航。

2026-10-14：macOS Editor 交互测试通过，修正布局后再次通过；1080×1920 预览检查完成。UIFrame 的 18 个设置字段、主界面设置按钮和 3 个登录新增字段均完成生成与回填；最终磁盘引用逐一核验，绑定任务已结束。验证图片仅使用系统临时目录并在结束时清理。
