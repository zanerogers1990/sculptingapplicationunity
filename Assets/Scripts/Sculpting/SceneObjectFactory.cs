using UnityEngine;

namespace Sculpting
{
    /// The one construction sequence for a live, sculptable scene object: a GameObject with a
    /// MeshFilter + MeshRenderer, a SculptableMesh, a MirrorController, and the shared sculpt
    /// material. Every path that makes one - load, import, primitives, clones, mask extract,
    /// ZSphere convert, Lathe create - goes through here, so none of them can forget a piece (a
    /// SculptableMesh without a MirrorController, or a renderer left on the default material).
    ///
    /// Selection and undo recording stay with the callers: each needs them at a different point
    /// in its own sequence.
    internal static class SceneObjectFactory
    {
        /// Builds a new sculptable object from a bare mesh.
        ///
        /// TAKES OWNERSHIP of `mesh` and destroys it once the object has copied it (see
        /// SculptableMesh.AddOwning) - pass a mesh built for this call, not one you keep using.
        public static SculptableMesh Create(Mesh mesh, string name, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = scale;

            // Order matters: SculptableMesh.Awake() reads meshFilter.sharedMesh and instantiates
            // it, so the mesh has to be in place BEFORE the component is added (AddComponent
            // runs Awake synchronously). Same sequencing PrimitiveSpawner relies on, which gets
            // it for free from GameObject.CreatePrimitive.
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>();

            SculptableMesh sculptable = SculptableMesh.AddOwning(go, mesh);
            AddSculptSiblings(go);
            return sculptable;
        }

        /// Makes an existing GameObject - whose MeshFilter already holds a SHARED mesh asset, as
        /// GameObject.CreatePrimitive hands back - sculptable. The shared mesh is left alone
        /// (SculptableMesh.Awake works on its own copy); destroying it would break every other
        /// user of Unity's built-in primitive.
        public static SculptableMesh MakeSculptableShared(GameObject go)
        {
            SculptableMesh sculptable = go.AddComponent<SculptableMesh>();
            AddSculptSiblings(go);
            return sculptable;
        }

        private static void AddSculptSiblings(GameObject go)
        {
            go.AddComponent<MirrorController>();
            Object.FindFirstObjectByType<SculptMaterialController>()?.ApplyTo(go.GetComponent<Renderer>());
        }
    }
}
