using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Spawns a new, independently-sculptable primitive object into the scene, positioned at
    /// the scene's main object (see MainObject) so it's ready to be moved into place via
    /// TransformGizmo. Found via FindFirstObjectByType by SceneGraphUIBuilder's "Add Primitive"
    /// buttons.
    public class PrimitiveSpawner : MonoBehaviour
    {
        // Spawned primitives default to this fraction of the main object's average bounds
        // extent, so they arrive at a usable size relative to the rest of the scene instead of
        // Unity's primitive default (1 world unit), which could dwarf or vanish next to it.
        // Close to 1 (not exactly 1) so a fresh primitive reads as "on par with" the main
        // object's own size rather than a small satellite next to it, while still being
        // distinguishable from an exact duplicate.
        private const float SpawnSizeFraction = 0.9f;
        private const float FallbackSize = 0.3f; // used only if there's no main object yet

        private SelectionManager _selection;
        private SelectionManager Selection => _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());

        /// The scene's anchor object - the first-registered SculptableMesh (today's
        /// SculptSphere) - used as the spawn point for new primitives and as the reflection
        /// center for Mirror (see MeshMirror).
        public SculptableMesh MainObject => Selection != null && Selection.AllObjects.Count > 0 ? Selection.AllObjects[0] : null;

        private readonly Dictionary<PrimitiveShapeType, int> _spawnCounts = new Dictionary<PrimitiveShapeType, int>();

        public SculptableMesh SpawnPrimitive(PrimitiveShapeType type)
        {
            PrimitiveType unityType;
            switch (type)
            {
                case PrimitiveShapeType.Cube: unityType = PrimitiveType.Cube; break;
                case PrimitiveShapeType.Sphere: unityType = PrimitiveType.Sphere; break;
                case PrimitiveShapeType.Cylinder: unityType = PrimitiveType.Cylinder; break;
                default: unityType = PrimitiveType.Capsule; break;
            }

            GameObject go = GameObject.CreatePrimitive(unityType);

            // A stray default collider shadows the MeshCollider SculptableMesh.Awake() adds
            // itself - same idiom MirrorController.CreatePlane already uses for its own quads.
            Collider col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            SculptableMesh main = MainObject;
            Vector3 spawnPos = main != null ? main.transform.position : Vector3.zero;
            float size = FallbackSize;
            if (main != null && main.Mesh != null)
            {
                // Mesh.bounds is in the mesh's own LOCAL space - scale by the object's actual
                // lossyScale to get its real on-screen extent, so this still matches if the main
                // object has itself been resized via the Scale tool.
                Vector3 e = Vector3.Scale(main.Mesh.bounds.extents, main.transform.lossyScale);
                float targetExtent = ((e.x + e.y + e.z) / 3f) * SpawnSizeFraction;

                // Unity's built-in primitives (Cube/Sphere/Cylinder/Capsule) all have an
                // intrinsic extent of 0.5 in their own local mesh space, so localScale has to be
                // DOUBLE the target world extent to actually reach it - using targetExtent
                // directly as localScale (the previous version of this code) silently landed
                // every spawned primitive at HALF the size the comment above describes, on top
                // of SpawnSizeFraction already being conservative. Together that's why spawned
                // shapes read as noticeably smaller than the starting sphere.
                size = targetExtent * 2f;
            }

            go.name = NextName(type);
            go.transform.position = spawnPos;
            go.transform.localScale = Vector3.one * size;

            SculptableMesh sculptable = go.AddComponent<SculptableMesh>();
            go.AddComponent<MirrorController>();

            SculptMaterialController materialController = FindFirstObjectByType<SculptMaterialController>();
            materialController?.ApplyTo(go.GetComponent<Renderer>());

            Selection?.Select(sculptable, false);
            return sculptable;
        }

        private string NextName(PrimitiveShapeType type)
        {
            int count = _spawnCounts.TryGetValue(type, out int c) ? c : 0;
            _spawnCounts[type] = count + 1;
            return count == 0 ? type.ToString() : $"{type} ({count})";
        }
    }
}
