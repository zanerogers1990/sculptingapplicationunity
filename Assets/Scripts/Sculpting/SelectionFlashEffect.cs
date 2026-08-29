using UnityEngine;

namespace Sculpting
{
    /// Briefly overlays a translucent color across an object's whole surface, fading out over
    /// its own duration - originally just the double-click-to-select viewport gesture's
    /// confirmation that a NEW object became the sculpt target (see
    /// SculptController.PickObjectUnderCursor), now reused with a shorter duration/different
    /// color for the Undo/Redo surface flash (see SculptController.TriggerUndoRedoFeedback).
    /// Runs as its own short-lived, self-destroying component rather than folding a per-object
    /// fade timer into SculptController's already-large Update() loop.
    [RequireComponent(typeof(Renderer))]
    public class SelectionFlashEffect : MonoBehaviour
    {
        private const float DefaultFlashDuration = 0.35f;
        private static readonly Color DefaultFlashColor = new Color(0.3f, 0.7f, 1f, 0.6f);
        private static Shader _overlayShader;

        private Renderer _renderer;
        private Material _flashMaterial;
        private float _startTime;
        private float _duration;
        private Color _flashColor;

        /// Plays the flash on target. Destroys any flash already mid-fade on it first (rapid
        /// re-selection, or an undo right after a select) rather than stacking a second overlay
        /// material slot on top of it - Destroy() is deferred to end-of-frame, so the old
        /// instance's own OnDestroy still runs and cleans up its own slot even though Init()
        /// below has by then already added a new one alongside it. duration/color default to
        /// the original selection-flash look when omitted.
        public static void Play(GameObject target, float duration = -1f, Color? color = null)
        {
            SelectionFlashEffect existing = target.GetComponent<SelectionFlashEffect>();
            if (existing != null) Destroy(existing);
            SelectionFlashEffect effect = target.AddComponent<SelectionFlashEffect>();
            effect.Init(duration > 0f ? duration : DefaultFlashDuration, color ?? DefaultFlashColor);
        }

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
        }

        // Split out from Awake() so Play() can hand over the real duration/color BEFORE any
        // Update() tick reads them - AddComponent<T>() runs Awake() synchronously, so setting
        // fields on the returned instance afterward would otherwise leave Update()'s very first
        // frame using stale defaults instead of what was actually asked for.
        private void Init(float duration, Color color)
        {
            _duration = duration;
            _flashColor = color;

            if (_overlayShader == null) _overlayShader = Shader.Find("Custom/SelectionFlashOverlay");
            if (_overlayShader == null) { Destroy(this); return; }

            _flashMaterial = new Material(_overlayShader) { color = _flashColor };

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
            float t = (Time.unscaledTime - _startTime) / _duration;
            if (t >= 1f) { Destroy(this); return; }
            if (_flashMaterial != null)
                _flashMaterial.color = new Color(_flashColor.r, _flashColor.g, _flashColor.b, _flashColor.a * (1f - t));
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
