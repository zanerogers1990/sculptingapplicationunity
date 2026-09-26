using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Sculpting
{
    /// A top-level collapsible group in one of the side panels - "Brushes", "Symmetry",
    /// "Environment" and so on. One level above UIFactory.CreateFoldoutSection: a taller header
    /// with an accent stripe in the category's colour, a chevron, and a live one-line summary on
    /// the right ("Clay", "X + Radial 8", "3 objects") so a collapsed category still says what
    /// state it is in. The ordinary foldouts nest inside these as sub-sections.
    ///
    /// Open/closed state is remembered per category in PlayerPrefs, so the panels come back the
    /// way they were left - across a scene-load rebuild and across app restarts.
    ///
    /// Every category registers itself for UICategorySearch, which filters both panels by what
    /// their controls are called.
    public sealed class UICategory : MonoBehaviour
    {
        private const string PrefPrefix = "ui.category.";
        private const float SummaryInterval = 0.2f;

        private static readonly Color CardColor = new Color(0.115f, 0.115f, 0.14f, 0.92f);
        private static readonly Color HeaderBase = new Color(0.15f, 0.15f, 0.185f, 1f);

        private static readonly List<UICategory> _all = new List<UICategory>();
        /// Every live category in both panels, in build order.
        public static IReadOnlyList<UICategory> All => _all;

        private static Sprite _chevron;

        public string Id { get; private set; }
        public string Title { get; private set; }
        /// The content transform - build the category's controls into this.
        public Transform Content { get; private set; }
        public bool IsOpen => Content != null && Content.gameObject.activeSelf;

        /// Extra search words that are not visible labels ("undo", "export"...).
        public string Keywords { get; set; } = string.Empty;

        private Func<string> _summarySource;
        private Text _titleText, _summaryText;
        private Image _headerImage;
        private RectTransform _chevronRect;
        private Color _accent;
        private float _nextSummary;
        private string _shownSummary;

        // While a search is running the category's own open state is parked here and restored
        // when the query is cleared, so searching never rearranges the panels permanently.
        private bool _openBeforeSearch;
        private bool _searchActive;

        /// Builds a category into `parent` (a panel's scrolling content). `id` keys the
        /// remembered open state; `summary` (optional) is polled a few times a second for the
        /// header's right-hand readout.
        public static UICategory Create(Transform parent, string id, string title, Color accent, bool defaultOpen,
                                        Func<string> summary = null, string tooltip = null)
        {
            var root = new GameObject("Category_" + id, typeof(RectTransform), typeof(Image));
            root.transform.SetParent(parent, false);
            root.GetComponent<Image>().color = CardColor;
            var rootLayout = root.AddComponent<VerticalLayoutGroup>();
            rootLayout.spacing = 0;
            rootLayout.childControlWidth = true;
            rootLayout.childControlHeight = true;
            rootLayout.childForceExpandWidth = true;
            rootLayout.childForceExpandHeight = false;

            var category = root.AddComponent<UICategory>();
            category.Id = id;
            category.Title = title;
            category._accent = accent;
            category._summarySource = summary;

            // ------------------------------------------------------------------- header
            var headerGO = new GameObject("Header", typeof(RectTransform), typeof(Image));
            headerGO.transform.SetParent(root.transform, false);
            category._headerImage = headerGO.GetComponent<Image>();
            headerGO.AddComponent<LayoutElement>().preferredHeight = 32;
            var button = headerGO.AddComponent<Button>();
            button.targetGraphic = category._headerImage;
            button.transition = Selectable.Transition.None;
            UIFactory.AddHoverGlow(headerGO);

            var stripe = new GameObject("Accent", typeof(RectTransform), typeof(Image));
            stripe.transform.SetParent(headerGO.transform, false);
            var stripeRect = stripe.GetComponent<RectTransform>();
            stripeRect.anchorMin = new Vector2(0, 0);
            stripeRect.anchorMax = new Vector2(0, 1);
            stripeRect.pivot = new Vector2(0, 0.5f);
            stripeRect.sizeDelta = new Vector2(4, 0);
            var stripeImage = stripe.GetComponent<Image>();
            stripeImage.color = accent;
            stripeImage.raycastTarget = false;

            var chevronGO = new GameObject("Chevron", typeof(RectTransform), typeof(Image));
            chevronGO.transform.SetParent(headerGO.transform, false);
            category._chevronRect = chevronGO.GetComponent<RectTransform>();
            category._chevronRect.anchorMin = category._chevronRect.anchorMax = new Vector2(0, 0.5f);
            category._chevronRect.pivot = new Vector2(0.5f, 0.5f);
            category._chevronRect.anchoredPosition = new Vector2(17, 0);
            category._chevronRect.sizeDelta = new Vector2(10, 10);
            var chevronImage = chevronGO.GetComponent<Image>();
            chevronImage.sprite = ChevronSprite;
            chevronImage.color = new Color(1f, 1f, 1f, 0.75f);
            chevronImage.raycastTarget = false;

            category._titleText = MakeHeaderText(headerGO.transform, title, 14, FontStyle.Bold, TextAnchor.MiddleLeft,
                new Vector2(28, 0), new Vector2(-8, 0));
            category._summaryText = MakeHeaderText(headerGO.transform, string.Empty, 11, FontStyle.Normal, TextAnchor.MiddleRight,
                new Vector2(28, 0), new Vector2(-10, 0));
            category._summaryText.color = new Color(accent.r, accent.g, accent.b, 0.9f);

            // ------------------------------------------------------------------ content
            var contentGO = new GameObject("Content", typeof(RectTransform));
            contentGO.transform.SetParent(root.transform, false);
            var vlg = contentGO.AddComponent<VerticalLayoutGroup>();
            vlg.padding = new RectOffset(10, 8, 8, 12);
            vlg.spacing = 7;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            category.Content = contentGO.transform;

            button.onClick.AddListener(category.OnHeaderClicked);
            TooltipSystem.Attach(headerGO, (string.IsNullOrEmpty(tooltip) ? string.Empty : tooltip + " ") +
                                           "Click to open or close. Ctrl+click opens only this one.");

            bool open = PlayerPrefs.GetInt(PrefPrefix + id, defaultOpen ? 1 : 0) == 1;
            category.ApplyOpen(open);
            return category;
        }

        private static Text MakeHeaderText(Transform parent, string text, int size, FontStyle style, TextAnchor anchor,
                                           Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = offsetMin;
            rect.offsetMax = offsetMax;
            var t = go.AddComponent<Text>();
            t.font = UIFactory.Font;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = anchor;
            t.color = Color.white;
            t.text = text;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        /// A right-pointing triangle, rotated to point down when open. Procedural rather than a
        /// glyph: the built-in LegacyRuntime font is not guaranteed to carry the triangle
        /// characters (see UIFactory.CreateFoldoutSection).
        internal static Sprite ChevronSprite
        {
            get
            {
                if (_chevron != null) return _chevron;
                const int size = 32;
                var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
                {
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        // Triangle with its point at the right edge, centred vertically; a one
                        // pixel ramp on the slanted edges keeps it from looking jagged.
                        float fx = (x + 0.5f) / size, fy = (y + 0.5f) / size;
                        float halfHeight = 0.5f * (1f - fx) * 0.9f;
                        float edge = (halfHeight - Mathf.Abs(fy - 0.5f)) * size;
                        float a = Mathf.Clamp01(edge) * (fx > 0.12f ? 1f : Mathf.Clamp01((fx - 0.06f) * size));
                        pixels[y * size + x] = new Color(1f, 1f, 1f, a);
                    }
                }
                tex.SetPixels(pixels);
                tex.Apply();
                _chevron = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
                return _chevron;
            }
        }

        public void SetTitle(string title)
        {
            Title = title;
            if (_titleText != null) _titleText.text = title;
        }

        /// Opens or closes the category and remembers the choice. Ignored while a search is
        /// running, which drives the open state itself.
        public void SetOpen(bool open)
        {
            if (_searchActive) { _openBeforeSearch = open; ApplyOpen(open); return; }
            ApplyOpen(open);
            PlayerPrefs.SetInt(PrefPrefix + Id, open ? 1 : 0);
        }

        private void ApplyOpen(bool open)
        {
            Content.gameObject.SetActive(open);
            _chevronRect.localEulerAngles = new Vector3(0, 0, open ? -90f : 0f);
            _headerImage.color = open ? Color.Lerp(HeaderBase, _accent, 0.16f) : HeaderBase;
        }

        private void OnHeaderClicked()
        {
            var kb = Keyboard.current;
            bool solo = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            if (!solo)
            {
                SetOpen(!IsOpen);
                return;
            }

            // Ctrl+click: this one open, every other category in the SAME panel closed.
            Transform panel = transform.parent;
            foreach (UICategory other in _all)
                if (other != this && other.transform.parent == panel) other.SetOpen(false);
            SetOpen(true);
        }

        private void OnEnable()
        {
            if (!_all.Contains(this)) _all.Add(this);
        }

        private void OnDestroy()
        {
            _all.Remove(this);
        }

        private void Update()
        {
            if (_summarySource == null || Time.unscaledTime < _nextSummary) return;
            _nextSummary = Time.unscaledTime + SummaryInterval;
            string summary;
            try { summary = _summarySource() ?? string.Empty; }
            catch (Exception) { summary = string.Empty; } // a summary must never take the panel down
            if (summary == _shownSummary) return;
            _shownSummary = summary;
            // Kept short enough never to run into the title on the left (a long file name would).
            _summaryText.text = summary.Length > 22 ? summary.Substring(0, 20) + "..." : summary;
        }

        /// Brings a sub-section into view: opens the category around it and, if `foldoutContent`
        /// is a closed UIFactory foldout, clicks its header. For tools that switch themselves on
        /// from elsewhere (a hotkey, the tool row) and whose controls live in a category.
        public static void Reveal(Transform foldoutContent)
        {
            if (foldoutContent == null) return;
            UICategory category = foldoutContent.GetComponentInParent<UICategory>(true);
            if (category != null && !category.IsOpen) category.SetOpen(true);
            if (foldoutContent.gameObject.activeSelf) return;
            int index = foldoutContent.GetSiblingIndex();
            if (index == 0) return;
            var header = foldoutContent.parent.GetChild(index - 1).GetComponent<Button>();
            if (header != null) header.onClick.Invoke();
        }

        // --------------------------------------------------------------------------- search

        /// Whether every word of `words` appears in this category's title, keywords or any text
        /// inside it (labels, button captions, sub-section titles - collapsed ones included).
        internal bool Matches(string[] words)
        {
            string haystack = (Title + " " + Keywords).ToLowerInvariant();
            foreach (Text t in Content.GetComponentsInChildren<Text>(true))
                haystack += " " + t.text.ToLowerInvariant();
            foreach (string w in words)
                if (!haystack.Contains(w)) return false;
            return true;
        }

        /// Search starts or changes: show and open this category if it matches, hide it if not.
        /// Sub-sections holding a match are opened too, so the control is actually on screen.
        internal void ApplySearch(string[] words)
        {
            if (!_searchActive)
            {
                _searchActive = true;
                _openBeforeSearch = IsOpen;
            }

            bool match = Matches(words);
            gameObject.SetActive(match);
            if (!match) return;
            ApplyOpen(true);
            OpenMatchingFoldouts(words);
        }

        internal void ClearSearch()
        {
            if (!_searchActive) return;
            _searchActive = false;
            gameObject.SetActive(true);
            ApplyOpen(_openBeforeSearch);
        }

        /// UIFactory.CreateFoldoutSection makes a header/content sibling pair named
        /// FoldoutHeader_X / FoldoutContent_X. Any closed content whose text matches gets its
        /// header clicked, so it opens exactly as if the user had done it.
        private void OpenMatchingFoldouts(string[] words)
        {
            foreach (Transform t in Content.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("FoldoutContent_", StringComparison.Ordinal) || t.gameObject.activeSelf) continue;
                string haystack = t.name.Substring("FoldoutContent_".Length).ToLowerInvariant();
                foreach (Text text in t.GetComponentsInChildren<Text>(true)) haystack += " " + text.text.ToLowerInvariant();
                bool all = true;
                foreach (string w in words) if (!haystack.Contains(w)) { all = false; break; }
                if (!all) continue;

                int index = t.GetSiblingIndex();
                if (index == 0) continue;
                Transform header = t.parent.GetChild(index - 1);
                var button = header.GetComponent<Button>();
                if (button != null && header.name.StartsWith("FoldoutHeader_", StringComparison.Ordinal))
                    button.onClick.Invoke();
            }
        }
    }

    /// The "Find a tool" box at the top of the Sculpt panel. Filters the categories of BOTH
    /// panels as you type: a category stays visible (and opens) only if every word of the query
    /// appears somewhere in it, so typing "mirror", "hdri" or "undo" takes you straight there.
    /// Esc or the x button clears it and puts every category back how it was.
    public static class UICategorySearch
    {
        private static string _query = string.Empty;

        public static InputField Create(Transform parent)
        {
            GameObject row = UIFactory.CreateRow(parent, 26f);
            InputField field = UIFactory.CreateInputField(row.transform, string.Empty, null);
            field.GetComponent<LayoutElement>().flexibleWidth = 5f;

            // Placeholder text, dimmed - InputField shows it whenever the field is empty.
            var placeholderGO = new GameObject("Placeholder", typeof(RectTransform));
            placeholderGO.transform.SetParent(field.transform, false);
            var rect = placeholderGO.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6, 0);
            rect.offsetMax = new Vector2(-6, 0);
            var placeholder = placeholderGO.AddComponent<Text>();
            placeholder.font = UIFactory.Font;
            placeholder.fontSize = 11;
            placeholder.fontStyle = FontStyle.Italic;
            placeholder.color = new Color(1f, 1f, 1f, 0.4f);
            placeholder.alignment = TextAnchor.MiddleLeft;
            placeholder.text = "Find a tool (mirror, remesh, hdri...)";
            placeholder.raycastTarget = false;
            field.placeholder = placeholder;

            field.onValueChanged.AddListener(Apply);
            Button clear = UIFactory.CreateButton(row.transform, "x", () => field.text = string.Empty, "Clears the search.");
            var clearLayout = clear.GetComponent<LayoutElement>();
            clearLayout.preferredWidth = 26;
            clearLayout.flexibleWidth = 0;

            field.gameObject.AddComponent<EscClears>().Field = field;
            TooltipSystem.Attach(field.gameObject,
                "Type what you're looking for - every panel section that mentions it opens, the rest step aside. Esc clears it.");

            // A rebuilt panel starts with an empty box; don't leave the other panel filtered.
            if (_query.Length > 0) Apply(string.Empty);
            return field;
        }

        private static void Apply(string text)
        {
            _query = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (_query.Length == 0)
            {
                foreach (UICategory c in UICategory.All) if (c != null) c.ClearSearch();
                return;
            }

            string[] words = _query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            // Copied: a match can open a foldout, which can build content, which must not
            // disturb the enumeration.
            foreach (UICategory c in new List<UICategory>(UICategory.All)) if (c != null) c.ApplySearch(words);
        }

        /// Esc while the box has focus clears it (the field keeps focus, so typing can go on).
        private sealed class EscClears : MonoBehaviour
        {
            public InputField Field;

            private void Update()
            {
                if (Field == null || !Field.isFocused) return;
                var kb = Keyboard.current;
                if (kb != null && kb.escapeKey.wasPressedThisFrame && Field.text.Length > 0) Field.text = string.Empty;
            }
        }
    }
}
