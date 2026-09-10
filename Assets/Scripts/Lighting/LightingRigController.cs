using UnityEngine;

namespace Sculpting
{
    public enum LightingMode { ThreePoint, FivePoint }
    public enum LightSlot { Key, Fill, Rim, Kicker1, Kicker2 }

    /// A studio-style key/fill/rim (optionally +2 kicker) light rig that orbits the sculpted
    /// mesh, each light positioned from yaw/pitch/distance rather than a hand-placed
    /// transform - mirrors CameraOrbitController's spherical-coordinate approach so the UI
    /// sliders behave the way orbiting the camera already does. Builds its own light
    /// GameObjects at runtime (no scene wiring needed) and disables the scene's original
    /// Directional Light while active so the two setups don't double up.
    public class LightingRigController : MonoBehaviour
    {
        [System.Serializable]
        public class RigLight
        {
            public string label;
            public bool enabled = true;
            public float intensity;
            public Color color = Color.white;
            public float yaw;
            public float pitch;
            public float distance;
            [System.NonSerialized] public Light light;
        }

        // Off by default since the rig lost its UI (see LightingUIBuilder.BuildContent) - lights
        // are placed in the scene now. The rig is kept, not deleted: scenes saved before that
        // change still carry its settings through SculptSaveData and can turn it back on, and it
        // is what hands control of the scene's own directional sun back over (see ApplyAll's
        // _sceneSun line) when it is off. Leaving it ON with no way to reach it would be the
        // worst of both worlds.
        //
        // The scene file carries its own serialized copy of this, which overrides the default
        // here - Assets/Scenes/SampleScene.unity was edited to match.
        [SerializeField] private bool studioLightingEnabled;
        [SerializeField] private LightingMode mode = LightingMode.ThreePoint;

        // Bounding-sphere radius of the app's default 1x1x1 sculpting primitive - the size the
        // MakeDefault() distance/range numbers below were tuned against. A loaded or imported
        // mesh is rarely that size, so every placement scales distance by the target's actual
        // radius relative to this constant instead of using it as a literal world distance.
        private const float CalibrationRadius = 0.8660254f;

        private RigLight[] _rig;
        private Transform _rigRoot;
        private Light _sceneSun;
        private SculptableMesh _target;
        private SelectionManager _selectionManager;

        public bool StudioLightingEnabled { get => studioLightingEnabled; set => studioLightingEnabled = value; }
        public LightingMode Mode { get => mode; set => mode = value; }
        public RigLight GetConfig(LightSlot slot) => _rig[(int)slot];
        public bool IsSlotAvailable(LightSlot slot) => (slot != LightSlot.Kicker1 && slot != LightSlot.Kicker2) || mode == LightingMode.FivePoint;

        private void Awake()
        {
            _sceneSun = FindSceneSun();
            BuildRig();
        }

        private Light FindSceneSun()
        {
            var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            foreach (Light l in lights)
                if (l.type == LightType.Directional) return l;
            return null;
        }

        private void BuildRig()
        {
            GameObject rootGO = GameObject.Find("StudioLightingRig") ?? new GameObject("StudioLightingRig");
            _rigRoot = rootGO.transform;

            _rig = new RigLight[5];
            _rig[(int)LightSlot.Key] = MakeDefault("Key", 8f, new Color(1f, 0.96f, 0.88f), 45f, 35f, 3f);
            _rig[(int)LightSlot.Fill] = MakeDefault("Fill", 3f, new Color(0.75f, 0.82f, 1f), -50f, 15f, 3.5f);
            _rig[(int)LightSlot.Rim] = MakeDefault("Rim", 5f, new Color(0.85f, 0.9f, 1f), 180f, 30f, 3f);
            _rig[(int)LightSlot.Kicker1] = MakeDefault("Kicker 1", 2.5f, new Color(1f, 0.85f, 0.7f), 135f, 20f, 3.2f);
            _rig[(int)LightSlot.Kicker2] = MakeDefault("Kicker 2", 2.5f, new Color(0.7f, 0.85f, 1f), -135f, 20f, 3.2f);

            for (int i = 0; i < _rig.Length; i++)
                _rig[i].light = CreateLightObject(_rig[i].label, (LightSlot)i);
        }

        private static RigLight MakeDefault(string label, float intensity, Color color, float yaw, float pitch, float distance)
            => new RigLight { label = label, intensity = intensity, color = color, yaw = yaw, pitch = pitch, distance = distance };

        private Light CreateLightObject(string label, LightSlot slot)
        {
            // Searches _rigRoot's own children rather than GameObject.Find: Find SKIPS INACTIVE
            // objects, and a slot that is currently switched off is exactly that - the two
            // kickers whenever the rig is in ThreePoint mode, or every light while studio
            // lighting is disabled. Missing them meant each rebuild (BuildRig re-runs on a
            // mid-Play domain reload - see Update's remarks) created a SECOND GameObject with
            // the same name: the old one orphaned in the scene, the new one sitting ACTIVE at
            // the world origin, because a fresh GameObject starts active and unpositioned.
            //
            // That is not a cosmetic leak. These lights are spot lights with shadows off, and
            // the world origin is inside the sculpted mesh the moment a stroke or a remesh grows
            // its bounds past it - so an origin-parked light burns a bright round pool straight
            // through the surface. Reproduced directly: one simulated rebuild took the scene
            // from one "Light_Kicker 1" to two, the new one active at (0,0,0).
            Transform existing = _rigRoot.Find("Light_" + label);
            GameObject go = existing != null ? existing.gameObject : new GameObject("Light_" + label);
            go.transform.SetParent(_rigRoot, false);
            Light light = go.GetComponent<Light>();
            if (light == null) light = go.AddComponent<Light>();

            light.type = LightType.Spot;
            light.spotAngle = 110f;
            light.innerSpotAngle = 40f;
            // range is set every frame in Update, scaled to the target's current size.
            // Only the Key light casts shadows by default - shadows from every light at once
            // muddy the read of the form and cost more to render than this tool needs.
            light.shadows = slot == LightSlot.Key ? LightShadows.Soft : LightShadows.None;
            return light;
        }

        // Which SculptableMesh the rig lights right now: the scene's primary selection - the
        // same target SculptController and TransformGizmo already sculpt/move - re-resolved
        // every frame so switching objects in the Scene Graph panel moves the lights with it
        // instead of leaving them parked on whichever mesh a one-time FindFirstObjectByType
        // happened to return first. Falls back to a bare find for scenes with no
        // SelectionManager at all (a minimal single-object scene predating the scene graph).
        private SculptableMesh ResolveTarget()
        {
            if (_selectionManager == null) _selectionManager = FindFirstObjectByType<SelectionManager>();
            SculptableMesh primary = _selectionManager != null ? _selectionManager.PrimarySelection : null;
            if (primary != null) return primary;
            if (_target == null) _target = FindFirstObjectByType<SculptableMesh>();
            return _target;
        }

        private static Vector3 GetPivot(SculptableMesh target)
        {
            if (target == null) return Vector3.zero;
            Mesh mesh = target.Mesh;
            return mesh != null ? target.transform.TransformPoint(mesh.bounds.center) : target.transform.position;
        }

        // How much bigger (or smaller) the current target is than the calibration primitive,
        // so a light's configured "distance" places it proportionally the same number of
        // subject-radii away regardless of the target's actual scale - a sculpt loaded in at
        // several units tall no longer parks every light inside its own surface.
        private static float GetSizeFactor(SculptableMesh target)
        {
            Mesh mesh = target != null ? target.Mesh : null;
            if (mesh == null) return 1f;
            Vector3 worldExtents = Vector3.Scale(mesh.bounds.extents, target.transform.lossyScale);
            float radius = worldExtents.magnitude;
            return radius > 1e-4f ? Mathf.Max(0.05f, radius / CalibrationRadius) : 1f;
        }

        private void Update()
        {
            if (_sceneSun != null) _sceneSun.enabled = !studioLightingEnabled;
            // Rebuild-if-null rather than bail-if-null: a script recompile DURING Play reloads
            // the domain, and Unity's state backup keeps _rig itself (a private field it can
            // serialize) while dropping every RigLight.light inside it - the field is explicitly
            // [NonSerialized], and a Light is a scene object reference the backup can't carry
            // across either way. That left _rig non-null with null lights, so the guard below
            // passed and the loop dereferenced null on EVERY frame for the rest of the session.
            // Same lazy-rebuild-if-null discipline SculptableMesh's own plain-class caches use
            // for exactly this reload hazard.
            if (_rig == null || _rigRoot == null) BuildRig();

            SculptableMesh target = ResolveTarget();
            Vector3 pivot = GetPivot(target);
            float sizeFactor = GetSizeFactor(target);
            for (int i = 0; i < _rig.Length; i++)
            {
                var slot = (LightSlot)i;
                RigLight cfg = _rig[i];
                if (cfg.light == null) cfg.light = CreateLightObject(cfg.label, slot);

                // Place and configure EVERY slot, including the ones that are switched off,
                // before deciding whether it renders. Positioning only the active ones left a
                // disabled light parked at wherever it was created - the origin - for its whole
                // disabled life, so whatever switched it back on (a mode change, a rebuild) had
                // one frame in which it lit the model from the inside before the next tick moved
                // it out. Placing first costs a transform write on two lights that may not be
                // drawn, and removes that window entirely.
                Quaternion rot = Quaternion.Euler(cfg.pitch, cfg.yaw, 0f);
                Vector3 pos = pivot + rot * (Vector3.back * (cfg.distance * sizeFactor));
                Vector3 aim = pivot - pos;
                Quaternion look = aim.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(aim) : cfg.light.transform.rotation;
                cfg.light.transform.SetPositionAndRotation(pos, look);
                cfg.light.intensity = cfg.intensity;
                cfg.light.color = cfg.color;
                cfg.light.range = 30f * sizeFactor;

                bool wantActive = studioLightingEnabled && cfg.enabled && IsSlotAvailable(slot);
                if (cfg.light.gameObject.activeSelf != wantActive) cfg.light.gameObject.SetActive(wantActive);
            }
        }
    }
}
