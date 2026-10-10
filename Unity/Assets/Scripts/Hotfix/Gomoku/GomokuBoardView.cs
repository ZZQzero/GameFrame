using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Game.Hotfix
{
    /// <summary>棋盘显示和点击转换。挂在 Checkerboard，规则和回合由棋局决定。</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public sealed class GomokuBoardView : UIBehaviour, IPointerClickHandler
    {
        [SerializeField] GomokuBoardGraphic grid;
        [SerializeField] RectTransform piecesRoot;
        [SerializeField] Image lastMoveMarker;
        [SerializeField] Sprite blackPiece;
        [SerializeField] Sprite whitePiece;
        [SerializeField, Min(1f)] float maximumBoardSize = 1000f;
        [SerializeField, Range(0.1f, 1f)] float pieceSizeRatio = 0.85f;

        readonly List<Image> pieces = new List<Image>();
        GomokuGame displayedGame;
        bool refreshingLayout;

        public GomokuBoardGraphic Grid => grid;
        public bool Interactable { get; set; }
        public event Action<int, int> IntersectionClicked;

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!Interactable || eventData.button != PointerEventData.InputButton.Left)
            {
                return;
            }

            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                grid.rectTransform, eventData.position, eventData.pressEventCamera, out var localPoint)
                && TryGetIntersection(localPoint, out var intersection))
            {
                IntersectionClicked?.Invoke(intersection.x, intersection.y);
            }
        }

        /// <summary>使用 Grid 局部坐标取最近交点，允许最外圈线之外半格的点击。</summary>
        public bool TryGetIntersection(Vector2 localPoint, out Vector2Int intersection)
        {
            intersection = default;
            var rect = grid.GridRect;
            if (rect.width <= 0f)
            {
                return false;
            }

            var spacing = grid.CellSize;
            var column = Mathf.RoundToInt((localPoint.x - rect.xMin) / spacing);
            var row = Mathf.RoundToInt((localPoint.y - rect.yMin) / spacing);
            if (column < 0 || column >= grid.PointCount || row < 0 || row >= grid.PointCount)
            {
                return false;
            }

            intersection = new Vector2Int(column, row);
            return true;
        }

        /// <summary>从完整棋局恢复显示，也用于落子、悔棋和重开。已创建的棋子在面板内复用。</summary>
        public void Render(GomokuGame game)
        {
            if (game == null)
            {
                throw new ArgumentNullException(nameof(game));
            }

            if (grid == null || piecesRoot == null || lastMoveMarker == null
                || blackPiece == null || whitePiece == null)
            {
                throw new InvalidOperationException("[Gomoku] 棋盘显示组件或黑白棋子资源未绑定。");
            }

            if (game.Size != grid.PointCount)
            {
                throw new InvalidOperationException("[Gomoku] 棋局与网格规格不一致，请通过面板 StartNewGame 切换规格。");
            }

            displayedGame = game;
            while (pieces.Count < game.Moves.Count)
            {
                var pieceObject = new GameObject("Piece", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                pieceObject.layer = piecesRoot.gameObject.layer;
                pieceObject.transform.SetParent(piecesRoot, false);
                var image = pieceObject.GetComponent<Image>();
                image.raycastTarget = false;
                image.preserveAspect = true;
                pieces.Add(image);
            }

            for (var index = 0; index < pieces.Count; index++)
            {
                var active = index < game.Moves.Count;
                pieces[index].gameObject.SetActive(active);
                if (active)
                {
                    pieces[index].sprite = game.Moves[index].Stone == GomokuStone.Black ? blackPiece : whitePiece;
                }
            }

            lastMoveMarker.gameObject.SetActive(game.Moves.Count > 0);
            lastMoveMarker.rectTransform.SetAsLastSibling();
            RefreshLayout();
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            RefreshLayout();
        }

        protected override void OnRectTransformDimensionsChange()
        {
            base.OnRectTransformDimensionsChange();
            RefreshLayout();
        }

        void RefreshLayout()
        {
            // 添加组件和 prefab 回填引用期间也会收到尺寸回调。
            if (refreshingLayout || grid == null || piecesRoot == null || transform.parent is not RectTransform parent)
            {
                return;
            }

            refreshingLayout = true;
            try
            {
                var board = (RectTransform)transform;
                // 锚点定义父级中的可用区域；sizeDelta 将实际矩形收缩为正方形。
                var available = Vector2.Scale(parent.rect.size, board.anchorMax - board.anchorMin);
                var size = Mathf.Max(0f, Mathf.Min(maximumBoardSize, Mathf.Min(available.x, available.y)));
                board.sizeDelta = Vector2.one * size - available;
                if (displayedGame == null || grid.rectTransform.rect.width <= 0f)
                {
                    return;
                }

                for (var index = 0; index < displayedGame.Moves.Count; index++)
                {
                    PlaceImage(pieces[index], displayedGame.Moves[index], grid.CellSize * pieceSizeRatio);
                }

                if (displayedGame.Moves.Count > 0)
                {
                    PlaceImage(lastMoveMarker, displayedGame.Moves[displayedGame.Moves.Count - 1], grid.CellSize * 0.16f);
                }
            }
            finally
            {
                // 设置自身尺寸会同步触发尺寸回调，避免重入并确保异常后恢复。
                refreshingLayout = false;
            }
        }

        void PlaceImage(Image image, GomokuMove move, float diameter)
        {
            // Pieces 与 Grid 都铺满 Checkerboard；比例锚点在父尺寸变化时保持交点对齐。
            var rect = grid.rectTransform.rect;
            var point = grid.GetIntersectionLocalPosition(move.Column, move.Row);
            var anchor = new Vector2((point.x - rect.xMin) / rect.width, (point.y - rect.yMin) / rect.height);
            var pieceRect = image.rectTransform;
            pieceRect.anchorMin = anchor;
            pieceRect.anchorMax = anchor;
            pieceRect.pivot = new Vector2(0.5f, 0.5f);
            pieceRect.anchoredPosition = Vector2.zero;
            pieceRect.sizeDelta = Vector2.one * diameter;
        }
    }
}
