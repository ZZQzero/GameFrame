# 五子棋本地对局

从 `Unity/Assets/Scenes/Launch.unity` 启动后，GameEntry 打开 `GomokuMainPanel`。
当前为同机双人：黑方先行，双方交替落子，横、竖、两种斜线连续五子及以上获胜，
没有三三、四四和长连禁手。棋盘填满且没有获胜方时和棋。
点击已占用位置或终局后继续点击不会改变棋局；“悔一步”撤销最近一手，“重新开始”清空棋局。

## 职责和操作流程

所有业务代码位于 `Unity/Assets/Scripts/Hotfix/Gomoku`，不向 Runtime 添加业务层。

| 类型 | 职责 |
| --- | --- |
| GomokuGame | 纯 C# 棋局数据、执子方、合法性检查、四方向胜负、和棋、历史和悔棋；不依赖 Unity |
| GomokuBoardGraphic | 网格、星位和交点坐标计算 |
| GomokuBoardView | 点击转行列、棋子和最后落子标记、可用区域适配；不判断回合和胜负 |
| GomokuMainPanel | 本地操作协调、棋局创建、调用规则、刷新显示与状态文字、按钮生命周期 |

本地流程是 `IntersectionClicked → GomokuMainPanel.ApplyMove → GomokuGame.PlaceMove → Render`。
被规则拒绝的落子不会刷新显示。操作包含行列和执子方，显示层不直接写棋盘数组。
后续网络接入可将点击处理改为发送请求，收到服务器确认后调用 ApplyMove；
匹配、房间、消息顺序、身份、超时和断线处理由届时的网络流程负责，当前不预建这些层。

棋局历史 `Moves` 为只读集合，按接受顺序保存行列和执子方。
可以通过 `new GomokuGame(size)` 和顺序 PlaceMove 重放历史，随后调用 BoardView.Render 恢复完整画面。
当前没有存盘或网络快照协议；历史重放提供恢复显示的基础。

## API 与错误语义

- `new GomokuGame(size)`：每个方向至少 2 个交点，小于 2 抛出 ArgumentOutOfRangeException。
  小于 5 路的棋盘不能形成五连，填满后和棋。
- `PlaceMove(column, row, stone)`：接受时返回 Accepted；GameFinished、WrongTurn、OutOfBounds、Occupied
  表示预期拒绝，棋盘、回合、终局状态和历史保持不变；空子或非法执子枚举抛出 ArgumentOutOfRangeException。
- `GetStone(column, row)`：返回空、黑、白；越界查询抛出 ArgumentOutOfRangeException。
- `UndoLastMove()`：空棋局返回 false；否则删除最后一手、恢复该手执子方和 Playing 状态。
  即使上一手结束了棋局，也可以撤销。
- `GomokuMainPanel.StartNewGame(pointCount)`：统一创建新棋局、修改网格规格并清空显示。
  在活动棋局中切换 18、20 路等规格必须走此入口。
- `GomokuMainPanel.ApplyMove(column, row, stone)`：应用操作并在接受后更新显示；未创建棋局时抛出 InvalidOperationException。
- `GomokuBoardView.Render(game)`：按完整棋局更新显示。空棋局参数抛出 ArgumentNullException；
  资源引用缺失或棋局与网格规格不一致抛出 InvalidOperationException。

缓存关闭保留棋局，重新打开继续；重新开始才清空棋局。
按钮和棋盘事件只在 OnCreate 注册，销毁时由 LifetimeScope 解除；关闭和暂停期间停止接收棋盘点击。
棋子图片以 prefab 序列化引用提供，随 prefab 资源句柄保持有效，不另外持有异步加载句柄。
棋子对象在本面板内复用，重开和悔棋隐藏多余对象，面板销毁时随子节点释放。

## Prefab 与配置

`Unity/Assets/Prefab/Gomoku/GomokuMain.prefab` 的主要层级为：

```text
GomokuMain
└── GomokuBg
    └── Content                         SafeAreaFitter
        ├── Title / Subtitle / Status
        ├── Checkerboard                Image + GomokuBoardView
        │   ├── Grid                    GomokuBoardGraphic
        │   └── Pieces
        │       ├── Piece …             运行时按需创建
        │       └── LastMoveMarker
        ├── UndoButton
        └── RestartButton
```

Checkerboard 的 Image 提供底色，Grid 和 Pieces 均铺满 Checkerboard，棋子与标记关闭 Raycast Target。
Checkerboard 的 Image 接收射线，由同节点的 GomokuBoardView 处理点击；右键不落子。
点击转为 Grid 的局部坐标，取最近交点，最外圈线之外允许半格；超出允许范围返回 false。

在 Grid 的 Inspector 配置网格：

| 参数 | 默认值 | 含义 |
| --- | --- | --- |
| Point Count | 15 | 横竖各自的交点数量，可设置 18、20 等；运行中切换使用 StartNewGame |
| Padding Ratio | 0.05 | 每侧留白占棋盘较短边的比例 |
| Line Width / Border Width | 2 / 3 | 内线和外圈线宽，单位为 UI 局部单位 |
| Color | 深褐色 | 网格和星位颜色；不透明色避免交叉处叠加变深 |
| Show Star Points | true | 星位开关 |
| Star Radius Ratio | 0.08 | 星位半径占格距的比例 |

每个方向至少 9 个交点时，四个星位距离最外侧线 3 个间隔；奇数路额外绘制中央星位。
小于 9 路的奇数棋盘只有中央星位，偶数棋盘没有。星位仅为装饰，不改变规则。
网格按配置或尺寸变化重建 UI Mesh，不每帧生成对象。

Checkerboard 的 GomokuBoardView 可设置 Maximum Board Size（默认 1000）和 Piece Size Ratio（默认 0.85）。
Checkerboard 的 RectTransform 锚点定义 Content 中的可用范围，默认 Min=(0.05, 0.2)、Max=(0.95, 0.8)，
为上下信息和按钮留出空间，Anchored Position 为零、Pivot 为中心。
实际棋盘边长取最大边长与父节点尺寸乘锚点跨度得到的可用宽高的最小值，
通过 sizeDelta 将矩形收缩为正方形；点击范围跟随实际棋盘，不额外创建布局容器。
Content 复用框架 SafeAreaFitter，方向不锁定。
GomokuBoardView 使用 ExecuteAlways，Prefab 编辑预览与运行时使用相同的正方形适配。
尺寸回调调整棋盘和已落棋子，无每帧轮询；尺寸写入期间阻止回调重入。
sizeDelta 由布局管理，调整可用范围使用锚点，调整最大边长使用 Maximum Board Size。

GridRect 返回网格边界，CellSize 返回格距，GetIntersectionLocalPosition(column, row) 返回交点局部坐标。
行列从 0 开始，左下角为 (0, 0)，列向右、行向上；越界行列抛出 ArgumentOutOfRangeException。
棋子使用相同比例锚点，尺寸根据当前格距计算，红色小方块标记最后一手。

## 资源与验证

黑白棋子使用 `Assets/ArtRes/Gomoku/Image/BlackPieces.png` 和 `WhitePieces.png`，导入类型为 Sprite。
GameUIRegistration 注册 GomokuMain，DefaultPackage 的 Gomoku 组保持 `gomoku` 标签并增加 `boot`，
采集 Images 和 prefab；字体通过 prefab 依赖采集。GameEntry 在框架和配置就绪后打开此面板。
正式包仍需重新编译 Hotfix DLL 并构建 YooAsset 资源包，不能仅更新 Editor 源码。

PlayMode 测试 GomokuGameTests 覆盖回合、非法落子、四方向胜负、白方胜利、长连、和棋、悔棋和历史重放；
GomokuPanelTests 覆盖指针输入、按钮、缓存重开、规格切换、区域缩放、坐标对齐和终局显示。
