# 五子棋棋盘

`Unity/Assets/Prefab/Gomoku/GomokuMain.prefab` 的棋盘位于
`GomokuMain/GomokuBg/Checkerboard`。`Checkerboard` 的 Image 负责底色或背景贴图，
子节点 `Grid` 的 `Game.Hotfix.GomokuBoardGraphic` 绘制网格和星位。
棋盘布局是游戏业务，代码位于 Hotfix，不进入 Runtime。

## 布局与配置

在 prefab 中选择 `Checkerboard/Grid`，通过 Inspector 配置：

| 参数 | 默认值 | 含义 |
| --- | --- | --- |
| Point Count | 15 | 横竖各自的交点数量，至少为 2；可改为 18、20 等 |
| Padding Ratio | 0.05 | 每侧留白占棋盘较短边的比例 |
| Line Width | 2 | 内部网格线宽，单位为 UI 局部单位 |
| Border Width | 3 | 最外圈网格线宽，单位为 UI 局部单位 |
| Color | 深褐色 | 网格和星位颜色；使用不透明色可避免交叉处叠加变深 |
| Show Star Points | true | 是否绘制星位 |
| Star Radius Ratio | 0.08 | 星位半径占格距的比例 |

15 路表示每个方向 15 条线、14 个间隔。网格在实际 RectTransform 中居中，
按较短边绘制正方形；默认 900×900 棋盘的网格边长为 810。
配置和布局变化会触发 UI Mesh 重建，编辑器和运行中均可显示，不需要每帧生成对象。
Grid 铺满 Checkerboard，关闭 Raycast Target，保留 Checkerboard 接收后续棋盘点击的能力。

每个方向至少 9 个交点时，四个星位位于距离最外侧线 3 个间隔的交点。
奇数路额外绘制中央星位；偶数路没有中央交点，不绘制中央星位。
小于 9 路的奇数棋盘只绘制中央星位，偶数棋盘不绘制星位。
18、20 等规格的星位仅为装饰，不代表棋局规则。

## 尺寸变化与坐标

现有 Checkerboard 保持居中的 900×900 UI 局部尺寸，由框架 CanvasScaler 缩放。
这不是根据安全区自动计算棋盘边长；后续加入顶部信息和底部操作区时，
可由页面布局调整 Checkerboard 尺寸，网格会自动按新尺寸重绘。

运行中调用 `SetPointCount(count)` 更改规格；小于 2 时抛出 ArgumentOutOfRangeException，
原配置保持不变。规格变化后，调用方需要自行更新棋局数据和棋子显示。

`GridRect` 返回网格边界，`CellSize` 返回格距，
`GetIntersectionLocalPosition(column, row)` 返回 Grid 的局部交点坐标。
行列从 0 开始，原点交点位于左下角，列向右、行向上；越界行列抛出 ArgumentOutOfRangeException。
后续棋子应与 Grid 使用相同的父坐标系与 RectTransform 布局，或显式转换坐标。

目前仅实现棋盘布局，没有落子、胜负判断和棋子显示。
现有 GameEntry 仍打开 WoodenFishMainPanel，GomokuMain 尚未注册到业务入口或 YooAsset 采集配置。
可在带 Canvas 的 prefab 编辑视图或场景中预览棋盘。
