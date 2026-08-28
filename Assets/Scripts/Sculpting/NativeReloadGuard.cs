#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Sculpting
{
    /// Releases the native/GPU allocations SculptableMesh and SculptController own immediately
    /// before the editor reloads assemblies.
    ///
    /// Recompiling a script WHILE Play mode is running triggers a domain reload, and a domain
    /// reload wipes every managed field without ever calling OnDestroy. So the GraphicsBuffer
    /// SculptableMesh's GPU scatter holds (SculptableMesh.Awake -> BindGpuScatter ->
    /// GpuVertexScatter.BindMesh) and any Allocator.Persistent job scratch alive at that moment
    /// are orphaned, with no managed reference left that could dispose them - Unity reports
    /// exactly this as "Leak Detected : Persistent allocates N individual allocations" on the
    /// next reload. Editor-only (a build never reloads assemblies) and bounded per occurrence,
    /// but it repeats on every recompile-while-playing and the memory is not reclaimed until the
    /// editor restarts, so a long session of iterating on scripts mid-play accumulates orphaned
    /// vertex buffers.
    ///
    /// Nothing needs restoring on the far side: every one of these allocations is already lazily
    /// rebuilt from null/!IsCreated on next use, precisely because the same domain reload was
    /// always going to null the managed side out anyway (see SculptableMesh.EnsureGpuScatter and
    /// EnsureNativeAdjacency, SculptController.EnsureNativeScratch). This just closes the native
    /// half of that same handover.
    [InitializeOnLoad]
    internal static class NativeReloadGuard
    {
        static NativeReloadGuard()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAll;
        }

        private static void ReleaseAll()
        {
            // FindObjectsInactive.Include deliberately: a hidden/disabled sculptable (the mask
            // extract preview object, for one) owns a vertex buffer exactly like a visible one.
            foreach (SculptableMesh mesh in Object.FindObjectsByType<SculptableMesh>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                mesh.ReleaseNativeResources();

            foreach (SculptController controller in Object.FindObjectsByType<SculptController>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                controller.ReleaseNativeResources();
        }
    }
}
#endif
