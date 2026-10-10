using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using GameFrame.Audio;
using GameFrame.Pooling;
using GameFrame.UI;
using PrimeTween;
using TMPro;
using UnityEngine;

namespace Game.Hotfix
{
    public partial class WoodenFishMainPanel : UIPanel<UINone>
    {
        [Header("敲击")]
        [SerializeField] private WoodenFishHitTarget hitTarget;
        [SerializeField] private RectTransform stickPivot;
        [SerializeField] private float strikeAngle = 28f;
        [SerializeField] private Vector3 hitScale = new Vector3(1.06f, 0.94f, 1f);
        [SerializeField] private TweenSettings strikeSettings = new TweenSettings(0.055f, Ease.OutQuad, useUnscaledTime: true);
        [SerializeField] private TweenSettings returnSettings = new TweenSettings(0.15f, Ease.OutBack, useUnscaledTime: true);

        [Header("功德飘字")]
        [SerializeField] private float floatHeight = 110f;
        [SerializeField] private float floatStartOffset = 20f;
        [SerializeField] private float horizontalSpread = 50f;
        [SerializeField] private TweenSettings floatSettings = new TweenSettings(0.7f, Ease.OutQuad, useUnscaledTime: true);
        [SerializeField] private TweenSettings fadeSettings = new TweenSettings(0.35f, Ease.Linear, useUnscaledTime: true);

        private readonly List<FloatingText> activeTexts = new List<FloatingText>(16);
        private ManagedObjectPool<FloatingText> textPool;
        private Sequence strikeAnimation;
        private Quaternion restRotation;
        private Vector3 restScale;
        private bool acceptingHits;

        public long MeritCount { get; private set; }

        protected override void OnCreate()
        {
            restRotation = stickPivot.localRotation;
            restScale = fishImg.rectTransform.localScale;
            num.gameObject.SetActive(false);
            textPool = new ManagedObjectPool<FloatingText>(
                CreateFloatingText,
                onReturn: static item => item.Reset(),
                onDestroy: static item => Destroy(item.Text.gameObject),
                options: new ManagedPoolOptions(initialCapacity: 8, maxSize: 64));

            var prewarm = new FloatingText[8];
            for (var i = 0; i < prewarm.Length; i++)
            {
                prewarm[i] = textPool.Get();
            }

            foreach (var item in prewarm)
            {
                textPool.Release(item);
            }

            hitTarget.Pressed += Hit;
            LifetimeScope.Register(() => hitTarget.Pressed -= Hit);
        }

        protected override void OnOpen(UINone args)
        {
            acceptingHits = true;
            RefreshCount();
            OpenScope.Register(StopPresentation);
        }

        protected override void OnPause()
        {
            StopPresentation();
        }

        protected override void OnResume()
        {
            acceptingHits = true;
        }

        protected override void OnDestroyPanel()
        {
            StopPresentation();
            textPool?.Dispose();
            textPool = null;
        }

        private void Hit()
        {
            if (!acceptingHits)
            {
                return;
            }

            MeritCount++;
            RefreshCount();
            PlayStrike();
            ShowFloatingText();
            // 音效常驻预加载；每次按下独立播放，不等待表现动画结束。
            GameAudio.TryPlayAsync(GameAudioIds.WoodenFishHit, OpenCancellationToken).Forget();
        }

        private void RefreshCount()
        {
            clickNum.text = $"功德：{MeritCount}";
        }

        private void PlayStrike()
        {
            StopStrike();
            var strikeRotation = restRotation * Quaternion.Euler(0f, 0f, strikeAngle);
            var pressedScale = Vector3.Scale(restScale, hitScale);
            strikeAnimation = Sequence.Create(useUnscaledTime: true)
                .Chain(Tween.LocalRotation(stickPivot, new TweenSettings<Quaternion>(restRotation, strikeRotation, strikeSettings)))
                .Group(Tween.Scale(fishImg.rectTransform, new TweenSettings<Vector3>(restScale, pressedScale, strikeSettings)))
                .Chain(Tween.LocalRotation(stickPivot, new TweenSettings<Quaternion>(strikeRotation, restRotation, returnSettings)))
                .Group(Tween.Scale(fishImg.rectTransform, new TweenSettings<Vector3>(pressedScale, restScale, returnSettings)));
        }

        private FloatingText CreateFloatingText()
        {
            var text = Instantiate(num, num.transform.parent, false);
            text.name = "MeritFloatingText";
            text.raycastTarget = false;
            text.text = "功德+1";
            var rect = text.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = rect.anchorMin;
            rect.pivot = rect.anchorMin;
            return new FloatingText(this, text);
        }

        private void ShowFloatingText()
        {
            var item = textPool.Get();
            activeTexts.Add(item);
            var parent = (RectTransform)num.transform.parent;
            var fish = fishImg.rectTransform;
            var worldPoint = fish.TransformPoint(new Vector3(0f, fish.rect.yMax + floatStartOffset, 0f));
            var start = (Vector2)parent.InverseTransformPoint(worldPoint);
            start.x += Random.Range(-horizontalSpread, horizontalSpread);
            item.Play(start, floatHeight, floatSettings, fadeSettings);
        }

        private void ReturnFloatingText(FloatingText item)
        {
            activeTexts.Remove(item);
            textPool.Release(item);
        }

        private void StopStrike()
        {
            if (strikeAnimation.isAlive)
            {
                strikeAnimation.Stop();
            }

            if (stickPivot != null)
            {
                stickPivot.localRotation = restRotation;
            }

            if (fishImg != null)
            {
                fishImg.rectTransform.localScale = restScale;
            }
        }

        private void StopPresentation()
        {
            acceptingHits = false;
            StopStrike();
            while (activeTexts.Count > 0)
            {
                ReturnFloatingText(activeTexts[activeTexts.Count - 1]);
            }
        }

        private sealed class FloatingText
        {
            private readonly WoodenFishMainPanel owner;
            private readonly Color color;
            private readonly Vector3 scale;
            private Sequence animation;

            public readonly TextMeshProUGUI Text;

            public FloatingText(WoodenFishMainPanel owner, TextMeshProUGUI text)
            {
                this.owner = owner;
                Text = text;
                color = text.color;
                scale = text.rectTransform.localScale;
            }

            public void Play(Vector2 start, float height, TweenSettings movement, TweenSettings fade)
            {
                Text.color = color;
                var rect = Text.rectTransform;
                rect.anchoredPosition = start;
                rect.localScale = scale * 0.85f;
                rect.SetAsLastSibling();
                Text.gameObject.SetActive(true);
                animation = Sequence.Create(useUnscaledTime: true)
                    .Group(Tween.UIAnchoredPosition(rect, new TweenSettings<Vector2>(start, start + Vector2.up * height, movement)))
                    .Group(Tween.Scale(rect, scale, duration: 0.12f, ease: Ease.OutBack, useUnscaledTime: true))
                    .Insert(Mathf.Max(0f, movement.duration - fade.duration), Tween.Alpha(Text, new TweenSettings<float>(color.a, 0f, fade)))
                    .OnComplete(this, static item => item.owner.ReturnFloatingText(item));
            }

            public void Reset()
            {
                if (animation.isAlive)
                {
                    animation.Stop();
                }

                Text.gameObject.SetActive(false);
                Text.color = color;
                Text.rectTransform.localScale = scale;
            }
        }
    }
}
