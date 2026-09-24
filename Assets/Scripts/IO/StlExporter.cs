using System.IO;
using UnityEngine;

namespace Sculpting
{
    /// Writes a mesh out as a BINARY .stl, at a real-world scale, for a slicer.
    ///
    /// Binary rather than ASCII: a mold half is millions of triangles at final resolution, and
    /// ASCII STL spends about 250 bytes on each of them where binary spends 50. A 3M-triangle
    /// half is 150MB binary and would be three quarters of a gigabyte as text.
    ///
    /// The axis map is exactly StlImporter.ToUnity inverted, and has to stay that way: that is
    /// what makes exporting a model and importing it back give the same object, and what makes
    /// a half exported here line up with the same lure imported from the user's Blender addon.
    /// See StlImporter for why Z-up is the convention being converted to.
    public static class StlExporter
    {
        /// Unity's left-handed Y-up back to STL's right-handed Z-up: (x, y, z) -> (-x, -z, y).
        ///
        /// The inverse of StlImporter.ToUnity, which is (x, y, z) -> (-x, z, -y). Substituting
        /// confirms it: ToUnity(-ux, -uz, uy) = (ux, uy, uz). Determinant -1, a handedness flip,
        /// so every triangle's winding has to be reversed on the way out or the whole surface
        /// reads inside-out to a slicer - which prints as a hollow shell or as nothing at all.
        private static Vector3 ToStl(Vector3 u) => new Vector3(-u.x, -u.z, u.y);

        /// Writes `mesh` to `filePath`. `millimetresPerUnit` scales world units to millimetres,
        /// which is what slicers read an STL as. Returns the path written, or null.
        public static string Export(SculptableMesh mesh, string filePath, float millimetresPerUnit)
        {
            if (mesh == null || mesh.Mesh == null) return null;

            Vector3[] local = mesh.VerticesExact();
            int[] tris = mesh.TrianglesExact();
            if (local == null) return null;

            // World space, so what is exported is what is on screen - any real transform on the
            // object belongs in the file. (The mold's Exploded offset does not: MoldController
            // parks the halves at the origin across the write.)
            Transform t = mesh.transform;
            var world = new Vector3[local.Length];
            for (int i = 0; i < local.Length; i++) world[i] = t.TransformPoint(local[i]);

            return Export(world, tris, filePath, millimetresPerUnit);
        }

        /// The same write from plain WORLD-space arrays.
        ///
        /// This is where the geometry actually happens, and it takes arrays so it can be tested
        /// without a scene: SculptableMesh cannot initialise outside Play mode (AddOwning
        /// destroys the mesh it replaces, which edit mode refuses, leaving the component blank),
        /// so a test that went through the overload above would be testing nothing but that.
        /// Same reasoning as MeshBoolean and MeshRemesher taking arrays.
        public static string Export(Vector3[] worldVertices, int[] triangles, string filePath,
                                    float millimetresPerUnit)
        {
            if (worldVertices == null || triangles == null || triangles.Length < 3) return null;
            if (string.IsNullOrEmpty(filePath)) return null;

            Vector3[] local = worldVertices;
            int[] tris = triangles;
            float scale = Mathf.Max(millimetresPerUnit, 1e-6f);

            var points = new Vector3[local.Length];
            for (int i = 0; i < local.Length; i++)
                points[i] = ToStl(local[i]) * scale;

            int triangleCount = tris.Length / 3;

            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            using (var w = new BinaryWriter(stream))
            {
                // 80-byte header. Must NOT begin with "solid", or readers that sniff the format
                // by that prefix will try to parse this binary file as ASCII.
                var header = new byte[80];
                byte[] tag = System.Text.Encoding.ASCII.GetBytes("Binary STL - Sculpting Application mold export");
                System.Array.Copy(tag, header, Mathf.Min(tag.Length, header.Length));
                w.Write(header);
                w.Write((uint)triangleCount);

                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 a = points[tris[i]];
                    // Reversed: (a, c, b). The axis flip above inverts orientation, so restoring
                    // it here keeps outward faces outward.
                    Vector3 b = points[tris[i + 2]];
                    Vector3 c = points[tris[i + 1]];

                    // Recomputed rather than carried over from the mesh: the stored normals are
                    // in Unity space and would need the inverse-transpose plus the same flip,
                    // and a normal that disagrees with its winding is exactly what makes a
                    // slicer report a non-manifold surface. The cross product of the emitted
                    // corners cannot disagree with them.
                    Vector3 n = VectorMath.NormalizeOr(Vector3.Cross(b - a, c - a), Vector3.up);

                    w.Write(n.x); w.Write(n.y); w.Write(n.z);
                    w.Write(a.x); w.Write(a.y); w.Write(a.z);
                    w.Write(b.x); w.Write(b.y); w.Write(b.z);
                    w.Write(c.x); w.Write(c.y); w.Write(c.z);
                    w.Write((ushort)0);   // attribute byte count, unused
                }
            }

            return filePath;
        }
    }
}
