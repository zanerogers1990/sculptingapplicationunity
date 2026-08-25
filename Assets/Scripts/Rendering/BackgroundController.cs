using UnityEngine;

namespace Sculpting
{
    public enum BackgroundMode { Flat, Gradient }

    /// Drives the camera/scene background: either a flat solid color, or a two-color
    /// vertical gradient rendered via a procedural skybox shader (Assets/Shaders/
    /// GradientSkybox.shader) so it still reads correctly from any orbit angle. ColorA is
    /// the flat color in Flat mode and the gradient's bottom color in Gradient mode.
    public class BackgroundController : MonoBehaviour
    {
        [SerializeField] private BackgroundMode mode = BackgroundMode.Gradient;
        [SerializeField] private Color colorA = new Color(0.10f, 0.11f, 0.14f);
        [SerializeField] private Color colorB = new Color(0.35f, 0.42f, 0.55f);
        [SerializeField, Range(0.2f, 4f)] private float gradientBias = 1f;

        private Camera _camera;
        private Material _skyboxMaterial;

        public BackgroundMode Mode { get => mode; set { mode = value; Apply(); } }
        public Color ColorA { get => colorA; set { colorA = value; Apply(); } }
        public Color ColorB { get => colorB; set { colorB = value; Apply(); } }
        public float GradientBias { get => gradientBias; set { gradientBias = Mathf.Clamp(value, 0.2f, 4f); Apply(); } }

        private void Awake()
        {
            _camera = Camera.main;
            Shader shader = Shader.Find("Custom/GradientSkybox");
            if (shader != null) _skyboxMaterial = new Material(shader) { name = "Gradient Skybox (Runtime)" };
            else Debug.LogWarning("[Background] Custom/GradientSkybox shader not found; falling back to flat color.");

            Apply();
        }

        private void Apply()
        {
            if (_camera == null) _camera = Camera.main;
            if (_camera == null) return;

            if (mode == BackgroundMode.Flat || _skyboxMaterial == null)
            {
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = colorA;
                return;
            }

            _skyboxMaterial.SetColor("_ColorBottom", colorA);
            _skyboxMaterial.SetColor("_ColorTop", colorB);
            _skyboxMaterial.SetFloat("_Bias", gradientBias);
            RenderSettings.skybox = _skyboxMaterial;
            _camera.clearFlags = CameraClearFlags.Skybox;
        }
    }
}
