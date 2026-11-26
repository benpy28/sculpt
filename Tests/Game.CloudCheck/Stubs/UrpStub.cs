// Compile-only stub of the Universal Render Pipeline and Core RP API surface used by Sculpt.Game (Rendering/RenderProfile.cs,
// PictureEffects.cs, SculptPostPass.cs). Mirrors the Unity 6 (URP 17) signatures we call; not shipped.
using System;
using System.Collections.Generic;
using UnityEngine;
namespace UnityEngine.Rendering.Universal
{
    public enum CameraOverrideOption { Off, On, UsePipelineSettings }
    public enum AntialiasingMode { None, FastApproximateAntialiasing, SubpixelMorphologicalAntiAliasing, TemporalAntiAliasing }
    public class UniversalAdditionalCameraData : MonoBehaviour
    {
        public CameraOverrideOption requiresDepthOption { get; set; }
        public CameraOverrideOption requiresColorOption { get; set; }
        public bool renderShadows { get; set; }
        public bool renderPostProcessing { get; set; }
        public AntialiasingMode antialiasing { get; set; }
        public bool stopNaN { get; set; }
        public bool dithering { get; set; }
        public AntialiasingQuality antialiasingQuality { get; set; }
        public ScriptableRenderer scriptableRenderer => null;
    }
    public enum AntialiasingQuality { Low, Medium, High }
    public abstract class ScriptableRenderer { public void EnqueuePass(ScriptableRenderPass pass) { } }
    public enum RenderPassEvent { BeforeRenderingPostProcessing = 550, AfterRenderingPostProcessing = 600 }
    [Flags] public enum ScriptableRenderPassInput { None = 0, Depth = 1, Normal = 2, Color = 4, Motion = 8 }
    public abstract class ScriptableRenderPass
    {
        public RenderPassEvent renderPassEvent { get; set; }
        public UnityEngine.Rendering.ProfilingSampler profilingSampler { get; set; }
        public bool requiresIntermediateTexture { get; set; }
        public void ConfigureInput(ScriptableRenderPassInput input) { }
        public virtual void RecordRenderGraph(UnityEngine.Rendering.RenderGraphModule.RenderGraph renderGraph, UnityEngine.Rendering.ContextContainer frameData) { }
    }
    public class UniversalResourceData
    {
        public bool isActiveTargetBackBuffer => false;
        public UnityEngine.Rendering.RenderGraphModule.TextureHandle activeColorTexture => default;
        public UnityEngine.Rendering.RenderGraphModule.TextureHandle cameraDepthTexture => default;
        public UnityEngine.Rendering.RenderGraphModule.TextureHandle cameraColor { get; set; }
    }
    public enum TonemappingMode { None, Neutral, ACES, Custom }
    public enum FilmGrainLookup { Thin1, Thin2, Medium1, Medium2, Medium3, Medium4, Medium5, Medium6, Large01, Large02, Custom }
    public enum DepthOfFieldMode { Off, Gaussian, Bokeh }
    public enum MotionBlurMode { CameraOnly, CameraAndObjects }
    public enum MotionBlurQuality { Low, Medium, High }
    public sealed class TonemappingModeParameter : UnityEngine.Rendering.VolumeParameter<TonemappingMode> { }
    public sealed class FilmGrainLookupParameter : UnityEngine.Rendering.VolumeParameter<FilmGrainLookup> { }
    public sealed class DepthOfFieldModeParameter : UnityEngine.Rendering.VolumeParameter<DepthOfFieldMode> { }
    public sealed class MotionBlurModeParameter : UnityEngine.Rendering.VolumeParameter<MotionBlurMode> { }
    public sealed class MotionBlurQualityParameter : UnityEngine.Rendering.VolumeParameter<MotionBlurQuality> { }
    public sealed class Tonemapping : UnityEngine.Rendering.VolumeComponent { public TonemappingModeParameter mode = new TonemappingModeParameter(); }
    public sealed class ColorAdjustments : UnityEngine.Rendering.VolumeComponent
    {
        public UnityEngine.Rendering.FloatParameter postExposure = new UnityEngine.Rendering.FloatParameter();
        public UnityEngine.Rendering.ClampedFloatParameter contrast = new UnityEngine.Rendering.ClampedFloatParameter();
        public UnityEngine.Rendering.ClampedFloatParameter saturation = new UnityEngine.Rendering.ClampedFloatParameter();
    }
    public sealed class WhiteBalance : UnityEngine.Rendering.VolumeComponent { public UnityEngine.Rendering.ClampedFloatParameter temperature = new UnityEngine.Rendering.ClampedFloatParameter(); }
    public sealed class Bloom : UnityEngine.Rendering.VolumeComponent
    {
        public UnityEngine.Rendering.MinFloatParameter threshold = new UnityEngine.Rendering.MinFloatParameter();
        public UnityEngine.Rendering.MinFloatParameter intensity = new UnityEngine.Rendering.MinFloatParameter();
        public UnityEngine.Rendering.ClampedFloatParameter scatter = new UnityEngine.Rendering.ClampedFloatParameter();
        public UnityEngine.Rendering.BoolParameter highQualityFiltering = new UnityEngine.Rendering.BoolParameter();
    }
    public sealed class Vignette : UnityEngine.Rendering.VolumeComponent
    {
        public UnityEngine.Rendering.ClampedFloatParameter intensity = new UnityEngine.Rendering.ClampedFloatParameter();
        public UnityEngine.Rendering.ClampedFloatParameter smoothness = new UnityEngine.Rendering.ClampedFloatParameter();
        public UnityEngine.Rendering.ColorParameter color = new UnityEngine.Rendering.ColorParameter();
    }
    public sealed class FilmGrain : UnityEngine.Rendering.VolumeComponent
    {
        public FilmGrainLookupParameter type = new FilmGrainLookupParameter();
        public UnityEngine.Rendering.ClampedFloatParameter intensity = new UnityEngine.Rendering.ClampedFloatParameter();
        public UnityEngine.Rendering.ClampedFloatParameter response = new UnityEngine.Rendering.ClampedFloatParameter();
    }
    public sealed class DepthOfField : UnityEngine.Rendering.VolumeComponent
    {
        public DepthOfFieldModeParameter mode = new DepthOfFieldModeParameter();
        public UnityEngine.Rendering.MinFloatParameter gaussianStart = new UnityEngine.Rendering.MinFloatParameter();
        public UnityEngine.Rendering.MinFloatParameter gaussianEnd = new UnityEngine.Rendering.MinFloatParameter();
        public UnityEngine.Rendering.ClampedFloatParameter gaussianMaxRadius = new UnityEngine.Rendering.ClampedFloatParameter();
        public UnityEngine.Rendering.BoolParameter highQualitySampling = new UnityEngine.Rendering.BoolParameter();
    }
    public sealed class MotionBlur : UnityEngine.Rendering.VolumeComponent
    {
        public MotionBlurModeParameter mode = new MotionBlurModeParameter();
        public MotionBlurQualityParameter quality = new MotionBlurQualityParameter();
        public UnityEngine.Rendering.ClampedFloatParameter intensity = new UnityEngine.Rendering.ClampedFloatParameter();
        public UnityEngine.Rendering.ClampedFloatParameter clamp = new UnityEngine.Rendering.ClampedFloatParameter();
    }
    public static class CameraExtensions
    {
        public static UniversalAdditionalCameraData GetUniversalAdditionalCameraData(this Camera camera) => null;
    }
    public abstract class ScriptableRendererFeature : ScriptableObject
    {
        public bool isActive => true;
        public void SetActive(bool active) { }
    }
    public abstract class ScriptableRendererData : ScriptableObject
    {
        public List<ScriptableRendererFeature> rendererFeatures => null;
    }
    public class UniversalRenderPipelineAsset : ScriptableObject
    {
        public ReadOnlySpan<ScriptableRendererData> rendererDataList => default;
        public float renderScale { get; set; }
        public int msaaSampleCount { get; set; }
        public bool supportsHDR { get; set; }
        public float shadowDistance { get; set; }
        public int shadowCascadeCount { get; set; }
    }
    public static class UniversalRenderPipeline
    {
        public static UniversalRenderPipelineAsset asset => null;
    }
}

namespace UnityEngine.Rendering
{
    public class VolumeParameter<T> { public T value; public bool overrideState; public void Override(T x) { overrideState = true; value = x; } }
    public sealed class FloatParameter : VolumeParameter<float> { }
    public sealed class MinFloatParameter : VolumeParameter<float> { }
    public sealed class ClampedFloatParameter : VolumeParameter<float> { }
    public sealed class BoolParameter : VolumeParameter<bool> { }
    public sealed class ColorParameter : VolumeParameter<Color> { }
    public class VolumeComponent : ScriptableObject { public bool active = true; }
    public sealed class VolumeProfile : ScriptableObject { public T Add<T>(bool overrides = false) where T : VolumeComponent => null; }
    public sealed class Volume : MonoBehaviour
    {
        public bool isGlobal;
        public float priority, weight;
        public VolumeProfile sharedProfile;
    }
    public class ProfilingSampler { public ProfilingSampler(string name) { } }
    public enum MSAASamples { None = 1, MSAA2x = 2, MSAA4x = 4, MSAA8x = 8 }
    public class ContextContainer { public T Get<T>() where T : class, new() => null; }
    public class RTHandle { }
    public static class CoreUtils
    {
        public static Material CreateEngineMaterial(Shader shader) => null;
        public static void Destroy(Object obj) { }
    }
    public static class Blitter
    {
        public static void BlitTexture(RenderGraphModule.RasterCommandBuffer cmd, RTHandle source, Vector4 scaleBias, Material material, int pass) { }
    }
}
namespace UnityEngine.Rendering.RenderGraphModule
{
    [System.Flags] public enum AccessFlags { None = 0, Read = 1, Write = 2, ReadWrite = 3 }
    public struct TextureHandle
    {
        public bool IsValid() => false;
        public static implicit operator RTHandle(TextureHandle h) => null;
    }
    public struct TextureDesc
    {
        public string name;
        public bool clearBuffer;
        public MSAASamples msaaSamples;
        public int width, height;
    }
    public class RasterCommandBuffer { }
    public class RasterGraphContext { public RasterCommandBuffer cmd; }
    public delegate void BaseRenderFunc<PassData, ContextType>(PassData data, ContextType renderGraphContext) where PassData : class, new();
    public interface IRasterRenderGraphBuilder : System.IDisposable
    {
        void UseTexture(in TextureHandle input, AccessFlags flags = AccessFlags.Read);
        void SetRenderAttachment(TextureHandle tex, int index, AccessFlags flags = AccessFlags.Write);
        void SetRenderFunc<PassData>(BaseRenderFunc<PassData, RasterGraphContext> renderFunc) where PassData : class, new();
    }
    public class RenderGraph
    {
        public TextureDesc GetTextureDesc(TextureHandle t) => default;
        public TextureHandle CreateTexture(in TextureDesc desc) => default;
        public IRasterRenderGraphBuilder AddRasterRenderPass<PassData>(string passName, out PassData passData, ProfilingSampler sampler) where PassData : class, new() { passData = null; return null; }
    }
}
