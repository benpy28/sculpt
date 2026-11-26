using System.Collections.Generic;
using Sculpt.Core.Settings;
using UnityEngine;

namespace Sculpt.Game.Settings
{
    /// <summary>The settings pages (tabs) a saved setting belongs to, for "reset this page".</summary>
    public static class SettingsGroup
    {
        public const string Video = "video", Latency = "latency", Mouse = "mouse", Controls = "controls", Crosshair = "crosshair",
                            Hud = "hud", Audio = "audio", Access = "access", Menu = "menu";
    }

    /// <summary>
    /// Per-player view, aim, HUD, audio, accessibility and control settings, saved in PlayerPrefs and edited in the
    /// settings menu (Esc). One shared instance (<see cref="Current"/>) that everything reads live, so a change shows at
    /// once. <see cref="Version"/> goes up on every change (for anything that caches, like meshes in rarity colours).
    /// </summary>
    public sealed class ClientSettings
    {
        // ---- View and HUD ----
        /// <summary>Horizontal field of view in degrees at 16:9.</summary>
        public float Fov = 95f;
        public bool HeadBob = true;
        public bool SpeedFovKick = true;
        /// <summary>First-person gun size, 0.7 .. 1.2 (1 = default, already small).</summary>
        public float ViewmodelSize = 1f;
        /// <summary>Faint streaks at the screen edge above sprint speed.</summary>
        public bool SpeedLines = true;
        /// <summary>Strength of the cosmetic camera motion: gun roll and FOV punch, hit shake and the landing dip (0..1).</summary>
        public float CameraShake = 1f;
        public bool HitMarkers = true, DamageNumbers = true, KillFeed = true, Compass = true;

        // ---- Mouse and aim ----
        /// <summary>Degrees of turn per mouse count = Sensitivity * 0.022 (Source-style scale).</summary>
        public float Sensitivity = 2.0f;
        /// <summary>The mouse's DPI, only for the cm/360 readout (the game never reads the mouse's DPI itself).</summary>
        public int MouseDpi = AimMath.DefaultDpi;
        /// <summary>Up/down turn speed relative to left/right.</summary>
        public float VerticalMultiplier = 1f;
        public bool InvertY;
        /// <summary>Sensitivity multiplier while zoomed (right mouse on the AR and pistol).</summary>
        public float AdsMultiplier = 1f;
        /// <summary>Also scale by the zoom itself, so a flick to a point on screen takes the same hand movement zoomed.</summary>
        public bool AdsZoomMatched;

        // ---- Controls ----
        /// <summary>Sprint and crouch/slide default to toggle (Ben 2026-11-25: they share a finger; less holding). New keys
        /// so the new default reached everyone once; hold stays a setting.</summary>
        public bool SprintToggle = true, CrouchToggle = true, ZoomToggle;

        // ---- Crosshair (Ben: colour only for now) ----
        public int CrosshairChoice;
        public float CrosshairR = 1f, CrosshairG = 1f, CrosshairB = 1f;

        // ---- Audio ----
        /// <summary>Master volume 0..1.</summary>
        public float MasterVolume = 0.8f;
        /// <summary>Wind and birdsong 0..1 (scaled by master).</summary>
        public float AmbienceVolume = 0.7f;
        public float WeaponsVolume = 1f, MovementVolume = 1f, WorldVolume = 1f, UiVolume = 1f;
        /// <summary>How strongly sounds behind you are dulled (the front/back cue), 0..2 (1 = as designed).</summary>
        public float HeadShadow = 1f;
        /// <summary>Sounds through walls and round corners keep more of their top end (easier to tell apart; never louder).</summary>
        public bool ClearerMuffled;
        /// <summary>Both ears get the same mix (for one-ear hearing or a single speaker); distance and muffling stay.</summary>
        public bool MonoAudio;

        // ---- Accessibility ----
        public ColourMode Colours = ColourMode.Off;
        /// <summary>Captions for key sounds you can't see (footsteps, wall breaks, doors, gunfire, blasts), with direction.</summary>
        public bool Captions;

        // ---- Menu ----
        /// <summary>The settings menu shows only the everyday settings (Ben: a Beginner view toggled in the bottom corner).</summary>
        public bool BeginnerView;

        public const float MinFov = 60f, MaxFov = 120f;
        public const float MinSensitivity = 0.05f, MaxSensitivity = 10f;
        public const float MinViewmodel = 0.7f, MaxViewmodel = 1.2f;

        public float DegreesPerCount => Sensitivity * (float)AimMath.DegreesPerCountAtOne;

        public Color CrosshairColour
        {
            get
            {
                Rgb c = CrosshairColours.Of(CrosshairChoice, new Rgb(CrosshairR, CrosshairG, CrosshairB));
                return new Color(c.R, c.G, c.B, 1f);
            }
        }

        public int Version { get; private set; } = 1;
        public void Changed() => Version++;

        private static ClientSettings _current;
        /// <summary>The settings in use (loaded once per run; survives scene reloads).</summary>
        public static ClientSettings Current => _current ??= Load();

        private static readonly PrefTable<ClientSettings> Table = new PrefTable<ClientSettings>()
            .Float(SettingsGroup.Hud, "sculpt.fov", 95f, MinFov, MaxFov, s => s.Fov, (s, v) => s.Fov = v)
            .Bool(SettingsGroup.Hud, "sculpt.bob", true, s => s.HeadBob, (s, v) => s.HeadBob = v)
            .Bool(SettingsGroup.Hud, "sculpt.fovkick", true, s => s.SpeedFovKick, (s, v) => s.SpeedFovKick = v)
            .Float(SettingsGroup.Hud, "sculpt.vmsize", 1f, MinViewmodel, MaxViewmodel, s => s.ViewmodelSize, (s, v) => s.ViewmodelSize = v)
            .Bool(SettingsGroup.Hud, "sculpt.speedlines", true, s => s.SpeedLines, (s, v) => s.SpeedLines = v)
            .Bool(SettingsGroup.Hud, "sculpt.hud.hitmarkers", true, s => s.HitMarkers, (s, v) => s.HitMarkers = v)
            .Bool(SettingsGroup.Hud, "sculpt.hud.damagenumbers", true, s => s.DamageNumbers, (s, v) => s.DamageNumbers = v)
            .Bool(SettingsGroup.Hud, "sculpt.hud.killfeed", true, s => s.KillFeed, (s, v) => s.KillFeed = v)
            .Bool(SettingsGroup.Hud, "sculpt.hud.compass", true, s => s.Compass, (s, v) => s.Compass = v)
            .Float(SettingsGroup.Access, "sculpt.camerashake", 1f, 0f, 1f, s => s.CameraShake, (s, v) => s.CameraShake = v)
            .Float(SettingsGroup.Mouse, "sculpt.sens", 2.0f, MinSensitivity, MaxSensitivity, s => s.Sensitivity, (s, v) => s.Sensitivity = v)
            .Int(SettingsGroup.Mouse, "sculpt.mouse.dpi", AimMath.DefaultDpi, AimMath.MinDpi, AimMath.MaxDpi, s => s.MouseDpi, (s, v) => s.MouseDpi = v)
            .Float(SettingsGroup.Mouse, "sculpt.mouse.vertical", 1f, 0.25f, 2f, s => s.VerticalMultiplier, (s, v) => s.VerticalMultiplier = v)
            .Bool(SettingsGroup.Mouse, "sculpt.mouse.inverty", false, s => s.InvertY, (s, v) => s.InvertY = v)
            .Float(SettingsGroup.Mouse, "sculpt.mouse.ads", 1f, 0.2f, 2f, s => s.AdsMultiplier, (s, v) => s.AdsMultiplier = v)
            .Bool(SettingsGroup.Mouse, "sculpt.mouse.adszoom", false, s => s.AdsZoomMatched, (s, v) => s.AdsZoomMatched = v)
            .Bool(SettingsGroup.Controls, "sculpt.sprintToggle.v2", true, s => s.SprintToggle, (s, v) => s.SprintToggle = v)
            .Bool(SettingsGroup.Controls, "sculpt.crouchToggle.v2", true, s => s.CrouchToggle, (s, v) => s.CrouchToggle = v)
            .Bool(SettingsGroup.Controls, "sculpt.zoomToggle", false, s => s.ZoomToggle, (s, v) => s.ZoomToggle = v)
            .Int(SettingsGroup.Crosshair, "sculpt.crosshair.choice", 0, 0, CrosshairColours.Custom, s => s.CrosshairChoice, (s, v) => s.CrosshairChoice = v)
            .Float(SettingsGroup.Crosshair, "sculpt.crosshair.r", 1f, 0f, 1f, s => s.CrosshairR, (s, v) => s.CrosshairR = v)
            .Float(SettingsGroup.Crosshair, "sculpt.crosshair.g", 1f, 0f, 1f, s => s.CrosshairG, (s, v) => s.CrosshairG = v)
            .Float(SettingsGroup.Crosshair, "sculpt.crosshair.b", 1f, 0f, 1f, s => s.CrosshairB, (s, v) => s.CrosshairB = v)
            .Float(SettingsGroup.Audio, "sculpt.volume", 0.8f, 0f, 1f, s => s.MasterVolume, (s, v) => s.MasterVolume = v)
            .Float(SettingsGroup.Audio, "sculpt.ambience", 0.7f, 0f, 1f, s => s.AmbienceVolume, (s, v) => s.AmbienceVolume = v)
            .Float(SettingsGroup.Audio, "sculpt.vol.weapons", 1f, 0f, 1f, s => s.WeaponsVolume, (s, v) => s.WeaponsVolume = v)
            .Float(SettingsGroup.Audio, "sculpt.vol.movement", 1f, 0f, 1f, s => s.MovementVolume, (s, v) => s.MovementVolume = v)
            .Float(SettingsGroup.Audio, "sculpt.vol.world", 1f, 0f, 1f, s => s.WorldVolume, (s, v) => s.WorldVolume = v)
            .Float(SettingsGroup.Audio, "sculpt.vol.ui", 1f, 0f, 1f, s => s.UiVolume, (s, v) => s.UiVolume = v)
            .Float(SettingsGroup.Audio, "sculpt.audio.headshadow", 1f, 0f, 2f, s => s.HeadShadow, (s, v) => s.HeadShadow = v)
            .Bool(SettingsGroup.Audio, "sculpt.audio.clearer", false, s => s.ClearerMuffled, (s, v) => s.ClearerMuffled = v)
            .Bool(SettingsGroup.Access, "sculpt.audio.mono", false, s => s.MonoAudio, (s, v) => s.MonoAudio = v)
            .Int(SettingsGroup.Access, "sculpt.colours", 0, 0, ColourVision.ModeCount - 1, s => (int)s.Colours, (s, v) => s.Colours = (ColourMode)v)
            .Bool(SettingsGroup.Access, "sculpt.captions", false, s => s.Captions, (s, v) => s.Captions = v)
            .Bool(SettingsGroup.Menu, "sculpt.menu.beginner", false, s => s.BeginnerView, (s, v) => s.BeginnerView = v);

        public static ClientSettings Load()
        {
            var s = new ClientSettings();
            Table.Load(s);
            return s;
        }

        /// <summary>Kept for old callers: values are clamped as they load and as they are set from a code.</summary>
        public ClientSettings Clamped()
        {
            Fov = Mathf.Clamp(Fov, MinFov, MaxFov);
            Sensitivity = Mathf.Clamp(Sensitivity, MinSensitivity, MaxSensitivity);
            ViewmodelSize = Mathf.Clamp(ViewmodelSize, MinViewmodel, MaxViewmodel);
            MasterVolume = Mathf.Clamp01(MasterVolume);
            AmbienceVolume = Mathf.Clamp01(AmbienceVolume);
            MouseDpi = AimMath.ClampDpi(MouseDpi);
            return this;
        }

        public void Save() => Table.Save(this);

        /// <summary>Puts one settings page back to its defaults (see <see cref="SettingsGroup"/>).</summary>
        public void ResetGroup(string group) { Table.Defaults(this, group); Changed(); }
        public void ResetAll() { bool beginner = BeginnerView; Table.Defaults(this); BeginnerView = beginner; Changed(); }

        public void WriteTo(IDictionary<string, string> into) => Table.Write(this, into);
        public int ReadFrom(IReadOnlyDictionary<string, string> from) { int n = Table.Read(this, from); Changed(); return n; }
    }
}
