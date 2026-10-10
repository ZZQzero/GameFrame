# UI 中文字体

项目默认 TMP 字体为 `Unity/Assets/ArtRes/Fonts/siyuansongti SDF.asset`，通过 `Unity/Assets/TextMesh Pro/Resources/TMP Settings.asset` 的 Default Font Asset 配置。新建或未指定字体的 TMP 文本使用该字体；已有组件显式指定的字体不会被批量替换。木鱼面板的两个文本已经显式引用它。

字体来源为同目录的 `siyuansongti.ttf`。源字体为 Source Han Serif CN Heavy，TMP 文本组件使用 Normal 样式，避免再叠加模拟 Bold。

| 参数 | 设置 |
| --- | --- |
| Atlas Population Mode | Dynamic，保留源 TTF，遇到新字符时按需生成 |
| Sampling Point Size | 90 |
| Render Mode | SDFAA |
| Atlas Size | 2048 × 2048，每张 Alpha8 图集约 4 MiB 原始像素数据 |
| Padding | 9 像素 |
| Multi Atlas Textures | 开启，首张图集填满后允许新增图集 |
| Clear Dynamic Data On Build | 关闭，构建保留预置字形 |
| Texture Filter / Wrap | Bilinear / Clamp |

预置字符包括 ASCII 可打印字符、中文标点和“功德木鱼累计敲击次数总计”，共 123 个字符，当前仅使用一张图集。不预生成整个中文字库；后续中文文案由 Dynamic 模式生成，新增图集会增加内存。

`WoodenFishMain.prefab` 中的 `DesTip`、`ClickNum` 已引用该字体及其默认材质，字号保持 60，文本内容、颜色与布局保持当前配置。

已在 Editor 中验证“功德+1”全部使用该字体生成可见字形，无回退；60 号文本的首选尺寸约为 193.4 × 86.23 UI 单位。未验证 IL2CPP 包及手机显示效果。
