using System;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Hotfix
{
    /// <summary>按棋盘局部尺寸绘制网格；行列从左下角开始，以交点计数。</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    [AddComponentMenu("Game/Gomoku/Board Graphic")]
    public sealed class GomokuBoardGraphic : MaskableGraphic
    {
        const int StarSegments = 20;

        [SerializeField, Min(2)]
        [Tooltip("每个方向的交点数量，15 表示 15 条线、14 个间隔。")]
        int pointCount = 15;

        [SerializeField, Range(0f, 0.45f)]
        [Tooltip("每侧留白占棋盘较短边的比例。")]
        float paddingRatio = 0.05f;

        [SerializeField, Min(0.1f)]
        float lineWidth = 2f;

        [SerializeField, Min(0.1f)]
        float borderWidth = 3f;

        [SerializeField]
        bool showStarPoints = true;

        [SerializeField, Range(0.01f, 0.25f)]
        float starRadiusRatio = 0.08f;

        public int PointCount => pointCount;

        public Rect GridRect
        {
            get
            {
                var rect = rectTransform.rect;
                var size = Mathf.Min(rect.width, rect.height) * (1f - paddingRatio * 2f);
                return new Rect(rect.center - Vector2.one * size * 0.5f, Vector2.one * size);
            }
        }

        public float CellSize => GridRect.width / (pointCount - 1);

        public void SetPointCount(int count)
        {
            if (count < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "棋盘每个方向至少需要两个交点。");
            }

            if (pointCount == count)
            {
                return;
            }

            pointCount = count;
            SetVerticesDirty();
        }

        public Vector2 GetIntersectionLocalPosition(int column, int row)
        {
            if (column < 0 || column >= pointCount)
            {
                throw new ArgumentOutOfRangeException(nameof(column));
            }

            if (row < 0 || row >= pointCount)
            {
                throw new ArgumentOutOfRangeException(nameof(row));
            }

            var grid = GridRect;
            var spacing = grid.width / (pointCount - 1);
            return grid.min + new Vector2(column * spacing, row * spacing);
        }

        protected override void OnPopulateMesh(VertexHelper vertices)
        {
            vertices.Clear();
            var grid = GridRect;
            if (grid.width <= 0f)
            {
                return;
            }

            var spacing = grid.width / (pointCount - 1);
            for (var index = 0; index < pointCount; index++)
            {
                var isBorder = index == 0 || index == pointCount - 1;
                var width = isBorder ? borderWidth : lineWidth;
                var halfWidth = width * 0.5f;
                var x = grid.xMin + index * spacing;
                var y = grid.yMin + index * spacing;
                var horizontalExtension = isBorder ? borderWidth * 0.5f : 0f;

                AddQuad(vertices, x - halfWidth, grid.yMin, x + halfWidth, grid.yMax);
                AddQuad(vertices, grid.xMin - horizontalExtension, y - halfWidth,
                    grid.xMax + horizontalExtension, y + halfWidth);
            }

            if (!showStarPoints)
            {
                return;
            }

            var radius = spacing * starRadiusRatio;
            if (pointCount >= 9)
            {
                // 星位是装饰；偶数路棋盘不在格子中心绘制中央星位。
                const int inset = 3;
                var far = pointCount - 1 - inset;
                AddStar(vertices, GetIntersectionLocalPosition(inset, inset), radius);
                AddStar(vertices, GetIntersectionLocalPosition(inset, far), radius);
                AddStar(vertices, GetIntersectionLocalPosition(far, inset), radius);
                AddStar(vertices, GetIntersectionLocalPosition(far, far), radius);
            }

            if (pointCount % 2 != 0)
            {
                var center = pointCount / 2;
                AddStar(vertices, GetIntersectionLocalPosition(center, center), radius);
            }
        }

        void AddQuad(VertexHelper vertices, float left, float bottom, float right, float top)
        {
            var start = vertices.currentVertCount;
            vertices.AddVert(new Vector3(left, bottom), color, Vector2.zero);
            vertices.AddVert(new Vector3(left, top), color, Vector2.zero);
            vertices.AddVert(new Vector3(right, top), color, Vector2.zero);
            vertices.AddVert(new Vector3(right, bottom), color, Vector2.zero);
            vertices.AddTriangle(start, start + 1, start + 2);
            vertices.AddTriangle(start, start + 2, start + 3);
        }

        void AddStar(VertexHelper vertices, Vector2 center, float radius)
        {
            var start = vertices.currentVertCount;
            vertices.AddVert(center, color, Vector2.zero);
            for (var index = 0; index < StarSegments; index++)
            {
                var angle = -index * Mathf.PI * 2f / StarSegments;
                var position = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                vertices.AddVert(position, color, Vector2.zero);
            }

            for (var index = 0; index < StarSegments; index++)
            {
                vertices.AddTriangle(start, start + 1 + index, start + 1 + (index + 1) % StarSegments);
            }
        }
    }
}
