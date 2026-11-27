using System.Collections.Generic;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sculpt.Game.Rendering
{
    /// <summary>
    /// Applies <see cref="VideoSettings"/>: frame pacing (V-Sync, frame cap, the driver's frame queue), the lean
    /// render profile and the picture settings (<see cref="PictureEffects"/>). The project's render pipeline assets are
    /// still the Unity template's (HDR target, depth and colour copies, shadow pass, an ambient occlusion feature);
    /// lean mode switches off what no picture setting in force needs, so the competitive picture costs the least.
    ///
    /// Nothing is written to an asset file. Camera options live on the code-made camera; the few values that sit on
    /// shared pipeline objects (renderer features, render scale, quality settings) are changed in memory and put back
    /// in <see cref="OnDestroy"/>, because in the Editor those objects outlive Play mode.
    /// </summary>
    public sealed class RenderProfile : MonoBehaviour
    {
        private Camera _camera;
        private VideoSettings _settings;
        private int _applied = -1;
        private int _appliedWidth, _appliedHeight;

        private PictureEffects _picture;
        private UniversalRenderPipelineAsset _scaledAsset;
        private float _assetScaleBefore;
        private int _assetMsaaBefore;
        private int _vSyncBefore, _queueBefore, _targetBefore;
        private bool _saved;

        /// <summary>One line saying what is in force, for the overlay and the benchmark log.</summary>
        public string Summary { get; private set; } = "";
        /// <summary>The frame cap in force (0 = none).</summary>
        public int ActiveCap { get; private set; }

        public static RenderProfile Install(GameObject host, Camera camera)
        {
            var p = host.AddComponent<RenderProfile>();
            p._camera = camera;
            p._settings = VideoSettings.Current;
            p.Apply();
            return p;
        }

        private void Update()
        {
            if (_settings == null) return;
            _picture?.Tick();
            // A change of display (window moved to another monitor) changes what "display - 3" means.
            if (_settings.Version != _applied || Screen.width != _appliedWidth || Screen.height != _appliedHeight) Apply();
        }

        public void Apply()
        {
            VideoSettings s = _settings;
            if (!_saved)
            {
                _saved = true;
                _vSyncBefore = QualitySettings.vSyncCount;
                _queueBefore = QualitySettings.maxQueuedFrames;
                _targetBefore = Application.targetFrameRate;
            }
            _applied = s.Version;
            _appliedWidth = Screen.width;
            _appliedHeight = Screen.height;

            // ---- Pacing ----
            QualitySettings.vSyncCount = s.VSync ? 1 : 0;
            double refresh = RefreshHz;
            ActiveCap = s.VSync ? 0 : s.CapFor(refresh);
            Application.targetFrameRate = ActiveCap > 0 ? ActiveCap : -1;
            // Frames the driver may hold finished but not yet shown. Every queued frame is a frame of delay between
            // the click and the screen whenever the graphics card, not the processor, sets the pace.
            QualitySettings.maxQueuedFrames = s.LowLatency ? 1 : 2;

            // ---- Camera ----
            // Lean skips the pipeline work nothing reads (HDR target, depth and colour copies); a picture setting that
            // needs one of them (Video > Picture) turns just that back on.
            bool lean = s.LeanRendering;
            Sculpt.Core.Settings.LookValues look = s.Look;
            bool taa = look.PostAa == 3;
            if (_camera != null)
            {
                UniversalAdditionalCameraData data = _camera.GetUniversalAdditionalCameraData();
                data.requiresDepthOption = look.NeedsDepth ? CameraOverrideOption.On : lean ? CameraOverrideOption.Off : CameraOverrideOption.UsePipelineSettings;
                data.requiresColorOption = lean ? CameraOverrideOption.Off : CameraOverrideOption.UsePipelineSettings;
                data.renderShadows = look.Shadows > 0;
                data.renderPostProcessing = look.NeedsPost;
                data.antialiasing = look.PostAa == 1 ? AntialiasingMode.FastApproximateAntialiasing
                                  : look.PostAa == 2 ? AntialiasingMode.SubpixelMorphologicalAntiAliasing
                                  : taa ? AntialiasingMode.TemporalAntiAliasing : AntialiasingMode.None;
                data.antialiasingQuality = AntialiasingQuality.High;
                data.stopNaN = false;
                data.dithering = look.NeedsPost; // hides banding in the sky gradient once post-processing runs
                _camera.allowHDR = look.NeedsHdr || !lean;
                _camera.allowMSAA = s.Msaa > 1 && !taa; // TAA replaces MSAA (URP can't run both)
                _camera.useOcclusionCulling = false; // nothing is baked; skips the query
            }

            // ---- Shared pipeline objects (restored on destroy) ----
            UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
            if (_picture == null && _camera != null) _picture = new PictureEffects(_camera);
            _picture?.Apply(s, asset);
            if (asset != null)
            {
                if (_scaledAsset != asset)
                {
                    RestoreScale();
                    _scaledAsset = asset;
                    _assetScaleBefore = asset.renderScale;
                    _assetMsaaBefore = asset.msaaSampleCount;
                }
                if (!Mathf.Approximately(asset.renderScale, s.RenderScale)) asset.renderScale = s.RenderScale;
                // MSAA samples live on the pipeline asset (the camera only allows or refuses it).
                int msaa = taa ? 1 : s.Msaa;
                if (asset.msaaSampleCount != msaa) asset.msaaSampleCount = msaa;
            }

            int queue = QualitySettings.maxQueuedFrames;
            Summary = (s.VSync ? "V-Sync on" : "V-Sync off") +
                      (ActiveCap > 0 ? ", cap " + ActiveCap : ", no cap") +
                      (queue > 0 ? ", frame queue " + queue : ", frame queue n/a") +
                      (lean ? ", lean" : ", full pipeline") +
                      (s.RenderScale < 0.995f ? ", scale " + Mathf.RoundToInt(s.RenderScale * 100f) + "%" : "") +
                      (s.Msaa > 1 && !taa ? ", MSAA " + s.Msaa + "x" : "") +
                      (_picture != null ? ", " + _picture.Summary : "");
            Debug.Log("[SCULPT] Video: " + Summary + $" ({Screen.width}x{Screen.height} at {refresh:0.#} Hz, {SystemInfo.graphicsDeviceType}).");
        }

        /// <summary>The display's refresh rate in Hz.</summary>
        public static double RefreshHz
        {
            get
            {
#if UNITY_2022_2_OR_NEWER
                return Screen.currentResolution.refreshRateRatio.value;
#else
                return Screen.currentResolution.refreshRate;
#endif
            }
        }

        private void RestoreScale()
        {
            if (_scaledAsset != null) { _scaledAsset.renderScale = _assetScaleBefore; _scaledAsset.msaaSampleCount = _assetMsaaBefore; }
            _scaledAsset = null;
        }

        private void OnDestroy()
        {
            _picture?.Restore();
            _picture = null;
            RestoreScale();
            if (!_saved) return;
            QualitySettings.vSyncCount = _vSyncBefore;
            QualitySettings.maxQueuedFrames = _queueBefore;
            Application.targetFrameRate = _targetBefore;
        }
    }
}
