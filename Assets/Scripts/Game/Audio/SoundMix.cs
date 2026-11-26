using Sculpt.Game.Settings;

namespace Sculpt.Game.Audio
{
    /// <summary>Which volume slider a sound follows (Settings > Audio).</summary>
    public enum SoundBus : byte
    {
        /// <summary>Gunshots, reloads, explosions, grenade and C4 sounds.</summary>
        Weapons,
        /// <summary>Footsteps, jumps, landings, slides and climbs (yours and other players').</summary>
        Movement,
        /// <summary>Wall breaks, doors, wall heals and sculpt hums.</summary>
        World,
        /// <summary>Hit and kill confirms, the ping blip and other interface sounds.</summary>
        Ui,
        /// <summary>Wind and birds (AmbientAudio applies the ambience slider itself).</summary>
        Ambience,
    }

    /// <summary>
    /// The player's mix (Settings > Audio): one volume per <see cref="SoundBus"/> under the master volume, the strength of
    /// the front/back head shadow, clearer muffled sounds and mono. All of it is local listening only: nothing changes
    /// what can be heard or from where, so the directional sound rules stay the same for everyone.
    /// </summary>
    public static class SoundMix
    {
        public static float Gain(SoundBus bus)
        {
            ClientSettings s = ClientSettings.Current;
            switch (bus)
            {
                case SoundBus.Weapons: return s.WeaponsVolume;
                case SoundBus.Movement: return s.MovementVolume;
                case SoundBus.World: return s.WorldVolume;
                case SoundBus.Ui: return s.UiVolume;
                default: return 1f;
            }
        }

        /// <summary>3D spread for a positional voice: 180 degrees puts it equally in both ears (mono); 0 is normal stereo.</summary>
        public static float Spread => ClientSettings.Current.MonoAudio ? 180f : 0f;

        /// <summary>The low-pass cutoff after the "clearer muffled sounds" option: doubled, at least 1.8 kHz, so a sound
        /// through a wall still sounds duller than one round a corner (the cue stays), just easier to make out.</summary>
        public static float Cutoff(float cutoff, float open)
        {
            if (!ClientSettings.Current.ClearerMuffled || cutoff >= open - 1f) return cutoff;
            return System.Math.Min(open, System.Math.Max(cutoff * 2f, 1800f));
        }
    }
}
