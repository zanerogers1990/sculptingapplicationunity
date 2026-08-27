using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// Tunables for MeshExtractor.Extract. Lengths are expressed as FRACTIONS of the source
    /// object's own size rather than in local units: an extract that behaves identically on a
    /// 0.5-unit sphere and a 100-unit imported model is the difference between a slider you can
    /// learn and one you have to re-find every time. MeshExtractor multiplies them by the
    /// source's largest local extent (see MeshExtractor.LargestExtent).
    public struct ExtractSettings
    {
        /// Shell thickness, as a fraction of the source's largest extent.
        public float ThicknessFraction;
        /// Pushes the shell's inner surface off the source along the surface normal before
        /// extruding, as a fraction of the source's largest extent. Negative sinks it in, so a
        /// plate can be made to bite into the body rather than float on it.
        public float OffsetFraction;
        /// Mask value a triangle's 3 vertices must average to be included. 0.5 by default so a
        /// soft-edged mask extracts roughly where it visually looks half-painted.
        public float MaskThreshold;
        /// Jacobi relaxation passes applied to the region's BOUNDARY vertices only, along the
        /// boundary itself - straightens the stair-stepped edge a mask threshold inevitably
        /// leaves behind (the region can only ever be a whole number of triangles).
        public int BorderSmoothing;
        /// Taubin smoothing passes over the finished shell - rounds the hard rim and settles
        /// the surface. See Smooth() for why this is Taubin rather than plain Laplacian.
        public int SurfaceSmoothing;
        /// 0 = uniform thickness everywhere; 1 = thickness tapers to nearly nothing where the
        /// mask fades out, giving a feathered edge instead of a slab with a square rim.
        public float FalloffAmount;
        /// How strongly the shell's INNER surface is pulled back onto the source after
        /// smoothing. 1 keeps it exactly flush (the extracted piece sits on the body like armor
        /// or cloth), 0 lets smoothing round it away from the surface. Only the inner half is
        /// affected - the outer surface stays fully smoothed either way.
        public float Shrinkwrap;
        /// Extracts the UNMASKED region instead. The mask marks what's protected from brushes,
        /// so "extract the masked part" is the natural default, but wanting the complement is
        /// common enough (mask the part to keep, extract everything else) to be worth a toggle.
        public bool InvertRegion;

        public static ExtractSettings Default => new ExtractSettings
        {
            ThicknessFraction = 0.06f,
            OffsetFraction = 0f,
            MaskThreshold = 0.5f,
            BorderSmoothing = 4,
            SurfaceSmoothing = 3,
            FalloffAmount = 0f,
            Shrinkwrap = 1f,
            InvertRegion = false
        };
    }

    /// Builds a solid, closed shell from the masked region of a sculpt - ZBrush's "Extract",
    /// used to pull armor plates, clothing, shells and similar surface-derived pieces off a
    /// body as their own object.
    ///
    /// Pure geometry: takes the source's vertex/normal/triangle/mask arrays and returns a brand
    /// new Mesh in the SOURCE'S OWN LOCAL SPACE, touching nothing. That keeps it trivially
    /// re-runnable, which is what makes the live preview in MaskExtractController possible -
    /// every settings change just throws the previous result away and calls this again.
    ///
    /// The result is watertight by construction: an outer surface (the region pushed out along
    /// its normals), an inner surface (the region itself, wound inside-out), and a rim wall
    /// stitching the two along the region's boundary edges.
    public static class MeshExtractor
    {
        /// Floor on the per-vertex thickness multiplier. With full falloff the taper would
        /// otherwise reach exactly zero at the mask's edge, collapsing the rim quads there into
        /// degenerate zero-area triangles - which read as shading artifacts and break the
        /// normals of everything touching them. A thin-but-real edge looks the same and stays
        /// valid geometry.
        private const float MinThicknessFactor = 0.04f;

        // Standard Taubin pair: a Laplacian pass that shrinks, then a slightly larger negative
        // pass that pushes back out. See Smooth().
        private const float TaubinLambda = 0.5f;
        private const float TaubinMu = -0.53f;

        /// Returns null and sets `error` when there's nothing sensible to build. `triangleCount`
        /// reports the finished shell's triangle count so the UI can show the cost before the
        /// user commits.
        public static Mesh Extract(SculptableMesh source, ExtractSettings settings,
                                   out int triangleCount, out string error)
        {
            triangleCount = 0;
            error = null;

            if (source == null) { error = "No object selected."; return null; }

            Vector3[] srcVerts = source.Vertices;
            Vector3[] srcNormals = source.Normals;
            int[] srcTris = source.Triangles;
            float[] mask = source.Mask;

            if (srcVerts == null || srcTris == null || mask == null ||
                srcNormals == null || srcNormals.Length != srcVerts.Length ||
                mask.Length != srcVerts.Length)
            {
                error = "Mesh data not ready.";
                return null;
            }

            bool invert = settings.InvertRegion;
            float threshold = Mathf.Clamp01(settings.MaskThreshold);

            // ---------------------------------------------------------- 1. pick the region
            // Averaged over the triangle's 3 vertices rather than requiring all 3 past the
            // threshold: an all-3 test erodes the region by a full ring of triangles, so a mask
            // painted right up to where you want the edge would extract visibly short of it.
            var selectedTris = new List<int>();
            for (int t = 0; t < srcTris.Length; t += 3)
            {
                float m = (MaskAt(mask, srcTris[t], invert) +
                           MaskAt(mask, srcTris[t + 1], invert) +
                           MaskAt(mask, srcTris[t + 2], invert)) / 3f;
                if (m >= threshold) selectedTris.Add(t);
            }

            if (selectedTris.Count == 0)
            {
                error = invert ? "Everything is masked - nothing left to extract."
                               : "Nothing masked - paint a mask first.";
                return null;
            }

            // ------------------------------------------------- 2. compact to region-local ids
            var remap = new int[srcVerts.Length];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;

            var regionToSource = new List<int>();
            for (int j = 0; j < selectedTris.Count; j++)
            {
                int t = selectedTris[j];
                for (int k = 0; k < 3; k++)
                {
                    int si = srcTris[t + k];
                    if (remap[si] < 0)
                    {
                        remap[si] = regionToSource.Count;
                        regionToSource.Add(si);
                    }
                }
            }

            int n = regionToSource.Count;
            var regionTris = new int[selectedTris.Count * 3];
            for (int j = 0; j < selectedTris.Count; j++)
            {
                int t = selectedTris[j];
                regionTris[j * 3] = remap[srcTris[t]];
                regionTris[j * 3 + 1] = remap[srcTris[t + 1]];
                regionTris[j * 3 + 2] = remap[srcTris[t + 2]];
            }

            // ------------------------------------------- 3. base surface, normals, thickness
            float extent = LargestExtent(srcVerts);
            float thickness = settings.ThicknessFraction * extent;
            float offset = settings.OffsetFraction * extent;
            float falloff = Mathf.Clamp01(settings.FalloffAmount);

            var basePos = new Vector3[n];
            var extrudeDir = new Vector3[n];
            var thicknessFactor = new float[n];

            for (int i = 0; i < n; i++)
            {
                int si = regionToSource[i];
                Vector3 nrm = srcNormals[si];
                // A degenerate/zero normal would send the whole extrusion to a point; falling
                // back to "no displacement direction" leaves that vertex flat against the base
                // instead, which the surface smoothing pass then pulls back into line.
                extrudeDir[i] = nrm.sqrMagnitude > 1e-12f ? nrm.normalized : Vector3.zero;
                basePos[i] = srcVerts[si] + extrudeDir[i] * offset;

                float t01 = Mathf.InverseLerp(threshold, 1f, MaskAt(mask, si, invert));
                float smooth01 = t01 * t01 * (3f - 2f * t01);
                thicknessFactor[i] = Mathf.Max(Mathf.Lerp(1f, smooth01, falloff), MinThicknessFactor);
            }

            // --------------------------------------------------- 4. boundary edges + cleanup
            FindBoundaryEdges(regionTris, out List<int> boundaryU, out List<int> boundaryV);
            if (settings.BorderSmoothing > 0)
                SmoothBoundary(basePos, boundaryU, boundaryV, n, settings.BorderSmoothing);

            // ------------------------------------------------------------ 5. build the shell
            // Inner surface occupies [0, n), outer surface [n, 2n) - a fixed +n offset between
            // a vertex and its extruded twin keeps every index expression below readable.
            var verts = new Vector3[n * 2];
            for (int i = 0; i < n; i++)
            {
                verts[i] = basePos[i];
                verts[n + i] = basePos[i] + extrudeDir[i] * (thickness * thicknessFactor[i]);
            }

            var tris = new List<int>(regionTris.Length * 2 + boundaryU.Count * 6);

            // Outer surface: source winding, so it faces away from the body exactly as the
            // source surface did.
            for (int j = 0; j < regionTris.Length; j += 3)
            {
                tris.Add(n + regionTris[j]);
                tris.Add(n + regionTris[j + 1]);
                tris.Add(n + regionTris[j + 2]);
            }

            // Inner surface: source winding REVERSED, so it faces back toward the body. Without
            // the flip both surfaces face the same way and the shell renders inside-out from
            // underneath.
            for (int j = 0; j < regionTris.Length; j += 3)
            {
                tris.Add(regionTris[j]);
                tris.Add(regionTris[j + 2]);
                tris.Add(regionTris[j + 1]);
            }

            // Rim wall. Each boundary edge is stored in the direction its one owning triangle
            // used, which (for outward-wound source geometry) runs counter-clockwise around the
            // region seen from outside - so this winding puts the wall's normal outward, away
            // from the region, rather than tucked inside the shell.
            for (int k = 0; k < boundaryU.Count; k++)
            {
                int u = boundaryU[k], v = boundaryV[k];
                tris.Add(n + u); tris.Add(u); tris.Add(v);
                tris.Add(n + u); tris.Add(v); tris.Add(n + v);
            }

            // ---------------------------------------------------------------- 6. polish pass
            if (settings.SurfaceSmoothing > 0)
                Smooth(verts, tris, settings.SurfaceSmoothing);

            // Pull the inner surface back onto the source. Smoothing is what rounds the rim and
            // settles the form, but it also drifts the inner face off the body - re-pinning it
            // to its pre-smoothing position (which already includes the boundary relaxation
            // from step 4, so that isn't undone) keeps the piece sitting flush while the outer
            // face stays fully smoothed.
            float shrinkwrap = Mathf.Clamp01(settings.Shrinkwrap);
            if (shrinkwrap > 0f)
                for (int i = 0; i < n; i++)
                    verts[i] = Vector3.Lerp(verts[i], basePos[i], shrinkwrap);

            // ------------------------------------------------------------------- 7. the mesh
            var mesh = new Mesh { name = source.name + " Extract (Source)" };
            if (verts.Length > 65000) mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            triangleCount = tris.Count / 3;
            return mesh;
        }

        private static float MaskAt(float[] mask, int index, bool invert) =>
            invert ? 1f - mask[index] : mask[index];

        /// Largest half-extent of the source in its own local space - the "how big is this
        /// object" reference every fraction-based setting is measured against. Mirrors
        /// SculptableMesh's own _cavityLengthScale, which needed the same thing for the same
        /// reason (a shape measure that behaves identically at any object scale).
        private static float LargestExtent(Vector3[] verts)
        {
            if (verts.Length == 0) return 1f;
            Vector3 min = verts[0], max = verts[0];
            for (int i = 1; i < verts.Length; i++)
            {
                min = Vector3.Min(min, verts[i]);
                max = Vector3.Max(max, verts[i]);
            }
            Vector3 extents = (max - min) * 0.5f;
            float e = Mathf.Max(extents.x, Mathf.Max(extents.y, extents.z));
            return e > 1e-6f ? e : 1f;
        }

        /// Edges used by exactly one selected triangle - i.e. where the region stops. Returned
        /// as parallel u/v lists carrying the DIRECTION that one owning triangle used, which the
        /// rim wall needs to wind itself correctly.
        ///
        /// Two passes over the triangles rather than one pass storing directions alongside the
        /// counts: the second pass re-derives the direction for free from the triangle that
        /// still owns it, so nothing has to be kept per edge except the count.
        private static void FindBoundaryEdges(int[] tris, out List<int> boundaryU, out List<int> boundaryV)
        {
            var counts = new Dictionary<long, int>(tris.Length);
            for (int j = 0; j < tris.Length; j += 3)
            {
                Bump(counts, tris[j], tris[j + 1]);
                Bump(counts, tris[j + 1], tris[j + 2]);
                Bump(counts, tris[j + 2], tris[j]);
            }

            boundaryU = new List<int>();
            boundaryV = new List<int>();
            for (int j = 0; j < tris.Length; j += 3)
            {
                TryAdd(counts, tris[j], tris[j + 1], boundaryU, boundaryV);
                TryAdd(counts, tris[j + 1], tris[j + 2], boundaryU, boundaryV);
                TryAdd(counts, tris[j + 2], tris[j], boundaryU, boundaryV);
            }
        }

        private static void Bump(Dictionary<long, int> counts, int a, int b)
        {
            long key = EdgeKey(a, b);
            counts.TryGetValue(key, out int c);
            counts[key] = c + 1;
        }

        private static void TryAdd(Dictionary<long, int> counts, int a, int b, List<int> us, List<int> vs)
        {
            if (counts[EdgeKey(a, b)] != 1) return;
            us.Add(a);
            vs.Add(b);
        }

        /// Order-independent key for an undirected edge. Vertex indices are non-negative and
        /// well under 2^31, so packing the smaller into the high word and the larger into the
        /// low word is collision-free.
        private static long EdgeKey(int a, int b) =>
            a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// Relaxes boundary vertices toward the midpoint of their boundary neighbours, along the
        /// boundary only. This is what turns a stair-stepped mask edge into a clean one: the
        /// region can only ever be a whole number of triangles, so its raw edge zigzags at the
        /// tessellation scale no matter how carefully the mask was painted, and no amount of
        /// whole-surface smoothing fixes that without also destroying the shape.
        ///
        /// Jacobi (compute every new position from the OLD buffer, then swap) rather than
        /// in-place Gauss-Seidel - updating in place makes the result depend on vertex index
        /// order, which would make the same mask smooth differently on a remeshed copy of the
        /// same sculpt.
        private static void SmoothBoundary(Vector3[] pos, List<int> boundaryU, List<int> boundaryV,
                                           int vertexCount, int iterations)
        {
            if (boundaryU.Count == 0) return;

            // Only boundary vertices get a neighbour list - on a big region that's a small
            // fraction of the whole, so leaving the rest null avoids allocating tens of
            // thousands of Lists that would never be read.
            var neighbours = new List<int>[vertexCount];
            for (int k = 0; k < boundaryU.Count; k++)
            {
                int u = boundaryU[k], v = boundaryV[k];
                (neighbours[u] ??= new List<int>(2)).Add(v);
                (neighbours[v] ??= new List<int>(2)).Add(u);
            }

            var next = new Vector3[vertexCount];
            for (int iter = 0; iter < iterations; iter++)
            {
                for (int i = 0; i < vertexCount; i++)
                {
                    List<int> nb = neighbours[i];
                    if (nb == null || nb.Count == 0) { next[i] = pos[i]; continue; }

                    Vector3 sum = Vector3.zero;
                    for (int k = 0; k < nb.Count; k++) sum += pos[nb[k]];
                    next[i] = Vector3.Lerp(pos[i], sum / nb.Count, 0.6f);
                }
                System.Array.Copy(next, pos, vertexCount);
            }
        }

        /// Taubin smoothing (a λ pass followed by a larger negative μ pass), not plain
        /// Laplacian. Plain Laplacian shrinks whatever it smooths, which on a thin shell is
        /// actively destructive: the inner and outer surfaces are only a few percent of the
        /// object apart, so a handful of shrinking passes visibly eats the thickness the user
        /// just dialled in. The negative pass pushes back out at a slightly larger rate,
        /// leaving the low-frequency shape (and therefore the thickness) essentially where it
        /// was while still removing the high-frequency rim hardness and faceting.
        private static void Smooth(Vector3[] verts, List<int> tris, int iterations)
        {
            BuildAdjacency(verts.Length, tris, out int[] offsets, out int[] neighbours);
            var scratch = new Vector3[verts.Length];

            for (int iter = 0; iter < iterations; iter++)
            {
                SmoothPass(verts, scratch, offsets, neighbours, TaubinLambda);
                SmoothPass(verts, scratch, offsets, neighbours, TaubinMu);
            }
        }

        private static void SmoothPass(Vector3[] verts, Vector3[] scratch, int[] offsets, int[] neighbours, float factor)
        {
            for (int i = 0; i < verts.Length; i++)
            {
                int start = offsets[i], end = offsets[i + 1];
                if (end <= start) { scratch[i] = verts[i]; continue; }

                Vector3 sum = Vector3.zero;
                for (int k = start; k < end; k++) sum += verts[neighbours[k]];
                Vector3 average = sum / (end - start);
                scratch[i] = verts[i] + (average - verts[i]) * factor;
            }
            System.Array.Copy(scratch, verts, verts.Length);
        }

        /// CSR adjacency (neighbours of i live in neighbours[offsets[i]..offsets[i+1])) built
        /// from a de-duplicated edge set. Deliberately NOT the per-vertex HashSet approach
        /// SculptableMesh.BuildAdjacency uses - that allocates one collection per vertex, which
        /// is fine there (once per topology change) but not here, where a preview rebuild can
        /// run several times a second while the user drags a slider. One shared HashSet of
        /// packed edge keys plus two flat arrays does the same job with three allocations.
        private static void BuildAdjacency(int vertexCount, List<int> tris, out int[] offsets, out int[] neighbours)
        {
            var edges = new HashSet<long>();
            for (int j = 0; j < tris.Count; j += 3)
            {
                edges.Add(EdgeKey(tris[j], tris[j + 1]));
                edges.Add(EdgeKey(tris[j + 1], tris[j + 2]));
                edges.Add(EdgeKey(tris[j + 2], tris[j]));
            }

            var degree = new int[vertexCount];
            foreach (long key in edges)
            {
                degree[(int)(key >> 32)]++;
                degree[(int)(key & 0xFFFFFFFF)]++;
            }

            offsets = new int[vertexCount + 1];
            int cursor = 0;
            for (int i = 0; i < vertexCount; i++)
            {
                offsets[i] = cursor;
                cursor += degree[i];
            }
            offsets[vertexCount] = cursor;

            neighbours = new int[cursor];
            var fill = new int[vertexCount];
            foreach (long key in edges)
            {
                int a = (int)(key >> 32);
                int b = (int)(key & 0xFFFFFFFF);
                neighbours[offsets[a] + fill[a]++] = b;
                neighbours[offsets[b] + fill[b]++] = a;
            }
        }
    }
}
