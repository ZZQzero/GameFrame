#if UNITY_EDITOR
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Game.AOT;
using Game.Hotfix;
using GameFrame.Diagnostics;
using GameFrame.UI;
using NUnit.Framework;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.TestTools;
using UnityEngine.UI;
using YooAsset;

namespace GameFrame.Tests
{
    public sealed class RuntimeDebugConsoleTests
    {
        RuntimeDebugConsole console;
        RuntimeLogBuffer buffer;
        ResourceLoadManager resources;
        GlobalConfig config;
        string clipboard;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            clipboard = GUIUtility.systemCopyBuffer;
            RuntimeLogCapture.Initialize();
            buffer = RuntimeLogCapture.Buffer;
            config = ScriptableObject.CreateInstance<GlobalConfig>();
            config.PlayMode = EPlayMode.EditorSimulateMode;
            resources = new ResourceLoadManager();
            await resources.InitializeAsync(config);
            await resources.UpdatePackageManifestAsync();
            GameUI.Init(resources.Package);
            GameUIRegistration.RegisterAll();
            buffer.Clear();
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            GameUI.Shutdown();
            RuntimeLogCapture.Shutdown();
            if (resources != null)
            {
                await resources.ShutdownAsync();
            }
            Object.Destroy(config);
            await UniTask.NextFrame();
            GUIUtility.systemCopyBuffer = clipboard;
        });

        async UniTask Open()
        {
            var panel = await GameUI.Guide<RuntimeLogPanel, RuntimeLogBuffer>(buffer);
            console = panel.GetComponent<RuntimeDebugConsole>();
        }

        [UnityTest]
        public IEnumerator CollectsBeforeOpenWhileClosedAndAfterPanelDestruction() => UniTask.ToCoroutine(async () =>
        {
            Assert.IsNull(GameUI.Get<RuntimeLogPanel>());
            await Task.Run(() => Debug.Log("before-first-open"));
            await Open();
            console.CopyAll();
            StringAssert.Contains("before-first-open", GUIUtility.systemCopyBuffer);
            GameUI.Close<RuntimeLogPanel>();
            Debug.Log("while-cached");
            await Open();
            console.CopyAll();
            StringAssert.Contains("while-cached", GUIUtility.systemCopyBuffer);
            GameUI.Close<RuntimeLogPanel>(destroy: true);
            await UniTask.NextFrame();
            await Task.Run(() => Debug.Log("after-panel-destroy"));
            await Open();
            console.CopyAll();
            StringAssert.Contains("before-first-open", GUIUtility.systemCopyBuffer);
            StringAssert.Contains("after-panel-destroy", GUIUtility.systemCopyBuffer);
            var retained = buffer;
            RuntimeLogCapture.Shutdown();
            long version = retained.Version;
            Debug.Log("after-capture-shutdown");
            Assert.AreEqual(version, retained.Version);
            RuntimeLogCapture.Initialize();
            Debug.Log("after-capture-restart");
            Assert.AreEqual(1, RuntimeLogCapture.Buffer.Snapshot(out _, out _).Count(x => x.Message == "after-capture-restart"));
        });

        [UnityTest]
        public IEnumerator VirtualRowsSelectDetailsAndCopyAllIgnoresFilters()
        {
            Assert.AreEqual(1, Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None).Length);
            buffer.Clear();
            for (int i = 0; i < 1000; i++)
            {
                buffer.Add("row-" + i, "full stack-" + i, i == 999 ? LogType.Warning : LogType.Log);
            }
            yield return Open().ToCoroutine();
            yield return null;
            var scroll = console.GetComponentInChildren<LoopVerticalScrollRect>();
            Assert.AreEqual(1000, scroll.totalCount);
            Assert.Less(console.GetComponentsInChildren<RuntimeLogRow>(true).Length, 80);
            var last = scroll.content.GetChild(scroll.content.childCount - 1);
            last.GetComponent<Button>().onClick.Invoke();
            StringAssert.Contains("row-999", Field<TMP_Text>("details").text);
            StringAssert.Contains("full stack-999", GUIUtility.systemCopyBuffer);
            Assert.AreEqual(Field<TMP_Text>("details").text, GUIUtility.systemCopyBuffer);

            Field<TMP_InputField>("search").text = "row-25";
            yield return new WaitForSecondsRealtime(0.15f);
            Assert.AreEqual(11, scroll.totalCount);
            Field<Toggle>("warnings").isOn = false;
            Field<Button>("copyAllButton").onClick.Invoke();
            StringAssert.Contains("row-0\nfull stack-0", GUIUtility.systemCopyBuffer);
            StringAssert.Contains("row-999\nfull stack-999", GUIUtility.systemCopyBuffer);

            var filteredLast = scroll.content.GetChild(scroll.content.childCount - 1);
            filteredLast.GetComponent<Button>().onClick.Invoke();
            StringAssert.Contains("row-259", GUIUtility.systemCopyBuffer);
            StringAssert.DoesNotContain("row-999", GUIUtility.systemCopyBuffer);
            Field<Button>("clearButton").onClick.Invoke();
            Assert.AreEqual(0, scroll.totalCount);
            StringAssert.Contains("Select a log", Field<TMP_Text>("details").text);
        }

        [UnityTest]
        public IEnumerator BrowsingStopsFollowingAndSelectionSurvivesBufferEviction()
        {
            buffer.Clear();
            buffer.Add("selected", "preserved stack", LogType.Log);
            var entry = buffer.Snapshot(out _, out _).Single();
            yield return Open().ToCoroutine();
            yield return null;
            console.Select(entry);
            var list = Field<RuntimeLogList>("list");
            list.OnScroll(null);
            Assert.IsFalse(Field<Toggle>("follow").isOn);
            for (int i = 0; i < 1100; i++)
            {
                buffer.Add("new-" + i, "", LogType.Log);
            }
            yield return new WaitForSecondsRealtime(0.15f);
            StringAssert.Contains("preserved stack", Field<TMP_Text>("details").text);
            Assert.AreEqual(1000, buffer.Snapshot(out _, out _).Length);
            GameUI.Close<RuntimeLogPanel>();
            yield return Open().ToCoroutine();
            Assert.IsTrue(GameUI.IsOpen<RuntimeLogPanel>());
        }

        T Field<T>(string name)
        {
            return (T)typeof(RuntimeDebugConsole).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(console);
        }
    }
}
#endif
