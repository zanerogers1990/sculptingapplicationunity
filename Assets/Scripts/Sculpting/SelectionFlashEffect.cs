using UnityEngine;

namespace Sculpting
{
    /// Briefly overlays a translucent color across an object's whole surface, fading out over
    /// FlashDuration - the double-click-to-select viewport gesture's confirmation that a NEW
    /// object became the sculpt target (see SculptController.PickObjectUnderCursor). Runs as
    /// its own short-lived, self-destroying component rather than folding a per-object fade
    /// timer into SculptController's already-large Update() loop.
    [RequireComponent(typeof(Renderer))]
    public class SelectionFlashEffect : MonoBehaviour
    {
        private const float FlashDuration = 0.35f;
        private static readonly Color FlashColor = new Color(0.3f, 0.7f, 1f, 0.6f);
        private static Shader _overlayShader;

        private Renderer _renderer;
        private Material _flashMaterial;
        private float _startTime;

        /// Plays the flash on target. Destroys any flash already mid-fade on it first (rapid
        /// re-selection) rather than stacking a second overlay material slot on top of it -
        /// Destroy() is deferred to end-of-frame, so the old instance's own OnDestroy still
        /// runs and cleans up its own slot even though Awake() below has by then already added
        /// a new one alongside it.
        public static void Play(GameObject target)
        {
            SelectionFlashEffect existing = target.GetComponent<SelectionFlashEffect>();
            if (existing != null) Destroy(existing);
            target.AddComponent<SelectionFlashEffect>();
        }

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            if (_overlayShader == null) _overlayShader = Shader.Find("Custom/SelectionFlashOverlay");
            if (_overlayShader == null) { Destroy(this); return; }

            _flashMaterial = new Material(_overlayShader) { color = FlashColor };

            // Extra material slots beyond the mesh's own submesh count re-render submesh 0
            // with each extra material - the same trick outline shaders use - so this overlay
            // needs no second mesh/renderer of its own. `.materials` returns per-renderer
            // instances already, so mutating this array doesn't touch any other object sharing
            // the base material.
            Material[] mats = _renderer.materials;
            var extended = new Material[mats.Length + 1];
            mats.CopyTo(extended, 0);
            extended[mats.Length] = _flashMaterial;
            _renderer.materials = extended;

            _startTime = Time.unscaledTime;
        }

        private void Update()
        {
            float t = (Time.unscaledTime - _startTime) / FlashDuration;
            if (t >= 1f) { Destroy(this); return; }
            if (_flashMaterial != null)
                _flashMaterial.color = new Color(FlashColor.r, FlashColor.g, FlashColor.b, FlashColor.a * (1f - t));
        }

        /// Covers both natural completion (Update destroys this) and interruption (Play()
        /// destroying an in-progress flash, or the GameObject itself being deleted mid-flash) -
        /// either way, the extra material slot and the Material instance must not be left
        /// behind on the renderer.
        private void OnDestroy()
        {
            if (_renderer != null && _flashMaterial != null)
            {
                Material[] mats = _renderer.materials;
                int idx = System.Array.IndexOf(mats, _flashMaterial);
                if (idx >= 0)
                {
                    var trimmed = new Material[mats.Length - 1];
                    for (int i = 0, w = 0; i < mats.Length; i++)
                        if (i != idx) trimmed[w++] = mats[i];
                    _renderer.materials = trimmed;
                }
            }
            if (_flashMaterial != null) Destroy(_flashMaterial);
        }
    }
}
