using UnityEngine;

namespace Sculpting
{
    /// Builds the merged top-right "Studio" panel: three collapsible sections - Studio
    /// Lighting, Material, Presentation - that used to be three separate always-open panels
    /// (top-right, bottom-center, bottom-right respectively). Merging them means one panel to
    /// move instead of three, and each section starts collapsed so the panel stays small until
    /// the user actually opens the one they want - opening several at once (e.g. Studio
    /// Lighting plus Presentation's own Bloom sub-section) is exactly the case the panel's
    /// scrollbar (see UIFactory.CreateScrollingPanelCanvas) exists for.
    ///
    /// This class owns only the panel shell (canvas, section headers, scrolling) - each
    /// section's actual controls still come from that panel's original builder via
    /// BuildContent, so none of their brush/light/material logic moves.
    public class StudioPanelUIBuilder : MonoBehaviour
    {
        private const float PanelWidth = 260f;

        // Start(), not Awake() - matches every other *UIBuilder in this project (see
        // SculptUIBuilder's remarks): the controllers each section resolves inside its own
        // BuildContent can depend on other objects' OnEnable/Awake having already run.
        private void Start()
        {
            var lighting = GetComponent<LightingUIBuilder>();
            var material = GetComponent<MaterialUIBuilder>();
            var presentation = GetComponent<PostProcessingUIBuilder>();

            float maxHeight = Mathf.Max(300f, Screen.height - 40f);
            Transform panel = UIFactory.CreateScrollingPanelCanvas(
                "StudioCanvas", new Vector2(1, 1), new Vector2(-12, -12), PanelWidth, maxHeight);

            if (lighting != null)
                lighting.BuildContent(UIFactory.CreateFoldoutSection(panel, "Studio Lighting", false));
            if (material != null)
                material.BuildContent(UIFactory.CreateFoldoutSection(panel, "Material", false));
            if (presentation != null)
                presentation.BuildContent(UIFactory.CreateFoldoutSection(panel, "Presentation", false));
        }
    }
}
