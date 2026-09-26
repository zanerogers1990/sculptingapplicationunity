using System.Globalization;
using System.IO;
using UnityEngine;

namespace Sculpting
{
    /// Wavefront .obj text for a PolyMesh, with faces kept as polygons rather than triangulated.
    ///
    /// Positions are written as given (callers bake transforms first). With `toRightHanded` the
    /// X axis is negated and every face's winding reversed, the same conversion ObjExporter uses,
    /// so the file opens un-mirrored and outward-facing in Blender, Maya or ZBrush.
    ///
    /// Takes a TextWriter rather than a path: opening files is the IO layer's job, not this
    /// assembly's.
    internal static class PolyMeshObj
    {
        public static void Write(TextWriter writer, PolyMesh mesh, string objectName, bool toRightHanded)
        {
            var ci = CultureInfo.InvariantCulture;
            writer.Write("o ");
            writer.Write(string.IsNullOrEmpty(objectName) ? "PolyMesh" : objectName);
            writer.Write('\n');

            float sx = toRightHanded ? -1f : 1f;
            foreach (Vector3 v in mesh.Vertices)
            {
                writer.Write("v ");
                writer.Write((sx * v.x).ToString("F6", ci));
                writer.Write(' ');
                writer.Write(v.y.ToString("F6", ci));
                writer.Write(' ');
                writer.Write(v.z.ToString("F6", ci));
                writer.Write('\n');
            }

            int[] start = mesh.FaceStart, idx = mesh.FaceIndices;
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                int s = start[f], e = start[f + 1];
                if (e - s < 3) continue;
                writer.Write('f');
                if (toRightHanded)
                    for (int c = e - 1; c >= s; c--) { writer.Write(' '); writer.Write(idx[c] + 1); }
                else
                    for (int c = s; c < e; c++) { writer.Write(' '); writer.Write(idx[c] + 1); }
                writer.Write('\n');
            }
        }
    }
}
