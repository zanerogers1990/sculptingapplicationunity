using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Sculpting
{
    /// Whether a lure plastic is showing, so the back-face depth buffer it reads is worth drawing.
    /// SculptMaterialController pushes it; a global switch rather than a material property because
    /// the pass that consumes it runs before any material is bound.
    public static class LurePlasticRender
    {
        public static bool Enabled;
    }

    /// Draws every SculptPBR mesh's back faces (its "SculptPlasticBackDepth" pass) into a float
    /// buffer of eye depth, before opaques. SculptPBR's lure plastic subtracts its own eye depth
    /// from that to get how much plastic the view ray crosses - which is what makes a thin claw
    /// read lighter and glow while the thick body behind it stays dark.
    ///
    /// Hosted by ScreenCavityFeature rather than a feature of its own, so both renderer assets
    /// pick it up without another feature entry to keep linked.
    internal class PlasticThicknessPass : ScriptableRenderPass
    {
        private static readonly ShaderTagId BackDepthTag = new ShaderTagId("SculptPlasticBackDepth");
        private static readonly int BackDepthTexId = Shader.PropertyToID("_SculptPlasticBackDepth");
        private static readonly int ParamsId = Shader.PropertyToID("_SculptPlasticParams");

        private class DrawData
        {
            public RendererListHandle rendererList;
        }

        private class OffData { }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            bool run = LurePlasticRender.Enabled &&
                       (cameraData.cameraType == CameraType.Game || cameraData.cameraType == CameraType.SceneView);
            if (!run)
            {
                // Still recorded: it's what tells SculptPBR this camera has no buffer, so it never
                // reads one left bound by another camera.
                using (var builder = renderGraph.AddUnsafePass<OffData>("Sculpt Plastic Thickness (off)", out _))
                {
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (OffData _, UnsafeGraphContext ctx) =>
                        ctx.cmd.SetGlobalVector(ParamsId, Vector4.zero));
                }
                return;
            }

            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            RenderTextureDescriptor target = cameraData.cameraTargetDescriptor;
            int width = Mathf.Max(1, target.width);
            int height = Mathf.Max(1, target.height);

            // 32-bit: thickness is a small DIFFERENCE of two eye depths, and half precision at a
            // few units of depth is already a tenth of a claw's thickness. Cleared to 0 = "no back
            // face here"; a real eye depth is always > 0.
            TextureHandle backDepth = renderGraph.CreateTexture(new TextureDesc(width, height)
            {
                name = "_SculptPlasticBackDepth",
                format = GraphicsFormat.R32_SFloat,
                clearBuffer = true,
                clearColor = Color.clear,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            });
            TextureHandle depth = renderGraph.CreateTexture(new TextureDesc(width, height)
            {
                name = "_SculptPlasticBackDepthZ",
                format = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil),
                clearBuffer = true,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
            });

            using (var builder = renderGraph.AddRasterRenderPass<DrawData>("Sculpt Plastic Thickness", out var data))
            {
                DrawingSettings drawing = RenderingUtils.CreateDrawingSettings(BackDepthTag, renderingData,
                    cameraData, lightData, SortingCriteria.CommonOpaque);
                var filtering = new FilteringSettings(RenderQueueRange.opaque);
                data.rendererList = renderGraph.CreateRendererList(
                    new RendererListParams(renderingData.cullResults, drawing, filtering));

                builder.UseRendererList(data.rendererList);
                builder.SetRenderAttachment(backDepth, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
                builder.SetGlobalTextureAfterPass(backDepth, BackDepthTexId);
                builder.AllowGlobalStateModification(true);
                // Read only as a global by the opaque pass, so nothing declares it - keep it.
                builder.AllowPassCulling(false);

                builder.SetRenderFunc(static (DrawData d, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalVector(ParamsId, new Vector4(1f, 0f, 0f, 0f));
                    ctx.cmd.DrawRendererList(d.rendererList);
                });
            }
        }
    }
}
