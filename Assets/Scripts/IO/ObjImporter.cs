using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Sculpting.IO
{
    /// Reads a Wavefront .obj into a Mesh ready to be sculpted - the counterpart to
    /// ObjExporter, and the format every DCC app (Blender/ZBrush/Maya) writes natively without
    /// needing an extra Unity package. FBX deliberately isn't supported: it's a closed binary
    /// format that would mean taking on the Autodesk SDK or a third-party dependency, whereas
    /// OBJ is a few hundred lines of text parsing.
    ///
    /// Mirrors ObjExporter's conventions exactly so a round trip is the identity: X is negated
    /// and face winding reversed, undoing the same flip the exporter applies for Unity's
    /// left-handed coordinate system.
    ///
    /// Deliberately indexes faces by POSITION only, ignoring each face-vertex's texture and
    /// normal indices. That yields a WELDED mesh - one vertex per 'v' line, shared by every
    /// face touching it - which is what sculpting needs: SculptableMesh's adjacency, smoothing
    /// and remeshing all assume neighbouring triangles share vertices, and a split-by-normal
    /// mesh (what you'd get by honouring v/vt/vn triples) would tear apart along every hard
    /// edge the moment it was smoothed. Normals are recalculated from the welded geometry for
    /// the same reason, so any 'vn' lines in the file are read past rather than used.
    public static class ObjImporter
    {
        /// Returns null and sets `error` on failure. `mesh` is left unattached to any
        /// GameObject - the caller decides what to do with it.
        public static Mesh Import(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) { error = "No file at " + path; return null; }

                var positions = new List<Vector3>();
                var triangles = new List<int>();
                // Reused per face so an n-gon doesn't allocate a list per line.
                var faceIndices = new List<int>(8);

                using (var reader = new StreamReader(path))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        // Fast reject before any splitting - the overwhelming majority of lines
                        // in a big OBJ are 'v'/'vn'/'vt', and vn/vt are skipped entirely.
                        if (line.Length < 2) continue;
                        char c0 = line[0];
                        if (c0 == '#') continue;

                        if (c0 == 'v' && line[1] == ' ')
                        {
                            if (TryParseVertex(line, out Vector3 v)) positions.Add(v);
                        }
                        else if (c0 == 'f' && (line[1] == ' ' || line[1] == '\t'))
                        {
                            ParseFace(line, positions.Count, faceIndices);

                            // Fan-triangulate: OBJ faces are legally any n-gon, and quads are
                            // extremely common from DCC exports. A fan is correct for convex
                            // faces, which is what quads and near-planar n-gons are in practice.
                            for (int i = 1; i + 1 < faceIndices.Count; i++)
                            {
                                // Reversed (c, b, a) for the same reason ObjExporter reverses on
                                // the way out - negating X below inverts triangle orientation,
                                // so the winding has to flip back or every face is inside-out.
                                triangles.Add(faceIndices[i + 1]);
                                triangles.Add(faceIndices[i]);
                                triangles.Add(faceIndices[0]);
                            }
                        }
                    }
                }

                if (positions.Count == 0) { error = "No vertices found - is this really an .obj?"; return null; }
                if (triangles.Count == 0) { error = "No faces found in the file."; return null; }

                var verts = new Vector3[positions.Count];
                for (int i = 0; i < positions.Count; i++)
                {
                    Vector3 p = positions[i];
                    verts[i] = new Vector3(-p.x, p.y, p.z); // undo ObjExporter's X flip
                }

                var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(path) };
                // Required above 65535 vertices; scanned models routinely exceed that.
                if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                mesh.vertices = verts;
                mesh.SetTriangles(triangles, 0);
                mesh.RecalculateNormals();
                mesh.RecalculateBounds();
                return mesh;
            }
            catch (Exception e)
            {
                error = e.Message;
                return null;
            }
        }

        private static bool TryParseVertex(string line, out Vector3 v)
        {
            v = default;
            // Split on whitespace, dropping the runs of blanks some exporters emit for column
            // alignment. 'v' lines may carry a 4th (w) component and a vertex colour after
            // that; both are ignored, so only the first three numbers are read.
            string[] parts = line.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) return false;
            return TryFloat(parts[1], out v.x) && TryFloat(parts[2], out v.y) && TryFloat(parts[3], out v.z);
        }

        private static void ParseFace(string line, int vertexCount, List<int> into)
        {
            into.Clear();
            string[] parts = line.Split(SplitChars, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 1; i < parts.Length; i++)
            {
                string token = parts[i];

                // Face vertices are "v", "v/vt", "v//vn" or "v/vt/vn" - only the leading
                // position index is used (see the welding remarks on the class).
                int slash = token.IndexOf('/');
                if (slash >= 0) token = token.Substring(0, slash);
                if (token.Length == 0) continue;
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx)) continue;

                // OBJ indices are 1-based, and may be NEGATIVE to mean "counting back from the
                // most recently defined vertex" (-1 is the last one). Blender writes these.
                if (idx < 0) idx = vertexCount + idx;
                else idx -= 1;

                // A face referencing a vertex that doesn't exist would throw deep inside
                // Mesh.SetTriangles with a far less useful message; drop it here instead.
                if (idx >= 0 && idx < vertexCount) into.Add(idx);
            }
        }

        private static bool TryFloat(string s, out float f) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f);

        private static readonly char[] SplitChars = { ' ', '\t' };
    }
}
