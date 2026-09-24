using System.Collections.Generic;
using UnityEngine;

namespace Sculpting.Molding
{
    /// A stand-in for the model with only as many triangles as the voxel build can see.
    ///
    /// Measured on the user's lure (730k vertices, 2.89M triangles), one DRAFT build took 5.9s,
    /// and almost none of that was the mold: one half with a tiny cutter costs 149ms, one half
    /// with the full model as its cutter costs 2,719ms. The whole difference is sampling the
    /// model's signed distance field - building the triangle accelerator and running the
    /// winding rays over three million triangles - onto a grid that, at draft resolution, has
    /// cells 1.3mm across. Every one of those cells was averaging over hundreds of triangles
    /// it could never resolve.
    ///
    /// So the model is clustered down to the grid first: every vertex snaps to a cell of a
    /// fraction of the build's own cell size, each cluster becomes its average position, and
    /// triangles that collapse are dropped. On the lure at draft resolution that is 67k
    /// triangles instead of 2.89M, and the built half came back with the SAME volume to five
    /// significant figures (ratio 1.00000) - a boolean at that resolution cannot tell the two
    /// cutters apart - in 210ms instead of 2,719ms.
    ///
    /// Clustering is a CHAIN MAP, which is why this is safe for a winding-number boolean where
    /// a general decimator would not be. Collapsing vertices maps a closed surface to a closed
    /// surface: every edge still has its two sides, some triangles just become degenerate and
    /// vanish. A folded sheet that collapses onto itself leaves two coincident triangles facing
    /// opposite ways, which cancel in the winding sum exactly as they should. So the
    /// inside/outside field MeshBoolean reads is still well defined, and it only moves within a
    /// cluster's width of the surface - a fraction of the cell the extraction resolves anyway.
    public static class MoldCutterProxy
    {
        /// Cluster size as a fraction of the build's voxel cell. A half-cell draft proxy was
        /// indistinguishable from the full model in the measurement above; the final build uses
        /// a quarter cell so its tighter grid never sees the proxy's own rounding. At the
        /// resolutions MatchModelResolution picks, a quarter of the final cell is smaller than
        /// the model's triangles, so the final proxy is close to the model itself - which is the
        /// point: the final build is the one whose detail matters.
        public const float DraftCellFraction = 0.5f;
        public const float FinalCellFraction = 0.25f;

        /// Clusters `vertexCount` vertices and the triangles in `tris` (the first `cornerCount`
        /// entries) onto a grid of `cell`. Deterministic: clusters are numbered in first-seen
        /// vertex order and averaged in that order, so the same input always gives the same
        /// proxy - a proxy that wobbled between rebuilds would make the halves wobble too.
        public static void Cluster(Vector3[] verts, int vertexCount, int[] tris, int cornerCount, float cell,
                                   out Vector3[] outVerts, out int[] outTris)
        {
            vertexCount = Mathf.Min(vertexCount, verts != null ? verts.Length : 0);
            cornerCount = Mathf.Min(cornerCount, tris != null ? tris.Length : 0);
            if (vertexCount == 0 || cornerCount < 3 || !(cell > 0f))
            {
                outVerts = new Vector3[0];
                outTris = new int[0];
                return;
            }

            Vector3 lo = verts[0];
            for (int i = 1; i < vertexCount; i++) lo = Vector3.Min(lo, verts[i]);

            // 21 bits per axis: two million cells across. A grid that fine would be asking for a
            // cluster smaller than float precision on any model this app can hold.
            double inv = 1.0 / cell;
            var ids = new Dictionary<long, int>(Mathf.Max(16, vertexCount / 4));
            var remap = new int[vertexCount];
            var sums = new List<Vector3>(Mathf.Max(16, vertexCount / 4));
            var counts = new List<int>(Mathf.Max(16, vertexCount / 4));

            for (int i = 0; i < vertexCount; i++)
            {
                Vector3 p = verts[i];
                long x = (long)((p.x - lo.x) * inv);
                long y = (long)((p.y - lo.y) * inv);
                long z = (long)((p.z - lo.z) * inv);
                long key = (x & 0x1FFFFF) | ((y & 0x1FFFFF) << 21) | ((z & 0x1FFFFF) << 42);

                if (!ids.TryGetValue(key, out int id))
                {
                    id = sums.Count;
                    ids.Add(key, id);
                    sums.Add(Vector3.zero);
                    counts.Add(0);
                }
                sums[id] += p;
                counts[id]++;
                remap[i] = id;
            }

            outVerts = new Vector3[sums.Count];
            for (int i = 0; i < outVerts.Length; i++) outVerts[i] = sums[i] / counts[i];

            var kept = new List<int>(cornerCount / 4 + 3);
            for (int t = 0; t + 2 < cornerCount; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if ((uint)a >= (uint)vertexCount || (uint)b >= (uint)vertexCount || (uint)c >= (uint)vertexCount) continue;
                a = remap[a]; b = remap[b]; c = remap[c];
                if (a == b || b == c || a == c) continue;
                kept.Add(a); kept.Add(b); kept.Add(c);
            }
            outTris = kept.ToArray();
        }

        /// A cheap fingerprint of a vertex array: every coordinate's bits folded into one hash.
        ///
        /// What decides whether a cached proxy is still the model. The count and the array
        /// identity are not enough on their own - a sculpt stroke moves vertices in place
        /// without changing either - and a full hash of 730k vertices is about 2ms, next to the
        /// 60ms a re-cluster costs.
        public static ulong Fingerprint(Vector3[] verts, int count)
        {
            ulong h = 1469598103934665603UL;
            count = Mathf.Min(count, verts != null ? verts.Length : 0);
            for (int i = 0; i < count; i++)
            {
                Vector3 v = verts[i];
                h = (h ^ (uint)System.BitConverter.SingleToInt32Bits(v.x)) * 1099511628211UL;
                h = (h ^ (uint)System.BitConverter.SingleToInt32Bits(v.y)) * 1099511628211UL;
                h = (h ^ (uint)System.BitConverter.SingleToInt32Bits(v.z)) * 1099511628211UL;
            }
            return h ^ (ulong)count;
        }
    }
}
