using System;
using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Settings;
using Sculpt.Game.Audio;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Player;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.UI
{
    /// <summary>
    /// The settings menu (Esc, and the gear on the home screen). Pages down the left (Video, Latency and overlays, Mouse
    /// and aim, Controls, Key binds, Crosshair, HUD and view, Audio, Accessibility, Profile), a search box over the top
    /// that looks through every page at once, and a bar along the bottom with the Beginner view switch in the corner
    /// (Ben: "as detailed as possible, but with a Beginner settings mode"), "reset this page", and resume / leave / quit.
    ///
    /// Every option does something real and applies at once (the game keeps running underneath, so the view, sound and
    /// HUD change live); a few have a preview here too (crosshair on three backgrounds, rarity colours as each eye sees
    /// them, test sounds from four sides, cm/360). Settings save when a slider is let go and when the menu closes.
    /// IMGUI like the rest of the HUD for now; no purple.
    /// </summary>
    public sealed class SettingsMenu : MonoBehaviour
    {
        private LocalPlayer _player;
        private ClientSettings _settings;
        private VideoSettings _video;
        private GameInput _input;
        private bool _open;
        private Vector2 _scroll;
        private string _search = "";
        private int _tab;
        private bool _dirty;

        private InputActionRebindingExtensions.RebindingOperation _rebind;
        private InputAction _rebindAction;
        private int _rebindIndex;
        private int _rebindEndedFrame = -10;
        private string _rebindOldPath;
        private InputAction _clashA, _clashB;
        private int _clashAIndex, _clashBIndex;
        private string _clashOldPathA;

        private GUIStyle _title, _label, _value, _button, _bindButton, _header, _sub, _warn, _note, _tabStyle, _tabOn, _field, _small;
        private Texture2D _panel, _white;

        public bool IsOpen => _open;

        /// <summary>Frame the settings last closed (the home screen opens them on Esc and must not reopen them on the same press).</summary>
        public static int ClosedFrame = -10;
        private int _openedFrame = -10;

        // ---------------------------------------------------------------- Pages and entries

        private const int TabVideo = 0, TabLatency = 1, TabMouse = 2, TabControls = 3, TabBinds = 4, TabCrosshair = 5, TabHud = 6, TabAudio = 7, TabAccess = 8, TabProfile = 9;
        private static readonly string[] Tabs = { "Video", "Latency and overlays", "Mouse and aim", "Controls", "Key binds", "Crosshair", "HUD and view", "Audio", "Accessibility", "Profile" };
        private static readonly string[] TabGroups = { SettingsGroup.Video, SettingsGroup.Latency, SettingsGroup.Mouse, SettingsGroup.Controls, null, SettingsGroup.Crosshair, SettingsGroup.Hud, SettingsGroup.Audio, SettingsGroup.Access, null };

        private sealed class Entry
        {
            public int Tab;
            public string Section, Label;
            public bool Beginner;
            public Action Draw;
            public List<string> Words;
        }

        private readonly List<Entry> _entries = new List<Entry>();

        private void Add(int tab, string section, string label, string keywords, bool beginner, Action draw)
        {
            _entries.Add(new Entry
            {
                Tab = tab, Section = section, Label = label, Beginner = beginner, Draw = draw,
                Words = SettingsSearch.Words(label + " " + section + " " + Tabs[tab] + " " + keywords),
            });
        }

        public void Init(LocalPlayer player, ClientSettings settings, GameInput input)
        {
            _player = player;
            _settings = settings;
            _video = VideoSettings.Current;
            _input = input;
            AudioListener.volume = _settings.MasterVolume;
            BuildEntries();
        }

        private void Changed() { _settings.Changed(); _dirty = true; }
        private void VideoChanged() { _video.Clamped(); _video.Changed(); _dirty = true; }

        private void BuildEntries()
        {
            _entries.Clear();
            ClientSettings s = _settings;
            VideoSettings v = _video;

            // ---- Video ----
            Add(TabVideo, "Preset", "Video preset", "quality competitive graphics fps performance", true, DrawPreset);
            Add(TabVideo, "Display", "Display mode", "fullscreen full screen borderless windowed window exclusive", true, DrawDisplayMode);
            Add(TabVideo, "Display", "Resolution", "screen size pixels 1080p 1440p 4k", true, DrawResolution);
            Add(TabVideo, "Display", "Frame rate limit", "fps cap limit max frames 144 240 vrr gsync freesync", true, DrawFpsCap);
            Add(TabVideo, "Display", "V-Sync", "vsync vertical sync tearing", true, () => { if (Toggle("V-Sync (off = lowest latency)", ref v.VSync)) VideoChanged(); });
            Add(TabVideo, "Rendering", "Render scale", "resolution scale upscale performance", false, () =>
            {
                float n = Slider("Render scale (3D view only; HUD stays sharp)", v.RenderScale, VideoSettings.MinRenderScale, VideoSettings.MaxRenderScale, Mathf.RoundToInt(v.RenderScale * 100f) + "%");
                n = Mathf.Round(n * 20f) / 20f; // 5% steps
                if (!Mathf.Approximately(n, v.RenderScale)) { v.RenderScale = n; VideoChanged(); }
            });
            Add(TabVideo, "Rendering", "Anti-aliasing (MSAA)", "aa antialiasing msaa jaggies edges smooth", false, () =>
            {
                int at = Array.IndexOf(MsaaChoices, DisplayPresets.NormMsaa(v.Msaa));
                int n = Choice("Anti-aliasing (MSAA, smooths edges)", Mathf.Max(0, at), MsaaNames);
                if (MsaaChoices[n] != v.Msaa) { v.Msaa = MsaaChoices[n]; VideoChanged(); }
            });
            Add(TabVideo, "Rendering", "Post anti-aliasing", "aa antialiasing fxaa smaa taa temporal jaggies shimmer", false, () =>
            {
                int n = Choice("Post anti-aliasing (smooths shader detail too)", v.Look.PostAa, LookValues.PostAaNames);
                if (n != v.Look.PostAa) { v.Look.PostAa = n; VideoChanged(); }
                if (v.Look.PostAa == 3) Note("TAA is the smoothest but replaces MSAA, softens the picture a little and can leave a faint trail behind fast-moving players. SMAA is the sharp choice.");
            });
            Add(TabVideo, "Rendering", "Lean rendering", "lean pipeline hdr performance", false, () => { if (Toggle("Lean rendering (recommended, same picture)", ref v.LeanRendering)) VideoChanged(); });

            // ---- Picture (post-processing, shadows, lighting) ----
            Add(TabVideo, "Picture", "Picture quality", "picture quality graphics off low medium high ultra beautiful", true, () => DrawPictureQuality(v));
            Add(TabVideo, "Picture", "About picture settings", "graphics quality beautiful pretty post processing effects", true, () =>
                Note("None of these add input delay; they only cost graphics card time. Players, loot and their colours are never shaded, fogged or darkened, and players never cast shadows, so turning these on or off never shows or hides anyone. Competitive turns them all off."));
            Add(TabVideo, "Picture", "Shadows", "shadows sun shadow quality cascades", true, () => LookChoice("Sun shadows", ref v.Look.Shadows, LookValues.ShadowNames, "Low: hard, 40 m. High: soft, 80 m. Ultra: soft, 150 m, sharper."));
            Add(TabVideo, "Picture", "Surface detail", "detail normals texture sheen surface quality", false, () => LookChoice("Surface detail (fine relief and sheen on walls, ground and bark)", ref v.Look.Detail, LookValues.OffOnNames, null));
            Add(TabVideo, "Picture", "Grass", "grass density foliage blades flowers", false, () => LookChoice("Grass", ref v.Look.Grass, LookValues.GrassNames, "Grass is always short and flattens round players, so it never hides anyone on any setting; this only changes how many blades are drawn."));
            Add(TabVideo, "Picture", "Ambient occlusion", "ambient occlusion ao ssao contact shadows corners", true, () => LookChoice("Ambient occlusion (soft shade in corners)", ref v.Look.Ao, LookValues.LowHighNames, null));
            Add(TabVideo, "Picture", "Lighting", "lighting sun light sky bounce rich flat", true, () => LookChoice("Lighting", ref v.Look.Lighting, LookValues.LightingNames, "Rich: warm sun on the sunny side, cool sky light in shade, a little bounce from the ground."));
            Add(TabVideo, "Picture", "Fog", "fog haze atmosphere volumetric atmospheric distance", false, () => FogChoice(v));
            Add(TabVideo, "Picture", "Light shafts", "light shafts god rays sun rays volumetric light", false, () => { if (Toggle("Light shafts (rays round the sun)", ref v.Look.Shafts)) VideoChanged(); });
            Add(TabVideo, "Picture", "Tonemapping", "tonemapping hdr filmic tone mapping", false, () => LookChoice("Tonemapping", ref v.Look.Tonemap, LookValues.TonemapNames, "Filmic rolls off bright highlights softly and keeps every hue true, so team and rarity colours never shift."));
            Add(TabVideo, "Picture", "Colour grading", "colour color grading saturation contrast vivid natural", false, () => LookChoice("Colour grading", ref v.Look.Grading, LookValues.GradingNames, null));
            Add(TabVideo, "Picture", "Bloom", "bloom glow bright", false, () => LookChoice("Bloom", ref v.Look.Bloom, LookValues.LowHighNames, "Only things brighter than white glow: the sun, sparks and blasts. Walls and players never do."));
            Add(TabVideo, "Picture", "Sharpening", "sharpen sharpening cas crisp clarity", false, () => LookSlider("Sharpening", ref v.Look.Sharpen));
            Add(TabVideo, "Picture", "Vignette", "vignette dark edges corners", false, () => LookSlider("Vignette (darker screen edges)", ref v.Look.Vignette));
            Add(TabVideo, "Picture", "Film grain", "film grain noise", false, () => LookSlider("Film grain", ref v.Look.Grain));
            Add(TabVideo, "Picture", "Depth of field", "depth of field dof blur background bokeh", false, () => LookChoice("Depth of field (off by default)", ref v.Look.Dof, LookValues.DofNames, v.Look.Dof > 0 ? "Blurs only past 90 m, so it can soften very distant players. Never near you or your gun." : null));
            Add(TabVideo, "Picture", "Motion blur", "motion blur camera blur", false, () => LookChoice("Motion blur (off by default)", ref v.Look.MotionBlur, LookValues.LowHighNames, v.Look.MotionBlur > 0 ? "Blurs the view while you turn (your gun too). Most competitive players keep it off." : null));

            // ---- Latency and overlays ----
            Add(TabLatency, "Latency", "Low latency mode", "reflex low latency frame queue input lag delay nvidia amd anti lag", true, () =>
            {
                if (Toggle("Low latency mode (one queued frame)", ref v.LowLatency)) VideoChanged();
                Note("Like Reflex or Anti-Lag: the graphics driver may hold one finished frame instead of two, so a click shows up to a frame sooner when the graphics card is the limit. Works on every graphics card. Costs a little average FPS.");
            });
            Add(TabLatency, "Latency", "Lowest latency tips", "tips latency", false, () =>
                Note("Lowest latency: Competitive preset, V-Sync off, low latency on. On a G-Sync or FreeSync display, frame limit \"Display - 3\" keeps it in its range with no tearing. The game ticks at " + SimTime.TicksPerSecond + " Hz."));
            Add(TabLatency, "Overlays", "Show FPS", "fps counter frame rate overlay", true, () => { if (Toggle("Show FPS", ref v.ShowFps)) VideoChanged(); });
            Add(TabLatency, "Overlays", "Show latency panel", "latency panel click to screen overlay debug", false, () =>
            {
                if (Toggle("Show latency panel" + (GameInput.DevBuild ? "  (F3)" : ""), ref v.ShowLatency)) VideoChanged();
                Note("Breaks every click down: local PC, network up, server, network down and display, plus frame times and a trace.");
            });
            Add(TabLatency, "Overlays", "Show network stats", "network ping net graph netgraph packets loss snapshot bandwidth debug", false, () =>
            {
                if (Toggle("Show network stats", ref v.ShowNetStats)) VideoChanged();
                Note("Ping, snapshot data rate, prediction corrections, the server's input lead and dropped packets.");
            });

            // ---- Mouse and aim ----
            Add(TabMouse, "Sensitivity", "Mouse sensitivity", "sens sensitivity mouse speed", true, () =>
            {
                float n = NumberSlider("Mouse sensitivity", s.Sensitivity, ClientSettings.MinSensitivity, ClientSettings.MaxSensitivity, "0.000", ref _sensText);
                if (n != s.Sensitivity) { s.Sensitivity = n; Changed(); }
            });
            Add(TabMouse, "Sensitivity", "Mouse DPI", "dpi cpi mouse", true, () =>
            {
                int n = IntField("Mouse DPI (for the readout only)", s.MouseDpi, ref _dpiText);
                n = AimMath.ClampDpi(n);
                if (n != s.MouseDpi) { s.MouseDpi = n; Changed(); }
            });
            Add(TabMouse, "Sensitivity", "cm/360", "cm 360 inch distance edpi readout turn", true, DrawCm360);
            Add(TabMouse, "Sensitivity", "Vertical multiplier", "vertical y axis up down", false, () =>
            {
                float n = Slider("Vertical multiplier", s.VerticalMultiplier, 0.25f, 2f, s.VerticalMultiplier.ToString("0.00") + "x");
                n = Mathf.Round(n * 100f) / 100f;
                if (n != s.VerticalMultiplier) { s.VerticalMultiplier = n; Changed(); }
            });
            Add(TabMouse, "Sensitivity", "Invert vertical look", "invert y inverted flight", false, () => { if (Toggle("Invert vertical look", ref s.InvertY)) Changed(); });
            Add(TabMouse, "Zoom (ADS)", "ADS sensitivity multiplier", "ads aim down sights zoom scope multiplier", false, () =>
            {
                float n = Slider("ADS multiplier (AR and pistol zoom)", s.AdsMultiplier, 0.2f, 2f, s.AdsMultiplier.ToString("0.00") + "x");
                n = Mathf.Round(n * 100f) / 100f;
                if (n != s.AdsMultiplier) { s.AdsMultiplier = n; Changed(); }
            });
            Add(TabMouse, "Zoom (ADS)", "Zoom-matched ADS", "zoom matched monitor distance coefficient ads", false, () =>
            {
                if (Toggle("Zoom-matched (scale by the zoom too)", ref s.AdsZoomMatched)) Changed();
                Note(AdsLine());
            });
            Add(TabMouse, "Input", "Raw input", "raw input acceleration smoothing pointer precision windows", false, () =>
                Note("Mouse input is always raw: the game reads the mouse's own counts, with no Windows pointer speed, acceleration or smoothing, so there is nothing to switch. Look is applied every frame, not every tick."));

            // ---- Controls ----
            Add(TabControls, "Hold or toggle", "Sprint", "sprint hold toggle run", true, () => { bool t = HoldToggle("Sprint", s.SprintToggle); if (t != s.SprintToggle) { s.SprintToggle = t; Changed(); } });
            Add(TabControls, "Hold or toggle", "Crouch and slide", "crouch slide hold toggle", true, () => { bool t = HoldToggle("Crouch and slide", s.CrouchToggle); if (t != s.CrouchToggle) { s.CrouchToggle = t; Changed(); } });
            Add(TabControls, "Hold or toggle", "Zoom", "zoom aim ads hold toggle right mouse", true, () => { bool t = HoldToggle("Zoom (right mouse, AR and pistol)", s.ZoomToggle); if (t != s.ZoomToggle) { s.ZoomToggle = t; Changed(); } });
            Add(TabControls, "Inventory", "Auto-sort on pickup", "inventory slots sort auto pickup order", true, DrawSlotPrefs);

            // ---- Key binds ----
            Add(TabBinds, "Key binds", "Reset all binds", "keybinds reset default", true, DrawBindsHeader);
            AddBindRows();

            // ---- Crosshair ----
            Add(TabCrosshair, "Crosshair", "Crosshair colour", "crosshair colour color reticle", true, DrawCrosshair);

            // ---- HUD and view ----
            Add(TabHud, "View", "Field of view", "fov field of view", true, () =>
            {
                float n = Slider("Field of view (horizontal at 16:9)", s.Fov, ClientSettings.MinFov, ClientSettings.MaxFov, Mathf.RoundToInt(s.Fov).ToString());
                n = Mathf.Round(n);
                if (n != s.Fov) { s.Fov = n; Changed(); }
            });
            Add(TabHud, "View", "Weapon size", "viewmodel gun size weapon", false, () =>
            {
                float n = Slider("Weapon size", s.ViewmodelSize, ClientSettings.MinViewmodel, ClientSettings.MaxViewmodel, s.ViewmodelSize.ToString("0.00"));
                n = Mathf.Round(n * 100f) / 100f;
                if (n != s.ViewmodelSize) { s.ViewmodelSize = n; Changed(); }
            });
            Add(TabHud, "View", "Head bob", "head bob slide lean motion sickness", false, () => { if (Toggle("Head bob and slide lean", ref s.HeadBob)) Changed(); });
            Add(TabHud, "View", "FOV kick at high speed", "fov kick speed", false, () => { if (Toggle("FOV kick at high speed", ref s.SpeedFovKick)) Changed(); });
            Add(TabHud, "View", "Speed lines", "speed lines streaks", false, () => { if (Toggle("Speed lines", ref s.SpeedLines)) Changed(); });
            Add(TabHud, "HUD", "Hit markers", "hitmarker hit marker", false, () => { if (Toggle("Hit markers", ref s.HitMarkers)) Changed(); });
            Add(TabHud, "HUD", "Damage numbers", "damage numbers", false, () => { if (Toggle("Damage numbers", ref s.DamageNumbers)) Changed(); });
            Add(TabHud, "HUD", "Kill feed", "kill feed killfeed", false, () => { if (Toggle("Kill feed", ref s.KillFeed)) Changed(); });
            Add(TabHud, "HUD", "Compass", "compass heading", false, () => { if (Toggle("Compass", ref s.Compass)) Changed(); });

            // ---- Audio ----
            Add(TabAudio, "Volume", "Master volume", "volume master sound", true, () => Volume("Master volume", ref s.MasterVolume));
            Add(TabAudio, "Volume", "Weapons", "volume guns weapons shots explosions grenades", false, () => Volume("Weapons and explosions", ref s.WeaponsVolume));
            Add(TabAudio, "Volume", "Footsteps and movement", "volume footsteps movement steps slides", false, () => Volume("Footsteps and movement", ref s.MovementVolume));
            Add(TabAudio, "Volume", "World", "volume walls breaking doors heal", false, () => Volume("World (walls, doors, heals)", ref s.WorldVolume));
            Add(TabAudio, "Volume", "Interface", "volume ui hit confirm hitsound kill ping", false, () => Volume("Interface (hit and kill confirms, ping)", ref s.UiVolume));
            Add(TabAudio, "Volume", "Ambience", "volume ambience wind birds", false, () => Volume("Ambience (wind, birds)", ref s.AmbienceVolume));
            Add(TabAudio, "Directional sound", "Front and back cue", "directional head shadow behind front back hrtf spatial", false, () =>
            {
                float n = Slider("Front/back cue strength", s.HeadShadow, 0f, 2f, Mathf.RoundToInt(s.HeadShadow * 100f) + "%");
                n = Mathf.Round(n * 20f) / 20f;
                if (n != s.HeadShadow) { s.HeadShadow = n; Changed(); }
                Note("Sounds behind you are a little quieter and duller, so front and back differ on plain stereo. 100% is as designed.");
            });
            Add(TabAudio, "Directional sound", "Clearer muffled sounds", "muffled through walls clarity hearing directional", false, () =>
            {
                if (Toggle("Clearer muffled sounds", ref s.ClearerMuffled)) Changed();
                Note("Sounds through walls and round corners keep more of their top end, so they are easier to make out. Never louder, and a wall still sounds duller than a doorway.");
            });
            Add(TabAudio, "Directional sound", "Test sounds", "test sound left right behind speakers headphones", true, DrawTestSounds);

            // ---- Accessibility ----
            Add(TabAccess, "Colour", "Colourblind mode", "colourblind colorblind color colour blind deuteranopia protanopia tritanopia rarity", true, DrawColourMode);
            Add(TabAccess, "Sound", "Captions for key sounds", "subtitles captions deaf hearing footsteps", true, () =>
            {
                if (Toggle("Captions for key sounds", ref s.Captions)) { Changed(); if (!s.Captions) SoundCaptions.Clear(); }
                Note("Footsteps, landings, slides, gunfire, explosions, C4, wall breaks, heals and doors, with an arrow the way each sound reaches you. Only sounds you can actually hear get one.");
            });
            Add(TabAccess, "Sound", "Mono audio", "mono one ear single speaker hearing", false, () =>
            {
                if (Toggle("Mono audio", ref s.MonoAudio)) Changed();
                Note("Both ears get the same mix. Distance, muffling and the captions still tell you where things are.");
            });
            Add(TabAccess, "Motion", "Camera motion", "camera shake motion sickness screen shake landing dip comfort", true, () =>
            {
                float n = Slider("Camera motion (gun shake, landing dip)", s.CameraShake, 0f, 1f, Mathf.RoundToInt(s.CameraShake * 100f) + "%");
                n = Mathf.Round(n * 20f) / 20f;
                if (n != s.CameraShake) { s.CameraShake = n; Changed(); }
                Note("Only the cosmetic roll and FOV punch: real recoil is aim and always shows. Head bob is under HUD and view.");
            });

            // ---- Profile ----
            Add(TabProfile, "Settings code", "Copy or paste a settings code", "export import share copy paste code profile another pc sync", true, DrawProfile);
            Add(TabProfile, "Reset", "Reset every setting", "reset defaults everything", false, DrawResetAll);
        }

        private static readonly int[] MsaaChoices = { 1, 2, 4, 8 };
        private static readonly string[] MsaaNames = { "Off", "2x", "4x", "8x" };

        // ---------------------------------------------------------------- Open, close and keys

        private void Update()
        {
            if (_player == null) return;
            AudioListener.volume = _settings.MasterVolume;
            var kb = Keyboard.current;
            if (_rebind != null)
            {
                // The wheel is not a button the rebinder listens for: catch it by hand.
                var mouse = Mouse.current;
                if (mouse != null)
                {
                    float y = mouse.scroll.ReadValue().y;
                    if (y > 0.1f) FinishRebindWith("<Mouse>/scroll/up");
                    else if (y < -0.1f) FinishRebindWith("<Mouse>/scroll/down");
                }
                return;
            }
            if (_open) UpdateDisplayRevert();
            if (kb == null || !kb.escapeKey.wasPressedThisFrame || Time.frameCount - _rebindEndedFrame < 2) return;
            if (_open)
            {
                if (Time.frameCount == _openedFrame) return;
                if (_search.Length > 0) { _search = ""; GUI.FocusControl(null); return; } // Esc clears the search first
                Close();
            }
            else if (!_player.InputBlocked && Time.frameCount != MainMenu.ClosedFrame) Open(); // not while another box (join address) has the keyboard
        }

        /// <summary>Opens the settings (Esc in game, or the home screen's gear and Esc).</summary>
        public void Open()
        {
            _openedFrame = Time.frameCount;
            _open = true;
            _player.InputBlocked = true;
            LocalPlayer.LockCursor(false);
            _sensText = null; _dpiText = null; _cmText = null;
        }

        private void Close()
        {
            CancelRebind();
            if (_displayRevertAt > 0) KeepDisplay();
            _open = false;
            Save();
            ClosedFrame = Time.frameCount;
            // Back to the home screen if it is up underneath: keep the mouse free and the player still.
            bool home = GetComponent<MainMenu>() is MainMenu m && m.IsOpen;
            _player.InputBlocked = home;
            LocalPlayer.LockCursor(!home);
        }

        private void Save()
        {
            _dirty = false;
            _settings.Clamped().Save();
            _video.Save();
            _input.SaveOverrides();
        }

        // ---------------------------------------------------------------- Drawing

        private void Styles()
        {
            if (_title != null) return;
            _panel = new Texture2D(1, 1);
            _panel.SetPixel(0, 0, new Color(0.07f, 0.075f, 0.085f, 0.95f));
            _panel.Apply();
            _white = Texture2D.whiteTexture;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold };
            _title.normal.textColor = Color.white;
            _header = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold };
            _header.normal.textColor = new Color(1f, 0.85f, 0.5f);
            _label = new GUIStyle(GUI.skin.label) { fontSize = 15 };
            _label.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            _value = new GUIStyle(_label) { alignment = TextAnchor.MiddleRight };
            _small = new GUIStyle(_label) { fontSize = 13 };
            _small.normal.textColor = new Color(0.75f, 0.78f, 0.82f);
            _note = new GUIStyle(_label) { fontSize = 13, wordWrap = true, padding = new RectOffset(18, 4, 0, 4) };
            _note.normal.textColor = new Color(0.68f, 0.71f, 0.75f);
            _button = new GUIStyle(GUI.skin.button) { fontSize = 14 };
            _bindButton = new GUIStyle(GUI.skin.button) { fontSize = 13 };
            _sub = new GUIStyle(_label) { fontStyle = FontStyle.Bold, fontSize = 15 };
            _sub.normal.textColor = new Color(0.72f, 0.8f, 0.88f);
            _warn = new GUIStyle(_label) { fontStyle = FontStyle.Bold, wordWrap = true };
            _warn.normal.textColor = new Color(1f, 0.72f, 0.2f);
            _tabStyle = new GUIStyle(GUI.skin.button) { fontSize = 15, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(14, 8, 6, 6) };
            _tabOn = new GUIStyle(_tabStyle) { fontStyle = FontStyle.Bold };
            _tabOn.normal.textColor = _tabOn.hover.textColor = new Color(1f, 0.85f, 0.5f);
            _tabOn.normal.background = _tabOn.active.background;
            _field = new GUIStyle(GUI.skin.textField) { fontSize = 15, padding = new RectOffset(8, 8, 5, 5) };
        }

        private bool Visible(Entry e) => !_settings.BeginnerView || e.Beginner;

        private bool TabVisible(int tab)
        {
            foreach (Entry e in _entries) if (e.Tab == tab && Visible(e)) return true;
            return false;
        }

        private void OnGUI()
        {
            if (!_open || _player == null) return;
            Styles();
            // Lay out at 1080p and scale to the screen.
            float k = Screen.height / 1080f;
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(k, k, 1));
            float w = Screen.width / k, h = 1080f;
            float bw = Mathf.Min(1240f, w - 40f);
            var box = new Rect(w / 2f - bw / 2f, 50, bw, h - 100);
            GUI.DrawTexture(box, _panel);

            // Title and search.
            GUILayout.BeginArea(new Rect(box.x + 24, box.y + 16, box.width - 48, 50));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Settings", _title, GUILayout.Width(220));
            GUILayout.FlexibleSpace();
            GUILayout.Label("Search", _small, GUILayout.Width(56));
            GUI.SetNextControlName("settings-search");
            string q = GUILayout.TextField(_search, 40, _field, GUILayout.Width(420), GUILayout.Height(32));
            if (q != _search) { _search = q; _scroll = Vector2.zero; }
            if (_search.Length > 0 && GUILayout.Button("Clear", _button, GUILayout.Width(70), GUILayout.Height(32))) { _search = ""; GUI.FocusControl(null); }
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            Event ev = Event.current;
            if (ev.type == EventType.KeyDown && ev.control && ev.keyCode == KeyCode.F) { GUI.FocusControl("settings-search"); ev.Use(); }

            // Pages.
            var tabs = new Rect(box.x + 20, box.y + 82, 230, box.height - 160);
            GUILayout.BeginArea(tabs);
            bool searching = _search.Trim().Length > 0;
            for (int t = 0; t < Tabs.Length; t++)
            {
                if (!TabVisible(t)) continue;
                if (!TabVisible(_tab)) _tab = t;
                if (GUILayout.Button(Tabs[t], !searching && t == _tab ? _tabOn : _tabStyle, GUILayout.Height(38)))
                {
                    _tab = t; _scroll = Vector2.zero;
                    if (searching) { _search = ""; GUI.FocusControl(null); }
                }
            }
            GUILayout.EndArea();

            // The page (or the search results).
            var content = new Rect(box.x + 270, box.y + 82, box.width - 294, box.height - 160);
            GUILayout.BeginArea(content);
            if (_displayRevertAt > 0) DrawDisplayRevert();
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
            if (searching) DrawSearch();
            else DrawTab(_tab);
            GUILayout.Space(20);
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            // Bottom bar: Beginner view in the corner (Ben), reset this page, resume / leave / quit.
            GUILayout.BeginArea(new Rect(box.x + 20, box.yMax - 64, box.width - 40, 48));
            GUILayout.BeginHorizontal();
            bool beginner = GUILayout.Toggle(_settings.BeginnerView, _settings.BeginnerView ? " Beginner view: on" : " Beginner view: off", GUILayout.Width(200), GUILayout.Height(36));
            if (beginner != _settings.BeginnerView) { _settings.BeginnerView = beginner; Changed(); _scroll = Vector2.zero; }
            GUILayout.Label(_settings.BeginnerView ? "Showing the everyday settings" : "Showing everything", _small, GUILayout.Width(230), GUILayout.Height(36));
            GUILayout.FlexibleSpace();
            string group = !searching ? TabGroups[_tab] : null;
            if (!searching && _tab == TabBinds && GUILayout.Button("Reset this page", _button, GUILayout.Width(150), GUILayout.Height(36))) ResetBinds();
            if (group != null && GUILayout.Button("Reset this page", _button, GUILayout.Width(150), GUILayout.Height(36)))
            {
                _settings.ResetGroup(group);
                _video.ResetGroup(group);
                if (group == SettingsGroup.Controls) ApplySlotPrefs(SlotPrefs.Default); // the auto-sort row is on this page too
                _sensText = _dpiText = _cmText = null;
                _dirty = true;
            }
            GUILayout.Space(16);
            if (GUILayout.Button("Resume (Esc)", _button, GUILayout.Width(150), GUILayout.Height(36))) Close();
            if (GUILayout.Button("Leave to main menu", _button, GUILayout.Width(180), GUILayout.Height(36))) { Close(); Sculpt.Game.Bootstrap.GameBootstrap.Relaunch(Sculpt.Game.Bootstrap.GameBootstrap.Launch.Menu, Sculpt.Game.Bootstrap.GameBootstrap.MapChoice.Duel); } // the home screen stands on the 1v1 map
            if (GUILayout.Button("Quit game", _button, GUILayout.Width(120), GUILayout.Height(36))) Application.Quit();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
            GUI.matrix = old;

            // Save once the mouse lets go, not on every step of a slider drag.
            if (_dirty && GUIUtility.hotControl == 0 && ev.type == EventType.Repaint) Save();
        }

        private void DrawTab(int tab)
        {
            GUILayout.Label(Tabs[tab], _header);
            string section = null;
            foreach (Entry e in _entries)
            {
                if (e.Tab != tab || !Visible(e)) continue;
                if (e.Section != section && tab != TabBinds)
                {
                    section = e.Section;
                    GUILayout.Space(8);
                    GUILayout.Label(section, _sub);
                }
                e.Draw();
            }
        }

        private void DrawSearch()
        {
            List<string> q = SettingsSearch.QueryWords(_search);
            // A bind changed from the results can clash: the Swap prompt lives in the binds header, which the search may hide.
            if (_clashA != null) { DrawClash(); GUILayout.Space(6); }
            int lastTab = -1, found = 0;
            foreach (Entry e in _entries)
            {
                if (!Visible(e) || !SettingsSearch.Matches(q, e.Words)) continue;
                if (e.Tab != lastTab)
                {
                    lastTab = e.Tab;
                    GUILayout.Space(found == 0 ? 0 : 10);
                    GUILayout.Label(Tabs[e.Tab] + (e.Tab == TabBinds ? "" : "  ·  " + e.Section), _header);
                }
                e.Draw();
                found++;
            }
            if (found == 0)
            {
                GUILayout.Label("Nothing matches \"" + _search.Trim() + "\".", _label);
                if (_settings.BeginnerView) GUILayout.Label("Beginner view hides some settings: switch it off (bottom left) to search everything.", _note);
            }
        }

        // ---------------------------------------------------------------- Widgets

        private const float LabelW = 360f;

        private float Slider(string name, float v, float min, float max, string shown)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(LabelW));
            float n = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(330));
            GUILayout.Label(shown, _value, GUILayout.Width(80));
            GUILayout.EndHorizontal();
            return n;
        }

        private bool Toggle(string name, ref bool on)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(LabelW));
            bool n = GUILayout.Toggle(on, on ? " On" : " Off");
            GUILayout.EndHorizontal();
            if (n == on) return false;
            on = n;
            return true;
        }

        private int Choice(string name, int at, string[] names)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(LabelW));
            for (int i = 0; i < names.Length; i++)
                if (GUILayout.Toggle(i == at, names[i], _button, GUILayout.MinWidth(70), GUILayout.Height(28)) && i != at) at = i;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return at;
        }

        private bool HoldToggle(string name, bool toggle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(LabelW));
            if (GUILayout.Toggle(!toggle, " Hold", GUILayout.Width(80)) && toggle) toggle = false;
            if (GUILayout.Toggle(toggle, " Toggle", GUILayout.Width(90)) && !toggle) toggle = true;
            GUILayout.EndHorizontal();
            return toggle;
        }

        private void Volume(string name, ref float v)
        {
            float n = Slider(name, v, 0f, 1f, Mathf.RoundToInt(v * 100f) + "%");
            n = Mathf.Round(n * 100f) / 100f;
            if (n != v) { v = n; Changed(); }
        }

        private void Note(string text) => GUILayout.Label(text, _note, GUILayout.MaxWidth(880));

        /// <summary>Fog: the distance haze is the same for everyone and always on; Atmospheric adds height fog to the scenery.</summary>
        private void FogChoice(VideoSettings v)
        {
            int n = Choice("Fog", Mathf.Clamp(v.Look.Fog - 1, 0, 1), LookValues.FogChoices) + 1;
            if (n != v.Look.Fog) { v.Look.Fog = n; VideoChanged(); }
            Note("The distance haze is the same on every setting, so nobody sees further. Atmospheric adds height fog to the scenery that glows toward the sun; players are never fogged by it.");
        }

        private void LookChoice(string name, ref int value, string[] names, string note)
        {
            int n = Choice(name, Mathf.Clamp(value, 0, names.Length - 1), names);
            if (n != value) { value = n; VideoChanged(); }
            if (note != null) Note(note);
        }

        private void LookSlider(string name, ref float value)
        {
            float n = Slider(name, value, 0f, 1f, Mathf.RoundToInt(value * 100f) + "%");
            n = Mathf.Round(n * 20f) / 20f; // 5% steps
            if (!Mathf.Approximately(n, value)) { value = n; VideoChanged(); }
        }

        private string _sensText, _dpiText, _cmText;

        /// <summary>A slider with a box you can type the exact number into (Enter or leaving the box applies it).</summary>
        private float NumberSlider(string name, float v, float min, float max, string fmt, ref string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(LabelW));
            float n = GUILayout.HorizontalSlider(v, min, max, GUILayout.Width(330));
            if (n != v) { n = Mathf.Round(n * 100f) / 100f; text = null; }
            text ??= v.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
            string t = GUILayout.TextField(text, 8, _field, GUILayout.Width(90));
            if (t != text)
            {
                text = t;
                if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float typed) && typed >= min && typed <= max) n = typed;
            }
            GUILayout.EndHorizontal();
            return n;
        }

        private int IntField(string name, int v, ref string text)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(name, _label, GUILayout.Width(LabelW));
            text ??= v.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string t = GUILayout.TextField(text, 6, _field, GUILayout.Width(110));
            int n = v;
            if (t != text) { text = t; if (int.TryParse(t, out int typed) && typed >= AimMath.MinDpi && typed <= AimMath.MaxDpi) n = typed; }
            foreach (int d in new[] { 400, 800, 1600, 3200 })
                if (GUILayout.Button(d.ToString(), _button, GUILayout.Width(64))) { n = d; text = null; }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            return n;
        }

        // ---------------------------------------------------------------- Video

        /// <summary>One step for the whole picture (Off .. Ultra); any single change below shows as Custom.</summary>
        private void DrawPictureQuality(VideoSettings v)
        {
            PictureQuality now = PictureLadder.Detect(v.Look);
            int at = now == PictureQuality.Custom ? -1 : (int)now;
            int n = Choice("Picture quality (now: " + PictureLadder.Name(now) + ")", at, PictureLadder.Names); // -1: none lit (Custom)
            if (n >= 0 && n != at) { v.Look = PictureLadder.Of(PictureLadder.Steps[n]); VideoChanged(); }
            Note(now == PictureQuality.Off ? "Off: the clean competitive picture, no lighting or effects. The fastest."
                : now == PictureQuality.Low ? "Low: sun and sky light on the world, filmic colour, FXAA, full grass."
                : now == PictureQuality.Medium ? "Medium: adds sun shadows to 40 m, soft corner shade, a little bloom, SMAA and sharpening."
                : now == PictureQuality.High ? "High: soft shadows to 80 m, deeper corner shade, atmospheric fog, light shafts, surface detail."
                : now == PictureQuality.Ultra ? "Ultra: shadows to 150 m, stronger bloom, dense grass. Needs a strong graphics card."
                : "Custom: you changed something below. Pick a step to go back to a set picture.");
        }

        private void DrawPreset()
        {
            VideoPreset now = _video.Preset;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Preset (now: " + DisplayPresets.Name(now) + ")", _label, GUILayout.Width(LabelW));
            if (GUILayout.Toggle(now == VideoPreset.Competitive, "Competitive", _button, GUILayout.Width(150), GUILayout.Height(30)) && now != VideoPreset.Competitive)
            { _video.Values = DisplayPresets.Of(VideoPreset.Competitive); _dirty = true; }
            if (GUILayout.Toggle(now == VideoPreset.Quality, "Quality", _button, GUILayout.Width(150), GUILayout.Height(30)) && now != VideoPreset.Quality)
            { _video.Values = DisplayPresets.Of(VideoPreset.Quality); _dirty = true; }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            Note(now == VideoPreset.Quality
                ? "Quality: 4x anti-aliasing, a frame limit just under your display's refresh for an even pace, and the full picture: soft shadows, ambient occlusion, rich lighting, atmospheric fog, light shafts, filmic tonemapping, natural grading, bloom, light sharpening and vignette. Low latency stays on."
                : "Competitive: highest frame rate and lowest latency, and the clean picture. V-Sync off, no frame limit, one queued frame, no anti-aliasing, no post-processing, shadows or ambient occlusion.");
        }

        private static readonly FullScreenMode[] Modes = { FullScreenMode.ExclusiveFullScreen, FullScreenMode.FullScreenWindow, FullScreenMode.Windowed };
        private static readonly string[] ModeNames = { "Fullscreen", "Borderless", "Windowed" };
        private float _displayRevertAt;
        private int _revertW, _revertH;
        private FullScreenMode _revertMode;

        private void DrawDisplayMode()
        {
            FullScreenMode cur = Screen.fullScreenMode == FullScreenMode.MaximizedWindow ? FullScreenMode.Windowed : Screen.fullScreenMode;
            int at = Mathf.Max(0, Array.IndexOf(Modes, cur));
            int n = Choice("Display mode", at, ModeNames);
            if (n != at) SetDisplay(Screen.width, Screen.height, Modes[n]);
            if (Application.isEditor) Note("Display mode and resolution only change in a built game, not in the Editor's Game view.");
            else Note("Fullscreen (exclusive) can have the least delay on some PCs; borderless makes alt-tab instant.");
        }

        private readonly List<Vector2Int> _resolutions = new List<Vector2Int>();
        private float _resolutionsAt = -10f;

        private void DrawResolution()
        {
            if (Time.unscaledTime - _resolutionsAt > 5f)
            {
                _resolutionsAt = Time.unscaledTime;
                _resolutions.Clear();
                foreach (Resolution r in Screen.resolutions)
                {
                    var v = new Vector2Int(r.width, r.height);
                    if (!_resolutions.Contains(v)) _resolutions.Add(v);
                }
                _resolutions.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            }
            var cur = new Vector2Int(Screen.width, Screen.height);
            int at = _resolutions.IndexOf(cur);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Resolution", _label, GUILayout.Width(LabelW));
            GUI.enabled = _resolutions.Count > 0;
            if (GUILayout.Button("<", _button, GUILayout.Width(32)) && _resolutions.Count > 0)
            {
                Vector2Int r = _resolutions[at < 0 ? 0 : (at + _resolutions.Count - 1) % _resolutions.Count];
                SetDisplay(r.x, r.y, Screen.fullScreenMode);
            }
            GUILayout.Label(cur.x + " x " + cur.y + (at < 0 ? " (window)" : ""), _value, GUILayout.Width(170));
            if (GUILayout.Button(">", _button, GUILayout.Width(32)) && _resolutions.Count > 0)
            {
                Vector2Int r = _resolutions[at < 0 ? _resolutions.Count - 1 : (at + 1) % _resolutions.Count];
                SetDisplay(r.x, r.y, Screen.fullScreenMode);
            }
            GUI.enabled = true;
            GUILayout.Label("  " + RenderProfileRefresh() + " Hz display", _small);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private static string RenderProfileRefresh() => Mathf.RoundToInt((float)Rendering.RenderProfile.RefreshHz).ToString();

        /// <summary>Changes the display, keeping what it was so it can go back if the player can't see the screen to keep it.</summary>
        private void SetDisplay(int width, int height, FullScreenMode mode)
        {
            if (_displayRevertAt <= 0)
            {
                _revertW = Screen.width; _revertH = Screen.height;
                _revertMode = Screen.fullScreenMode;
            }
            Screen.SetResolution(width, height, mode);
            _displayRevertAt = Time.unscaledTime + 12f;
        }

        private void UpdateDisplayRevert()
        {
            if (_displayRevertAt > 0 && Time.unscaledTime > _displayRevertAt)
            {
                Screen.SetResolution(_revertW, _revertH, _revertMode);
                _displayRevertAt = 0;
            }
        }

        private void KeepDisplay() => _displayRevertAt = 0;

        private void DrawDisplayRevert()
        {
            GUILayout.BeginHorizontal();
            int secs = Mathf.CeilToInt(_displayRevertAt - Time.unscaledTime);
            GUILayout.Label("Keep this display setting? Going back in " + Mathf.Max(0, secs) + " s.", _warn, GUILayout.Width(420));
            if (GUILayout.Button("Keep", _button, GUILayout.Width(90), GUILayout.Height(30))) KeepDisplay();
            if (GUILayout.Button("Go back", _button, GUILayout.Width(100), GUILayout.Height(30))) _displayRevertAt = Time.unscaledTime - 1f;
            GUILayout.EndHorizontal();
        }

        private void DrawFpsCap()
        {
            VideoSettings v = _video;
            GUILayout.BeginHorizontal();
            GUILayout.Label(v.VSync ? "Frame rate limit (V-Sync sets it)" : "Frame rate limit", _label, GUILayout.Width(LabelW));
            int at = Mathf.Max(0, Array.IndexOf(VideoSettings.CapChoices, v.FpsCap));
            GUI.enabled = !v.VSync;
            if (GUILayout.Button("<", _button, GUILayout.Width(32))) { at = (at + VideoSettings.CapChoices.Length - 1) % VideoSettings.CapChoices.Length; v.FpsCap = VideoSettings.CapChoices[at]; VideoChanged(); }
            string name = VideoSettings.CapName(v.FpsCap);
            if (v.FpsCap == VideoSettings.CapBelowRefresh)
            {
                int cap = v.CapFor(Rendering.RenderProfile.RefreshHz);
                if (cap > 0) name += " (" + cap + ")";
            }
            GUILayout.Label(name, _value, GUILayout.Width(170));
            if (GUILayout.Button(">", _button, GUILayout.Width(32))) { at = (at + 1) % VideoSettings.CapChoices.Length; v.FpsCap = VideoSettings.CapChoices[at]; VideoChanged(); }
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        // ---------------------------------------------------------------- Mouse

        private void DrawCm360()
        {
            ClientSettings s = _settings;
            double cm = AimMath.CmPer360(s.Sensitivity, s.MouseDpi);
            GUILayout.BeginHorizontal();
            GUILayout.Label("cm per 360 turn", _label, GUILayout.Width(LabelW));
            GUILayout.Label(cm.ToString("0.0") + " cm  (" + (cm / AimMath.CmPerInch).ToString("0.0") + " in)", _header, GUILayout.Width(220));
            GUILayout.Label("eDPI " + AimMath.Edpi(s.Sensitivity, s.MouseDpi).ToString("0") + "   " + AimMath.DegreesPerCount(s.Sensitivity).ToString("0.0000") + " deg per count", _small);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("   Set sensitivity by cm/360", _small, GUILayout.Width(LabelW));
            _cmText ??= "";
            _cmText = GUILayout.TextField(_cmText, 6, _field, GUILayout.Width(90));
            if (GUILayout.Button("Set", _button, GUILayout.Width(60)) &&
                double.TryParse(_cmText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double want) && want > 0)
            {
                float sens = Mathf.Clamp((float)AimMath.SensitivityFor(want, s.MouseDpi), ClientSettings.MinSensitivity, ClientSettings.MaxSensitivity);
                s.Sensitivity = sens; _sensText = null; Changed();
            }
            GUILayout.Label("  Same scale as most competitive shooters (0.022 deg per count at 1), so you can copy a sensitivity over.", _small);
            GUILayout.EndHorizontal();
        }

        private string AdsLine()
        {
            ClientSettings s = _settings;
            WeaponsConfig w = _player.Config.Weapons;
            double ar = w.Of(Weapon.Ar).AimFovScale, pistol = w.Of(Weapon.Pistol).AimFovScale;
            double cmAr = AimMath.CmPer360(s.Sensitivity, s.MouseDpi, AimMath.AdsDegreesPerCount(1, s.AdsMultiplier, s.AdsZoomMatched, ar) / AimMath.DegreesPerCountAtOne);
            double cmPi = AimMath.CmPer360(s.Sensitivity, s.MouseDpi, AimMath.AdsDegreesPerCount(1, s.AdsMultiplier, s.AdsZoomMatched, pistol) / AimMath.DegreesPerCountAtOne);
            return "Zoomed: AR " + cmAr.ToString("0.0") + " cm/360, pistol " + cmPi.ToString("0.0") + " cm/360. Pump and SMG don't zoom, so right mouse never changes their sensitivity.";
        }

        // ---------------------------------------------------------------- Controls

        private void DrawSlotPrefs()
        {
            GUILayout.Label("Auto-sort when you pick something up (I opens the inventory to sort by hand)", _label);
            SlotPrefs prefs = NetSession.LocalSlotPrefs;
            bool changed = false;
            GUILayout.BeginHorizontal();
            for (int si = 0; si < Items.Slots; si++)
            {
                if (GUILayout.Button($"{si + 1}: {SlotPrefs.Name(prefs[si])}", _button, GUILayout.Width(140), GUILayout.Height(30)))
                {
                    prefs[si] = SlotPrefsStore.Next(prefs[si]);
                    changed = true;
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("   Click a slot to change what it is for.", _small);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Default (pump 1, AR 2, heals 5)", _button, GUILayout.Width(260))) { prefs = SlotPrefs.Default; changed = true; }
            GUILayout.EndHorizontal();
            if (changed) ApplySlotPrefs(prefs);
        }

        private void ApplySlotPrefs(in SlotPrefs prefs)
        {
            SlotPrefsStore.Save(prefs);
            if (_player.Session != null) _player.Session.SetLocalPrefs(prefs);
            else NetSession.LocalSlotPrefs = prefs;
        }

        // ---------------------------------------------------------------- Key binds

        private static string SectionOf(GameInput.Context c) =>
            c == GameInput.Context.Gun ? "Guns" : c == GameInput.Context.Sculpt ? "Sculpt mode (parked)" : c == GameInput.Context.Watch ? "Spectating and replays" : "Movement and actions";

        private void AddBindRows()
        {
            foreach (InputAction a in _input.Player.actions)
            {
                if (a == _input.Look) continue; // the mouse is the mouse
                GameInput.Context ctx = _input.ContextOf(a);
                bool beginner = ctx == GameInput.Context.Always || ctx == GameInput.Context.Gun;
                AddBindRowsFor(a, SectionOf(ctx), beginner);
            }
            if (GameInput.DevBuild)
                foreach (InputAction a in _input.Dev.actions) AddBindRowsFor(a, "Dev keys (dev builds only)", false);
        }

        private void AddBindRowsFor(InputAction a, string section, bool beginner)
        {
            int second = 0;
            for (int i = 0; i < a.bindings.Count; i++)
            {
                InputBinding b = a.bindings[i];
                if (b.isComposite) continue;
                string label = b.isPartOfComposite ? $"{a.name} {Nice(b.name)}" : (second++ == 0 ? a.name : $"{a.name} (second)");
                int index = i;
                InputAction action = a;
                string sec = section;
                Add(TabBinds, section, label, "bind key keybind button " + Nice(b.name), beginner, () => DrawBindRow(action, index, label, sec));
            }
        }

        private void DrawBindRow(InputAction a, int index, string label, string section)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _label, GUILayout.Width(LabelW));
            bool waiting = _rebind != null && _rebindAction == a && _rebindIndex == index;
            string shown = waiting ? "press a key..." : a.GetBindingDisplayString(index);
            if (string.IsNullOrEmpty(shown)) shown = "(none)";
            if (GUILayout.Button(shown, _bindButton, GUILayout.Width(230), GUILayout.Height(26)) && _rebind == null) StartRebind(a, index);
            if (GUILayout.Button("Clear", _bindButton, GUILayout.Width(60), GUILayout.Height(26)) && _rebind == null)
            {
                a.ApplyBindingOverride(index, "");
                _input.SaveOverrides();
            }
            GUILayout.Label(section, _small);
            GUILayout.EndHorizontal();
        }

        private void DrawBindsHeader()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(_rebind != null ? "Press a key or mouse button for the highlighted bind (Esc cancels)." : "Click a bind to change it. Clear leaves it unbound.", _label);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reset all binds", _button, GUILayout.Width(150))) ResetBinds();
            GUILayout.EndHorizontal();
            if (_search.Trim().Length == 0) DrawClash(); // search results draw it at their top
            GUILayout.Space(6);
        }

        /// <summary>The "also bound to" warning with Swap / Keep both (on the binds page and over search results).</summary>
        private void DrawClash()
        {
            if (_clashA != null)
            {
                string key = _clashA.GetBindingDisplayString(_clashAIndex);
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{key} is also bound to {_clashB.name}.", _warn, GUILayout.Width(380));
                string was = string.IsNullOrEmpty(_clashOldPathA) ? "nothing" : InputControlPath.ToHumanReadableString(_clashOldPathA, InputControlPath.HumanReadableStringOptions.OmitDevice);
                if (GUILayout.Button($"Swap ({_clashB.name} gets {was})", _button)) SwapClash();
                if (GUILayout.Button("Keep both", _button, GUILayout.Width(110))) _clashA = null;
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>After a bind changed: is its key also bound to another action that can be live at the same time?</summary>
        private void CheckClash(InputAction a, int index, string oldPath)
        {
            _clashA = null;
            string path = a.bindings[index].effectivePath;
            if (string.IsNullOrEmpty(path)) return;
            GameInput.Context ca = _input.ContextOf(a);
            foreach (InputActionMap map in new[] { _input.Player, _input.Dev })
            {
                if (map == _input.Dev && !GameInput.DevBuild) continue; // dev keys are off (and not shown) in a release build
                foreach (InputAction other in map.actions)
                {
                    if (other == a || other == _input.Look) continue; // a second bind on the same action is fine
                    if (!GameInput.Clashes(ca, _input.ContextOf(other))) continue;
                    for (int i = 0; i < other.bindings.Count; i++)
                    {
                        if (other.bindings[i].isComposite) continue;
                        if (!string.Equals(other.bindings[i].effectivePath, path, StringComparison.OrdinalIgnoreCase)) continue;
                        _clashA = a; _clashAIndex = index; _clashB = other; _clashBIndex = i; _clashOldPathA = oldPath;
                        return;
                    }
                }
            }
        }

        private void SwapClash()
        {
            if (_clashA == null) return;
            _clashB.ApplyBindingOverride(_clashBIndex, string.IsNullOrEmpty(_clashOldPathA) ? "" : _clashOldPathA);
            _clashA = null;
            _input.SaveOverrides();
        }

        private static string Nice(string part) => string.IsNullOrEmpty(part) ? "" : char.ToUpperInvariant(part[0]) + part.Substring(1);

        private void StartRebind(InputAction action, int index)
        {
            CancelRebind();
            _rebindAction = action;
            _rebindIndex = index;
            _rebindOldPath = action.bindings[index].effectivePath;
            _clashA = null;
            action.Disable();
            _rebind = action.PerformInteractiveRebinding(index)
                .WithControlsExcluding("<Mouse>/position")
                .WithControlsExcluding("<Mouse>/delta")
                .WithControlsExcluding("<Pointer>/position")
                .WithControlsExcluding("<Pointer>/delta")
                .WithCancelingThrough("<Keyboard>/escape")
                .OnMatchWaitForAnother(0.05f)
                .OnComplete(op => EndRebind(true))
                .OnCancel(op => EndRebind(false))
                .Start();
        }

        private void FinishRebindWith(string path)
        {
            InputAction a = _rebindAction;
            int i = _rebindIndex;
            string old = _rebindOldPath;
            CancelRebind();
            if (a == null) return;
            a.ApplyBindingOverride(i, path);
            _input.SaveOverrides();
            CheckClash(a, i, old);
        }

        private void EndRebind(bool saved)
        {
            InputAction a = _rebindAction;
            int index = _rebindIndex;
            _rebind?.Dispose();
            _rebind = null;
            _rebindAction = null;
            _rebindEndedFrame = Time.frameCount;
            a?.Enable();
            if (saved && a != null)
            {
                _input.SaveOverrides();
                CheckClash(a, index, _rebindOldPath);
            }
        }

        private void CancelRebind()
        {
            if (_rebind == null) return;
            var op = _rebind;
            op.Cancel(); // calls EndRebind(false)
            if (_rebind == op) EndRebind(false);
        }

        private void ResetBinds()
        {
            CancelRebind();
            _input.Player.RemoveAllBindingOverrides();
            _input.Dev.RemoveAllBindingOverrides();
            _clashA = null;
            _input.SaveOverrides();
        }

        // ---------------------------------------------------------------- Crosshair

        private void DrawCrosshair()
        {
            ClientSettings s = _settings;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Colour", _label, GUILayout.Width(LabelW));
            for (int i = 0; i < CrosshairColours.Names.Length; i++)
            {
                Color sw = i < CrosshairColours.Colours.Length ? ToColor(CrosshairColours.Colours[i]) : s.CrosshairColour;
                Rect r = GUILayoutUtility.GetRect(58, 46, GUILayout.Width(58), GUILayout.Height(46));
                if (GUI.Button(r, GUIContent.none, _button) && s.CrosshairChoice != i) { s.CrosshairChoice = i; Changed(); }
                Fill(new Rect(r.x + 6, r.y + 5, r.width - 12, 20), sw);
                if (s.CrosshairChoice == i) Fill(new Rect(r.x + 4, r.yMax - 6, r.width - 8, 3), new Color(1f, 0.85f, 0.5f));
                GUI.Label(new Rect(r.x, r.y + 24, r.width, 18), CrosshairColours.Names[i], new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, fontSize = 11 });
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (s.CrosshairChoice == CrosshairColours.Custom)
            {
                float r = Slider("   Red", s.CrosshairR, 0f, 1f, Mathf.RoundToInt(s.CrosshairR * 255f).ToString());
                float g = Slider("   Green", s.CrosshairG, 0f, 1f, Mathf.RoundToInt(s.CrosshairG * 255f).ToString());
                float b = Slider("   Blue", s.CrosshairB, 0f, 1f, Mathf.RoundToInt(s.CrosshairB * 255f).ToString());
                if (r != s.CrosshairR || g != s.CrosshairG || b != s.CrosshairB) { s.CrosshairR = r; s.CrosshairG = g; s.CrosshairB = b; Changed(); }
                if (ColourVision.IsPurple(new Rgb(r, g, b))) Note("Purple is kept for \"sculptable\", so this colour is drawn as the nearest blue or pink (shown in the preview).");
            }
            // Live preview on the three backdrops a crosshair has to read on.
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Preview", _label, GUILayout.Width(LabelW));
            Color c = s.CrosshairColour;
            foreach (Color bg in new[] { new Color(0.62f, 0.78f, 0.95f), new Color(0.62f, 0.33f, 0.24f), new Color(0.09f, 0.1f, 0.11f) })
            {
                Rect box = GUILayoutUtility.GetRect(120, 90, GUILayout.Width(120), GUILayout.Height(90));
                Fill(box, bg);
                PreviewCross(box.center, c);
                GUILayout.Space(10);
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            Note("The crosshair's gap still shows your real spread, and the pump still shows its pellet ring. Colour only for now; a full crosshair editor may come later.");
        }

        private void PreviewCross(Vector2 c, Color col)
        {
            const float gap = 6f, len = 9f, th = 2f;
            var dark = new Color(0, 0, 0, 0.55f);
            Rect[] arms =
            {
                new Rect(c.x - gap - len, c.y - th / 2, len, th), new Rect(c.x + gap, c.y - th / 2, len, th),
                new Rect(c.x - th / 2, c.y - gap - len, th, len), new Rect(c.x - th / 2, c.y + gap, th, len),
            };
            foreach (Rect a in arms) Fill(new Rect(a.x - 1, a.y - 1, a.width + 2, a.height + 2), dark);
            foreach (Rect a in arms) Fill(a, col);
            Fill(new Rect(c.x - 1, c.y - 1, 2, 2), col);
        }

        private void Fill(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }

        private static Color ToColor(Rgb c) => new Color(c.R, c.G, c.B, 1f);

        // ---------------------------------------------------------------- Audio

        private void DrawTestSounds()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Test footsteps from", _label, GUILayout.Width(LabelW));
            Transform ear = SoundRouter.Listener;
            GUI.enabled = ear != null;
            if (GUILayout.Button("Left", _button, GUILayout.Width(80))) TestSound(-1, 0);
            if (GUILayout.Button("Right", _button, GUILayout.Width(80))) TestSound(1, 0);
            if (GUILayout.Button("Front", _button, GUILayout.Width(80))) TestSound(0, 1);
            if (GUILayout.Button("Behind", _button, GUILayout.Width(80))) TestSound(0, -1);
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            Note("Plays a footstep 4 m away with your current mix (movement volume, front/back cue, mono). Check your headphones are the right way round.");
        }

        private void TestSound(int right, int ahead)
        {
            Transform ear = SoundRouter.Listener;
            if (ear == null) return;
            Vector3 f = ear.forward; f.y = 0; f = f.sqrMagnitude > 0.001f ? f.normalized : Vector3.forward;
            Vector3 r = Vector3.Cross(Vector3.up, f);
            Vector3 at = ear.position + (r * right + f * ahead) * 4f;
            AudioClip[] steps = MovementAudio.Steps;
            if (steps == null || steps.Length == 0) return;
            // Not routed (nothing in the way matters for a test), but the head shadow is: apply it by hand.
            // Level and top end both, exactly as a routed sound gets them (the dulling is most of the cue).
            var clear = new Sculpt.Core.Audio.SoundRoute
            {
                Way = Sculpt.Core.Audio.SoundWay.Direct, ArrivesFrom = (at - ear.position).normalized.ToCore(), CutoffHz = SoundRouter.Config.OpenCutoffHz,
            };
            Sculpt.Core.Audio.SoundPaths.Hear(clear, ear.forward.ToCore(), _settings.HeadShadow, SoundRouter.Config, out double db, out double hz, out _);
            float gain = Mathf.Pow(10f, (float)db / 20f);
            Synth.Play(steps[UnityEngine.Random.Range(0, steps.Length)], at, 0.9f * gain, 1f, true, 1.5f, 30f, route: false, bus: SoundBus.Movement,
                       cutoffHz: SoundMix.Cutoff((float)hz, (float)SoundRouter.Config.OpenCutoffHz));
        }

        // ---------------------------------------------------------------- Accessibility

        private static readonly string[] ModeNames2 = { "Off", "Protanopia", "Deuteranopia", "Tritanopia" };
        private static readonly string[] RarityNames = { "Common", "Uncommon", "Rare", "Epic", "Legendary" };

        private void DrawColourMode()
        {
            ClientSettings s = _settings;
            int n = Choice("Colourblind mode", (int)s.Colours, ModeNames2);
            if (n != (int)s.Colours) { s.Colours = (ColourMode)n; Changed(); }
            Note(ColourVision.Name(s.Colours) + ". Changes the battle royale rarity colours (loot plates, hotbar, item names) to a set made for that eye. Team blue and orange already stay apart for every kind of colour blindness, so they don't change.");
            // Preview: the five rarities as you'll see them, and as that eye sees them.
            foreach (ColourMode seen in s.Colours == ColourMode.Off ? new[] { ColourMode.Off } : new[] { ColourMode.Off, s.Colours })
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(seen == ColourMode.Off ? "   Rarities" : "   Seen with " + ModeNames2[(int)seen].ToLowerInvariant(), _small, GUILayout.Width(LabelW));
                for (int i = 1; i <= 5; i++)
                {
                    Rgb c = ColourVision.Simulate(ColourVision.Rarity(s.Colours, i), seen);
                    Rect r = GUILayoutUtility.GetRect(96, 30, GUILayout.Width(96), GUILayout.Height(30));
                    Fill(new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4), new Color(0.05f, 0.06f, 0.08f));
                    GUI.Label(r, RarityNames[i - 1], new GUIStyle(_small) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, normal = { textColor = ToColor(c) } });
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
        }

        // ---------------------------------------------------------------- Profile

        private string _pasteText = "", _profileMessage = "";
        private bool _confirmReset;

        private const string BindsKey = "sculpt.binds", SlotsKey = "sculpt.slots";

        private Dictionary<string, string> Gather()
        {
            var map = new Dictionary<string, string>();
            _settings.WriteTo(map);
            _video.WriteTo(map);
            map[BindsKey] = GameInput.ExportOverrides(_input.Player);
            SlotPrefs p = NetSession.LocalSlotPrefs;
            var c = new char[Items.Slots];
            for (int k = 0; k < Items.Slots; k++) c[k] = (char)('0' + (int)p[k]);
            map[SlotsKey] = new string(c);
            return map;
        }

        private void DrawProfile()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Copy my settings code", _label, GUILayout.Width(LabelW));
            if (GUILayout.Button("Copy to clipboard", _button, GUILayout.Width(180), GUILayout.Height(30)))
            {
                string code = SettingsCode.Encode(Gather());
                GUIUtility.systemCopyBuffer = code;
                _profileMessage = "Copied (" + code.Length + " characters). Paste it on another PC under Profile.";
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Paste a settings code", _label, GUILayout.Width(LabelW));
            _pasteText = GUILayout.TextField(_pasteText ?? "", 20000, _field, GUILayout.Width(300));
            if (GUILayout.Button("Paste", _button, GUILayout.Width(70))) _pasteText = GUIUtility.systemCopyBuffer ?? "";
            if (GUILayout.Button("Apply", _button, GUILayout.Width(70))) ApplyCode(_pasteText);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (_profileMessage.Length > 0) GUILayout.Label(_profileMessage, _warn);
            Note("Everything in this menu, your binds and your auto-sort go into one line of text. Settings are saved on this PC; they will follow your account once accounts exist.");
        }

        private void ApplyCode(string code)
        {
            Dictionary<string, string> map = SettingsCode.Decode(code);
            if (map == null) { _profileMessage = "That isn't a settings code (it should start with " + SettingsCode.Prefix + ")."; return; }
            bool beginner = _settings.BeginnerView;
            int n = _settings.ReadFrom(map) + _video.ReadFrom(map);
            _settings.BeginnerView = beginner; // the view you are using stays
            if (map.TryGetValue(BindsKey, out string json))
            {
                string before = GameInput.ExportOverrides(_input.Player);
                try { if (json.Length > 0) GameInput.ImportOverrides(_input.Player, json); else _input.Player.RemoveAllBindingOverrides(); n++; }
                catch (Exception e)
                {
                    // A bad binds part must not leave you with default binds: put yours back.
                    Debug.LogWarning("[SCULPT] Settings code binds ignored: " + e.Message);
                    _input.Player.RemoveAllBindingOverrides();
                    if (before.Length > 0) GameInput.ImportOverrides(_input.Player, before);
                }
            }
            if (map.TryGetValue(SlotsKey, out string slots) && slots.Length == Items.Slots)
            {
                var p = new SlotPrefs();
                for (int k = 0; k < Items.Slots; k++)
                {
                    int v = slots[k] - '0';
                    p[k] = v >= 0 && v <= (int)SlotPrefs.Last ? (SlotPref)v : SlotPref.Any;
                }
                ApplySlotPrefs(p);
                n++;
            }
            _sensText = _dpiText = _cmText = null;
            Save();
            _profileMessage = "Applied " + n + " settings.";
            _pasteText = "";
        }

        private void DrawResetAll()
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Reset every setting and bind", _label, GUILayout.Width(LabelW));
            if (!_confirmReset)
            {
                if (GUILayout.Button("Reset everything...", _button, GUILayout.Width(180))) _confirmReset = true;
            }
            else
            {
                if (GUILayout.Button("Yes, reset everything", _button, GUILayout.Width(190)))
                {
                    _settings.ResetAll();
                    _video.ResetAll();
                    ResetBinds();
                    ApplySlotPrefs(SlotPrefs.Default);
                    _sensText = _dpiText = _cmText = null;
                    _confirmReset = false;
                    Save();
                    _profileMessage = "Everything is back to its default.";
                }
                if (GUILayout.Button("Cancel", _button, GUILayout.Width(90))) _confirmReset = false;
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private void OnDestroy()
        {
            _rebind?.Dispose();
            if (_panel != null) Destroy(_panel);
        }
    }
}
