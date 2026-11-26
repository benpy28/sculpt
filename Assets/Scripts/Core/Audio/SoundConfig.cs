namespace Sculpt.Core.Audio
{
    /// <summary>
    /// Tunables for how sound travels through the world (<see cref="SoundPaths"/>). Plain data with defaults, like
    /// <see cref="Net.NetConfig"/>. Losses are in decibels so they read like a mixing desk.
    /// </summary>
    public sealed class SoundConfig
    {
        /// <summary>Edge of one cell of the air grid sound is routed on (metres). 0.5 m: a 0.3 m wall still splits cells,
        /// and any opening at least <see cref="FaceSampleOffset"/> x 2 wide on both axes is always found.</summary>
        public double CellSize = 0.5;

        /// <summary>The four lines that test one cell face are this far off its centre on both face axes (metres). At a
        /// quarter of <see cref="CellSize"/> all lines in the world sit on one lattice half a cell (0.25 m) apart, so any
        /// opening 0.25 x 0.25 m or bigger lets sound through. Must stay CellSize / 4 for the fast grid
        /// (<see cref="SoundGrid"/>); anything else falls back to ray casts.</summary>
        public double FaceSampleOffset = 0.125;

        /// <summary>Most air cells one route may open before it gives up on going around (then only the muffled sound
        /// through the walls is heard).</summary>
        public int MaxExpansions = 3000;

        /// <summary>Most uncached face tests one route may run (keeps a cold cache from stalling a frame).</summary>
        public int MaxNewFaceTests = 2500;

        /// <summary>Greedy weight on the search heuristic. Above 1 finds a route far faster; the route is then pulled
        /// tight, so only which opening it picks can change, never the length of the line through that opening.</summary>
        public double HeuristicWeight = 1.4;

        /// <summary>Grid steps run along the axes, so a route measured on the grid is longer than the real one (up to 1.41x
        /// on a flat diagonal). The search keeps cells whose grid cost is within the range times this; the pulled-tight
        /// route must still be inside the range.</summary>
        public double GridSlack = 1.45;

        /// <summary>Longest way round searched for (metres). Loud sounds carry further (shots 220 m, blasts 190 m); past
        /// this they are heard through the walls on the straight line, or (if that's too much solid) distant and dull.</summary>
        public double SearchRange = 60;
        /// <summary>A loud sound with no way round in the search range and too much solid on the straight line.</summary>
        public double FarObstructedLossDb = 12;
        public double FarObstructedCutoffHz = 1800;

        /// <summary>Searches for a way round one frame may run (each up to ~1-2 ms on a slow PC at the expansion cap); more
        /// sounds that frame are heard direct or through the walls only, and route properly next time.</summary>
        public int MaxSearchesPerFrame = 3;

        /// <summary>Loss per corner the sound bends round (doorways, holes, over a wall top).</summary>
        public double BendLossDb = 2.5;
        /// <summary>At most this many corners are charged (a long winding corridor is far, not silent).</summary>
        public int MaxChargedBends = 4;
        /// <summary>Top end left after each corner: cutoff x this per bend, never below <see cref="MinBendCutoffHz"/>.</summary>
        public double BendCutoffScale = 0.55;
        public double MinBendCutoffHz = 2500;
        /// <summary>No filtering.</summary>
        public double OpenCutoffHz = 22000;

        /// <summary>Loss for each wall the straight line passes through, plus a loss per metre of solid crossed.</summary>
        public double WallLossDb = 11;
        public double SolidLossDbPerMetre = 18;
        /// <summary>More solid than this on the straight line (a hill, a stack of floors) and nothing comes through it.</summary>
        public double MaxSolidMetres = 2.0;
        /// <summary>More walls than this and nothing comes through.</summary>
        public int MaxWalls = 3;
        /// <summary>Through one wall only the low end survives (thump, not crack); each further wall halves it again.</summary>
        public double ThroughCutoffHz = 900;

        /// <summary>Distances below this count as this when comparing which way a sound is louder (metres).</summary>
        public double ReferenceDistance = 2.0;

        /// <summary>A route is reused while listener and source stay in the same <see cref="RouteCacheCell"/> cells, for
        /// this long (seconds). A sprinting player's steps reuse one route for about a metre of running.</summary>
        public double RouteCacheSeconds = 0.25;
        public double RouteCacheCell = 1.0;

        /// <summary>Head shadow for sounds straight behind you: this much quieter, top end down to this cutoff (front and
        /// back sound the same on plain stereo otherwise).</summary>
        public double BehindLossDb = 1.5;
        public double BehindCutoffHz = 9000;
    }
}
