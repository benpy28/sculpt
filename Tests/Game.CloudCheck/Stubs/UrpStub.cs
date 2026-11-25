// Compile-only stub of the Universal Render Pipeline API surface used by Sculpt.Game (Rendering/RenderProfile.cs). Not shipped.
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
        public bool supportsHDR { get; set; }
    }
    public static class UniversalRenderPipeline
    {
        public static UniversalRenderPipelineAsset asset => null;
    }
}
