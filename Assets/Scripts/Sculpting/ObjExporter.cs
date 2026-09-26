using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sculpting
{
    /// Writes a SculptableMesh's current geometry out as a plain-text Wavefront .obj file -
    /// the simplest broadly-compatible interchange format (Blender/ZBrush/Maya all read it
    /// natively) and needs no extra Unity package, unlike FBX. Bakes the mesh's world
    /// transform into the exported vertices/normals so the file matches what's on screen
    /// rather than the local-space sculpt data. Negates X (with a correspondingly reversed
    /// face winding) to correct for Unity's left-handed coordinate system - otherwise the
    /// geometry reads mirrored/backwards-facing once opened in a right-handed DCC app.
    public static class ObjExporter
    {
        // Writes to exactly `fullPath` (e.g. one picked in a save dialog), creating its folder
        // if needed. Returns the path written, or null if there's no mesh yet to export.
        public static string ExportToFile(SculptableMesh sculptableMesh, string fullPath)
        {
            Mesh mesh = sculptableMesh.Mesh;
            if (mesh == null) return null;

            Transform t = sculptableMesh.transform;
            Vector3[] verts = sculptableMesh.VerticesExact();
            Vector3[] normals = sculptableMesh.NormalsExact();
            // TrianglesExact, not mesh.triangles: while anything is hidden the Mesh's index
            // buffer holds only the visible triangles, and exporting from it silently dropped
            // the hidden part of the model.
            int[] triangles = sculptableMesh.TrianglesExact();

            // After a quad remesh the faces are written as the quads themselves (the triangles
            // are those quads split in two); otherwise as triangles.
            int[] faceStart = sculptableMesh.QuadFaceStart;
            int[] faceIndices = sculptableMesh.QuadFaceIndices;
            if (faceStart == null)
            {
                faceStart = new int[triangles.Length / 3 + 1];
                for (int f = 0; f < faceStart.Length; f++) faceStart[f] = f * 3;
                faceIndices = triangles;
            }

            var sb = new StringBuilder();
            sb.Append("# Exported from Sculpting Application\n");
            sb.Append("o SculptMesh\n");

            for (int i = 0; i < verts.Length; i++)
            {
                Vector3 world = t.TransformPoint(verts[i]);
                AppendVector(sb, "v", -world.x, world.y, world.z);
            }

            for (int i = 0; i < normals.Length; i++)
            {
                // Not t.TransformDirection: that is rotation-only, so on a non-uniformly
                // scaled object it exports normals that are no longer perpendicular to the
                // exported (baked, and therefore stretched) faces - see
                // SculptableMesh.LocalToWorldNormal for the inverse-transpose this needs.
                Vector3 n = sculptableMesh.LocalToWorldNormal(normals[i]);
                AppendVector(sb, "vn", -n.x, n.y, n.z);
            }

            // 1-based indices, reversed winding order (c, b, a instead of a, b, c) to match
            // the X flip above - flipping one axis inverts face orientation, so the winding
            // has to flip back too or every face reads backwards/inside-out.
            for (int f = 0; f + 1 < faceStart.Length; f++)
            {
                sb.Append('f');
                for (int c = faceStart[f + 1] - 1; c >= faceStart[f]; c--)
                {
                    int v = faceIndices[c] + 1;
                    sb.Append(' ').Append(v).Append("//").Append(v);
                }
                sb.Append('\n');
            }

            string folderPath = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(folderPath)) Directory.CreateDirectory(folderPath);
            File.WriteAllText(fullPath, sb.ToString());
            return fullPath;
        }

        private static void AppendVector(StringBuilder sb, string prefix, float x, float y, float z)
        {
            sb.Append(prefix).Append(' ')
              .Append(x.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
              .Append(y.ToString("F6", CultureInfo.InvariantCulture)).Append(' ')
              .Append(z.ToString("F6", CultureInfo.InvariantCulture)).Append('\n');
        }
    }
}
