using UnityEditor;
using UnityEngine;

namespace Sculpting.EditorTools
{
    /// Clears sculpting objects leaked by EditMode tests before Play mode starts.
    ///
    /// The EditMode fixtures (SymmetryDriftTests, MirrorRepeaterTests, the brush parity tests...)
    /// build their SculptableMesh / SculptController objects with HideAndDontSave and destroy
    /// them in [OneTimeTearDown]. A run cut short - a script recompile mid-run is enough - never
    /// reaches the teardown, and HideAndDontSave objects outlive everything short of quitting the
    /// editor: scene loads, entering and leaving Play mode. In Play they come to life - a leaked
    /// MirrorController builds its plane quads (the magenta planes that turned up around the
    /// origin), and a leaked SculptController runs its whole Update alongside the real one.
    ///
    /// Swept on the way INTO Play mode, because in Edit mode the app itself never creates either
    /// component: any scene-less, non-asset object carrying one at that moment can only be a leak.
    [InitializeOnLoad]
    internal static class LeakedTestFixtureSweeper
    {
        static LeakedTestFixtureSweeper()
        {
            EditorApplication.playModeStateChanged += change =>
            {
                if (change == PlayModeStateChange.ExitingEditMode) Sweep();
            };
        }

        [MenuItem("Window/Sculpting/Clear Leaked Test Objects")]
        private static void Sweep()
        {
            int removed = 0;
            foreach (GameObject go in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (go == null || go.transform.parent != null) continue;
                // Assets and prefabs are persistent; anything in a scene is the user's.
                if (EditorUtility.IsPersistent(go) || go.scene.IsValid()) continue;
                if ((go.hideFlags & HideFlags.DontSave) == 0) continue;
                bool mirrorCopy = go.GetComponent<MirrorRepeaterView>() != null;
                if (!mirrorCopy && go.GetComponentInChildren<SculptableMesh>(true) == null &&
                    go.GetComponentInChildren<SculptController>(true) == null) continue;

                // The fixtures hand their meshes over as sharedMesh, so they leaked too. Not for a
                // live mirror copy: its mesh is its object's, freed with that object.
                if (!mirrorCopy)
                    foreach (MeshFilter filter in go.GetComponentsInChildren<MeshFilter>(true))
                        if (filter.sharedMesh != null && !EditorUtility.IsPersistent(filter.sharedMesh))
                            Object.DestroyImmediate(filter.sharedMesh);

                Object.DestroyImmediate(go);
                removed++;
            }

            if (removed > 0)
                Debug.Log($"[Sculpting] Removed {removed} object(s) left behind by an interrupted EditMode test run.");
        }
    }
}
