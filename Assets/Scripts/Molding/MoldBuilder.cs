using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace Sculpting.Molding
{
    /// Turns a parting surface plus a list of placed features into two mold halves.
    ///
    /// The addon's build_mold, restructured around what this app's boolean can do that Blender's
    /// cannot. MeshBoolean reads inside/outside from a WINDING NUMBER, not a parity bit, so
    /// concatenating several closed shells into one operand array is already their union (that is
    /// what MeshJoiner relies on - see SignedDistanceField.ComputeInsideMask). That collapses
    /// what was six modifier evaluations per half into exactly ONE field combination per half:
    /// the pegs are concatenated into the lower block instead of being unioned with it, and the
    /// sockets, channels and the model itself are concatenated into a single cutter instead of
    /// being subtracted one at a time.
    ///
    /// Split in two so the expensive part never blocks the app. BuildGeometry is pure arrays in,
    /// arrays out - it samples both booleans and extracts both surfaces - and is safe to run on a
    /// worker thread (the extraction's shared scratch is serialised by MeshRemesher's
    /// ExtractionLock). Finish is the only piece that has to be on the main thread, because it
    /// makes Unity Meshes. Build does both back to back for callers that want to wait.
    ///
    /// The model reaching the boolean is expected to be a PROXY sized to the build's grid (see
    /// MoldCutterProxy) - measured on a 2.9M-triangle lure, that is the difference between a
    /// 5.9s draft and a sub-second one, with the same halves coming out.
    public static class MoldBuilder
    {
        public sealed class Result
        {
            /// The half that fills DOWN from the parting surface - the one the pegs stand on.
            public Mesh LowerHalf;
            /// The half that fills UP from the parting surface, with the sockets cut into it.
            public Mesh UpperHalf;

            public string Error;
            public readonly List<string> Report = new List<string>();
            public float Seconds;
            public int Resolution;

            public bool Success => Error == null && LowerHalf != null && UpperHalf != null;
        }

        /// Everything BuildGeometry produces, before any of it becomes a Unity object.
        internal sealed class Geometry
        {
            public MeshRemesher.RemeshResult Lower;
            public MeshRemesher.RemeshResult Upper;
            public string Error;
            public int Resolution;
            public float Seconds;
            public int Pins, Channels, UnconnectedChannels;
            public float BlockVolume, BuiltVolume, ModelVolume;
            public int CutterTriangles;
        }

        /// Builds both halves at `resolution` voxels along the block's longest axis, and waits.
        ///
        /// `lureWorldVertices`/`lureTriangles` are the model in WORLD space - the same space the
        /// field and the block are in, so nothing here has to know about transforms. `columns`
        /// lets channels find where they break into the cavity (see MoldGeometry.LayoutChannel);
        /// null runs every channel from its anchor exactly as placed.
        public static Result Build(Vector3[] lureWorldVertices, int[] lureTriangles, int lureVertexCount,
                                   PartingField field, in MoldBlock block, PullColumnMap columns,
                                   IReadOnlyList<MoldFeature> features, MoldSettings settings, int resolution)
        {
            Vector3[] verts = lureWorldVertices != null ? Exact(lureWorldVertices, lureVertexCount) : null;
            Geometry g = BuildGeometry(verts, lureTriangles, field, block, columns, features, settings, resolution);
            return Finish(g);
        }

        /// The worker-safe part: both booleans, sampled and extracted, as plain arrays.
        ///
        /// `cutterVerts`/`cutterTris` are the model (or its proxy) in world space, exactly sized.
        /// Nothing passed in is written to, and nothing here touches a Unity object, so every
        /// input just has to stay unchanged until this returns - the controller hands it copies.
        internal static Geometry BuildGeometry(Vector3[] cutterVerts, int[] cutterTris,
                                               PartingField field, in MoldBlock block, PullColumnMap columns,
                                               IReadOnlyList<MoldFeature> features, MoldSettings settings, int resolution)
        {
            var g = new Geometry { Resolution = resolution };
            var clock = Stopwatch.StartNew();

            if (field == null)
            {
                g.Error = "there is no parting surface yet";
                return g;
            }
            if (cutterVerts == null || cutterVerts.Length == 0 || cutterTris == null || cutterTris.Length < 3)
            {
                g.Error = "the model has no geometry";
                return g;
            }
            g.CutterTriangles = cutterTris.Length / 3;

            // ---------------------------------------------------------------- the two blocks
            field.BuildHalfSolid(block.UBottom, out Vector3[] lowerVerts, out int[] lowerTris);
            field.BuildHalfSolid(block.UTop, out Vector3[] upperVerts, out int[] upperTris);

            // ------------------------------------------------------------------- the features
            var pegVerts = new List<Vector3>();
            var pegTris = new List<int>();
            // Sockets are kept SEPARATE from the channels, not merged into one cutter.
            //
            // A socket is deliberately a size larger than the peg it mates with and covers the
            // same span of the parting surface, so subtracting it from the LOWER half removes
            // exactly the peg that half was supposed to grow. Merging them cost nothing visible -
            // the halves still built, the sockets still appeared upstairs - and silently produced
            // a mold with no registration pins at all.
            var socketVerts = new List<Vector3>();
            var socketTris = new List<int>();
            var channelVerts = new List<Vector3>();
            var channelTris = new List<int>();

            if (features != null)
            {
                // Mirrors are expanded here rather than stored on the controller, so the build
                // and the viewport preview iterate the same list and cannot disagree about what
                // is in the mold. See MoldGeometry.ExpandMirrors.
                foreach (MoldFeature f in MoldGeometry.ExpandAll(features, block, settings))
                {
                    if (f == null || !f.Enabled) continue;

                    if (f.Kind == MoldFeatureKind.Pin)
                    {
                        if (!settings.Pins) continue;
                        MoldGeometry.BuildPin(f, field, settings, pegVerts, pegTris, socketVerts, socketTris);
                        g.Pins++;
                    }
                    else
                    {
                        if (f.Kind == MoldFeatureKind.Sprue && !settings.Sprue) continue;
                        if (!MoldGeometry.LayoutChannel(f, field, block, columns, settings).Connected && columns != null)
                            g.UnconnectedChannels++;
                        MoldGeometry.BuildChannel(f, field, block, columns, settings, channelVerts, channelTris);
                        g.Channels++;
                    }
                }
            }

            // ------------------------------------------------------------------- the booleans
            // Lower half's target is the block WITH the pegs concatenated on: one winding-number
            // field already treats overlapping shells as a union, so this is the union, for free.
            Vector3[] targetLowerV = lowerVerts;
            int[] targetLowerT = lowerTris;
            if (pegVerts.Count > 0)
                Concat(lowerVerts, lowerTris, pegVerts, pegTris, out targetLowerV, out targetLowerT);

            // The model plus the channels, as ONE cutter for the lower half. Their overlaps (a
            // channel deliberately reaches inside the model) are handled by the same winding sum,
            // so concatenating them is their union.
            Vector3[] lowerCutterV = cutterVerts;
            int[] lowerCutterT = cutterTris;
            if (channelVerts.Count > 0)
                Concat(cutterVerts, cutterTris, channelVerts, channelTris, out lowerCutterV, out lowerCutterT);

            // The upper half gets the same thing plus the sockets.
            Vector3[] upperCutterV = lowerCutterV;
            int[] upperCutterT = lowerCutterT;
            if (socketVerts.Count > 0)
                Concat(lowerCutterV, lowerCutterT, socketVerts, socketTris, out upperCutterV, out upperCutterT);

            if (!Half(targetLowerV, targetLowerT, lowerCutterV, lowerCutterT, "cavity + channels", resolution,
                      out g.Lower, out string lowerError))
            {
                g.Error = "lower half - " + lowerError;
                return g;
            }
            if (!Half(upperVerts, upperTris, upperCutterV, upperCutterT, "cavity + channels + sockets", resolution,
                      out g.Upper, out string upperError))
            {
                g.Error = "upper half - " + upperError;
                return g;
            }

            clock.Stop();
            g.Seconds = (float)clock.Elapsed.TotalSeconds;

            // ------------------------------------------------------------ the report's numbers
            g.BlockVolume = Mathf.Abs(SignedVolume(lowerVerts, lowerTris)) + Mathf.Abs(SignedVolume(upperVerts, upperTris));
            g.BuiltVolume = Mathf.Abs(SignedVolume(g.Lower.Vertices, g.Lower.Triangles)) +
                            Mathf.Abs(SignedVolume(g.Upper.Vertices, g.Upper.Triangles));
            g.ModelVolume = Mathf.Abs(SignedVolume(cutterVerts, cutterTris));
            return g;
        }

        /// One half: sample the boolean, then extract it.
        private static bool Half(Vector3[] targetV, int[] targetT, Vector3[] cutterV, int[] cutterT, string cutterName,
                                 int resolution, out MeshRemesher.RemeshResult result, out string error)
        {
            result = default;
            var cutter = new List<MeshBoolean.Operand>(1) { new MeshBoolean.Operand(cutterV, cutterT, cutterName) };
            if (!MeshBoolean.TrySample(targetV, targetT, cutter, BooleanOp.Subtract, resolution,
                                       out MeshBoolean.SampledField field, out error))
                return false;

            result = MeshRemesher.BuildFromSdfGeometry(field.Sdf, field.Dims, field.Origin, field.CellSize);
            if (result.IsEmpty)
            {
                error = MeshBoolean.EmptyResultError(BooleanOp.Subtract);
                return false;
            }
            return true;
        }

        /// The main-thread part: turns the arrays into Meshes and writes the report.
        internal static Result Finish(Geometry g)
        {
            var result = new Result { Resolution = g.Resolution, Seconds = g.Seconds };
            if (g.Error != null)
            {
                result.Error = g.Error;
                return result;
            }

            Mesh lower = MeshRemesher.BuildMesh(g.Lower);
            Mesh upper = MeshRemesher.BuildMesh(g.Upper);
            lower.name = "Mold Lower";
            upper.name = "Mold Upper";
            result.LowerHalf = lower;
            result.UpperHalf = upper;

            float removed = g.BlockVolume - g.BuiltVolume;
            float pct = g.ModelVolume > 1e-9f ? 100f * removed / g.ModelVolume : 0f;

            result.Report.Add($"Built in {g.Seconds:0.0}s at resolution {g.Resolution}");
            result.Report.Add($"Lower {g.Lower.Triangles.Length / 3:n0} tris, upper {g.Upper.Triangles.Length / 3:n0} tris");
            if (g.Pins > 0) result.Report.Add($"{g.Pins} registration pin{(g.Pins == 1 ? "" : "s")}");
            if (g.Channels > 0) result.Report.Add($"{g.Channels} channel{(g.Channels == 1 ? "" : "s")} (sprue + vents)");
            if (g.UnconnectedChannels > 0)
                result.Report.Add($"{g.UnconnectedChannels} channel{(g.UnconnectedChannels == 1 ? " does" : "s do")} not reach the cavity - move {(g.UnconnectedChannels == 1 ? "it" : "them")} onto the model");
            result.Report.Add($"Cavity volume {pct:0.0}% of the model (expect 95-105%)");
            if (pct < 85f)
                result.Report.Add("Low cavity volume - raise the resolution, or check the model is watertight");
            else if (pct > 130f)
                result.Report.Add("High cavity volume - the channels may be eating into the block");

            return result;
        }

        private static Vector3[] Exact(Vector3[] src, int count)
        {
            if (src.Length == count) return src;
            var outv = new Vector3[count];
            System.Array.Copy(src, outv, count);
            return outv;
        }

        /// Appends one shell set onto another, offsetting the second's indices. The result is
        /// their UNION as far as the voxel boolean is concerned - see the class remarks.
        private static void Concat(Vector3[] aV, int[] aT, List<Vector3> bV, List<int> bT,
                                   out Vector3[] outV, out int[] outT)
        {
            outV = new Vector3[aV.Length + bV.Count];
            System.Array.Copy(aV, outV, aV.Length);
            for (int i = 0; i < bV.Count; i++) outV[aV.Length + i] = bV[i];

            outT = new int[aT.Length + bT.Count];
            System.Array.Copy(aT, outT, aT.Length);
            for (int i = 0; i < bT.Count; i++) outT[aT.Length + i] = bT[i] + aV.Length;
        }

        /// Signed volume via the divergence theorem. Only ever used as a ratio and always taken
        /// as an absolute value, so the winding convention does not matter here - what matters
        /// is that the same formula is applied to the block and to the result.
        public static float SignedVolume(Vector3[] verts, int[] tris)
        {
            double v = 0.0;
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                Vector3 a = verts[tris[t]], b = verts[tris[t + 1]], c = verts[tris[t + 2]];
                v += Vector3.Dot(a, Vector3.Cross(b, c));
            }
            return (float)(v / 6.0);
        }
    }
}
