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

        /// How far a topological propagation step may reach, as a multiple of Tolerance.
        ///
        /// Propagation (see Propagate) grows the pairing along the mesh's own edges out of pairs
        /// already found, so the distance test is no longer being asked "are these two the same
        /// vertex" on its own - the topology has already said they are the corresponding
        /// neighbours of a known pair, and this only has to be wide enough to cover how far the
        /// two sides have DRIFTED. That is a different, much looser question than the seeding
        /// pass answers, which is why it gets its own, wider radius: the drift this tool exists
        /// to repair is routinely a whole vertex spacing, and Tolerance is deliberately sized
        /// below that.
        public const float PropagationReach = 4f;

        /// Propagation is a fixed point - each round can only pair vertices adjacent to a pair
        /// found in an earlier one - so it stops on its own. The cap is a guard against a
        /// pathological mesh, not a tuning knob; meshes here settle in two or three rounds.
        private const int MaxPropagationRounds = 64;

        /// Which local axis the mirror plane is perpendicular to (AxisX = the YZ plane, etc).
        public int Axis { get; private set; }

        /// How far apart two positions may be and still be considered reflections of each other.
        /// Also the half-thickness of the "on the plane" band. See DefaultTolerance for the
        /// scale this wants to be at and why.
        public float Tolerance { get; private set; }

        private int[] _partner;
        private bool[] _onPlane;

        // Direct-edge neighbours in CSR form, built only when Build is given triangles: vertex
        // i's neighbours are _adjNeighbours[_adjStart[i] .. _adjStart[i] + _adjCount[i]).
        // Deliberately built here rather than borrowed from SculptableMesh - this class is pure
        // geometry over plain arrays (it runs outside Unity against the shim harness), and the
        // map has to describe the vertex array it was HANDED, which during a repair is a working
        // copy rather than the live mesh.
        private int[] _adjStart;
        private int[] _adjCount;
        private int[] _adjNeighbours;

        public int VertexCount => _partner.Length;

        /// Whether the map was built with triangles, and so can answer neighbour queries and
        /// propagate a pairing along the surface.
        public bool HasTopology => _adjNeighbours != null;

        public int NeighbourCount(int index) => _adjCount == null ? 0 : _adjCount[index];
        public int Neighbour(int index, int k) => _adjNeighbours[_adjStart[index] + k];

        /// Vertex pairs found (counted once per pair, not once per vertex).
        public int PairCount { get; private set; }

        /// How many of those pairs came from propagating along the surface rather than from the
        /// distance test alone (counted once per pair). Zero when the map was built without
        /// triangles.
        public int PropagatedPairCount { get; private set; }

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
            => Build(vertices, null, axis, tolerance);

        /// Builds the pairing, then - given the mesh's triangles - grows it along the surface out
        /// of what the distance test found (see Propagate).
        ///
        /// Always prefer this overload where the triangles are to hand. Distance alone can only
        /// pair vertices that are still within Tolerance of their reflection, which is a
        /// judgement about DRIFT made with no way to tell drift from a different vertex nearby;
        /// once the topology is available, the two questions separate and the pairing stops
        /// depending on how far the model has been pushed out of shape.
        public static SymmetryMap Build(Vector3[] vertices, int[] triangles, int axis, float tolerance)
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

            // Pass 3: grow that pairing along the mesh's own edges. Everything above is a
            // distance test, and a distance test can only ever pair the parts of the model that
            // have not drifted far - which on a model that needs repairing is precisely the parts
            // that did not need it.
            if (triangles != null && triangles.Length >= 3)
            {
                map.BuildAdjacency(triangles);
                map.Propagate(vertices);
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

        /// Fills the CSR neighbour tables from a triangle list. Duplicates are removed per vertex
        /// (every interior edge is named by two triangles) so a neighbour is visited once, which
        /// matters because Propagate's inner loop is a product of two neighbour lists.
        private void BuildAdjacency(int[] triangles)
        {
            int n = _partner.Length;
            var degree = new int[n];
            int triEnd = triangles.Length - 2;

            for (int t = 0; t < triEnd; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (a < 0 || a >= n || b < 0 || b >= n || c < 0 || c >= n) continue;
                degree[a] += 2; degree[b] += 2; degree[c] += 2;
            }

            _adjStart = new int[n];
            int total = 0;
            for (int i = 0; i < n; i++) { _adjStart[i] = total; total += degree[i]; }

            var raw = new int[total];
            var fill = new int[n];
            for (int t = 0; t < triEnd; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
                if (a < 0 || a >= n || b < 0 || b >= n || c < 0 || c >= n) continue;
                raw[_adjStart[a] + fill[a]++] = b; raw[_adjStart[a] + fill[a]++] = c;
                raw[_adjStart[b] + fill[b]++] = a; raw[_adjStart[b] + fill[b]++] = c;
                raw[_adjStart[c] + fill[c]++] = a; raw[_adjStart[c] + fill[c]++] = b;
            }

            _adjCount = new int[n];
            for (int i = 0; i < n; i++)
            {
                int start = _adjStart[i], len = fill[i];
                if (len == 0) continue;

                System.Array.Sort(raw, start, len);
                int write = start + 1;
                for (int read = start + 1; read < start + len; read++)
                    if (raw[read] != raw[write - 1]) raw[write++] = raw[read];
                _adjCount[i] = write - start;
            }

            _adjNeighbours = raw;
        }

        /// Grows the pairing outward along the surface: where i and j are already partners, a
        /// neighbour of i should pair with a neighbour of j, and the only question left is which
        /// one. That is a far weaker demand than the seeding pass makes - it is asked of a
        /// handful of candidates that the topology has already vouched for, instead of of every
        /// vertex in a tolerance ball - so it keeps working long after the two sides have drifted
        /// past any distance the seeding pass could safely use.
        ///
        /// This is what decides whether a repair is exact. Distance-only pairing left 540 of a
        /// 17,650-vertex sculpted torso unpaired, and MakeSymmetric cannot move a vertex it has
        /// no counterpart for: those 540 stayed exactly where they were while every vertex around
        /// them snapped onto its mirror, which is visible as the repair "not quite lining up".
        /// Propagation pairs all but 136 of them - and what is left over after it is genuinely
        /// unpairable, being the places where the two halves are not even tessellated alike
        /// (SymmetryTools.CarryUnmatched is what those need instead).
        ///
        /// Rounds are batched rather than run vertex-by-vertex, and each round's candidates are
        /// accepted closest-first, for the same reason the seeding pass is greedy: the order
        /// vertices happen to be visited in must not decide which of two near-equal candidates
        /// wins, or the map stops being a property of the geometry.
        private void Propagate(Vector3[] vertices)
        {
            float limitSqr = Tolerance * PropagationReach;
            limitSqr *= limitSqr;
            int n = _partner.Length;
            var edges = new List<Edge>();

            for (int round = 0; round < MaxPropagationRounds; round++)
            {
                edges.Clear();

                for (int i = 0; i < n; i++)
                {
                    // An on-plane vertex is its own reflection, so it seeds propagation into
                    // BOTH sides at once - which is what carries the pairing off the centreline
                    // on a model whose halves only meet there.
                    int j = _onPlane[i] ? i : _partner[i];
                    if (j == NoPartner) continue;

                    int iCount = _adjCount[i], jCount = _adjCount[j];
                    for (int x = 0; x < iCount; x++)
                    {
                        int a = _adjNeighbours[_adjStart[i] + x];
                        if (_onPlane[a] || _partner[a] != NoPartner) continue;

                        Vector3 target = Reflect(vertices[a], Axis);
                        float sideA = Coord(vertices[a], Axis);

                        for (int y = 0; y < jCount; y++)
                        {
                            int b = _adjNeighbours[_adjStart[j] + y];
                            if (b == a || _onPlane[b] || _partner[b] != NoPartner) continue;
                            // Same rule the seeding pass uses: a reflection lives across the
                            // plane, so two vertices on one side are never each other's partner
                            // however close the reflected position happens to land.
                            if (Coord(vertices[b], Axis) * sideA > 0f) continue;

                            float d = (vertices[b] - target).sqrMagnitude;
                            if (d <= limitSqr) edges.Add(new Edge { Sqr = d, A = a, B = b });
                        }
                    }
                }

                if (edges.Count == 0) return;
                edges.Sort((p, q) => p.Sqr.CompareTo(q.Sqr));

                int accepted = 0;
                for (int e = 0; e < edges.Count; e++)
                {
                    Edge edge = edges[e];
                    if (_partner[edge.A] != NoPartner || _partner[edge.B] != NoPartner) continue;
                    _partner[edge.A] = edge.B;
                    _partner[edge.B] = edge.A;
                    accepted++;
                }

                if (accepted == 0) return;
                PropagatedPairCount += accepted;
            }
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
