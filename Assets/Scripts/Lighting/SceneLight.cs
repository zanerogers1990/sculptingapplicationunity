using UnityEngine;

namespace Sculpting
{
    /// One user-placed light: the Light itself, plus the small marker that makes it visible and
    /// clickable in the viewport.
    ///
    /// A Light has no geometry of its own, so without a marker there is nothing on screen to aim
    /// at and nothing for a raycast to hit - which is the whole reason the studio rig never let
    /// you touch its lights directly and drove them off yaw/pitch sliders instead. The marker is a
    /// real primitive with its collider kept (the same picking idiom TransformGizmo's handles and
    /// ZSphereController's rig spheres both use) and is shown only while a transform tool is up,
    /// so an ordinary sculpting view stays clean.
    [RequireComponent(typeof(Light))]
    public class SceneLight : MonoBehaviour
    {
        /// Radius of the clickable bulb marker, in world units. Fixed rather than scaled to the
        /// scene: it is a control, not part of the model, and one that shrank with the subject
        /// would be unpickable on a small sculpt.
        private const float MarkerRadius = 0.09f;
        // Length of the direction shaft, as a multiple of the bulb radius. Long enough to read as
        // "this is which way it points" from across the viewport, short enough not to spear the
        // model it is aimed at.
        private const float ShaftLength = 3.2f;
        private const float ShaftThickness = 0.22f;

        private Light _light;
        private Transform _marker;
        private Transform _shaft;
        private Renderer _markerRenderer;
        private Renderer _shaftRenderer;
        private Material _markerMaterial;
        private bool _selected;

        public Light Light => _light != null ? _light : (_light = GetComponent<Light>());

        /// Tint the marker draws in when this light is not selected - the light's own colour, so a
        /// scene full of lights reads at a glance.
        private static readonly Color SelectedTint = new Color(1f, 0.62f, 0.2f);

        private void Awake()
        {
            _light = GetComponent<Light>();
            BuildMarker();
        }

        private void OnDestroy()
        {
            // The marker's material is instantiated per light (each one draws its own colour), so
            // nothing else can free it - the same ownership rule SculptableMesh applies to its own
            // runtime mesh.
            if (_markerMaterial != null) Destroy(_markerMaterial);
        }

        private void BuildMarker()
        {
            GameObject bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bulb.name = "LightMarker";
            _marker = bulb.transform;
            _marker.SetParent(transform, false);
            _marker.localPosition = Vector3.zero;
            _marker.localScale = Vector3.one * (MarkerRadius * 2f);
            _markerRenderer = bulb.GetComponent<Renderer>();

            // Kept (unlike MirrorController's decorative planes, which destroy theirs): this
            // collider IS how the light gets picked - see SceneLightManager.Raycast.
            var collider = bulb.GetComponent<Collider>();
            if (collider != null) collider.isTrigger = false;

            GameObject shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            shaft.name = "LightDirection";
            _shaft = shaft.transform;
            _shaft.SetParent(transform, false);
            // Unity's cylinder runs along its local Y and is 2 units tall at unit scale, so a
            // half-length scale plus a rotation puts it along local +Z - the direction a Light
            // actually shines.
            _shaft.localRotation = Quaternion.Euler(90f, 0f, 0f);
            _shaft.localScale = new Vector3(MarkerRadius * ShaftThickness,
                                            MarkerRadius * ShaftLength * 0.5f,
                                            MarkerRadius * ShaftThickness);
            _shaft.localPosition = new Vector3(0f, 0f, MarkerRadius * ShaftLength * 0.5f);
            _shaftRenderer = shaft.GetComponent<Renderer>();
            // The shaft is decoration only - picking the bulb is what selects the light, and a
            // collider out along the beam would be grabbable from far away for no reason.
            Collider shaftCollider = shaft.GetComponent<Collider>();
            if (shaftCollider != null) Destroy(shaftCollider);

            _markerMaterial = BuildMarkerMaterial();
            _markerRenderer.sharedMaterial = _markerMaterial;
            _shaftRenderer.sharedMaterial = _markerMaterial;
            foreach (Renderer r in new[] { _markerRenderer, _shaftRenderer })
            {
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            RefreshMarker();
        }

        // Same always-on-top overlay shader the gizmo handles use, for the same reason: a light
        // placed inside or behind the model is still a control the user has to be able to find
        // and click.
        private static readonly int ColorId = Shader.PropertyToID("_Color");

        private static Material BuildMarkerMaterial()
        {
            Shader overlay = Shader.Find("Custom/BrushPreviewOverlay");
            return overlay != null ? new Material(overlay) : new Material(Shader.Find("Sprites/Default"));
        }

        /// Shows or hides the marker without touching the LIGHT - a hidden marker still lights the
        /// scene, which is what makes it right to hide them all while sculpting.
        public void SetMarkerVisible(bool visible)
        {
            if (_marker == null) return;
            if (_marker.gameObject.activeSelf != visible) _marker.gameObject.SetActive(visible);
            // Only a directional or spot light has a meaningful direction; a point light shines
            // every way at once, so a shaft on one would be a lie about what rotating it does.
            bool wantShaft = visible && Light != null && Light.type != LightType.Point;
            if (_shaft.gameObject.activeSelf != wantShaft) _shaft.gameObject.SetActive(wantShaft);
        }

        public void SetSelected(bool selected)
        {
            if (_selected == selected) return;
            _selected = selected;
            RefreshMarker();
        }

        /// Re-reads the light's own colour into the marker. Called on any change to the light so
        /// the marker never describes a colour the light no longer has.
        public void RefreshMarker()
        {
            if (_markerMaterial == null || Light == null) return;

            // Selected wins over the light's own colour: while several lights are selected, which
            // ones they are matters more than what colour each one is.
            Color tint = _selected ? SelectedTint : Light.color;
            // Lifted away from black so a deliberately dim or deep-coloured light still has a
            // visible marker - the marker is a control, not a readout of intensity.
            tint = Color.Lerp(tint, Color.white, 0.25f);
            tint.a = 1f;
            _markerMaterial.SetColor(ColorId, tint);
            _markerMaterial.color = tint; // Sprites/Default fallback path
        }
    }
}
