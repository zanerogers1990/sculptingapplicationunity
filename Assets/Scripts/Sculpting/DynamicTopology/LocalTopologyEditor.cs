using System.Collections.Generic;
using UnityEngine;

namespace Sculpting.DynamicTopology
{
    /// The three topology operations a local remesh is built from - split, collapse, flip - each
    /// applied to one edge, each leaving the mesh 2-manifold, and each recording what it touched
    /// into a TopologyPatch.
    ///
    /// Works directly on SculptableMesh's index buffer and MeshAdjacency, editing both in place.
    /// That is the whole point: the alternative (build a half-edge structure, operate, write a new
    /// mesh out) costs a pass over the entire model per refine and renumbers every vertex, which
    /// would invalidate the mask, the undo stack's vertex indices and every hidden-triangle flag.
    /// Here a vertex index means the same vertex for as long as the object lives, and a triangle
    /// slot is only ever rewritten in place or appended past the end.
    ///
    /// Plain single-threaded C#, deliberately - see DynamicTopologyRemesher's remarks.
    internal sealed class LocalTopologyEditor
    {
        private readonly SculptableMesh _mesh;
        private readonly MeshAdjacency _topology;
        private readonly TopologyPatch _patch;

        // Retired triangle slots, handed back out by the next append. A collapse retires two
        // triangles and a split creates one per incident face, so over a stroke splits dominate
        // and this stays close to empty - but without it a long collapse-heavy pass would grow the
        // index buffer with holes that nothing ever reclaims.
        private readonly List<int> _freeTriangles = new List<int>();

        // Scratch for the adjacency rewrites. Reused rather than allocated per operation: a refine
        // runs thousands of these.
        private readonly List<int> _scratchA = new List<int>();
        private readonly List<int> _scratchB = new List<int>();
        private readonly List<int> _affected = new List<int>();

        public int Operations { get; private set; }

        public LocalTopologyEditor(SculptableMesh mesh, MeshAdjacency topology, TopologyPatch patch)
        {
            _mesh = mesh;
            _topology = topology;
            _patch = patch;
        }

        private int[] Tris => _mesh.Triangles;
        private Vector3[] Verts => _mesh.Vertices;

        // ------------------------------------------------------------------------ queries

        /// The triangles using BOTH a and b - one on a boundary edge, two in the interior, and
        /// more only on non-manifold geometry, which every operation here refuses to touch.
        /// Walks a's incident list rather than intersecting two sets: valence is single digits.
        private int EdgeTriangles(int a, int b, out int t0, out int t1)
        {
            t0 = t1 = -1;
            int found = 0;
            int[] tris = Tris;
            int start = _topology.TriangleStart[a], end = start + _topology.TriangleCount[a];
            for (int k = start; k < end; k++)
            {
                int t = _topology.TriangleIndices[k];
                int bse = t * 3;
                if (tris[bse] != b && tris[bse + 1] != b && tris[bse + 2] != b) continue;
                if (found == 0) t0 = t;
                else if (found == 1) t1 = t;
                found++;
            }
            return found;
        }

        /// The corner of triangle `t` that is neither a nor b.
        private int Opposite(int t, int a, int b)
        {
            int bse = t * 3;
            int[] tris = Tris;
            for (int k = 0; k < 3; k++)
            {
                int v = tris[bse + k];
                if (v != a && v != b) return v;
            }
            return -1;
        }

        /// True if triangle `t` traverses the edge from -> to in its own winding order. On a
        /// consistently wound surface exactly one of an interior edge's two triangles does.
        private bool HasDirectedEdge(int t, int from, int to)
        {
            int b = t * 3;
            int[] tris = Tris;
            for (int k = 0; k < 3; k++)
                if (tris[b + k] == from && tris[b + (k + 1) % 3] == to) return true;
            return false;
        }

        /// Normalized triangle quality: 1 for equilateral, falling to 0 as the three points become
        /// collinear. The standard 4*sqrt(3)*area / (sum of squared edge lengths) measure.
        private static float Quality(Vector3 p0, Vector3 p1, Vector3 p2)
        {
            float sumSqr = (p1 - p0).sqrMagnitude + (p2 - p1).sqrMagnitude + (p0 - p2).sqrMagnitude;
            if (sumSqr <= 1e-20f) return 0f;
            float area = Vector3.Cross(p1 - p0, p2 - p0).magnitude * 0.5f;
            return 6.9282032f * area / sumSqr; // 4 * sqrt(3)
        }

        /// Below this, a triangle is a sliver: so thin that its normal is dominated by rounding,
        /// which is what puts the streaky black creases across a refined surface.
        ///
        /// An operation is refused when it would produce a triangle under the floor - UNLESS the
        /// one it replaces was already worse, because otherwise the passes could never repair a
        /// mesh that arrived in bad shape, which is exactly the mesh a user reaches for this
        /// feature on. Checking only for INVERSION, as this used to, let a collapse flatten a
        /// triangle to a millionth of its area and still call it valid.
        private const float QualityFloor = 0.05f;

        private static bool QualityAcceptable(Vector3 p0, Vector3 p1, Vector3 p2, float previousQuality)
        {
            float quality = Quality(p0, p1, p2);
            return quality >= QualityFloor || quality >= previousQuality;
        }

        private bool AreNeighbors(int a, int b)
        {
            int start = _topology.NeighborStart[a], end = start + _topology.NeighborCount[a];
            for (int k = start; k < end; k++)
                if (_topology.NeighborIndices[k] == b) return true;
            return false;
        }

        /// True if the vertex sits on an open boundary: some edge of its one-ring belongs to only
        /// one triangle. Boundary vertices are handled conservatively everywhere below - a
        /// sculpting mesh is normally closed, and the operations that misbehave on a boundary do so
        /// by tearing it.
        private bool IsBoundary(int v)
        {
            if (_boundaryCache.TryGetValue(v, out bool cached)) return cached;

            bool boundary = false;
            int start = _topology.NeighborStart[v], end = start + _topology.NeighborCount[v];
            for (int k = start; k < end; k++)
            {
                if (EdgeTriangles(v, _topology.NeighborIndices[k], out _, out _) == 2) continue;
                boundary = true;
                break;
            }
            _boundaryCache[v] = boundary;
            return boundary;
        }

        // Memoized because the split pass asks about both endpoints of every candidate edge, and
        // the test itself walks the vertex's whole one-ring asking about each edge in turn - which
        // without this is quadratic in valence, per edge, on the hottest pass here. Keyed by vertex
        // and thrown away with the editor at the end of each refine, so it can never go stale
        // against topology a later refine changes. Entries ARE invalidated in place when an
        // operation rewires a vertex - see Invalidate.
        private readonly Dictionary<int, bool> _boundaryCache = new Dictionary<int, bool>();

        private void InvalidateBoundary(int v) => _boundaryCache.Remove(v);

        // ------------------------------------------------------------------------- split

        /// Splits edge (a, b) at its midpoint, turning each incident triangle into two. Returns the
        /// new vertex, or -1 if the edge is not an interior manifold edge.
        ///
        /// Always legal on such an edge, which is why it needs no validity test: the midpoint lies
        /// on the edge, so neither half can be inverted or degenerate relative to the original.
        ///
        /// BOUNDARY EDGES ARE REFUSED, and that is not conservatism about open meshes. Almost every
        /// "boundary" in a closed sculpt is a UV seam, where the two sides are geometrically
        /// coincident but topologically separate - Unity's own sphere primitive has 252 of them
        /// (515 vertices for 386 distinct positions). The two sides are separate edges, so a refine
        /// can split one and not the other whenever the operation budget happens to fall between
        /// them. Nothing looks wrong at that moment, because the new vertex lies exactly on the
        /// other side's edge - and then the next dab moves it, and the seam opens into a visible
        /// tear running the length of the model. Leaving seam edges at their original tessellation
        /// costs a thin band of coarser triangles and cannot tear.
        public int SplitEdge(int a, int b)
        {
            int count = EdgeTriangles(a, b, out int t0, out int t1);
            if (count != 2) return -1;

            // And nothing in the seam's one-ring either. Refusing only the seam edge itself is not
            // enough: the triangles ALONG a seam then have one edge that can never be split while
            // their other two are split again and again, so they grow steadily thinner until they
            // are slivers - measured as the worst triangles in the whole mesh after six refines.
            // Holding the whole first ring back keeps that band at its original tessellation, which
            // is a density step rather than a defect.
            if (IsBoundary(a) || IsBoundary(b)) return -1;

            Vector3[] verts = Verts;
            Vector3[] normals = _mesh.Normals;
            float[] mask = _mesh.Mask;

            Vector3 position = (verts[a] + verts[b]) * 0.5f;
            Vector3 normal = VectorMath.NormalizeOr(normals[a] + normals[b], normals[a]);
            // The mask is interpolated, not inherited: a new vertex on the boundary of a masked
            // region has to be half protected, or refining along that boundary would carve a
            // sculptable channel straight through it.
            float newMask = (mask[a] + mask[b]) * 0.5f;
            float cavity = (_mesh.CurvatureRawAt(a) + _mesh.CurvatureRawAt(b)) * 0.5f;

            int m = _mesh.AppendVertex(position, normal, newMask, cavity);
            _patch.EnsureCapacity(_mesh.Vertices.Length, _mesh.Triangles.Length);
            _patch.SetCounts(_mesh.VertexCount, _mesh.TriangleCount * 3);

            _affected.Clear();
            _affected.Add(a);
            _affected.Add(b);
            _affected.Add(m);

            SplitOneSide(t0, a, b, m);
            SplitOneSide(t1, a, b, m);

            for (int i = 0; i < _affected.Count; i++) RebuildNeighbors(_affected[i]);
            for (int i = 0; i < _affected.Count; i++)
            {
                _patch.RecordTouchedVertex(_affected[i]);
                InvalidateBoundary(_affected[i]);
            }

            Operations++;
            return m;
        }

        // Rewrites `t` as the half on a's side and appends the half on b's side, preserving the
        // triangle's winding - which is not optional: a single reversed triangle reads as a black
        // hole in the surface under any lighting, and breaks the inside/outside sign every voxel
        // operation in this project depends on.
        private void SplitOneSide(int t, int a, int b, int m)
        {
            int bse = t * 3;
            int[] tris = Tris;

            // Find the corner where a is followed by b (a -> b -> c) or b by a (b -> a -> c).
            int first = -1, second = -1, third = -1;
            for (int k = 0; k < 3; k++)
            {
                int v0 = tris[bse + k], v1 = tris[bse + (k + 1) % 3], v2 = tris[bse + (k + 2) % 3];
                if ((v0 == a && v1 == b) || (v0 == b && v1 == a)) { first = v0; second = v1; third = v2; break; }
            }
            if (first < 0) return;

            int c = third;
            _patch.RecordTriangleBefore(t, tris);

            // (first, second, c) becomes (first, m, c) plus (m, second, c) - both keep the
            // original's orientation, and their shared edge (m, c) is traversed in opposite
            // directions by the two, which is what manifold consistency means.
            _mesh.SetTriangle(t, first, m, c);
            int nt = AppendTriangle(m, second, c, t);

            // `second` is no longer a corner of t; m and the new triangle take its place.
            RemoveIncident(second, t);
            AddIncident(m, t);
            AddIncident(m, nt);
            AddIncident(second, nt);
            AddIncident(c, nt);

            AddAffected(c);
        }

        // ---------------------------------------------------------------------- collapse

        /// Collapses edge (a, b) onto a, if that is legal and does not wreck the surface. Returns
        /// true if it happened.
        ///
        /// Three separate refusals, and all three are needed:
        ///
        /// - THE LINK CONDITION. Collapsing (a, b) is topologically safe only when the vertices
        ///   adjacent to BOTH a and b are exactly the ones opposite the edge (two in the interior,
        ///   one on a boundary). Any extra shared neighbour means a and b are joined by a path
        ///   outside the edge, and merging them folds that path into a non-manifold spike - the
        ///   classic way a decimator quietly produces a mesh nothing downstream can process.
        /// - BOUNDARY. Collapsing a boundary vertex into an interior one drags the open edge
        ///   inward and eats the border.
        /// - GEOMETRY. Even a legal collapse can flip a triangle inside out if b's other
        ///   neighbours lie past a. Every surviving triangle is re-tested against its own previous
        ///   normal, and the collapse is abandoned whole if any of them turns over.
        public bool CollapseEdge(int a, int b)
        {
            if (a == b) return false;
            int shared = EdgeTriangles(a, b, out int t0, out int t1);
            if (shared == 0 || shared > 2) return false;

            // Never pull a boundary into the interior. Collapsing the other way round is fine, so
            // the caller can retry with the endpoints swapped.
            if (IsBoundary(b) && !IsBoundary(a)) return false;

            int opposite0 = Opposite(t0, a, b);
            int opposite1 = shared == 2 ? Opposite(t1, a, b) : -1;
            if (opposite0 < 0 || (shared == 2 && opposite1 < 0)) return false;

            // The link condition, counted rather than set-intersected: valence is single digits,
            // so walking b's neighbours and testing each against a's list is cheaper than building
            // anything.
            int start = _topology.NeighborStart[b], end = start + _topology.NeighborCount[b];
            for (int k = start; k < end; k++)
            {
                int n = _topology.NeighborIndices[k];
                if (n == a || n == opposite0 || n == opposite1) continue;
                if (AreNeighbors(a, n)) return false;
            }

            Vector3[] verts = Verts;
            Vector3 target = verts[a];
            if (!CollapseKeepsTrianglesValid(a, b, t0, t1, target)) return false;

            _affected.Clear();
            AddAffected(a);
            AddAffected(opposite0);
            if (opposite1 >= 0) AddAffected(opposite1);

            // Retire the two triangles along the edge, then point everything else that used b at a.
            RetireTriangle(t0, a, b, opposite0);
            if (shared == 2) RetireTriangle(t1, a, b, opposite1);

            int[] tris = Tris;
            _scratchA.Clear();
            CopyIncident(b, _scratchA);
            for (int i = 0; i < _scratchA.Count; i++)
            {
                int t = _scratchA[i];
                int bse = t * 3;
                _patch.RecordTriangleBefore(t, tris);
                for (int k = 0; k < 3; k++)
                {
                    if (tris[bse + k] != b) { AddAffected(tris[bse + k]); continue; }
                    tris[bse + k] = a;
                }
                AddIncident(a, t);
            }

            // b keeps its slot in every buffer - removing it would renumber every vertex after it
            // and take the mask, the undo deltas and the mirror pairing with it. Parked on top of
            // a instead, where nothing references it, nothing draws it, and a brush that happens to
            // pick it up moves a point that is already exactly where it is.
            SetIncident(b, null, 0);
            SetNeighbors(b, null, 0);
            verts[b] = target;
            _mesh.Normals[b] = _mesh.Normals[a];
            _patch.RecordTouchedVertex(b);

            for (int i = 0; i < _affected.Count; i++) RebuildNeighbors(_affected[i]);
            for (int i = 0; i < _affected.Count; i++)
            {
                _patch.RecordTouchedVertex(_affected[i]);
                InvalidateBoundary(_affected[i]);
            }
            InvalidateBoundary(b);

            Operations++;
            return true;
        }

        // Would any triangle that survives the collapse turn inside out? Measured as a sign change
        // of the face normal rather than an absolute orientation test, so it is independent of how
        // the surface happens to be oriented in space.
        private bool CollapseKeepsTrianglesValid(int a, int b, int t0, int t1, Vector3 target)
        {
            Vector3[] verts = Verts;
            int[] tris = Tris;
            int start = _topology.TriangleStart[b], end = start + _topology.TriangleCount[b];
            for (int k = start; k < end; k++)
            {
                int t = _topology.TriangleIndices[k];
                if (t == t0 || t == t1) continue;

                int bse = t * 3;
                Vector3 q0 = verts[tris[bse]], q1 = verts[tris[bse + 1]], q2 = verts[tris[bse + 2]];
                Vector3 before = Vector3.Cross(q1 - q0, q2 - q0);
                float previousQuality = Quality(q0, q1, q2);

                Vector3 p0 = q0, p1 = q1, p2 = q2;
                if (tris[bse] == b) p0 = target;
                else if (tris[bse + 1] == b) p1 = target;
                else p2 = target;
                Vector3 after = Vector3.Cross(p1 - p0, p2 - p0);

                // A triangle that collapses to nothing is as bad as one that inverts: it has no
                // normal for anything downstream to read.
                if (after.sqrMagnitude <= 1e-20f) return false;
                if (Vector3.Dot(before, after) <= 0f) return false;
                if (!QualityAcceptable(p0, p1, p2, previousQuality)) return false;
            }
            return true;
        }

        // -------------------------------------------------------------------------- flip

        /// Flips edge (a, b) to the other diagonal of the quad its two triangles form, when that
        /// brings the four corners' valences closer to 6 - the valence a vertex has on a regular
        /// triangulation, and what the whole pass is steering towards.
        public bool FlipEdge(int a, int b)
        {
            if (EdgeTriangles(a, b, out int t0, out int t1) != 2) return false;

            int c = Opposite(t0, a, b);
            int d = Opposite(t1, a, b);
            if (c < 0 || d < 0 || c == d) return false;

            // The new edge must not already exist: creating a second copy of it would make the two
            // triangles share two edges, which is not a surface.
            if (AreNeighbors(c, d)) return false;
            if (IsBoundary(a) || IsBoundary(b)) return false;

            int va = _topology.NeighborCount[a], vb = _topology.NeighborCount[b];
            int vc = _topology.NeighborCount[c], vd = _topology.NeighborCount[d];
            // Flipping moves one unit of valence off a and b and onto c and d.
            int before = Deviation(va) + Deviation(vb) + Deviation(vc) + Deviation(vd);
            int after = Deviation(va - 1) + Deviation(vb - 1) + Deviation(vc + 1) + Deviation(vd + 1);
            if (after >= before) return false;

            // WHICH triangle traverses the edge a->b decides the winding of both replacements, and
            // EdgeTriangles hands them back in incident-list order, which says nothing about that.
            // Assuming it - as this did - produces a correctly shaped quad wound inside out half
            // the time.
            int abTriangle, baTriangle, abOpposite, baOpposite;
            if (HasDirectedEdge(t0, a, b)) { abTriangle = t0; abOpposite = c; baTriangle = t1; baOpposite = d; }
            else if (HasDirectedEdge(t1, a, b)) { abTriangle = t1; abOpposite = d; baTriangle = t0; baOpposite = c; }
            else return false; // both traverse it the same way: the surface is already inconsistent

            if (!HasDirectedEdge(baTriangle, b, a)) return false;

            Vector3[] verts = Verts;
            if (!FlipKeepsTrianglesValid(verts[a], verts[b], verts[abOpposite], verts[baOpposite], t0, t1)) return false;

            int[] tris = Tris;
            _patch.RecordTriangleBefore(t0, tris);
            _patch.RecordTriangleBefore(t1, tris);

            // The quad's boundary runs b -> abOpposite -> a -> baOpposite -> b, taking the four
            // outer edges straight from the two triangles being replaced. Cutting it along the
            // other diagonal gives (b, abOpposite, baOpposite) and (abOpposite, a, baOpposite):
            // together they cover the same quad, they traverse the new edge in opposite directions
            // - which is what manifold consistency means - and every one of the four outer edges
            // still points the way it already did.
            _mesh.SetTriangle(abTriangle, b, abOpposite, baOpposite);
            _mesh.SetTriangle(baTriangle, abOpposite, a, baOpposite);

            // abTriangle held {a, b, abOpposite} and now holds {b, abOpposite, baOpposite};
            // baTriangle held {a, b, baOpposite} and now holds {abOpposite, a, baOpposite}.
            RemoveIncident(a, abTriangle);
            AddIncident(baOpposite, abTriangle);
            RemoveIncident(b, baTriangle);
            AddIncident(abOpposite, baTriangle);

            _affected.Clear();
            AddAffected(a);
            AddAffected(b);
            AddAffected(c);
            AddAffected(d);
            for (int i = 0; i < _affected.Count; i++) RebuildNeighbors(_affected[i]);
            for (int i = 0; i < _affected.Count; i++)
            {
                _patch.RecordTouchedVertex(_affected[i]);
                InvalidateBoundary(_affected[i]);
            }

            Operations++;
            return true;
        }

        private static int Deviation(int valence) => Mathf.Abs(valence - 6);

        // The flipped pair has to face the same way the original pair did. On a curved surface the
        // two halves of a quad are not coplanar, so each new triangle is compared against the
        // average of the two it replaces rather than against either one.
        private bool FlipKeepsTrianglesValid(Vector3 pa, Vector3 pb, Vector3 pc, Vector3 pd, int t0, int t1)
        {
            Vector3[] verts = Verts;
            int[] tris = Tris;
            Vector3 reference = FaceNormal(verts, tris, t0) + FaceNormal(verts, tris, t1);

            // The two triangles the flip is about to write - (b, c, d) and (c, a, d), where c is
            // the corner opposite the edge in the a->b triangle and d the one in the b->a triangle.
            Vector3 n0 = Vector3.Cross(pc - pb, pd - pb);
            Vector3 n1 = Vector3.Cross(pa - pc, pd - pc);
            if (n0.sqrMagnitude <= 1e-20f || n1.sqrMagnitude <= 1e-20f) return false;
            if (Vector3.Dot(reference, n0) <= 0f || Vector3.Dot(reference, n1) <= 0f) return false;

            // A flip is chosen on VALENCE alone, which says nothing about shape - and the other
            // diagonal of a long thin quad is longer still, so the valence win can be paid for with
            // two slivers. Refused unless the pair it replaces was at least as bad.
            float worstBefore = Mathf.Min(Quality(verts[tris[t0 * 3]], verts[tris[t0 * 3 + 1]], verts[tris[t0 * 3 + 2]]),
                                          Quality(verts[tris[t1 * 3]], verts[tris[t1 * 3 + 1]], verts[tris[t1 * 3 + 2]]));
            return QualityAcceptable(pb, pc, pd, worstBefore) && QualityAcceptable(pc, pa, pd, worstBefore);
        }

        private static Vector3 FaceNormal(Vector3[] verts, int[] tris, int t)
        {
            int b = t * 3;
            Vector3 p0 = verts[tris[b]];
            return Vector3.Cross(verts[tris[b + 1]] - p0, verts[tris[b + 2]] - p0);
        }

        // -------------------------------------------------------- triangle slot bookkeeping

        private int AppendTriangle(int a, int b, int c, int inheritHiddenFrom)
        {
            int t;
            if (_freeTriangles.Count > 0)
            {
                t = _freeTriangles[_freeTriangles.Count - 1];
                _freeTriangles.RemoveAt(_freeTriangles.Count - 1);
                _patch.RecordTriangleBefore(t, Tris);
                _mesh.SetTriangle(t, a, b, c);
            }
            else
            {
                t = _mesh.AppendTriangle(a, b, c);
                _patch.EnsureCapacity(_mesh.Vertices.Length, _mesh.Triangles.Length);
                _patch.SetCounts(_mesh.VertexCount, _mesh.TriangleCount * 3);
            }

            // A triangle born inside a hidden region has to be hidden too, or refining under a
            // hidden area punches visible holes through it.
            bool[] hidden = _mesh.HiddenTriangles;
            if (hidden != null && inheritHiddenFrom >= 0 && inheritHiddenFrom < hidden.Length)
                _mesh.SetTriangleHiddenFlag(t, hidden[inheritHiddenFrom]);
            return t;
        }

        // Retires the triangle along a collapsed edge: blanked rather than removed, because
        // removing it would renumber every triangle after it - see MeshAdjacency.Build for what
        // reads these blanks, and _freeTriangles for what reclaims the slot.
        private void RetireTriangle(int t, int a, int b, int opposite)
        {
            _patch.RecordTriangleBefore(t, Tris);
            _mesh.SetTriangle(t, 0, 0, 0);
            RemoveIncident(a, t);
            RemoveIncident(b, t);
            if (opposite >= 0) RemoveIncident(opposite, t);
            _freeTriangles.Add(t);
        }

        // ------------------------------------------------------------ adjacency bookkeeping

        private void CopyIncident(int v, List<int> into)
        {
            int start = _topology.TriangleStart[v], end = start + _topology.TriangleCount[v];
            for (int k = start; k < end; k++) into.Add(_topology.TriangleIndices[k]);
        }

        private void AddIncident(int v, int t)
        {
            _scratchB.Clear();
            CopyIncident(v, _scratchB);
            for (int i = 0; i < _scratchB.Count; i++)
                if (_scratchB[i] == t) return;
            _scratchB.Add(t);
            SetIncident(v, _scratchB, _scratchB.Count);
        }

        private void RemoveIncident(int v, int t)
        {
            _scratchB.Clear();
            int start = _topology.TriangleStart[v], end = start + _topology.TriangleCount[v];
            for (int k = start; k < end; k++)
            {
                int ti = _topology.TriangleIndices[k];
                if (ti != t) _scratchB.Add(ti);
            }
            SetIncident(v, _scratchB, _scratchB.Count);
        }

        private void SetIncident(int v, List<int> values, int count) =>
            _topology.SetIncidentTriangles(v, values ?? _scratchB, count);

        private void SetNeighbors(int v, List<int> values, int count) =>
            _topology.SetNeighbors(v, values ?? _scratchA, count);

        /// Re-derives one vertex's neighbour list from its (already updated) incident triangles,
        /// in the same order MeshAdjacency.Build produces - triangle order, then corner order,
        /// first occurrence wins. Matching that order is not housekeeping: every Laplacian in the
        /// app sums neighbours in array order, so a different order moves results in the last bits
        /// of a float, which is the resolution the mirror-symmetry tests measure at.
        private void RebuildNeighbors(int v)
        {
            _scratchA.Clear();
            int[] tris = Tris;
            int start = _topology.TriangleStart[v], end = start + _topology.TriangleCount[v];
            for (int k = start; k < end; k++)
            {
                int bse = _topology.TriangleIndices[k] * 3;
                int c0 = tris[bse], c1 = tris[bse + 1], c2 = tris[bse + 2];
                int p, q;
                if (c0 == v) { p = c1; q = c2; }
                else if (c1 == v) { p = c0; q = c2; }
                else { p = c0; q = c1; }

                if (!_scratchA.Contains(p)) _scratchA.Add(p);
                if (!_scratchA.Contains(q)) _scratchA.Add(q);
            }
            _topology.SetNeighbors(v, _scratchA, _scratchA.Count);
        }

        private void AddAffected(int v)
        {
            if (v < 0) return;
            for (int i = 0; i < _affected.Count; i++)
                if (_affected[i] == v) return;
            _affected.Add(v);
        }
    }
}
