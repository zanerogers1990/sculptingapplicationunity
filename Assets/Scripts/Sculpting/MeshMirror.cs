using UnityEngine;

namespace Sculpting
{
    /// Duplicate-and-reflect, used by SceneGraphUIBuilder's two Mirror buttons, always reflecting
    /// across PrimitiveSpawner.MainObject's world position (the scene's anchor sphere). Two flavours
    /// of the same copy:
    /// - SEPARATE: independent from the moment it exists, like Blender's Duplicate + Mirror.
    /// - LINKED: the copy and the original keep following each other, mirrored, until the user
    ///   finalizes them - like Nomad's mirror or Blender's Mirror modifier. See MirrorLink.
    ///
    /// The copy is an exact mirror in LOCAL space: each vertex and normal is reflected through the
    /// object's own origin, and the transform is reflected to match (position across the centre,
    /// rotation conjugated by the reflection, scale unchanged). It used to bake the source's
    /// WORLD-space shape into an identity transform and then call RecalculateNormals, which is what
    /// made a mirrored sphere look lower-poly than the original with the very same 515 verts and 768
    /// tris. Unity's sphere duplicates its vertices along the UV seam and at the poles, and
    /// RecalculateNormals averages each duplicate over only its own triangles, so every seam came
    /// out as a hard crease - measured 108 seam positions up to 17.5 degrees apart on the copy,
    /// against none on the original. Carrying the source's own normals keeps the shading identical,
    /// and keeping the transform intact is what lets a linked copy follow a drag with a transform
    /// write instead of a rebake.
    public static class MeshMirror
    {
        /// Returns the new copy, or null when there is nothing to do: no source, no axis checked,
        /// or a LINKED copy asked of an object already in a pair (see MirrorLink.Create).
        public static SculptableMesh MirrorAcross(SculptableMesh source, Vector3 centerWorld,
                                                  bool axisX, bool axisY, bool axisZ, bool linked = false)
        {
            if (source == null || (!axisX && !axisY && !axisZ)) return null;
            if (linked && source.LinkedMirror != null) return null;

            Vector3 signs = AxisSigns(axisX, axisY, axisZ);
            Transform srcT = source.transform;
            SculptableMesh mirrored = MeshCloner.Duplicate(source, source.name + " Mirror", signs,
                ReflectPoint(srcT.position, centerWorld, signs), ReflectRotation(srcT.rotation, signs));

            if (linked) MirrorLink.Create(source, mirrored, centerWorld, signs);
            return mirrored;
        }

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
