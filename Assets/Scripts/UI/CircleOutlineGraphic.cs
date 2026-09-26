using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// A thin circle of constant on-screen width, drawn as one uGUI mesh - the brush cursor's
    /// inner falloff ring. The outer ring is a stretched sprite, whose line thickens and thins
    /// with its diameter; that suits the outer ring, but the inner one can shrink to a few
    /// pixels, where a stretched sprite's line vanishes. Each band is feathered by a pixel on
    /// both sides, since uGUI geometry is not antialiased.
    ///
    /// A dark halo band is drawn under the line for contrast, like the outer ring's halo. The
    /// line takes the Graphic's color; the halo takes its alpha only.
    ///
    /// RequireComponent restated for the reason RegionMarqueeGraphic gives: a Graphic built from
    /// code does not get a CanvasRenderer from the base class's attribute, and draws nothing.
    [RequireComponent(typeof(CanvasRenderer))]
    public class CircleOutlineGraphic : Graphic
    {
        private const float FeatherPx = 1f;
        private const float HaloExtraPx = 1f;   // per side
        private const float HaloAlpha = 0.45f;
        // Same dash pattern as the outer ring's dashed sprite (Smooth).
        private const int DashCount = 14;
        private const float DashOnFraction = 0.6f;

        private float _radius;
        private float _thickness = 1.5f;
        private bool _dashed;

        /// Marks the mesh dirty only when something changed, so a still cursor costs nothing.
        public void Set(float radius, float thickness, bool dashed)
        {
            if (Mathf.Approximately(radius, _radius) && Mathf.Approximately(thickness, _thickness) && dashed == _dashed)
                return;
            _radius = radius;
            _thickness = thickness;
            _dashed = dashed;
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (_radius <= 0f) return;

            // About one segment per 3 px of circumference: smooth at any size, cheap at all of them.
            int segments = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * _radius / 3f), 24, 128);
            Color halo = new Color(0f, 0f, 0f, HaloAlpha * color.a);
            float halfLine = _thickness * 0.5f;

            if (!_dashed)
            {
                AddArc(vh, 0f, 2f * Mathf.PI, segments, halfLine + HaloExtraPx, halo);
                AddArc(vh, 0f, 2f * Mathf.PI, segments, halfLine, color);
                return;
            }

            int perDash = Mathf.Max(2, segments / DashCount);
            float step = 2f * Mathf.PI / DashCount;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < DashCount; i++)
                {
                    float a0 = i * step;
                    AddArc(vh, a0, a0 + step * DashOnFraction, perDash,
                        pass == 0 ? halfLine + HaloExtraPx : halfLine, pass == 0 ? halo : color);
                }
        }

        /// One feathered band from angle a0 to a1: four rings of vertices (transparent, solid,
        /// solid, transparent) across the band's width, joined by three quad strips.
        private void AddArc(VertexHelper vh, float a0, float a1, int segments, float halfWidth, Color c)
        {
            Color clear = new Color(c.r, c.g, c.b, 0f);
            float[] radii =
            {
                Mathf.Max(0f, _radius - halfWidth - FeatherPx), Mathf.Max(0f, _radius - halfWidth),
                _radius + halfWidth, _radius + halfWidth + FeatherPx,
            };
            Color[] colors = { clear, c, c, clear };

            int start = vh.currentVertCount;
            for (int s = 0; s <= segments; s++)
            {
                float a = Mathf.Lerp(a0, a1, s / (float)segments);
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                for (int k = 0; k < 4; k++) vh.AddVert(dir * radii[k], colors[k], Vector2.zero);
            }
            for (int s = 0; s < segments; s++)
            {
                int row = start + s * 4, next = row + 4;
                for (int k = 0; k < 3; k++)
                {
                    vh.AddTriangle(row + k, next + k, next + k + 1);
                    vh.AddTriangle(row + k, next + k + 1, row + k + 1);
                }
            }
        }
    }
}
