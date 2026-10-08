using GameFrame.UI;
using GameFrame.Display;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GameFrame.Tests
{
    public class ExtensionPanel : FailurePanel { }
    public class AddressPanel : FailurePanel { }

    public class UIExtensionTests
    {
        Action rootReady;
        Action<UIPanel> panelShown;
        UIManager manager;

        [UnityTearDown] public IEnumerator TearDown()
        {
            GameUI.RootReady -= rootReady;
            GameUI.PanelShown -= panelShown;
            GameUI.Shutdown();
            manager?.Shutdown();
            yield return null;
        }

        [UnityTest] public IEnumerator RootReadyFailureClearsManagerAndAllowsRetry()
        {
            var failure = new InvalidOperationException("root-ready-primary");
            rootReady = () =>
            {
                Assert.IsTrue(GameUI.IsInited);
                Assert.IsNotNull(GameUI.CanvasRoot);
                throw failure;
            };
            GameUI.RootReady += rootReady;
            Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(GameUI.Init));
            Assert.IsFalse(GameUI.IsInited);
            Assert.IsNull(GameUI.CanvasRoot);
            GameUI.RootReady -= rootReady;
            yield return null; // Destroy 完成后 EventSystem 所有权才释放。
            int readyCount = 0;
            rootReady = () => readyCount++;
            GameUI.RootReady += rootReady;
            Assert.DoesNotThrow(GameUI.Init);
            Assert.IsNotNull(GameUI.CanvasRoot);
            Assert.AreEqual(1, readyCount);
        }

        [UnityTest] public IEnumerator RootReadyShutdownCancelsInitAndAllowsRetry()
        {
            rootReady = GameUI.Shutdown;
            GameUI.RootReady += rootReady;

            Assert.Throws<OperationCanceledException>(GameUI.Init);
            Assert.IsFalse(GameUI.IsInited);
            Assert.IsNull(GameUI.CanvasRoot);

            GameUI.RootReady -= rootReady;
            yield return null; // 等待旧 Root 与 EventSystem 销毁。
            Assert.DoesNotThrow(GameUI.Init);
            Assert.IsTrue(GameUI.IsInited);
            Assert.IsNotNull(GameUI.CanvasRoot);
        }

        ExtensionPanel Prepare(UIOpenMode mode)
        {
            manager = new UIManager();
            manager.Init();
            var panel = new GameObject("extension-panel", typeof(RectTransform))
                .AddComponent<ExtensionPanel>();
            panel.Location = "compatibility/extension-panel";
            UIPanelCatalog.Register<ExtensionPanel>(panel.Location);
            if (mode == UIOpenMode.Toast)
                Field<Dictionary<Type, List<UIPanel>>>("_toastIdle")[typeof(ExtensionPanel)] = new() { panel };
            else
                Field<Dictionary<Type, UIPanel>>("_cached")[typeof(ExtensionPanel)] = panel;
            return panel;
        }

        T Field<T>(string name) => (T)typeof(UIManager)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);

        [Test] public void RegisteredAddressIsNotTrimmedOrAliased()
        {
            manager = new UIManager();
            manager.Init();
            const string location = " extension-panel ";
            UIPanelCatalog.Register<AddressPanel>(location);
            Assert.AreEqual(location, UIPanelCatalog.Resolve(typeof(AddressPanel), UIOpenMode.Hud).Location);
            Assert.DoesNotThrow(() => UIPanelCatalog.Register<AddressPanel>(location));
            Assert.Throws<InvalidOperationException>(() => UIPanelCatalog.Register<AddressPanel>("extension-panel"));
        }

        [TestCase(UIOpenMode.Hud)] [TestCase(UIOpenMode.Toast)]
        public void ExplicitNullArgumentsDoNotBecomeUINone(UIOpenMode mode)
        {
            var panel = Prepare(mode);
            int opens = 0;
            panel.OpenAction = () => opens++;
            Assert.Throws<InvalidOperationException>(() =>
                manager.Open<ExtensionPanel, UINone>(mode, null).GetAwaiter().GetResult());
            Assert.AreEqual(0, opens);
            Assert.IsTrue(panel.DestroyDispatched);
        }

        [TestCase(UIOpenMode.Hud)] [TestCase(UIOpenMode.Toast)]
        public void PanelShownRunsAfterOpenWithCanvasAvailable(UIOpenMode mode)
        {
            var panel = Prepare(mode);
            bool opened = false;
            int shownCount = 0;
            panel.OpenAction = () => opened = true;
            panelShown = shown =>
            {
                Assert.IsTrue(opened);
                Assert.AreSame(panel, shown);
                Assert.IsTrue(shown.gameObject.activeInHierarchy);
                Assert.IsTrue(shown.transform.IsChildOf(manager.CanvasRoot));
                shownCount++;
            };
            GameUI.PanelShown += panelShown;
            Assert.AreSame(panel, manager.Open<ExtensionPanel, UINone>(mode, UINone.Value).GetAwaiter().GetResult());
            Assert.AreEqual(1, shownCount);
        }

        [TestCase(UIOpenMode.Hud)] [TestCase(UIOpenMode.Toast)]
        public void PanelShownFailurePropagatesAndDoesNotLeaveCachedPanel(UIOpenMode mode)
        {
            var panel = Prepare(mode);
            var failure = new InvalidOperationException("panel-shown-primary");
            panelShown = _ => throw failure;
            GameUI.PanelShown += panelShown;
            Assert.AreSame(failure, Assert.Throws<InvalidOperationException>(() =>
                manager.Open<ExtensionPanel, UINone>(mode, UINone.Value).GetAwaiter().GetResult()));
            Assert.IsTrue(panel.DestroyDispatched);
            Assert.IsFalse(Field<Dictionary<Type, UIPanel>>("_cached").ContainsKey(typeof(ExtensionPanel)));
            Assert.IsFalse(Field<Dictionary<Type, List<UIPanel>>>("_toastIdle").ContainsKey(typeof(ExtensionPanel)));
        }

        [TestCase(UIOpenMode.Hud)] [TestCase(UIOpenMode.Toast)]
        public void PanelShownShutdownCancelsOpenWithoutLeavingPanel(UIOpenMode mode)
        {
            var panel = Prepare(mode);
            panelShown = _ => manager.Shutdown();
            GameUI.PanelShown += panelShown;
            Assert.Throws<OperationCanceledException>(() =>
                manager.Open<ExtensionPanel, UINone>(mode, UINone.Value).GetAwaiter().GetResult());
            Assert.IsFalse(manager.IsInited);
            Assert.IsTrue(panel.DestroyDispatched);
        }
    }
}
