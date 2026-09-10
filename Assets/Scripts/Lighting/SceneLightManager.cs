using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Owns the scene's user-placed lights: adding, deleting, selecting (one or many) and handing
    /// the selection to the transform gizmo so they can be moved and aimed like any other object.
    ///
    /// This is the replacement for LightingRigController's fixed key/fill/rim slots. That rig
    /// placed every light from yaw/pitch/distance sliders because a Light has no geometry to grab;
    /// SceneLight solves that with a clickable marker, which makes an arbitrary number of lights
    /// you can just put where you want them possible. The old rig is left intact and still
    /// serialises - turning studio lighting off and adding lights here is the migration path, and
    /// a scene saved before this existed keeps working untouched.
    ///
    /// Light SELECTION is deliberately separate from SelectionManager's mesh selection rather than
    /// folded into it: SelectionManager is SculptableMesh-typed all the way through (Join, the
    /// scene graph list, visibility, the brush target), and widening it to hold two unrelated kinds
    /// of thing would touch every one of those for no gain. Selecting a light clears the mesh
    /// selection and vice versa, which also keeps "what will the gizmo move?" unambiguous.
    public class SceneLightManager : MonoBehaviour, IGizmoTargetSource
    {
        // Default placement distance for a new light, as a multiple of the subject's radius -
        // far enough out to light it rather than sit inside it, close enough to still be on
        // screen at the framing the subject is usually viewed at.
        private const float SpawnDistanceFactor = 2.6f;
        private const float DefaultPointIntensity = 6f;
        private const float DefaultSpotIntensity = 8f;
        private const float DefaultDirectionalIntensity = 1.6f;
        // Bounding-sphere radius of the app's default 1x1x1 primitive - the size the intensity and
        // range numbers above were picked against, and what a scene with nothing in it falls back
        // to. Same calibration constant, for the same reason, as LightingRigController's.
        private const float CalibrationRadius = 0.8660254f;

        private readonly List<SceneLight> _lights = new List<SceneLight>();
        private readonly List<SceneLight> _selected = new List<SceneLight>();
        private readonly List<GizmoTarget> _gizmoTargets = new List<GizmoTarget>();

        private Transform _root;
        private int _pushedSelectionVersion = -1;

        /// Bumped on every add/delete/selection change, so a UI panel can poll one int instead of
        /// diffing the list - the same idiom SelectionManager.SelectionVersion already serves.
        public int Version { get; private set; }

        public IReadOnlyList<SceneLight> Lights => _lights;
        public IReadOnlyList<SceneLight> Selected => _selected;

        private TransformGizmo _gizmo;
        private TransformGizmo Gizmo => _gizmo != null ? _gizmo : (_gizmo = FindFirstObjectByType<TransformGizmo>());

        private SelectionManager _selection;
        private SelectionManager Selection => _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());

        private Transform Root
        {
            get
            {
                if (_root != null) return _root;
                GameObject go = GameObject.Find("SceneLights") ?? new GameObject("SceneLights");
                return _root = go.transform;
            }
        }

        // ------------------------------------------------------------------------ lifecycle

        private void Update()
        {
            TransformGizmo gizmo = Gizmo;
            if (gizmo == null) return;

            // Markers are controls, so they appear only while a tool that can actually move them
            // is up. During a brush stroke they would be clutter sitting in front of the model.
            bool toolActive = gizmo.Mode == GizmoMode.Transpose || gizmo.Mode == GizmoMode.Scale;
            for (int i = _lights.Count - 1; i >= 0; i--)
            {
                if (_lights[i] == null) { _lights.RemoveAt(i); Version++; continue; }
                _lights[i].SetMarkerVisible(toolActive);
            }

            // A tool being put away drops the light selection's hold on the gizmo, so switching
            // back to sculpting does not leave handles floating on a light.
            if (!toolActive)
            {
                if (_pushedSelectionVersion != -1) { gizmo.ClearExternalTargets(this); _pushedSelectionVersion = -1; }
                return;
            }

            PushGizmoTargets(gizmo);
            HandleDeleteKey();
        }

        private void PushGizmoTargets(TransformGizmo gizmo)
        {
            PruneSelection();
            if (_selected.Count == 0)
            {
                if (_pushedSelectionVersion != -1) { gizmo.ClearExternalTargets(this); _pushedSelectionVersion = -1; }
                return;
            }

            if (_pushedSelectionVersion == Version) return;
            _pushedSelectionVersion = Version;

            _gizmoTargets.Clear();
            for (int i = 0; i < _selected.Count; i++)
            {
                // A light has no size of its own, so the gizmo is given the marker's radius to
                // scale its arms from - otherwise every light would fall back to the minimum arm
                // length and the handles would be too small to grab.
                _gizmoTargets.Add(new TransformGizmoTarget(_selected[i].transform, 0.12f));
            }

            // Move AND rotate: aiming a spot or a directional light is most of what placing one
            // is. Scale is left off - a Light's transform scale means nothing.
            gizmo.SetExternalTargets(this, _gizmoTargets, GizmoHandleSet.Transpose);
        }

        private void HandleDeleteKey()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null || _selected.Count == 0) return;
            if (!kb.deleteKey.wasPressedThisFrame && !kb.backspaceKey.wasPressedThisFrame) return;
            DeleteSelected();
        }

        // ---------------------------------------------------------------------------- adding

        /// Adds a light of `type` in front of the subject, selects it, and records the add as one
        /// undo step. Returns the new light so a caller can tune it immediately.
        public SceneLight AddLight(LightType type)
        {
            Vector3 pivot = SubjectPivot(out float radius);

            var go = new GameObject(NextName(type));
            go.transform.SetParent(Root, false);

            go.transform.position = pivot + SpawnOffset(radius);
            go.transform.rotation = Quaternion.LookRotation((pivot - go.transform.position).normalized, Vector3.up);

            Light light = go.AddComponent<Light>();
            light.type = type;
            light.color = Color.white;
            light.shadows = LightShadows.None;
            switch (type)
            {
                case LightType.Spot:
                    light.intensity = DefaultSpotIntensity;
                    light.spotAngle = 70f;
                    light.innerSpotAngle = 30f;
                    light.range = radius * 12f;
                    break;
                case LightType.Directional:
                    light.intensity = DefaultDirectionalIntensity;
                    break;
                default:
                    light.type = LightType.Point;
                    light.intensity = DefaultPointIntensity;
                    light.range = radius * 12f;
                    break;
            }

            // AddComponent runs SceneLight.Awake synchronously, so the marker exists before
            // anything below can ask about it.
            SceneLight sceneLight = go.AddComponent<SceneLight>();
            Register(sceneLight);
            Select(sceneLight, additive: false);

            RecordAddUndo(sceneLight);
            return sceneLight;
        }

        private void RecordAddUndo(SceneLight light)
        {
            GameObject go = light.gameObject;
            EditHistory.RecordSceneAction("Add Light",
                () => { if (go != null) { Unregister(go.GetComponent<SceneLight>()); go.SetActive(false); } },
                () => { if (go != null) { go.SetActive(true); Register(go.GetComponent<SceneLight>()); } },
                // Deactivated rather than destroyed so redo has something to bring back; the
                // discard hook is what finally frees it once the step falls off the end of
                // history. Same "hold it inactive until history lets go" shape the mesh deltas
                // use for their payload arrays.
                () => { if (go != null && !go.activeSelf) Destroy(go); },
                0);
        }

        public void Register(SceneLight light)
        {
            if (light == null || _lights.Contains(light)) return;
            _lights.Add(light);
            Version++;
        }

        public void Unregister(SceneLight light)
        {
            if (light == null) return;
            bool removed = _lights.Remove(light);
            removed |= _selected.Remove(light);
            if (removed) Version++;
        }

        /// Deletes every selected light as one undo step.
        public void DeleteSelected()
        {
            if (_selected.Count == 0) return;

            var doomed = _selected.ToArray();
            for (int i = 0; i < doomed.Length; i++)
            {
                Unregister(doomed[i]);
                doomed[i].gameObject.SetActive(false);
            }
            _selected.Clear();
            Version++;

            EditHistory.RecordSceneAction(doomed.Length > 1 ? "Delete Lights" : "Delete Light",
                () =>
                {
                    for (int i = 0; i < doomed.Length; i++)
                        if (doomed[i] != null) { doomed[i].gameObject.SetActive(true); Register(doomed[i]); }
                },
                () =>
                {
                    for (int i = 0; i < doomed.Length; i++)
                        if (doomed[i] != null) { Unregister(doomed[i]); doomed[i].gameObject.SetActive(false); }
                },
                () =>
                {
                    for (int i = 0; i < doomed.Length; i++)
                        if (doomed[i] != null && !doomed[i].gameObject.activeSelf) Destroy(doomed[i].gameObject);
                },
                0);
        }

        // -------------------------------------------------------------------------- selecting

        /// additive:false replaces the selection (plain click); additive:true toggles this light in
        /// or out of it (shift-click), matching SelectionManager.ToggleSelected's semantics so both
        /// kinds of object behave the same way under the same modifier.
        public void Select(SceneLight light, bool additive)
        {
            if (light == null) return;

            if (!additive)
            {
                _selected.Clear();
                _selected.Add(light);
            }
            else if (!_selected.Remove(light))
            {
                _selected.Add(light);
            }

            // Two kinds of thing cannot be gizmo-dragged together (see the class remarks), so
            // taking a light selection gives up the mesh one.
            if (_selected.Count > 0) Selection?.ClearSelection();

            RefreshSelectionTint();
            Version++;
        }

        public void ClearSelection()
        {
            if (_selected.Count == 0) return;
            _selected.Clear();
            RefreshSelectionTint();
            Version++;
        }

        public bool IsSelected(SceneLight light) => _selected.Contains(light);

        private void PruneSelection()
        {
            for (int i = _selected.Count - 1; i >= 0; i--)
                if (_selected[i] == null) { _selected.RemoveAt(i); Version++; }
        }

        private void RefreshSelectionTint()
        {
            for (int i = 0; i < _lights.Count; i++)
                if (_lights[i] != null) _lights[i].SetSelected(_selected.Contains(_lights[i]));
        }

        /// The light whose marker `ray` hits first, or null. Used by TransformGizmo's
        /// click-to-select so one click can land on either a mesh or a light, whichever is nearer.
        public SceneLight Raycast(Ray ray, out float distance)
        {
            distance = float.MaxValue;
            SceneLight best = null;

            foreach (RaycastHit hit in Physics.RaycastAll(ray, 1000f))
            {
                SceneLight candidate = hit.collider.GetComponentInParent<SceneLight>();
                if (candidate == null || hit.distance >= distance) continue;
                distance = hit.distance;
                best = candidate;
            }
            return best;
        }

        // ------------------------------------------------------------------- gizmo callbacks

        // Lights move through plain Transform writes, so the gizmo's own scene-level transform
        // step is exactly the right undo entry - there is no separate light history to open, and
        // saying false here is what makes the gizmo record one.
        public bool RecordsOwnUndoStep => false;

        public void OnGizmoDragStarted() { }

        public void OnGizmoDragEnded(bool changed) { }

        // ------------------------------------------------------------------------- placement

        // How many lights have been placed, ever - the spread below advances with it rather than
        // with the live count, so deleting a light and adding another does not drop the new one
        // back on top of an existing one.
        private int _spawnOrdinal;

        /// Where the next light goes, relative to the subject.
        ///
        /// Every light used to spawn at one fixed offset, which put each new one exactly inside the
        /// last: two lights at the same point are one light you cannot click, and the second was
        /// unselectable because the raycast could only ever return whichever marker it reached
        /// first. Successive lights are now spread around the subject by the golden angle - the
        /// standard trick for scattering points around a circle so that no two land close together
        /// at any count, rather than the clumping a fixed step gives whenever the count divides
        /// evenly into it. Elevation alternates high/low over three steps for the same reason.
        ///
        /// Still starts up and to the side rather than dead in front: a light on the camera axis
        /// flattens the form, which is the first thing any lighting guide tells you not to do.
        private Vector3 SpawnOffset(float radius)
        {
            const float GoldenAngle = 137.507764f;
            int n = _spawnOrdinal++;

            float yaw = -35f + n * GoldenAngle;
            float pitch = new[] { 32f, 8f, 55f }[n % 3];
            Vector3 direction = Quaternion.Euler(-pitch, yaw, 0f) * Vector3.back;
            return direction * (radius * SpawnDistanceFactor);
        }

        /// Where new lights aim, and the scale everything about them is sized against: the current
        /// sculpt subject, falling back to the world origin at calibration size in an empty scene.
        private Vector3 SubjectPivot(out float radius)
        {
            radius = CalibrationRadius;
            SculptableMesh target = Selection != null ? Selection.PrimarySelection : null;
            if (target == null) return Vector3.zero;

            Mesh mesh = target.Mesh;
            if (mesh == null) return target.transform.position;

            Vector3 worldExtents = Vector3.Scale(mesh.bounds.extents, target.transform.lossyScale);
            float measured = worldExtents.magnitude;
            if (measured > 1e-4f) radius = measured;
            return target.transform.TransformPoint(mesh.bounds.center);
        }

        private readonly Dictionary<LightType, int> _nameCounts = new Dictionary<LightType, int>();

        private string NextName(LightType type)
        {
            _nameCounts.TryGetValue(type, out int n);
            _nameCounts[type] = ++n;
            return type + " Light " + n;
        }
    }
}
