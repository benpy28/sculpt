using System.Collections.Generic;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sculpt.Game.Rendering
{
    /// <summary>
    /// Applies <see cref="VideoSettings"/>: frame pacing (V-Sync, frame cap, the driver's frame queue) and the lean
    /// render profile. The project's render pipeline assets are still the Unity template's (HDR target, depth and
    /// colour copies, shadow pass, an ambient occlusion feature), none of which the game's unlit shaders read, so lean
    /// mode switches all of that off for the game camera: same picture, less work per frame.
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

        private readonly List<ScriptableRendererFeature> _switchedOff = new List<ScriptableRendererFeature>();
        private UniversalRenderPipelineAsset _scaledAsset;
        private float _assetScaleBefore;
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
            bool lean = s.LeanRendering;
            if (_camera != null)
            {
                UniversalAdditionalCameraData data = _camera.GetUniversalAdditionalCameraData();
                data.requiresDepthOption = lean ? CameraOverrideOption.Off : CameraOverrideOption.UsePipelineSettings;
                data.requiresColorOption = lean ? CameraOverrideOption.Off : CameraOverrideOption.UsePipelineSettings;
                data.renderShadows = !lean;
                data.renderPostProcessing = false;
                data.antialiasing = AntialiasingMode.None;
                data.stopNaN = false;
                data.dithering = false;
                _camera.allowHDR = !lean;
                _camera.allowMSAA = !lean;
                _camera.useOcclusionCulling = false; // nothing is baked; skips the query
            }

            // ---- Shared pipeline objects (restored on destroy) ----
            UniversalRenderPipelineAsset asset = UniversalRenderPipeline.asset;
            RestoreFeatures();
            if (lean && asset != null)
            {
                foreach (ScriptableRendererData rd in asset.rendererDataList)
                {
                    if (rd == null) continue;
                    foreach (ScriptableRendererFeature f in rd.rendererFeatures)
                    {
                        // Ambient occlusion: a depth-normals pass plus full-screen passes whose result only lit shaders read.
                        if (f == null || !f.isActive || f.GetType().Name != "ScreenSpaceAmbientOcclusion") continue;
                        f.SetActive(false);
                        _switchedOff.Add(f);
                    }
                }
            }
            if (asset != null)
            {
                if (_scaledAsset != asset)
                {
                    RestoreScale();
                    _scaledAsset = asset;
                    _assetScaleBefore = asset.renderScale;
                }
                if (!Mathf.Approximately(asset.renderScale, s.RenderScale)) asset.renderScale = s.RenderScale;
            }

            int queue = QualitySettings.maxQueuedFrames;
            Summary = (s.VSync ? "V-Sync on" : "V-Sync off") +
                      (ActiveCap > 0 ? ", cap " + ActiveCap : ", no cap") +
                      (queue > 0 ? ", frame queue " + queue : ", frame queue n/a") +
                      (lean ? ", lean" : ", full pipeline") +
                      (s.RenderScale < 0.995f ? ", scale " + Mathf.RoundToInt(s.RenderScale * 100f) + "%" : "");
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

        private void RestoreFeatures()
        {
            foreach (ScriptableRendererFeature f in _switchedOff) if (f != null) f.SetActive(true);
            _switchedOff.Clear();
        }

        private void RestoreScale()
        {
            if (_scaledAsset != null) _scaledAsset.renderScale = _assetScaleBefore;
            _scaledAsset = null;
        }

        private void OnDestroy()
        {
            RestoreFeatures();
            RestoreScale();
            if (!_saved) return;
            QualitySettings.vSyncCount = _vSyncBefore;
            QualitySettings.maxQueuedFrames = _queueBefore;
            Application.targetFrameRate = _targetBefore;
        }
    }
}
