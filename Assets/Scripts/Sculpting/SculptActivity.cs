using UnityEngine;

namespace Sculpting
{
    /// The single "did the model actually change just now?" signal the timelapse recorder gates on.
    /// Lives with the sculpting core, not under Timelapse/: the core is what reports it and it
    /// depends on nothing else, so the timelapse only ever READS it.
    ///
    /// The recorder needs to answer one question every frame - should this frame end up in the
    /// video? - and the honest answer is "only if the sculpt itself moved". Nothing about the
    /// INPUT can answer that: the left button is held while orbiting the view, the cursor moves
    /// while the user is only lining up a stroke, and a click that misses the mesh looks exactly
    /// like one that lands on it. So this is reported from the geometry side instead - the places
    /// that actually push new vertex positions into a mesh (see SculptableMesh.ApplyVertices/
    /// ApplyVerticesLocal, both of which early-out when nothing really drifted) plus the scene
    /// actions that change the model without touching one object's vertices (EditHistory.
    /// RecordSceneAction) and a live gizmo drag (TransformGizmo.HandleDragInput).
    ///
    /// Undo and redo are the deliberate exception. They also write vertices, and they go through
    /// the very same apply paths, so they WOULD register as activity - which would mean a
    /// timelapse that faithfully records you erasing your own work. EditHistory.Undo/Redo wrap
    /// their step in BeginSuppress/EndSuppress so those writes report nothing at all.
    ///
    /// Static, matching how EditHistory and the rest of this project reach shared state without a
    /// singleton GameObject, and reset on load for the same reason (Enter Play Mode without a
    /// domain reload would otherwise carry the previous session's timestamps in).
    public static class SculptActivity
    {
        /// Unscaled time of the most recent real change. Negative infinity until something
        /// happens, which reads as "idle forever" - exactly right before the first stroke.
        public static float LastEditUnscaledTime { get; private set; } = float.NegativeInfinity;

        /// How many changes have been reported this session. The recorder window shows it; it is
        /// also the cheapest way to confirm the hooks are actually firing.
        public static int EditCount { get; private set; }

        /// The object most recently sculpted, so the camera can follow whatever is being worked
        /// on rather than whatever happens to be selected. Null for a change that isn't about one
        /// object's vertices (an SSphere convert, a gizmo drag on a light).
        public static SculptableMesh LastEdited { get; private set; }

        public static float SecondsSinceLastEdit => Time.unscaledTime - LastEditUnscaledTime;

        private static int _suppressDepth;

        // Where the work is, kept as an object plus a point in ITS local space rather than a
        // world position. Two things fall out of that for free: the point stays correct when the
        // object is moved, rotated or scaled after the fact, and it invalidates itself when the
        // object is deleted (the Unity null check below), so nothing has to remember to clear it.
        private static SculptableMesh _pointOwner;
        private static Vector3 _pointLocal;

        /// Roughly where on the model the most recent change landed, in world space - what the
        /// "follow the artist" timelapse camera leans its framing towards. False when nothing has
        /// reported a position yet, or when the object that did has since been destroyed.
        public static bool TryGetEditPoint(out Vector3 worldPoint)
        {
            if (_pointOwner == null)
            {
                worldPoint = Vector3.zero;
                return false;
            }
            worldPoint = _pointOwner.transform.TransformPoint(_pointLocal);
            return true;
        }

        /// True while an undo/redo is replaying - see the class remarks.
        public static bool Suppressed => _suppressDepth > 0;

        /// Call from anywhere that just changed the model for real. `source` is the object that
        /// changed when there is one; pass null for a change that isn't about a single mesh, and
        /// LastEdited keeps whatever it had (the camera would rather keep framing the last thing
        /// you sculpted than snap to nothing).
        public static void ReportEdit(SculptableMesh source = null)
        {
            if (_suppressDepth > 0) return;

            LastEditUnscaledTime = Time.unscaledTime;
            EditCount++;
            if (source != null) LastEdited = source;
        }

        /// As above, plus roughly WHERE on `source` the change landed, in its local space.
        ///
        /// Only the paths that genuinely know a location report one - a brush footprint, a mask
        /// dab. Whole-mesh operations (Remesh, a mirror, a boolean) use the plain overload, and
        /// that deliberately leaves the last known point ALONE rather than clearing it, for the
        /// same reason the plain overload leaves LastEdited alone: after a remesh, the last place
        /// the user was actually working is still a far better guess at what to frame than
        /// nothing at all.
        public static void ReportEdit(SculptableMesh source, Vector3 localPoint)
        {
            if (_suppressDepth > 0) return;

            ReportEdit(source);
            if (source == null) return;

            _pointOwner = source;
            _pointLocal = localPoint;
        }

        /// Counted rather than a plain bool: the suppressed regions are short and non-overlapping
        /// today, but a counter costs nothing and means a future nested one can't have its inner
        /// scope switch reporting back on halfway through the outer one.
        public static void BeginSuppress() => _suppressDepth++;

        public static void EndSuppress() => _suppressDepth = Mathf.Max(0, _suppressDepth - 1);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForNewSession()
        {
            LastEditUnscaledTime = float.NegativeInfinity;
            EditCount = 0;
            LastEdited = null;
            _suppressDepth = 0;
            _pointOwner = null;
            _pointLocal = Vector3.zero;
        }
    }
}
