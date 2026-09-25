using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Per-vertex curvature and the vertex-colour encoding the shader and Surface Relax read.
    public partial class SculptableMesh
    {
        // Per-vertex concavity/convexity, recomputed after every stroke and written into the
        // mesh's vertex colors (.r) for SculptPBR's cavity coloring - see RecomputeCavity.
        private Color[] _cavityColors;

        // Raw (unsmoothed, pre-sensitivity) curvature per vertex, kept as its own array so the
        // one-ring blur in EncodeCavityAt has unsmoothed neighbour values to average - blurring
        // in place would feed already-blurred values back in and diffuse far more than intended.
        private float[] _cavityRaw;

        // Scales CurvatureAt's dimensionless output into the -1..1 encoded range. Was 25 back
        // when curvature was a RAW DISTANCE (see CurvatureAt for why that was wrong); a
        // size-relative input needs a far smaller multiplier. A sphere reads about -0.5 by
        // construction, so this leaves a plain ball comfortably inside the range and lets
        // genuine creases saturate.
        private const float CavitySensitivity = 1.2f;

        /// Raw (dimensionless, resolution/scale-independent - see CurvatureAt), UNCLAMPED
        /// departure from this mesh's own mean curvature at a vertex: 0 is exactly baseline,
        /// larger is more extreme in EITHER direction (a recess or a ridge). Deliberately NOT
        /// _cavityColors.r - that value exists for VISUAL cavity tinting and is tuned
        /// (CavitySensitivity, clamped to +-1) to saturate quickly for contrast, which erases
        /// exactly the distinction a gating threshold needs: a deliberately rounded lobe tip
        /// and a genuinely sharp crease both clamp to the same saturated extreme, even though
        /// the crease's raw curvature can be many times larger. Exposed read-only so a brush
        /// can scale its own effect by how extreme the EXISTING curvature already is at a
        /// vertex - see SculptController's surface-relax pass, the first caller. Stale within
        /// a single dab exactly like Normals is (see ApplyVerticesLocal) - refreshed once per
        /// dab-application, not recomputed mid-dab.
        public float CurvatureDeviationAt(int index) => Mathf.Abs(_cavityRaw[index] - _cavityMean);

        /// The stored raw curvature itself, signed and un-centred. Internal because it is a cache
        /// detail, not a measurement anything outside the mesh pipeline should be interpreting.
        internal float CurvatureRawAt(int index) => _cavityRaw[index];

        private Action<int, int> _cavityCurvatureRange;

        private Action<int, int> _cavityEncodeRange;

        /// Approximates per-vertex concavity/convexity from how far a vertex sits from its
        /// neighbors' average position along its own normal: a vertex recessed relative to
        /// its neighbors (a dent) has its neighbor average out ahead of it along the normal,
        /// a vertex proud of its neighbors (a peak) has the average behind it. Encoded into
        /// vertex color .r around a 0.5 "flat" baseline (>0.5 recess, <0.5 peak) for
        /// SculptPBR's cavity coloring. Uses the cached _workingNormals (refreshed by the
        /// caller right before this runs) rather than re-reading Mesh.normals, which copies the
        /// whole array on every access.
        private void RecomputeCavity()
        {
            // Every caller of the full recompute (Awake / Remesh / ReplaceMesh) is exactly the
            // case where the object's size can have changed, so the scale is refreshed here
            // rather than at each of those call sites.
            UpdateCavityLengthScale();
            EnsureAdjacency();
            EnsureCavityBuffers();
            // Two passes, because the encode step blurs across neighbours: every raw value has
            // to exist before any of them is read. Both are split across cores the same way the
            // per-stroke version is (see RecomputeCavityLocal) - this one walks the WHOLE mesh,
            // so it is the single largest cost in Remesh, Trim, Join and a scene load.
            int vertexCount = _vertexCount;
            ParallelPass.ForRange(vertexCount,
                _cavityFullCurvatureRange ?? (_cavityFullCurvatureRange = CavityFullCurvatureRange));

            // Summed on one thread afterwards rather than folded into the pass above: it is a
            // reduction over the array the pass just filled, and at a few nanoseconds per entry
            // it is nowhere near worth the partial-sum machinery to split.
            double sum = 0.0; // double, not float - this accumulates millions of terms
            for (int i = 0; i < vertexCount; i++) sum += _cavityRaw[i];
            // The DC term EncodeCavityAt subtracts. Computed only on a full recompute, so a
            // stroke never shifts the whole mesh's tint out from under itself - a brush changes
            // the average curvature of a whole object negligibly, and a mean that drifted every
            // frame would make untouched geometry flicker.
            _cavityMean = vertexCount > 0 ? (float)(sum / vertexCount) : 0f;

            ParallelPass.ForRange(vertexCount,
                _cavityFullEncodeRange ?? (_cavityFullEncodeRange = CavityFullEncodeRange));
        }

        private Action<int, int> _cavityFullCurvatureRange;

        private Action<int, int> _cavityFullEncodeRange;

        // The whole-mesh forms of the two cavity passes: index IS the vertex here, where the
        // per-stroke pair above indexes through _affectedList.
        private void CavityFullCurvatureRange(int start, int end)
        {
            float[] raw = _cavityRaw;
            for (int i = start; i < end; i++) raw[i] = CurvatureAt(i);
        }

        private void CavityFullEncodeRange(int start, int end)
        {
            for (int i = start; i < end; i++) EncodeCavityAt(i);
        }

        private float _cavityMean;

        /// Rebuild-if-null, the same treatment _topology/_triangleGrid/_gpuScatter get: a
        /// mid-Play script recompile triggers a domain reload that does not preserve these
        /// caches, and a stroke immediately afterward would otherwise NullReference. Also covers
        /// a length mismatch, which would mean the buffers survived a topology change they
        /// should not have.
        private void EnsureCavityBuffers()
        {
            int n = _workingVertices.Length;
            if (_cavityRaw == null || _cavityRaw.Length != n) _cavityRaw = new float[n];
            if (_cavityColors == null || _cavityColors.Length != n) _cavityColors = new Color[n];
        }

        /// Same effect as RecomputeCavity(), but only for the given vertices plus their direct
        /// neighbors - a moved vertex changes not just its own cavity value but every
        /// neighbor's too, since their GetNeighborAverage includes it. Measured as the dominant
        /// remaining per-frame cost after the triangle-grid fix (this app's high-poly-brush-lag
        /// investigation): ~5.6ms of an ~8ms small-footprint stroke at ~144k triangles.
        private void RecomputeCavityLocal()
        {
            EnsureCavityBuffers();

            // Same two-pass split as the full recompute. The encode pass reads raw values one
            // ring beyond this set, which are left over from before the stroke and so are very
            // slightly stale - that only softens the blur at the footprint's rim by a fraction
            // of a vertex, and widening the recompute by another ring every frame would cost
            // far more than it could possibly be worth.
            //
            // EnsureAdjacency once for the whole pass rather than per vertex: CurvatureAt used to
            // call it on every entry, which is two array-length compares per affected vertex for a
            // condition that cannot change inside a loop that never touches topology.
            EnsureAdjacency();

            // Two separate splits, never one fused pass: the encode step reads its NEIGHBOURS'
            // raw curvature, so every raw value has to exist before any of them is read. The
            // barrier is exactly ParallelPass.ForRange returning.
            int count = _affectedList.Count;
            ParallelPass.ForRange(count,
                _cavityCurvatureRange ?? (_cavityCurvatureRange = CavityCurvatureRange));
            ParallelPass.ForRange(count,
                _cavityEncodeRange ?? (_cavityEncodeRange = CavityEncodeRange));
        }

        private void CavityCurvatureRange(int start, int end)
        {
            List<int> affected = _affectedList;
            float[] raw = _cavityRaw;
            for (int k = start; k < end; k++)
            {
                int i = affected[k];
                raw[i] = CurvatureAt(i);
            }
        }

        private void CavityEncodeRange(int start, int end)
        {
            List<int> affected = _affectedList;
            for (int k = start; k < end; k++) EncodeCavityAt(affected[k]);
        }

        /// Discrete mean curvature at a vertex, expressed relative to the object's own size:
        /// mean over neighbours of dot(direction to neighbour, normal) / |direction|^2, scaled
        /// by _cavityLengthScale. 0 on a flat surface, positive in a concave valley, negative
        /// on a convex ridge.
        ///
        /// Both divisions matter, and each fixes a different half of the same bug. The original
        /// version measured dot(neighbourAverage - vertex, normal) - a raw DISTANCE, which for a
        /// sphere of radius R with edge length e scales as e^2/R, so it collapsed toward zero as
        /// a mesh got denser. It had been tuned against a ~500-vertex sphere; on a 442k-vertex
        /// imported model (edges ~100x shorter) the identical shape produced values ~10,000x
        /// smaller, flattening the whole mesh to a uniform 0.5 and making the cavity controls
        /// look broken on exactly the dense models they matter most for.
        ///
        /// Dividing by |d| once gives dot(unit, normal), which still scales as e/R - measurably
        /// better but still density-dependent (verified: mean drifted 0.20 -> 0.44 -> 0.48
        /// across the same sphere at 515 / 10.7k / 91.5k vertices). Dividing by |d|^2 yields
        /// true curvature ~1/R, which is density-INdependent but now scales with object size;
        /// multiplying by the object's own extent cancels that too. The result is a pure shape
        /// measure: the same sphere reads the same at any tessellation and any scale, while a
        /// crease far sharper than the object is large saturates and pops, which is what cavity
        /// shading is for.
        // Callers (RecomputeCavity / RecomputeCavityLocal) call EnsureAdjacency once for the whole
        // pass - this deliberately does NOT repeat it per vertex; nothing inside a cavity pass can
        // change topology out from under it.
        private float CurvatureAt(int i)
        {
            Vector3[] verts = _workingVertices;
            MeshAdjacency topology = _topology;
            int from = topology.NeighborStart[i], to = from + topology.NeighborCount[i];
            if (to == from) return 0f;
            int[] neighbors = topology.NeighborIndices;

            Vector3 p = verts[i];
            Vector3 n = _workingNormals[i];

            // Accumulate first, divide ONCE - not dot(d,n)/|d|^2 per neighbour. Both give the
            // same answer on a regular mesh, but the per-edge form divides by each individual
            // edge length, so one unusually short edge produces a huge term. Surface Nets output
            // is full of those (its one-vertex-per-cell placement puts neighbours at wildly
            // varying distances), and the per-edge version turned that into visible speckle:
            // measured stdev 0.19 on a remeshed sphere against 0.007 on the authored one, with
            // values pinned at both 0 and 1. Averaging the offsets and the squared lengths
            // separately keeps the same curvature estimate while letting a stray short edge
            // barely move it.
            // Accumulated in double for mirror symmetry, the same reason RecomputeNormalsRange
            // and GetNeighborAverage do. This one matters most of the three: the offsets very
            // nearly cancel on a smooth surface, so ox/oy/oz are a small difference of much
            // larger terms and a single ulp of accumulation order is a LARGE relative error in
            // the result. Curvature then gates Surface Relax's per-vertex weight, so twins that summed the same offsets in a different order were
            // relaxed by slightly different amounts - the last seed of mirror drift left once the
            // Laplacians themselves were made order-independent (SymmetryDriftTests).
            //
            // Each dx is already exactly mirrored (IEEE subtraction negates exactly), and a
            // handful of them sum exactly in double, so both twins now reach the same float.
            double ox = 0d, oy = 0d, oz = 0d;
            double sqrLenSum = 0d;
            int counted = 0;
            for (int k = from; k < to; k++)
            {
                Vector3 q = verts[neighbors[k]];
                float dx = q.x - p.x, dy = q.y - p.y, dz = q.z - p.z;
                float sqrLen = dx * dx + dy * dy + dz * dz;
                // Skip coincident vertices - welded/degenerate geometry does occur, and a NaN
                // here would propagate into the vertex colours and the rendered mesh.
                if (sqrLen < 1e-18f) continue;
                ox += dx; oy += dy; oz += dz;
                sqrLenSum += sqrLen;
                counted++;
            }
            if (counted == 0 || sqrLenSum <= 0d) return 0f;

            // dot(meanOffset, normal) has units of length; dividing by the mean SQUARED edge
            // length gives 1/length (true curvature); multiplying by the object's extent makes
            // it dimensionless. No square roots anywhere on this path, which matters because it
            // runs per touched vertex on every stroke. The two per-vertex divides the original
            // form did (offsetSum/counted, then /meanSqrLen) collapse into one reciprocal: the
            // 1/counted in the mean offset and the counted in meanSqrLen cancel exactly.
            double dot = ox * n.x + oy * n.y + oz * n.z;
            return (float)(dot / sqrLenSum * _cavityLengthScale);
        }

        /// Characteristic size of the object in LOCAL space, used to make CurvatureAt's true
        /// curvature (units of 1/length) dimensionless. Refreshed on a full recompute only -
        /// Awake, Remesh and ReplaceMesh - not per stroke: a brush changes the silhouette far
        /// too little to be worth an O(n) bounds pass every frame, and a cavity tint that
        /// subtly rescaled itself mid-stroke would read as flicker.
        private float _cavityLengthScale = 1f;

        private void UpdateCavityLengthScale()
        {
            if (_workingVertices == null || _vertexCount == 0)
            {
                _cavityLengthScale = 1f;
                RefreshResyncThreshold();
                return;
            }

            // Component-wise min/max in plain floats rather than Vector3.Min/Max: those are method
            // calls returning a new struct per component pair, and this walks every vertex of the
            // mesh (millions, on the models this app is meant to handle) on every full recompute.
            Vector3 first = _workingVertices[0];
            float minX = first.x, minY = first.y, minZ = first.z;
            float maxX = minX, maxY = minY, maxZ = minZ;
            for (int i = 1; i < _vertexCount; i++)
            {
                Vector3 p = _workingVertices[i];
                if (p.x < minX) minX = p.x; else if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y; else if (p.y > maxY) maxY = p.y;
                if (p.z < minZ) minZ = p.z; else if (p.z > maxZ) maxZ = p.z;
            }

            _cavityLengthScale = Mathf.Max((maxX - minX) * 0.5f,
                Mathf.Max((maxY - minY) * 0.5f, (maxZ - minZ) * 0.5f));
            if (_cavityLengthScale < 1e-6f) _cavityLengthScale = 1f;
            RefreshResyncThreshold();
        }

        private void RefreshResyncThreshold()
        {
            float t = _cavityLengthScale / ResyncDivisor;
            _resyncSqrThreshold = t * t;
        }

        /// Turns raw curvature into the encoded 0..1 vertex-colour value, blurring across the
        /// vertex's one-ring on the way.
        ///
        /// The blur is not cosmetic polish - without it the measure is unusable on remeshed
        /// geometry. Surface Nets places one vertex per grid cell, so its output is genuinely
        /// bumpy at the cell scale, and true curvature (which is what CurvatureAt now reports)
        /// faithfully reports that bumpiness as very high: measured stdev 0.185 with 21% of
        /// vertices pinned at 0 or 1 on a remeshed sphere, against 0.007 on the same shape as
        /// authored. That reads as speckle rather than shading. Averaging over the one-ring
        /// suppresses per-vertex noise while leaving real creases - which span many vertices -
        /// essentially untouched.
        private void EncodeCavityAt(int i)
        {
            float[] raw = _cavityRaw;
            MeshAdjacency topology = _topology;
            int from = topology.NeighborStart[i], to = from + topology.NeighborCount[i];
            int[] neighbors = topology.NeighborIndices;
            float sum = raw[i];
            for (int k = from; k < to; k++) sum += raw[neighbors[k]];
            float smoothed = sum / (to - from + 1);

            // Subtracting the mesh-wide mean makes this a high-pass of curvature, which is what
            // "cavity" actually means: tint where the surface departs from its own overall
            // curvature, not wherever it is curved at all. Without it every convex object is
            // uniformly peak-tinted - a plain sphere measured a flat 0.199 across its whole
            // surface, which the shader renders as a solid peak colour rather than the neutral
            // it should be. Now a smooth ball sits at ~0.5 (neutral), while a crease or ridge,
            // whose curvature departs sharply from the body it sits on, still swings hard.
            float departure = smoothed - _cavityMean;
            float normalized = Mathf.Clamp(departure * CavitySensitivity, -1f, 1f);
            float encoded = 0.5f + normalized * 0.5f;
            // .b is the same measure for SculptPBR's aged-metal wash and wear, soft-saturated
            // (x / (1 + |x|)) instead of clamped: .r's clamp flattens a shallow recess and a deep
            // groove to the same value, and the wash needs to tell those apart.
            float soft = departure * WashSensitivity;
            soft /= 1f + Mathf.Abs(soft);
            // .r = cavity (unused by the shader since the screen-space cavity), .g = mask (see _mask).
            _cavityColors[i] = new Color(encoded, _mask[i], 0.5f + soft * 0.5f, 1f);
        }

        // Scales the dimensionless curvature for .b. A detail whose radius of curvature is 2.5%
        // of the object's half-size lands halfway to saturation; broad body curvature stays
        // near neutral while carved grooves and crests span most of the range.
        private const float WashSensitivity = 1f / 40f;
    }
}
