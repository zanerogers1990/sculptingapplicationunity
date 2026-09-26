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
    /// MirrorController.GetSymmetry and SculptController's apply sites). That approach
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

        /// How far a topological propagation step may reach, as a multiple of the mesh's VERTEX
        /// SPACING (MeanSpacing), falling back to Tolerance only when there are no triangles to
        /// measure spacing from.
        ///
        /// Propagation (see Propagate) grows the pairing along the mesh's own edges out of pairs
        /// already found, so the distance test is no longer being asked "are these two the same
        /// vertex" on its own - the topology has already said they are the corresponding
        /// neighbours of a known pair, and this only has to be wide enough to cover how far the
        /// two sides have DRIFTED. That is a different, much looser question than the seeding pass
        /// answers, which is why it gets its own, wider radius.
        ///
        /// Decoupled from Tolerance because those two must be free to move in opposite directions,
        /// and coupling them broke this outright. Tolerance has to stay BELOW the vertex spacing or
        /// it pairs a vertex with its reflection's neighbour; drift has to be allowed to reach WELL
        /// PAST it, because a single sculpt stroke moves a surface much further than one spacing.
        /// As a multiple of Tolerance the reach collapsed along with it the moment Tolerance was
        /// correctly tightened - measured: pairs fell from 31,905 to 16,697 and MakeSymmetric began
        /// refusing the repair outright as too asymmetric.
        ///
        /// Generous on purpose. This is only a coarse "not somewhere else entirely" bound; the
        /// discrimination is done by the structural residual in Propagate, which does not care how
        /// far the halves have drifted, only whether a candidate sits where the LOCAL shape says it
        /// should.
        public const float PropagationReach = 8f;

        /// The other half of that bound, as a fraction of the bounding-box diagonal, and the one
        /// that governs on a dense mesh.
        ///
        /// Drift between two halves is a WORLD-SPACE quantity - it is how far a stroke pushed the
        /// surface, which does not change when the mesh is re-tessellated more finely. Vertex
        /// spacing does. So a reach expressed only in spacings silently tightens as density rises,
        /// which is the identical unit error that made Tolerance too loose, running the other way:
        /// measured across the same one-sided sculpt, a spacings-only reach paired 33,207 vertices
        /// at 134k triangles but only 106,121 of 496,178 at 992k, and MakeSymmetric refused the
        /// repair at both 357k and 992k. Taking whichever of the two bounds is LARGER keeps a
        /// coarse mesh's few-spacings reach while letting a dense one span the same world distance
        /// it always could.
        public const float PropagationDriftFraction = 0.10f;

        /// Propagation is a fixed point - each round can only pair vertices adjacent to a pair
        /// found in an earlier one - so it stops on its own. The cap is a guard against a
        /// pathological mesh, not a tuning knob.
        ///
        /// One round advances the pairing by one ring of vertices, so the number of rounds a mesh
        /// needs scales with how many rings wide its unpaired regions are - which is a WORLD
        /// distance divided by the vertex spacing, and therefore grows as a mesh gets denser. At 64
        /// this bound silently bit: the region left unpaired by a stroke of a given brush radius is
        /// about radius/spacing rings across, which measured ~26 rings at 134k triangles (settles
        /// well inside the cap) and ~75 at 1.08M (stalls against it). The visible result was that
        /// the identical repair succeeded on a coarse mesh and was refused as "too asymmetric" on a
        /// dense one - 107,897 vertices left unpaired at 1.08M purely because propagation ran out
        /// of rounds with work still to do.
        ///
        /// Affordable at this size only because a round now costs O(frontier), not O(vertex count)
        /// - see Propagate. The loop still exits the moment a round accepts nothing, so a mesh that
        /// settles in three rounds pays for three.
        private const int MaxPropagationRounds = 4096;

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

        /// The mesh's own vertex spacing, measured when Build is given triangles. Propagation
        /// judges candidates against this rather than against Tolerance alone - see Propagate.
        public float MeanSpacing { get; private set; }

        /// The model's bounding-box diagonal - the scale drift is measured in, as against
        /// MeanSpacing, the scale a vertex's identity is measured in. See PropagationDriftFraction.
        public float Diagonal { get; private set; }

        /// How far a propagated pair's LOCAL offset may disagree, as a fraction of MeanSpacing. A
        /// correct pair disagrees only by the remesher's own placement noise (see SpacingFraction)
        /// whatever global drift there is between the halves, because the anchor's drift is
        /// subtracted first; a vertex paired with its reflection's NEIGHBOUR disagrees by about a
        /// full spacing. 0.75 clears the measured noise ceiling of ~0.73 while staying under that.
        ///
        /// This can afford to sit above SpacingFraction precisely because it is a different, much
        /// sharper measurement: the seeding pass compares absolute positions, so it eats the full
        /// placement noise of both vertices, while this compares each candidate against what the
        /// LOCAL shape predicts, and neighbouring cells' noise is correlated and largely cancels.
        private const float StructuralFraction = 0.75f;

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

        /// The property that actually matters, and the one every consumer of Tolerance relies on:
        /// the tolerance must stay comfortably BELOW the mesh's vertex spacing. A tolerance
        /// approaching the spacing starts pairing each vertex with its reflection's NEIGHBOUR
        /// rather than with its reflection, and it makes the "on the plane" band (which is this
        /// same value, used as a half-thickness) wide enough to swallow a strip of genuine
        /// geometry either side of the centreline.
        ///
        /// Half the vertex spacing. The two things this has to separate are a correct pair and an
        /// off-by-one-neighbour pair, and those sit at ~0 and ~1 spacing apart, so half a spacing
        /// is the natural divide - and the greedy closest-first matching in Build widens the
        /// margin further, since a true reflection is claimed long before any looser candidate
        /// gets a chance at either end.
        ///
        /// Not tighter than half, because the remesher does not place mirrored vertices at exactly
        /// mirrored positions and the discrepancy GROWS with resolution: Surface Nets puts one
        /// vertex per cell at the average of that cell's edge crossings, so a roughly constant
        /// absolute error in the distance field becomes a larger fraction of a cell as cells
        /// shrink. Measured on a remeshed sphere, the distance from a vertex to the nearest vertex
        /// to its reflection ran mean 0.025 / max 0.47 spacings at 134k triangles and mean 0.061 /
        /// max 0.73 at 1.08M. A tolerance of 0.35 sat below that noise floor and refused to pair
        /// most of a perfectly symmetric model - measured 45.9% of a 992k-triangle sphere left
        /// unmatched, which made MakeSymmetric decline the repair outright.
        private const float SpacingFraction = 0.5f;

        /// Half a percent of the bounding-box diagonal - the original rule, kept as a CEILING for
        /// the coarse meshes it was calibrated on, where it is genuinely below the spacing.
        private const float DiagonalFraction = 0.005f;

        /// A tolerance proportional to the model rather than an absolute number, because this is
        /// used on everything from a default unit sphere to an imported multi-metre scan, and a
        /// fixed epsilon would pair everything on one and nothing on the other.
        ///
        /// Prefer the overload that takes triangles wherever they are to hand. Bounding-box
        /// diagonal alone was the original rule, justified by "half a percent is comfortably below
        /// the vertex spacing of any mesh this app produces (a 500-vertex sphere spaces vertices
        /// ~4% of its diagonal apart)". That holds at 500 vertices and fails as the mesh gets
        /// denser, because spacing shrinks with density while the fraction does not: measured on a
        /// 67,234-vertex remesh of that same sphere, spacing had fallen to 0.502% of the diagonal
        /// and the tolerance came out at 1.00 vertex spacings - exactly the ratio the rule exists
        /// to stay under, and worse still on anything denser. That is the root cause of a repaired
        /// half coming back jagged: at that ratio the pairing can and does pick the reflection's
        /// neighbour, and SnapToPlane flattens a band two spacings wide onto the centreline.
        public static float DefaultTolerance(Vector3[] vertices) => DefaultTolerance(vertices, null);

        /// Tolerance bounded by BOTH the model's overall size and its actual vertex spacing.
        /// Identical to the diagonal-only rule on the coarse meshes that rule was calibrated
        /// against (there the spacing bound is the looser of the two and never binds); on a dense
        /// mesh the spacing bound takes over, which is exactly where the diagonal rule broke down.
        public static float DefaultTolerance(Vector3[] vertices, int[] triangles)
        {
            if (vertices == null || vertices.Length == 0) return 0.001f;

            Vector3 min = vertices[0], max = vertices[0];
            for (int i = 1; i < vertices.Length; i++)
            {
                min = Vector3.Min(min, vertices[i]);
                max = Vector3.Max(max, vertices[i]);
            }
            float tolerance = (max - min).magnitude * DiagonalFraction;

            float spacing = MeanEdgeLength(vertices, triangles);
            if (spacing > 0f) tolerance = Mathf.Min(tolerance, spacing * SpacingFraction);

            return Mathf.Max(tolerance, 1e-6f);
        }

        /// Mean length of one edge per triangle - a cheap, robust stand-in for vertex spacing.
        /// One edge rather than all three because this only needs a scale, not a census, and the
        /// three edges of a triangle are the same length to within the mesh's own regularity.
        /// Returns 0 when there are no triangles to measure.
        public static float MeanEdgeLength(Vector3[] vertices, int[] triangles)
        {
            if (vertices == null || triangles == null || triangles.Length < 3) return 0f;

            double sum = 0;
            int counted = 0;
            for (int t = 0; t + 1 < triangles.Length; t += 3)
            {
                int a = triangles[t], b = triangles[t + 1];
                if (a < 0 || a >= vertices.Length || b < 0 || b >= vertices.Length) continue;
                sum += (vertices[b] - vertices[a]).magnitude;
                counted++;
            }
            return counted > 0 ? (float)(sum / counted) : 0f;
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

            // Inside the centreline band - a CANDIDATE for being on the plane, decided below.
            var inBand = new bool[n];
            for (int i = 0; i < n; i++)
            {
                map._partner[i] = NoPartner;
                inBand[i] = Mathf.Abs(Coord(vertices[i], axis)) <= tol;

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
                float side = Coord(vertices[i], axis);

                // A vertex in the centreline band nominates ITSELF too, at its distance from its own
                // reflection, and competes in the greedy pass with every twin across the plane. It is
                // filed as on the plane only if nothing sits closer to its reflection than it does.
                //
                // Being inside the band used to settle it outright - and the band is half a vertex
                // spacing wide, which on a remesh is exactly where the first row of vertices either
                // side of the plane lands (Surface Nets puts them near the middle of the cells that
                // border it). Measured on a remeshed symmetric sphere: 873 of 1224 "centreline"
                // vertices were in fact one half of a mirrored pair 0.8 spacings apart. SnapToPlane
                // then pulled both halves onto the plane - onto each other - collapsing the edge
                // between them: Make Symmetric turned an already symmetric model into one with 726
                // degenerate triangles down the middle, and Cleanup welded 741 of them away
                // (SymmetryRepairTests).
                if (inBand[i]) edges.Add(new Edge { Sqr = 4f * side * side, A = i, B = i });
                // Exactly on the plane: its own reflection, and nothing else can be.
                if (side == 0f) continue;

                Vector3 target = Reflect(vertices[i], axis);
                Vector3Int home = CellOf(target, cell);

                // A centreline candidate is matched out to its own distance from its reflection, even
                // past Tolerance: any twin nearer than that is a better account of it than "on the
                // plane". The remesher leaves mirrored pairs up to ~0.8 spacings out of true, so with
                // Tolerance alone the noisier straddling pairs found no twin, both fell back to the
                // plane, and Cleanup welded them into each other. Never more than twice Tolerance
                // (the band's own width), so two cells either way always cover it.
                float reachSqr = inBand[i] ? Mathf.Max(tolSqr, 4f * side * side) : tolSqr;
                int reachCells = reachSqr > tolSqr ? 2 : 1;

                for (int z = -reachCells; z <= reachCells; z++)
                for (int y = -reachCells; y <= reachCells; y++)
                for (int x = -reachCells; x <= reachCells; x++)
                {
                    if (!buckets.TryGetValue(new Vector3Int(home.x + x, home.y + y, home.z + z),
                                             out List<int> list)) continue;

                    for (int k = 0; k < list.Count; k++)
                    {
                        int j = list[k];
                        if (j == i) continue;
                        // Must genuinely be across the plane. Without this a pair of vertices
                        // straddling the plane closer together than `tol` could pair with
                        // themselves-ish on the same side, which is not a reflection. Strictly across:
                        // a vertex exactly on the plane is its own twin, never anyone else's.
                        if (Coord(vertices[j], axis) * side >= 0f) continue;

                        float d = (vertices[j] - target).sqrMagnitude;
                        if (d <= reachSqr) edges.Add(new Edge { Sqr = d, A = i, B = j });
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
                if (map._onPlane[edge.A] || map._onPlane[edge.B]) continue;
                if (edge.A == edge.B)
                {
                    map._onPlane[edge.A] = true; // its own reflection was the closest thing to it
                    continue;
                }
                map._partner[edge.A] = edge.B;
                map._partner[edge.B] = edge.A;
            }

            // Pass 3: grow that pairing along the mesh's own edges. Everything above is a
            // distance test, and a distance test can only ever pair the parts of the model that
            // have not drifted far - which on a model that needs repairing is precisely the parts
            // that did not need it.
            if (triangles != null && triangles.Length >= 3)
            {
                map.MeanSpacing = MeanEdgeLength(vertices, triangles);

                Vector3 bmin = vertices[0], bmax = vertices[0];
                for (int i = 1; i < n; i++)
                {
                    bmin = Vector3.Min(bmin, vertices[i]);
                    bmax = Vector3.Max(bmax, vertices[i]);
                }
                map.Diagonal = (bmax - bmin).magnitude;

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
        /// accepted best-first, for the same reason the seeding pass is greedy: the order vertices
        /// happen to be visited in must not decide which of two near-equal candidates wins, or the
        /// map stops being a property of the geometry.
        ///
        /// "Best" is measured STRUCTURALLY - how well b sits relative to its anchor j compared to
        /// where a sits relative to its anchor i - rather than by raw distance from a's reflected
        /// position. The two agree when the halves have not drifted and diverge exactly when they
        /// have, which is the case this pass exists for. Raw distance has to be given a wide
        /// radius to tolerate drift at all (PropagationReach is four Tolerances), and a radius
        /// that wide is several vertex spacings across on a dense mesh - wide enough for the
        /// closest candidate to be the reflection's NEIGHBOUR rather than the reflection. Those
        /// wrong pairs are what MakeSymmetric then writes mismatched positions through, which
        /// shows up as a jagged repaired half and, where a wrong pair inverts a triangle, as
        /// flipped normals. The structural residual cancels whatever drift i and j already carry,
        /// so it stays small for a correct pair however far the halves have moved apart, while an
        /// off-by-one-neighbour candidate scores about a full vertex spacing.
        private void Propagate(Vector3[] vertices)
        {
            // Whichever bound is larger: a few vertex spacings, or a fixed fraction of the model.
            // The first governs a coarse mesh, the second a dense one - see PropagationReach and
            // PropagationDriftFraction for why neither works alone. Tolerance is the fallback only
            // for a map built without measurable topology.
            float spacingReach = (MeanSpacing > 0f ? MeanSpacing : Tolerance) * PropagationReach;
            float driftReach = Diagonal * PropagationDriftFraction;
            float limit = Mathf.Max(spacingReach, driftReach);
            float limitSqr = limit * limit;

            // No spacing to measure against means no structural test either, so such a map
            // behaves exactly as it did before this gate existed.
            float structural = MeanSpacing * StructuralFraction;
            float structuralSqr = MeanSpacing > 0f ? structural * structural : float.MaxValue;

            int n = _partner.Length;
            var edges = new List<Edge>();

            // Only vertices that gained a pairing in the PREVIOUS round can offer anything new
            // this round, so each round walks that frontier instead of rescanning the whole mesh.
            // This is sound because nothing a round rejects can become acceptable later: a
            // candidate is turned away either because its would-be partner is already claimed
            // (claims are never released) or because the geometry does not fit (positions do not
            // change during a build). A vertex that could not pair from one anchor can still pair
            // from a different one - and that anchor, being newly paired itself, is in the
            // frontier by construction.
            //
            // Round 0 seeds from everything the distance pass established, plus the centreline.
            var frontier = new List<int>();
            var nextFrontier = new List<int>();
            for (int i = 0; i < n; i++)
                if (_onPlane[i] || _partner[i] != NoPartner) frontier.Add(i);

            for (int round = 0; round < MaxPropagationRounds && frontier.Count > 0; round++)
            {
                edges.Clear();

                for (int f = 0; f < frontier.Count; f++)
                {
                    int i = frontier[f];
                    // An on-plane vertex is its own reflection, so it seeds propagation into
                    // BOTH sides at once - which is what carries the pairing off the centreline
                    // on a model whose halves only meet there.
                    int j = _onPlane[i] ? i : _partner[i];
                    if (j == NoPartner) continue;

                    // Where the anchor pair's own reflection lands. The residual below is measured
                    // against THIS, so any drift already between i and j cancels out instead of
                    // being charged to every candidate they vouch for.
                    Vector3 anchorReflected = Reflect(vertices[i], Axis);
                    Vector3 anchorDrift = vertices[j] - anchorReflected;

                    int iCount = _adjCount[i], jCount = _adjCount[j];
                    for (int x = 0; x < iCount; x++)
                    {
                        int a = _adjNeighbours[_adjStart[i] + x];
                        if (_onPlane[a] || _partner[a] != NoPartner) continue;

                        Vector3 target = Reflect(vertices[a], Axis);
                        // What b would be if the two halves matched locally as well as the anchor
                        // pair does: a's reflection, carried by the anchor's own drift.
                        Vector3 expected = target + anchorDrift;
                        float sideA = Coord(vertices[a], Axis);

                        for (int y = 0; y < jCount; y++)
                        {
                            int b = _adjNeighbours[_adjStart[j] + y];
                            if (b == a || _onPlane[b] || _partner[b] != NoPartner) continue;
                            // Same rule the seeding pass uses: a reflection lives across the
                            // plane, so two vertices on one side are never each other's partner
                            // however close the reflected position happens to land.
                            if (Coord(vertices[b], Axis) * sideA > 0f) continue;

                            // Both gates must pass. The absolute one keeps the pairing anchored to
                            // real geometry (a candidate cannot be arbitrarily far from a's
                            // reflection however well it matches locally); the structural one is
                            // what actually discriminates between the reflection and its
                            // neighbour.
                            if ((vertices[b] - target).sqrMagnitude > limitSqr) continue;

                            float residual = (vertices[b] - expected).sqrMagnitude;
                            if (residual > structuralSqr) continue;

                            edges.Add(new Edge { Sqr = residual, A = a, B = b });
                        }
                    }
                }

                if (edges.Count == 0) return;
                edges.Sort((p, q) => p.Sqr.CompareTo(q.Sqr));

                nextFrontier.Clear();
                int accepted = 0;
                for (int e = 0; e < edges.Count; e++)
                {
                    Edge edge = edges[e];
                    if (_partner[edge.A] != NoPartner || _partner[edge.B] != NoPartner) continue;
                    _partner[edge.A] = edge.B;
                    _partner[edge.B] = edge.A;
                    // Both ends become anchors for the next round - they are the only vertices
                    // that can vouch for anything new.
                    nextFrontier.Add(edge.A);
                    nextFrontier.Add(edge.B);
                    accepted++;
                }

                if (accepted == 0) return;
                PropagatedPairCount += accepted;

                var swap = frontier;
                frontier = nextFrontier;
                nextFrontier = swap;
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
