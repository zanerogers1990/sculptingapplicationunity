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

        [SerializeField] private bool studioLightingEnabled = true;
        [SerializeField] private LightingMode mode = LightingMode.ThreePoint;

        private RigLight[] _rig;
        private Transform _rigRoot;
        private Light _sceneSun;
        private SculptableMesh _target;

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
            GameObject go = GameObject.Find("Light_" + label) ?? new GameObject("Light_" + label);
            go.transform.SetParent(_rigRoot, false);
            Light light = go.GetComponent<Light>();
            if (light == null) light = go.AddComponent<Light>();

            light.type = LightType.Spot;
            light.spotAngle = 110f;
            light.innerSpotAngle = 40f;
            light.range = 30f;
            // Only the Key light casts shadows by default - shadows from every light at once
            // muddy the read of the form and cost more to render than this tool needs.
            light.shadows = slot == LightSlot.Key ? LightShadows.Soft : LightShadows.None;
            return light;
        }

        private Vector3 GetPivot()
        {
            if (_target == null) _target = FindFirstObjectByType<SculptableMesh>();
            if (_target == null) return Vector3.zero;
            Mesh mesh = _target.Mesh;
            return mesh != null ? _target.transform.TransformPoint(mesh.bounds.center) : _target.transform.position;
        }

        private void Update()
        {
            if (_sceneSun != null) _sceneSun.enabled = !studioLightingEnabled;
            if (_rig == null) return;

            Vector3 pivot = GetPivot();
            for (int i = 0; i < _rig.Length; i++)
            {
                var slot = (LightSlot)i;
                RigLight cfg = _rig[i];
                bool wantActive = studioLightingEnabled && cfg.enabled && IsSlotAvailable(slot);
                if (cfg.light.gameObject.activeSelf != wantActive) cfg.light.gameObject.SetActive(wantActive);
                if (!wantActive) continue;

                Quaternion rot = Quaternion.Euler(cfg.pitch, cfg.yaw, 0f);
                Vector3 pos = pivot + rot * (Vector3.back * cfg.distance);
                Vector3 aim = pivot - pos;
                Quaternion look = aim.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(aim) : cfg.light.transform.rotation;
                cfg.light.transform.SetPositionAndRotation(pos, look);
                cfg.light.intensity = cfg.intensity;
                cfg.light.color = cfg.color;
            }
        }
    }
}
