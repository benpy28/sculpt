using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Sculpt.Game.Rendering
{
    /// <summary>
    /// One full-screen pass of SCULPT/Post (sharpening or light shafts), enqueued per frame by
    /// <see cref="PictureEffects"/> only while its setting is on. Render graph: reads the camera colour (and the scene
    /// depth for shafts), writes a new colour texture and makes it the camera colour, so later passes see the result.
    /// </summary>
    public sealed class SculptPostPass : ScriptableRenderPass
    {
        public static readonly int SharpenId = Shader.PropertyToID("_SculptSharpen");
        public static readonly int SunScreenId = Shader.PropertyToID("_SculptSunScreen");
        private static readonly int TexelId = Shader.PropertyToID("_SculptTexel");

        private readonly string _name;
        private readonly Material _material;
        private readonly int _pass;
        private readonly bool _depth;

        private sealed class PassData
        {
            public TextureHandle Source;
            public Material Material;
            public int Pass;
        }

        public SculptPostPass(string name, Material material, int pass, RenderPassEvent at, bool needsDepth)
        {
            _name = name;
            _material = material;
            _pass = pass;
            _depth = needsDepth;
            renderPassEvent = at;
            profilingSampler = new ProfilingSampler(name);
            requiresIntermediateTexture = true;
            ConfigureInput(needsDepth ? ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color : ScriptableRenderPassInput.Color);
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            if (resources.isActiveTargetBackBuffer) return; // needs an intermediate texture to read from
            TextureHandle source = resources.activeColorTexture;
            TextureDesc desc = renderGraph.GetTextureDesc(source);
            desc.name = _name;
            desc.clearBuffer = false;
            desc.msaaSamples = MSAASamples.None;
            TextureHandle target = renderGraph.CreateTexture(desc);
            _material.SetVector(TexelId, new Vector4(1f / Mathf.Max(1, desc.width), 1f / Mathf.Max(1, desc.height), desc.width, desc.height));

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(_name, out PassData data, profilingSampler))
            {
                data.Source = source;
                data.Material = _material;
                data.Pass = _pass;
                builder.UseTexture(source, AccessFlags.Read);
                if (_depth && resources.cameraDepthTexture.IsValid()) builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
                builder.SetRenderAttachment(target, 0, AccessFlags.Write);
                builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
                    Blitter.BlitTexture(ctx.cmd, d.Source, new Vector4(1f, 1f, 0f, 0f), d.Material, d.Pass));
            }
            resources.cameraColor = target;
        }
    }
}
