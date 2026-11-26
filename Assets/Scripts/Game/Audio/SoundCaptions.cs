using Sculpt.Game.Settings;
using UnityEngine;

namespace Sculpt.Game.Audio
{
    /// <summary>A sound that gets a caption when captions are on (Ben: subtitles for key sounds, footsteps and wall breaks).</summary>
    public enum SoundCaption : byte
    {
        None, Footsteps, Running, Landing, Jump, Climbing, Sliding, Gunfire, Explosion, C4Beeping, GrenadeThrow,
        WallBreaking, DoorOpening, DoorClosing, WallHealing,
    }

    /// <summary>
    /// Captions for the sounds of other players and the world, made from exactly the sounds that are heard: a sound the
    /// directional sound pass decides you can't hear gets no caption either, and the caption points the way the sound
    /// arrives (a doorway, a hole, through a wall) and says when it is muffled. Shown by <see cref="UI.CaptionsHud"/>.
    /// The same caption from about the same way within a second only refreshes the line, so a run of footsteps is one
    /// caption.
    /// </summary>
    public static class SoundCaptions
    {
        public struct Line
        {
            public SoundCaption Kind;
            public Vector3 At;
            public bool Muffled;
            public float Time, Loudness;
        }

        public const int Max = 6;
        public const float Life = 2.2f;
        public static readonly Line[] Lines = new Line[Max];
        public static int Version { get; private set; }

        public static bool On => ClientSettings.Current.Captions;

        public static string Text(SoundCaption c)
        {
            switch (c)
            {
                case SoundCaption.Footsteps: return "Footsteps";
                case SoundCaption.Running: return "Running footsteps";
                case SoundCaption.Landing: return "Someone landing";
                case SoundCaption.Jump: return "Someone jumping";
                case SoundCaption.Climbing: return "Someone climbing";
                case SoundCaption.Sliding: return "Someone sliding";
                case SoundCaption.Gunfire: return "Gunfire";
                case SoundCaption.Explosion: return "Explosion";
                case SoundCaption.C4Beeping: return "C4 beeping";
                case SoundCaption.GrenadeThrow: return "Grenade thrown";
                case SoundCaption.WallBreaking: return "Wall breaking";
                case SoundCaption.DoorOpening: return "Door opening";
                case SoundCaption.DoorClosing: return "Door closing";
                case SoundCaption.WallHealing: return "Wall being healed";
                default: return "";
            }
        }

        /// <summary>Adds or refreshes a caption for a sound heard at <paramref name="heardAt"/> (where it reaches you from).</summary>
        public static void Post(SoundCaption kind, Vector3 heardAt, bool muffled, float loudness)
        {
            if (kind == SoundCaption.None || !On) return;
            float now = UnityEngine.Time.unscaledTime;
            int free = -1, oldest = 0;
            for (int i = 0; i < Max; i++)
            {
                ref Line l = ref Lines[i];
                bool alive = l.Kind != SoundCaption.None && now - l.Time < Life;
                if (alive && l.Kind == kind && (l.At - heardAt).sqrMagnitude < 6f * 6f)
                {
                    l.At = heardAt; l.Time = now; l.Muffled = muffled; l.Loudness = Mathf.Max(loudness, l.Loudness * 0.8f);
                    Version++;
                    return;
                }
                if (!alive && free < 0) free = i;
                if (Lines[i].Time < Lines[oldest].Time) oldest = i;
            }
            int at = free >= 0 ? free : oldest;
            Lines[at] = new Line { Kind = kind, At = heardAt, Muffled = muffled, Time = now, Loudness = loudness };
            Version++;
        }

        public static void Clear()
        {
            for (int i = 0; i < Max; i++) Lines[i] = default;
            Version++;
        }
    }
}
