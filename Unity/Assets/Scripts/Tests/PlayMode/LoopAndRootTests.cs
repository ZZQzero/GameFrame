using GameFrame.UI;
using GameFrame.Display;
using System;
using System.Collections;
using Cysharp.Threading.Tasks;
using GameFrame.Pooling;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using UnityEngine.UI;

namespace GameFrame.Tests
{
    public class LoopAndRootTests
    {
        sealed class CellSource : LoopScrollPrefabSource, LoopScrollDataSource, LoopScrollMultiDataSource
        {
            public int Created;
            public float Size;
            public GameObject GetObject(int index)
            {
                Created++;
                var cell = new GameObject("Cell-" + index, typeof(RectTransform));
                ((RectTransform)cell.transform).sizeDelta = new Vector2(Size, Size);
                return cell;
            }
            public void ReturnObject(Transform trans) => UnityEngine.Object.Destroy(trans.gameObject);
            public void ProvideData(Transform transform, int index) { }
        }

        sealed class AddressProvider : IPrefabProvider, IPrefabHandle
        {
            public string LoadedLocation;
            public UniTask<IPrefabHandle> LoadAsync(string location)
            {
                LoadedLocation = location;
                return UniTask.FromResult<IPrefabHandle>(this);
            }
            public GameObject Instantiate(Transform parent)
            {
                var cell = new GameObject("address-cell", typeof(RectTransform));
                cell.transform.SetParent(parent, false);
                return cell;
            }
            public void Dispose() { }
        }

        [TestCase(false, "cell")] [TestCase(true, "cell")]
        [TestCase(false, " cell ")] [TestCase(true, " cell ")]
        public void PreparedCellAddressIsUsedWithoutRewriting(bool prewarm, string location)
        {
            var provider = new AddressProvider();
            using var pool = new GameObjectPoolService(provider);
            var source = new LoopScrollPoolSource();
            source.SetPool(pool);
            if (prewarm) source.PrewarmLocationsAsync(new[] { location }, 1).GetAwaiter().GetResult();
            else source.PrepareLocationsAsync(new[] { location }).GetAwaiter().GetResult();
            Assert.AreEqual(location, provider.LoadedLocation);
            source.SetLocation(location);
            var cell = source.GetObject(0);
            source.ReturnObject(cell.transform);
            source.SetLocation(_ => location);
            Assert.AreSame(cell, source.GetObject(0));
            source.ReturnObject(cell.transform);
            Assert.AreEqual(1, pool.PoolCount);
            Assert.Throws<ArgumentException>(() => source.SetLocation(" "));
        }

        [Test] public void FixedScrollbarSizeRejectsInvalidValuesWithoutChangingState()
        {
            var root = new GameObject("scrollbar-test", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                var scroll = root.AddComponent<LoopVerticalScrollRect>();
                foreach (float valid in new[] { 0f, 0.25f, 1f })
                {
                    scroll.fixedHorizontalScrollbarSize = valid;
                    scroll.fixedVerticalScrollbarSize = valid;
                    foreach (float invalid in new[] { -1f, 2f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                    {
                        Assert.Throws<ArgumentOutOfRangeException>(() => scroll.fixedHorizontalScrollbarSize = invalid);
                        Assert.Throws<ArgumentOutOfRangeException>(() => scroll.fixedVerticalScrollbarSize = invalid);
                        Assert.AreEqual(valid, scroll.fixedHorizontalScrollbarSize);
                        Assert.AreEqual(valid, scroll.fixedVerticalScrollbarSize);
                    }
                }
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [UnityTest] public IEnumerator InvalidScrollRequestThrowsBeforeStoppingExistingCoroutines()
        {
            var root = new GameObject("scroll-request-test", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                ((RectTransform)root.transform).sizeDelta = new Vector2(100f, 100f);
                var scroll = root.AddComponent<LoopVerticalScrollRect>();
                scroll.horizontal = false;
                var content = new GameObject("content", typeof(RectTransform));
                content.transform.SetParent(root.transform, false);
                scroll.content = (RectTransform)content.transform;
                scroll.content.anchorMin = new Vector2(0, 1);
                scroll.content.anchorMax = Vector2.one;
                scroll.content.pivot = new Vector2(0.5f, 1);
                var source = new CellSource { Size = 20f };
                scroll.prefabSource = source;
                scroll.dataSource = source;
                scroll.totalCount = 20;
                root.SetActive(true);
                scroll.RefillCells();
                bool continued = false;
                IEnumerator ExistingWork() { yield return null; continued = true; }
                scroll.StartCoroutine(ExistingWork());
                foreach (int index in new[] { -1, 20 })
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => scroll.ScrollToCell(index, 1));
                    Assert.Throws<ArgumentOutOfRangeException>(() => scroll.ScrollToCellWithinTime(index, 1));
                }
                foreach (float invalid in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                {
                    Assert.Throws<ArgumentOutOfRangeException>(() => scroll.ScrollToCell(0, invalid));
                    Assert.Throws<ArgumentOutOfRangeException>(() => scroll.ScrollToCellWithinTime(0, invalid));
                }
                Assert.Throws<ArgumentException>(() => scroll.ScrollToCellWithinTime(
                    0, 1, mode: LoopScrollRectBase.ScrollMode.JustAppear));
                yield return null;
                yield return null;
                Assert.IsTrue(continued);
                Assert.DoesNotThrow(() => scroll.ScrollToCell(0, 1));
                Assert.DoesNotThrow(() => scroll.ScrollToCellWithinTime(0, 1));
                scroll.StopAllCoroutines();
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [TestCase(typeof(LoopVerticalScrollRect))]
        [TestCase(typeof(LoopVerticalScrollRectMulti))]
        [TestCase(typeof(LoopHorizontalScrollRect))]
        [TestCase(typeof(LoopHorizontalScrollRectMulti))]
        public void InfiniteRefillRejectsZeroCellAfterFirstAllocation(Type type)
        {
            var root = new GameObject("loop-test", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                ((RectTransform)root.transform).sizeDelta = new Vector2(100f, 100f);
                var scroll = (LoopScrollRectBase)root.AddComponent(type);
                var content = new GameObject("content", typeof(RectTransform));
                content.transform.SetParent(root.transform, false);
                scroll.content = (RectTransform)content.transform;
                var source = new CellSource();
                scroll.prefabSource = source;
                if (scroll is LoopScrollRect single) single.dataSource = source;
                if (scroll is LoopScrollRectMulti multi) multi.dataSource = source;
                scroll.totalCount = -1;
                Assert.Throws<InvalidOperationException>(() => scroll.RefillCells());
                Assert.AreEqual(1, source.Created, "非法尺寸不能继续分配 Cell");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [Test] public void InvalidGridCannotBeSkippedOnSecondRefill()
        {
            var root = new GameObject("grid-test", typeof(RectTransform));
            root.SetActive(false);
            try
            {
                var scroll = root.AddComponent<LoopVerticalScrollRect>();
                var content = new GameObject("content", typeof(RectTransform));
                content.transform.SetParent(root.transform, false);
                var grid = content.AddComponent<GridLayoutGroup>();
                grid.constraint = GridLayoutGroup.Constraint.Flexible;
                scroll.content = (RectTransform)content.transform;
                Assert.Throws<InvalidOperationException>(() => scroll.RefillCells());
                Assert.Throws<InvalidOperationException>(() => scroll.RefillCells());
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        [UnityTest] public IEnumerator FailedRootBuildReleasesObjectsAndAllowsExplicitRestart()
        {
            var primary = new InvalidOperationException("root-build-failure");
            Action<GameScreenOrientation> failLayout = _ => throw primary;
            ScreenOrientationManager.Shutdown();
            ScreenOrientationManager.CanvasLayoutChanged += failLayout;
            try
            {
                int before = UnityEngine.Object.FindObjectsByType<UIFrameRoot>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
                Assert.AreSame(primary, Assert.Throws<InvalidOperationException>(() => GameUI.Init()));
                Assert.IsFalse(GameUI.IsInited);
                Assert.IsEmpty(UnityEngine.Object.FindObjectsByType<EventSystem>(FindObjectsSortMode.None),
                    "失败根节点应立即停用，不能继续处理输入");
                yield return null;
                Assert.AreEqual(before, UnityEngine.Object.FindObjectsByType<UIFrameRoot>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                    "管理器尚未接管的根节点也必须被销毁");
                Assert.IsEmpty(UnityEngine.Object.FindObjectsByType<EventSystem>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None));

                ScreenOrientationManager.CanvasLayoutChanged -= failLayout;
                ScreenOrientationManager.Shutdown();
                GameUI.Init();
                Assert.IsTrue(GameUI.IsInited);
            }
            finally
            {
                ScreenOrientationManager.CanvasLayoutChanged -= failLayout;
                GameUI.Shutdown();
                ScreenOrientationManager.Shutdown();
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator CameraStackIsConfiguredByCallerAndReinitializationDoesNotLeaveDuplicates()
        {
            var cameraObject = new GameObject("camera-stack-test") { tag = "MainCamera" };
            var baseCamera = cameraObject.AddComponent<Camera>();
            var baseData = baseCamera.GetUniversalAdditionalCameraData();
            baseData.renderType = CameraRenderType.Base;
            var overlayObject = new GameObject("existing-overlay-test");
            var existingOverlay = overlayObject.AddComponent<Camera>();
            existingOverlay.GetUniversalAdditionalCameraData().renderType = CameraRenderType.Overlay;
            var stack = baseData.cameraStack;
            stack.Add(existingOverlay);
            var originalMask = baseCamera.cullingMask;
            Action rootReady = () =>
            {
                CollectionAssert.AreEqual(new[] { existingOverlay }, stack, "UI 根节点不应替调用方选择主相机。");
                Assert.AreSame(GameUI.UICamera, GameUI.CanvasRoot.GetComponent<Canvas>().worldCamera);
            };
            GameUI.RootReady += rootReady;
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    GameUI.Init();
                    var uiCamera = GameUI.UICamera;
                    CollectionAssert.AreEqual(new[] { existingOverlay }, stack);
                    GameUI.ConfigureURPCameraStack(baseCamera);
                    Assert.AreEqual(CameraRenderType.Overlay, uiCamera.GetUniversalAdditionalCameraData().renderType);
                    CollectionAssert.AreEqual(new[] { existingOverlay, uiCamera }, stack);
                    Assert.AreEqual(originalMask, baseCamera.cullingMask);
                    Assert.Throws<InvalidOperationException>(() => GameUI.Init());
                    GameUI.ConfigureURPCameraStack();
                    CollectionAssert.AreEqual(new[] { existingOverlay, uiCamera }, stack);

                    GameUI.Shutdown();
                    yield return null;
                    CollectionAssert.AreEqual(new[] { existingOverlay }, stack);
                    Assert.IsTrue(uiCamera == null);
                }
            }
            finally
            {
                GameUI.RootReady -= rootReady;
                GameUI.Shutdown();
                UnityEngine.Object.Destroy(cameraObject);
                UnityEngine.Object.Destroy(overlayObject);
            }
            yield return null;
        }

        [UnityTest] public IEnumerator ExistingEventSystemIsRejectedBeforeCreatingRoot()
        {
            var existing = new GameObject("external-event-system");
            existing.AddComponent<EventSystem>();
            try
            {
                int before = UnityEngine.Object.FindObjectsByType<UIFrameRoot>(FindObjectsSortMode.None).Length;
                Assert.Throws<InvalidOperationException>(() => UIFrameRoot.Create());
                Assert.AreEqual(before, UnityEngine.Object.FindObjectsByType<UIFrameRoot>(FindObjectsSortMode.None).Length);
                Assert.IsNotNull(existing.GetComponent<EventSystem>());
            }
            finally { UnityEngine.Object.Destroy(existing); }
            yield return null;
        }
    }
}
