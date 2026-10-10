---
name: unity-primetween-reference
description: PrimeTween API 速查与 DOTween 对照（项目表现层补充材料）
---

# PrimeTween API 参考

版本：`com.kyrylokuzyk.primetween` **1.4.11**  
命名空间：`PrimeTween`

## Tween 生命周期

- 静态 `Tween.*` / `Sequence.Create()` 返回 **struct**
- `isAlive`：已创建且未结束（含暂停）
- 结束后不可再控制或读属性（会打错误日志）
- 需要再播：重新调用对应 `Tween.*` 方法

## Transform

```csharp
Tween.Position / PositionX / Y / Z
Tween.LocalPosition / LocalPositionX / Y / Z
Tween.PositionAtSpeed / LocalPositionAtSpeed  // Chain 后续段必须带 startValue
Tween.Scale / ScaleX / Y / Z
Tween.Rotation / LocalRotation               // Quaternion
Tween.EulerAngles / LocalEulerAngles
```

## UI

```csharp
Tween.UIAnchoredPosition / UIAnchoredPosition3D / 轴分量
Tween.Alpha(CanvasGroup | Graphic | Shadow, …)
Tween.Color(Graphic | Shadow, …)
Tween.UIPreferredSize / Width / Height       // LayoutElement
Tween.UIFlexibleSize / MinSize / …
```

可逆面板：`tweenSettings.WithDirection(toEndValue: opened)`（`TweenSettings<T>`）。

## 渲染 / 其它

```csharp
Tween.Alpha(SpriteRenderer, …)
Tween.Color(SpriteRenderer | Material | Light | Camera.background, …)
Tween.CameraFieldOfView / OrthographicSize / …
Tween.AudioVolume / Pitch / …
Tween.MaterialProperty / MaterialPropertyBlock…  // PRO 能力以包内文档为准
```

## Sequence

```csharp
Sequence.Create(cycles: 1, SequenceCycleMode.Restart, Ease.Linear);

seq.Group(tween);              // 与上一段并行
seq.Chain(tween);              // 接在整条序列末尾
seq.Insert(atTime, tween);     // 指定时间线位置
seq.ChainDelay(seconds);
seq.ChainCallback(target, t => …);
seq.InsertCallback(atTime, target, t => …);
```

控制与 Tween 相同：`Stop` / `Complete` / `isPaused` / `timeScale` / `await sequence`。

## TweenSettings / TweenSettings&lt;T&gt;

```csharp
[Serializable]
public struct TweenSettings
{
    public float duration;
    public Ease ease;
    public AnimationCurve customEase;
    public int cycles;              // -1 = 无限
    public CycleMode cycleMode;     // Restart / Yoyo / Incremental / Rewind
    public float startDelay;
    public float endDelay;
    public bool useUnscaledTime;
    public UpdateType updateType;   // Update / LateUpdate / FixedUpdate
}

[Serializable]
public struct TweenSettings<T> where T : struct
{
    public bool startFromCurrent;
    public T startValue;
    public T endValue;
    public TweenSettings settings;
}
```

### 推荐写法（1.4.11）

```csharp
[SerializeField] TweenSettings moveSettings;

// 运行时只知道 end：包成 TweenSettings<T>（startFromCurrent=true）
Tween.Position(t, new TweenSettings<Vector3>(endPos, moveSettings));
Tween.Scale(t, new TweenSettings<Vector3>(endScale, moveSettings));
Tween.UIAnchoredPositionY(panel, new TweenSettings<float>(endY, moveSettings));

// 起终点都可在 Inspector 配
[SerializeField] TweenSettings<Vector3> moveSettingsT;
Tween.Position(t, moveSettingsT);

// 字面量
Tween.Position(t, endPos, duration: 0.28f, ease: Ease.OutQuad);
```

### 过时（禁止，CS0618）

```csharp
// ❌ Obsolete: Use the overload with TweenSettings<T> instead
Tween.Position(t, endPos, moveSettings);
Tween.Scale(t, endScale, moveSettings);
Tween.LocalPosition(t, endPos, moveSettings);
```

## Ease

| Ease | 场景 |
|------|------|
| `InOutSine` | 平滑平移 |
| `OutQuad` / `OutCubic` | UI 滑入、减速 |
| `OutBack` | 轻微过冲 |
| `Linear` | 匀速、进度 |
| `Default` | `PrimeTweenConfig.defaultEase` |

参数化：`Easing.BounceExact(1f)`、`Easing.Overshoot(1.2f)`、`Easing.Elastic(1f)`。

## 回调与自定义

```csharp
tween.OnComplete(target, t => t.Foo());
tween.OnUpdate(target, (t, tw) => { var p = tw.progress; });

Tween.Delay(target, duration, t => t.Foo());
Tween.Custom(target, 0f, 1f, duration, (t, v) => t.field = v);
```

## Shake / Punch

```csharp
Tween.ShakeLocalPosition(transform, strength, duration, frequency);
Tween.PunchLocalPosition(transform, strength, duration, frequency);
Tween.ShakeLocalRotation / ShakeScale / ShakeCamera / ShakeCustom
```

## 全局配置

```csharp
PrimeTweenConfig.SetTweensCapacity(128);
PrimeTweenConfig.defaultEase = Ease.OutCubic;
PrimeTweenConfig.warnEndValueEqualsCurrent = false; // 可选降噪
```

调试：Hierarchy → DontDestroyOnLoad → **PrimeTweenManager**（含 Max alive tweens）。

## DOTween → PrimeTween

| DOTween | PrimeTween |
|---------|------------|
| `transform.DOMove` | `Tween.Position(t, new TweenSettings<Vector3>(…))` |
| `DOLocalMove` | `Tween.LocalPosition` |
| `ui.DOAnchorPos` | `Tween.UIAnchoredPosition` |
| `DOFade` | `Tween.Alpha` |
| `SetEase` | `TweenSettings` / `ease:` 参数 |
| `SetLoops(2, Yoyo)` | `cycles: 2, CycleMode.Rewind`（注意语义差异） |
| `Kill(false)` | `Stop()` |
| `Kill(true)` | `Complete()` |
| `DOTween.Kill(t)` | `Tween.StopAll(t)` |
| `Sequence.Append` | `Chain` |
| `Sequence.Join` | `Group` |
| `AsyncWaitForCompletion` | `await tween` |
| `SetAutoKill(false)` + PlayForward | **不支持**；按方向新开 Tween |

Adapter：Player Settings 加 `PRIME_TWEEN_DOTWEEN_ADAPTER`（仅迁移旧代码）。

## 与本项目异步栈

| 层 | 技术 |
|----|------|
| 网络 / 业务 | UniTask |
| 表现补间 | PrimeTween（可 await） |
| 协程 | 仅遗留流程用 `ToYieldInstruction()`；新代码优先 Sequence / await |

## 官方链接

- 包内 `readme.md`
- Discussions：https://github.com/KyryloKuzyk/PrimeTween/discussions
- 性能对比：https://github.com/KyryloKuzyk/PrimeTween/discussions/10
