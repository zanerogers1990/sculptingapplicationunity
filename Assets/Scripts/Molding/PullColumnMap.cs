using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Sculpting.Molding
{
    /// Where the model's surface sits along every line parallel to the pull direction.
    ///
    /// This is the piece that makes the mold tools live. Everything the parting-surface fit and
    /// the undercut readout need is a visibility question along ONE fixed direction - "is there
    /// more material above this point", "can this half lift off" - and the Blender addon answers
    /// each one with a BVH ray cast, twelve thousand of them per candidate axis. That is fine
    /// for a button you press once and wait on; it is nowhere near fast enough for a surface you
    /// drag.
    ///
    /// So instead of casting rays, the model is rasterised ONCE into columns: the (Right, Eye)
    /// plane is divided into a grid, and every triangle deposits, into each column its projection
    /// covers, the height at which it crosses that column's centre line. Sorting each column's
    /// crossings turns every later visibility query into a binary search. The result is the same
    /// information a ray cast would give, computed for the whole model in one pass, and it is
    /// exact for the query it answers (the crossing heights are the true plane intersections,
    /// not samples of a field).
    ///
    /// It doubles as the fit's SAMPLER. The addon draws area-weighted random points off the
    /// surface and ray-casts each one; the crossings here are already a uniform sampling of the
    /// surface in projected area - which is precisely the weighting that sampling was
    /// approximating - and they come with their blocked-above/blocked-below flags for free
    /// (crossing k in a column of n is blocked above iff k is not the last, blocked below iff it
    /// is not the first). That removes the RNG, so a fit is deterministic and re-running it
    /// never wobbles the surface.
    ///
    /// Pure geometry on plain arrays, no scene objects, so it is testable outside Unity the same
    /// way MeshRemesher and MeshBoolean are.
    public sealed class PullColumnMap
    {
        /// Columns whose centre line misses every triangle contribute nothing - that is most of
        /// the grid once the block padding is included - so the crossings are held CSR-style
        /// (one offset array plus one packed value array) rather than as a jagged array of
        /// lists. Same reason MeshAdjacency is CSR: a per-column List at 64x40 is 2,560 heap
        /// objects rebuilt on every drag.
        private readonly int[] _start;     // length ColumnCount + 1
        private readonly float[] _heights; // crossing heights, sorted within each column
        /// Crossings actually live in each column after coincident ones are collapsed. Separate
        /// from the span _start implies, because the dedup happens in place inside each slice.
        private readonly int[] _count;

        /// The sample line runs a hair off the column's geometric centre.
        ///
        /// Exactly the nudge SignedDistanceField.ComputeInsideMask applies, for exactly the same
        /// reason: on an axis-aligned mesh a centred sample line lands precisely on shared
        /// triangle edges and vertices, and both triangles sharing that edge then report a
        /// crossing. A duplicated crossing is worse than it sounds - the undercut readout counts
        /// crossings to decide what is trapped, so a plain box came back with its whole top face
        /// flagged as blocked. Irrational-looking fractions of a cell, different per axis, so a
        /// regular grid never lines up with a face diagonal either.
        private const float JitterR = 0.0173f;
        private const float JitterE = 0.0091f;

        public MoldFrame Frame { get; }
        public int Nr { get; }
        public int Ne { get; }
        public float R0 { get; }
        public float E0 { get; }
        public float Dr { get; }
        public float De { get; }

        /// Frame-space extent of the model itself (not the padded block) - the fit clamps
        /// against these so the parting surface always stays inside the mold material.
        public Vector3 ModelMin { get; }
        public Vector3 ModelMax { get; }

        public int ColumnCount => Nr * Ne;
        /// Total crossings recorded - the sample count the fit actually has to work with.
        public int CrossingCount { get; private set; }

        /// Where column i's sample line actually is. Anything that samples the parting field
        /// "at this column" has to use this rather than the geometric centre, or the two are
        /// reading slightly different places - see JitterR.
        public float ColumnCentreR(int i) => R0 + (i + 0.5f + JitterR) * Dr;
        public float ColumnCentreE(int j) => E0 + (j + 0.5f + JitterE) * De;

        private PullColumnMap(MoldFrame frame, int nr, int ne, float r0, float e0, float dr, float de,
                              int[] start, int[] count, float[] heights, Vector3 modelMin, Vector3 modelMax)
        {
            Frame = frame;
            Nr = nr; Ne = ne;
            R0 = r0; E0 = e0;
            Dr = dr; De = de;
            _start = start;
            _count = count;
            _heights = heights;
            ModelMin = modelMin;
            ModelMax = modelMax;
        }

        /// Rasterises `verts`/`tris` (world space) into `nr` x `ne` columns spanning the frame-space
        /// rectangle [r0, r1] x [e0, e1]. That rectangle is the PADDED block footprint, so the map
        /// and the parting field share one grid and an index in either means the same place.
        public static PullColumnMap Build(Vector3[] verts, int[] tris, MoldFrame frame,
                                          float r0, float r1, float e0, float e1, int nr, int ne)
        {
            nr = Mathf.Max(2, nr);
            ne = Mathf.Max(2, ne);
            float dr = Mathf.Max((r1 - r0) / nr, 1e-9f);
            float de = Mathf.Max((e1 - e0) / ne, 1e-9f);
            int columns = nr * ne;

            int triCount = verts == null || tris == null ? 0 : tris.Length / 3;
            if (triCount == 0)
            {
                return new PullColumnMap(frame, nr, ne, r0, e0, dr, de,
                                         new int[columns + 1], new int[columns], Array.Empty<float>(),
                                         Vector3.zero, Vector3.zero);
            }

            // One pass to put every vertex in frame coordinates. Every later stage reads these
            // instead of re-projecting per triangle, which would dot each shared vertex once per
            // face that uses it (six times over, on a typical sculpt).
            var fv = new Vector3[verts.Length];
            Parallel.For(0, verts.Length, i => fv[i] = frame.ToFrame(verts[i]));

            Vector3 lo = fv[0], hi = fv[0];
            for (int i = 1; i < fv.Length; i++) { lo = Vector3.Min(lo, fv[i]); hi = Vector3.Max(hi, fv[i]); }

            // Two passes over the triangles - count, then fill - so the packed array can be
            // allocated exactly once at the right size. The rasteriser is identical in both, and
            // lives in one place (Rasterize) so the two can never drift apart and overflow.
            var counts = new int[columns];
            Parallel.For(0, triCount, t =>
            {
                Rasterize(fv, tris, t, r0, e0, dr, de, nr, ne, null, counts, null);
            });

            var start = new int[columns + 1];
            int running = 0;
            for (int c = 0; c < columns; c++) { start[c] = running; running += counts[c]; }
            start[columns] = running;

            var heights = new float[running];
            // Per-column write cursor, advanced with Interlocked so triangles can be rasterised
            // in parallel even where several land in the same column.
            var cursor = new int[columns];
            Array.Copy(start, cursor, columns);
            Parallel.For(0, triCount, t =>
            {
                Rasterize(fv, tris, t, r0, e0, dr, de, nr, ne, heights, null, cursor);
            });

            // Sorted per column so every query is a binary search, then coincident crossings are
            // collapsed. Parallel over columns: each owns a disjoint slice.
            //
            // The dedup is the backstop behind the jitter above. It only merges crossings that
            // agree to within a millionth of the model's height - far below any real thin shell,
            // and orders of magnitude below what a duplicated face or a grazed shared edge
            // produces - so a genuine double wall still reads as two surfaces.
            float weld = 1e-6f * Mathf.Max(hi.y - lo.y, 1e-6f);
            var count = new int[columns];
            Parallel.For(0, columns, c =>
            {
                int s = start[c], n = start[c + 1] - s;
                if (n > 1) Array.Sort(heights, s, n);

                int w = 0;
                for (int k = 0; k < n; k++)
                {
                    if (w > 0 && heights[s + k] - heights[s + w - 1] <= weld) continue;
                    heights[s + w++] = heights[s + k];
                }
                count[c] = w;
            });

            var map = new PullColumnMap(frame, nr, ne, r0, e0, dr, de, start, count, heights, lo, hi);
            int total = 0;
            for (int c = 0; c < columns; c++) total += count[c];
            map.CrossingCount = total;
            return map;
        }

        /// One triangle into the grid. With `counts` non-null it only tallies; with `heights` and
        /// `cursor` non-null it writes. Keeping both modes in one method is what guarantees the
        /// count pass and the fill pass agree exactly on which cells a triangle covers - the two
        /// having slightly different edge rules is the classic way a CSR build corrupts itself.
        private static void Rasterize(Vector3[] fv, int[] tris, int t,
                                      float r0, float e0, float dr, float de, int nr, int ne,
                                      float[] heights, int[] counts, int[] cursor)
        {
            Vector3 a = fv[tris[t * 3]];
            Vector3 b = fv[tris[t * 3 + 1]];
            Vector3 c = fv[tris[t * 3 + 2]];

            // Signed area of the projection onto (Right, Eye). A triangle seen edge-on projects
            // to a sliver and is skipped: a vertical ray meets it only on a set of measure zero,
            // so it carries no crossing, and dividing by its area would produce garbage
            // barycentrics. This is the one place the map is an approximation, and it is the
            // same approximation a ray cast makes when it misses an edge-on face.
            float area = (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z);
            if (Mathf.Abs(area) < 1e-12f) return;
            float inv = 1f / area;

            float minR = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
            float maxR = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
            float minE = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
            float maxE = Mathf.Max(a.z, Mathf.Max(b.z, c.z));

            // Cell centres sit at r0 + (i + 0.5) * dr, so the first centre at or past minR is
            // ceil((minR - r0)/dr - 0.5).
            int i0 = Mathf.Max(0, Mathf.CeilToInt((minR - r0) / dr - 0.5f));
            int i1 = Mathf.Min(nr - 1, Mathf.FloorToInt((maxR - r0) / dr - 0.5f));
            int j0 = Mathf.Max(0, Mathf.CeilToInt((minE - e0) / de - 0.5f));
            int j1 = Mathf.Min(ne - 1, Mathf.FloorToInt((maxE - e0) / de - 0.5f));
            if (i0 > i1 || j0 > j1) return;

            for (int i = i0; i <= i1; i++)
            {
                float rc = r0 + (i + 0.5f + JitterR) * dr;
                for (int j = j0; j <= j1; j++)
                {
                    float ec = e0 + (j + 0.5f + JitterE) * de;

                    // Barycentrics of the column centre within the projected triangle. The
                    // half-open tolerance is deliberately symmetric (both bounds tested against
                    // the same epsilon) so a centre exactly on a shared edge is claimed by both
                    // adjacent triangles rather than falling through the crack - a dropped
                    // crossing reads as a hole in the solid, which is far worse than a duplicate
                    // (a duplicate pair just looks like a zero-thickness shell to the parity
                    // logic below, and the fit's cost function is indifferent to it).
                    float w0 = ((b.x - rc) * (c.z - ec) - (c.x - rc) * (b.z - ec)) * inv;
                    if (w0 < -1e-6f) continue;
                    float w1 = ((c.x - rc) * (a.z - ec) - (a.x - rc) * (c.z - ec)) * inv;
                    if (w1 < -1e-6f) continue;
                    float w2 = 1f - w0 - w1;
                    if (w2 < -1e-6f) continue;

                    int col = i * ne + j;
                    if (counts != null)
                    {
                        Interlocked.Increment(ref counts[col]);
                    }
                    else
                    {
                        int at = Interlocked.Increment(ref cursor[col]) - 1;
                        heights[at] = w0 * a.y + w1 * b.y + w2 * c.y;
                    }
                }
            }
        }

        // ------------------------------------------------------------------------ queries

        /// Column containing frame coordinates (r, e), clamped to the grid. Clamping rather than
        /// failing: a query just outside the block (a vertex the user has since dragged past the
        /// padding) should answer with its nearest column, not with "no information".
        public void Locate(float r, float e, out int i, out int j)
        {
            i = Mathf.Clamp(Mathf.FloorToInt((r - R0) / Dr), 0, Nr - 1);
            j = Mathf.Clamp(Mathf.FloorToInt((e - E0) / De), 0, Ne - 1);
        }

        public int CrossingsIn(int i, int j) => _count[i * Ne + j];

        public float Crossing(int i, int j, int k) => _heights[_start[i * Ne + j] + k];

        /// True when there is more of the model above `u` in this column - i.e. a half lifting
        /// along +Up from here is blocked. The epsilon keeps a crossing from blocking ITSELF
        /// when the query point is that crossing, which is exactly the case the fit asks about.
        public bool BlockedAbove(int i, int j, float u, float eps)
        {
            int c = i * Ne + j;
            int n = _count[c];
            if (n == 0) return false;
            // Ascending order, so the last crossing is the highest: if that one is not above the
            // query point, nothing is.
            return _heights[_start[c] + n - 1] > u + eps;
        }

        public bool BlockedBelow(int i, int j, float u, float eps)
        {
            int c = i * Ne + j;
            if (_count[c] == 0) return false;
            return _heights[_start[c]] < u - eps;
        }

        /// Index of the crossing closest to `u` in this column, or -1 for an empty column.
        ///
        /// This is how a point ON the surface finds its OWN crossing. Asking "is anything above
        /// me" with a fixed height tolerance does not work for a vertex that sits on the model:
        /// the column's crossings are sampled at the cell CENTRE, up to half a cell away from
        /// the vertex, and on a curved silhouette half a cell of sideways movement moves the
        /// crossing far more than any sane tolerance - so roughly half the vertices on a plain
        /// sphere came back flagged as trapped. Locating the nearest crossing and then counting
        /// how many lie beyond it makes the test depend only on the ORDER of the crossings,
        /// which is exactly the quantity that is robust to that half-cell offset, and it is the
        /// same rule the fit already applies to its own pooled samples.
        public int NearestCrossing(int i, int j, float u)
        {
            int c = i * Ne + j;
            int s = _start[c], n = _count[c];
            if (n == 0) return -1;

            int lo = 0, hi = n;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_heights[s + mid] < u) lo = mid + 1; else hi = mid;
            }
            if (lo >= n) return n - 1;
            if (lo == 0) return 0;
            return (u - _heights[s + lo - 1]) <= (_heights[s + lo] - u) ? lo - 1 : lo;
        }

        /// True when the frame-space point (r, u, e) lies inside the model: an odd number of the
        /// column's crossings sit below it.
        ///
        /// What a channel asks to find where it breaks into the cavity - see
        /// MoldGeometry.LayoutChannel. Parity is the right test here even though the undercut
        /// readout counts by order: this is asked of points on the parting SHEET, which the fit
        /// keeps inside the material rather than on the surface, so there is no crossing sitting
        /// exactly at the query height to be confused about. Outside the grid is outside the
        /// model - Locate would otherwise clamp a far point onto an edge column and answer for
        /// somewhere else.
        public bool InsideAt(float r, float u, float e)
        {
            if (r < R0 || e < E0 || r >= R0 + Nr * Dr || e >= E0 + Ne * De) return false;
            Locate(r, e, out int i, out int j);

            int c = i * Ne + j;
            int s = _start[c], n = _count[c];
            if (n < 2) return false;

            int lo = 0, hi = n;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_heights[s + mid] < u) lo = mid + 1; else hi = mid;
            }
            return (lo & 1) == 1;
        }

        /// Both tests at a world-space point, which is what the undercut overlay asks per vertex.
        public void ClassifyWorld(Vector3 world, float eps, out float u, out bool blockedAbove, out bool blockedBelow)
        {
            Vector3 f = Frame.ToFrame(world);
            u = f.y;
            Locate(f.x, f.z, out int i, out int j);
            blockedAbove = BlockedAbove(i, j, u, eps);
            blockedBelow = BlockedBelow(i, j, u, eps);
        }

        /// Appends every crossing within `radius` columns of (i, j) to the caller's buffers,
        /// tagged with whether it is blocked looking up and looking down. This is the fit's
        /// sample pool for one grid node - see PartingSurfaceFitter.
        public void GatherPool(int i, int j, int radius, System.Collections.Generic.List<float> us,
                               System.Collections.Generic.List<bool> up, System.Collections.Generic.List<bool> down)
        {
            int ia = Mathf.Max(0, i - radius), ib = Mathf.Min(Nr - 1, i + radius);
            int ja = Mathf.Max(0, j - radius), jb = Mathf.Min(Ne - 1, j + radius);
            for (int a = ia; a <= ib; a++)
            {
                for (int b = ja; b <= jb; b++)
                {
                    int c = a * Ne + b;
                    int s = _start[c], n = _count[c];
                    for (int k = 0; k < n; k++)
                    {
                        us.Add(_heights[s + k]);
                        // Position within its own column is all the visibility test needs:
                        // anything above this crossing in the same column blocks a lift, and
                        // anything below blocks a drop.
                        up.Add(k < n - 1);
                        down.Add(k > 0);
                    }
                }
            }
        }
    }
}
