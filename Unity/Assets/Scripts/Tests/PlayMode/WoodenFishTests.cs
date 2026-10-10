using System.Collections;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using Game.AOT;
using Game.Hotfix;
using GameFrame.Audio;
using GameFrame.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using YooAsset;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace GameFrame.Tests
{
    public sealed class WoodenFishTests
    {
        private ResourceLoadManager resources;
        private GlobalConfig config;
        private WoodenFishMainPanel panel;
        private WoodenFishHitTarget target;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
#if UNITY_EDITOR
            config = ScriptableObject.CreateInstance<GlobalConfig>();
            config.PlayMode = EPlayMode.EditorSimulateMode;
            resources = new ResourceLoadManager();
            await resources.InitializeAsync(config);
            await resources.UpdatePackageManifestAsync();
            Assert.IsTrue(resources.ContainsLocation("WoodenFishMain"));
            Assert.IsTrue(resources.ContainsLocation("WoodenFishHit"));
            GameUI.Init(resources.Package);
            var audio = AssetDatabase.LoadAssetAtPath<AudioRuntimeConfig>(
                "Assets/Config/Audio/GameAudioConfig.asset");
            await GameAudio.InitAsync(resources.Package, audio);
            GameUIRegistration.RegisterAll();
            panel = await GameUI.Push<WoodenFishMainPanel>();
            target = panel.GetComponentInChildren<WoodenFishHitTarget>();
#else
            Assert.Ignore("WoodenFish tests require Editor simulated assets.");
            await UniTask.CompletedTask;
#endif
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            GameUI.Shutdown();
            await GameAudio.ShutdownAsync();
            if (resources != null)
            {
                await resources.ShutdownAsync();
            }

            Object.Destroy(config);
            await UniTask.NextFrame();
        });

        [UnityTest]
        public IEnumerator RapidPressesCountIndependentlyAndReuseFloatingTexts()
        {
            for (var i = 0; i < 20; i++)
            {
                Press();
            }

            Assert.AreEqual(20, panel.MeritCount);
            Assert.AreEqual(20, FloatingTexts(true));
            Assert.AreEqual("功德：20", panel.GetComponentsInChildren<TextMeshProUGUI>()
                .Single(text => text.name == "ClickNum").text);
            yield return new WaitForSecondsRealtime(0.85f);
            Assert.AreEqual(0, FloatingTexts(true));

            var allocated = FloatingTexts(false);
            Press();
            Assert.AreEqual(21, panel.MeritCount);
            Assert.AreEqual(1, FloatingTexts(true));
            Assert.AreEqual(allocated, FloatingTexts(false));
        }

        [Test]
        public void RightPressAndDisabledTargetAreIgnored()
        {
            Press(PointerEventData.InputButton.Right);
            target.enabled = false;
            Press();
            Assert.AreEqual(0, panel.MeritCount);
            Assert.AreEqual(0, FloatingTexts(true));
        }

        [UnityTest]
        public IEnumerator ClosingAndReopeningPreservesCountAndClearsPresentation()
        {
            Press();
            GameUI.Close<WoodenFishMainPanel>();
            Assert.AreEqual(0, FloatingTexts(true));
            Press();
            Assert.AreEqual(1, panel.MeritCount);

            WoodenFishMainPanel reopened = null;
            yield return GameUI.Push<WoodenFishMainPanel>().ToCoroutine(value => reopened = value);
            Assert.AreSame(panel, reopened);
            Press();
            Assert.AreEqual(2, panel.MeritCount, "Reopening must not duplicate input subscriptions.");
            Assert.AreEqual(1, FloatingTexts(true));
            GameUI.Close<WoodenFishMainPanel>(destroy: true);
            yield return null;
            Assert.IsTrue(panel == null);
        }

        [Test]
        public void StrikeKeepsStickIndependentAndRestartsBothAnimations()
        {
            var fish = target.transform;
            var pivot = panel.transform.Find("StickPivot");
            var restRotation = pivot.localRotation;
            var restScale = fish.localScale;
            var stickScale = pivot.lossyScale;
            var stickPosition = pivot.position;

            Press();
            AdvanceStrike(0.0275f);
            Assert.Greater(Quaternion.Angle(restRotation, pivot.localRotation), 0f);
            Assert.Greater(fish.localScale.x, restScale.x);
            Assert.Less(fish.localScale.y, restScale.y);
            Assert.Less(Vector3.Distance(stickScale, pivot.lossyScale), 0.001f);
            Assert.Less(Vector3.Distance(stickPosition, pivot.position), 0.001f);

            Press();
            Assert.Less(Quaternion.Angle(restRotation, pivot.localRotation), 0.01f);
            Assert.AreEqual(restScale, fish.localScale);
            AdvanceStrike(0.205f);
            Assert.Less(Quaternion.Angle(restRotation, pivot.localRotation), 0.01f);
            Assert.Less(Vector3.Distance(restScale, fish.localScale), 0.001f);
            Assert.AreEqual(2, panel.MeritCount);
        }

        [Test]
        public void PauseClearsAnimationsAndResumeAcceptsPresses()
        {
            var fish = target.transform;
            var pivot = panel.transform.Find("StickPivot");
            var restScale = fish.localScale;
            var restRotation = pivot.localRotation;
            Press();
            Dispatch("DispatchPause");
            Assert.AreEqual(0, FloatingTexts(true));
            Assert.AreEqual(restScale, fish.localScale);
            Assert.Less(Quaternion.Angle(restRotation, pivot.localRotation), 0.01f);
            Press();
            Assert.AreEqual(1, panel.MeritCount);
            Dispatch("DispatchResume");
            Press();
            Assert.AreEqual(2, panel.MeritCount);
        }

        private void Press(PointerEventData.InputButton button = PointerEventData.InputButton.Left)
        {
            target.OnPointerDown(new PointerEventData(EventSystem.current) { button = button });
        }

        private int FloatingTexts(bool activeOnly)
        {
            return panel.GetComponentsInChildren<TextMeshProUGUI>(true)
                .Count(text => text.name == "MeritFloatingText" && (!activeOnly || text.gameObject.activeSelf));
        }

        private void Dispatch(string method)
        {
            typeof(UIPanel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(panel, null);
        }

        private void AdvanceStrike(float elapsedTime)
        {
            var animation = typeof(WoodenFishMainPanel)
                .GetField("strikeAnimation", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel);
            animation.GetType().GetProperty("elapsedTime").SetValue(animation, elapsedTime);
        }
    }
}
