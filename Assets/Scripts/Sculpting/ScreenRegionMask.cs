using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// A dragged box or lasso as a screen-space stencil.
    ///
    /// A box is stored as plain bounds; a lasso is scan-converted ONCE into a coverage bitmap
    /// over its own bounding box, so testing a vertex is an array lookup rather than a walk of
    /// every lasso segment. That is the difference between O(vertices) and
    /// O(vertices x lasso points) - the latter runs into hundreds of millions of operations for
    /// a detailed lasso on a dense mesh, all of it in the frame the user releases the button.
    ///
    /// Lived inside RegionSelectTool until the Trim tool needed the same shape (see
    /// MeshTrimmer). Trim needs one thing hiding and masking never did: the shape's BOUNDARY,
    /// not just its interior. Extruded along the view direction the boundary is the surface that
    /// actually does the cutting, and ArcPosition below is the coordinate along it that lets a
    /// cut loop be triangulated on that surface rather than flattened onto a plane.
    public sealed class ScreenRegionMask
    {
        /// What one cell of the acceleration grid can say about the points inside it. Only cells
        /// no outline segment passes through can answer on their own; the rest defer to the exact
        /// test, which is what keeps the region's boundary a real curve instead of a staircase.
        private const byte CellOutside = 0, CellInside = 1, CellStraddles = 2;

        private readonly int _minX, _minY, _width, _height;
        // Null for a box, which needs no acceleration - its exact test is two comparisons.
        private readonly byte[] _cells;

        // The exact bounds of a box region, in unsnapped screen pixels.
        private readonly Vector2 _boxMin, _boxMax;

        // The closed outline, and the cumulative arc length reaching each of its points.
        // _arc has one more entry than _boundary: the last is the full perimeter, i.e. the
        // length of the implicit closing segment back to _boundary[0].
        private readonly Vector2[] _boundary;
        private readonly float[] _arc;

        private ScreenRegionMask(int minX, int minY, int width, int height, byte[] cells,
                                 Vector2[] boundary, Vector2 boxMin, Vector2 boxMax)
        {
            _minX = minX;
            _minY = minY;
            _width = width;
            _height = height;
            _cells = cells;
            _boundary = boundary;
            _boxMin = boxMin;
            _boxMax = boxMax;

            _arc = new float[boundary.Length + 1];
            for (int i = 0; i < boundary.Length; i++)
                _arc[i + 1] = _arc[i] + Vector2.Distance(boundary[i], boundary[(i + 1) % boundary.Length]);
        }

        /// The outline as a closed polyline in screen pixels - four corners for a box, the
        /// recorded path for a lasso. Owned by this object (the lasso's own point list is
        /// cleared as soon as a gesture is applied, so it is copied rather than referenced).
        public IReadOnlyList<Vector2> Boundary => _boundary;

        /// Total length of the closed outline, in pixels. ArcPosition returns a value in
        /// [0, Perimeter).
        public float Perimeter => _arc[_arc.Length - 1];

        public static ScreenRegionMask Box(Vector2 a, Vector2 b)
        {
            var lo = new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
            var hi = new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            if (hi.x - lo.x <= 0f || hi.y - lo.y <= 0f) return null;

            // Corners of the RAW drag, and the coverage test compares against those same floats -
            // a cut placed against a boundary even half a pixel away from the one classifying the
            // vertices would leave slivers on the wrong side. (This used to snap both to whole
            // pixels together, which was self-consistent but put the cut on a pixel edge rather
            // than where the user dragged it.)
            var corners = new[] { new Vector2(lo.x, lo.y), new Vector2(hi.x, lo.y),
                                  new Vector2(hi.x, hi.y), new Vector2(lo.x, hi.y) };
            return new ScreenRegionMask(Mathf.FloorToInt(lo.x), Mathf.FloorToInt(lo.y),
                                        Mathf.CeilToInt(hi.x) - Mathf.FloorToInt(lo.x),
                                        Mathf.CeilToInt(hi.y) - Mathf.FloorToInt(lo.y),
                                        null, corners, lo, hi);
        }

        public static ScreenRegionMask Lasso(IReadOnlyList<Vector2> points)
        {
            // Fewer than three points cannot enclose anything - a stray click that slipped
            // past the click-slop test, in practice.
            if (points == null || points.Count < 3) return null;

            float fMinX = float.MaxValue, fMinY = float.MaxValue;
            float fMaxX = float.MinValue, fMaxY = float.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                fMinX = Mathf.Min(fMinX, points[i].x);
                fMinY = Mathf.Min(fMinY, points[i].y);
                fMaxX = Mathf.Max(fMaxX, points[i].x);
                fMaxY = Mathf.Max(fMaxY, points[i].y);
            }

            int minX = Mathf.FloorToInt(fMinX), minY = Mathf.FloorToInt(fMinY);
            int width = Mathf.CeilToInt(fMaxX) - minX, height = Mathf.CeilToInt(fMaxY) - minY;
            if (width <= 0 || height <= 0) return null;

            var cells = new byte[width * height];
            var crossings = new List<float>();

            // Even-odd scanline fill, with the path implicitly closed from the last point
            // back to the first - which is exactly what a lasso means by "the bit I drew
            // around", however open the drawn path was left.
            for (int row = 0; row < height; row++)
            {
                float y = minY + row + 0.5f;
                crossings.Clear();
                for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
                {
                    Vector2 p1 = points[j], p2 = points[i];
                    // Half-open comparison (<=, not <) counts a vertex sitting exactly on
                    // the scanline once rather than twice, which is what keeps the parity
                    // right through a horizontal run of points.
                    if ((p1.y <= y) == (p2.y <= y)) continue;
                    float t = (y - p1.y) / (p2.y - p1.y);
                    crossings.Add(p1.x + t * (p2.x - p1.x));
                }
                if (crossings.Count < 2) continue;

                crossings.Sort();
                for (int c = 0; c + 1 < crossings.Count; c += 2)
                {
                    int x0 = Mathf.Max(Mathf.CeilToInt(crossings[c] - 0.5f) - minX, 0);
                    int x1 = Mathf.Min(Mathf.FloorToInt(crossings[c + 1] - 0.5f) - minX, width - 1);
                    for (int x = x0; x <= x1; x++) cells[row * width + x] = CellInside;
                }
            }

            var boundary = new Vector2[points.Count];
            for (int i = 0; i < points.Count; i++) boundary[i] = points[i];

            // The fill above only sampled each cell's CENTRE, so a cell the outline runs through
            // is right about its middle and wrong about the rest of itself. Marking every such
            // cell sends the points inside it to the exact test instead, which is what turns the
            // grid from an answer into a mere accelerator - see Contains.
            MarkStraddlingCells(boundary, cells, minX, minY, width, height);

            return new ScreenRegionMask(minX, minY, width, height, cells, boundary,
                                        new Vector2(minX, minY), new Vector2(minX + width, minY + height));
        }

        /// Flags every cell any outline segment passes through, plus its immediate neighbours.
        /// Walked at half-pixel steps with a one-cell halo rather than by an exact line
        /// rasterizer: a segment can clip the corner of a cell over a span far shorter than the
        /// sampling step, and the halo covers that case for the cost of eight extra writes.
        private static void MarkStraddlingCells(Vector2[] boundary, byte[] cells,
                                                int minX, int minY, int width, int height)
        {
            for (int i = 0; i < boundary.Length; i++)
            {
                Vector2 a = boundary[i], b = boundary[(i + 1) % boundary.Length];
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) * 2f));
                for (int s = 0; s <= steps; s++)
                {
                    Vector2 p = a + (b - a) * (s / (float)steps);
                    int cx = Mathf.FloorToInt(p.x) - minX, cy = Mathf.FloorToInt(p.y) - minY;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int y = cy + dy;
                        if (y < 0 || y >= height) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int x = cx + dx;
                            if (x < 0 || x >= width) continue;
                            cells[y * width + x] = CellStraddles;
                        }
                    }
                }
            }
        }

        /// Whether the shape covers this screen point - EXACTLY, to floating-point precision,
        /// not to the nearest pixel.
        ///
        /// That distinction is the whole reason this is not just a bitmap lookup. Trim finds its
        /// cut by bisecting this predicate (see MeshTrimmer.FindCrossing), so whatever shape the
        /// predicate has IS the shape of the cut surface. A per-pixel bitmap makes that surface a
        /// staircase of pixel-wide steps, and on a dense sculpt one pixel is about one triangle -
        /// so the cut came out visibly serrated, and the rim's alternating long/short edges left
        /// the cap's area-weighted normals fringed along the whole cut. Measured on the user's
        /// 359k-triangle model: the cut wandered +/-0.8 px off the drawn circle, against +/-0.14 px
        /// once the test became exact.
        ///
        /// The grid is kept, but only as an accelerator: a cell no outline segment crosses is
        /// wholly inside or wholly outside and answers immediately, so the O(outline) polygon test
        /// runs only for points in the thin band along the boundary - a few per thousand vertices
        /// on a real gesture, and the crossing search is entirely within that band by definition.
        public bool Contains(float screenX, float screenY)
        {
            if (_cells == null)
                return screenX >= _boxMin.x && screenX <= _boxMax.x &&
                       screenY >= _boxMin.y && screenY <= _boxMax.y;

            int x = Mathf.FloorToInt(screenX) - _minX;
            int y = Mathf.FloorToInt(screenY) - _minY;
            if (x < 0 || y < 0 || x >= _width || y >= _height) return false;

            byte cell = _cells[y * _width + x];
            if (cell == CellInside) return true;
            if (cell == CellOutside) return false;
            return ContainsExact(screenX, screenY);
        }

        public bool Contains(Vector2 screenPoint) => Contains(screenPoint.x, screenPoint.y);

        /// Even-odd crossing test against the outline itself. Same rule as the scanline fill that
        /// built the grid (half-open in y, so a vertex exactly on the ray counts once), which is
        /// what lets the two be mixed without ever disagreeing.
        private bool ContainsExact(float screenX, float screenY)
        {
            bool inside = false;
            for (int i = 0, j = _boundary.Length - 1; i < _boundary.Length; j = i++)
            {
                Vector2 p1 = _boundary[j], p2 = _boundary[i];
                if ((p1.y <= screenY) == (p2.y <= screenY)) continue;
                float t = (screenY - p1.y) / (p2.y - p1.y);
                if (p1.x + t * (p2.x - p1.x) > screenX) inside = !inside;
            }
            return inside;
        }

        /// Distance along the closed outline, from Boundary[0], of the outline point nearest
        /// `screenPoint`. This is the "which way around the shape am I" coordinate of the
        /// extruded cutting surface: paired with view depth it gives that surface a flat 2D
        /// chart, which is what lets MeshTrimmer triangulate a cut loop ON the surface -
        /// including across a box's corner, where any single flat plane through the loop would
        /// slice the corner off.
        ///
        /// A plain linear scan over the segments. Called once per CUT vertex (thousands at
        /// most, against a few hundred lasso segments), never per mesh vertex.
        public float ArcPosition(Vector2 screenPoint)
        {
            float bestSqr = float.MaxValue;
            float bestArc = 0f;

            for (int i = 0; i < _boundary.Length; i++)
            {
                Vector2 a = _boundary[i];
                Vector2 b = _boundary[(i + 1) % _boundary.Length];
                Vector2 ab = b - a;
                float lenSqr = ab.sqrMagnitude;

                float t = lenSqr > 1e-12f ? Mathf.Clamp01(Vector2.Dot(screenPoint - a, ab) / lenSqr) : 0f;
                Vector2 closest = a + ab * t;
                float sqr = (screenPoint - closest).sqrMagnitude;
                if (sqr >= bestSqr) continue;

                bestSqr = sqr;
                bestArc = _arc[i] + t * Mathf.Sqrt(lenSqr);
            }

            return bestArc;
        }

        /// The inverse of ArcPosition: the outline point `arc` pixels along from Boundary[0],
        /// wrapping round as many times as needed.
        ///
        /// This is what lets the cut FACE be evaluated on the cutting surface rather than
        /// interpolated across it. A cap's interior points used to be the 3D centroids of their
        /// parent triangles, which for a curved outline sit off the surface by the chord's
        /// sagitta - a quarter of a world unit on a wide lasso, which rendered as fangs and
        /// gouges across the cut face. Reconstructing (arc, depth) back into a point puts every
        /// one of them exactly on the swept surface instead. See MeshTrimmer.SurfacePoint.
        public Vector2 PointAtArc(float arc)
        {
            float perimeter = Perimeter;
            if (_boundary.Length == 0) return default;
            if (perimeter <= 0f) return _boundary[0];

            arc -= perimeter * Mathf.Floor(arc / perimeter);
            if (arc < 0f) arc = 0f;

            // The last segment closes the outline, so the search runs over _boundary.Length
            // segments and _arc[i + 1] is always a valid end.
            int lo = 0, hi = _boundary.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) >> 1;
                if (_arc[mid] <= arc) lo = mid; else hi = mid - 1;
            }

            float length = _arc[lo + 1] - _arc[lo];
            float t = length > 1e-6f ? (arc - _arc[lo]) / length : 0f;
            Vector2 a = _boundary[lo], b = _boundary[(lo + 1) % _boundary.Length];
            return a + (b - a) * t;
        }

        /// Projects a point in some object's local space to screen pixels through `mvp`
        /// (projection * worldToCamera * localToWorld). False when the point is behind the
        /// camera, where a projection would be mirrored nonsense.
        ///
        /// Shared by RegionSelectTool and MeshTrimmer rather than written twice: the hide/mask
        /// gestures decide WHICH vertices a shape covers with this, and Trim then hunts along
        /// mesh edges for the exact place the answer flips. If the two disagreed by even a
        /// rounding step the cut would land off the boundary the selection was made against.
        public static bool ProjectToScreen(Matrix4x4 mvp, Vector3 localPos, Rect viewport, out Vector2 screen)
        {
            Vector4 clip = mvp * new Vector4(localPos.x, localPos.y, localPos.z, 1f);
            screen = default;

            // Behind a PERSPECTIVE camera: w is the view-space depth, so a non-positive w means
            // the point is at or behind the eye.
            if (clip.w <= 1e-6f) return false;

            float invW = 1f / clip.w;
            // Behind an ORTHOGRAPHIC camera, where w is always 1 and the test above can never
            // fire: everything in front of the near plane has ndc z >= -1.
            if (clip.z * invW < -1f) return false;

            screen = new Vector2(
                viewport.x + (clip.x * invW * 0.5f + 0.5f) * viewport.width,
                viewport.y + (clip.y * invW * 0.5f + 0.5f) * viewport.height);
            return true;
        }
    }
}
