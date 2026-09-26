using UnityEngine;

namespace Sculpting
{
    /// The reflection maths shared by MirrorRepeater's live copies and by baking them into real
    /// objects (MirrorRepeater.Bake -> MeshCloner.Duplicate).
    ///
    /// A baked copy is an exact mirror in LOCAL space: each vertex and normal is reflected through
    /// the object's own origin, and the transform is reflected to match (position across the
    /// centre, rotation conjugated by the reflection, scale unchanged). Baking the WORLD-space
    /// shape into an identity transform and calling RecalculateNormals instead made a mirrored
    /// sphere look lower-poly than the original with the very same 515 verts and 768 tris: Unity's
    /// sphere duplicates its vertices along the UV seam and at the poles, and RecalculateNormals
    /// averages each duplicate over only its own triangles, so every seam came out as a hard
    /// crease. Carrying the source's own normals keeps the shading identical.
    public static class MeshMirror
    {
        /// -1 on each mirrored axis and +1 on the rest. Scaling a local point or normal by this
        /// reflects it through the origin on exactly those axes.
        public static Vector3 AxisSigns(bool axisX, bool axisY, bool axisZ) =>
            new Vector3(axisX ? -1f : 1f, axisY ? -1f : 1f, axisZ ? -1f : 1f);

        public static Vector3 ReflectPoint(Vector3 point, Vector3 center, Vector3 signs) =>
            center + Vector3.Scale(point - center, signs);

        /// The rotation that makes a copy whose LOCAL geometry is reflected by `signs` render as the
        /// world-space reflection of the original. With F the reflection, that rotation is F·R·F:
        /// conjugating by F carries the rotation axis u to F·u, and reverses the turn when F is
        /// improper (an odd number of axes), which together fold into the quaternion as det(F)·F·xyz.
        /// The result is always a proper rotation, so no negative scale ever reaches a Transform, and
        /// scale needs no change at all - a diagonal scale commutes with F.
        public static Quaternion ReflectRotation(Quaternion rotation, Vector3 signs)
        {
            float det = signs.x * signs.y * signs.z;
            return new Quaternion(det * signs.x * rotation.x, det * signs.y * rotation.y,
                                  det * signs.z * rotation.z, rotation.w);
        }

        /// Reflects a mesh's local arrays by `signs` into fresh ones, reversing each triangle's
        /// winding when the reflection is improper so faces still point outward. The swap is of the
        /// LAST two corners, keeping each triangle's first corner in place: SculptableMesh's normal
        /// pass takes a face's edges from that corner, so both halves then sum exactly negated terms
        /// and their normals stay exact mirror images through every later stroke. `mirroredNormals`
        /// is null when `normals` doesn't match the vertex count, for the caller to recalculate.
        public static void ReflectGeometry(Vector3[] vertices, Vector3[] normals, int[] triangles, Vector3 signs,
                                           out Vector3[] mirroredVertices, out Vector3[] mirroredNormals,
                                           out int[] mirroredTriangles)
        {
            mirroredVertices = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) mirroredVertices[i] = Vector3.Scale(vertices[i], signs);

            mirroredNormals = null;
            if (normals != null && normals.Length == vertices.Length)
            {
                mirroredNormals = new Vector3[normals.Length];
                for (int i = 0; i < normals.Length; i++) mirroredNormals[i] = Vector3.Scale(normals[i], signs);
            }

            mirroredTriangles = (int[])triangles.Clone();
            if (signs.x * signs.y * signs.z > 0f) return;
            for (int t = 0; t + 2 < mirroredTriangles.Length; t += 3)
            {
                int tmp = mirroredTriangles[t + 1];
                mirroredTriangles[t + 1] = mirroredTriangles[t + 2];
                mirroredTriangles[t + 2] = tmp;
            }
        }
    }
}
