using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Sculpting
{
    /// Live settings for the screen-space cavity. A viewport display setting (as in Blender's
    /// Viewport Shading popover), not a per-material one, so it lives here rather than in the
    /// material's cbuffer. SculptMaterialController owns the persisted values and pushes them
    /// in; the defaults below match its field defaults so a mid-Play recompile - which resets
    /// statics but not the controller - doesn't visibly change anything.
    public static class ScreenCavity
    {
        public static bool Enabled = true;
        public static float Ridge = 1f;
        public static float Valley = 1f;
    }

    /// Blender Workbench's "Cavity - Screen" (the curvature half of its cavity effect) for URP.
    ///
    /// Before opaques, every SculptPBR mesh is drawn once more through its "SculptCavityNormals"
    /// pass into a half-float buffer: view-space normal in xyz, eye depth in w (0 = nothing drawn).
    /// SculptPBR's forward pass then reads that buffer one pixel up/down/left/right of itself and
    /// turns the spread of the normals - how fast the surface is turning on screen - into a
    /// brighten-ridges / darken-valleys multiplier, exactly as Workbench's curvature_compute does.
    ///
    /// Why a buffer of our own rather than URP's _CameraNormalsTexture (which SSAO already makes):
    /// that texture is 8-bit SNORM. Curvature is a DIFFERENCE of neighbouring normals, and on a
    /// smooth surface a one-pixel step changes a normal by far less than 1/127, so 8 bits turns
    /// gentle curvature into a +-2% dither pattern. Blender keeps its normal buffer in 16-bit
    /// float for the same reason. Doing the composite in the sculpt shader instead of as a
    /// full-screen pass also keeps gizmos, grids and previews out of it.
    public class ScreenCavityFeature : ScriptableRendererFeature
    {
        private ScreenCavityPass _pass;
        // Lure plastic's back-face depth - see PlasticThicknessPass for why it rides along here.
        private PlasticThicknessPass _thicknessPass;

        public override void Create()
        {
            _pass = new ScreenCavityPass { renderPassEvent = RenderPassEvent.AfterRenderingPrePasses };
            _thicknessPass = new PlasticThicknessPass { renderPassEvent = RenderPassEvent.AfterRenderingPrePasses };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // Enqueued for every camera, even when cavity is off: the pass is also what tells
            // SculptPBR (via _SculptCavityParams.x) whether THIS camera's buffer exists. Skipping
            // it would leave the previous camera's flag - and its buffer - bound.
            renderer.EnqueuePass(_pass);
            renderer.EnqueuePass(_thicknessPass);
        }

        private class ScreenCavityPass : ScriptableRenderPass
        {
            private static readonly ShaderTagId NormalsTag = new ShaderTagId("SculptCavityNormals");
            private static readonly int NormalsTexId = Shader.PropertyToID("_SculptCavityNormals");
            private static readonly int ParamsId = Shader.PropertyToID("_SculptCavityParams");
            private static readonly int TexelId = Shader.PropertyToID("_SculptCavityTexel");

            private class DrawData
            {
                public RendererListHandle rendererList;
                public Vector4 parameters;
                public Vector4 texel;
            }

            private class OffData { }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                bool run = ScreenCavity.Enabled &&
                           (cameraData.cameraType == CameraType.Game || cameraData.cameraType == CameraType.SceneView);
                if (!run)
                {
                    RecordOff(renderGraph);
                    return;
                }

                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                RenderTextureDescriptor target = cameraData.cameraTargetDescriptor;
                int width = Mathf.Max(1, target.width);
                int height = Mathf.Max(1, target.height);

                // Cleared to all-zero so w = 0 marks "no sculpt surface here" - a real eye depth
                // is always > 0.
                TextureHandle normals = renderGraph.CreateTexture(new TextureDesc(width, height)
                {
                    name = "_SculptCavityNormals",
                    format = GraphicsFormat.R16G16B16A16_SFloat,
                    clearBuffer = true,
                    clearColor = Color.clear,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                });
                TextureHandle depth = renderGraph.CreateTexture(new TextureDesc(width, height)
                {
                    name = "_SculptCavityDepth",
                    format = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil),
                    clearBuffer = true,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp,
                });

                using (var builder = renderGraph.AddRasterRenderPass<DrawData>("Sculpt Screen Cavity Normals", out var data))
                {
                    DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(NormalsTag, renderingData,
                        cameraData, lightData, SortingCriteria.CommonOpaque);
                    var filtering = new FilteringSettings(RenderQueueRange.opaque);
                    data.rendererList = renderGraph.CreateRendererList(
                        new RendererListParams(renderingData.cullResults, drawing, filtering));
                    data.parameters = BuildParameters();
                    data.texel = new Vector4(1f / width, 1f / height, width, height);

                    builder.UseRendererList(data.rendererList);
                    builder.SetRenderAttachment(normals, 0, AccessFlags.Write);
                    builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(normals, NormalsTexId);
                    builder.AllowGlobalStateModification(true);
                    // Nothing downstream declares a read of this texture - the opaque pass picks
                    // it up as a global - so culling would otherwise be free to drop the pass.
                    builder.AllowPassCulling(false);

                    builder.SetRenderFunc(static (DrawData d, RasterGraphContext ctx) =>
                    {
                        ctx.cmd.SetGlobalVector(ParamsId, d.parameters);
                        ctx.cmd.SetGlobalVector(TexelId, d.texel);
                        ctx.cmd.DrawRendererList(d.rendererList);
                    });
                }
            }

            private static void RecordOff(RenderGraph renderGraph)
            {
                using (var builder = renderGraph.AddUnsafePass<OffData>("Sculpt Screen Cavity (off)", out _))
                {
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (OffData _, UnsafeGraphContext ctx) =>
                        ctx.cmd.SetGlobalVector(ParamsId, Vector4.zero));
                }
            }

            /// x = on, y/z = ridge/valley soft-clamp controls, w = sample offset in pixels.
            /// The controls are Workbench's own: 0.5 / ridge^2 and 0.7 / valley^2, so a factor of
            /// 1 lets a ridge at most double the colour and a valley at most take it to ~29%,
            /// and a factor of 0 makes the soft clamp's ceiling effectively zero.
            private static Vector4 BuildParameters()
            {
                float ridge = Mathf.Max(ScreenCavity.Ridge * ScreenCavity.Ridge, 1e-4f);
                float valley = Mathf.Max(ScreenCavity.Valley * ScreenCavity.Valley, 1e-4f);
                // Workbench offsets by one pixel times the UI scale, so the lines keep the same
                // apparent weight on a high-DPI screen. Screen.dpi is 96 at 100% on Windows and
                // 0 where the platform can't say.
                float dpi = Screen.dpi;
                float pixelOffset = dpi > 0f ? Mathf.Clamp(dpi / 96f, 1f, 3f) : 1f;
                return new Vector4(1f, 0.5f / ridge, 0.7f / valley, pixelOffset);
            }
        }
    }
}
