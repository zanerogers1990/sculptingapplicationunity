using System;
using UnityEngine;

namespace Sculpting.Molding
{
    /// The parting surface, as a height field H(r, e) over the mold block's footprint.
    ///
    /// The addon's central idea, kept intact: the split is not a plane and not a mesh the user
    /// has to keep watertight - it is a single-valued height above the (Right, Eye) plane. Both
    /// halves are then built from the SAME nodes, one filling downwards and one upwards, so
    /// however the surface bends the two pieces mate exactly by construction. There is no
    /// tolerance to tune and no seam to repair, which is why this beats "boolean the model out of
    /// a box and then cut the box in two".
    ///
    /// The cost of that guarantee is that an overhanging parting surface cannot be expressed -
    /// but an overhanging parting surface would not release anyway, so the representation is
    /// exactly as expressive as the problem needs.
    public sealed class PartingField
    {
        public MoldFrame Frame;

        public readonly int Nr;
        public readonly int Ne;
        /// Frame-space extent of the grid: the padded block footprint.
        public readonly float R0, R1, E0, E1;

        /// Split height per node, row-major [i * Ne + j].
        public readonly float[] H;

        public float Dr => Nr > 1 ? (R1 - R0) / (Nr - 1) : 0f;
        public float De => Ne > 1 ? (E1 - E0) / (Ne - 1) : 0f;

        public float RAt(int i) => Nr > 1 ? Mathf.Lerp(R0, R1, i / (float)(Nr - 1)) : R0;
        public float EAt(int j) => Ne > 1 ? Mathf.Lerp(E0, E1, j / (float)(Ne - 1)) : E0;

        public PartingField(MoldFrame frame, float r0, float r1, float e0, float e1, int nr, int ne)
        {
            Frame = frame;
            R0 = r0; R1 = r1; E0 = e0; E1 = e1;
            Nr = Mathf.Max(2, nr);
            Ne = Mathf.Max(2, ne);
            H = new float[Nr * Ne];
        }

        /// A flat split at a constant height - the fallback when no fit has been run, and what
        /// the "flat plane" mode produces. Still a full field rather than a special case, so
        /// every downstream stage has exactly one kind of parting surface to handle.
        public static PartingField Flat(MoldFrame frame, float r0, float r1, float e0, float e1,
                                        int nr, int ne, float height)
        {
            var f = new PartingField(frame, r0, r1, e0, e1, nr, ne);
            f.Fill(height);
            return f;
        }

        /// Sets every node to one height. Split out of Flat because the flat split now has to
        /// build its grid, measure the model's centre through it, and only then fill it - the
        /// centre is not known until the column map exists.
        public void Fill(float height)
        {
            for (int k = 0; k < H.Length; k++) H[k] = height;
        }

        public PartingField Clone()
        {
            var f = new PartingField(Frame, R0, R1, E0, E1, Nr, Ne);
            Array.Copy(H, f.H, H.Length);
            return f;
        }

        // ------------------------------------------------------------------------ sampling

        /// Bilinear sample, clamped to the grid at the edges. The addon's field_at, one point at
        /// a time - the callers here are per-vertex loops that are already parallel, so there is
        /// nothing for a vectorised version to win.
        public float Sample(float r, float e)
        {
            float fr = Nr > 1 ? (r - R0) / Mathf.Max(Dr, 1e-9f) : 0f;
            float fe = Ne > 1 ? (e - E0) / Mathf.Max(De, 1e-9f) : 0f;

            int i = Mathf.Clamp(Mathf.FloorToInt(fr), 0, Nr - 2);
            int j = Mathf.Clamp(Mathf.FloorToInt(fe), 0, Ne - 2);
            float tr = Mathf.Clamp01(fr - i);
            float te = Mathf.Clamp01(fe - j);

            float h00 = H[i * Ne + j], h10 = H[(i + 1) * Ne + j];
            float h01 = H[i * Ne + j + 1], h11 = H[(i + 1) * Ne + j + 1];
            return Mathf.Lerp(Mathf.Lerp(h00, h10, tr), Mathf.Lerp(h01, h11, tr), te);
        }

        public float SampleWorld(Vector3 world)
        {
            Vector3 f = Frame.ToFrame(world);
            return Sample(f.x, f.z);
        }

        /// Signed height of a world point above the parting surface. Positive means it belongs
        /// to the upper half.
        public float SignedHeightWorld(Vector3 world)
        {
            Vector3 f = Frame.ToFrame(world);
            return f.y - Sample(f.x, f.z);
        }

        // ------------------------------------------------------------------- field editing

        /// Box blur with edge padding - the addon's smooth_field. This is what rounds the cut so
        /// it prints and releases cleanly; the 4:1 centre weighting keeps it from eating the
        /// surface's real shape as fast as a plain average would.
        public void Smooth(int passes)
        {
            if (passes <= 0) return;
            var tmp = new float[H.Length];
            for (int p = 0; p < passes; p++)
            {
                for (int i = 0; i < Nr; i++)
                {
                    int im = Mathf.Max(i - 1, 0) * Ne, ip = Mathf.Min(i + 1, Nr - 1) * Ne, ic = i * Ne;
                    for (int j = 0; j < Ne; j++)
                    {
                        int jm = Mathf.Max(j - 1, 0), jp = Mathf.Min(j + 1, Ne - 1);
                        tmp[ic + j] = (H[im + j] + H[ip + j] + H[ic + jm] + H[ic + jp] + 4f * H[ic + j]) / 8f;
                    }
                }
                Array.Copy(tmp, H, H.Length);
            }
        }

        /// Diffuses known values into the cells that had no surface under them - the block's
        /// padding, and any column the model simply did not cover. The addon's fill_holes: seed
        /// the unknowns with the median of the knowns, then Jacobi-relax until it stops moving.
        ///
        /// Without this the surface falls to zero outside the model's footprint and the parting
        /// line drops off a cliff at the model's silhouette, which is both unprintable and
        /// exactly where the halves have to seal against each other.
        public void FillHoles(bool[] known)
        {
            int n = H.Length;
            int knownCount = 0;
            for (int k = 0; k < n; k++) if (known[k]) knownCount++;
            if (knownCount == 0) { Array.Clear(H, 0, n); return; }

            float median = Median(H, known, knownCount);
            for (int k = 0; k < n; k++) if (!known[k]) H[k] = median;

            // NOTE (measured 2026-09-20): this pass count dominates a fit at fine grid density -
            // 170ms of a 192ms fit at a 0.25mm cell, because it scales with the GRID while the
            // band it has to fill is the block's padding, a fixed size in millimetres. Sizing it
            // by the chamfer distance to the nearest known cell instead cuts a fit 192ms -> 79ms.
            // Left alone deliberately: the relaxation does not converge at EITHER count (the
            // field is still ~0.40 from converged here, on a range of 0.65), so changing the
            // count changes the extrapolated padding - which is where the two halves seal. Worth
            // doing as its own piece of work, with the built mold looked at.
            //
            // What CAN change without changing a single bit of the result is how each pass is
            // run. This is Jacobi - every new value reads only the previous pass - so the cells
            // of one pass are independent: the unknown cells and their four (edge-clamped)
            // neighbours are indexed once up front, known cells are never touched again (they
            // never change), the two buffers swap instead of being copied back each pass, and
            // big grids split each pass across cores. Each cell still computes the identical
            // expression in the identical operand order, and a max is exact whatever order it is
            // taken in, so the output is bit-for-bit the old one's. On the user's 2.9M-triangle
            // lure this pass was 335ms of a 497ms fit.
            int iterations = Mathf.Max(Nr, Ne) * 2;
            Relax(known, n - knownCount, iterations);
        }

        /// The Jacobi passes of FillHoles - see the note there for why this is laid out as it is.
        private void Relax(bool[] known, int unknownCount, int iterations)
        {
            int n = H.Length;
            if (unknownCount <= 0 || iterations <= 0) return;

            var cells = new int[unknownCount];
            var neighbours = new int[unknownCount * 4];
            int w = 0;
            for (int i = 0; i < Nr; i++)
            {
                int im = Mathf.Max(i - 1, 0) * Ne, ip = Mathf.Min(i + 1, Nr - 1) * Ne, ic = i * Ne;
                for (int j = 0; j < Ne; j++)
                {
                    int at = ic + j;
                    if (known[at]) continue;
                    int jm = Mathf.Max(j - 1, 0), jp = Mathf.Min(j + 1, Ne - 1);
                    cells[w] = at;
                    // Same four terms, same order, as the sum below always used.
                    neighbours[4 * w] = im + j;
                    neighbours[4 * w + 1] = ip + j;
                    neighbours[4 * w + 2] = ic + jm;
                    neighbours[4 * w + 3] = ic + jp;
                    w++;
                }
            }

            float[] src = new float[n];
            float[] dst = new float[n];
            Array.Copy(H, src, n);
            Array.Copy(H, dst, n);

            // Below a few thousand cells a pass is microseconds, and the fork/join would cost more
            // than it saves.
            int chunks = unknownCount >= 8192 ? Mathf.Clamp(Environment.ProcessorCount, 1, 16) : 1;
            int chunkSize = (unknownCount + chunks - 1) / chunks;
            var chunkMax = new float[chunks];

            for (int it = 0; it < iterations; it++)
            {
                float[] from = src, to = dst;
                if (chunks == 1)
                {
                    chunkMax[0] = RelaxRange(from, to, cells, neighbours, 0, unknownCount);
                }
                else
                {
                    System.Threading.Tasks.Parallel.For(0, chunks, c =>
                    {
                        int a = c * chunkSize;
                        int b = Mathf.Min(unknownCount, a + chunkSize);
                        chunkMax[c] = a < b ? RelaxRange(from, to, cells, neighbours, a, b) : 0f;
                    });
                }

                float maxDelta = 0f;
                for (int c = 0; c < chunks; c++) if (chunkMax[c] > maxDelta) maxDelta = chunkMax[c];

                src = to;
                dst = from;
                if (maxDelta <= 1e-6f) break;
            }

            Array.Copy(src, H, n);
        }

        private static float RelaxRange(float[] from, float[] to, int[] cells, int[] neighbours, int a, int b)
        {
            float maxDelta = 0f;
            for (int k = a; k < b; k++)
            {
                int at = cells[k];
                int q = 4 * k;
                float avg = (from[neighbours[q]] + from[neighbours[q + 1]] + from[neighbours[q + 2]] + from[neighbours[q + 3]]) * 0.25f;
                float delta = Math.Abs(avg - from[at]);
                if (delta > maxDelta) maxDelta = delta;
                to[at] = avg;
            }
            return maxDelta;
        }

        private static float Median(float[] values, bool[] mask, int count)
        {
            var picked = new float[count];
            int w = 0;
            for (int k = 0; k < values.Length; k++) if (mask[k]) picked[w++] = values[k];
            Array.Sort(picked);
            return picked[count / 2];
        }

        /// Keeps the surface inside the block, so a half never ends up with zero thickness
        /// somewhere. Returns true if anything was actually clipped - worth telling the user,
        /// since it means the fit wanted to go somewhere the block does not reach.
        public bool ClampHeights(float lo, float hi)
        {
            bool clipped = false;
            for (int k = 0; k < H.Length; k++)
            {
                float v = Mathf.Clamp(H[k], lo, hi);
                if (v != H[k]) { H[k] = v; clipped = true; }
            }
            return clipped;
        }

        public void Offset(float du)
        {
            for (int k = 0; k < H.Length; k++) H[k] += du;
        }

        // ------------------------------------------------------------------ mesh building

        /// The parting sheet on its own, as a world-space mesh with normals - what the viewport
        /// overlay draws. Double-sided is the renderer's job (the overlay material turns culling
        /// off); this emits one set of faces pointing along +Up.
        public Mesh BuildSurfaceMesh()
        {
            var verts = new Vector3[Nr * Ne];
            for (int i = 0; i < Nr; i++)
            {
                float r = RAt(i);
                for (int j = 0; j < Ne; j++)
                    verts[i * Ne + j] = Frame.ToWorld(new Vector3(r, H[i * Ne + j], EAt(j)));
            }

            var tris = new int[(Nr - 1) * (Ne - 1) * 6];
            int t = 0;
            for (int i = 0; i < Nr - 1; i++)
            {
                for (int j = 0; j < Ne - 1; j++)
                {
                    int a = i * Ne + j, b = i * Ne + j + 1, c = (i + 1) * Ne + j + 1, d = (i + 1) * Ne + j;
                    // Winding derived in BuildSlab's remarks: (00, 01, 11) / (00, 11, 10) faces +Up.
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;
                    tris[t++] = a; tris[t++] = c; tris[t++] = d;
                }
            }

            var mesh = new Mesh { name = "Parting Surface" };
            if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// A closed solid between the parting surface and a flat face at `uFlat` - one raw mold
        /// half, before anything is cut out of it. World space, outward-facing, watertight.
        ///
        /// `uFlat` below the surface gives the LOWER half, above it the upper one; the routine
        /// sorts that out itself (see BuildSlab) so callers never have to think about winding.
        public void BuildHalfSolid(float uFlat, out Vector3[] verts, out int[] tris)
        {
            var lower = new float[H.Length];
            var upper = new float[H.Length];
            bool flatIsBelow = true;
            for (int k = 0; k < H.Length; k++)
            {
                if (uFlat <= H[k]) { lower[k] = uFlat; upper[k] = H[k]; }
                else { lower[k] = H[k]; upper[k] = uFlat; flatIsBelow = false; }
            }
            // The mixed case (the flat face crossing the surface) cannot happen for a field that
            // has been clamped into the block, and if it somehow did the slab would pinch to zero
            // thickness there rather than self-intersect - which the voxel boolean handles.
            _ = flatIsBelow;

            BuildSlab(lower, upper, out verts, out tris);
        }

        /// Closed solid between two height grids, lower <= upper. Single place the block's
        /// winding lives.
        ///
        /// The frame has determinant +1, so "outward" can be reasoned about entirely in frame
        /// coordinates (r = x, u = y, e = z) and carries into world space unchanged. With Unity's
        /// convention that a triangle's normal is cross(v1 - v0, v2 - v0):
        ///   - upper sheet (normal +y): (00, 01, 11) and (00, 11, 10)
        ///   - lower sheet (normal -y): the same two reversed
        ///   - e = E0 wall (normal -z): (U0, L1, L0) and (U0, U1, L1)
        ///   - e = E1 wall (normal +z): those two reversed
        ///   - r = R0 wall (normal -x): (U0, L0, L1) and (U0, L1, U1)
        ///   - r = R1 wall (normal +x): those two reversed
        /// MoldGeometryTests checks the whole thing by signed volume rather than by trusting this
        /// list, since an inverted solid reads to the voxel boolean as "everything except the
        /// block" and would quietly produce a mold with the world subtracted from it.
        private void BuildSlab(float[] lower, float[] upper, out Vector3[] verts, out int[] tris)
        {
            int n = Nr * Ne;
            verts = new Vector3[n * 2];
            for (int i = 0; i < Nr; i++)
            {
                float r = RAt(i);
                for (int j = 0; j < Ne; j++)
                {
                    float e = EAt(j);
                    int k = i * Ne + j;
                    verts[k] = Frame.ToWorld(new Vector3(r, upper[k], e));       // upper sheet: [0, n)
                    verts[n + k] = Frame.ToWorld(new Vector3(r, lower[k], e));   // lower sheet: [n, 2n)
                }
            }

            int quadsRE = (Nr - 1) * (Ne - 1);
            int triCount = quadsRE * 2 * 2 + (Nr - 1) * 2 * 2 + (Ne - 1) * 2 * 2;
            tris = new int[triCount * 3];
            int t = 0;

            for (int i = 0; i < Nr - 1; i++)
            {
                for (int j = 0; j < Ne - 1; j++)
                {
                    int a = i * Ne + j, b = i * Ne + j + 1, c = (i + 1) * Ne + j + 1, d = (i + 1) * Ne + j;
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;
                    tris[t++] = a; tris[t++] = c; tris[t++] = d;

                    int la = n + a, lb = n + b, lc = n + c, ld = n + d;
                    tris[t++] = la; tris[t++] = lc; tris[t++] = lb;
                    tris[t++] = la; tris[t++] = ld; tris[t++] = lc;
                }
            }

            // Walls at e = E0 (j = 0) and e = E1 (j = Ne - 1), swept along i.
            for (int i = 0; i < Nr - 1; i++)
            {
                int u0 = i * Ne, u1 = (i + 1) * Ne;
                int l0 = n + u0, l1 = n + u1;
                tris[t++] = u0; tris[t++] = l1; tris[t++] = l0;
                tris[t++] = u0; tris[t++] = u1; tris[t++] = l1;

                int v0 = i * Ne + (Ne - 1), v1 = (i + 1) * Ne + (Ne - 1);
                int m0 = n + v0, m1 = n + v1;
                tris[t++] = v0; tris[t++] = m0; tris[t++] = m1;
                tris[t++] = v0; tris[t++] = m1; tris[t++] = v1;
            }

            // Walls at r = R0 (i = 0) and r = R1 (i = Nr - 1), swept along j.
            for (int j = 0; j < Ne - 1; j++)
            {
                int u0 = j, u1 = j + 1;
                int l0 = n + u0, l1 = n + u1;
                tris[t++] = u0; tris[t++] = l0; tris[t++] = l1;
                tris[t++] = u0; tris[t++] = l1; tris[t++] = u1;

                int v0 = (Nr - 1) * Ne + j, v1 = (Nr - 1) * Ne + j + 1;
                int m0 = n + v0, m1 = n + v1;
                tris[t++] = v0; tris[t++] = m1; tris[t++] = m0;
                tris[t++] = v0; tris[t++] = v1; tris[t++] = m1;
            }
        }

        /// Where a world ray first crosses the parting surface, or false if it never does inside
        /// the grid's footprint.
        ///
        /// Marched-and-bisected rather than raycast against a collider, on purpose: the surface
        /// is a live overlay that rebuilds as the user drags, and giving it a MeshCollider would
        /// put it in front of SelectionManager's own picking (it would become a clickable,
        /// selectable object in the middle of the model). Marching keeps the surface purely
        /// visual and costs nothing measurable - it is a few dozen bilinear samples on a click.
        public bool RaycastSurface(Ray worldRay, float maxDistance, out Vector3 hit)
        {
            hit = default;

            Vector3 o = Frame.ToFrame(worldRay.origin);
            Vector3 d = Frame.ToFrame(worldRay.direction);  // rotation-only frame: safe on a direction

            // March only the span of the ray that is actually over the grid, not the whole of
            // `maxDistance`.
            //
            // This used to divide `maxDistance` into a fixed number of steps regardless of how
            // much of it could possibly hit anything. Callers pass a reach with a generous
            // constant added so that a far-off camera still reaches the mold (PlaceAt adds
            // 1000 world units), which made the step several times WIDER than the entire block
            // on any normal sculpt - 5.2 units per step across a mold 2.2 units long. A
            // crossing is only ever detected between two CONSECUTIVE samples that are both over
            // the footprint, so the march jumped clean over the surface and every placement
            // click missed. Clipping to the slab first makes the step proportional to the part
            // of the ray that can actually cross, which is what the step count was always
            // assuming.
            float tEnter = 0f, tExit = maxDistance;
            if (!ClipSlab(o.x, d.x, R0, R1, ref tEnter, ref tExit)) return false;
            if (!ClipSlab(o.z, d.z, E0, E1, ref tEnter, ref tExit)) return false;

            // Bilinear sampling never leaves the range of the nodes it interpolates, so the
            // crossing has to lie inside the height band too. Clipping to it as well keeps the
            // step tight on a grazing ray that skims the footprint for a long way without ever
            // getting near the sheet. The pad keeps a crossing that sits exactly on the band's
            // edge bracketed by samples on both sides.
            MinMaxHeight(out float hMin, out float hMax);
            float pad = 0.05f * Mathf.Max(hMax - hMin, 1e-4f) + 1e-4f;
            if (!ClipSlab(o.y, d.y, hMin - pad, hMax + pad, ref tEnter, ref tExit)) return false;

            if (tExit <= tEnter) return false;

            const int steps = 192;
            float step = (tExit - tEnter) / steps;
            float prevT = 0f;
            bool havePrev = false;
            float prevGap = 0f;

            for (int s = 0; s <= steps; s++)
            {
                float t = tEnter + s * step;
                Vector3 p = o + d * t;
                if (p.x < R0 || p.x > R1 || p.z < E0 || p.z > E1)
                {
                    // Outside the footprint the field has no meaning - drop the previous sample
                    // so a crossing is never interpolated across the gap where the ray left and
                    // re-entered the grid.
                    havePrev = false;
                    continue;
                }

                float gap = p.y - Sample(p.x, p.z);
                if (havePrev && ((gap <= 0f && prevGap > 0f) || (gap >= 0f && prevGap < 0f)))
                {
                    float lo = prevT, hi = t;
                    for (int b = 0; b < 24; b++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Vector3 q = o + d * mid;
                        float g = q.y - Sample(Mathf.Clamp(q.x, R0, R1), Mathf.Clamp(q.z, E0, E1));
                        if ((g <= 0f) == (prevGap <= 0f)) lo = mid; else hi = mid;
                    }
                    hit = worldRay.GetPoint(0.5f * (lo + hi));
                    return true;
                }

                prevGap = gap;
                prevT = t;
                havePrev = true;
            }

            return false;
        }

        /// Standard slab clip: narrows [tEnter, tExit] to the stretch of `origin + t * dir` that
        /// lies within [lo, hi] on one axis. False when the ray misses that slab altogether, in
        /// which case the caller can stop without marching anything.
        private static bool ClipSlab(float origin, float dir, float lo, float hi,
                                     ref float tEnter, ref float tExit)
        {
            // Parallel to the slab: either the ray is inside it for its whole length or it never
            // enters, and dividing by the near-zero direction would produce infinities.
            if (Mathf.Abs(dir) < 1e-9f) return origin >= lo && origin <= hi;

            float t0 = (lo - origin) / dir;
            float t1 = (hi - origin) / dir;
            if (t0 > t1) { float swap = t0; t0 = t1; t1 = swap; }

            if (t0 > tEnter) tEnter = t0;
            if (t1 < tExit) tExit = t1;
            return tEnter <= tExit;
        }

        /// The band every sampled height lies in. Used to bound a ray march; also the honest
        /// answer to "how far does this surface bend", which the panel reports.
        public void MinMaxHeight(out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            for (int k = 0; k < H.Length; k++)
            {
                float h = H[k];
                if (h < min) min = h;
                if (h > max) max = h;
            }
            if (min > max) min = max = 0f;
        }
    }
}
