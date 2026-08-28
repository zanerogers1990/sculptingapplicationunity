using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sculpting
{
    /// The corner view gizmo: six axis cones around a centre cube that always show which way
    /// the camera is pointing, and snap it to an axis view when clicked - Unity's own scene
    /// gizmo, which is what the app was missing to make "look at this from the side" a click
    /// instead of an orbit. Clicking an axis also switches to an orthographic projection, since
    /// an axis view is only worth snapping to if it is actually square-on; the Persp/Ortho
    /// button underneath (and the centre cube) toggle that back by hand.
    ///
    /// Drawn as flat uGUI on its own screen-space canvas rather than as real 3D geometry, which
    /// a gizmo like this normally is. Real geometry would need a second camera with its own
    /// viewport, layer and depth clear - awkward under URP, where a second base camera drawing
    /// into a corner of the same target is not a supported arrangement - whereas the gizmo's
    /// entire content is six directions projected onto the screen, which is two components of a
    /// camera-space vector (see Refresh). The third component becomes the draw order, the
    /// fade, and the cone/disc crossfade that stands in for a cone seen end-on.
    public class ViewGizmoUIBuilder : MonoBehaviour
    {
        private const string CanvasName = "ViewGizmoCanvas";

        // Inset far enough from the right edge to clear SceneGraphUIBuilder's panel, which is
        // docked flush to that edge at 260px wide. Top-right is where this gizmo belongs (it is
        // where every DCC puts one) and the panel is the only thing already there.
        private const float RightPanelWidth = 260f;
        private const float ScreenMargin = 14f;
        private const float GizmoBox = 104f;
        // Distance from the gizmo's centre to a handle pointing straight across the screen.
        // GizmoBox has to leave room for this plus half a handle on either side.
        private const float AxisRadius = 34f;
        private const float HandleSize = 28f;
        private const float ConeSize = 25f;
        private const float DiscSize = 17f;
        private const float CentreSize = 17f;

        // +X, +Y, +Z then their opposites, so axis % 3 indexes the colours and axis >= 3 is the
        // test for "this is a negative axis" (unlabelled and greyed, as Unity draws them).
        private static readonly Vector3[] AxisDirections =
        {
            Vector3.right, Vector3.up, Vector3.forward,
            Vector3.left, Vector3.down, Vector3.back
        };

        // Same X red / Y green / Z blue as TransformGizmo's handles - the two gizmos describe
        // the same three axes and would be actively confusing in different colours.
        private static readonly Color[] AxisColors =
        {
            new Color(1f, 0.25f, 0.25f), new Color(0.35f, 1f, 0.35f), new Color(0.3f, 0.55f, 1f)
        };

        private static readonly string[] AxisLabels = { "X", "Y", "Z", "", "", "" };

        // Yaw that puts the camera ON each axis looking back at the pivot: the rig's forward is
        // Euler(pitch, yaw, 0) * (0,0,1), so +X (camera to the right of the model, looking left)
        // needs a forward of -X, which is yaw -90. The Y entries are unused - a top or bottom
        // view keeps whatever yaw the user was already at, so that snapping up and back down
        // returns to the side they were working from rather than to some arbitrary heading.
        private static readonly float[] AxisYaw = { -90f, 0f, 180f, 90f, 0f, 0f };
        private static readonly float[] AxisPitch = { 0f, 90f, 0f, 0f, -90f, 0f };

        /// Hover tracking for one handle. uGUI's own Selectable tint only reaches a single
        /// target graphic, and a handle is three graphics (cone, disc, label) whose colours this
        /// builder already rewrites every frame - so the highlight is applied there, from a flag
        /// this sets, rather than fought over with the Button's transition.
        private class ViewGizmoHandle : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public bool Hovered { get; private set; }
            public void OnPointerEnter(PointerEventData eventData) => Hovered = true;
            public void OnPointerExit(PointerEventData eventData) => Hovered = false;
        }

        // Shared by every handle and tinted per-axis through Image.color, so the six cones cost
        // one texture between them. Static so a panel rebuild reuses them instead of leaking a
        // fresh pair each time; the != null test also covers their loss to a domain reload.
        private static Sprite _coneSprite;
        private static Sprite _discSprite;

        private CameraOrbitController _orbit;
        private GameObject _canvasRoot;

        private readonly RectTransform[] _handleRects = new RectTransform[6];
        private readonly Image[] _cones = new Image[6];
        private readonly Image[] _discs = new Image[6];
        private readonly Text[] _labels = new Text[6];
        private readonly RectTransform[] _lines = new RectTransform[6];
        private readonly Image[] _lineImages = new Image[6];
        private readonly ViewGizmoHandle[] _hovers = new ViewGizmoHandle[7];
        private RectTransform _centreRect;
        private Image _centreImage;
        private Image _projImage;
        private Text _projText;

        // Scratch for the per-frame depth sort - the seventh entry is the centre cube.
        private readonly int[] _order = new int[7];
        private readonly float[] _depth = new float[7];

        private void Start()
        {
            _orbit = FindFirstObjectByType<CameraOrbitController>();
            BuildUI();
        }

        /// LateUpdate, not Update: CameraOrbitController moves the camera in Update, and a gizmo
        /// that reports where the camera was last frame visibly lags the orbit it is describing.
        private void LateUpdate()
        {
            if (_canvasRoot == null) BuildUI();
            if (_orbit == null) _orbit = FindFirstObjectByType<CameraOrbitController>();
            Refresh();
        }

        private void BuildUI()
        {
            // Same idiom as every other panel here: root-level canvas under a fixed name,
            // destroying any leftover from a previous build first - see UIFactory's
            // DestroyStaleCanvas remarks for why a rebuild would otherwise stack two copies.
            GameObject stale = GameObject.Find(CanvasName);
            if (stale != null) DestroyImmediate(stale);

            var canvasGO = new GameObject(CanvasName, typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasGO.AddComponent<GraphicRaycaster>();
            _canvasRoot = canvasGO;

            var gizmoGO = new GameObject("Gizmo", typeof(RectTransform));
            gizmoGO.transform.SetParent(canvasGO.transform, false);
            var gizmoRect = gizmoGO.GetComponent<RectTransform>();
            gizmoRect.anchorMin = gizmoRect.anchorMax = gizmoRect.pivot = new Vector2(1f, 1f);
            gizmoRect.sizeDelta = new Vector2(GizmoBox, GizmoBox);
            gizmoRect.anchoredPosition = new Vector2(-(RightPanelWidth + ScreenMargin), -ScreenMargin);

            // Everything else in here is depth-sorted every frame; the axis lines are not, they
            // just live behind the lot in their own container at sibling index 0. A line to a
            // handle behind the cube reads correctly under it, and a line to one in front reads
            // as emerging from it, so sorting them individually would buy nothing.
            var linesGO = new GameObject("Lines", typeof(RectTransform));
            linesGO.transform.SetParent(gizmoRect, false);
            var linesRect = linesGO.GetComponent<RectTransform>();
            linesRect.anchorMin = Vector2.zero;
            linesRect.anchorMax = Vector2.one;
            linesRect.sizeDelta = Vector2.zero;

            for (int i = 0; i < 6; i++) BuildLine(linesRect, i);
            BuildCentre(gizmoRect);
            for (int i = 0; i < 6; i++) BuildHandle(gizmoRect, i);

            BuildProjectionButton(gizmoRect);
            Refresh();
        }

        private void BuildLine(RectTransform parent, int axis)
        {
            var go = new GameObject("Line" + axis, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            // Pivot at the bottom edge so the line grows outward from the gizmo's centre and
            // rotates about it - length and angle then fully describe it (see Refresh).
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(2f, AxisRadius);
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            _lines[axis] = rect;
            _lineImages[axis] = img;
        }

        private void BuildCentre(RectTransform parent)
        {
            var go = new GameObject("Centre", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = new Vector2(CentreSize, CentreSize);

            var img = go.GetComponent<Image>();
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(ToggleProjection);
            _hovers[6] = go.AddComponent<ViewGizmoHandle>();
            _centreRect = rect;
            _centreImage = img;
        }

        private void BuildHandle(RectTransform parent, int axis)
        {
            // The clickable rect is a hair larger than the cone drawn inside it: these targets
            // are ~25px across and partly overlap each other, and a click that lands between
            // two cones should still hit the one it was aimed at.
            var go = new GameObject("Axis" + axis, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(HandleSize, HandleSize);

            var hitbox = go.GetComponent<Image>();
            hitbox.color = new Color(0f, 0f, 0f, 0f); // invisible, but still raycast-hit
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = hitbox;
            btn.transition = Selectable.Transition.None;
            int captured = axis;
            btn.onClick.AddListener(() => SnapToAxis(captured));
            _hovers[axis] = go.AddComponent<ViewGizmoHandle>();

            var coneGO = new GameObject("Cone", typeof(RectTransform), typeof(Image));
            coneGO.transform.SetParent(rect, false);
            var coneRect = coneGO.GetComponent<RectTransform>();
            coneRect.anchorMin = coneRect.anchorMax = coneRect.pivot = new Vector2(0.5f, 0.5f);
            coneRect.sizeDelta = new Vector2(ConeSize, ConeSize);
            var cone = coneGO.GetComponent<Image>();
            cone.sprite = ConeSprite;
            cone.raycastTarget = false;

            var discGO = new GameObject("Disc", typeof(RectTransform), typeof(Image));
            discGO.transform.SetParent(rect, false);
            var discRect = discGO.GetComponent<RectTransform>();
            discRect.anchorMin = discRect.anchorMax = discRect.pivot = new Vector2(0.5f, 0.5f);
            discRect.sizeDelta = new Vector2(DiscSize, DiscSize);
            var disc = discGO.GetComponent<Image>();
            disc.sprite = DiscSprite;
            disc.raycastTarget = false;

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(rect, false);
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.sizeDelta = Vector2.zero;
            var label = labelGO.AddComponent<Text>();
            label.font = UIFactory.Font;
            label.fontSize = 11;
            label.fontStyle = FontStyle.Bold;
            label.alignment = TextAnchor.MiddleCenter;
            label.raycastTarget = false;
            label.text = AxisLabels[axis];
            // Dark, not white: the letter sits on a fully saturated red/green/blue cone, and
            // white on green is the one combination that disappears.
            label.color = new Color(0.06f, 0.06f, 0.08f);

            _handleRects[axis] = rect;
            _cones[axis] = cone;
            _discs[axis] = disc;
            _labels[axis] = label;
        }

        private void BuildProjectionButton(RectTransform gizmoRect)
        {
            Button btn = UIFactory.CreateButton(gizmoRect, "Persp", ToggleProjection);
            var rect = btn.GetComponent<RectTransform>();
            // Hung off the gizmo's own rect rather than the canvas, so the pair moves together
            // if the gizmo is ever repositioned - and directly under it, where Unity puts the
            // same label.
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -2f);
            rect.sizeDelta = new Vector2(76f, 22f);
            _projImage = btn.GetComponent<Image>();
            _projText = btn.GetComponentInChildren<Text>();
        }

        private void Refresh()
        {
            Camera cam = Camera.main;
            if (cam == null || _handleRects[0] == null) return;

            // Each axis, expressed in camera space: x/y place the handle on screen, z is how far
            // it points away from the viewer, and that is the whole gizmo.
            Quaternion inverseView = Quaternion.Inverse(cam.transform.rotation);

            for (int i = 0; i < 6; i++)
            {
                Vector3 v = inverseView * AxisDirections[i];
                var dir = new Vector2(v.x, v.y);
                float spread = dir.magnitude; // 1 = across the screen, 0 = straight at/away from us

                // 0 when the axis points at the viewer, 1 when it points away.
                float depth01 = Mathf.InverseLerp(-1f, 1f, v.z);
                bool negative = i >= 3;
                bool hovered = _hovers[i].Hovered;

                RectTransform rect = _handleRects[i];
                rect.anchoredPosition = dir * AxisRadius;
                // Near handles read larger, the way the near end of a real 3D gizmo would, and
                // the hovered one grows a little further to confirm what a click would hit.
                rect.localScale = Vector3.one * (Mathf.Lerp(1f, 0.74f, depth01) * (hovered ? 1.12f : 1f));

                Color tint = negative
                    ? Color.Lerp(AxisColors[i - 3], new Color(0.44f, 0.44f, 0.48f), 0.68f)
                    : AxisColors[i];
                if (hovered) tint = Color.Lerp(tint, Color.white, 0.45f);
                float alpha = Mathf.Lerp(1f, 0.5f, depth01);

                // A cone seen from the side is a triangle and seen end-on is a circle. Rather
                // than model one, crossfade the two silhouettes on how much of the axis survives
                // the projection - which is exactly what makes the real thing change shape.
                float sideOn = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.12f, 0.45f, spread));
                SetColor(_cones[i], tint, alpha * sideOn);
                SetColor(_discs[i], tint, alpha * (1f - sideOn));
                // The cone points outward along its axis; the letter stays upright regardless,
                // as a rotating letter would be unreadable at this size.
                float angle = spread > 0.0001f ? Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg - 90f : 0f;
                _cones[i].rectTransform.localRotation = Quaternion.Euler(0f, 0f, angle);

                Color labelColor = _labels[i].color;
                labelColor.a = negative ? 0f : alpha;
                _labels[i].color = labelColor;

                _lines[i].sizeDelta = new Vector2(2f, spread * AxisRadius);
                _lines[i].localRotation = Quaternion.Euler(0f, 0f, angle);
                SetColor(_lineImages[i], tint, alpha * 0.4f);

                _order[i] = i;
                _depth[i] = v.z;
            }

            // The cube sits at the origin, so it is in front of every axis pointing away and
            // behind every axis pointing towards us - a depth of exactly zero puts it there.
            _order[6] = 6;
            _depth[6] = 0f;
            SortByDepthAndApply();

            Color centreTint = _hovers[6].Hovered ? new Color(0.85f, 0.85f, 0.9f) : new Color(0.58f, 0.58f, 0.64f);
            SetColor(_centreImage, centreTint, 1f);

            bool ortho = _orbit != null && _orbit.Orthographic;
            if (_projText != null) _projText.text = ortho ? "Ortho" : "Persp";
            if (_projImage != null) _projImage.color = ortho ? UIFactory.ActiveColor : UIFactory.InactiveColor;
        }

        /// Draws the seven pieces back to front. uGUI has no depth within a canvas beyond
        /// sibling order, so this IS the gizmo's occlusion - and it doubles as the hit order,
        /// since a raycast picks the topmost graphic: the handle drawn nearest the viewer is
        /// also the one a click on two overlapping cones lands on, which is what the eye expects.
        private void SortByDepthAndApply()
        {
            for (int i = 1; i < 7; i++)
            {
                int item = _order[i];
                float key = _depth[i];
                int j = i - 1;
                while (j >= 0 && _depth[j] < key)
                {
                    _depth[j + 1] = _depth[j];
                    _order[j + 1] = _order[j];
                    j--;
                }
                _depth[j + 1] = key;
                _order[j + 1] = item;
            }

            for (int i = 0; i < 7; i++)
            {
                Transform t = _order[i] == 6 ? _centreRect : (Transform)_handleRects[_order[i]];
                t.SetSiblingIndex(i + 1); // index 0 is the Lines container, which stays behind
            }
        }

        private static void SetColor(Graphic graphic, Color rgb, float alpha)
        {
            rgb.a = alpha;
            graphic.color = rgb;
        }

        private void SnapToAxis(int axis)
        {
            if (_orbit == null) return;

            float yaw = axis == 1 || axis == 4 ? _orbit.TargetYaw : AxisYaw[axis];
            float pitch = AxisPitch[axis];

            // Clicking the axis you are already looking down flips to the opposite side, same as
            // Unity's gizmo - otherwise the second click on a cone is a no-op, and getting to the
            // back of the model would mean hunting for the unlabelled grey cone hidden behind it.
            if (Mathf.Abs(Mathf.DeltaAngle(_orbit.TargetYaw, yaw)) < 0.5f &&
                Mathf.Abs(Mathf.DeltaAngle(_orbit.TargetPitch, pitch)) < 0.5f)
            {
                if (axis == 1 || axis == 4) pitch = -pitch;
                else yaw += 180f;
            }

            // An axis view is the one view worth being square-on, which perspective never is:
            // it is the projection that makes a side view read as a silhouette to sculpt against
            // rather than as a slightly-turned three-quarter view.
            _orbit.Orthographic = true;
            _orbit.SnapToView(yaw, pitch);
        }

        private void ToggleProjection()
        {
            if (_orbit != null) _orbit.Orthographic = !_orbit.Orthographic;
        }

        private static Sprite ConeSprite
        {
            get
            {
                if (_coneSprite == null) _coneSprite = BuildSprite(48, ConeCoverage);
                return _coneSprite;
            }
        }

        private static Sprite DiscSprite
        {
            get
            {
                if (_discSprite == null) _discSprite = BuildSprite(48, DiscCoverage);
                return _discSprite;
            }
        }

        /// White RGB with the shape in the alpha, so one texture serves all six handles under
        /// six different Image.color tints. Coverage is sampled as a hard in/out test on a grid
        /// of sub-pixels and averaged, which is the cheapest way to get an antialiased edge -
        /// and an aliased cone at 25px would be the first thing anyone noticed about this gizmo.
        private static Sprite BuildSprite(int size, Func<Vector2, float> coverage)
        {
            const int subSamples = 3;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            var pixels = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float acc = 0f;
                    for (int sy = 0; sy < subSamples; sy++)
                    {
                        for (int sx = 0; sx < subSamples; sx++)
                        {
                            var uv = new Vector2(
                                (x + (sx + 0.5f) / subSamples) / size,
                                (y + (sy + 0.5f) / subSamples) / size);
                            acc += coverage(uv);
                        }
                    }

                    byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(acc / (subSamples * subSamples)) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, a);
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f));
        }

        /// Triangle with its apex at the top of the sprite, so a handle rotated to face along
        /// its axis points outward.
        private static float ConeCoverage(Vector2 uv)
        {
            var apex = new Vector2(0.5f, 0.97f);
            var left = new Vector2(0.11f, 0.16f);
            var right = new Vector2(0.89f, 0.16f);
            return Inside(uv, apex, left, right) ? 1f : 0f;
        }

        private static float DiscCoverage(Vector2 uv) =>
            Vector2.Distance(uv, new Vector2(0.5f, 0.5f)) <= 0.46f ? 1f : 0f;

        private static bool Inside(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(p - a, b - a);
            float d2 = Cross(p - b, c - b);
            float d3 = Cross(p - c, a - c);
            bool anyNegative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool anyPositive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(anyNegative && anyPositive);
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    }
}
