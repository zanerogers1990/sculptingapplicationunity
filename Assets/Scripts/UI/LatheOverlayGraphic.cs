using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// Screen-space drawing for the lathe tool: the axis, the profile curve on both silhouettes,
    /// and the control-point handles. One uGUI mesh, rebuilt when LatheController hands it a new
    /// frame of primitives.
    ///
    /// Drawn in screen space rather than as 3D line meshes so it stays crisp and constant-width at
    /// any zoom, always sits on top of the preview it annotates (the silhouette handles would
    /// otherwise z-fight with the very surface they shape), and needs no materials.
    ///
    /// Stretched over its canvas with a (0,0) pivot, so local coordinates ARE screen pixels - the
    /// same arrangement as RegionMarqueeGraphic, whose remarks also explain why RequireComponent
    /// is restated here.
    [RequireComponent(typeof(CanvasRenderer))]
    public class LatheOverlayGraphic : Graphic
    {
        private struct Line { public Vector2 A, B; public float Width; public Color32 Color; }
        private struct Dot { public Vector2 Center; public float Radius; public Color32 Fill; public Color32 Outline; public bool Square; }

        private readonly List<Line> _lines = new List<Line>();
        private readonly List<Dot> _dots = new List<Dot>();

        private const int DiscSides = 16;

        public void Clear()
        {
            _lines.Clear();
            _dots.Clear();
        }

        public void AddLine(Vector2 a, Vector2 b, float width, Color color) =>
            _lines.Add(new Line { A = a, B = b, Width = width, Color = color });

        /// A polyline through `points`, optionally closed.
        public void AddPolyline(IReadOnlyList<Vector2> points, float width, Color color, bool closed = false)
        {
            if (points == null) return;
            for (int i = 1; i < points.Count; i++) AddLine(points[i - 1], points[i], width, color);
            if (closed && points.Count > 2) AddLine(points[points.Count - 1], points[0], width, color);
        }

        /// A dashed line - the axis, which is a reference, not something the artist shapes.
        public void AddDashedLine(Vector2 a, Vector2 b, float width, Color color, float dash, float gap)
        {
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 1e-3f) return;
            d /= len;
            for (float s = 0f; s < len; s += dash + gap)
                AddLine(a + d * s, a + d * Mathf.Min(s + dash, len), width, color);
        }

        public void AddDot(Vector2 center, float radius, Color fill, Color outline, bool square = false) =>
            _dots.Add(new Dot { Center = center, Radius = radius, Fill = fill, Outline = outline, Square = square });

        /// Pushes this frame's primitives to the canvas.
        public void Commit() => SetVerticesDirty();

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            for (int i = 0; i < _lines.Count; i++)
            {
                Line l = _lines[i];
                AddSegment(vh, l.A, l.B, l.Width, l.Color);
            }
            // Dots after lines, so a handle sits over the curve it lies on.
            for (int i = 0; i < _dots.Count; i++)
            {
                Dot d = _dots[i];
                if (d.Square)
                {
                    AddSquare(vh, d.Center, d.Radius + 1.5f, d.Outline);
                    AddSquare(vh, d.Center, d.Radius, d.Fill);
                }
                else
                {
                    AddDisc(vh, d.Center, d.Radius + 1.5f, d.Outline);
                    AddDisc(vh, d.Center, d.Radius, d.Fill);
                }
            }
        }

        private static void AddSegment(VertexHelper vh, Vector2 a, Vector2 b, float width, Color32 tint)
        {
            Vector2 dir = b - a;
            float length = dir.magnitude;
            if (length < 0.001f) return;
            Vector2 normal = new Vector2(-dir.y, dir.x) / length * (width * 0.5f);
            // Extended by half a width at each end so consecutive segments of a polyline overlap
            // at the joins instead of leaving a notch on the outside of every bend.
            Vector2 along = dir / length * (width * 0.5f);
            AddQuad(vh, a - along - normal, b + along - normal, b + along + normal, a - along + normal, tint);
        }

        private static void AddDisc(VertexHelper vh, Vector2 c, float radius, Color32 tint)
        {
            int center = vh.currentVertCount;
            vh.AddVert(c, tint, Vector2.zero);
            for (int i = 0; i < DiscSides; i++)
            {
                float a = i * Mathf.PI * 2f / DiscSides;
                vh.AddVert(c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, tint, Vector2.zero);
            }
            for (int i = 0; i < DiscSides; i++)
                vh.AddTriangle(center, center + 1 + (i + 1) % DiscSides, center + 1 + i);
        }

        private static void AddSquare(VertexHelper vh, Vector2 c, float half, Color32 tint)
        {
            AddQuad(vh, c + new Vector2(-half, -half), c + new Vector2(half, -half),
                    c + new Vector2(half, half), c + new Vector2(-half, half), tint);
        }

        private static void AddQuad(VertexHelper vh, Vector2 v0, Vector2 v1, Vector2 v2, Vector2 v3, Color32 tint)
        {
            int start = vh.currentVertCount;
            vh.AddVert(v0, tint, Vector2.zero);
            vh.AddVert(v1, tint, Vector2.zero);
            vh.AddVert(v2, tint, Vector2.zero);
            vh.AddVert(v3, tint, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start + 2, start + 3, start);
        }
    }
}
