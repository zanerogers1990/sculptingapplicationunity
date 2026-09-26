using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sculpting
{
    /// The brush falloff curve editor: a small graph of weight (up) against distance from the brush
    /// centre (left = centre, right = edge), drawn as one uGUI mesh.
    ///
    /// Drag a point to move it; click empty space to add one; right-click a point to remove it.
    /// The two end points only move up and down - the curve always spans the whole radius. An
    /// interior point cannot be dragged past its neighbours, so the points never reorder under the
    /// cursor mid-drag.
    ///
    /// RequireComponent is restated for the reason RegionMarqueeGraphic gives: a Graphic built from
    /// code does not get a CanvasRenderer from the base class's attribute, and draws nothing.
    [RequireComponent(typeof(CanvasRenderer))]
    public class FalloffCurveGraphic : Graphic, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        private const float PointSizePx = 7f;
        private const float PickRadiusPx = 10f;
        private const float LineWidthPx = 2f;
        private const int CurveSegments = 64;

        private static readonly Color Background = new Color(0.05f, 0.05f, 0.07f, 1f);
        private static readonly Color Grid = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color CurveColor = new Color(0.35f, 0.65f, 1f, 1f);
        private static readonly Color PointColor = Color.white;
        private static readonly Color DisabledColor = new Color(1f, 1f, 1f, 0.2f);
        private static readonly Color ShiftedColor = new Color(1f, 0.8f, 0.35f, 0.7f);

        /// Where the curve being edited comes from - the controller's current brush, re-read every
        /// frame so switching brushes shows that brush's curve. Null means the built-in falloff.
        public Func<BrushFalloffCurve> Source;

        /// The current brush's Focal Shift (-1..1). While it is not 0 the graph also draws the
        /// curve the brush actually uses - the points stay where they were drawn, unshifted.
        public Func<float> FocalShiftSource;

        private float _shownFocal;

        private BrushFalloffCurve _shown;
        private int _shownVersion = -1;
        private int _dragIndex = -1;

        private void LateUpdate()
        {
            BrushFalloffCurve curve = Source?.Invoke();
            int version = curve?.Version ?? -1;
            float focal = FocalShiftSource?.Invoke() ?? 0f;
            if (curve == _shown && version == _shownVersion && focal == _shownFocal) return;
            _shownFocal = focal;
            // Our own edits bump Version every drag frame; only a different curve (brush switch)
            // should drop the drag.
            if (curve != _shown) _dragIndex = -1;
            _shown = curve;
            _shownVersion = version;
            SetVerticesDirty();
        }

        private Vector2 ToLocal(Vector2 curvePoint)
        {
            Rect r = rectTransform.rect;
            return new Vector2(r.xMin + curvePoint.x * r.width, r.yMin + curvePoint.y * r.height);
        }

        private bool ToCurve(PointerEventData e, out Vector2 curvePoint)
        {
            curvePoint = default;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, e.position, e.pressEventCamera, out Vector2 local))
                return false;
            Rect r = rectTransform.rect;
            curvePoint = new Vector2(Mathf.Clamp01((local.x - r.xMin) / r.width), Mathf.Clamp01((local.y - r.yMin) / r.height));
            return true;
        }

        private int PickPoint(BrushFalloffCurve curve, PointerEventData e)
        {
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform, e.position, e.pressEventCamera, out Vector2 local))
                return -1;
            int best = -1;
            float bestDist = PickRadiusPx;
            for (int i = 0; i < curve.Points.Count; i++)
            {
                float d = Vector2.Distance(ToLocal(curve.Points[i]), local);
                if (d <= bestDist) { bestDist = d; best = i; }
            }
            return best;
        }

        public void OnPointerDown(PointerEventData e)
        {
            BrushFalloffCurve curve = Source?.Invoke();
            if (curve == null || !ToCurve(e, out Vector2 at)) return;

            int picked = PickPoint(curve, e);
            if (e.button == PointerEventData.InputButton.Right)
            {
                if (picked > 0 && picked < curve.Points.Count - 1)
                {
                    curve.Points.RemoveAt(picked);
                    curve.Normalize();
                }
                return;
            }
            if (e.button != PointerEventData.InputButton.Left) return;

            if (picked < 0)
            {
                // Insert in order, so the new point is draggable straight away.
                int insert = 1;
                while (insert < curve.Points.Count - 1 && curve.Points[insert].x < at.x) insert++;
                curve.Points.Insert(insert, at);
                picked = insert;
                curve.Normalize();
            }
            _dragIndex = picked;
        }

        public void OnDrag(PointerEventData e)
        {
            BrushFalloffCurve curve = Source?.Invoke();
            if (curve == null || _dragIndex < 0 || _dragIndex >= curve.Points.Count || !ToCurve(e, out Vector2 at)) return;

            int last = curve.Points.Count - 1;
            float x;
            if (_dragIndex == 0) x = 0f;
            else if (_dragIndex == last) x = 1f;
            else x = Mathf.Clamp(at.x, curve.Points[_dragIndex - 1].x + 0.01f, curve.Points[_dragIndex + 1].x - 0.01f);
            curve.Points[_dragIndex] = new Vector2(x, at.y);
            curve.Normalize();
        }

        public void OnPointerUp(PointerEventData e) => _dragIndex = -1;

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Rect r = rectTransform.rect;
            AddQuad(vh, r.min, r.max, Background);
            for (int k = 1; k < 4; k++)
            {
                float x = r.xMin + r.width * k / 4f, y = r.yMin + r.height * k / 4f;
                AddQuad(vh, new Vector2(x - 0.5f, r.yMin), new Vector2(x + 0.5f, r.yMax), Grid);
                AddQuad(vh, new Vector2(r.xMin, y - 0.5f), new Vector2(r.xMax, y + 0.5f), Grid);
            }

            BrushFalloffCurve curve = _shown;
            if (curve == null)
            {
                // Built-in falloff: a faint smoothstep as a reminder of the default shape.
                Vector2 prev = ToLocal(new Vector2(0f, 1f));
                for (int i = 1; i <= CurveSegments; i++)
                {
                    float u = i / (float)CurveSegments, t = 1f - BrushFalloff.ShiftDistance(u, _shownFocal);
                    Vector2 p = ToLocal(new Vector2(u, t * t * (3f - 2f * t)));
                    AddSegment(vh, prev, p, DisabledColor);
                    prev = p;
                }
                return;
            }

            Vector2 last = ToLocal(new Vector2(0f, curve.Evaluate(0f)));
            for (int i = 1; i <= CurveSegments; i++)
            {
                float u = i / (float)CurveSegments;
                Vector2 p = ToLocal(new Vector2(u, curve.Evaluate(u)));
                AddSegment(vh, last, p, CurveColor);
                last = p;
            }
            if (_shownFocal != 0f)
            {
                last = ToLocal(new Vector2(0f, curve.Evaluate(0f)));
                for (int i = 1; i <= CurveSegments; i++)
                {
                    float u = i / (float)CurveSegments;
                    Vector2 p = ToLocal(new Vector2(u, curve.Evaluate(BrushFalloff.ShiftDistance(u, _shownFocal))));
                    AddSegment(vh, last, p, ShiftedColor);
                    last = p;
                }
            }
            float half = PointSizePx * 0.5f;
            foreach (Vector2 point in curve.Points)
            {
                Vector2 c = ToLocal(point);
                AddQuad(vh, c - new Vector2(half, half), c + new Vector2(half, half), PointColor);
            }
        }

        private static void AddQuad(VertexHelper vh, Vector2 min, Vector2 max, Color32 c)
        {
            int start = vh.currentVertCount;
            vh.AddVert(new Vector3(min.x, min.y), c, Vector2.zero);
            vh.AddVert(new Vector3(min.x, max.y), c, Vector2.zero);
            vh.AddVert(new Vector3(max.x, max.y), c, Vector2.zero);
            vh.AddVert(new Vector3(max.x, min.y), c, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }

        private static void AddSegment(VertexHelper vh, Vector2 a, Vector2 b, Color32 c)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 1e-6f) return;
            Vector2 n = new Vector2(-d.y, d.x).normalized * (LineWidthPx * 0.5f);
            int start = vh.currentVertCount;
            vh.AddVert(a - n, c, Vector2.zero);
            vh.AddVert(a + n, c, Vector2.zero);
            vh.AddVert(b + n, c, Vector2.zero);
            vh.AddVert(b - n, c, Vector2.zero);
            vh.AddTriangle(start, start + 1, start + 2);
            vh.AddTriangle(start, start + 2, start + 3);
        }
    }
}
