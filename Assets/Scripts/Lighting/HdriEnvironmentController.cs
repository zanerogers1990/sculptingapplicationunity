using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// Image-based lighting from an equirectangular HDRI picked off disk.
    ///
    /// Unity gets diffuse ambient and the default specular reflection by rendering whatever
    /// material sits in RenderSettings.skybox, so this controller owns an HDRI skybox material
    /// and hands it to BackgroundController, which is the single writer of RenderSettings.skybox
    /// and the camera's clear flags. That split is what makes the two toggles the user actually
    /// asked for independent: the skybox slot follows the LIGHTING choice (HDRI or not), while
    /// what you SEE behind the sculpt follows the BACKGROUND choice (HDRI / gradient / flat).
    /// With an HDRI lighting the scene but a coloured background selected, the camera clears to
    /// solid colour - which makes URP skip its skybox pass - and BackgroundController's gradient
    /// dome draws the backdrop instead, while the HDRI carries on lighting from the skybox slot.
    ///
    /// Not placed in the scene: it creates itself on first use (see Instance) so no scene edit
    /// or inspector wiring is needed, and its settings live in the .sculpt save file anyway.
    public class HdriEnvironmentController : MonoBehaviour
    {
        // Rotating the HDRI has to re-bake the ambient probe and the default reflection probe
        // for the light to swing round with the image, and that bake is far too expensive to
        // run on every frame of a slider drag. Coalescing to this interval keeps dragging
        // responsive (the visible background rotates immediately - only the bake is delayed).
        private const float BakeInterval = 0.15f;

        private static HdriEnvironmentController _instance;

        private bool _enabled;
        private float _rotation;
        private float _exposure = 1f;
        private float _ambientIntensity = 1f;
        private float _reflectionIntensity = 1f;
        private string _path;
        private string _status = "No HDRI loaded.";

        [System.NonSerialized] private Material _skyboxMaterial;
        [System.NonSerialized] private Texture2D _texture;
        // An .exr loaded through the Editor's AssetDatabase is a project asset, not something
        // this controller allocated - destroying it would delete the imported texture out from
        // under the project.
        [System.NonSerialized] private bool _textureIsProjectAsset;

        private bool _envDirty;
        private float _lastBakeTime = -999f;
        private bool _environmentOverridden;
        private AmbientMode _savedAmbientMode;
        private float _savedAmbientIntensity;
        private DefaultReflectionMode _savedReflectionMode;
        private float _savedReflectionIntensity;
        private Material _savedSkybox;

        /// Finds the controller, creating it if the scene has none. Never returns null.
        public static HdriEnvironmentController Instance
        {
            get
            {
                if (_instance == null) _instance = FindFirstObjectByType<HdriEnvironmentController>();
                if (_instance == null)
                {
                    var go = new GameObject("HdriEnvironment");
                    _instance = go.AddComponent<HdriEnvironmentController>();
                }
                return _instance;
            }
        }

        /// The scene's controller if one exists, without creating one. For callers that only
        /// want to read state (BackgroundController on a scene with no HDRI in play).
        public static HdriEnvironmentController Existing =>
            _instance != null ? _instance : (_instance = FindFirstObjectByType<HdriEnvironmentController>());

        /// True only when an HDRI is both loaded and switched on - i.e. when the skybox slot and
        /// the environment lighting settings are actually being driven by this controller.
        public bool IsActive => _enabled && _texture != null && SkyboxMaterial != null;

        public bool HasImage => _texture != null;

        /// True when the last pick or restore was REFUSED (bad format, unreadable, missing
        /// file). Distinct from "no HDRI loaded", which is the ordinary resting state - the UI
        /// needs to tell those apart to colour the status line, and conflating them is what made
        /// a rejected file look like nothing having happened at all.
        public bool LastLoadFailed { get; private set; }
        public string Path => _path;
        public string FileName => string.IsNullOrEmpty(_path) ? string.Empty : System.IO.Path.GetFileName(_path);
        public string Status => _status;

        public Material SkyboxMaterial
        {
            get
            {
                // Rebuild-if-null rather than build-once: a script recompile DURING Play reloads
                // the domain and drops every [NonSerialized] field, and this one is read from
                // BackgroundController every time the background is re-applied.
                if (_skyboxMaterial == null)
                {
                    Shader shader = Shader.Find("Custom/HdriSkybox");
                    if (shader == null)
                    {
                        Debug.LogWarning("[HDRI] Custom/HdriSkybox shader not found.");
                        return null;
                    }
                    _skyboxMaterial = new Material(shader) { name = "HDRI Skybox (Runtime)" };
                    PushMaterialProperties();
                }
                return _skyboxMaterial;
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set { if (_enabled == value) return; _enabled = value; ApplyAll(); }
        }

        /// Degrees of yaw applied to the HDRI, 0-360. Turns the light around the sculpt without
        /// moving the sculpt or the camera.
        public float Rotation
        {
            get => _rotation;
            set
            {
                float wrapped = Mathf.Repeat(value, 360f);
                if (Mathf.Approximately(_rotation, wrapped)) return;
                _rotation = wrapped;
                PushMaterialProperties();
                _envDirty = true;
            }
        }

        /// Multiplier on the HDRI's own values - brightness of both the visible sky and the
        /// light it casts, since they come from the same material.
        public float Exposure
        {
            get => _exposure;
            set
            {
                float v = Mathf.Clamp(value, 0f, 8f);
                if (Mathf.Approximately(_exposure, v)) return;
                _exposure = v;
                PushMaterialProperties();
                _envDirty = true;
            }
        }

        /// Strength of the diffuse ambient the HDRI contributes (RenderSettings.ambientIntensity).
        public float AmbientIntensity
        {
            get => _ambientIntensity;
            set
            {
                float v = Mathf.Clamp(value, 0f, 3f);
                if (Mathf.Approximately(_ambientIntensity, v)) return;
                _ambientIntensity = v;
                if (IsActive) RenderSettings.ambientIntensity = v;
            }
        }

        /// Strength of the HDRI's specular reflection on the sculpt's material.
        public float ReflectionIntensity
        {
            get => _reflectionIntensity;
            set
            {
                float v = Mathf.Clamp01(value);
                if (Mathf.Approximately(_reflectionIntensity, v)) return;
                _reflectionIntensity = v;
                if (IsActive) RenderSettings.reflectionIntensity = v;
            }
        }

        // ---------------------------------------------------------------------------- loading

        /// Loads `path` as the environment image, switching HDRI lighting on if it succeeds.
        /// Returns false with the reason in Status (and leaves the previous image in place) on
        /// any failure, so a bad pick never blanks out a scene the user was happy with.
        public bool LoadFrom(string path)
        {
            Texture2D loaded = HdriLoader.Load(path, out string error, out bool isProjectAsset);
            if (loaded == null)
            {
                _status = error ?? "Could not load HDRI.";
                LastLoadFailed = true;
                return false;
            }
            LastLoadFailed = false;

            ReleaseTexture();
            _texture = loaded;
            _textureIsProjectAsset = isProjectAsset;
            _path = path;
            _status = $"{System.IO.Path.GetFileName(path)} ({loaded.width}x{loaded.height})";
            _enabled = true;

            PushMaterialProperties();
            ApplyAll();
            return true;
        }

        /// Drops the image and hands the environment back to whatever was driving it before.
        public void Clear()
        {
            ReleaseTexture();
            _path = null;
            _status = "No HDRI loaded.";
            _enabled = false;
            LastLoadFailed = false;
            PushMaterialProperties();
            ApplyAll();
        }

        private void ReleaseTexture()
        {
            if (_texture != null && !_textureIsProjectAsset) Destroy(_texture);
            _texture = null;
            _textureIsProjectAsset = false;
        }

        // --------------------------------------------------------------------------- applying

        private void PushMaterialProperties()
        {
            // Read the field directly, not the SkyboxMaterial property: this is called from
            // inside that property's own lazy build, and going back through it would recurse.
            Material m = _skyboxMaterial;
            if (m == null) return;
            m.SetTexture("_HdriTex", _texture);
            m.SetFloat("_Rotation", _rotation);
            m.SetFloat("_Exposure", _exposure);
        }

        /// Re-points the environment settings and asks the background to re-evaluate which
        /// skybox and clear mode it should be using.
        private void ApplyAll()
        {
            PushMaterialProperties();

            if (IsActive)
            {
                if (!_environmentOverridden)
                {
                    // Remembered so turning the HDRI back off restores the scene's own lighting
                    // settings rather than leaving Skybox-mode ambient over a gradient sky.
                    _savedAmbientMode = RenderSettings.ambientMode;
                    _savedAmbientIntensity = RenderSettings.ambientIntensity;
                    _savedReflectionMode = RenderSettings.defaultReflectionMode;
                    _savedReflectionIntensity = RenderSettings.reflectionIntensity;
                    _savedSkybox = RenderSettings.skybox;
                    _environmentOverridden = true;
                }
                RenderSettings.ambientMode = AmbientMode.Skybox;
                RenderSettings.ambientIntensity = _ambientIntensity;
                RenderSettings.defaultReflectionMode = DefaultReflectionMode.Skybox;
                RenderSettings.reflectionIntensity = _reflectionIntensity;
            }
            else if (_environmentOverridden)
            {
                RenderSettings.ambientMode = _savedAmbientMode;
                RenderSettings.ambientIntensity = _savedAmbientIntensity;
                RenderSettings.defaultReflectionMode = _savedReflectionMode;
                RenderSettings.reflectionIntensity = _savedReflectionIntensity;
                RenderSettings.skybox = _savedSkybox;
                _environmentOverridden = false;
            }

            var background = FindFirstObjectByType<BackgroundController>();
            if (background != null) background.Refresh();

            _envDirty = true;
        }

        private void Update()
        {
            if (!_envDirty) return;
            if (Time.unscaledTime - _lastBakeTime < BakeInterval) return;

            _envDirty = false;
            _lastBakeTime = Time.unscaledTime;
            // Re-renders the skybox into the ambient probe and the default reflection probe.
            // Doing this unconditionally (not only when active) is deliberate: switching the
            // HDRI off has to rebake from whatever skybox took its place, or the sculpt keeps
            // being lit by an environment that is no longer on screen.
            DynamicGI.UpdateEnvironment();
        }

        private void OnDestroy()
        {
            if (_environmentOverridden)
            {
                RenderSettings.ambientMode = _savedAmbientMode;
                RenderSettings.ambientIntensity = _savedAmbientIntensity;
                RenderSettings.defaultReflectionMode = _savedReflectionMode;
                RenderSettings.reflectionIntensity = _savedReflectionIntensity;
                RenderSettings.skybox = _savedSkybox;
                _environmentOverridden = false;
            }
            ReleaseTexture();
            if (_skyboxMaterial != null) Destroy(_skyboxMaterial);
            if (_instance == this) _instance = null;
        }

        // ------------------------------------------------------------------------ persistence

        /// Restores saved settings in one go, loading the image from disk if the file is still
        /// there. Kept as a single call so a load never fires ApplyAll once per property.
        public void ApplySaved(bool enabled, string path, float rotation, float exposure,
                               float ambientIntensity, float reflectionIntensity)
        {
            _rotation = Mathf.Repeat(rotation, 360f);
            _exposure = Mathf.Clamp(exposure, 0f, 8f);
            _ambientIntensity = Mathf.Clamp(ambientIntensity, 0f, 3f);
            _reflectionIntensity = Mathf.Clamp01(reflectionIntensity);

            bool wantImage = !string.IsNullOrEmpty(path);
            bool samePath = wantImage && _path == path && _texture != null;

            if (wantImage && !samePath)
            {
                if (File.Exists(path))
                {
                    // LoadFrom turns the HDRI on as a side effect of a successful pick; the
                    // saved `enabled` flag below is what actually decides, so it is re-applied
                    // afterwards rather than trusted from here.
                    LoadFrom(path);
                }
                else
                {
                    // Drop whatever was loaded before. Keeping it would leave the PREVIOUS
                    // scene's environment lighting the sculpt while the status line claims the
                    // saved one is missing - the two would disagree, and the visible one would
                    // be the wrong answer.
                    ReleaseTexture();
                    _path = path;
                    _status = "Saved HDRI not found: " + System.IO.Path.GetFileName(path);
                    LastLoadFailed = true;
                }
            }
            else if (!wantImage)
            {
                ReleaseTexture();
                _path = null;
                _status = "No HDRI loaded.";
                LastLoadFailed = false;
            }

            _enabled = enabled;
            ApplyAll();
        }
    }
}
