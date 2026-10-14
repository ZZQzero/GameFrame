using System;
using System.Collections;
using Cysharp.Threading.Tasks;
using Game.AOT;
using Game.Hotfix;
using GameFrame.Audio;
using GameFrame.Scene;
using GameFrame.UI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YooAsset;

namespace GameFrame.Tests
{
    public sealed class MainPanelTests
    {
        private ResourceLoadManager resources;
        private GlobalConfig config;
        private MainPanel main;
        private UnityEngine.SceneManagement.Scene previousScene;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
#if UNITY_EDITOR
            previousScene = SceneManager.GetActiveScene();
            config = ScriptableObject.CreateInstance<GlobalConfig>();
            config.PlayMode = EPlayMode.EditorSimulateMode;
            resources = new ResourceLoadManager();
            await resources.InitializeAsync(config);
            await resources.UpdatePackageManifestAsync();
            GameUI.Init(resources.Package);
            GameUIRegistration.RegisterAll();
            var audio = UnityEditor.AssetDatabase.LoadAssetAtPath<AudioRuntimeConfig>(
                "Assets/Config/Audio/GameAudioConfig.asset");
            await GameAudio.InitAsync(resources.Package, audio);
            GameScene.Init(resources.Package);
            await GameScene.LoadAsync("Main", LoadSceneMode.Single);
            GameUI.ConfigureURPCameraStack(Camera.main);
            main = await GameUI.Push<MainPanel>();
            await UniTask.NextFrame();
            Assert.AreEqual("Main", SceneManager.GetActiveScene().name);
            CollectionAssert.AreEqual(new[] { GameUI.UICamera },
                Camera.main.GetUniversalAdditionalCameraData().cameraStack);
#else
            Assert.Ignore("Main panel tests require Editor simulated assets.");
            await UniTask.CompletedTask;
#endif
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            GameUI.Shutdown();
            if (GameAudio.IsInited)
            {
                await GameAudio.ShutdownAsync();
            }

            if (GameScene.IsInited)
            {
                if (GameScene.IsLoaded("Main"))
                {
                    SceneManager.SetActiveScene(previousScene.IsValid() && previousScene.isLoaded
                        ? previousScene : SceneManager.CreateScene("MainPanelTests"));
                    await GameScene.UnloadAsync("Main");
                }

                await GameScene.ShutdownAsync();
            }

            if (resources != null)
            {
                await resources.ShutdownAsync();
            }

            UnityEngine.Object.Destroy(config);
            await UniTask.NextFrame();
        });

        [UnityTest]
        public IEnumerator GameButtonsAndExitReturnToMainWithCachedProgress() => UniTask.ToCoroutine(async () =>
        {
            MainButton("GomokuButton").onClick.Invoke();
            await UniTask.WaitUntil(() => GameUI.IsOpen<GomokuMainPanel>()).Timeout(TimeSpan.FromSeconds(10));
            var gomoku = GameUI.Get<GomokuMainPanel>();
            Assert.IsFalse(main.gameObject.activeInHierarchy);
            gomoku.ApplyMove(0, 0, GomokuStone.Black);
            gomoku.transform.Find("GomokuBg/Content/ExitButton").GetComponent<Button>().onClick.Invoke();
            Assert.IsFalse(GameUI.IsOpen<GomokuMainPanel>());
            Assert.IsTrue(main.gameObject.activeInHierarchy);
            Assert.IsFalse(gomoku.GetComponentInChildren<GomokuBoardView>(true).Interactable);

            MainButton("WoodenFishButton").onClick.Invoke();
            await UniTask.WaitUntil(() => GameUI.IsOpen<WoodenFishMainPanel>()).Timeout(TimeSpan.FromSeconds(10));
            var fish = GameUI.Get<WoodenFishMainPanel>();
            Assert.IsFalse(main.gameObject.activeInHierarchy);
            var target = fish.GetComponentInChildren<WoodenFishHitTarget>();
            target.OnPointerDown(new PointerEventData(EventSystem.current));
            Assert.AreEqual(1, fish.MeritCount);
            fish.transform.Find("Navigation/ExitButton").GetComponent<Button>().onClick.Invoke();
            Assert.IsFalse(GameUI.IsOpen<WoodenFishMainPanel>());
            Assert.IsTrue(main.gameObject.activeInHierarchy);
            target.OnPointerDown(new PointerEventData(EventSystem.current));
            Assert.AreEqual(1, fish.MeritCount, "退出后不能继续敲击。");

            MainButton("GomokuButton").onClick.Invoke();
            await UniTask.WaitUntil(() => GameUI.IsOpen<GomokuMainPanel>()).Timeout(TimeSpan.FromSeconds(10));
            Assert.AreSame(gomoku, GameUI.Get<GomokuMainPanel>());
            Assert.AreEqual(1, gomoku.Game.Moves.Count);
            gomoku.transform.Find("GomokuBg/Content/ExitButton").GetComponent<Button>().onClick.Invoke();
            MainButton("WoodenFishButton").onClick.Invoke();
            await UniTask.WaitUntil(() => GameUI.IsOpen<WoodenFishMainPanel>()).Timeout(TimeSpan.FromSeconds(10));
            Assert.AreSame(fish, GameUI.Get<WoodenFishMainPanel>());
            target.OnPointerDown(new PointerEventData(EventSystem.current));
            Assert.AreEqual(2, fish.MeritCount, "缓存重开不应重复订阅点击。");
            fish.transform.Find("Navigation/ExitButton").GetComponent<Button>().onClick.Invoke();
            Assert.IsTrue(main.gameObject.activeInHierarchy);
            Assert.IsTrue(MainButton("GomokuButton").interactable);
            Assert.IsTrue(MainButton("WoodenFishButton").interactable);
        });

        [UnityTest]
        public IEnumerator RapidGameClicksDoNotStackBothGamesAndSceneGroupClosesAll() => UniTask.ToCoroutine(async () =>
        {
            MainButton("GomokuButton").onClick.Invoke();
            MainButton("WoodenFishButton").onClick.Invoke();
            MainButton("GomokuButton").onClick.Invoke();
            await UniTask.WaitUntil(() => GameUI.IsOpen<GomokuMainPanel>()).Timeout(TimeSpan.FromSeconds(10));
            Assert.IsFalse(GameUI.IsOpen<WoodenFishMainPanel>());
            Assert.IsFalse(main.gameObject.activeInHierarchy);

            GameUI.CloseGroup(UIGroup.Scene);
            Assert.IsFalse(GameUI.IsOpen<MainPanel>());
            Assert.IsFalse(GameUI.IsOpen<GomokuMainPanel>());
            Assert.IsFalse(GameUI.IsOpen<WoodenFishMainPanel>());
            Assert.IsFalse(main.gameObject.activeInHierarchy);
            Assert.AreSame(main, await GameUI.Push<MainPanel>());
            Assert.IsTrue(main.gameObject.activeInHierarchy);
            Assert.IsTrue(MainButton("GomokuButton").interactable);
            Assert.IsTrue(MainButton("WoodenFishButton").interactable);
        });

        private Button MainButton(string name)
        {
            return main.transform.Find("Content/Card/" + name).GetComponent<Button>();
        }
    }
}
