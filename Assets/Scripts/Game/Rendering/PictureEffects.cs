using System;
using System.Collections.Generic;
using System.Reflection;
using Sculpt.Core.Settings;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Sculpt.Game.Rendering
{
    /// <summary>
    /// The picture settings (Video > Picture): URP post-processing through one global volume made in code (tonemapping,
    /// colour grading, bloom, depth of field, motion blur, vignette, film grain), our own full-screen passes (sharpening,
    /// light shafts, <see cref="SculptPostPass"/>), sun shadows, screen-space ambient occlusion, rich lighting and
    /// atmospheric fog (shader globals read by SculptLook.hlsl), and the post anti-aliasing mode on the camera.
    ///
    /// Every effect is off in the competitive picture, and off means off: no volume component active, no post pass, no
    /// shadow pass, no AO pass, no depth copy, our passes not enqueued. Fairness: only scenery takes shadows, AO, light
    /// and atmosphere (players, loot and effects keep their exact colours), and players never cast shadows.
    /// Nothing here touches the simulation, input or frame pacing, so none of it adds latency; a frame only costs more
    /// graphics card time.
    ///
    /// Shared pipeline objects (the pipeline asset's shadow distance and cascades, the AO renderer feature's settings)
    /// are changed in memory and put back in <see cref="Restore"/>, like <see cref="RenderProfile"/> does.
    /// </summary>
    public sealed class PictureEffects
    {
        // Shader globals (SculptLook.hlsl, SculptSky.shader, SculptPost.shader).
        private static readonly int LookId = Shader.PropertyToID("_SculptLook");
        private static readonly int SunDirId = Shader.PropertyToID("_SculptSunDir");
        private static readonly int SunColId = Shader.PropertyToID("_SculptSunCol");
        private static readonly int ShadeColId = Shader.PropertyToID("_SculptShadeCol");
        private static readonly int FogColId = Shader.PropertyToID("_SculptFogCol");

        /// <summary>Warm light on faces in the sun (a multiplier, not a colour: set as a vector so it is not converted to linear).</summary>
        public static readonly Vector4 SunTint = new Vector4(1.07f, 1.02f, 0.93f, 1f);
        /// <summary>Cool sky light on everything out of the sun (multiplier). Deep enough to read the sun's direction, light
        /// enough that a wall in shade never turns into a dark hiding place.</summary>
        public static readonly Vector4 ShadeTint = new Vector4(0.70f, 0.75f, 0.86f, 1f);
        /// <summary>Atmospheric fog density per metre at ground level and the most of a view it may cover.</summary>
        public const float FogDensity = 0.0042f, FogMax = 0.7f;

        private readonly Camera _camera;
        private VolumeProfile _profile;
        private Volume _volume;
        private Tonemapping _tonemap;
        private ColorAdjustments _colour;
        private WhiteBalance _white;
        private Bloom _bloom;
        private Vignette _vignette;
        private FilmGrain _grain;
        private DepthOfField _dof;
        private MotionBlur _motion;

        private SculptPostPass _shafts, _sharpen;
        private Material _postMat;
        private bool _hooked;
        private LookValues _look;

        private UniversalRenderPipelineAsset _asset;
        private float _shadowDistanceBefore;
        private int _cascadesBefore;
        private readonly List<(ScriptableRendererFeature feature, bool active, object settings, Dictionary<string, object> before)> _ao =
            new List<(ScriptableRendererFeature, bool, object, Dictionary<string, object>)>();
        private bool _aoFound;
        private bool _fogBefore;
        private bool _fogSaved;

        public PictureEffects(Camera camera)
        {
            _camera = camera;
            BuildVolume();
            Shader post = Shader.Find("SCULPT/Post");
            if (post != null)
            {
                _postMat = CoreUtils.CreateEngineMaterial(post);
                _shafts = new SculptPostPass("SCULPT Light shafts", _postMat, 1, RenderPassEvent.BeforeRenderingPostProcessing, needsDepth: true);
                _sharpen = new SculptPostPass("SCULPT Sharpen", _postMat, 0, RenderPassEvent.AfterRenderingPostProcessing, needsDepth: false);
            }
            else Debug.LogError("[SCULPT] Shader SCULPT/Post not found: sharpening and light shafts are unavailable.");
        }

        /// <summary>One line for the overlay and the benchmark log.</summary>
        public string Summary { get; private set; } = "";

        // ------------------------------------------------------------------ Volume

        private void BuildVolume()
        {
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "Picture effects (runtime)";
            _profile.hideFlags = HideFlags.DontSave;
            _tonemap = _profile.Add<Tonemapping>(false);
            _colour = _profile.Add<ColorAdjustments>(false);
            _white = _profile.Add<WhiteBalance>(false);
            _bloom = _profile.Add<Bloom>(false);
            _vignette = _profile.Add<Vignette>(false);
            _grain = _profile.Add<FilmGrain>(false);
            _dof = _profile.Add<DepthOfField>(false);
            _motion = _profile.Add<MotionBlur>(false);

            var go = new GameObject("Picture effects");
            _volume = go.AddComponent<Volume>();
            _volume.isGlobal = true;
            _volume.priority = 100f; // above the project's default profile
            _volume.weight = 1f;
            _volume.sharedProfile = _profile;
        }

        private void ApplyVolume(in LookValues l)
        {
            // Tonemapping: neutral only. ACES shifts saturated blues toward purple, and purple means "sculptable" only.
            _tonemap.active = l.Tonemap > 0;
            _tonemap.mode.Override(TonemappingMode.Neutral);

            // Colour grading (plus a small exposure lift that keeps the neutral curve from dulling our bright art).
            float contrast = l.Grading == 2 ? 16f : l.Grading == 1 ? 8f : 0f;
            float saturation = l.Grading == 2 ? 24f : l.Grading == 1 ? 10f : 0f;
            float exposure = l.Tonemap > 0 ? 0.3f : 0f;
            _colour.active = l.Grading > 0 || l.Tonemap > 0;
            _colour.postExposure.Override(exposure);
            _colour.contrast.Override(contrast);
            _colour.saturation.Override(saturation);
            _white.active = l.Grading > 0;
            _white.temperature.Override(l.Grading == 2 ? 6f : 4f);

            // Bloom: only above white (the sun disc, sparks, blasts). Walls, ground and players stay under it.
            _bloom.active = l.Bloom > 0;
            _bloom.threshold.Override(1.05f);
            _bloom.intensity.Override(l.Bloom == 2 ? 0.8f : 0.35f);
            _bloom.scatter.Override(l.Bloom == 2 ? 0.7f : 0.6f);
            _bloom.highQualityFiltering.Override(l.Bloom == 2);

            _vignette.active = l.Vignette > 0.001f;
            _vignette.intensity.Override(l.Vignette * 0.45f);
            _vignette.smoothness.Override(0.45f);
            _vignette.color.Override(Color.black);

            _grain.active = l.Grain > 0.001f;
            _grain.type.Override(FilmGrainLookup.Thin1);
            _grain.intensity.Override(l.Grain * 0.6f);
            _grain.response.Override(0.8f);

            // Depth of field: Gaussian far blur only, so nothing near you (or your gun, drawn at the near plane) blurs.
            _dof.active = l.Dof > 0;
            _dof.mode.Override(l.Dof > 0 ? DepthOfFieldMode.Gaussian : DepthOfFieldMode.Off);
            _dof.gaussianStart.Override(90f);
            _dof.gaussianEnd.Override(300f);
            _dof.gaussianMaxRadius.Override(1f);
            _dof.highQualitySampling.Override(false);

            _motion.active = l.MotionBlur > 0;
            _motion.mode.Override(MotionBlurMode.CameraOnly);
            _motion.quality.Override(l.MotionBlur == 2 ? MotionBlurQuality.Medium : MotionBlurQuality.Low);
            _motion.intensity.Override(l.MotionBlur == 2 ? 0.5f : 0.25f);
            _motion.clamp.Override(0.05f);
        }

        // ------------------------------------------------------------------ Apply

        /// <summary>Applies the picture part of the video settings. Called by <see cref="RenderProfile.Apply"/>.</summary>
        public void Apply(VideoSettings s, UniversalRenderPipelineAsset asset)
        {
            LookValues l = s.Look;
            _look = l;
            ApplyVolume(l);
            ApplyGlobals(l);
            ApplyShadows(l, asset);
            ApplyAo(l, asset);
            ApplyFog(l);
            HookPasses(l.Shafts || l.Sharpen > 0.001f);
            if (_sharpen != null) _postMat.SetFloat(SculptPostPass.SharpenId, l.Sharpen);

            Summary = "picture: " + (l.NeedsPost ? "post on" : "post off") +
                      (l.Shadows > 0 ? ", shadows " + LookValues.LowHighNames[l.Shadows] : "") +
                      (l.Ao > 0 ? ", AO " + LookValues.LowHighNames[l.Ao] : "") +
                      (l.Lighting > 0 ? ", rich light" : "") +
                      ", fog " + LookValues.FogNames[l.Fog] +
                      (l.Shafts ? ", shafts" : "") +
                      (l.PostAa > 0 ? ", " + LookValues.PostAaNames[l.PostAa] : "") +
                      (l.Sharpen > 0.001f ? ", sharpen " + Mathf.RoundToInt(l.Sharpen * 100f) + "%" : "");
        }

        private void ApplyGlobals(in LookValues l)
        {
            Shader.SetGlobalVector(LookId, new Vector4(
                l.Shadows > 0 ? 1f : 0f,
                l.Lighting > 0 ? 1f : 0f,
                l.Fog == 2 ? FogDensity : 0f,
                l.Ao == 2 ? 1f : l.Ao == 1 ? 0.7f : 0f));
            Shader.SetGlobalVector(SunColId, SunTint);
            Shader.SetGlobalVector(ShadeColId, ShadeTint);
            Color fog = World.Scenery.Horizon;
            fog.a = FogMax;
            Shader.SetGlobalColor(FogColId, fog);
            UpdateSun();
        }

        private Light Sun
        {
            get
            {
                if (RenderSettings.sun != null) return RenderSettings.sun;
                foreach (Light li in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                    if (li.type == LightType.Directional) return li;
                return null;
            }
        }

        private Light _sun;

        private void UpdateSun()
        {
            if (_sun == null) _sun = Sun;
            if (_sun == null) { Shader.SetGlobalVector(SunDirId, Vector4.zero); return; }
            Vector3 toSun = -_sun.transform.forward;
            Shader.SetGlobalVector(SunDirId, new Vector4(toSun.x, toSun.y, toSun.z, 1f));
            LightShadows want = _look.Shadows == 2 ? LightShadows.Soft : _look.Shadows == 1 ? LightShadows.Hard : LightShadows.None;
            if (_sun.shadows != want) _sun.shadows = want;
            _sun.shadowStrength = 1f; // the shade colour is ours (ShadeTint): the shadow map only says where
        }

        private void ApplyShadows(in LookValues l, UniversalRenderPipelineAsset asset)
        {
            if (asset == null) return;
            if (_asset != asset)
            {
                RestoreAsset();
                _asset = asset;
                _shadowDistanceBefore = asset.shadowDistance;
                _cascadesBefore = asset.shadowCascadeCount;
            }
            if (l.Shadows == 0) { asset.shadowDistance = _shadowDistanceBefore; asset.shadowCascadeCount = _cascadesBefore; return; }
            asset.shadowDistance = l.Shadows == 2 ? 80f : 40f;
            asset.shadowCascadeCount = l.Shadows == 2 ? 4 : 2;
        }

        // The template's SSAO renderer feature. Its settings class is internal, so the two numbers we change are set
        // by name; if a URP update renames them, AO still switches on and off and only Low and High look the same.
        private void ApplyAo(in LookValues l, UniversalRenderPipelineAsset asset)
        {
            if (!_aoFound && asset != null)
            {
                _aoFound = true;
                foreach (ScriptableRendererData rd in asset.rendererDataList)
                {
                    if (rd == null) continue;
                    foreach (ScriptableRendererFeature f in rd.rendererFeatures)
                    {
                        if (f == null || f.GetType().Name != "ScreenSpaceAmbientOcclusion") continue;
                        object settings = f.GetType().GetField("m_Settings", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(f);
                        var before = new Dictionary<string, object>();
                        foreach (string name in AoFields)
                        {
                            FieldInfo fi = settings?.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fi != null) before[name] = fi.GetValue(settings);
                        }
                        _ao.Add((f, f.isActive, settings, before));
                    }
                }
                if (_ao.Count == 0 && asset != null) Debug.LogWarning("[SCULPT] No ambient occlusion feature on the renderer: the AO setting does nothing.");
            }
            foreach (var a in _ao)
            {
                if (a.feature == null) continue;
                bool on = l.Ao > 0;
                if (a.feature.isActive != on) a.feature.SetActive(on);
                if (!on || a.settings == null) continue;
                SetAo(a.settings, "Intensity", l.Ao == 2 ? 0.9f : 0.55f);
                SetAo(a.settings, "Radius", l.Ao == 2 ? 0.45f : 0.3f);
                SetAo(a.settings, "Downsample", l.Ao != 2);
                SetAo(a.settings, "AfterOpaque", false); // our shaders read it; "after opaque" would darken players too
            }
        }

        private static readonly string[] AoFields = { "Intensity", "Radius", "Downsample", "AfterOpaque" };

        private static void SetAo(object settings, string name, object value)
        {
            FieldInfo fi = settings.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi == null || fi.FieldType != value.GetType()) return;
            fi.SetValue(settings, value);
        }

        private void ApplyFog(in LookValues l)
        {
            if (!_fogSaved) { _fogSaved = true; _fogBefore = RenderSettings.fog; }
            RenderSettings.fog = l.Fog > 0;
        }

        /// <summary>Every frame: the scenery (sun, fog) may be built after the settings were applied.</summary>
        public void Tick()
        {
            if (_sun == null) UpdateSun();
            bool fog = _look.Fog > 0;
            if (RenderSettings.fog != fog) RenderSettings.fog = fog;
            if (!_diagLogged && Debug.isDebugBuild && Time.realtimeSinceStartup > 6f) LogDiagnostics();
        }

        private bool _diagLogged;

        /// <summary>Dev builds: one line with what decides whether sun shadows draw, a few seconds in.</summary>
        private void LogDiagnostics()
        {
            _diagLogged = true;
            int directional = 0;
            foreach (Light li in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
                if (li.type == LightType.Directional) directional++;
            UniversalAdditionalCameraData data = _camera != null ? _camera.GetUniversalAdditionalCameraData() : null;
            UniversalRenderPipelineAsset a = UniversalRenderPipeline.asset;
            Debug.Log("[SCULPT] Picture check: sun " + (_sun != null ? $"'{_sun.name}' shadows {_sun.shadows} strength {_sun.shadowStrength:0.##} enabled {_sun.isActiveAndEnabled}" : "none") +
                      $", RenderSettings.sun is ours {RenderSettings.sun == _sun}, directional lights {directional}" +
                      (a != null ? $", asset '{a.name}' main shadows {a.supportsMainLightShadows} mode {a.mainLightRenderingMode} distance {a.shadowDistance} cascades {a.shadowCascadeCount}" : ", no URP asset") +
                      (data != null ? $", camera renderShadows {data.renderShadows} far {_camera.farClipPlane} near {_camera.nearClipPlane}" : "") +
                      $", quality level '{QualitySettings.names[QualitySettings.GetQualityLevel()]}'.");
        }

        // ------------------------------------------------------------------ Our passes

        private void HookPasses(bool on)
        {
            if (on == _hooked || _postMat == null) return;
            _hooked = on;
            if (on) RenderPipelineManager.beginCameraRendering += OnBeginCamera;
            else RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        }

        private void OnBeginCamera(ScriptableRenderContext context, Camera cam)
        {
            if (cam != _camera || _camera == null) return;
            UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
            ScriptableRenderer renderer = data != null ? data.scriptableRenderer : null;
            if (renderer == null) return;
            if (_look.Shafts && _sun != null && SetSunOnScreen(cam)) renderer.EnqueuePass(_shafts);
            if (_look.Sharpen > 0.001f) renderer.EnqueuePass(_sharpen);
        }

        /// <summary>Where the sun is on screen and how strong the shafts are (0 when it is behind or far off screen).</summary>
        private bool SetSunOnScreen(Camera cam)
        {
            Vector3 toSun = -_sun.transform.forward;
            float facing = Vector3.Dot(cam.transform.forward, toSun);
            Vector3 vp = cam.WorldToViewportPoint(cam.transform.position + toSun * 1000f);
            if (vp.z <= 0f || facing <= 0.2f) return false;
            float off = Mathf.Max(Mathf.Max(-vp.x, vp.x - 1f), Mathf.Max(-vp.y, vp.y - 1f));
            float strength = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.2f, 0.6f, facing)) * (1f - Mathf.Clamp01(off / 0.4f));
            if (strength <= 0.001f) return false;
            _postMat.SetVector(SculptPostPass.SunScreenId, new Vector4(vp.x, vp.y, strength, 0f));
            _postMat.SetVector(SunColId, new Vector4(1f, 0.9f, 0.7f, 1f));
            return true;
        }

        // ------------------------------------------------------------------ Restore

        private void RestoreAsset()
        {
            if (_asset == null) return;
            _asset.shadowDistance = _shadowDistanceBefore;
            _asset.shadowCascadeCount = _cascadesBefore;
            _asset = null;
        }

        public void Restore()
        {
            HookPasses(false);
            RestoreAsset();
            foreach (var a in _ao)
            {
                if (a.feature == null) continue;
                if (a.feature.isActive != a.active) a.feature.SetActive(a.active);
                if (a.settings == null) continue;
                foreach (KeyValuePair<string, object> kv in a.before) SetAo(a.settings, kv.Key, kv.Value);
            }
            _ao.Clear();
            if (_fogSaved) RenderSettings.fog = _fogBefore;
            if (_volume != null) UnityEngine.Object.Destroy(_volume.gameObject);
            if (_profile != null) UnityEngine.Object.Destroy(_profile);
            if (_postMat != null) CoreUtils.Destroy(_postMat);
            Shader.SetGlobalVector(LookId, Vector4.zero);
        }
    }
}
