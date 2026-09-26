using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Cuts a mesh against a screen-space shape swept along the view direction, deletes what
    /// falls on one side, and closes the opening it leaves. The geometry half of the Trim tool -
    /// see RegionSelectTool for the gesture and TrimTool for the scene-level plumbing, the same
    /// split MeshBoolean/MeshBooleanTool and MeshExtractor already use.
    ///
    /// WHY NOT THE VOXEL BOOLEAN. MeshBoolean could express this: build a prism from the dragged
    /// shape and subtract it. But it works by sampling both operands into a signed distance
    /// field and re-meshing the result, which replaces the WHOLE object's topology with an
    /// evenly-spaced lattice. Trim is supposed to leave the surface you were sculpting exactly as
    /// it was and only touch the boundary - and a cut whose crispness is limited by the voxel
    /// size is precisely what makes voxel booleans read as soft. So this cuts the triangles
    /// directly: every triangle away from the cut survives untouched, and the ones that straddle
    /// it are split exactly on the boundary.
    ///
    /// WHERE THE CUT IS. The classification is the same screen-space test the hide and mask
    /// gestures use (ScreenRegionMask), so what a Trim removes is exactly what a Hide would have
    /// hidden. The exact crossing along a straddling edge is found by BISECTING that same test
    /// rather than by intersecting against plane equations: the swept surface of a lasso is a
    /// bundle of hundreds of planes (and, under a perspective camera, planes through the eye),
    /// and hunting for the flip point in the one predicate that defines the region cannot
    /// disagree with the classification the way a separately-derived plane set can.
    ///
    /// CLOSING THE HOLE. Every clipped triangle contributes one directed edge to the boundary of
    /// what survives; chained together those form the cut loops. Each loop is triangulated in the
    /// swept surface's own 2D coordinates - distance along the dragged outline (see
    /// ScreenRegionMask.ArcPosition) against view depth - NOT flattened onto a best-fit plane.
    /// For a straight cut the two are identical, but for a box the loop bends around the corner
    /// between two walls, and a planar cap there would slice that corner off.
    ///
    /// The cap SHARES the loop's vertices with the shell rather than duplicating them for a hard
    /// crease. Duplicates would render a touch crisper, but they tear apart the moment a brush is
    /// dragged across the rim - and this is a sculpting app, where the cut is rarely the last
    /// thing that happens to that surface.
    public static class MeshTrimmer
    {
        /// Bisection steps for locating a crossing along a straddling edge. Each halves the
        /// interval, so 20 places the cut within a millionth of one edge length - far below the
        /// sub-pixel scale anything here is judged at, and only ever paid on the handful of edges
        /// that actually straddle the boundary.
        private const int BisectionSteps = 20;

        /// How many interior vertices a cap may add per rim vertex before refinement gives up and
        /// leaves the middle of the face coarser than the edge. A roughly circular cut face at
        /// full mesh density wants about 0.18 * rim^2 of them, which for a big cut on a dense
        /// model is more geometry than the cut is worth - and the density that MATTERS is the
        /// density at the rim, which refinement reaches first.
        private const int CapVerticesPerRimVertex = 40;
        private const int MinCapVertexBudget = 512;
        private const int MaxCapVertexBudget = 60000;

        /// Ceilings on the rings a tunnel wall may be divided into - see StitchTunnel. The same
        /// bargain the cap's vertex budget makes: match the surrounding density where that is
        /// affordable, and stop before a deep hole through a dense model adds more geometry than
        /// the trim took away.
        private const int MaxTunnelRings = 400;
        private const int MaxTunnelRingVertices = 4000;
        private const int MaxTunnelWallVertices = 200000;

        /// What one cut produced. `Vertices`/`Triangles` are null unless `Success`.
        public readonly struct Result
        {
            public readonly bool Success;
            public readonly Vector3[] Vertices;
            public readonly int[] Triangles;
            /// Why nothing was cut, when Success is false.
            public readonly string Error;
            /// Cut loops that could not be closed cleanly and fell back to a fan (or were left
            /// open). Zero on any ordinary cut; non-zero is worth telling the user about, since a
            /// hole is invisible until the object is remeshed or booleaned.
            public readonly int OpenLoops;
            /// Triangles the cut face itself contributed. It is filled at the density of the
            /// surface around it (see CapTriangulator), so on a big cut it can easily outnumber
            /// what the trim removed - worth saying out loud rather than letting the triangle
            /// count appear to go the wrong way.
            public readonly int CapTriangles;
            /// Cut faces that had to fall back to averaging their corners because the swept
            /// surface could not be evaluated for them (see CapContractibleLoop). Diagnostic
            /// only - such a face is still closed and still watertight, just filled the older,
            /// less accurate way. Zero on any ordinary cut.
            public readonly int ApproximatedCapFaces;

            public Result(Vector3[] vertices, int[] triangles, int openLoops, int capTriangles,
                          int approximatedCapFaces)
            {
                Success = true;
                Vertices = vertices;
                Triangles = triangles;
                Error = null;
                OpenLoops = openLoops;
                CapTriangles = capTriangles;
                ApproximatedCapFaces = approximatedCapFaces;
            }

            public Result(string error)
            {
                Success = false;
                Vertices = null;
                Triangles = null;
                Error = error;
                OpenLoops = 0;
                CapTriangles = 0;
                ApproximatedCapFaces = 0;
            }
        }

        /// Cuts `verts`/`tris` (an object's own local space) against `region` swept along the
        /// view direction, returning the surviving geometry.
        ///
        /// `mvp` is projection * worldToCamera * localToWorld and `modelToView` is
        /// worldToCamera * localToWorld, both for the object being cut. `symmetry` is a symmetry
        /// op (a mirror flip or radial rotation): a point is tested through its inverse, which is
        /// how one pass cuts against the op's mirrored or rotated copy of the shape (see TrimTool -
        /// symmetry is done as one pass per op rather than by widening the test, so each pass has
        /// a single well-defined swept surface to close the hole against).
        ///
        /// `removeCovered` false inverts the sense: everything the shape does NOT cover is
        /// removed, i.e. a crop down to the shape.
        ///
        /// Fails (with a reason) when the cut would do nothing, or would leave nothing behind.
        public static Result Trim(
            Vector3[] verts, int[] tris,
            Matrix4x4 mvp, Matrix4x4 modelToView, Rect viewport, SymmetryOp symmetry,
            ScreenRegionMask region, bool removeCovered)
        {
            if (verts == null || verts.Length == 0 || tris == null || tris.Length < 3)
                return new Result("no geometry");
            if (region == null)
                return new Result("the region was empty");

            var pass = new Pass(verts, tris, mvp, modelToView, viewport, symmetry, region, removeCovered);
            return pass.Run();
        }

        /// One cut. A class rather than a pile of ref parameters because every step below reads
        /// the same dozen pieces of state, and because a Trim under symmetry runs several of
        /// these back to back with nothing shared between them.
        private sealed class Pass
        {
            private readonly Vector3[] _verts;
            private readonly int[] _tris;
            private readonly Matrix4x4 _mvp;
            private readonly Matrix4x4 _modelToView;
            private readonly Rect _viewport;
            private readonly SymmetryOp _symmetry;
            private readonly ScreenRegionMask _region;
            private readonly bool _removeCovered;

            // The two inverses SurfacePoint needs to turn a point on the cutting surface back
            // into geometry. Built once per pass: view->clip is what the projection does on its
            // own (mvp with the model and view parts divided out), view->model undoes the rest.
            private readonly Matrix4x4 _viewToClip;
            private readonly Matrix4x4 _viewToModel;

            private bool[] _remove;
            private int[] _remap;
            private int _keptCount;

            private readonly List<Vector3> _outVerts = new List<Vector3>();
            private readonly List<int> _outTris = new List<int>();

            // Crossing vertices, keyed by the source edge that produced them so the two triangles
            // sharing an edge land on the SAME new vertex - without that the cut boundary comes
            // apart into unconnected fragments and no loop can be chained through it.
            private readonly Dictionary<long, int> _crossings = new Dictionary<long, int>(EdgeKeyComparer.Instance);
            // Swept-surface coordinates of each crossing vertex, in the order they were created -
            // so a crossing's slot is (its index in _outVerts) - _keptCount.
            private readonly List<float> _cutArc = new List<float>();
            private readonly List<float> _cutDepth = new List<float>();

            // The cut boundary as directed edges: _next[p] is the vertex that follows p when
            // walking the hole's rim in the direction the surviving surface's winding implies.
            private readonly Dictionary<int, int> _next = new Dictionary<int, int>();
            private int _ambiguousEdges;
            private int _unclosedLoops;
            private int _chartMappingRejected;

            // Mean edge length of the triangles the cut actually passed through - the cap's
            // density target. See where it is accumulated in ClipTriangles.
            private double _shellEdgeSum;
            private int _shellEdgeCount;
            private float ShellEdgeLength => _shellEdgeCount > 0 ? (float)(_shellEdgeSum / _shellEdgeCount) : 0f;

            public Pass(Vector3[] verts, int[] tris, Matrix4x4 mvp, Matrix4x4 modelToView, Rect viewport,
                        SymmetryOp symmetry, ScreenRegionMask region, bool removeCovered)
            {
                _verts = verts;
                _tris = tris;
                _mvp = mvp;
                _modelToView = modelToView;
                _viewport = viewport;
                _symmetry = symmetry;
                _region = region;
                _removeCovered = removeCovered;

                _viewToModel = modelToView.inverse;
                _viewToClip = mvp * _viewToModel;
            }

            public Result Run()
            {
                int removed = Classify();
                if (removed == 0) return new Result("the shape covered nothing");
                if (removed == _verts.Length) return new Result("that would delete the whole object");

                BuildKeptVertices();
                ClipTriangles();
                if (_outTris.Count == 0) return new Result("that would delete the whole object");

                int beforeCap = _outTris.Count;
                CapCutLoops();
                int capTriangles = (_outTris.Count - beforeCap) / 3;

                // Loops that could not be closed cleanly - either because the cut boundary ran
                // through one vertex twice (a shape whose edge grazes the surface tangentially)
                // or because a loop dead-ended. Surfaced rather than swallowed: a leftover hole
                // is invisible from outside but shows up the moment the object is remeshed,
                // booleaned or exported.
                int openLoops = _unclosedLoops + (_ambiguousEdges > 0 ? 1 : 0);
                return new Result(_outVerts.ToArray(), _outTris.ToArray(), openLoops, capTriangles,
                                  _chartMappingRejected);
            }

            // ------------------------------------------------------------------ classification

            private bool IsRemoved(Vector3 local)
            {
                Vector3 tested = _symmetry.ApplyInversePoint(local);
                bool covered = ScreenRegionMask.ProjectToScreen(_mvp, tested, _viewport, out Vector2 screen) &&
                               _region.Contains(screen);
                return covered == _removeCovered;
            }

            private int Classify()
            {
                _remove = new bool[_verts.Length];
                int removed = 0;
                for (int i = 0; i < _verts.Length; i++)
                {
                    if (!IsRemoved(_verts[i])) continue;
                    _remove[i] = true;
                    removed++;
                }
                return removed;
            }

            private void BuildKeptVertices()
            {
                _remap = new int[_verts.Length];
                for (int i = 0; i < _verts.Length; i++)
                {
                    if (_remove[i]) { _remap[i] = -1; continue; }
                    _remap[i] = _outVerts.Count;
                    _outVerts.Add(_verts[i]);
                }
                // Everything appended past this point is a crossing vertex, which is what lets a
                // crossing's swept-surface coordinates be found by index rather than by lookup.
                _keptCount = _outVerts.Count;
            }

            // --------------------------------------------------------------------- the cutting

            /// Walks the edge from a surviving vertex toward a removed one and returns the point
            /// where the region test flips. Pure bisection on the same predicate that classified
            /// the endpoints, so the returned point is on the boundary BY CONSTRUCTION - there is
            /// no separate surface it could be off by.
            private Vector3 FindCrossing(Vector3 keep, Vector3 drop)
            {
                float lo = 0f, hi = 1f; // lo end survives, hi end is removed - true at entry
                for (int i = 0; i < BisectionSteps; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    if (IsRemoved(Vector3.LerpUnclamped(keep, drop, mid))) hi = mid;
                    else lo = mid;
                }
                return Vector3.LerpUnclamped(keep, drop, (lo + hi) * 0.5f);
            }

            private int CrossingVertex(int keepIndex, int dropIndex)
            {
                int a = keepIndex < dropIndex ? keepIndex : dropIndex;
                int b = keepIndex < dropIndex ? dropIndex : keepIndex;
                long key = ((long)a << 32) | (uint)b;
                if (_crossings.TryGetValue(key, out int existing)) return existing;

                Vector3 p = FindCrossing(_verts[keepIndex], _verts[dropIndex]);
                int index = _outVerts.Count;
                _outVerts.Add(p);
                _crossings.Add(key, index);

                Vector3 tested = _symmetry.ApplyInversePoint(p);
                ScreenRegionMask.ProjectToScreen(_mvp, tested, _viewport, out Vector2 screen);
                _cutArc.Add(_region.ArcPosition(screen));
                // Unity's view space looks down -Z, so a point in front of the camera has
                // negative z and this is its distance ahead of the eye.
                _cutDepth.Add(-_modelToView.MultiplyPoint3x4(tested).z);
                return index;
            }

            private void EmitTriangle(int a, int b, int c)
            {
                // A triangle collapsed by the cut (both of its crossings landing on the same
                // point, where the boundary clips a corner) contributes nothing but a zero-area
                // face that then poisons its vertices' recalculated normals.
                if (a == b || b == c || c == a) return;
                _outTris.Add(a);
                _outTris.Add(b);
                _outTris.Add(c);
            }

            private void AddCutEdge(int from, int to)
            {
                if (from == to) return;
                if (_next.ContainsKey(from)) { _ambiguousEdges++; return; }
                _next.Add(from, to);
            }

            /// The classic triangle-vs-halfspace case split, except the "halfspace" is the swept
            /// region. Both mixed cases leave exactly one new boundary edge, P->Q, oriented to
            /// continue the surviving surface's own winding - which is what makes the cut edges
            /// chain into loops with a consistent direction.
            private void ClipTriangles()
            {
                for (int t = 0; t + 2 < _tris.Length; t += 3)
                {
                    int i0 = _tris[t], i1 = _tris[t + 1], i2 = _tris[t + 2];
                    bool r0 = _remove[i0], r1 = _remove[i1], r2 = _remove[i2];
                    int removedCount = (r0 ? 1 : 0) + (r1 ? 1 : 0) + (r2 ? 1 : 0);

                    if (removedCount == 3) continue;
                    if (removedCount == 0)
                    {
                        EmitTriangle(_remap[i0], _remap[i1], _remap[i2]);
                        continue;
                    }

                    // The surface's own edge length right where the cut lands, which is the
                    // density the cap has to be filled at. Measured from the triangles being CUT,
                    // never from the resulting rim: a cut crosses a triangle on a chord, and a
                    // crossing that lands near an existing vertex leaves a rim edge near zero, so
                    // rim edges come out far shorter than the mesh they were cut from - measured
                    // at 0.35x on a sphere, which as a density target over-fills the cap eightfold.
                    Vector3 v0 = _verts[i0], v1 = _verts[i1], v2 = _verts[i2];
                    _shellEdgeSum += Vector3.Distance(v0, v1) + Vector3.Distance(v1, v2) + Vector3.Distance(v2, v0);
                    _shellEdgeCount += 3;

                    if (removedCount == 1)
                    {
                        // Rotated to the cyclic order (a survives, b survives, c removed), which
                        // preserves the source winding whichever corner was the removed one.
                        int a, b, c;
                        if (r0) { c = i0; a = i1; b = i2; }
                        else if (r1) { c = i1; a = i2; b = i0; }
                        else { c = i2; a = i0; b = i1; }

                        int p = CrossingVertex(b, c);
                        int q = CrossingVertex(a, c);
                        int ra = _remap[a], rb = _remap[b];
                        // The surviving quad a-b-P-Q, fanned from a.
                        EmitTriangle(ra, rb, p);
                        EmitTriangle(ra, p, q);
                        AddCutEdge(p, q);
                    }
                    else
                    {
                        // Cyclic order (a survives, b removed, c removed).
                        int a, b, c;
                        if (!r0) { a = i0; b = i1; c = i2; }
                        else if (!r1) { a = i1; b = i2; c = i0; }
                        else { a = i2; b = i0; c = i1; }

                        int p = CrossingVertex(a, b);
                        int q = CrossingVertex(a, c);
                        EmitTriangle(_remap[a], p, q);
                        AddCutEdge(p, q);
                    }
                }
            }

            // ----------------------------------------------------------------- closing the hole

            private void CapCutLoops()
            {
                if (_next.Count == 0) return;

                var visited = new HashSet<int>();
                var loops = new List<List<int>>();

                foreach (KeyValuePair<int, int> entry in _next)
                {
                    int start = entry.Key;
                    if (visited.Contains(start)) continue;

                    var loop = new List<int>();
                    bool closed = false;
                    int current = start;
                    while (visited.Add(current))
                    {
                        loop.Add(current);
                        if (!_next.TryGetValue(current, out int following)) break;
                        if (following == start) { closed = true; break; }
                        current = following;
                    }

                    if (closed && loop.Count >= 3) loops.Add(loop);
                    else _unclosedLoops++;
                }

                // Every chart is built BEFORE any capping runs. A cap appends its own interior
                // vertices to _outVerts, and a crossing vertex's swept-surface coordinates are
                // found by its offset past _keptCount - so capping one loop first would shift the
                // next loop's lookups onto the wrong entries.
                var charts = new List<LoopChart>(loops.Count);
                for (int i = 0; i < loops.Count; i++) charts.Add(BuildChart(loops[i]));

                // A loop that wraps the whole way around the dragged outline is one END of a
                // tunnel - the shape was drawn entirely within the object's silhouette, so it
                // punched straight through. Those close against each OTHER (the tunnel wall), not
                // against themselves, so they are set aside and paired up afterwards.
                var wrapping = new List<LoopChart>();
                for (int i = 0; i < charts.Count; i++)
                {
                    if (charts[i].Wraps) wrapping.Add(charts[i]);
                    else CapContractibleLoop(charts[i]);
                }

                CapWrappingLoops(wrapping);
            }

            /// One cut loop expressed in the swept surface's own coordinates: `U` is distance
            /// along the dragged outline, unwrapped so a loop that walks off one end of the
            /// outline continues past it rather than jumping back to zero, and `D` is depth from
            /// the camera. Triangulating in these coordinates is what makes the cap follow the
            /// surface that did the cutting.
            private struct LoopChart
            {
                public List<int> Vertices;
                public float[] U;
                public float[] D;
                public float[] Arc;   // the raw, still-wrapped arc position - the tunnel stitch wants it
                public float Wind;    // net travel along the outline over the whole loop
                public bool Wraps;
                /// Multipliers putting U and D into the same units as the mesh itself, so that a
                /// shape judged well-proportioned in the chart really is well-proportioned in 3D.
                /// Raw U is in screen PIXELS and raw D in world units - a chart stretched by two
                /// orders of magnitude, in which a perfectly reasonable-looking triangulation maps
                /// to a fan of needles. That is what it did: measured on a trimmed sphere, 100% of
                /// cap triangles came out as slivers under 5 degrees.
                public float ScaleU;
                public float ScaleD;
                /// Mean 3D length of a rim edge. Reported for diagnostics only - it is deliberately
                /// NOT the cap's density target, see ShellEdgeLength for why.
                public float RimEdge;
            }

            private LoopChart BuildChart(List<int> loop)
            {
                int m = loop.Count;
                float perimeter = _region.Perimeter;

                var chart = new LoopChart
                {
                    Vertices = loop,
                    U = new float[m],
                    D = new float[m],
                    Arc = new float[m]
                };

                for (int k = 0; k < m; k++)
                {
                    int slot = loop[k] - _keptCount;
                    chart.Arc[k] = slot >= 0 && slot < _cutArc.Count ? _cutArc[slot] : 0f;
                    chart.D[k] = slot >= 0 && slot < _cutDepth.Count ? _cutDepth[slot] : 0f;
                }

                chart.U[0] = chart.Arc[0];
                float wind = 0f;
                for (int k = 1; k < m; k++)
                {
                    float step = ShortestArcStep(chart.Arc[k] - chart.Arc[k - 1], perimeter);
                    chart.U[k] = chart.U[k - 1] + step;
                    wind += step;
                }
                wind += ShortestArcStep(chart.Arc[0] - chart.Arc[m - 1], perimeter);

                chart.Wind = wind;
                // Half a perimeter of slack: a loop that merely stays put nets out near zero, one
                // that goes the whole way round nets out near +/- the perimeter, and nothing
                // real lands between.
                chart.Wraps = perimeter > 0f && Mathf.Abs(wind) > perimeter * 0.5f;

                FitChartScales(ref chart);
                return chart;
            }

            /// Finds the two axis scales that make the chart as close to a true distance map of
            /// the cut surface as a linear rescale can be, by least-squares over the rim's own
            /// edges: for each one, (ScaleU*du)^2 + (ScaleD*dd)^2 should come out as its real 3D
            /// length squared.
            ///
            /// Fitted rather than derived from the camera because the two axes are not even in
            /// comparable units to begin with - U is screen pixels and D is view-space depth,
            /// while the geometry is in the object's own local space, so a derivation would have
            /// to unpick the projection, the viewport height and the object's scale. Two unknowns
            /// (u = ScaleU^2, w = ScaleD^2) against one linear system does the same job and cannot
            /// be wrong about any of them.
            private void FitChartScales(ref LoopChart chart)
            {
                chart.ScaleU = 1f;
                chart.ScaleD = 1f;

                int m = chart.Vertices.Count;
                double duu = 0, ddd = 0, dud = 0, ru = 0, rd = 0;
                double edgeSum = 0;
                int edges = 0;

                for (int k = 0; k < m; k++)
                {
                    int next = k + 1 == m ? 0 : k + 1;
                    double du = ShortestArcStep(chart.Arc[next] - chart.Arc[k], _region.Perimeter);
                    double dd = chart.D[next] - chart.D[k];
                    double len2 = (_outVerts[chart.Vertices[next]] - _outVerts[chart.Vertices[k]]).sqrMagnitude;

                    edgeSum += System.Math.Sqrt(len2);
                    edges++;

                    double du2 = du * du, dd2 = dd * dd;
                    duu += du2 * du2;
                    ddd += dd2 * dd2;
                    dud += du2 * dd2;
                    ru += du2 * len2;
                    rd += dd2 * len2;
                }

                chart.RimEdge = edges > 0 ? (float)(edgeSum / edges) : 0f;

                double det = duu * ddd - dud * dud;
                if (System.Math.Abs(det) > 1e-30)
                {
                    double u = (ru * ddd - rd * dud) / det;
                    double w = (rd * duu - ru * dud) / det;
                    if (u > 0 && w > 0)
                    {
                        chart.ScaleU = (float)System.Math.Sqrt(u);
                        chart.ScaleD = (float)System.Math.Sqrt(w);
                        return;
                    }
                }

                // Degenerate system - a rim that never moves along one axis (a perfectly
                // silhouette-aligned cut, say). Scale the axis that does vary and leave the other
                // alone; a wrong-but-finite chart still triangulates, it just is not optimal.
                if (duu > 1e-30 && ru > 0) chart.ScaleU = (float)System.Math.Sqrt(ru / duu);
                if (ddd > 1e-30 && rd > 0) chart.ScaleD = (float)System.Math.Sqrt(rd / ddd);
            }

            /// The signed step between two arc positions, taken the short way around the closed
            /// outline - the loop is walked in small hops, so a raw difference of nearly a whole
            /// perimeter always means it crossed the outline's seam rather than sprinted around
            /// it.
            private static float ShortestArcStep(float delta, float perimeter)
            {
                if (perimeter <= 0f) return delta;
                return delta - perimeter * Mathf.Round(delta / perimeter);
            }

            /// The point of the cutting surface whose swept-surface coordinates are (`arc`,
            /// `depth`) - the exact inverse of how a crossing's chart coordinates were recorded in
            /// CrossingVertex, so feeding a rim vertex's own coordinates back through this returns
            /// that vertex.
            ///
            /// This is what the cut FACE is filled from. Its rim was exact from the start - every
            /// crossing bisected onto the boundary - but the interior of the face was not: each
            /// point was the plain centroid of the three around it, which is a point on the CHORD
            /// between them. Wherever the outline curves a chord cuts the corner, the ear clipper's
            /// first triangles span the whole face, and every later round subdivided that error
            /// rather than correcting it. Measured on the user's 359k-triangle model, cut-face
            /// points sat up to 34 screen pixels - about fifteen triangle widths - off the surface
            /// they were meant to be on, which is what rendered as fangs and gouges across the cut.
            ///
            /// Unity's projection matrices have a zero bottom-left 2x2, so clip w depends only on
            /// view depth (it is -z under perspective and 1 under orthographic) and the remaining
            /// unknowns are a 2x2 solve. False when that solve is degenerate, which cannot happen
            /// for a real camera but leaves the caller a defined fallback.
            private bool SurfacePoint(float arc, float depth, out Vector3 local)
            {
                Vector2 screen = _region.PointAtArc(arc);
                float ndcX = (screen.x - _viewport.x) / _viewport.width * 2f - 1f;
                float ndcY = (screen.y - _viewport.y) / _viewport.height * 2f - 1f;
                float z = -depth; // view space looks down -Z

                float w = _viewToClip[3, 2] * z + _viewToClip[3, 3];
                float a = _viewToClip[0, 0], b = _viewToClip[0, 1];
                float c = _viewToClip[1, 0], d = _viewToClip[1, 1];
                float det = a * d - b * c;
                if (Mathf.Abs(w) < 1e-9f || Mathf.Abs(det) < 1e-12f)
                {
                    local = Vector3.zero;
                    return false;
                }

                float rx = ndcX * w - _viewToClip[0, 2] * z - _viewToClip[0, 3];
                float ry = ndcY * w - _viewToClip[1, 2] * z - _viewToClip[1, 3];
                Vector3 view = new Vector3((d * rx - b * ry) / det, (a * ry - c * rx) / det, z);

                // Back from the tested copy to the geometry being cut.
                local = _symmetry.ApplyPoint(_viewToModel.MultiplyPoint3x4(view));
                return true;
            }

            private void CapContractibleLoop(LoopChart chart)
            {
                int m = chart.Vertices.Count;
                if (m < 3) return;

                // Reversed: the surviving shell already walks this rim one way round, and a cap
                // sharing those edges in the SAME direction would face the wrong way. Every
                // closed surface's two sides of an edge run opposite.
                var order = new int[m];
                var positions = new List<Vector3>(m);
                var points = new List<Vector2>(m);
                for (int k = 0; k < m; k++)
                {
                    int src = m - 1 - k;
                    order[k] = chart.Vertices[src];
                    positions.Add(_outVerts[chart.Vertices[src]]);
                    points.Add(new Vector2(chart.U[src] * chart.ScaleU, chart.D[src] * chart.ScaleD));
                }

                // Where a new interior point of the cut face goes, given where it landed in the
                // chart. The chart handed to the triangulator is SCALED into mesh units (see
                // FitChartScales), so the scale comes back off before the coordinates mean arc
                // length and depth again.
                float scaleU = chart.ScaleU, scaleD = chart.ScaleD;
                System.Func<Vector2, Vector3> onSurface = point =>
                {
                    float arc = scaleU > 1e-12f ? point.x / scaleU : 0f;
                    float depth = scaleD > 1e-12f ? point.y / scaleD : 0f;
                    return SurfacePoint(arc, depth, out Vector3 p) ? p : Vector3.zero;
                };

                // The mapping is only used on a loop it can be shown to describe, and there are
                // two ways it may not.
                //
                // It must reproduce the rim. Every rim vertex was bisected onto the cutting
                // surface and had its chart coordinates read off it, so putting those coordinates
                // back through has to return the vertex itself; if it does not, the chart's fitted
                // scales or the camera's inverse are not what this loop is lying on.
                //
                // And the chart must actually separate the rim's vertices. A cut passing exactly
                // through a high-valence vertex - a UV sphere's pole, where 160 triangles fan into
                // one point - makes every edge around it cross at that same point, so a long RUN
                // of the rim arrives with identical chart coordinates. That is not a
                // parametrisation of anything: the refinement cannot tell those triangles apart to
                // subdivide them, so they survive at whatever size the ear clipper left them and
                // their flat faces overlap. Averaging the corners handles that case perfectly well
                // (the cut face there is flat, which is the one case a chord IS on the surface).
                //
                // Measured as a SHARE of the rim, not as "any coincidence at all". On a dense
                // sculpt two crossings landing on the same point is ordinary luck - it happened on
                // a 359k-triangle box cut - and one such pair must not throw away the mapping for
                // the whole loop, which would silently put that cut face back on the old chords.
                // A singularity is not subtle: it collapses half the rim, not a thousandth of it.
                float tolerance = Mathf.Max(ShellEdgeLength * 0.25f, 1e-6f);
                int coincident = 0;
                bool trustworthy = true;
                for (int k = 0; k < m && trustworthy; k++)
                {
                    if (Vector2.Distance(points[k], points[k == 0 ? m - 1 : k - 1]) <= 0f) coincident++;
                    else if (Vector3.Distance(onSurface(points[k]), positions[k]) > tolerance) trustworthy = false;
                }
                if (coincident * 10 > m) trustworthy = false;
                if (!trustworthy) _chartMappingRejected++;

                // Filled at the density of the surface it was cut from, rather than left as a bare
                // fan of the rim - see CapTriangulator for what a bare fan does to the shading
                // along the cut, and for why it also leaves the cut face unsculptable.
                var extra = new List<Vector3>();
                var patch = new List<int>();
                CapTriangulator.Fill(positions, points,
                    trustworthy ? onSurface : null,
                    ShellEdgeLength,
                    Mathf.Clamp(m * CapVerticesPerRimVertex, MinCapVertexBudget, MaxCapVertexBudget),
                    extra, patch);

                int firstExtra = _outVerts.Count;
                for (int i = 0; i < extra.Count; i++) _outVerts.Add(extra[i]);

                for (int i = 0; i + 2 < patch.Count; i += 3)
                    EmitTriangle(
                        MapPatchIndex(patch[i], order, firstExtra),
                        MapPatchIndex(patch[i + 1], order, firstExtra),
                        MapPatchIndex(patch[i + 2], order, firstExtra));
            }

            // The triangulator numbers the rim 0..m-1 in the order it was handed and anything it
            // added from m upwards; both have to come back as indices into _outVerts.
            private static int MapPatchIndex(int index, int[] rim, int firstExtra) =>
                index < rim.Length ? rim[index] : firstExtra + (index - rim.Length);


            // ------------------------------------------------------------------- tunnel walls

            /// Wrapping loops come in pairs - a tunnel through the object has an opening where it
            /// goes in and another where it comes out - and the surface between them IS the swept
            /// wall. Sorted by depth and paired off front-to-back, which is the same in/out
            /// parity a ray through a solid sees.
            private void CapWrappingLoops(List<LoopChart> wrapping)
            {
                if (wrapping.Count == 0) return;

                if (wrapping.Count % 2 != 0)
                {
                    // An odd count means the tunnel does not have a matching far opening -
                    // geometry with a boundary of its own, or a cut too tangential to resolve.
                    // Close each one on itself instead: overlapping, but never left open.
                    for (int i = 0; i < wrapping.Count; i++) FanLoop(wrapping[i]);
                    _unclosedLoops += wrapping.Count;
                    return;
                }

                wrapping.Sort((a, b) => MeanDepth(a).CompareTo(MeanDepth(b)));
                for (int i = 0; i + 1 < wrapping.Count; i += 2)
                    StitchTunnel(wrapping[i], wrapping[i + 1]);
            }

            private static float MeanDepth(LoopChart chart)
            {
                float sum = 0f;
                for (int i = 0; i < chart.D.Length; i++) sum += chart.D[i];
                return chart.D.Length > 0 ? sum / chart.D.Length : 0f;
            }

            /// Joins two loops that each wrap once around the outline into a tube.
            ///
            /// The two are traversed in opposite directions by their own shell windings - that is
            /// forced, since a tube's two rims bound the same consistently-oriented surface - so
            /// one is walked forwards and the other backwards to get both running the same way
            /// around the outline. Then a plain zipper: advance whichever side is proportionally
            /// behind and emit one triangle per step, which handles the two rims having quite
            /// different vertex counts.
            ///
            /// The zipper alone only ever produces triangles that reach the whole way from one rim
            /// to the other. That is fine on a thin wall and badly wrong on anything else: the rims
            /// are sampled at the density of the surface (about one mesh edge apart) while the tube
            /// is as long as the model is thick, so every triangle comes out as a needle spanning
            /// that whole distance. Measured on a 359k-triangle sculpt, a punched tunnel's wall was
            /// 100% slivers with triangles 417x a shell triangle - a band of garbage down the
            /// inside of every hole, which no test on a small sphere would ever show, because there
            /// the tube is only a couple of triangles long to begin with.
            ///
            /// So the tube is built with intermediate RINGS, spaced about one mesh edge apart down
            /// its length. Each ring is evaluated straight off the cutting surface at the rims' own
            /// arc positions, which makes the wall a proper quad grid instead of a fan of needles,
            /// and leaves both rims untouched and still welded to the shell.
            private void StitchTunnel(LoopChart first, LoopChart second)
            {
                if (first.Wind * second.Wind >= 0f)
                {
                    // Same direction: not the two ends of one tube after all. Close them
                    // separately rather than stitching a twisted surface between them.
                    FanLoop(first);
                    FanLoop(second);
                    _unclosedLoops += 2;
                    return;
                }

                List<int> a = first.Vertices;
                float[] aArc = first.Arc;

                int n = second.Vertices.Count;
                var b = new List<int>(n);
                var bArc = new float[n];
                for (int k = 0; k < n; k++)
                {
                    b.Add(second.Vertices[n - 1 - k]);
                    bArc[k] = second.Arc[n - 1 - k];
                }

                // Line the two rims up before stitching, or the tube comes out twisted.
                int offset = NearestArcIndex(bArc, aArc.Length > 0 ? aArc[0] : 0f, _region.Perimeter);
                if (offset > 0)
                {
                    var rotated = new List<int>(n);
                    var rotatedArc = new float[n];
                    for (int k = 0; k < n; k++) { rotated.Add(b[(k + offset) % n]); rotatedArc[k] = bArc[(k + offset) % n]; }
                    b = rotated;
                    bArc = rotatedArc;
                }

                // Both rims and every ring start at the same place on the outline, so the strips
                // below line up instead of spiralling. The two end strips join an irregular rim to
                // an evenly sampled ring; the many strips in between are ring-to-ring and become
                // plain quads.
                System.Func<int, float> alongA = ArcFractionOf(aArc);
                System.Func<int, float> alongB = ArcFractionOf(bArc);

                List<int> near = a;
                System.Func<int, float> alongNear = alongA;
                foreach (List<int> ring in TunnelRings(first, second))
                {
                    System.Func<int, float> alongRing = EvenArcFraction(ring.Count);
                    if (near.Count == ring.Count && !ReferenceEquals(near, a)) ZipEvenly(near, ring);
                    else ZipUnevenly(near, alongNear, ring, alongRing);
                    near = ring;
                    alongNear = alongRing;
                }
                ZipUnevenly(near, alongNear, b, alongB);
            }

            /// The intermediate rings of a tunnel wall, from the one nearest `first` outwards.
            /// Empty when the wall is already about one mesh edge deep, which is the case the plain
            /// zipper was always right for.
            private IEnumerable<List<int>> TunnelRings(LoopChart first, LoopChart second)
            {
                float target = ShellEdgeLength;
                float perimeter = _region.Perimeter;
                if (target <= 0f || perimeter <= 0f) yield break;

                // Each rim's depth as a function of position along the outline. A loop that wraps
                // exactly once covers every arc position exactly once, so sorting its samples by
                // arc gives a table that can just be interpolated.
                DepthByArc(first, out float[] nearArc, out float[] nearDepth);
                DepthByArc(second, out float[] farArc, out float[] farDepth);
                if (nearArc.Length == 0 || farArc.Length == 0) yield break;

                // How deep the wall actually is, measured in the geometry rather than in depth
                // units - under perspective a step in depth is not a fixed distance.
                //
                // Sized from the DEEPEST column, not the average one. Every column is cut into the
                // same number of rings (they have to be, or the rings stop being closed loops and
                // the grid grows T-junctions), so the deepest column decides how tall the tallest
                // quad ends up.
                float span = 0f;
                for (int k = 0; k < nearArc.Length; k++)
                {
                    if (!SurfacePoint(nearArc[k], nearDepth[k], out Vector3 nearPoint)) continue;
                    if (!SurfacePoint(nearArc[k], Sample(farArc, farDepth, nearArc[k]), out Vector3 farPoint)) continue;
                    span = Mathf.Max(span, Vector3.Distance(nearPoint, farPoint));
                }

                int rings = Mathf.RoundToInt(span / target) - 1;
                // Capped for the same reason the cap has a vertex budget: a deep tunnel through a
                // dense model would otherwise be free to add more geometry than the trim removed.
                rings = Mathf.Clamp(rings, 0, MaxTunnelRings);
                if (rings <= 0) yield break;

                // How many vertices go round each ring, from the wall's real width at mid depth.
                // The rings are sampled EVENLY along the outline rather than reusing the near rim's
                // own vertices, which is what the first version did and got badly wrong: a cut
                // crosses a triangle on a chord, so consecutive crossings sit anywhere from a whole
                // mesh edge apart to almost on top of each other. Inheriting that spacing for every
                // ring repeated the rim's worst gap all the way down the tunnel - which is where
                // the wall's 39x triangles and its slivers were both coming from, and why making
                // the rings TALLER did not help.
                float width = 0f;
                for (int k = 0; k < nearArc.Length; k++)
                {
                    float arc = nearArc[k], next = nearArc[(k + 1) % nearArc.Length];
                    float mid = Mathf.Lerp(Sample(nearArc, nearDepth, arc), Sample(farArc, farDepth, arc), 0.5f);
                    if (SurfacePoint(arc, mid, out Vector3 p) && SurfacePoint(next, mid, out Vector3 q))
                        width += Vector3.Distance(p, q);
                }
                int around = Mathf.Clamp(Mathf.RoundToInt(width / target), 8, MaxTunnelRingVertices);
                if ((long)around * rings > MaxTunnelWallVertices)
                    rings = Mathf.Max(1, MaxTunnelWallVertices / around);

                // Anchored to where the near rim starts and running the same way round it, so every
                // ring is in phase with both rims rather than spiralling against them.
                float origin = first.Arc.Length > 0 ? first.Arc[0] : 0f;
                float direction = first.Wind < 0f ? -1f : 1f;
                for (int r = 1; r <= rings; r++)
                {
                    float t = r / (float)(rings + 1);
                    var ring = new List<int>(around);
                    for (int k = 0; k < around; k++)
                    {
                        float arc = origin + direction * perimeter * k / around;
                        float depth = Mathf.Lerp(Sample(nearArc, nearDepth, arc), Sample(farArc, farDepth, arc), t);
                        ring.Add(AddVertex(SurfacePoint(arc, depth, out Vector3 p) ? p : Vector3.zero));
                    }
                    yield return ring;
                }
            }

            /// A loop's depth samples, sorted by position along the outline so they can be
            /// interpolated. Only meaningful for a loop that wraps once - see LoopChart.Wraps.
            private static void DepthByArc(LoopChart loop, out float[] arc, out float[] depth)
            {
                int n = loop.Vertices.Count;
                var order = new int[n];
                for (int k = 0; k < n; k++) order[k] = k;
                System.Array.Sort(order, (x, y) => loop.Arc[x].CompareTo(loop.Arc[y]));

                arc = new float[n];
                depth = new float[n];
                for (int k = 0; k < n; k++) { arc[k] = loop.Arc[order[k]]; depth[k] = loop.D[order[k]]; }
            }

            private int AddVertex(Vector3 position)
            {
                _outVerts.Add(position);
                return _outVerts.Count - 1;
            }

            /// Linear interpolation through a table sorted by arc, wrapping round the outline.
            private float Sample(float[] arc, float[] value, float at)
            {
                int n = arc.Length;
                if (n == 0) return 0f;
                if (n == 1) return value[0];

                float perimeter = _region.Perimeter;
                if (perimeter > 0f) at -= perimeter * Mathf.Floor(at / perimeter);

                int lo = 0, hi = n - 1;
                if (at <= arc[0] || at >= arc[n - 1])
                {
                    // Between the last sample and the first, the short way round the seam.
                    float wrap = arc[0] + perimeter - arc[n - 1];
                    float along = at >= arc[n - 1] ? at - arc[n - 1] : at + perimeter - arc[n - 1];
                    return Mathf.Lerp(value[n - 1], value[0], wrap > 1e-6f ? along / wrap : 0f);
                }
                while (lo + 1 < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (arc[mid] <= at) lo = mid; else hi = mid;
                }
                float step = arc[hi] - arc[lo];
                return Mathf.Lerp(value[lo], value[hi], step > 1e-6f ? (at - arc[lo]) / step : 0f);
            }

            /// Two rings with the same vertex count and the same arc positions: a plain quad strip.
            ///
            /// Each quad is split along its SHORTER diagonal. The rings are evenly spaced down the
            /// tunnel but the rim's own vertices are not evenly spaced around it - a cut crosses a
            /// triangle on a chord, so consecutive crossings can be a whole edge apart or almost
            /// coincident - and always cutting the same way turns every one of those narrow quads
            /// into a pair of slivers.
            private void ZipEvenly(List<int> near, List<int> far)
            {
                int m = near.Count;
                for (int k = 0; k < m; k++)
                {
                    int k1 = (k + 1) % m;
                    int a = near[k], b = far[k], c = far[k1], d = near[k1];
                    if ((_outVerts[a] - _outVerts[c]).sqrMagnitude <= (_outVerts[b] - _outVerts[d]).sqrMagnitude)
                    {
                        EmitTriangle(a, b, c);
                        EmitTriangle(a, c, d);
                    }
                    else
                    {
                        EmitTriangle(a, b, d);
                        EmitTriangle(b, c, d);
                    }
                }
            }

            /// Two rings with different vertex counts: advance whichever side has got less far
            /// round the outline and emit one triangle per step.
            ///
            /// Position round the outline, not position in the LIST. The two are the same only if
            /// both rings are evenly sampled, and a rim never is - a cut crosses each triangle on a
            /// chord, so its crossings bunch and gap. Stepping by index against an evenly sampled
            /// ring therefore drifts out of phase and leaves a long triangle reaching back to catch
            /// up. `arcOf` returns how far along the outline an entry sits, as a fraction of the
            /// whole way round starting from that ring's first vertex.
            private void ZipUnevenly(List<int> a, System.Func<int, float> arcOfA,
                                     List<int> b, System.Func<int, float> arcOfB)
            {
                int ma = a.Count, mb = b.Count;
                if (ma == 0 || mb == 0) return;

                int i = 0, j = 0;
                for (int step = 0; step < ma + mb; step++)
                {
                    bool advanceA;
                    if (i >= ma) advanceA = false;
                    else if (j >= mb) advanceA = true;
                    else advanceA = arcOfA(i + 1) <= arcOfB(j + 1);

                    int av = a[i % ma], bv = b[j % mb];
                    if (advanceA)
                    {
                        EmitTriangle(av, bv, a[(i + 1) % ma]);
                        i++;
                    }
                    else
                    {
                        EmitTriangle(av, bv, b[(j + 1) % mb]);
                        j++;
                    }
                }
            }

            /// How far round the outline each entry of a rim sits, as a fraction of the whole way
            /// round from its own first vertex. Accumulated step by step rather than differenced
            /// against the start, so it stays monotone across the outline's seam.
            private System.Func<int, float> ArcFractionOf(float[] arc)
            {
                int n = arc.Length;
                var along = new float[n + 1];
                for (int k = 1; k <= n; k++)
                    along[k] = along[k - 1] + Mathf.Abs(ShortestArcStep(arc[k % n] - arc[k - 1], _region.Perimeter));

                float total = along[n];
                if (total <= 1e-6f) total = 1f;
                return index => along[Mathf.Clamp(index, 0, n)] / total;
            }

            /// The same, for a ring this class sampled evenly itself.
            private static System.Func<int, float> EvenArcFraction(int count) =>
                index => count > 0 ? Mathf.Clamp01(index / (float)count) : 0f;

            private static int NearestArcIndex(float[] arc, float target, float perimeter)
            {
                int best = 0;
                float bestDistance = float.MaxValue;
                for (int k = 0; k < arc.Length; k++)
                {
                    float distance = Mathf.Abs(ShortestArcStep(arc[k] - target, perimeter));
                    if (distance >= bestDistance) continue;
                    bestDistance = distance;
                    best = k;
                }
                return best;
            }

            /// Last-resort closure: a fan from the loop's first vertex. Correct only for a loop
            /// that is star-shaped from that point, but it always produces a closed surface, which
            /// is the property that matters when the alternative is a hole.
            private void FanLoop(LoopChart chart)
            {
                List<int> loop = chart.Vertices;
                for (int k = loop.Count - 1; k >= 2; k--)
                    EmitTriangle(loop[0], loop[k], loop[k - 1]);
            }
        }
    }
}
