using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Nomad's Mirror: live mirrored copies of one object across world-space planes, updating as
    /// the object is sculpted, moved, remeshed, trimmed - anything.
    ///
    /// A copy is not a second mesh kept in step. It is a render-only GameObject (see
    /// MirrorRepeaterView) whose MeshFilter points at THIS object's own live Mesh, drawn through
    /// a reflected transform - Nomad's instancing. So there is nothing to sync and nothing that
    /// can drift: every edit to the mesh is already an edit to every copy, and a topology change
    /// (the thing that broke the old vertex-copying MirrorLink) is just a new mesh the copies
    /// pick up next frame.
    ///
    /// Either side can be worked on. A brush raycasts every copy and sculpts through whichever it
    /// hit (SculptableMesh.RaycastAnyCopy, SculptController's brush frame); the transform gizmo
    /// moves a selected copy by moving the original to match (MirrorViewGizmoTarget). Validate
    /// bakes the copies into real, independent objects.
    ///
    /// One copy per non-empty combination of the enabled axes (X and Z make three: X, Z and XZ),
    /// all through `center` - the world origin unless set otherwise.
    [RequireComponent(typeof(SculptableMesh))]
    public class MirrorRepeater : MonoBehaviour
    {
        // X = 1, Y = 2, Z = 4 - the same flip masks SymmetryGroup enumerates.
        [SerializeField] private int axisMask;
        [SerializeField] private Vector3 center;
        // Serialized so a mid-Play script reload keeps the copies it made rather than orphaning
        // them in the scene and building a second set.
        [SerializeField] private List<MirrorRepeaterView> views = new List<MirrorRepeaterView>();

        private SculptableMesh _source;
        private Renderer _sourceRenderer;
        private string _namedFor;

        public SculptableMesh Source => _source != null ? _source : (_source = GetComponent<SculptableMesh>());
        public int AxisMask => axisMask;
        public Vector3 Center => center;
        public int ViewCount => views.Count;

        public MirrorRepeaterView View(int index) => index >= 0 && index < views.Count ? views[index] : null;

        /// The copy's transform when it is actually on screen (the original visible and not
        /// parked), otherwise null - what raycasts and picking should consider.
        public Transform ShownViewTransform(int index)
        {
            MirrorRepeaterView view = View(index);
            if (view == null || !view.gameObject.activeInHierarchy || !view.Renderer.enabled) return null;
            return view.transform;
        }

        /// Index of the copy drawn by `t`, or -1 (the original's own transform, or anything else).
        public int IndexOfView(Transform t)
        {
            for (int i = 0; i < views.Count; i++)
                if (views[i] != null && views[i].transform == t) return i;
            return -1;
        }

        // ---------------------------------------------------------------------------- lifecycle

        /// Gives `source` live mirror copies across the axes in `mask` (X = 1, Y = 2, Z = 4)
        /// through `center`, replacing any it had; mask 0 removes them. Recorded as one undo step
        /// when `recordUndo`.
        public static void Set(SculptableMesh source, int mask, Vector3 center, bool recordUndo)
        {
            if (source == null) return;
            mask &= 7;
            MirrorRepeater existing = source.Repeater;
            int oldMask = existing != null ? existing.axisMask : 0;
            Vector3 oldCenter = existing != null ? existing.center : Vector3.zero;
            if (oldMask == mask && (mask == 0 || oldCenter == center)) return;

            Apply(source, mask, center);

            if (!recordUndo) return;
            EditHistory.RecordSceneAction(mask == 0 ? "Remove Mirror" : "Mirror",
                undo: () => Apply(source, oldMask, oldCenter),
                redo: () => Apply(source, mask, center),
                discard: null,
                approxBytes: 64);
        }

        private static void Apply(SculptableMesh source, int mask, Vector3 center)
        {
            if (source == null) return;
            MirrorRepeater repeater = source.Repeater;
            if (mask == 0)
            {
                if (repeater != null) repeater.DestroySelf();
                return;
            }

            if (repeater == null) repeater = source.gameObject.AddComponent<MirrorRepeater>();
            repeater.axisMask = mask;
            repeater.center = center;
            repeater.RebuildViews();
        }

        /// Turns every copy into a real, independent object exactly where it is shown, then
        /// removes the live mirror. Returns the new objects. Not an undo step, like every other
        /// way of creating an object.
        public List<SculptableMesh> Bake()
        {
            var created = new List<SculptableMesh>(views.Count);
            SculptableMesh source = Source;
            if (source == null) return created;

            Transform t = source.transform;
            foreach (MirrorRepeaterView view in views)
            {
                if (view == null) continue;
                // Reflects the local geometry and keeps a positive scale (see MeshCloner.Duplicate),
                // which renders identically to the copy's reflected transform.
                created.Add(MeshCloner.Duplicate(source, source.name + " Mirror " + view.AxisName, view.Signs,
                    MeshMirror.ReflectPoint(t.position, center, view.Signs),
                    MeshMirror.ReflectRotation(t.rotation, view.Signs)));
            }

            DestroySelf();
            FindFirstObjectByType<SelectionManager>()?.Select(source, false);
            return created;
        }

        private void DestroySelf()
        {
            DestroyViews();
            axisMask = 0;
            if (Source != null && Source.Repeater == this) Source.Repeater = null;
            if (Application.isPlaying) Destroy(this);
            else DestroyImmediate(this);
            FindFirstObjectByType<SelectionManager>()?.NotifyChanged();
        }

        private void Awake() => Source.Repeater = this;

        private void OnEnable()
        {
            // Also what re-points the mesh after a mid-Play script reload, which clears its
            // non-serialized back-reference but keeps this component.
            if (axisMask == 0) return;
            Source.Repeater = this;
            SetViewsActive(true);
            SyncViews();
        }

        // A deleted object is only PARKED (deactivated, so Z can bring it back - see
        // SelectionManager.DeleteObject): its copies go with it and come back with it.
        private void OnDisable() => SetViewsActive(false);

        private void OnDestroy()
        {
            DestroyViews();
            if (_source != null && _source.Repeater == this) _source.Repeater = null;
        }

        private void LateUpdate() => SyncViews();

        private void RebuildViews()
        {
            DestroyViews();
            SculptableMesh source = Source;
            source.Repeater = this;
            _sourceRenderer = source.GetComponent<Renderer>();

            for (int m = 1; m < 8; m++)
            {
                if ((m & ~axisMask) != 0) continue;
                var go = new GameObject("Mirror");
                go.layer = source.gameObject.layer;
                // An object that is not saved with the scene (a test fixture) must not leave its
                // copies in it either.
                go.hideFlags = source.gameObject.hideFlags;
                go.AddComponent<MeshFilter>();
                var renderer = go.AddComponent<MeshRenderer>();
                if (_sourceRenderer != null)
                {
                    renderer.shadowCastingMode = _sourceRenderer.shadowCastingMode;
                    renderer.receiveShadows = _sourceRenderer.receiveShadows;
                }
                var view = go.AddComponent<MirrorRepeaterView>();
                view.Init(this, SymmetryGroup.SignOfFlipMask(m), AxisNameOf(m));
                views.Add(view);
            }

            _namedFor = null;
            SyncViews();
            FindFirstObjectByType<SelectionManager>()?.NotifyChanged();
        }

        private void DestroyViews()
        {
            foreach (MirrorRepeaterView view in views)
            {
                if (view == null) continue;
                if (Application.isPlaying) Destroy(view.gameObject);
                else DestroyImmediate(view.gameObject);
            }
            views.Clear();
        }

        private void SetViewsActive(bool active)
        {
            foreach (MirrorRepeaterView view in views)
                if (view != null && view.gameObject.activeSelf != active) view.gameObject.SetActive(active);
        }

        /// Puts every copy where the original's reflection is, drawing the original's current
        /// mesh and material. Runs every LateUpdate - after any Update that can move, reshape or
        /// re-material the original - and costs a few field writes per copy.
        public void SyncViews()
        {
            SculptableMesh source = Source;
            if (source == null) return;
            if (_sourceRenderer == null) _sourceRenderer = source.GetComponent<Renderer>();

            Transform t = source.transform;
            Mesh mesh = source.Mesh;
            Material material = _sourceRenderer != null ? _sourceRenderer.sharedMaterial : null;
            bool renamed = !ReferenceEquals(_namedFor, source.name);
            if (renamed) _namedFor = source.name;

            foreach (MirrorRepeaterView view in views)
            {
                if (view == null) continue;
                Vector3 signs = view.Signs;
                view.transform.SetPositionAndRotation(MeshMirror.ReflectPoint(t.position, center, signs),
                                                      MeshMirror.ReflectRotation(t.rotation, signs));
                // A NEGATIVE scale on the mirrored axes is the whole trick: the copy draws the
                // original's local geometry reflected, and Unity flips the winding for an odd
                // number of negative axes by itself. Lossy, since the copy is a scene root.
                view.transform.localScale = Vector3.Scale(t.lossyScale, signs);

                if (view.Filter.sharedMesh != mesh) view.Filter.sharedMesh = mesh;
                if (material != null && view.Renderer.sharedMaterial != material) view.Renderer.sharedMaterial = material;
                if (view.Renderer.enabled != source.Visible) view.Renderer.enabled = source.Visible;
                if (renamed) view.gameObject.name = source.name + " (Mirror " + view.AxisName + ")";
            }
        }

        public static string AxisNameOf(int mask) =>
            ((mask & 1) != 0 ? "X" : string.Empty) + ((mask & 2) != 0 ? "Y" : string.Empty) + ((mask & 4) != 0 ? "Z" : string.Empty);
    }
}
