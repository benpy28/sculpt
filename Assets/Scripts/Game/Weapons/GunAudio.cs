using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Weapons
{
    /// <summary>
    /// Gun sounds generated in code (nothing licensed). Each shot is layered so it lands with weight, not as a toy pop:
    /// a dry close layer (supersonic crack, pitch-dropping body thump, a low sub thump you feel more than hear, and the
    /// mechanical clack of the bolt or slide), then a reflection layer chosen by the space around the shooter: a short,
    /// bright slap off nearby walls (indoors, alleys) or a long rolling outdoor tail (open ground), mixed by how
    /// enclosed the spot is and delayed by how far the walls are (<see cref="Space"/>, six cheap rays per shot).
    /// Hit confirms are short and distinct: body tick, brighter head ping, heavy kill thud with a chime.
    /// </summary>
    public static class GunAudio
    {
        private const int Rate = 44100;
        private static AudioClip[] _shots, _rooms, _outs;
        private static AudioClip _rack, _hit, _head, _kill, _hurt, _impact, _flesh;

        private struct Shape
        {
            public float Len, Crack, CrackDecay, Thump, ThumpHi, ThumpLo, ThumpDecay, Tail, TailDecay, TailCutoff;
            /// <summary>Mechanical action under the shot (bolt or slide cycling): level and delay in seconds.</summary>
            public float Mech, MechAt;
            /// <summary>Sub thump: level, start and end pitch (Hz), decay.</summary>
            public float Sub, SubHi, SubLo, SubDecay;
            /// <summary>Wall slap (indoors): level, decay; outdoor rolling tail: level, length, decay.</summary>
            public float Room, RoomDecay, Out, OutLen, OutDecay;
        }

        private static readonly Shape[] Shapes =
        {
            // Pump: huge low body and sub, a big slap and a long roll outdoors.
            new Shape { Len = 0.7f, Crack = 0.6f, CrackDecay = 42, Thump = 1.0f, ThumpHi = 100, ThumpLo = 38, ThumpDecay = 9, Tail = 0.3f, TailDecay = 7f, TailCutoff = 0.06f,
                        Sub = 0.75f, SubHi = 62, SubLo = 34, SubDecay = 7.5f,
                        Room = 0.85f, RoomDecay = 7f, Out = 0.7f, OutLen = 1.7f, OutDecay = 2.4f },
            // SMG: short, snappy, less body; small tails so 12 shots a second don't smear.
            new Shape { Len = 0.32f, Crack = 0.72f, CrackDecay = 85, Thump = 0.45f, ThumpHi = 175, ThumpLo = 78, ThumpDecay = 30, Tail = 0.15f, TailDecay = 14, TailCutoff = 0.12f, Mech = 0.24f, MechAt = 0.026f,
                        Sub = 0.3f, SubHi = 85, SubLo = 52, SubDecay = 22,
                        Room = 0.45f, RoomDecay = 14f, Out = 0.35f, OutLen = 0.7f, OutDecay = 5.5f },
            // AR: punchy crack, solid thump and sub, a clear bolt clack.
            new Shape { Len = 0.5f, Crack = 0.85f, CrackDecay = 68, Thump = 0.78f, ThumpHi = 140, ThumpLo = 50, ThumpDecay = 15, Tail = 0.2f, TailDecay = 9f, TailCutoff = 0.08f, Mech = 0.3f, MechAt = 0.034f,
                        Sub = 0.55f, SubHi = 72, SubLo = 42, SubDecay = 13,
                        Room = 0.7f, RoomDecay = 9f, Out = 0.55f, OutLen = 1.15f, OutDecay = 3.4f },
            // Pistol: sharp crack, medium body, snappy slide.
            new Shape { Len = 0.4f, Crack = 0.9f, CrackDecay = 95, Thump = 0.58f, ThumpHi = 190, ThumpLo = 72, ThumpDecay = 22, Tail = 0.16f, TailDecay = 11f, TailCutoff = 0.1f, Mech = 0.32f, MechAt = 0.021f,
                        Sub = 0.35f, SubHi = 90, SubLo = 55, SubDecay = 18,
                        Room = 0.6f, RoomDecay = 11f, Out = 0.42f, OutLen = 0.9f, OutDecay = 4.2f },
        };

        /// <summary>The dry close layer of a shot (crack, thump, sub, clack).</summary>
        public static AudioClip Shot(Weapon w)
        {
            if (_shots == null) _shots = new AudioClip[4];
            int i = (int)w;
            if (_shots[i] != null) return _shots[i];
            Shape s = Shapes[i];
            float lp = 0, phase = 0, subPhase = 0;
            return _shots[i] = Make("gun-" + w, s.Len, (t, n) =>
            {
                float crack = s.Crack * Noise(n) * Mathf.Exp(-t * s.CrackDecay);
                float f = s.ThumpLo + (s.ThumpHi - s.ThumpLo) * Mathf.Exp(-t * 25f);
                phase += 2 * Mathf.PI * f / Rate;
                float thump = s.Thump * Mathf.Sin(phase) * Mathf.Exp(-t * s.ThumpDecay);
                float sf = s.SubLo + (s.SubHi - s.SubLo) * Mathf.Exp(-t * 18f);
                subPhase += 2 * Mathf.PI * sf / Rate;
                float sub = s.Sub * Mathf.Sin(subPhase) * Mathf.Exp(-t * s.SubDecay) * Mathf.Min(1f, t * 400f);
                lp += (Noise(n + 7919) - lp) * s.TailCutoff;
                float tail = s.Tail * lp * 3f * Mathf.Exp(-t * s.TailDecay) * Mathf.Min(1f, t * 200f);
                float mech = s.Mech > 0 ? s.Mech * (Clack(t, s.MechAt, 1f, n) + Clack(t, s.MechAt + 0.03f, 0.6f, n + 31)) : 0f;
                return Soft(crack + thump + sub + tail + mech);
            });
        }

        /// <summary>The slap off nearby walls: a few dense early reflections of the crack, band-limited, short.</summary>
        public static AudioClip Room(Weapon w)
        {
            if (_rooms == null) _rooms = new AudioClip[4];
            int i = (int)w;
            if (_rooms[i] != null) return _rooms[i];
            Shape s = Shapes[i];
            float lo = 0, hi = 0;
            float len = Mathf.Min(0.6f, 4.5f / s.RoomDecay + 0.05f);
            return _rooms[i] = Make("gun-room-" + w, len, (t, n) =>
            {
                // Taps at irregular spacings (no flutter), each a short burst; then a dense, fast-decaying wash.
                float x = Tap(t, 0.000f, 1.0f, n) + Tap(t, 0.011f, 0.75f, n + 101) + Tap(t, 0.019f, 0.6f, n + 211)
                        + Tap(t, 0.032f, 0.5f, n + 307) + Tap(t, 0.047f, 0.35f, n + 401);
                float wash = Noise(n + 503) * 0.5f * Mathf.Exp(-t * s.RoomDecay) * Mathf.Min(1f, t * 60f);
                float v = x + wash;
                lo += (v - lo) * 0.25f; // ~2 kHz low-pass: walls eat the top
                hi += (lo - hi) * 0.012f; // minus ~85 Hz: the slap is mid, the floor shake is in the dry layer
                return Soft(s.Room * (lo - hi) * 2.4f);
            });
        }

        /// <summary>The outdoor tail: a long low roll with a couple of distant swells.</summary>
        public static AudioClip Outdoor(Weapon w)
        {
            if (_outs == null) _outs = new AudioClip[4];
            int i = (int)w;
            if (_outs[i] != null) return _outs[i];
            Shape s = Shapes[i];
            float lp = 0, lp2 = 0;
            return _outs[i] = Make("gun-out-" + w, s.OutLen, (t, n) =>
            {
                float env = Mathf.Exp(-t * s.OutDecay) * Mathf.Min(1f, t * 30f)
                          * (1f + 0.5f * Bump(t, 0.18f, 0.06f) + 0.3f * Bump(t, 0.42f, 0.1f));
                lp += (Noise(n + 1301) - lp) * 0.035f;
                lp2 += (lp - lp2) * 0.05f;
                return Soft(s.Out * lp2 * 9f * env);
            });
        }

        private static float Tap(float t, float at, float amp, int n)
        {
            float u = t - at;
            if (u < 0) return 0;
            return amp * Noise(n * 7 + 13) * Mathf.Exp(-u * 120f);
        }

        private static float Bump(float t, float at, float width) => Mathf.Exp(-Mathf.Pow((t - at) / width, 2f));

        // ---------------------------------------------------------------- The space around a shot

        private static Vector3 _spaceAt = new Vector3(1e9f, 0, 0);
        private static float _spaceTime = -10f, _enclosure, _wallDist;
        private static readonly Vector3[] Probe =
        {
            new Vector3(1, 0, 0), new Vector3(-1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 0, -1),
            new Vector3(0, 1, 0), new Vector3(0.7071f, 0.5f, 0.5f),
        };
        private const float ProbeRange = 20f;

        /// <summary>
        /// How enclosed a spot is (0 open field .. 1 a small room) and the mean distance to the walls that answer.
        /// Six rays on the client's own world copy, reused while the shooter stays within 0.6 m for 0.25 s (an SMG burst
        /// costs one probe, not twelve).
        /// </summary>
        public static void Space(ITerrain grid, Vector3 at, out float enclosure, out float wallDistance)
        {
            float now = Time.time;
            if (grid == null) { enclosure = 0.3f; wallDistance = 8f; return; }
            if ((at - _spaceAt).sqrMagnitude < 0.36f && now - _spaceTime < 0.25f) { enclosure = _enclosure; wallDistance = _wallDist; return; }
            float close = 0f, dist = 0f;
            int hits = 0;
            Vec3d o = at.ToCore();
            for (int i = 0; i < Probe.Length; i++)
            {
                TerrainHit h = grid.Raycast(o, Probe[i].normalized.ToCore(), ProbeRange);
                if (!h.Hit) continue;
                float d = (float)h.Distance;
                close += Mathf.Clamp01(1.25f - d / 14f);
                dist += d;
                hits++;
            }
            _enclosure = close / Probe.Length;
            _wallDist = hits > 0 ? dist / hits : ProbeRange;
            _spaceAt = at;
            _spaceTime = now;
            enclosure = _enclosure;
            wallDistance = _wallDist;
        }

        /// <summary>A full gunshot: the dry layer plus the reflections the space around <paramref name="at"/> gives back.</summary>
        public static void PlayShot(Weapon w, ITerrain grid, Vector3 at, float volume, float pitch, bool spatial)
        {
            Play(Shot(w), at, volume, pitch, spatial);
            Space(grid, at, out float enc, out float wallDist);
            // The slap arrives after the sound has gone to the walls and back (capped so it never reads as a separate echo).
            float delay = Mathf.Min(0.055f, wallDist * 2f / 343f);
            float room = enc * enc * 0.9f + enc * 0.25f;
            if (room > 0.04f) Play(Room(w), at, volume * room, pitch, spatial, delay: delay);
            float open = 0.55f * (0.18f + 0.82f * (1f - enc)); // the roll is low and long: under the dry shot, never over it
            Play(Outdoor(w), at, volume * open, pitch * Random.Range(0.97f, 1.03f), spatial, delay: 0.015f);
        }

        // ---------------------------------------------------------------- Mechanics

        /// <summary>Pump rack: back (two metal clacks) then forward.</summary>
        public static AudioClip Rack => _rack ? _rack : _rack = Make("gun-rack", 0.42f, (t, n) =>
            Clack(t, 0.0f, 1.0f, n) + Clack(t, 0.035f, 0.5f, n) + Clack(t, 0.2f, 0.9f, n) + 0.35f * Mathf.Sin(2 * Mathf.PI * 160 * t) * Mathf.Exp(-(t - 0.2f) * 40f) * (t > 0.2f ? 1f : 0f));

        private static AudioClip _magOut, _magIn, _shellIn;

        /// <summary>Magazine out: a release click, then the mag scraping free.</summary>
        public static AudioClip MagOut => _magOut ? _magOut : _magOut = Make("gun-mag-out", 0.3f, (t, n) =>
            0.7f * Clack(t, 0.0f, 0.8f, n) + 0.35f * Noise(n * 5 + 3) * Mathf.Exp(-Mathf.Pow((t - 0.09f) / 0.05f, 2f)));

        /// <summary>Magazine in: a heavy seat and a latch click (the reload's main beat).</summary>
        public static AudioClip MagIn => _magIn ? _magIn : _magIn = Make("gun-mag-in", 0.32f, (t, n) =>
            Clack(t, 0.0f, 1.1f, n) + 0.45f * Mathf.Sin(2 * Mathf.PI * 95 * t) * Mathf.Exp(-t * 30f) + Clack(t, 0.11f, 0.55f, n + 17));

        /// <summary>One shell pushed into the pump tube.</summary>
        public static AudioClip ShellIn => _shellIn ? _shellIn : _shellIn = Make("gun-shell-in", 0.16f, (t, n) =>
            0.8f * Clack(t, 0.0f, 0.7f, n) + 0.3f * Mathf.Sin(2 * Mathf.PI * 420 * t) * Mathf.Exp(-t * 45f));

        // ---------------------------------------------------------------- Confirms

        /// <summary>Body hit: a hard 2 ms click on a short bright tick. Crisp enough to count pellets by ear.</summary>
        public static AudioClip HitTick => _hit ? _hit : _hit = Make("hit-body", 0.06f, (t, n) =>
            0.45f * Noise(n) * Mathf.Exp(-t * 900f) +
            0.5f * Mathf.Sin(2 * Mathf.PI * 2400 * t) * Mathf.Exp(-t * 110f) +
            0.2f * Mathf.Sin(2 * Mathf.PI * 3700 * t) * Mathf.Exp(-t * 160f));

        /// <summary>Head hit: a click and a ringing two-partial ping, clearly higher and longer than a body hit.</summary>
        public static AudioClip HitHead => _head ? _head : _head = Make("hit-head", 0.16f, (t, n) =>
            0.4f * Noise(n) * Mathf.Exp(-t * 900f) +
            (0.45f * Mathf.Sin(2 * Mathf.PI * 4200 * t) + 0.25f * Mathf.Sin(2 * Mathf.PI * 6300 * t) + 0.12f * Mathf.Sin(2 * Mathf.PI * 2100 * t)) * Mathf.Exp(-t * 28f));

        /// <summary>Kill: a heavy dropping thud you feel, a hard click on top and a short bright chime ringing over it.</summary>
        public static AudioClip Kill => _kill ? _kill : _kill = Make("hit-kill", 0.5f, (t, n) =>
            Soft(0.9f * Mathf.Sin(2 * Mathf.PI * (48 + 80 * Mathf.Exp(-t * 22)) * t) * Mathf.Exp(-t * 8f) +
                 0.5f * Noise(n) * Mathf.Exp(-t * 600f) +
                 (0.3f * Mathf.Sin(2 * Mathf.PI * 1480 * t) + 0.18f * Mathf.Sin(2 * Mathf.PI * 2220 * t)) * Mathf.Exp(-t * 7f) * Mathf.Min(1f, t * 300f)));

        public static AudioClip Hurt => _hurt ? _hurt : _hurt = Make("hurt", 0.25f, (t, n) =>
            Mathf.Exp(-t * 16f) * (0.7f * Mathf.Sin(2 * Mathf.PI * 58 * t) + 0.3f * Noise(n) * Mathf.Exp(-t * 40f)));

        /// <summary>Bullet into rock or brick: a hard dry snap with grit and a short knock.</summary>
        public static AudioClip Impact => _impact ? _impact : _impact = Make("impact-rock", 0.14f, (t, n) =>
            Mathf.Exp(-t * 50f) * (0.65f * Noise(n) + 0.3f * Mathf.Sin(2 * Mathf.PI * 900 * t)) + 0.3f * Mathf.Sin(2 * Mathf.PI * 210 * t) * Mathf.Exp(-t * 35f));

        public static AudioClip Flesh => _flesh ? _flesh : _flesh = Make("impact-flesh", 0.1f, (t, n) =>
            Mathf.Exp(-t * 45f) * (0.6f * Mathf.Sin(2 * Mathf.PI * 140 * t) + 0.25f * Noise(n)));

        private static float Clack(float t, float at, float amp, int n)
        {
            float u = t - at;
            if (u < 0) return 0;
            return amp * Mathf.Exp(-u * 160f) * (0.55f * Noise(n * 3 + 11) + 0.4f * Mathf.Sin(2 * Mathf.PI * 1850 * u) + 0.25f * Mathf.Sin(2 * Mathf.PI * 3300 * u));
        }

        private static float Soft(float x) => x / (1f + Mathf.Abs(x) * 0.6f); // gentle clip: loud without harsh distortion

        private static float Noise(int i)
        {
            uint h = (uint)i * 2654435761u ^ 0x5bd1e995u;
            h ^= h >> 13; h *= 1274126177u; h ^= h >> 16;
            return (h & 0xFFFF) / 32767.5f - 1f;
        }

        private static AudioClip Make(string name, float seconds, System.Func<float, int, float> f)
        {
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)Rate;
                float fade = Mathf.Min(1f, (seconds - t) / 0.02f); // no click at the end
                data[i] = Mathf.Clamp(f(t, i) * fade, -1f, 1f);
            }
            var clip = AudioClip.Create(name, n, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>Generates every gun clip now (at load), so the first shot of a match never stalls a frame.</summary>
        public static void Warm()
        {
            for (int i = 0; i < 4; i++) { Shot((Weapon)i); Room((Weapon)i); Outdoor((Weapon)i); }
            _ = Rack; _ = HitTick; _ = HitHead; _ = Kill; _ = Impact;
        }

        /// <summary>
        /// A one-shot from the shared voice pool (no GameObject per sound). Gunshots carry far (logarithmic rolloff out to
        /// 220 m) so you can always hear where fire comes from.
        /// </summary>
        public static void Play(AudioClip clip, Vector3 at, float volume, float pitch, bool spatial, float maxDistance = 220f, float minDistance = 3f, float delay = 0f)
        {
            Audio.Synth.Play(clip, at, volume, pitch, spatial, minDistance, maxDistance, logarithmic: true, priority: spatial ? 128 : 16, delay: delay);
        }
    }
}
