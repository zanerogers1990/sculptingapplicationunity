using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// The scene's entire lighting setup, built from one LightingPresets entry at a time.
    ///
    /// Picking a preset DESTROYS every light the previous one made and builds the new rig from
    /// scratch - there is never a leftover light from an earlier look, and nothing to merge. It
    /// also replaces the rest of Unity's stock setup: any Light it did not create (the scene's
    /// default Directional Light) is switched off, and the ambient is taken over with the preset's
    /// own trilight gradient, so what is on screen is the preset and only the preset.
    ///
    /// Every light is DIRECTIONAL. The rigs this replaced used spot/point lights placed at a
    /// distance from the subject, which made every look depend on the subject's size and position
    /// and produced the classic failure of a light parked inside the model burning a hot pool
    /// through its surface. A directional light has no position at all: a preset looks the same on
    /// any sculpt, at any scale, anywhere in the scene. The key is RenderSettings.sun, so URP makes
    /// it the main light - the only one that casts shadows.
    ///
    /// By default the rig is locked to the camera, like Blender's studio lights and ZBrush's: the
    /// key always comes from the upper-left of the VIEW, so orbiting to the back of a sculpt never
    /// leaves you looking at it in the rim light's silhouette. Unlocking freezes the rig in the
    /// world where it currently is.
    ///
    /// This file used to be LightingRigController (the .meta was kept, so the component already
    /// in the scene is this one - its old studioLightingEnabled/mode fields are simply ignored).
    [DefaultExecutionOrder(1000)] // after CameraOrbitController's Update, so a camera-locked rig never lags a frame
    public class LightingPresetController : MonoBehaviour
    {
        private const string RigName = "LightingPresetRig";
        public const float MaxBrightness = 2f;

        [SerializeField] private string presetId = LightingPresets.DefaultId;
        [SerializeField] private bool fivePoint;
        [SerializeField] private float brightness = 1f;
        [SerializeField] private float rotation;
        [SerializeField] private bool followCamera = true;
        [SerializeField] private float worldYaw;
        [SerializeField] private bool shadows = true;

        private Transform _rig;
        // NonSerialized so a mid-Play recompile drops these to null and LateUpdate rebuilds,
        // rather than the reload's state backup handing back an array of dead references.
        [System.NonSerialized] private Light[] _lights;
        [System.NonSerialized] private PresetLight[] _specs;
        private bool _hdriWasActive;
        private Camera _camera;

        private static LightingPresetController _instance;

        /// The scene's controller, self-installing in Play mode if the scene has none. Outside
        /// Play it only ever FINDS - an editor tool inspecting this property must not be able to
        /// leave a stray GameObject in the scene.
        public static LightingPresetController Instance
        {
            get
            {
                if (_instance != null) return _instance;
                _instance = FindFirstObjectByType<LightingPresetController>();
                if (_instance == null && Application.isPlaying)
                    _instance = new GameObject("Lighting").AddComponent<LightingPresetController>();
                return _instance;
            }
        }

        /// Bumped on every change, so the panel can poll one int instead of diffing settings.
        public int Version { get; private set; }

        public LightingPreset Current => LightingPresets.Find(presetId);

        public string PresetId
        {
            get => Current.Id;
            set { presetId = value; Rebuild(); }
        }

        /// false = 3-point (key, fill, rim); true = 5-point (+ kicker, top).
        public bool FivePoint
        {
            get => fivePoint;
            set { if (fivePoint == value) return; fivePoint = value; Rebuild(); }
        }

        /// Scales every light AND the ambient together, so the preset's look (its ratios) holds
        /// and only the exposure changes.
        public float Brightness
        {
            get => brightness;
            set
            {
                brightness = Mathf.Clamp(value, 0f, MaxBrightness);
                ApplyIntensities();
                ApplyAmbient();
                Version++;
            }
        }

        /// Spins the whole rig around the subject's vertical axis, in degrees.
        public float Rotation
        {
            get => rotation;
            set { rotation = Mathf.Repeat(value + 180f, 360f) - 180f; UpdateOrientation(); Version++; }
        }

        public bool FollowCamera
        {
            get => followCamera;
            set
            {
                if (followCamera == value) return;
                // Unlocking freezes the rig where it is on screen right now, rather than letting
                // it jump to wherever world-forward happens to be. Only the camera's heading is
                // kept - a world-space rig should stay level, whatever angle it was unlocked at.
                if (!value && Cam != null) worldYaw = Cam.transform.eulerAngles.y;
                followCamera = value;
                UpdateOrientation();
                Version++;
            }
        }

        public bool Shadows
        {
            get => shadows;
            set { shadows = value; ApplyIntensities(); Version++; }
        }

        public float WorldYaw => worldYaw;

        /// Restores a saved lighting state in one rebuild.
        public void ApplySaved(string id, bool five, float bright, float rot, bool follow, float yaw, bool castShadows)
        {
            presetId = id;
            fivePoint = five;
            brightness = Mathf.Clamp(bright, 0f, MaxBrightness);
            rotation = rot;
            followCamera = follow;
            worldYaw = yaw;
            shadows = castShadows;
            Rebuild();
        }

        private Camera Cam => _camera != null ? _camera : (_camera = Camera.main);

        private static bool HdriActive => HdriEnvironmentController.Existing?.IsActive ?? false;

        // ------------------------------------------------------------------------ lifecycle

        private void Awake()
        {
            if (_instance == null) _instance = this;
            Rebuild();
        }

        private void LateUpdate()
        {
            if (_rig == null || _lights == null || AnyLightMissing()) Rebuild();

            // The HDRI owns the ambient while it is on (see ApplyAmbient). Switching it off
            // restores whatever ambient was in place when it switched on - which may be an
            // earlier preset's - so the current one is put back the moment it lets go.
            bool hdri = HdriActive;
            if (_hdriWasActive && !hdri) ApplyAmbient();
            _hdriWasActive = hdri;

            if (followCamera) UpdateOrientation();
        }

        private bool AnyLightMissing()
        {
            for (int i = 0; i < _lights.Length; i++)
                if (_lights[i] == null) return true;
            return false;
        }

        // ------------------------------------------------------------------------- building

        /// Tears down the current rig and builds the selected preset's.
        private void Rebuild()
        {
            EnsureRig();

            // Deactivated before the (end-of-frame) Destroy so the outgoing lights stop
            // contributing immediately and can never be picked as the main light again.
            for (int i = _rig.childCount - 1; i >= 0; i--)
            {
                GameObject old = _rig.GetChild(i).gameObject;
                old.SetActive(false);
                Destroy(old);
            }

            LightingPreset preset = Current;
            PresetLight[] all = { preset.Key, preset.Fill, preset.Rim, preset.Kicker, preset.Top };
            int count = fivePoint ? 5 : 3;

            _lights = new Light[count];
            _specs = new PresetLight[count];
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject(LightingPresets.SlotNames[i] + " Light");
                go.transform.SetParent(_rig, false);

                Light light = go.AddComponent<Light>();
                light.type = LightType.Directional;
                // Colours are authored directly; a colour temperature on top would tint them
                // again (the scene's stock light had 5000K switched on).
                light.useColorTemperature = false;
                light.color = all[i].Color;
                light.shadows = LightShadows.None;

                _lights[i] = light;
                _specs[i] = all[i];
            }

            // Named explicitly: URP otherwise promotes the BRIGHTEST directional light to main,
            // and several presets have a rim hotter than their key.
            RenderSettings.sun = _lights[0];

            ApplyIntensities();
            ApplyAmbient();
            DisableForeignLights();
            UpdateOrientation();
            Version++;
        }

        private void EnsureRig()
        {
            if (_rig != null) return;
            Transform existing = transform.Find(RigName);
            if (existing == null)
            {
                existing = new GameObject(RigName).transform;
                existing.SetParent(transform, false);
            }
            _rig = existing;
        }

        private void ApplyIntensities()
        {
            if (_lights == null) return;
            LightingPreset preset = Current;
            for (int i = 0; i < _lights.Length; i++)
            {
                if (_lights[i] == null) continue;
                _lights[i].intensity = _specs[i].Intensity * brightness;
            }

            Light key = _lights.Length > 0 ? _lights[0] : null;
            if (key != null)
            {
                key.shadows = shadows ? LightShadows.Soft : LightShadows.None;
                key.shadowStrength = preset.ShadowStrength;
            }
        }

        /// Trilight ambient from the preset, scaled by Brightness. Skipped while an HDRI is on:
        /// the HDRI's whole job is to be the environment light, and the preset's lights still
        /// shape the form on top of it.
        private void ApplyAmbient()
        {
            if (HdriActive) return;
            LightingPreset preset = Current;

            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientIntensity = 1f;
            // Scaled in LINEAR space - multiplying the gamma values would make Brightness act
            // on the ambient far more steeply than on the lights.
            RenderSettings.ambientSkyColor = ScaleLinear(preset.AmbientSky, brightness);
            RenderSettings.ambientEquatorColor = ScaleLinear(preset.AmbientEquator, brightness);
            RenderSettings.ambientGroundColor = ScaleLinear(preset.AmbientGround, brightness);
            RenderSettings.reflectionIntensity = preset.Reflection;
            // Re-evaluates the ambient probe (and default reflection) from the new settings;
            // without it the shaders keep sampling whatever SH was there before.
            DynamicGI.UpdateEnvironment();
        }

        private static Color ScaleLinear(Color gamma, float scale) => (gamma.linear * scale).gamma;

        /// Switches off every Light this controller did not create - the scene's stock
        /// Directional Light, in practice. Disabled rather than destroyed: the scene file owns it.
        private void DisableForeignLights()
        {
            Light[] lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                Light light = lights[i];
                if (light.enabled && !light.transform.IsChildOf(_rig)) light.enabled = false;
            }
        }

        // ----------------------------------------------------------------------- orientation

        /// Aims every light from its preset azimuth/elevation, in the camera's frame when locked
        /// or a level world frame when not, plus the user's Rotation.
        private void UpdateOrientation()
        {
            if (_lights == null) return;

            Quaternion frame = followCamera && Cam != null
                ? Cam.transform.rotation
                : Quaternion.Euler(0f, worldYaw, 0f);
            frame *= Quaternion.Euler(0f, rotation, 0f);
            Vector3 up = frame * Vector3.up;

            for (int i = 0; i < _lights.Length; i++)
            {
                if (_lights[i] == null) continue;
                Vector3 shine = -(frame * SourceDirection(_specs[i]));
                // A near-overhead light is almost parallel to `up`, which LookRotation cannot
                // use as a hint.
                Vector3 hint = Mathf.Abs(Vector3.Dot(shine, up)) > 0.999f ? frame * Vector3.forward : up;
                _lights[i].transform.rotation = Quaternion.LookRotation(shine, hint);
            }
        }

        /// Unit vector from the subject TOWARD the light, in the rig's frame (camera looks down
        /// +Z): azimuth 0 = from the camera, +90 = from the right; elevation + = from above.
        private static Vector3 SourceDirection(PresetLight spec)
            => Quaternion.Euler(spec.Elevation, -spec.Azimuth, 0f) * Vector3.back;
    }
}
