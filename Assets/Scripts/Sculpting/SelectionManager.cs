using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Tracks every SculptableMesh in the scene and which one(s) are selected. Found via
    /// FindFirstObjectByType (no singleton, matching the rest of this codebase) by
    /// SculptController (to pick its sculpt/mirror target), TransformGizmo (to pick its
    /// target transform) and SceneGraphUIBuilder (to list/select/delete objects).
    ///
    /// PrimarySelection deliberately has NO Awake()-time "auto-select the first object" logic -
    /// early attempts at that hit a real Unity lifecycle hazard: SculptUIBuilder used to build
    /// its whole UI (including reading SculptController.Mirror.MirrorX) from Awake(), and
    /// Awake() order between separate GameObjects/components is not guaranteed, so a
    /// SelectionManager.Awake() auto-select could easily run AFTER SculptUIBuilder's Awake()
    /// tried to read it. Instead PrimarySelection lazily falls back to the first registered
    /// object the first time it's read with nothing explicitly selected yet - registration
    /// happens in SculptableMesh.OnEnable(), and every object's Awake+OnEnable is guaranteed
    /// complete before any Start() runs, so as long callers needing a startup selection read it
    /// from Start() (as SculptUIBuilder now does), it's always populated correctly regardless of
    /// inter-object ordering.
    public class SelectionManager : MonoBehaviour
    {
        private readonly List<SculptableMesh> _allObjects = new List<SculptableMesh>();
        private readonly List<SculptableMesh> _selectedSet = new List<SculptableMesh>();
        private SculptableMesh _primary;

        public IReadOnlyList<SculptableMesh> AllObjects => _allObjects;
        public IReadOnlyList<SculptableMesh> SelectedSet => _selectedSet;

        // Bumped on every selection/visibility change so UI panels can cheaply poll "did
        // anything change" once per frame instead of diffing lists - same idiom
        // SculptUIBuilder already uses for brush-state changes (see its _lastShownBrush check).
        public int SelectionVersion { get; private set; }

        public SculptableMesh PrimarySelection
        {
            get
            {
                if (_primary == null && _allObjects.Count > 0) _primary = _allObjects[0];
                return _primary;
            }
        }

        public void Register(SculptableMesh obj)
        {
            if (!_allObjects.Contains(obj)) _allObjects.Add(obj);
        }

        public void Unregister(SculptableMesh obj)
        {
            _allObjects.Remove(obj);
            _selectedSet.Remove(obj);
            if (_primary == obj) _primary = null; // PrimarySelection getter falls back on next read
            SelectionVersion++;
        }

        /// Selects obj. additive=false clears any prior selection first and makes obj primary
        /// (ordinary click); additive=true adds obj to the multi-select set for Join without
        /// disturbing the existing primary (Ctrl/Shift+click).
        public void Select(SculptableMesh obj, bool additive)
        {
            if (obj == null) return;
            if (!additive)
            {
                _selectedSet.Clear();
                _selectedSet.Add(obj);
                _primary = obj;
            }
            else
            {
                if (!_selectedSet.Contains(obj)) _selectedSet.Add(obj);
                // Deliberately does NOT reassign _primary to obj (which is what this branch
                // used to do, contradicting the summary above). Join treats the primary as the
                // SURVIVOR, and MeshJoiner combines every other mesh into the survivor's own
                // local space - so the merged result inherits the survivor's pivot, and
                // MirrorController mirrors through that pivot (its planes sit at localPosition
                // zero and GetMirrorSigns reflects local coordinates through the origin).
                //
                // Reassigning here meant the survivor was whichever object was Ctrl-clicked
                // LAST: merging a torso plus two shoulder spheres left the merged body with a
                // shoulder sphere's pivot, putting the symmetry plane out at the shoulder
                // instead of down the torso's centerline - and mirrored brush strokes reflected
                // through that wrong plane too, so this was a real sculpting bug and not just a
                // misdrawn gizmo. Keeping the first-selected object primary means the object
                // you clicked normally (and which the scene-graph list highlights) is the one
                // whose center the merged result keeps.
                if (_primary == null) _primary = obj;
            }
            SelectionVersion++;
        }

        /// Shift-click semantics: adds obj to the selection, or drops it if it was already in
        /// there. Distinct from Select(additive:true), which only ever adds - that one exists for
        /// Join, where clicking an already-picked object again should not silently remove it from
        /// the merge. Here the click IS the toggle, which is what every DCC does and what makes a
        /// multi-object transform selection correctable without starting over.
        ///
        /// Removing the primary promotes whatever is left, so the scene graph's highlight and the
        /// gizmo's pivot never point at something no longer in the set.
        public void ToggleSelected(SculptableMesh obj)
        {
            if (obj == null) return;

            if (_selectedSet.Remove(obj))
            {
                if (_primary == obj) _primary = _selectedSet.Count > 0 ? _selectedSet[0] : null;
                SelectionVersion++;
                return;
            }

            _selectedSet.Add(obj);
            if (_primary == null) _primary = obj;
            SelectionVersion++;
        }

        public void ClearSelection()
        {
            _selectedSet.Clear();
            _primary = null;
            SelectionVersion++;
        }

        public bool IsSelected(SculptableMesh obj) => _selectedSet.Contains(obj);

        /// The visible registered object whose surface `ray` hits first, or null. Hit-tested
        /// against each object's own live vertex data (SculptableMesh.RaycastMesh), not against a
        /// MeshCollider - the collider is deliberately not kept in step with the sculpted surface
        /// (see SculptableMesh's class remarks), so picking through it would miss geometry the
        /// user can plainly see.
        ///
        /// Lives here rather than in either caller because both the brush controller's
        /// double-click pick and the transform gizmo's click-to-select need exactly this, and a
        /// second copy would be a second place for the visibility rule to drift.
        public SculptableMesh Raycast(Ray ray, float maxDistance = 1000f) => Raycast(ray, out _, maxDistance);

        /// Raycast, also reporting how far along the ray the hit is (world units, assuming a
        /// normalized ray direction) - what CameraOrbitController needs to keep the camera and
        /// its near plane out of the surface it is looking at.
        public SculptableMesh Raycast(Ray ray, out float distance, float maxDistance = 1000f)
        {
            distance = 0f;
            SculptableMesh closest = null;
            float closestSqr = float.MaxValue;
            for (int i = 0; i < _allObjects.Count; i++)
            {
                SculptableMesh obj = _allObjects[i];
                if (obj == null || !obj.Visible) continue;
                if (!obj.RaycastMesh(ray, maxDistance, out Vector3 hitPoint, out _)) continue;

                float sqr = (hitPoint - ray.origin).sqrMagnitude;
                if (sqr >= closestSqr) continue;
                closestSqr = sqr;
                closest = obj;
            }
            if (closest != null) distance = Mathf.Sqrt(closestSqr);
            return closest;
        }

        /// Bumps SelectionVersion for a change this class didn't make itself - today, an object
        /// being renamed. The scene-graph list draws object names, so it has to rebuild for a
        /// rename exactly as it does for a spawn or a delete, and SelectionVersion is already
        /// the one signal it polls.
        public void NotifyChanged() => SelectionVersion++;

        /// Toggles obj's visibility; if hiding the current primary selection, backs the
        /// selection off it onto the next visible object (or none) - sculpting an object you
        /// can't see would be confusing.
        public void SetVisible(SculptableMesh obj, bool visible)
        {
            obj.SetVisible(visible);
            if (!visible && _primary == obj)
            {
                _selectedSet.Remove(obj);
                _primary = null;
                for (int i = 0; i < _allObjects.Count; i++)
                {
                    if (_allObjects[i] != obj && _allObjects[i].Visible)
                    {
                        _primary = _allObjects[i];
                        _selectedSet.Add(_primary);
                        break;
                    }
                }
            }
            SelectionVersion++;
        }

        /// Unregisters and deactivates obj's GameObject - NOT an immediate Destroy - and records
        /// one EditHistory scene action so Z undoes it, reselecting a remaining object if obj was
        /// primary. Mirrors ZSphereController.Skin's Convert-undo: parked (unregistered, inactive) rather than destroyed, so undo just reactivates
        /// and reselects the same object - any strokes on it are untouched. The GameObject is only
        /// actually freed once the step falls off history (or the scene is cleared), via the
        /// discard closure below - SceneSerializer's save path walks SelectionManager.AllObjects,
        /// not every SculptableMesh in the hierarchy, so a parked object is correctly left out of
        /// a save made while its delete is still undoable.
        public void DeleteObject(SculptableMesh obj)
        {
            if (obj == null) return;
            bool wasPrimary = _primary == obj;
            Unregister(obj);
            obj.gameObject.SetActive(false);
            if (wasPrimary && _allObjects.Count > 0)
            {
                foreach (SculptableMesh candidate in _allObjects)
                {
                    if (candidate.Visible) { Select(candidate, false); break; }
                }
            }

            // Rough mesh footprint (positions + triangle indices only) - same estimate
            // RecordConvertUndo uses for a SculptableMesh scene action, so the memory budget
            // sees roughly what a parked object is actually holding onto.
            Mesh mesh = obj.Mesh;
            long bytes = mesh != null ? (long)mesh.vertexCount * 12 + (long)mesh.triangles.Length * 4 : 0;

            EditHistory.RecordSceneAction("Delete Object",
                undo: () =>
                {
                    if (obj == null) return;
                    obj.gameObject.SetActive(true);
                    Select(obj, false);
                },
                redo: () =>
                {
                    if (obj == null) return;
                    Unregister(obj);
                    obj.gameObject.SetActive(false);
                },
                discard: () =>
                {
                    // Only a still-parked object is freed - one reactivated since (by a redo, or
                    // by some other path) is real and in the scene again.
                    if (obj != null && !obj.gameObject.activeSelf) Destroy(obj.gameObject);
                },
                approxBytes: bytes);
        }
    }
}
