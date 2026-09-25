using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// Detail and closure options for a lathe mesh. The SHAPE lives in LatheProfile; these only
    /// decide how it is tessellated and whether open ends get closed.
    public struct LatheSettings
    {
        /// Vertices around the widest ring. Rounded to a multiple of 4 (see LatheMeshBuilder).
        public int RadialSegments;
        /// Closes an open end that stops short of the axis with a flat disc, so the result is a
        /// watertight solid. Off leaves the end open (a vase, a tube).
        public bool CapEnds;
        /// Fewer vertices on rings near the axis, so triangles stay close to the same size all
        /// over instead of squeezing into slivers at the poles. What sculpting brushes want.
        public bool EvenTriangles;

        public static LatheSettings Default => new LatheSettings
        {
            RadialSegments = 64,
            CapEnds = true,
            EvenTriangles = true
        };
    }

    /// Everything one build produced: the tessellated profile (for drawing the silhouette exactly
    /// as the mesh has it), the mesh arrays, and what is worth telling the artist about it. Reused
    /// across builds, so a live drag rebuilds without allocating.
    public sealed class LatheBuildResult
    {
        /// The profile as tessellated - caps included, oriented so the surface faces outward.
        public readonly List<Vector2> Profile = new List<Vector2>();
        public bool ProfileIsLoop;

        public readonly List<Vector3> Vertices = new List<Vector3>();
        public readonly List<Vector3> Normals = new List<Vector3>();
        public readonly List<int> Triangles = new List<int>();

        public int VertexCount => Vertices.Count;
        public int TriangleCount => Triangles.Count / 3;

        /// No boundary edges: both ends closed (on the axis or capped), or a loop.
        public bool Watertight;
        /// The profile crosses itself, so the revolved surface passes through itself.
        public bool SelfIntersecting;
        /// Part of the curve swung across the axis between two points and was held just off it -
        /// the shape has a very thin neck there, which is rarely what was meant.
        public bool TouchesAxis;
        /// Null when the build succeeded.
        public string Error;

        public void Clear()
        {
            Profile.Clear();
            Vertices.Clear();
            Normals.Clear();
            Triangles.Clear();
            ProfileIsLoop = false;
            Watertight = false;
            SelfIntersecting = false;
            TouchesAxis = false;
            Error = null;
        }
    }

    /// Revolves a LatheProfile around the local Y axis into a clean, welded triangle mesh.
    ///
    /// The geometry is built for sculpting, not just for looking at:
    ///   - the profile is resampled at an even arc-length spacing matched to the widest ring's
    ///     vertex spacing, so faces come out close to square rather than long strips;
    ///   - with EvenTriangles, each ring gets a vertex count proportional to its radius, and rings
    ///     of different counts are zipped together, so there are no sliver fans at the poles;
    ///   - an end on the axis becomes ONE pole vertex (never a ring of coincident vertices), so
    ///     the mesh is 2-manifold and closed - ready for brushes, Remesh, Boolean and the mold
    ///     maker;
    ///   - it is EXACTLY mirror-symmetric across both the X and Z planes, positions and topology
    ///     alike (see Ring and StitchRings), because this app's mirrored sculpting pairs vertices
    ///     by position and smooths them over their neighbours - a mesh whose two halves are
    ///     triangulated differently drifts apart under a mirrored Smooth.
    public static class LatheMeshBuilder
    {
        public const int MinSegments = 8;
        public const int MaxSegments = 256;

        /// Upper bound on profile samples, so a tall, thin profile at high detail cannot ask for
        /// millions of vertices. Past this the profile spacing grows instead.
        public const int MaxProfileSamples = 512;

        /// The vertex colour SculptableMesh writes for an untouched, unmasked vertex (cavity .r and
        /// wash .b at neutral 0.5, mask .g at 0). The preview uses the sculpt material, which reads
        /// these channels, so an uncoloured mesh would render with a full mask tint.
        public static readonly Color NeutralVertexColor = new Color(0.5f, 0f, 0.5f, 1f);

        public static int RoundSegments(int segments) =>
            Mathf.Clamp(Mathf.RoundToInt(segments / 4f) * 4, MinSegments, MaxSegments);

        // Scratch, reused across builds. The builder is only ever driven from the main thread.
        private static readonly List<LatheCurve.Sample> SampleScratch = new List<LatheCurve.Sample>();
        private static readonly List<bool> CornerScratch = new List<bool>();
        private static readonly List<int> RingStart = new List<int>();
        private static readonly List<int> RingCount = new List<int>();
        private static readonly List<int> Stitch = new List<int>();
        private static readonly Dictionary<int, (float[] cos, float[] sin)> AngleTables =
            new Dictionary<int, (float[], float[])>();

        public static bool Build(LatheProfile profile, LatheSettings settings, LatheBuildResult result)
        {
            result.Clear();
            if (profile == null || profile.Count < profile.MinPoints)
            {
                result.Error = profile != null && profile.ClosedLoop
                    ? "A closed loop needs at least 3 points."
                    : "Click in the viewport to add profile points (at least 2).";
                return false;
            }

            float maxRadius = profile.MaxRadius;
            if (maxRadius < profile.Size * 1e-3f)
            {
                result.Error = "Every point is on the axis - drag one out to give the shape some width.";
                return false;
            }

            bool loop = profile.ClosedLoop;
            int segments = RoundSegments(settings.RadialSegments);

            // The spacing that makes a face on the widest ring square. Widened if the profile is
            // so long relative to its radius that it would exceed the sample budget.
            float spacing = 2f * Mathf.PI * maxRadius / segments;
            float estimatedLength = LatheCurve.ControlPolygonLength(profile.Points, loop) * 1.25f;
            if (settings.CapEnds && !loop) estimatedLength += 2f * maxRadius;
            if (estimatedLength / spacing > MaxProfileSamples) spacing = estimatedLength / MaxProfileSamples;

            SampleScratch.Clear();
            LatheCurve.Resample(profile.Points, loop, spacing, SampleScratch);
            if (SampleScratch.Count < 2)
            {
                result.Error = "The profile is too short to revolve.";
                return false;
            }

            BuildTessellatedProfile(profile, loop, settings.CapEnds, spacing, result);

            List<Vector2> prof = result.Profile;
            if (prof.Count < (loop ? 3 : 2))
            {
                result.Error = "The profile is too short to revolve.";
                return false;
            }

            OrientOutward(prof, CornerScratch, loop);
            result.ProfileIsLoop = loop;
            result.SelfIntersecting = CrossesItself(prof, loop);

            int count = prof.Count;
            bool startPole = !loop && prof[0].x <= 0f;
            bool endPole = !loop && prof[count - 1].x <= 0f;
            result.Watertight = loop || (startPole && endPole);

            // ---- rings
            RingStart.Clear();
            RingCount.Clear();
            for (int i = 0; i < count; i++)
            {
                bool pole = (i == 0 && startPole) || (i == count - 1 && endPole);
                int ring = pole ? 1 : RingVertexCount(prof[i].x, spacing, segments, settings.EvenTriangles);
                RingStart.Add(result.Vertices.Count);
                RingCount.Add(ring);

                Vector2 normal2 = ProfileNormal(prof, CornerScratch, i, loop);
                if (pole)
                {
                    float sign = normal2.y != 0f ? Mathf.Sign(normal2.y) : (i == 0 ? -1f : 1f);
                    result.Vertices.Add(new Vector3(0f, prof[i].y, 0f));
                    result.Normals.Add(new Vector3(0f, sign, 0f));
                    continue;
                }

                (float[] cos, float[] sin) = Ring(ring);
                float r = prof[i].x, h = prof[i].y;
                for (int k = 0; k < ring; k++)
                {
                    result.Vertices.Add(new Vector3(r * cos[k], h, r * sin[k]));
                    result.Normals.Add(new Vector3(normal2.x * cos[k], normal2.y, normal2.x * sin[k]));
                }
            }

            // ---- strips between consecutive rings
            int strips = loop ? count : count - 1;
            for (int i = 0; i < strips; i++)
            {
                int a = i, b = (i + 1) % count;
                StitchRings(RingStart[a], RingCount[a], RingStart[b], RingCount[b], result.Triangles);
            }

            return true;
        }

        /// Uploads a build into `mesh` (a fresh one when null) - the live preview reuses one Mesh
        /// across rebuilds; Convert asks for a new one to hand to SculptableMesh.
        public static Mesh ToMesh(LatheBuildResult result, Mesh mesh = null)
        {
            if (mesh == null)
            {
                mesh = new Mesh { name = "Lathe" };
            }
            mesh.Clear();
            mesh.indexFormat = result.VertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(result.Vertices);
            mesh.SetNormals(result.Normals);

            var colors = new Color[result.VertexCount];
            for (int i = 0; i < colors.Length; i++) colors[i] = NeutralVertexColor;
            mesh.SetColors(colors);

            mesh.SetTriangles(result.Triangles, 0, true);
            return mesh;
        }

        // ----------------------------------------------------------------- the profile

        /// Resampled curve plus the caps and the axis rules, written into result.Profile with the
        /// corner flags alongside in CornerScratch.
        private static void BuildTessellatedProfile(LatheProfile profile, bool loop, bool capEnds, float spacing,
                                                    LatheBuildResult result)
        {
            List<Vector2> prof = result.Profile;
            CornerScratch.Clear();

            int n = SampleScratch.Count;
            // Held off the axis: at least the profile's own interior minimum, and a quarter of the
            // spacing so the thinnest ring still has room for its four vertices.
            float minInterior = Mathf.Max(profile.MinInteriorRadius, spacing * 0.25f);
            // An end this close to the axis closes onto it. Leaving it would put a ring of a few
            // vertices a hair from the axis - a pinhole with a fan of slivers around it.
            float poleSnap = spacing * 0.5f;

            Vector2 first = SampleScratch[0].Position;
            Vector2 last = SampleScratch[n - 1].Position;
            if (!loop && first.x < poleSnap) first.x = 0f;
            if (!loop && last.x < poleSnap) last.x = 0f;

            if (!loop && capEnds && first.x > 0f) AddCap(prof, first, spacing, fromAxis: true);

            for (int i = 0; i < n; i++)
            {
                Vector2 p = SampleScratch[i].Position;
                bool corner = SampleScratch[i].Corner;
                if (!loop && i == 0) p = first;
                else if (!loop && i == n - 1) p = last;
                else if (p.x < minInterior)
                {
                    p.x = minInterior;
                    result.TouchesAxis = true;
                }

                // A cap meets the wall at a corner.
                if (!loop && capEnds && ((i == 0 && first.x > 0f) || (i == n - 1 && last.x > 0f))) corner = true;
                AddUnique(prof, p, corner, spacing);
            }

            if (!loop && capEnds && last.x > 0f) AddCap(prof, last, spacing, fromAxis: false);

            // A loop's closing duplicate, if resampling rounding left one.
            if (loop && prof.Count > 2 && (prof[0] - prof[prof.Count - 1]).sqrMagnitude < (spacing * 1e-3f) * (spacing * 1e-3f))
            {
                prof.RemoveAt(prof.Count - 1);
                CornerScratch.RemoveAt(CornerScratch.Count - 1);
            }
        }

        /// A flat disc from the rim point to the axis, at the rim's height, sampled at the same
        /// spacing as the wall so the cap's faces match the wall's.
        private static void AddCap(List<Vector2> prof, Vector2 rim, float spacing, bool fromAxis)
        {
            int pieces = Mathf.Max(1, Mathf.RoundToInt(rim.x / spacing));
            if (fromAxis)
            {
                // Axis up to (not including) the rim, which the wall's first sample supplies.
                for (int k = 0; k < pieces; k++)
                    AddUnique(prof, new Vector2(rim.x * k / pieces, rim.y), false, spacing);
            }
            else
            {
                for (int k = pieces - 1; k >= 0; k--)
                    AddUnique(prof, new Vector2(rim.x * k / pieces, rim.y), false, spacing);
            }
        }

        private static void AddUnique(List<Vector2> prof, Vector2 p, bool corner, float spacing)
        {
            float eps = spacing * 1e-3f;
            if (prof.Count > 0 && (prof[prof.Count - 1] - p).sqrMagnitude < eps * eps)
            {
                // Keep the corner flag of whichever duplicate had one.
                if (corner) CornerScratch[CornerScratch.Count - 1] = true;
                return;
            }
            prof.Add(p);
            CornerScratch.Add(corner);
        }

        /// Reverses the profile if needed so the revolved surface faces OUT of the solid. The
        /// artist can draw the curve top-down or bottom-up; either way the result is the same.
        ///
        /// Decided by the signed volume of revolution, pi * (closed integral of r^2 dh), not by the
        /// profile's signed area. The two agree for a simple curve, but a curve that dips across
        /// the axis twice gets both dips clamped onto the same thin radius and folds over itself -
        /// its area can then be positive while the solid it sweeps is inside out. Volume is also
        /// what the faces have to agree with, and it needs no closing segment for an open profile:
        /// the axis has r = 0, so it contributes nothing.
        private static void OrientOutward(List<Vector2> prof, List<bool> corners, bool loop)
        {
            double volume = 0.0;
            int n = prof.Count;
            int segs = loop ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                Vector2 a = prof[i], b = prof[(i + 1) % n];
                // Exact integral of r^2 dh along the straight segment a -> b.
                volume += ((double)b.y - a.y) * ((double)a.x * a.x + (double)a.x * b.x + (double)b.x * b.x) / 3.0;
            }
            // A counter-clockwise (r right, h up) boundary encloses positive volume, and on it the
            // right-hand normal is the outward one - see ProfileNormal.
            if (volume >= 0.0) return;
            prof.Reverse();
            corners.Reverse();
        }

        /// Outward normal of the profile at sample i, in (radius, height) space.
        private static Vector2 ProfileNormal(List<Vector2> prof, List<bool> corners, int i, bool loop)
        {
            int n = prof.Count;
            bool hasPrev = loop || i > 0;
            bool hasNext = loop || i < n - 1;
            Vector2 p = prof[i];
            Vector2 dirIn = hasPrev ? (p - prof[(i - 1 + n) % n]).normalized : Vector2.zero;
            Vector2 dirOut = hasNext ? (prof[(i + 1) % n] - p).normalized : Vector2.zero;

            // Smooth samples use the chord through both neighbours' directions; corners the
            // bisector too, which is what an averaged vertex normal would come to.
            Vector2 t = dirIn + dirOut;
            if (t.sqrMagnitude < 1e-12f) t = dirOut.sqrMagnitude > 0f ? dirOut : dirIn;
            t.Normalize();
            // Right-hand normal of a counter-clockwise boundary points outward.
            return new Vector2(t.y, -t.x);
        }

        private static bool CrossesItself(List<Vector2> prof, bool loop)
        {
            int n = prof.Count;
            int segs = loop ? n : n - 1;
            for (int i = 0; i < segs; i++)
            {
                Vector2 a0 = prof[i], a1 = prof[(i + 1) % n];
                float minX = Mathf.Min(a0.x, a1.x), maxX = Mathf.Max(a0.x, a1.x);
                float minY = Mathf.Min(a0.y, a1.y), maxY = Mathf.Max(a0.y, a1.y);
                for (int j = i + 2; j < segs; j++)
                {
                    // Neighbours share an endpoint - including the loop's last and first segments.
                    if (loop && i == 0 && j == segs - 1) continue;
                    Vector2 b0 = prof[j], b1 = prof[(j + 1) % n];
                    if (Mathf.Max(b0.x, b1.x) < minX || Mathf.Min(b0.x, b1.x) > maxX ||
                        Mathf.Max(b0.y, b1.y) < minY || Mathf.Min(b0.y, b1.y) > maxY) continue;
                    if (SegmentsCross(a0, a1, b0, b1)) return true;
                }
            }
            return false;
        }

        private static bool SegmentsCross(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
        {
            float d1 = Cross(q2 - q1, p1 - q1);
            float d2 = Cross(q2 - q1, p2 - q1);
            float d3 = Cross(p2 - p1, q1 - p1);
            float d4 = Cross(p2 - p1, q2 - p1);
            return ((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) &&
                   ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f));
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        // -------------------------------------------------------------------- the rings

        private static int RingVertexCount(float radius, float spacing, int segments, bool even)
        {
            if (!even) return segments;
            int n = Mathf.RoundToInt(2f * Mathf.PI * radius / spacing / 4f) * 4;
            return Mathf.Clamp(n, 4, segments);
        }

        /// Unit circle at `count` evenly spaced angles from +X toward +Z, with count a multiple
        /// of 4. Only the first quadrant is computed with trig; the other three are sign flips of
        /// it, so a vertex and its reflection across x = 0 or z = 0 are exact negations of each
        /// other - bit for bit, not merely within a float's rounding. The quarter points are exact
        /// too (cos 90 is 0, not 6e-8), which puts those vertices exactly ON the mirror planes.
        private static (float[] cos, float[] sin) Ring(int count)
        {
            if (AngleTables.TryGetValue(count, out var table)) return table;

            var cos = new float[count];
            var sin = new float[count];
            int q = count / 4, half = count / 2;
            for (int k = 0; k <= q; k++)
            {
                double angle = 2.0 * System.Math.PI * k / count;
                cos[k] = (float)System.Math.Cos(angle);
                sin[k] = (float)System.Math.Sin(angle);
            }
            cos[0] = 1f; sin[0] = 0f;
            cos[q] = 0f; sin[q] = 1f;
            for (int k = q + 1; k <= half; k++) { cos[k] = -cos[half - k]; sin[k] = sin[half - k]; }
            for (int k = half + 1; k < count; k++) { cos[k] = cos[count - k]; sin[k] = -sin[count - k]; }

            table = (cos, sin);
            AngleTables[count] = table;
            return table;
        }

        /// Triangulates the band between two rings (either of which may be a single pole vertex).
        ///
        /// Rings of different counts are zipped by angle - each step advances whichever ring's
        /// next vertex comes first. Only the first quadrant is zipped; the other three are its
        /// reflections across x = 0 and z = 0 (and the half-turn that is both), which is what makes
        /// the triangulation - not just the vertex positions - exactly mirror-symmetric. A plain
        /// zip all the way round would break ties the same way on both sides of the mirror, which
        /// is the mirror image of the wrong way on one of them.
        private static void StitchRings(int startA, int countA, int startB, int countB, List<int> tris)
        {
            if (countA == 1 && countB == 1) return; // two poles - nothing between them

            if (countA == 1)
            {
                for (int k = 0; k < countB; k++)
                    AddTri(tris, startA, startB + k, startB + (k + 1) % countB);
                return;
            }
            if (countB == 1)
            {
                for (int j = 0; j < countA; j++)
                    AddTri(tris, startA + j, startB, startA + (j + 1) % countA);
                return;
            }

            // First-quadrant zip, recorded as (ring, index) corners: ring 0 = A, 1 = B.
            Stitch.Clear();
            int qa = countA / 4, qb = countB / 4;
            int ja = 0, kb = 0;
            while (ja < qa || kb < qb)
            {
                bool advanceA;
                if (ja == qa) advanceA = false;
                else if (kb == qb) advanceA = true;
                // Compare the next angles (ja+1)/countA and (kb+1)/countB without division.
                else advanceA = (long)(ja + 1) * countB <= (long)(kb + 1) * countA;

                if (advanceA)
                {
                    Stitch.Add(0); Stitch.Add(ja);
                    Stitch.Add(1); Stitch.Add(kb);
                    Stitch.Add(0); Stitch.Add(ja + 1);
                    ja++;
                }
                else
                {
                    Stitch.Add(0); Stitch.Add(ja);
                    Stitch.Add(1); Stitch.Add(kb);
                    Stitch.Add(1); Stitch.Add(kb + 1);
                    kb++;
                }
            }

            // Four images of each first-quadrant triangle. A reflection reverses orientation, so
            // its corners are emitted in reverse to keep the face pointing out.
            for (int t = 0; t < Stitch.Count; t += 6)
            {
                int r0 = Stitch[t], i0 = Stitch[t + 1];
                int r1 = Stitch[t + 2], i1 = Stitch[t + 3];
                int r2 = Stitch[t + 4], i2 = Stitch[t + 5];

                // Identity.
                AddTri(tris, V(r0, i0, 0), V(r1, i1, 0), V(r2, i2, 0));
                // Across x = 0: angle -> 180 - angle.
                AddTri(tris, V(r0, i0, 1), V(r2, i2, 1), V(r1, i1, 1));
                // Across z = 0: angle -> -angle.
                AddTri(tris, V(r0, i0, 2), V(r2, i2, 2), V(r1, i1, 2));
                // Both - a half turn, which keeps orientation.
                AddTri(tris, V(r0, i0, 3), V(r1, i1, 3), V(r2, i2, 3));
            }

            int V(int ring, int index, int image)
            {
                int count = ring == 0 ? countA : countB;
                int start = ring == 0 ? startA : startB;
                int mapped;
                switch (image)
                {
                    case 1: mapped = count / 2 - index; break;
                    case 2: mapped = (count - index) % count; break;
                    case 3: mapped = (index + count / 2) % count; break;
                    default: mapped = index % count; break;
                }
                return start + mapped;
            }
        }

        private static void AddTri(List<int> tris, int a, int b, int c)
        {
            tris.Add(a);
            tris.Add(b);
            tris.Add(c);
        }
    }
}
