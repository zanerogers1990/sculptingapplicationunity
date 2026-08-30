using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// Draws the in-progress box/lasso marquee for RegionSelectTool as a single uGUI mesh.
    ///
    /// A Graphic subclass rather than the Image-per-element approach the brush cursor and the
    /// Lazy Mouse tether use: a lasso is an arbitrary polyline of a hundred-odd points, which
    /// would mean a hundred GameObjects created and destroyed as the drag grows. One mesh
    /// rebuilt per frame of the drag costs nothing by comparison and is far simpler to keep in
    /// step with the path.
    ///
    /// Its RectTransform is stretched over the whole canvas with a (0,0) pivot, so local
    /// coordinates ARE screen pixels and the tool's screen-space points can be used verbatim
    /// with no conversion.
    ///
    /// RequireComponent is restated here even though the base Graphic already declares it:
    /// building this via `new GameObject(name, typeof(RegionMarqueeGraphic))` does NOT pick up
    /// the base class's attribute, so the object came up with no CanvasRenderer and drew
    /// absolutely nothing - silently, since nothing throws until something asks the renderer
    /// for a material.
    [RequireComponent(typeof(CanvasRenderer))]
    public class RegionMarqueeGraphic : Graphic
    {
        // Deliberately thin and translucent: this is a transient gesture affordance drawn
        // straight over the model the user is looking at, and anything heavier competes with
        // the shape they are trying to trace around.
        private const float OutlineThicknessPx = 1.5f;
        private const float FillAlpha = 0.1f;

        private bool _isLasso;
        private Rect _box;
        private readonly List<Vector2> _path = new List<Vector2>();

        /// Feeds this frame's box. Marks the mesh dirty only when something actually changed,
        /// so a held-still drag doesn't rebuild the mesh every frame.
        public void SetBox(Rect box)
        {
            if (!_isLasso && _box == box) return;
            _isLasso = false;
            _box = box;
            SetVerticesDirty();
        }

        /// Feeds this frame's lasso path (screen pixels, implicitly closed - see
        /// RegionSelectTool's rasterizer, which closes it the same way).
        public void SetPath(IReadOnlyList<Vector2> points)
        {
            if (_isLasso && SamePath(points)) return;
            _isLasso = true;
            _path.Clear();
            if (points != null)
                for (int i = 0; i < points.Count; i++) _path.Add(points[i]);
            SetVerticesDirty();
        }

        private bool SamePath(IReadOnlyList<Vector2> points)
        {
            int count = points?.Count ?? 0;
            if (count != _path.Count) return false;
            // Only the tail can have moved: points are appended, never edited (the live cursor
            // end is appended too, once it clears the spacing threshold).
            return count == 0 || _path[count - 1] == points[count - 1];
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            Color32 line = color;
            Color32 fill = new Color(color.r, color.g, color.b, color.a * FillAlpha);

            if (_isLasso)
            {
                for (int i = 1; i < _path.Count; i++) AddSegment(vh, _path[i - 1], _path[i], line);
                // The closing segment back to the start, drawn like any other: it is part of the
                // region the release will actually select, so leaving it out would misrepresent
                // the shape at exactly the moment the user is deciding whether to let go.
                if (_path.Count > 2) AddSegment(vh, _path[_path.Count - 1], _path[0], line);
                return;
            }

            var bl = new Vector2(_box.xMin, _box.yMin);
            var br = new Vector2(_box.xMax, _box.yMin);
            var tr = new Vector2(_box.xMax, _box.yMax);
            var tl = new Vector2(_box.xMin, _box.yMax);

            AddQuad(vh, bl, br, tr, tl, fill);
            AddSegment(vh, bl, br, line);
            AddSegment(vh, br, tr, line);
            AddSegment(vh, tr, tl, line);
            AddSegment(vh, tl, bl, line);
        }

        // One segment as a thickness-wide quad along its own perpendicular. No line caps: at
        // 1.5px the joins between segments of a smooth lasso path are invisible, and mitring
        // them would be a lot of geometry for something on screen for under a second.
        private static void AddSegment(VertexHelper vh, Vector2 a, Vector2 b, Color32 tint)
        {
            Vector2 dir = b - a;
            float length = dir.magnitude;
            if (length < 0.001f) return;

            Vector2 normal = new Vector2(-dir.y, dir.x) / length * (OutlineThicknessPx * 0.5f);
            AddQuad(vh, a - normal, b - normal, b + normal, a + normal, tint);
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
