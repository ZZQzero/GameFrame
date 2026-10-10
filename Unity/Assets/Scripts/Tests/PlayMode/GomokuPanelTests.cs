using System.Collections;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Game.AOT;
using Game.Hotfix;
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
    public sealed class GomokuPanelTests
    {
        ResourceLoadManager resources;
        GlobalConfig config;
        GomokuMainPanel panel;
        GomokuBoardView view;

        [UnitySetUp]
        public IEnumerator SetUp() => UniTask.ToCoroutine(async () =>
        {
            config = ScriptableObject.CreateInstance<GlobalConfig>();
            config.PlayMode = EPlayMode.EditorSimulateMode;
            resources = new ResourceLoadManager();
            await resources.InitializeAsync(config);
            await resources.UpdatePackageManifestAsync();
            GameUI.Init(resources.Package);
            GameUIRegistration.RegisterAll();
            panel = await GameUI.Push<GomokuMainPanel>();
            view = panel.GetComponentInChildren<GomokuBoardView>();
            await UniTask.NextFrame();
            Canvas.ForceUpdateCanvases();
        });

        [UnityTearDown]
        public IEnumerator TearDown() => UniTask.ToCoroutine(async () =>
        {
            GameUI.Shutdown();
            if (resources != null)
            {
                await resources.ShutdownAsync();
            }

            Object.Destroy(config);
            await UniTask.NextFrame();
        });

        [UnityTest]
        public IEnumerator PointerInputUndoRestartAndCacheReopenKeepStateConsistent() => UniTask.ToCoroutine(async () =>
        {
            Click(0, 0, PointerEventData.InputButton.Right);
            Assert.AreEqual(0, panel.Game.Moves.Count);
            Click(0, 0, PointerEventData.InputButton.Left);
            Click(1, 0, PointerEventData.InputButton.Left);
            Click(1, 0, PointerEventData.InputButton.Left);
            Assert.AreEqual(2, panel.Game.Moves.Count);
            Assert.AreEqual(2, VisiblePieces());
            Assert.AreEqual(GomokuStone.Black, panel.Game.CurrentTurn);
            Assert.IsFalse(view.TryGetIntersection(view.Grid.GridRect.max + Vector2.one * view.Grid.CellSize, out _));

            Button("UndoButton").onClick.Invoke();
            Assert.AreEqual(1, panel.Game.Moves.Count);
            Assert.AreEqual(1, VisiblePieces());
            Assert.AreEqual(GomokuStone.White, panel.Game.CurrentTurn);

            GameUI.Close<GomokuMainPanel>();
            Assert.IsFalse(view.Interactable);
            var reopened = await GameUI.Push<GomokuMainPanel>();
            Assert.AreSame(panel, reopened);
            Click(2, 0, PointerEventData.InputButton.Left);
            Assert.AreEqual(2, panel.Game.Moves.Count, "缓存重开不能重复订阅点击。");

            Button("RestartButton").onClick.Invoke();
            Assert.AreEqual(0, panel.Game.Moves.Count);
            Assert.AreEqual(0, VisiblePieces());
            Assert.AreEqual(GomokuStone.Black, panel.Game.CurrentTurn);
            Assert.IsFalse(Button("UndoButton").interactable);
        });

        [UnityTest]
        public IEnumerator SizeSwitchAndAreaResizeAlignPiecesAndHitCoordinates()
        {
            foreach (var size in new[] { 18, 20 })
            {
                panel.StartNewGame(size);
                Click(size - 1, size - 1, PointerEventData.InputButton.Left);
                Assert.AreEqual(GomokuStone.Black, panel.Game.GetStone(size - 1, size - 1));
                Assert.AreEqual(1, VisiblePieces());
                Assert.AreEqual(size, view.Grid.PointCount);
            }

            var board = (RectTransform)view.Grid.transform.parent;
            Assert.AreEqual(new Vector2(0.5f, 0.5f), board.anchorMin);
            Assert.AreEqual(board.anchorMin, board.anchorMax);
            var area = (RectTransform)view.transform;
            Assert.AreSame(area, board.parent);
            Assert.AreEqual("BoardArea", area.name);
            area.anchorMin = new Vector2(0.5f, 0.5f);
            area.anchorMax = area.anchorMin;
            var root = board.Find("Pieces");
            var piece = root.Find("Piece").GetComponent<Image>();
            var marker = root.Find("LastMoveMarker");
            foreach (var available in new[] { new Vector2(480f, 700f), new Vector2(1400f, 1200f), new Vector2(700f, 360f) })
            {
                area.sizeDelta = available;
                yield return null;
                Canvas.ForceUpdateCanvases();
                var expectedSize = Mathf.Min(1000f, Mathf.Min(available.x, available.y));
                Assert.That(board.rect.width, Is.EqualTo(expectedSize).Within(0.01f));
                Assert.That(board.rect.height, Is.EqualTo(expectedSize).Within(0.01f));
                var expected = view.Grid.transform.TransformPoint(view.Grid.GetIntersectionLocalPosition(19, 19));
                Assert.That(Vector3.Distance(expected, piece.transform.position), Is.LessThan(0.01f));
                Assert.That(Vector3.Distance(expected, marker.position), Is.LessThan(0.01f));
                Assert.That(piece.rectTransform.rect.width, Is.EqualTo(view.Grid.CellSize * 0.85f).Within(0.01f));
            }

            Click(0, 0, PointerEventData.InputButton.Left);
            Assert.AreEqual(GomokuStone.White, panel.Game.GetStone(0, 0));

            var outside = board.TransformPoint(new Vector3(board.rect.xMax + 10f, 0f));
            var eventData = new PointerEventData(EventSystem.current)
            {
                position = RectTransformUtility.WorldToScreenPoint(GameUI.UICamera, outside)
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(eventData, hits);
            foreach (var hit in hits)
            {
                Assert.AreNotSame(view.gameObject, ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject),
                    "BoardArea 中棋盘外的空白不能命中棋盘点击处理器。");
            }
        }

        [UnityTest]
        public IEnumerator WinningMoveStopsFurtherPlayAndUndoRestoresTurn()
        {
            for (var index = 0; index < 5; index++)
            {
                panel.ApplyMove(index, 0, GomokuStone.Black);
                if (index < 4)
                {
                    panel.ApplyMove(index * 2, 10, GomokuStone.White);
                }
            }

            Assert.AreEqual(GomokuGameState.BlackWon, panel.Game.State);
            var text = panel.transform.Find("GomokuBg/Content/Status").GetComponent<TextMeshProUGUI>();
            StringAssert.Contains("黑方获胜", text.text);
            Click(14, 14, PointerEventData.InputButton.Left);
            Assert.AreEqual(9, panel.Game.Moves.Count);
            Assert.AreEqual(9, VisiblePieces());
            Button("UndoButton").onClick.Invoke();
            Assert.AreEqual(GomokuGameState.Playing, panel.Game.State);
            Assert.AreEqual(GomokuStone.Black, panel.Game.CurrentTurn);
            Click(4, 0, PointerEventData.InputButton.Left);
            Assert.AreEqual(GomokuGameState.BlackWon, panel.Game.State);
            yield return null;
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator InspectorChangesRefreshBoardAndPieceSizesWithoutAnotherMove()
        {
            Click(14, 14, PointerEventData.InputButton.Left);
            var serialized = new UnityEditor.SerializedObject(view);
            serialized.FindProperty("maximumBoardSize").floatValue = 225f;
            serialized.FindProperty("pieceSizeRatio").floatValue = 0.6f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            yield return new WaitForSeconds(0.05f);

            var board = (RectTransform)view.Grid.transform.parent;
            Assert.That(board.rect.width, Is.EqualTo(225f).Within(0.01f));
            Assert.That(board.rect.height, Is.EqualTo(225f).Within(0.01f));
            var piece = board.Find("Pieces/Piece").GetComponent<Image>();
            Assert.That(piece.rectTransform.rect.width, Is.EqualTo(view.Grid.CellSize * 0.6f).Within(0.01f));
            var expected = view.Grid.transform.TransformPoint(view.Grid.GetIntersectionLocalPosition(14, 14));
            Assert.That(Vector3.Distance(expected, piece.transform.position), Is.LessThan(0.01f));
            Assert.AreEqual(1, panel.Game.Moves.Count);
        }
#endif

        void Click(int column, int row, PointerEventData.InputButton button)
        {
            var world = view.Grid.transform.TransformPoint(view.Grid.GetIntersectionLocalPosition(column, row));
            var eventData = new PointerEventData(EventSystem.current)
            {
                button = button,
                position = RectTransformUtility.WorldToScreenPoint(GameUI.UICamera, world)
            };
            var hits = new List<RaycastResult>();
            EventSystem.current.RaycastAll(eventData, hits);
            Assert.IsNotEmpty(hits, "交点必须命中棋盘。");
            Assert.AreSame(view.gameObject, ExecuteEvents.GetEventHandler<IPointerClickHandler>(hits[0].gameObject));
            eventData.pointerPressRaycast = hits[0];
            ExecuteEvents.ExecuteHierarchy(hits[0].gameObject, eventData, ExecuteEvents.pointerClickHandler);
        }

        Button Button(string name)
        {
            return panel.transform.Find("GomokuBg/Content/" + name).GetComponent<Button>();
        }

        int VisiblePieces()
        {
            var root = view.Grid.transform.parent.Find("Pieces");
            var count = 0;
            for (var index = 0; index < root.childCount; index++)
            {
                if (root.GetChild(index).name == "Piece" && root.GetChild(index).gameObject.activeSelf)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
