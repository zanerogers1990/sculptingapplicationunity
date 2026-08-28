using UnityEngine;

namespace Sculpting
{
    public enum BackgroundMode { Flat, Gradient, Hdri }

    /// Drives the camera/scene background: a flat solid color, a two-color vertical gradient,
    /// or the loaded HDRI itself. ColorA is the flat color in Flat mode and the gradient's
    /// bottom color in Gradient mode.
    ///
    /// This is the SINGLE writer of RenderSettings.skybox and the camera's clear flags, because
    /// those two settings have to be decided together and they answer different questions:
    ///
    ///   - RenderSettings.skybox is what LIGHTS the scene (Unity bakes the ambient probe and the
    ///     default reflection probe from it), so it follows HdriEnvironmentController: HDRI
    ///     material while an HDRI is active, the gradient skybox otherwise.
    ///   - The camera's clear flags decide what you SEE, which is this component's `mode`.
    ///
    /// Keeping them separate is what lets the user have HDRI lighting with a plain coloured
    /// backdrop. In that combination the camera clears to solid colour - which makes URP skip
    /// its skybox pass, so the HDRI stays in the lighting slot without being drawn - and a
    /// camera-following gradient dome (Custom/GradientSky) paints the backdrop instead. With no
    /// HDRI active the original path is used unchanged: the gradient comes straight from the
    /// skybox, no dome involved.
    public class BackgroundController : MonoBehaviour
    {
        [SerializeField] private BackgroundMode mode = BackgroundMode.Gradient;
        [SerializeField] private Color colorA = new Color(0.10f, 0.11f, 0.14f);
        [SerializeField] private Color colorB = new Color(0.35f, 0.42f, 0.55f);
        [SerializeField, Range(0.2f, 4f)] private float gradientBias = 1f;

        private Camera _camera;
        private BackgroundMode _lastColorMode = BackgroundMode.Gradient;
        [System.NonSerialized] private Material _skyboxMaterial;
        [System.NonSerialized] private Material _domeMaterial;
        [System.NonSerialized] private Transform _dome;

        public BackgroundMode Mode
        {
            get => mode;
            set
            {
                // Remember the last COLOUR backdrop on the way past. Turning the HDRI backdrop
                // off has to put back the Flat or Gradient the scene was actually using - not a
                // hardcoded default - and nothing else in the app is in a position to know which
                // that was.
                if (value != BackgroundMode.Hdri) _lastColorMode = value;
                mode = value;
                Apply();
            }
        }
        public Color ColorA { get => colorA; set { colorA = value; Apply(); } }
        public Color ColorB { get => colorB; set { colorB = value; Apply(); } }
        public float GradientBias { get => gradientBias; set { gradientBias = Mathf.Clamp(value, 0.2f, 4f); Apply(); } }

        /// Re-evaluates everything. Called by HdriEnvironmentController whenever the HDRI is
        /// loaded, cleared, toggled or re-exposed, since any of those changes which skybox
        /// belongs in the slot and whether the gradient needs the dome.
        public void Refresh() => Apply();

        /// True when `mode` cannot actually be honoured - Hdri selected with no HDRI loaded.
        /// The UI uses this to explain why the background did not change.
        public bool HdriBackgroundUnavailable =>
            mode == BackgroundMode.Hdri && !(HdriEnvironmentController.Existing?.IsActive ?? false);

        /// The Flat or Gradient backdrop to come back to when the HDRI backdrop is switched off.
        public BackgroundMode LastColorMode => _lastColorMode;

        private void Awake()
        {
            _camera = Camera.main;
            if (mode != BackgroundMode.Hdri) _lastColorMode = mode;
            Apply();
        }

        private Material SkyboxMaterial
        {
            get
            {
                // Rebuild-if-null: a script recompile DURING Play reloads the domain and drops
                // the runtime material, and Apply() runs on every background change afterwards.
                if (_skyboxMaterial == null)
                {
                    Shader shader = Shader.Find("Custom/GradientSkybox");
                    if (shader == null)
                    {
                        Debug.LogWarning("[Background] Custom/GradientSkybox shader not found; falling back to flat color.");
                        return null;
                    }
                    _skyboxMaterial = new Material(shader) { name = "Gradient Skybox (Runtime)" };
                }
                return _skyboxMaterial;
            }
        }

        private void Apply()
        {
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            HdriEnvironmentController hdri = HdriEnvironmentController.Existing;
            bool hdriActive = hdri != null && hdri.IsActive;

            // --- the lighting slot -------------------------------------------------------
            if (hdriActive)
            {
                RenderSettings.skybox = hdri.SkyboxMaterial;
            }
            else if (mode == BackgroundMode.Gradient && SkyboxMaterial != null)
            {
                // Only assigned in Gradient mode, exactly as before HDRI existed: in Flat mode
                // the scene's own skybox is left alone so its ambient contribution does not
                // change just because the backdrop was set to a solid colour.
                PushGradientProperties(SkyboxMaterial);
                RenderSettings.skybox = SkyboxMaterial;
            }

            // --- what the camera shows ---------------------------------------------------
            // Hdri without an HDRI loaded falls back to the gradient rather than showing
            // whatever stale skybox happens to be in the slot.
            BackgroundMode effective = mode;
            if (effective == BackgroundMode.Hdri && !hdriActive) effective = BackgroundMode.Gradient;
            if (effective == BackgroundMode.Gradient && SkyboxMaterial == null) effective = BackgroundMode.Flat;

            switch (effective)
            {
                case BackgroundMode.Hdri:
                    _camera.clearFlags = CameraClearFlags.Skybox;
                    SetDomeActive(false);
                    break;

                case BackgroundMode.Flat:
                    // No dome needed even under an active HDRI: a solid clear colour already
                    // hides the skybox completely, and going through the camera keeps Flat
                    // pixel-identical to what it was before HDRI support existed.
                    _camera.clearFlags = CameraClearFlags.SolidColor;
                    _camera.backgroundColor = colorA;
                    SetDomeActive(false);
                    break;

                default: // Gradient
                    if (hdriActive)
                    {
                        _camera.clearFlags = CameraClearFlags.SolidColor;
                        _camera.backgroundColor = colorA;
                        SetDomeActive(true);
                    }
                    else
                    {
                        _camera.clearFlags = CameraClearFlags.Skybox;
                        SetDomeActive(false);
                    }
                    break;
            }
        }

        private void PushGradientProperties(Material m)
        {
            m.SetColor("_ColorBottom", colorA);
            m.SetColor("_ColorTop", colorB);
            m.SetFloat("_Bias", gradientBias);
        }

        // ------------------------------------------------------------------- gradient dome

        private void SetDomeActive(bool active)
        {
            if (!active)
            {
                if (_dome != null) _dome.gameObject.SetActive(false);
                return;
            }

            if (_dome == null) BuildDome();
            if (_dome == null) return;

            PushGradientProperties(_domeMaterial);
            if (!_dome.gameObject.activeSelf) _dome.gameObject.SetActive(true);
        }

        private void BuildDome()
        {
            Shader shader = Shader.Find("Custom/GradientSky");
            if (shader == null)
            {
                Debug.LogWarning("[Background] Custom/GradientSky shader not found; background will be flat while an HDRI is lighting the scene.");
                return;
            }

            var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = "BackgroundDome";
            // A collider here would sit between the cursor and the sculpt on every brush
            // raycast. Destroy, not disable, so nothing can turn it back on.
            Collider collider = sphere.GetComponent<Collider>();
            if (collider != null) Destroy(collider);

            var renderer = sphere.GetComponent<MeshRenderer>();
            _domeMaterial = new Material(shader) { name = "Gradient Sky (Runtime)" };
            renderer.sharedMaterial = _domeMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            _dome = sphere.transform;
            _dome.gameObject.SetActive(false);
        }

        private void LateUpdate()
        {
            if (_dome == null || !_dome.gameObject.activeSelf) return;
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            // Recentred every frame so the dome is always concentric with the camera, which is
            // what makes the gradient depend only on view direction. The radius only has to sit
            // between the clip planes - the shader never depth-tests - so half the far plane is
            // an arbitrary safe choice that survives the camera switching to orthographic.
            float radius = Mathf.Clamp(_camera.farClipPlane * 0.5f,
                                       _camera.nearClipPlane * 8f,
                                       _camera.farClipPlane * 0.9f);
            _dome.SetPositionAndRotation(_camera.transform.position, Quaternion.identity);
            _dome.localScale = Vector3.one * (radius * 2f); // built-in sphere is radius 0.5
        }

        private void OnDestroy()
        {
            if (_skyboxMaterial != null) Destroy(_skyboxMaterial);
            if (_domeMaterial != null) Destroy(_domeMaterial);
            if (_dome != null) Destroy(_dome.gameObject);
        }
    }
}
