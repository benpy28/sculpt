using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// The North East segment of the battle royale map (id 8, see <see cref="BrLayout"/>): Hilltop Fort.
    ///
    /// A 30 m hill fills the segment's north-east. Its south side is a rock face nobody climbs (the cliff line wanders,
    /// <see cref="Lip"/>); its west, north and east sides are long walkable slopes. On its flat summit stands a walled
    /// fort: 6 m walls (four tiles, too tall to climb), one gate on the west side between two gate towers, a six-storey
    /// keep, barracks, an armoury with a sealed vault, a chapel with a sealed crypt. Below the cliff: Cliffside, a works
    /// hamlet with a seven-storey lift tower whose roof bridge lands on the summit (the attackers' fast way up, then
    /// breach the fort's south wall). South-west of the hill: Underhill, a village on a level terrace. North-west: a
    /// ridge with a five-storey lookout. East of the spawn region: Rim Farm. Along the west border: West Road farms.
    /// The home POI, Hillgate, is an inn, a smithy, stables and a sealed granary round a small square on the plaza; the
    /// inn and the smithy stand across every pad's view past the anchor.
    ///
    /// One ground rule shapes the layout: a footing's 28 degree bank reaches as far as it needs to, so a footing near a
    /// rock face cuts or fills the face into a walkable bank. So the summit's buildings stand on the summit's own exact
    /// level (no footing), the villages near the cliff stand on level terraces the ground function makes (no footings),
    /// and every footing elsewhere is checked against the face (<see cref="CarvesFace"/>).
    /// </summary>
    public sealed class NorthEastSegment : BrSegment
    {
        public override int Id => 8;

        public override string HomePoiName => "Hillgate";

        private const double M = GreyboxBlock.Module, Col = GreyboxBlock.Column, Base = BrGround.Base;
        private static readonly double Tan28 = Math.Tan(28 * Math.PI / 180);

        // ================================================================ ground

        /// <summary>The hill's level summit (the fort stands on it, exactly at <see cref="SummitY"/>).</summary>
        public static readonly Rect2 Summit = new Rect2(1080, 1075, 1155, 1150);
        public const double SummitY = 31.5;
        /// <summary>The walkable slopes fall from the summit over this distance (steepest about 26.6 degrees).</summary>
        public const double HillReach = 90;
        public const double FaceDegrees = 65;
        private static readonly double FaceTan = Math.Tan(FaceDegrees * Math.PI / 180);

        /// <summary>The fort's walls (outer faces).</summary>
        public static readonly Rect2 Fort = new Rect2(1090, 1092, 1148, 1146);
        /// <summary>The gate: a gap in the west wall between these z.</summary>
        public const double GateZ0 = 1117.5, GateZ1 = 1120.5;
        /// <summary>Fort walls are this many 1.5 m tiles tall (6 m: past a 3 m climb).</summary>
        public const int FortWallRows = 4;

        /// <summary>The lookout ridge: axis end points, half width, height.</summary>
        public const double RidgeAx = 935, RidgeAz = 1120, RidgeBx = 985, RidgeBz = 1185, RidgeHalf = 40, RidgeHeight = 15;

        /// <summary>Level terraces at <see cref="BrGround.Base"/> (Underhill, Cliffside): buildings there need no footing.</summary>
        public static readonly Rect2[] Terraces = { new Rect2(946, 952, 1068, 1054), new Rect2(1096, 946, 1226, 1060) };
        public const double TerraceBlend = 15;

        /// <summary>The cliff's top edge (z) where the face meets the summit's level: it wanders a few metres.</summary>
        public static double Lip(double x) => 1076 + 4 * Math.Sin((x - 1000) / 31) + 2 * Math.Sin((x - 1000) / 13);

        /// <summary>The hill alone: walkable slopes all round (<see cref="HillReach"/>), cut on the south by the rock face.</summary>
        public static double Hill(double x, double z)
        {
            double d = GroundShapes.ToRect(x, z, Summit.X0, Summit.Z0, Summit.X1, Summit.Z1);
            double slopes = Base + (SummitY - Base) * (1 - GroundShapes.Smoothstep(d / HillReach));
            double face = SummitY - (Lip(x) - z) * FaceTan;
            return Math.Min(slopes, face);
        }

        public override double Ground(double x, double z)
        {
            double g = BrGround.Shared(x, z);
            foreach (Rect2 t in Terraces)
            {
                double d = GroundShapes.ToRect(x, z, t.X0, t.Z0, t.X1, t.Z1);
                if (d < TerraceBlend) g += (Base - g) * (1 - GroundShapes.Smoothstep(d / TerraceBlend));
            }
            double h = Math.Max(g, Hill(x, z));
            return Math.Max(h, Base + GroundShapes.Ridge(x, z, RidgeAx, RidgeAz, RidgeBx, RidgeBz, RidgeHalf, RidgeHeight));
        }

        private static List<(double X, double Z, double H)> _face;
        private static readonly object FaceGate = new object();

        /// <summary>Sample points on the rock face (where the ground drops more than 1.4 m in a metre southward).</summary>
        public static List<(double X, double Z, double H)> FacePoints
        {
            get
            {
                lock (FaceGate)
                {
                    if (_face != null) return _face;
                    var seg = new NorthEastSegment();
                    var list = new List<(double, double, double)>();
                    for (double x = 980; x <= 1262; x += 1.5)
                        for (double z = Lip(x) + 1; z >= Lip(x) - 22; z -= 1)
                        {
                            double h0 = seg.Ground(x, z), h1 = seg.Ground(x, z - 1);
                            if (h0 - h1 > 1.4) list.Add((x, z - 0.5, (h0 + h1) / 2));
                        }
                    _face = list;
                    return _face;
                }
            }
        }

        /// <summary>
        /// True if a footing over <paramref name="r"/> (plus apron) at <paramref name="level"/> would cut or fill any of
        /// the rock face: its bank (28 degrees from the apron's edge) must clear every face point.
        /// </summary>
        public static bool CarvesFace(Rect2 r, double apron, double level)
        {
            foreach (var p in FacePoints)
            {
                double d = GroundShapes.ToRect(p.X, p.Z, r.X0, r.Z0, r.X1, r.Z1) - apron;
                if (d > 90) continue;
                if (Math.Abs(level - p.H) > Math.Max(0, d) * Tan28 - 0.6) return true;
            }
            return false;
        }

        // ================================================================ build

        /// <summary>Placements the build had to leave out (tests assert none).</summary>
        public readonly List<string> Skipped = new List<string>();

        public override void Build(BrBuild b)
        {
            Skipped.Clear();
            b.Nature.Groves = 5;
            b.Nature.LoneTrees = 14;
            b.Nature.RockGroups = 16;
            b.Nature.Bushes = 30;
            Home(b);
            FortOnTheHill(b);
            Underhill(b);
            Cliffside(b);
            RimFarm(b);
            WestRoad(b);
            LookoutRidge(b);
            Slopes(b);
            b.Style = BrStyle.Brick;
            b.Variant = 0;
        }

        // ---------------------------------------------------------------- placement helpers

        private bool Skip(string what, string why)
        {
            Skipped.Add($"{what}: {why}");
            return false;
        }

        /// <summary>A building's rectangle on a level terrace (no footing): clear and at least 2 m inside a terrace.</summary>
        private bool OnTerrace(BrBuild b, string what, double x, double z, double w, double d, double street = 1.5)
        {
            var r = new Rect2(x, z, x + w, z + d);
            bool inside = false;
            foreach (Rect2 t in Terraces) inside |= t.Contains(r.X0, r.Z0, 2) && t.Contains(r.X1, r.Z1, 2);
            if (!inside) return Skip(what, $"{r} is not on a terrace");
            if (!b.IsClear(r, street)) return Skip(what, $"{r} is not clear");
            return true;
        }

        /// <summary>A building's rectangle on its own footing (never one that would carve the cliff); returns its level.</summary>
        private bool Footed(BrBuild b, string what, double x, double z, double w, double d, double apron, out double y)
        {
            y = double.NaN;
            var r = new Rect2(x, z, x + w, z + d);
            if (!b.IsClear(r, Math.Max(apron, 2) + Cell) || b.InHome(r, 2)) return Skip(what, $"{r} is not clear");
            // The footing grows to put its apron's edges on the ground's vertices, so no ground cell straddles the edge.
            var f = new Rect2(Snap(x - apron, false) + apron, Snap(z - apron, false) + apron, Snap(x + w + apron, true) - apron, Snap(z + d + apron, true) - apron);
            if (!b.FootAllowed(f.X0, f.Z0, f.X1, f.Z1, apron, out double lo, out double hi)) return Skip(what, "no level allowed");
            double want = Math.Round(Math.Max(lo, Math.Min(hi, b.MeanNatural(x, z, x + w, z + d))) / 0.3) * 0.3;
            if (want < lo - 1e-9) want += 0.3;
            if (want > hi + 1e-9) want -= 0.3;
            if (CarvesFace(f, apron, want)) return Skip(what, "its footing would carve the cliff");
            if (!b.TryFoot(f.X0, f.Z0, f.X1, f.Z1, apron, out y, want)) return Skip(what, "footing refused");
            if (Math.Abs(y - want) > 1e-6 && CarvesFace(f, apron, y)) return Skip(what, "its footing carves the cliff");
            return true;
        }

        /// <summary>The ground's cell (<see cref="BrMap.Field"/>: vertices at 4.8 + 1.5 k).</summary>
        private const double Cell = 1.5, Vertex0 = 4.8;

        private static double Snap(double v, bool up)
        {
            double k = (v - Vertex0) / Cell;
            return Vertex0 + Cell * (up ? Math.Ceiling(k - 1e-9) : Math.Floor(k + 1e-9));
        }

        private static double Wide(int modules) => modules * M + Col;

        private static void Look(BrBuild b, BrStyle s, int variant = 0)
        {
            b.Style = s;
            b.Variant = variant;
        }

        /// <summary>A tower from the kit's plan, upper floors windowed all round if asked, a parapet, raised.</summary>
        private static GreyboxBlock.Building Tower(BrBuild b, string name, double x, double y, double z, int mx, int mz, int storeys,
                                                   bool windowsAllRound, Action<GreyboxBlock.Building> tweak = null, bool parapet = true)
        {
            GreyboxBlock.Building t = BrKit.TowerPlan(b, x, y, z, mx, mz, storeys);
            if (windowsAllRound)
                for (int s = 1; s < storeys; s++)
                {
                    for (int i = 1; i < mx; i++) { t.AlongX[s, 0, i] = WallKind.Window; t.AlongX[s, mz, i] = WallKind.Window; }
                    for (int j = 0; j < mz; j++) { t.AlongZ[s, 0, j] = (j + s) % 2 == 0 ? WallKind.Window : WallKind.Wall; t.AlongZ[s, mx, j] = WallKind.Window; }
                }
            tweak?.Invoke(t);
            b.Raise(t, name);
            if (parapet) BrKit.Parapet(b, t);
            return t;
        }

        /// <summary>Chests on every second floor of a tower (far lane, alternating ends) and one on its roof.</summary>
        private static void TowerChests(BrBuild b, GreyboxBlock.Building t)
        {
            for (int s = 0; s < t.Storeys; s += 2) b.Chest(t, t.Mx - 1, (s / 2) % 2 == 0 ? 0 : t.Mz - 1, s, Math.PI);
            b.Chest(t, t.Mx - 1, 1, t.Storeys, Math.PI);
        }

        /// <summary>A low wall (cover) on a terrace or the summit, at a known level, if it is clear.</summary>
        private void Cover(BrBuild b, string what, double x, double y, double z, double length, bool alongX, int rows)
        {
            var r = alongX ? new Rect2(x, z, x + length, z + Col) : new Rect2(x, z, x + Col, z + length);
            if (!b.IsClear(r, 0.8)) { Skip(what, $"{r} is not clear"); return; }
            b.Strip(new Vec3d(x, y, z), length, alongX, rows);
        }

        /// <summary>A low wall on its own footing (slopes, farms), checked against the cliff.</summary>
        private void FootedWall(BrBuild b, string what, double x, double z, double length, bool alongX, int rows)
        {
            double w = alongX ? length : Col, d = alongX ? Col : length;
            if (!Footed(b, what, x, z, w, d, 1.2, out double y)) return;
            b.Strip(new Vec3d(x, y, z), length, alongX, rows);
        }

        /// <summary>A chest on its own small level spot (a hillside), checked against the cliff.</summary>
        private void HillChest(BrBuild b, string what, double x, double z, double yaw)
        {
            if (!Footed(b, what, x - 1.5, z - 1.5, 3, 3, 1.0, out double y)) return;
            b.Chest(new Vec3d(x, y, z), yaw);
        }

        // ---------------------------------------------------------------- Hillgate (home POI)

        /// <summary>
        /// Hillgate: a square on the home plaza with the inn on its west side (two storeys, a flat roof with a parapet, a
        /// sealed cellar), the smithy south (a sealed coal store), the stables north (a sealed tack room) and a sealed
        /// granary beyond the inn. Ten chests. The inn's long walls stay solid where the pads' lines past the anchor cross
        /// them, and the smithy, the granary and a stone wall close the rest of the west side.
        /// </summary>
        private void Home(BrBuild b)
        {
            Vec3d a = b.Anchor;
            double y = Base;

            // The inn: 2 x 6 modules, its east wall 7 m west of the anchor, centred on it.
            Look(b, BrStyle.Timber, 1);
            var inn = new GreyboxBlock.Building(a.X - 7 - Wide(2), a.Z - Wide(6) / 2, 2, 6, 2);
            b.Foot(inn, 2, Base);
            b.Stairwell(inn, 0, true, +1);
            inn.AlongZ[0, 2, 1] = WallKind.ShutDoor;   // onto the square, south end
            inn.AlongZ[0, 2, 4] = WallKind.ShutDoor;   // onto the square, north end
            inn.AlongZ[0, 0, 5] = WallKind.ShutDoor;   // back door
            inn.AlongX[0, 6, 1] = WallKind.ShutDoor;   // north end
            inn.AlongX[0, 0, 1] = WallKind.Window;
            inn.AlongZ[0, 2, 0] = WallKind.Window;
            inn.AlongZ[0, 2, 5] = WallKind.Window;
            // The sealed cellar, cell (0, 3): walls on every side.
            inn.AlongX[0, 3, 0] = WallKind.Wall;
            inn.AlongX[0, 4, 0] = WallKind.Wall;
            inn.AlongZ[0, 1, 3] = WallKind.Wall;
            // Upstairs: guest rooms with windows away from the pads' band (segments 2 and 3 of the long walls stay solid).
            foreach (int j in new[] { 0, 1, 4, 5 }) inn.AlongZ[1, 2, j] = WallKind.Window;
            inn.AlongZ[1, 0, 4] = WallKind.Window;
            inn.AlongX[1, 6, 1] = WallKind.Window;
            inn.AlongX[1, 0, 1] = WallKind.Window;
            inn.AlongX[1, 3, 1] = WallKind.Wall;
            inn.AlongX[1, 4, 1] = WallKind.Door;
            b.Raise(inn, "Hillgate inn");
            BrKit.Parapet(b, inn);
            b.Chest(inn, 0, 3, 0, 0);           // cellar (sealed)
            b.Chest(inn, 1, 5, 0, Math.PI);     // the bar
            b.Chest(inn, 1, 2, 1, 0);           // a guest room
            b.Chest(inn, 1, 4, 2, Math.PI);     // the roof

            // The smithy: 3 x 3, south of the square.
            Look(b, BrStyle.Stone, 1);
            var smithy = new GreyboxBlock.Building(a.X - 5.34, a.Z - 7.04 - Wide(3), 3, 3, 1);
            b.Foot(smithy, 2, Base);
            smithy.AlongX[0, 3, 1] = WallKind.ShutDoor;   // onto the square
            smithy.AlongZ[0, 3, 1] = WallKind.WideDoor;   // the forge yard
            smithy.AlongX[0, 0, 1] = WallKind.Window;
            smithy.AlongZ[0, 0, 2] = WallKind.Window;
            smithy.AlongX[0, 1, 0] = WallKind.Wall;       // the coal store, sealed
            smithy.AlongZ[0, 1, 0] = WallKind.Wall;
            smithy.AlongX[0, 2, 2] = WallKind.Wall;       // a back room
            smithy.AlongZ[0, 2, 2] = WallKind.Door;
            b.Raise(smithy, "Hillgate smithy");
            b.Chest(smithy, 0, 0, 0, 0);
            b.Chest(smithy, 2, 2, 0, Math.PI);

            // The stables: 3 x 2, north of the square.
            Look(b, BrStyle.Timber, 2);
            var stables = new GreyboxBlock.Building(a.X - 5.34, a.Z + 8.16, 3, 2, 1);
            b.Foot(stables, 2, Base);
            stables.AlongX[0, 0, 1] = WallKind.ShutDoor;  // onto the square
            stables.AlongZ[0, 3, 0] = WallKind.WideDoor;  // the paddock
            stables.AlongX[0, 2, 2] = WallKind.Window;
            stables.AlongX[0, 1, 0] = WallKind.Wall;      // the tack room, sealed
            stables.AlongZ[0, 1, 1] = WallKind.Wall;
            b.Raise(stables, "Hillgate stables");
            b.Chest(stables, 0, 1, 0, 0);
            b.Chest(stables, 2, 0, 0, Math.PI);

            // The granary: 2 x 2, no way in at all.
            Look(b, BrStyle.Brick, 2);
            var granary = new GreyboxBlock.Building(a.X - 23.84, a.Z + 22.66, 2, 2, 1);
            b.Foot(granary, 2, Base);
            b.Raise(granary, "Hillgate granary");
            b.Chest(granary, 1, 1, 0, 0);

            // A stone wall closing the south-west corner, cover on the square, one chest by the well.
            Look(b, BrStyle.Stone, 0);
            b.LowWall(a.X - 27.84, a.Z - 29.84, 20, true, 3);
            b.Strip(new Vec3d(a.X + 5, y, a.Z - 3.84), 4.5, true, 1);
            b.Strip(new Vec3d(a.X + 9.5, y, a.Z + 2.16), 4.5, false, 2);
            b.Chest(new Vec3d(a.X + 4, y, a.Z), Math.PI / 2);
            b.Item(Item.Grenade, 2, new Vec3d(a.X + 2, y, a.Z + 3));
            b.Poi(HomePoiName, a, inn.Bounds, smithy.Bounds, stables.Bounds, granary.Bounds);
        }

        // ---------------------------------------------------------------- Hilltop Fort

        /// <summary>
        /// The fort on the summit (no footings: the summit is exactly level). Walls four tiles tall all round, one gate in
        /// the west wall between two three-storey gate towers whose upper windows look out over the wall; a six-storey keep
        /// (two shut doors, a sealed vault, a chest on every floor and its roof); barracks with a sealed room; an armoury
        /// whose back two rooms are a sealed vault; a chapel with a sealed crypt; a sealed well house.
        /// </summary>
        private void FortOnTheHill(BrBuild b)
        {
            double y = SummitY;
            var fort = new List<Aabb>();

            // Gate towers either side of the passage to the gate.
            Look(b, BrStyle.Stone, 2);
            foreach (double z in new[] { 1096.0, 1122.0 })
            {
                var t = Tower(b, z < 1100 ? "Fort south gate tower" : "Fort north gate tower", 1091.5, y, z, 2, 3, 3, false, p =>
                {
                    for (int s = 1; s < 3; s++)
                        for (int j = 0; j < 3; j++) p.AlongZ[s, 0, j] = WallKind.Window; // over the wall, onto the approach
                });
                b.Chest(t, 1, 1, 0, Math.PI);
                b.Chest(t, 1, z < 1100 ? 0 : 2, 2, Math.PI);
                b.Chest(t, 1, 1, 3, Math.PI);
                fort.Add(t.Bounds);
            }

            // The keep: 3 x 3 modules, six storeys.
            Look(b, BrStyle.Stone, 1);
            var keep = Tower(b, "Fort keep", 1121, y, 1110, 3, 3, 6, false, p =>
            {
                p.AlongX[0, 0, 2] = WallKind.ShutDoor;   // south
                p.AlongX[0, 3, 2] = WallKind.Wall;       // no north door
                p.AlongZ[0, 0, 2] = WallKind.ShutDoor;   // west, facing the gate
                p.AlongX[0, 2, 2] = WallKind.Wall;       // the vault, cell (2, 2)
                p.AlongZ[0, 2, 2] = WallKind.Wall;
                for (int s = 2; s < 6; s++)
                    for (int j = 0; j < 3; j++) p.AlongZ[s, 0, j] = (j + s) % 2 == 0 ? WallKind.Window : WallKind.Wall;
                for (int s = 1; s < 6; s++)
                    for (int i = 1; i < 3; i++) p.AlongX[s, 3, i] = (i + s) % 2 == 1 ? WallKind.Window : WallKind.Wall;
            });
            b.Chest(keep, 2, 2, 0, Math.PI);                                   // the vault (sealed)
            for (int s = 1; s < 6; s++) b.Chest(keep, 2, s % 2 == 1 ? 0 : 2, s, Math.PI);
            b.Chest(keep, 2, 2, 6, Math.PI);                                   // the roof
            b.Item(Item.Launcher, 6, new Vec3d(keep.Cx(1) + Col + 3, keep.Top, keep.Cz(1) + Col + 3));
            fort.Add(keep.Bounds);

            // Barracks along the north wall: three rooms, the middle one sealed.
            Look(b, BrStyle.Plaster, 2);
            fort.Add(BrKit.Terrace(b, 1110, y, 1137.5, 3, true, 1).Bounds);

            // The armoury: 2 x 2, one shut door; the back two rooms are one sealed vault.
            Look(b, BrStyle.Stone, 3);
            var arm = new GreyboxBlock.Building(1133, 1094.5, 2, 2, 1) { Y0 = y };
            arm.AlongZ[0, 0, 0] = WallKind.ShutDoor;
            arm.AlongZ[0, 1, 0] = WallKind.Door;
            arm.AlongX[0, 1, 0] = WallKind.Wall;
            arm.AlongX[0, 1, 1] = WallKind.Wall;
            b.Raise(arm, "Fort armoury");
            b.Chest(arm, 1, 0, 0, Math.PI);
            b.Chest(arm, 0, 1, 0, 0);
            b.Chest(arm, 1, 1, 0, Math.PI);
            fort.Add(arm.Bounds);

            // The chapel: 2 x 2, a wide door north, a sealed crypt.
            Look(b, BrStyle.Stone, 4);
            var chapel = new GreyboxBlock.Building(1107, 1094.5, 2, 2, 1) { Y0 = y };
            chapel.AlongX[0, 2, 0] = WallKind.WideDoor;
            chapel.AlongX[0, 0, 0] = WallKind.Window;
            chapel.AlongX[0, 1, 1] = WallKind.Wall;      // the crypt, cell (1, 0)
            chapel.AlongZ[0, 1, 0] = WallKind.Wall;
            b.Raise(chapel, "Fort chapel");
            b.Chest(chapel, 0, 1, 0, Math.PI);
            b.Chest(chapel, 1, 0, 0, 0);
            fort.Add(chapel.Bounds);

            // The well house: sealed.
            Look(b, BrStyle.Stone, 5);
            fort.Add(BrKit.Shed(b, 1137, y, 1133).Bounds);

            // The walls (after the buildings, so the buildings' clearance checks don't see them).
            Look(b, BrStyle.Stone, 0);
            double th = Col;
            b.Strip(new Vec3d(Fort.X0, y, Fort.Z0), Fort.X1 - Fort.X0, true, FortWallRows);
            b.Strip(new Vec3d(Fort.X0, y, Fort.Z1 - th), Fort.X1 - Fort.X0, true, FortWallRows);
            b.Strip(new Vec3d(Fort.X0, y, Fort.Z0 + th), GateZ0 - Fort.Z0 - th, false, FortWallRows);
            b.Strip(new Vec3d(Fort.X0, y, GateZ1), Fort.Z1 - th - GateZ1, false, FortWallRows);
            b.Strip(new Vec3d(Fort.X1 - th, y, Fort.Z0 + th), Fort.Z1 - Fort.Z0 - 2 * th, false, FortWallRows);

            // The courtyard: two chests, grenades, low walls to fight round.
            b.Chest(new Vec3d(1112, y, 1122), -Math.PI / 2);
            b.Chest(new Vec3d(1144, y, 1120), -Math.PI / 2);
            b.Item(Item.Grenade, 2, new Vec3d(1112, y, 1126));
            Cover(b, "fort cover 1", 1108, y, 1112, 4.5, true, 1);
            Cover(b, "fort cover 2", 1115.5, y, 1128, 4.5, false, 2);
            Cover(b, "fort cover 3", 1143.5, y, 1112, 4.5, false, 1);
            b.Open(Fort.Grow(6));
            b.Poi("Hilltop Fort", new Vec3d(Fort.CX, y, Fort.CZ), fort.ToArray());
        }

        // ---------------------------------------------------------------- shared village builder

        /// <summary>Builds kit buildings either on a terrace (no footing) or on their own footings.</summary>
        private sealed class Builder
        {
            private readonly NorthEastSegment _s;
            private readonly BrBuild _b;
            private readonly bool _terrace;
            public readonly List<Aabb> All = new List<Aabb>();

            public Builder(NorthEastSegment s, BrBuild b, bool terrace) { _s = s; _b = b; _terrace = terrace; }

            private bool Site(string name, double x, double z, double w, double d, double apron, out double y)
            {
                y = Base;
                return _terrace ? _s.OnTerrace(_b, name, x, z, w, d) : _s.Footed(_b, name, x, z, w, d, apron, out y);
            }

            public void Row(string name, double x, double z, int rooms, bool alongX, int sealedRoom, BrStyle st, int v)
            {
                double w = alongX ? Wide(rooms) : Wide(1), d = alongX ? Wide(1) : Wide(rooms);
                if (!Site(name, x, z, w, d, 2.5, out double y)) return;
                Look(_b, st, v);
                All.Add(BrKit.Terrace(_b, x, y, z, rooms, alongX, sealedRoom).Bounds);
            }

            public void House(string name, double x, double z, BrStyle st, int v)
            {
                if (!Site(name, x, z, Wide(2), Wide(3), 3, out double y)) return;
                Look(_b, st, v);
                All.Add(BrKit.House(_b, x, y, z).Bounds);
            }

            public void Warehouse(string name, double x, double z, BrStyle st, int v)
            {
                if (!Site(name, x, z, Wide(4), Wide(3), 3, out double y)) return;
                Look(_b, st, v);
                All.Add(BrKit.Warehouse(_b, x, y, z).Bounds);
            }

            public void Shed(string name, double x, double z, BrStyle st, int v)
            {
                if (!Site(name, x, z, Wide(1), Wide(1), 2, out double y)) return;
                Look(_b, st, v);
                All.Add(BrKit.Shed(_b, x, y, z).Bounds);
            }

            public void Yard(string name, double x, double z, BrStyle st, int v)
            {
                if (!Site(name, x, z, 22.5, 22.5, 2.5, out double y)) return;
                Look(_b, st, v);
                BrKit.Compound(_b, x, y, z);
            }

            public GreyboxBlock.Building Tower(string name, double x, double z, int mx, int storeys, bool windows, BrStyle st, int v)
            {
                if (!Site(name, x, z, Wide(mx), Wide(3), 3, out double y)) return null;
                Look(_b, st, v);
                var t = NorthEastSegment.Tower(_b, name, x, y, z, mx, 3, storeys, windows);
                TowerChests(_b, t);
                All.Add(t.Bounds);
                return t;
            }
        }

        // ---------------------------------------------------------------- Underhill

        /// <summary>
        /// Underhill: the village at the hill's south-west foot, on a level terrace (no footings, so nothing carves the
        /// cliff behind it). Two rows of plots either side of a main street, a lane through the middle, low walls in the
        /// streets. Row houses with sealed rooms, warehouses with sealed storerooms, houses, small towers, sheds, a yard.
        /// </summary>
        private void Underhill(BrBuild b)
        {
            double y = Base;
            var v = new Builder(this, b, terrace: true);
            // South side of the main street.
            v.House("Underhill S1", 952, 958, BrStyle.Plaster, 1);
            v.Shed("Underhill S2", 967, 958, BrStyle.Timber, 0);
            v.Row("Underhill S3", 952, 989, 3, true, 2, BrStyle.Brick, 1);
            v.Warehouse("Underhill S4", 977, 960, BrStyle.Timber, 3);
            v.Row("Underhill S5", 977, 985, 4, true, 1, BrStyle.Brick, 2);
            v.Yard("Underhill S6", 1020, 958, BrStyle.Stone, 2);
            v.Tower("Underhill S7", 1046, 958, 2, 4, false, BrStyle.Plaster, 3);
            v.Row("Underhill S8", 1048, 983, 2, false, 0, BrStyle.Painted, 1);
            v.Shed("Underhill S9", 1022, 985, BrStyle.Timber, 1);
            v.Shed("Underhill S10", 1032, 985, BrStyle.Timber, 2);
            // North side.
            v.House("Underhill N1", 952, 1012, BrStyle.Painted, 2);
            v.Shed("Underhill N2", 952, 1036, BrStyle.Timber, 3);
            v.Row("Underhill N3", 967, 1012, 3, false, 2, BrStyle.Brick, 3);
            v.Tower("Underhill tavern", 978, 1012, 3, 3, false, BrStyle.Timber, 4);
            v.Shed("Underhill N5", 1000.5, 1013, BrStyle.Brick, 0);
            v.Row("Underhill N6", 978, 1038, 4, true, 2, BrStyle.Plaster, 4);
            v.Warehouse("Underhill N7", 1020, 1012, BrStyle.Brick, 4);
            v.House("Underhill N8", 1049, 1012, BrStyle.Plaster, 5);
            v.Row("Underhill N9", 1020, 1036, 3, true, 0, BrStyle.Brick, 5);
            v.Shed("Underhill N10", 1043.5, 1036, BrStyle.Timber, 4);
            v.Shed("Underhill N11", 1052.5, 1036, BrStyle.Stone, 3);

            // Low walls in the streets and yards.
            Look(b, BrStyle.Stone, 1);
            Cover(b, "Underhill wall 1", 956, y, 1003.5, 4.5, true, 1);
            Cover(b, "Underhill wall 2", 990, y, 1004, 6, true, 1);
            Cover(b, "Underhill wall 3", 1030, y, 1003, 4.5, true, 2);
            Cover(b, "Underhill wall 4", 1012.5, y, 985, 6, false, 1);
            Cover(b, "Underhill wall 5", 955, y, 982, 6, true, 1);
            Cover(b, "Underhill wall 6", 1012.5, y, 1020, 4.5, false, 2);
            Cover(b, "Underhill wall 7", 1003, y, 1047, 6, true, 1);
            b.Item(Item.Grenade, 2, new Vec3d(1008, y, 1006));
            b.Item(Item.C4, 2, new Vec3d(1040, y, 1047));
            b.Poi("Underhill", new Vec3d(1007, y, 1004), v.All.ToArray());
        }

        // ---------------------------------------------------------------- Cliffside

        /// <summary>
        /// Cliffside: a works hamlet on a level terrace under the rock face. Warehouses, yards, row houses, small towers,
        /// and the lift tower: seven storeys against the cliff, its roof bridge landing on the summit in front of the fort's
        /// south wall (the attackers' fast way up; the bridge ends 3 m over the summit, so it's one way).
        /// </summary>
        private void Cliffside(BrBuild b)
        {
            double y = Base;
            var v = new Builder(this, b, terrace: true);
            // Front row (facing the spawn region's back).
            v.Warehouse("Cliffside C1", 1102, 952, BrStyle.Industrial, 0);
            v.Warehouse("Cliffside C2", 1132, 952, BrStyle.Industrial, 1);
            v.House("Cliffside C3", 1162, 952, BrStyle.Concrete, 0);
            v.Yard("Cliffside C4", 1180, 952, BrStyle.Concrete, 1);
            v.Row("Cliffside C5", 1208, 952, 3, false, 1, BrStyle.Brick, 6);
            // Middle row.
            v.Tower("Cliffside office", 1102, 980, 2, 4, true, BrStyle.Concrete, 2);
            v.Row("Cliffside C7", 1120, 980, 4, true, 2, BrStyle.Brick, 7);
            v.Yard("Cliffside C8", 1150, 980, BrStyle.Industrial, 2);
            v.House("Cliffside C9", 1178, 980, BrStyle.Painted, 3);
            v.Tower("Cliffside C10", 1196, 980, 2, 3, true, BrStyle.Concrete, 3);
            v.Shed("Cliffside C11", 1120, 992, BrStyle.Steel, 0);
            v.Shed("Cliffside C12", 1131, 992, BrStyle.Steel, 1);
            // Under the cliff.
            v.Row("Cliffside C13", 1102, 1009, 3, true, 0, BrStyle.Brick, 8);
            v.Warehouse("Cliffside C14", 1131, 1009, BrStyle.Industrial, 3);
            v.House("Cliffside C15", 1160, 1009, BrStyle.Concrete, 4);
            v.Row("Cliffside C16", 1180, 1008, 4, false, 3, BrStyle.Brick, 9);
            v.Shed("Cliffside C17", 1192, 1009, BrStyle.Steel, 2);
            v.Shed("Cliffside C18", 1136, 1036, BrStyle.Steel, 3);
            v.Tower("Cliffside watch", 1200, 1030, 2, 5, true, BrStyle.Steel, 4);

            // The lift tower and its bridge to the summit.
            if (OnTerrace(b, "Cliffside lift tower", 1112, 1036, Wide(2), Wide(3)))
            {
                Look(b, BrStyle.Steel, 5);
                var lift = Tower(b, "Cliffside lift tower", 1112, y, 1036, 2, 3, 7, true, null, parapet: false);
                double top = lift.Top, x0 = lift.X0, z0 = lift.Z0, x1 = lift.Cx(2) + Col, z1 = lift.Cz(3) + Col;
                double gx0 = lift.Cx(0) + Col, gx1 = gx0 + 3;
                b.Strip(new Vec3d(x0, top, z0), x1 - x0, true, 1);
                b.Strip(new Vec3d(x0, top, z1 - Col), gx0 - x0, true, 1);
                b.Strip(new Vec3d(gx1, top, z1 - Col), x1 - gx1, true, 1);
                b.Strip(new Vec3d(x0, top, z0 + Col), z1 - z0 - 2 * Col, false, 1);
                b.Strip(new Vec3d(x1 - Col, top, z0 + Col), z1 - z0 - 2 * Col, false, 1);
                Bridge(b, gx0, z1, gx1, Lip(gx0 + 1.5) + 5, top);
                for (int s = 0; s < 7; s += 2) b.Chest(lift, 1, (s / 2) % 2 == 0 ? 0 : 2, s, Math.PI);
                b.Chest(lift, 1, 0, 7, Math.PI);
                b.Item(Item.C4, 2, new Vec3d(lift.Cx(1) + Col + 3, y, lift.Z0 - 2.5));
                v.All.Add(lift.Bounds);
            }

            // Low walls.
            Look(b, BrStyle.Concrete, 5);
            Cover(b, "Cliffside wall 1", 1150, y, 1005.5, 6, true, 1);
            Cover(b, "Cliffside wall 2", 1196, y, 1004, 4.5, true, 2);
            Cover(b, "Cliffside wall 3", 1168, y, 1045, 6, true, 1);
            Cover(b, "Cliffside wall 4", 1150, y, 1040, 4.5, false, 2);
            Cover(b, "Cliffside wall 5", 1128, y, 975.5, 6, true, 1);
            Cover(b, "Cliffside wall 6", 1185, y, 1040, 4.5, false, 1);
            b.Item(Item.Grenade, 2, new Vec3d(1176, y, 1004));
            b.Item(Item.Grenade, 2, new Vec3d(1145, y, 1046));
            b.Poi("Cliffside", new Vec3d(1160, y, 1000), v.All.ToArray());
        }

        /// <summary>A bridge of floor tiles (about 1.5 m each), its top at <paramref name="top"/>.</summary>
        private static void Bridge(BrBuild b, double x0, double z0, double x1, double z1, double top)
        {
            double s = b.World.Tiles.Config.TileSize, sx = x1 - x0, sz = z1 - z0;
            int nx = (int)Math.Ceiling(sx / s - 1e-9), nz = (int)Math.Ceiling(sz / s - 1e-9);
            for (int i = 0; i < nx; i++)
                for (int k = 0; k < nz; k++)
                {
                    var box = new Aabb(new Vec3d(x0 + sx * i / nx, top - Col, z0 + sz * k / nz), new Vec3d(x0 + sx * (i + 1) / nx, top, z0 + sz * (k + 1) / nz));
                    int id = b.World.Tiles.AddTile(box, Dir.Up, TileKind.Floor);
                    b.Info.Tiles.Add(id);
                    b.Info.Floors.Add(id);
                }
            b.Info.Bridges.Add(new Aabb(new Vec3d(x0, top - Col, z0), new Vec3d(x1, top, z1)));
        }

        // ---------------------------------------------------------------- footed places

        /// <summary>Rim Farm: barns, yards and cottages east of the spawn region.</summary>
        private void RimFarm(BrBuild b)
        {
            var f = new Builder(this, b, terrace: false);
            f.Yard("Rim Farm F1", 1104, 888, BrStyle.Timber, 5);
            f.Warehouse("Rim Farm barn", 1132, 887, BrStyle.Timber, 6);
            f.House("Rim Farm F3", 1162, 887, BrStyle.Brick, 3);
            f.Row("Rim Farm F4", 1180, 887, 3, false, 2, BrStyle.Brick, 10);
            f.Shed("Rim Farm F5", 1192, 888, BrStyle.Timber, 7);
            f.Shed("Rim Farm F6", 1204, 888, BrStyle.Timber, 8);
            f.House("Rim Farm F7", 1104, 917, BrStyle.Plaster, 6);
            f.Row("Rim Farm F8", 1122, 922, 4, true, 3, BrStyle.Brick, 11);
            f.Yard("Rim Farm F9", 1155, 914, BrStyle.Stone, 6);
            f.Warehouse("Rim Farm F10", 1186, 913, BrStyle.Timber, 9);
            Look(b, BrStyle.Stone, 2);
            FootedWall(b, "Rim Farm wall 1", 1150, 940, 9, true, 1);
            FootedWall(b, "Rim Farm wall 2", 1182, 941, 7.5, true, 1);
            b.Poi("Rim Farm", new Vec3d(1160, Base, 912), f.All.ToArray());
        }

        /// <summary>West Road: farms and cottages along the west border street, either side of the road port.</summary>
        private void WestRoad(BrBuild b)
        {
            var f = new Builder(this, b, terrace: false);
            f.Yard("West Road W1", 888, 966, BrStyle.Stone, 7);
            f.House("West Road W2", 916, 970, BrStyle.Painted, 4);
            f.Row("West Road W3", 888, 995, 4, false, 1, BrStyle.Brick, 12);
            f.House("West Road W4", 902, 997, BrStyle.Plaster, 7);
            f.Tower("West Road mill", 922, 997, 2, 3, false, BrStyle.Timber, 10);
            f.Shed("West Road W6", 939, 997, BrStyle.Timber, 11);
            f.Shed("West Road W7", 939, 1009, BrStyle.Timber, 12);
            f.Row("West Road W8", 888, 1025, 3, true, 0, BrStyle.Plaster, 8);
            f.Shed("West Road W9", 915, 1027, BrStyle.Brick, 4);
            f.Row("West Road W13", 926, 1032, 2, true, 1, BrStyle.Brick, 13);
            f.Warehouse("West Road W10", 888, 1062, BrStyle.Brick, 5);
            f.House("West Road W11", 920, 1062, BrStyle.Painted, 5);
            f.Shed("West Road W12", 939, 1066, BrStyle.Timber, 13);
            Look(b, BrStyle.Stone, 3);
            FootedWall(b, "West Road wall 1", 925, 1042.5, 6, true, 1);
            FootedWall(b, "West Road wall 2", 935, 1090, 7.5, true, 2);
            b.Poi("West Road", new Vec3d(912, Base, 1030), f.All.ToArray());
        }

        /// <summary>Lookout Ridge: a five-storey lookout on the crest (windows all round), cottages and a yard round its foot.</summary>
        private void LookoutRidge(BrBuild b)
        {
            var f = new Builder(this, b, terrace: false);
            GreyboxBlock.Building look = f.Tower("Ridge lookout", 955.5, 1145.4, 2, 5, true, BrStyle.Steel, 6);
            f.Shed("Ridge hut", 934, 1110, BrStyle.Timber, 14);
            f.House("Ridge R3", 896, 1100, BrStyle.Plaster, 9);
            f.Yard("Ridge R4", 893, 1192, BrStyle.Stone, 8);
            f.Row("Ridge R5", 985, 1104, 3, true, 2, BrStyle.Brick, 14);
            f.House("Ridge R6", 1000, 1190, BrStyle.Timber, 15);
            f.Shed("Ridge R7", 915, 1160, BrStyle.Stone, 6);
            f.House("Ridge farmhouse", 956, 1072, BrStyle.Painted, 6);
            Look(b, BrStyle.Ruin, 0);
            FootedWall(b, "Ridge ruin 1", 975, 1170, 7.5, false, 2);
            FootedWall(b, "Ridge ruin 2", 945, 1135, 6, true, 2);
            FootedWall(b, "Ridge ruin 3", 965, 1192, 6, true, 1);
            if (look != null) b.Item(Item.Grenade, 2, new Vec3d(look.Cx(1) + Col + 3, look.Top, look.Cz(2) + Col + 3));
            b.Poi("Lookout Ridge", new Vec3d(962, Base + RidgeHeight, 1155), f.All.ToArray());
        }

        /// <summary>
        /// The hill's slopes: broken stretches of an old outer wall (ruin, mostly two tiles tall: cover on the climb), huts,
        /// chests on small level spots, bushes, rocks and trees to hide behind on the way up.
        /// </summary>
        private void Slopes(BrBuild b)
        {
            var f = new Builder(this, b, terrace: false);
            // A watch post on the north slope and a lodge on the east slope (flat roofs over the slopes).
            f.Tower("North watch post", 1140, 1188, 2, 3, true, BrStyle.Stone, 7);
            f.House("East lodge", 1203, 1098, BrStyle.Timber, 17);

            Look(b, BrStyle.Ruin, 1);
            // West slope, the way up from Underhill to the gate.
            FootedWall(b, "west slope wall 1", 1012, 1100, 7.5, false, 2);
            FootedWall(b, "west slope wall 2", 1028, 1124, 6, false, 2);
            FootedWall(b, "west slope wall 3", 1042, 1110, 7.5, false, 2);
            FootedWall(b, "west slope wall 4", 1056, 1130, 6, false, 2);
            FootedWall(b, "west slope wall 5", 1070, 1128, 6, false, 1);
            FootedWall(b, "west slope wall 6", 1034, 1150, 7.5, true, 2);
            // North slope.
            FootedWall(b, "north slope wall 1", 1098, 1168, 9, true, 2);
            FootedWall(b, "north slope wall 3", 1150, 1166, 6, true, 1);
            FootedWall(b, "north slope wall 4", 1080, 1196, 7.5, true, 2);
            // East slope.
            FootedWall(b, "east slope wall 1", 1172, 1118, 7.5, false, 2);
            FootedWall(b, "east slope wall 2", 1182, 1134, 6, false, 2);
            FootedWall(b, "east slope wall 3", 1196, 1160, 7.5, false, 1);

            f.Shed("shepherd's hut", 1048, 1182, BrStyle.Timber, 16);
            f.Shed("east slope hut", 1200, 1192, BrStyle.Stone, 9);
            HillChest(b, "west slope chest", 1022, 1140, 0);
            HillChest(b, "north slope chest", 1112, 1200, Math.PI);
            HillChest(b, "north-east slope chest", 1175, 1195, Math.PI);
            HillChest(b, "east slope chest", 1186, 1150, -Math.PI / 2);
            HillChest(b, "north-west slope chest", 1068, 1215, Math.PI);

            // More broken wall on the climb (props only stand on near-level ground, so the slopes' cover is wall).
            FootedWall(b, "west slope wall 7", 1004, 1125, 6, false, 2);
            FootedWall(b, "west slope wall 8", 1060, 1152, 6, true, 1);
            FootedWall(b, "north slope wall 5", 1112, 1210, 7.5, true, 2);
            FootedWall(b, "east slope wall 4", 1210, 1130, 6, false, 2);

            // Trees, bushes and rocks on the level ground round the hill's foot.
            int seed = 8100;
            foreach (var (x, z) in new[] { (984.0, 1076.0), (940.0, 1216.0), (962.0, 1218.0) }) b.Tree(x, z, seed++);
            foreach (var (x, z) in new[] { (978.0, 1064.0), (1215.0, 1215.0) }) b.Bush(x, z, seed++);
            foreach (var (x, z) in new[] { (992.0, 1062.0), (944.0, 1200.0) }) b.Rocks(x, z, seed++);
        }
    }
}
