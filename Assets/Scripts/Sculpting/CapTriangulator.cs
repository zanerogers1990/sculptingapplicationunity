using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Fills a closed 3D loop with a patch whose triangles are about the same size and shape as
    /// the mesh around it. Built for MeshTrimmer's cut faces; nothing in here knows what a trim
    /// is, only that it has a rim and a flat-ish 2D chart to work in.
    ///
    /// WHY THIS EXISTS. A cut face used to be one ear-clipped fan of the rim and nothing else.
    /// Measured on a trimmed 50k-triangle sphere: 636 cap triangles, ZERO interior vertices, the
    /// largest 94x the area of an average shell triangle, and every single one a sliver under 5
    /// degrees. Two things go wrong with that, and both are visible:
    ///
    ///  - **Shading.** Vertex normals are area-weighted across incident triangles, so the amount
    ///    a rim vertex gets pulled off the shell and toward the flat cap is proportional to how
    ///    much cap area happens to hang off it. Across neighbouring rim vertices that varied by a
    ///    factor of 300 million, which renders as a ruffled, streaky band along the cut - read as
    ///    "warping and stretching" rather than as a clean edge.
    ///  - **You cannot sculpt it.** With no interior vertices there is nothing for a brush to
    ///    move; the cut face is a dead flat sheet in a sculpting app.
    ///
    /// The patch is left WELDED to the shell (it reuses the rim's own vertices, and never splits
    /// a rim edge). Giving the cap its own copies would render a touch crisper, but the two
    /// copies then drift apart under any brush that reads neighbours or normals - Smooth,
    /// Inflate, Clay - and the model splits open along the cut. A welded cap at mesh density
    /// instead leaves a one-edge-wide crease, which is exactly how every other sharp feature on a
    /// dense sculpt reads.
    internal static class CapTriangulator
    {
        /// Ceiling on the ear-clipper's inner point-in-triangle tests for one loop. Ear clipping
        /// is O(m^2) on a well-behaved loop and O(m^3) on a pathological one; whatever is left when
        /// the budget runs out is closed by a fan, which can overlap itself but always closes.
        ///
        /// Sized for the rims REAL sculpts produce, not the ones a test sphere does. A cut through
        /// a 1.4M-triangle model leaves a rim of four to five thousand vertices, and plain O(m^2)
        /// ear clipping on that is already 20M tests - so at 4M the clipper ran out every time and
        /// quietly handed the rest to the fan. On a convex-ish loop that is survivable; on a lasso
        /// drawn with a shaky hand it produced cap triangles joining rim vertices a third of the
        /// model apart (54,000x a shell triangle, and a handful of self-overlapping faces with it).
        /// At 64M a rim of eight thousand clips in full, and the tests are a few cheap
        /// multiplications each - about a tenth of a second in the worst case that reaches it.
        private const int EarClipBudget = 64000000;

        /// Each round splits every over-size triangle once (a centroid split cuts area to a
        /// third), so this many rounds covers a cap starting out 3^14 times too coarse. The loop
        /// exits as soon as a sweep finds nothing over size, so the only cuts that pay for the
        /// extra headroom are the ones that need it.
        ///
        /// Eight rounds was enough for the test sphere and NOT enough for a real sculpt: at 359k
        /// triangles a lasso cut still had cap triangles 21x a shell triangle when it ran out,
        /// because the ear clipper's first triangles span a face that now wants twenty thousand
        /// triangles to fill. That is the whole reason this constant is not tuned against the
        /// sphere cases.
        private const int MaxRefinementRounds = 14;

        /// A flip round walks the whole patch, so its cost is what decides whether a trim feels
        /// instant. The original version re-flipped after every one of twelve inner split passes
        /// and, measured on a 306k-triangle sphere, took 4,424 ms of a 4,439 ms trim - the entire
        /// rest of the cut was 15 ms. One flip round per split round, with an early exit as soon
        /// as a pass changes nothing, brings the same cut in comfortably under a tenth of a second.
        private const int TidyFlipPasses = 3;
        private const int FinalFlipPasses = 24;

        /// Triangulates the closed loop `loopPositions` (already in the winding the patch must
        /// have) and refines it until its triangles are roughly `targetEdge` across.
        ///
        /// `loopChart` is the loop in a 2D chart of the surface being filled, in the SAME units
        /// as the 3D positions - see MeshTrimmer's chart scale. Everything here reasons about
        /// shape in that chart and about size in 3D.
        ///
        /// `chartToPosition` turns a point of the chart into the point of the surface being
        /// filled, and is what decides where a new interior vertex actually goes: averaging the
        /// corners instead only lands on the surface where that surface is flat. Pass null to fall
        /// back to averaging - see RefinePass.
        ///
        /// New interior vertices are appended to `extraPositions`. Emitted indices below
        /// `loopPositions.Count` refer to the loop; the rest are `loopPositions.Count + i` into
        /// `extraPositions`. Rim vertices and rim edges are never split, so the patch always
        /// matches the shell it is being sewn onto.
        public static void Fill(
            IReadOnlyList<Vector3> loopPositions,
            IReadOnlyList<Vector2> loopChart,
            Func<Vector2, Vector3> chartToPosition,
            float targetEdge,
            int extraVertexBudget,
            List<Vector3> extraPositions,
            List<int> triangles)
        {
            int rim = loopPositions.Count;
            if (rim < 3) return;

            var chart = new List<Vector2>(rim + 64);
            var position = new List<Vector3>(rim + 64);
            for (int i = 0; i < rim; i++) { chart.Add(loopChart[i]); position.Add(loopPositions[i]); }

            var tris = new List<int>(rim * 3);
            EarClip(rim, chart, tris);
            if (tris.Count == 0) return;

            // The loop's own orientation in the chart. Every triangle below inherits it, and both
            // the in-circle test and the convexity test are expressed relative to it, so the
            // patch works whichever way round the rim happens to run.
            float orientation = SignedArea(chart, rim) >= 0f ? 1f : -1f;

            // Tidy the ear clip up first so the first refinement sweep measures triangles worth
            // measuring, then alternate: one sweep of splits, one round of flips, until nothing
            // is over size. Splitting and flipping HAVE to alternate - a sliver has almost no
            // area, so a sweep leaves it alone, and the flip that later turns it into a fat
            // triangle hands back something well over the target. Doing all the splitting first
            // and flipping once at the end left cap triangles 60x the size of a shell triangle.
            FlipToDelaunay(chart, tris, rim, orientation, TidyFlipPasses);


            int budget = extraVertexBudget;
            if (targetEdge > 0f)
                for (int round = 0; round < MaxRefinementRounds && budget > 0; round++)
                {
                    int added = RefinePass(chart, position, tris, chartToPosition, targetEdge, ref budget);
                    bool flipped = FlipToDelaunay(chart, tris, rim, orientation, FinalFlipPasses);
                    // Stopping on "nothing was over size" alone would leave whatever the flip that
                    // followed it produced unmeasured - and flips are judged in the CHART, so one
                    // can hand back a triangle well over the target in 3D. The loop ends only in a
                    // state where a sweep found nothing to split AND a flip round found nothing to
                    // turn, which is the only state that is actually settled.
                    if (added == 0 && !flipped) break;
                }

            for (int i = rim; i < position.Count; i++) extraPositions.Add(position[i]);
            triangles.AddRange(tris);
        }

        // ------------------------------------------------------------------------ ear clipping

        private static void EarClip(int rim, List<Vector2> chart, List<int> tris)
        {
            var prev = new int[rim];
            var next = new int[rim];
            for (int i = 0; i < rim; i++)
            {
                prev[i] = i == 0 ? rim - 1 : i - 1;
                next[i] = i + 1 == rim ? 0 : i + 1;
            }

            float orientation = SignedArea(chart, rim) >= 0f ? 1f : -1f;
            int remaining = rim;
            int current = 0;
            int stalled = 0;
            int budget = EarClipBudget;

            while (remaining > 3 && stalled <= remaining && budget > 0)
            {
                int p = prev[current], n = next[current];
                if (IsEar(p, current, n, chart, orientation, next, ref budget))
                {
                    Emit(tris, p, current, n);
                    next[p] = n;
                    prev[n] = p;
                    remaining--;
                    current = p;
                    stalled = 0;
                }
                else
                {
                    current = next[current];
                    stalled++;
                }
            }

            // The last triangle, or - if the clip stalled or ran out of budget - a fan over
            // whatever is left.
            int anchor = current;
            int b = next[anchor];
            for (int i = 0; i < remaining - 2; i++)
            {
                int c = next[b];
                Emit(tris, anchor, b, c);
                b = c;
            }
        }

        private static void Emit(List<int> tris, int a, int b, int c)
        {
            if (a == b || b == c || c == a) return;
            tris.Add(a); tris.Add(b); tris.Add(c);
        }

        private static float SignedArea(List<Vector2> chart, int count)
        {
            float doubled = 0f;
            for (int i = 0; i < count; i++)
            {
                int j = i + 1 == count ? 0 : i + 1;
                doubled += chart[i].x * chart[j].y - chart[j].x * chart[i].y;
            }
            return doubled;
        }

        private static bool IsEar(int p, int c, int n, List<Vector2> chart, float orientation,
                                  int[] next, ref int budget)
        {
            Vector2 a = chart[p], b = chart[c], d = chart[n];
            float cross = (b.x - a.x) * (d.y - a.y) - (b.y - a.y) * (d.x - a.x);
            if (cross * orientation <= 0f) return false; // reflex, or collinear

            for (int v = next[n]; v != p; v = next[v])
            {
                if (--budget <= 0) return false;
                if (PointInTriangle(chart[v], a, b, d, orientation)) return false;
            }
            return true;
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c, float orientation)
        {
            float d1 = ((b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x)) * orientation;
            float d2 = ((c.x - b.x) * (p.y - b.y) - (c.y - b.y) * (p.x - b.x)) * orientation;
            float d3 = ((a.x - c.x) * (p.y - c.y) - (a.y - c.y) * (p.x - c.x)) * orientation;
            return d1 >= 0f && d2 >= 0f && d3 >= 0f;
        }

        // -------------------------------------------------------------------------- refinement

        /// Splits any triangle bigger than the target into three by inserting its centroid, then
        /// re-flips. A 1-to-3 centroid split is used rather than the more usual edge bisection
        /// because it touches NO edge: rim edges therefore cannot be subdivided, and the patch
        /// stays exactly welded to the shell. (Bisecting edges would put a vertex in the middle
        /// of a rim edge that the shell's own triangle does not have - a T-junction, which is a
        /// visible crack.)
        private static int RefinePass(List<Vector2> chart, List<Vector3> position, List<int> tris,
                                      Func<Vector2, Vector3> chartToPosition,
                                      float targetEdge, ref int budget)
        {
            // An equilateral triangle of side targetEdge has area (sqrt(3)/4)e^2 ~ 0.433e^2.
            // Split at 1.6x that rather than at the target itself: a centroid split cuts area to
            // a THIRD, so a triangle a hair over target comes back as three well under it, and
            // splitting on the nose overshoots the density it was aiming for by about half again.
            // Anything between one and 1.6 target is close enough to its neighbours to shade
            // evenly, which is the whole point of matching density.
            float targetArea = targetEdge * targetEdge * 0.433f * 1.6f;

            int added = 0;
            int triangleCount = tris.Count / 3; // only the triangles present when the sweep began
            for (int t = 0; t < triangleCount && budget > 0; t++)
            {
                int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                Vector3 a = position[i0], b = position[i1], c = position[i2];

                // Over size in EITHER the chart or in space. The 3D test alone is the honest
                // measure of density, but it cannot see a triangle that is huge across the cut
                // face and merely happens to have its three surface points near-collinear - and
                // those are exactly the triangles whose flat faces overlap their neighbours. The
                // chart is fitted to mesh units (FitChartScales), so its area is comparable, and
                // it shrinks to exactly a third per split, which is what makes the refinement
                // certain to finish.
                Vector2 ca = chart[i0], cb = chart[i1], cc = chart[i2];
                float chartArea = Mathf.Abs((cb.x - ca.x) * (cc.y - ca.y) - (cb.y - ca.y) * (cc.x - ca.x)) * 0.5f;
                if (Vector3.Cross(b - a, c - a).magnitude * 0.5f <= targetArea && chartArea <= targetArea)
                    continue;

                // The centroid IN THE CHART, then that chart point evaluated back onto the surface.
                // Averaging the three 3D corners instead puts the new vertex on the CHORD between
                // them rather than on the surface, and on a curved cut face that is the difference
                // between a clean cut and one with fangs across it - see MeshTrimmer.SurfacePoint.
                Vector2 centreChart = (chart[i0] + chart[i1] + chart[i2]) / 3f;
                int centre = position.Count;
                position.Add(chartToPosition != null ? chartToPosition(centreChart) : (a + b + c) / 3f);
                chart.Add(centreChart);
                budget--;
                added++;

                // The parent slot becomes the first child; the other two are appended.
                tris[t * 3 + 2] = centre;
                tris.Add(i1); tris.Add(i2); tris.Add(centre);
                tris.Add(i2); tris.Add(i0); tris.Add(centre);
            }
            return added;
        }

        // ------------------------------------------------------------------------ Delaunay flips

        /// Turns the triangulation into the CONSTRAINED Delaunay one by repeatedly flipping any
        /// interior edge that fails the in-circle test. This is what actually removes the slivers:
        /// ear clipping produces a valid triangulation, not a good-looking one, and Delaunay is
        /// the triangulation that maximises the smallest angle.
        ///
        /// Two edges are never flipped. Rim edges are constraints - flipping one would detach the
        /// patch from the shell. And an edge whose two triangles form a non-convex quad cannot be
        /// flipped at all: the replacement pair would overlap and stick out of the loop, which on
        /// a concave cut face means geometry outside the model.
        /// Returns whether any edge was actually turned, which is what lets the refinement loop
        /// tell a settled patch from one the last flip round has just changed.
        private static bool FlipToDelaunay(List<Vector2> chart, List<int> tris, int rim, float orientation, int maxPasses)
        {
            // EdgeKeyComparer, not the default: a packed (lo, hi) vertex pair hashes under
            // EqualityComparer&lt;long&gt; to lo ^ hi, and an edge's two endpoints are always nearby
            // indices, so that collapses to a handful of distinct hashes and turns every bucket
            // into a linear scan. This is the same trap - and the same fix - the remesher hit.
            var edgeOwners = new Dictionary<long, int>(EdgeKeyComparer.Instance);
            var edgeSecond = new Dictionary<long, int>(EdgeKeyComparer.Instance);

            bool any = false;
            for (int pass = 0; pass < maxPasses; pass++)
            {
                edgeOwners.Clear();
                edgeSecond.Clear();
                int triangleCount = tris.Count / 3;
                for (int t = 0; t < triangleCount; t++)
                    for (int k = 0; k < 3; k++)
                    {
                        long key = EdgeKey(tris[t * 3 + k], tris[t * 3 + (k + 1) % 3]);
                        if (!edgeOwners.ContainsKey(key)) edgeOwners[key] = t;
                        else if (!edgeSecond.ContainsKey(key)) edgeSecond[key] = t;
                    }

                bool flipped = false;
                foreach (KeyValuePair<long, int> entry in edgeSecond)
                {
                    int u = (int)(entry.Key >> 32), v = (int)(entry.Key & 0xffffffffL);
                    if (IsRimEdge(u, v, rim)) continue;

                    int t0 = edgeOwners[entry.Key], t1 = entry.Value;
                    if (!FindOpposite(tris, t0, u, v, out int c, out int a0, out int b0)) continue;
                    if (!FindOpposite(tris, t1, u, v, out int d, out _, out _)) continue;

                    // a0 -> b0 is the shared edge as t0 walks it, so t0 = (a0, b0, c) and
                    // t1 = (b0, a0, d). The flip replaces them with (a0, d, c) and (d, b0, c).
                    Vector2 pa = chart[a0], pb = chart[b0], pc = chart[c], pd = chart[d];
                    if (!IsConvexQuad(pa, pd, pb, pc, orientation)) continue;
                    if (InCircle(pa, pb, pc, pd) * orientation <= 0f) continue;

                    Write(tris, t0, a0, d, c);
                    Write(tris, t1, d, b0, c);
                    flipped = true;
                }

                if (!flipped) return any;
                any = true;
            }
            return any;
        }

        private static long EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b, hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        // Rim vertices are 0..rim-1 in loop order, so a rim EDGE is a consecutive pair (including
        // the closing one). A chord between two non-adjacent rim vertices is an ordinary interior
        // edge and is free to flip.
        private static bool IsRimEdge(int u, int v, int rim)
        {
            if (u >= rim || v >= rim) return false;
            int diff = u > v ? u - v : v - u;
            return diff == 1 || diff == rim - 1;
        }

        /// The vertex of triangle `t` that is not on edge (u,v), plus that edge in the order this
        /// triangle actually walks it.
        private static bool FindOpposite(List<int> tris, int t, int u, int v, out int opposite, out int from, out int to)
        {
            int i0 = tris[t * 3], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
            if ((i0 == u && i1 == v) || (i0 == v && i1 == u)) { opposite = i2; from = i0; to = i1; return true; }
            if ((i1 == u && i2 == v) || (i1 == v && i2 == u)) { opposite = i0; from = i1; to = i2; return true; }
            if ((i2 == u && i0 == v) || (i2 == v && i0 == u)) { opposite = i1; from = i2; to = i0; return true; }
            opposite = from = to = -1;
            return false;
        }

        private static void Write(List<int> tris, int t, int a, int b, int c)
        {
            tris[t * 3] = a; tris[t * 3 + 1] = b; tris[t * 3 + 2] = c;
        }

        private static bool IsConvexQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float orientation)
        {
            return Cross(a, b, c) * orientation > 0f
                && Cross(b, c, d) * orientation > 0f
                && Cross(c, d, a) * orientation > 0f
                && Cross(d, a, b) * orientation > 0f;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 c) =>
            (b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x);

        /// Positive when `d` lies inside the circumcircle of the counter-clockwise triangle
        /// (a, b, c) - the standard in-circle determinant. Callers multiply by the patch's own
        /// orientation rather than pre-sorting the triangle.
        private static float InCircle(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float ax = a.x - d.x, ay = a.y - d.y;
            float bx = b.x - d.x, by = b.y - d.y;
            float cx = c.x - d.x, cy = c.y - d.y;
            return (ax * ax + ay * ay) * (bx * cy - by * cx)
                 - (bx * bx + by * by) * (ax * cy - ay * cx)
                 + (cx * cx + cy * cy) * (ax * by - ay * bx);
        }
    }
}
