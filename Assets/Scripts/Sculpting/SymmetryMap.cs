using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// A one-to-one pairing of vertices across a local-space mirror plane: which vertex on one
    /// side IS the same vertex on the other, which sit ON the plane (and so are their own
    /// reflection), and which have no counterpart at all.
    ///
    /// Deliberately NOT consulted by brush strokes. Mirrored sculpting reflects the brush
    /// POSITION and re-runs the falloff on whatever vertices it finds there (see
    /// MirrorController.GetMirrorSigns and SculptController's seven apply sites). That approach
    /// never fails and never goes stale, which a map on the hot path could not promise - it would
    /// have to be rebuilt after every Remesh, Join, Extract and mask-transform, and a map that
    /// silently fell out of date would move the WRONG vertices, which is a far worse failure than
    /// the slight asymmetry it was brought in to remove.
    ///
    /// What the map is for is the two things positional mirroring cannot do: TELL you whether an
    /// object is actually symmetric, and REPAIR it when it is not. Positional mirroring keeps two
    /// sides looking alike while the topology matches, but a Remesh re-tessellates both halves
    /// independently and an imported mesh may never have matched at all - and once the sides have
    /// drifted, nothing without an explicit correspondence can put them back. SymmetryTools reads
    /// this map to do exactly that.
    ///
    /// Pure geometry (Vector3/Mathf only, no MonoBehaviour, no Mesh), so it compiles and runs
    /// outside Unity against the shim harness - see the reference_dotnet_shim_harness note.
    public class SymmetryMap
    {
        public const int AxisX = 0;
        public const int AxisY = 1;
        public const int AxisZ = 2;

        /// Returned by PartnerOf for a vertex with no counterpart. Also what an on-plane vertex
        /// reports: it has no SEPARATE partner, being its own reflection, which IsOnPlane is the
        /// question to ask about instead.
        public const int NoPartner = -1;

        /// Which local axis the mirror plane is perpendicular to (AxisX = the YZ plane, etc).
        public int Axis { get; private set; }

        /// How far apart two positions may be and still be considered reflections of each other.
        /// Also the half-thickness of the "on the plane" band. See DefaultTolerance for the
        /// scale this wants to be at and why.
        public float Tolerance { get; private set; }

        private int[] _partner;
        private bool[] _onPlane;

        public int VertexCount => _partner.Length;

        /// Vertex pairs found (counted once per pair, not once per vertex).
        public int PairCount { get; private set; }

        /// Vertices sitting on the mirror plane - the centreline of a symmetric model.
        public int OnPlaneCount { get; private set; }

        /// Vertices that are neither on the plane nor paired with anything. These are precisely
        /// the places the model is NOT symmetric, and the count is the honest answer to "is this
        /// object symmetric" that a user otherwise has to judge by eye.
        public int UnmatchedCount { get; private set; }

        public bool IsSymmetric => UnmatchedCount == 0;

        public int PartnerOf(int index) => _partner[index];
        public bool IsOnPlane(int index) => _onPlane[index];

        private SymmetryMap() { }

        /// A candidate pairing awaiting the greedy matching pass in Build. Squared distance is
        /// kept rather than the real one - the pass only ever compares these against each other,
        /// and sqrt on every candidate of a multi-million-vertex mesh buys nothing.
        private struct Edge
        {
            public float Sqr;
            public int A;
            public int B;
        }

        /// Reflects a local-space point through the plane perpendicular to `axis` and passing
        /// through the local origin - the same plane MirrorController's stroke mirroring and its
        /// drawn quads both use, so a map built here describes the geometry that mirrored
        /// sculpting actually produces.
        public static Vector3 Reflect(Vector3 p, int axis)
        {
            switch (axis)
            {
                case AxisX: p.x = -p.x; break;
                case AxisY: p.y = -p.y; break;
                default: p.z = -p.z; break;
            }
            return p;
        }

        /// The component of `p` measured across the mirror plane - its signed distance from the
        /// plane, since the plane passes through the origin and is axis-aligned.
        public static float Coord(Vector3 p, int axis) =>
            axis == AxisX ? p.x : (axis == AxisY ? p.y : p.z);

        /// A tolerance proportional to the model rather than an absolute number, because this is
        /// used on everything from a default unit sphere to an imported multi-metre scan, and a
        /// fixed epsilon would pair everything on one and nothing on the other.
        ///
        /// Half a percent of the bounding-box diagonal is comfortably below the vertex spacing of
        /// any mesh this app produces (a 500-vertex sphere spaces vertices ~4% of its diagonal
        /// apart), which is the property that matters: a tolerance approaching the spacing starts
        /// pairing each vertex with its reflection's NEIGHBOUR, and the mutual-agreement rule in
        /// Build then throws those away rather than pairing them wrongly - so an over-large
        /// tolerance shows up as a low pair count, not as silently corrupt output.
        public static float DefaultTolerance(Vector3[] vertices)
        {
            if (vertices == null || vertices.Length == 0) return 0.001f;

            Vector3 min = vertices[0], max = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            float diagonal = (max - min).magnitude;
            return Mathf.Max(diagonal * 0.005f, 1e-6f);
        }

        /// Builds the pairing. O(vertex count) with a uniform spatial hash - the same bucketing
        /// idea VertexSpatialGrid uses for brush queries, rebuilt privately here because that
        /// class hands back a SHARED result buffer that the nested lookup below would clobber
        /// mid-iteration, and because this wants a snapshot that cannot be invalidated by a
        /// stroke moving vertices underneath it.
        public static SymmetryMap Build(Vector3[] vertices, int axis, float tolerance)
        {
            int n = vertices?.Length ?? 0;
            var map = new SymmetryMap
            {
                Axis = axis,
                Tolerance = Mathf.Max(tolerance, 1e-6f),
                _partner = new int[n],
                _onPlane = new bool[n]
            };
            if (n == 0) return map;

            float tol = map.Tolerance;
            float tolSqr = tol * tol;

            // Cell size == tolerance, so the 3x3x3 block around a target's own cell is guaranteed
            // to contain every vertex within `tol` of it: the block extends a full cell past the
            // target's cell in each direction, and the target cannot be more than one cell from
            // the block edge. Larger cells would only make each bucket longer to scan.
            float cell = tol;
            var buckets = new Dictionary<Vector3Int, List<int>>(n / 4 + 1);

            for (int i = 0; i < n; i++)
            {
                map._partner[i] = NoPartner;
                map._onPlane[i] = Mathf.Abs(Coord(vertices[i], axis)) <= tol;

                Vector3Int c = CellOf(vertices[i], cell);
                if (!buckets.TryGetValue(c, out List<int> list))
                {
                    list = new List<int>();
                    buckets[c] = list;
                }
                list.Add(i);
            }

            // Pass 1: every off-plane vertex nominates its nearest few candidates across the
            // plane, each nomination emitted as a (distance, i, j) edge.
            //
            // Several candidates rather than just the nearest, because meshes routinely carry
            // COINCIDENT vertices - a UV seam splits the vertices along one meridian so the two
            // sides can hold different texture coordinates, and poles are split once per
            // surrounding triangle. Unity's own primitive sphere, the default object in this app,
            // has enough of them that a nearest-only rule left 115 of its 515 vertices
            // "unmatched" and reported a perfectly symmetric sphere as asymmetric. Those extra
            // candidates sit at the SAME position, so which one a vertex nominates first is
            // arbitrary - and any rule that needs two vertices to independently agree on an
            // arbitrary choice will keep disagreeing.
            var edges = new List<Edge>(n);

            for (int i = 0; i < n; i++)
            {
                if (map._onPlane[i]) continue;

                Vector3 target = Reflect(vertices[i], axis);
                float side = Coord(vertices[i], axis);
                Vector3Int home = CellOf(target, cell);

                for (int z = -1; z <= 1; z++)
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    if (!buckets.TryGetValue(new Vector3Int(home.x + x, home.y + y, home.z + z),
                                             out List<int> list)) continue;

                    for (int k = 0; k < list.Count; k++)
                    {
                        int j = list[k];
                        if (j == i || map._onPlane[j]) continue;
                        // Must genuinely be across the plane. Without this a pair of vertices
                        // straddling the plane closer together than `tol` could pair with
                        // themselves-ish on the same side, which is not a reflection.
                        if (Coord(vertices[j], axis) * side > 0f) continue;

                        float d = (vertices[j] - target).sqrMagnitude;
                        if (d <= tolSqr) edges.Add(new Edge { Sqr = d, A = i, B = j });
                    }
                }
            }

            // Pass 2: greedy one-to-one matching, closest pairs first. Each vertex can be claimed
            // once, so the result is guaranteed to be a genuine involution - exactly what
            // SymmetryTools.MakeSymmetric needs to copy one side onto the other without a vertex
            // being written twice from two different sources.
            //
            // Closest-first is what makes greedy the right rule here rather than merely a cheap
            // one: a true reflection sits at distance ~0, so real pairs are matched long before
            // any looser almost-match gets a chance to steal either end. Where several candidates
            // are exactly coincident the choice between them is arbitrary AND harmless - they
            // occupy the same point, so either assignment describes the same geometry.
            edges.Sort((p, q) => p.Sqr.CompareTo(q.Sqr));

            for (int e = 0; e < edges.Count; e++)
            {
                Edge edge = edges[e];
                if (map._partner[edge.A] != NoPartner || map._partner[edge.B] != NoPartner) continue;
                map._partner[edge.A] = edge.B;
                map._partner[edge.B] = edge.A;
            }

            for (int i = 0; i < n; i++)
            {
                if (map._onPlane[i]) map.OnPlaneCount++;
                else if (map._partner[i] != NoPartner) map.PairCount++;
                else map.UnmatchedCount++;
            }
            map.PairCount /= 2; // counted from both ends above

            return map;
        }

        private static Vector3Int CellOf(Vector3 p, float cell) => new Vector3Int(
            Mathf.FloorToInt(p.x / cell),
            Mathf.FloorToInt(p.y / cell),
            Mathf.FloorToInt(p.z / cell));

        /// One-line report for the UI. The whole value of building a correspondence map is that
        /// it can answer "is this symmetric, and where isn't it" with numbers instead of leaving
        /// the user to rotate the model and squint at it.
        public string Summary()
        {
            string axisName = Axis == AxisX ? "X" : (Axis == AxisY ? "Y" : "Z");
            if (VertexCount == 0) return $"{axisName}: no geometry";
            return IsSymmetric
                ? $"{axisName}: symmetric - {PairCount} pairs, {OnPlaneCount} on centre"
                : $"{axisName}: {UnmatchedCount} unmatched of {VertexCount} ({PairCount} pairs, {OnPlaneCount} on centre)";
        }
    }
}
