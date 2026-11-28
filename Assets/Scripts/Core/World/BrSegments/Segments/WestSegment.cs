using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// The West segment of the battle royale map (id 3, see <see cref="BrLayout"/>): the Construction Site. The spawn
    /// region stands on the rim to the west; its pads walk east into the home POI, the <b>Site Compound</b> (site office,
    /// containers, stacked materials) round the anchor. North of it, inside 4.5 m hoarding, <b>The Frames</b>: five
    /// unfinished towers (6 to 10 storeys of bare slabs on a concrete stair core) tied together by scaffold bridges, with
    /// a canteen, containers and an explosives store in the yard. South, the finished <b>Phase One</b> apartment blocks
    /// (a sealed flat on every floor), a show home and site cabins, and <b>The Dig</b>: a 9 m excavation pit with a
    /// half-built core rising out of it, a ramp and a stair down, a scaffold bridge out of the core to the rim. On the
    /// rim side the <b>Workers' Camp</b> under <b>Spoil Hill</b> (north) and the <b>Batching Plant</b> among aggregate
    /// heaps (south); east, the <b>Plant Hire</b> depot on the road to the centre.
    /// </summary>
    public sealed class WestSegment : BrSegment
    {
        public override int Id => 3;

        public override string HomePoiName => "Site Compound";

        private const double Col = GreyboxBlock.Column, M = GreyboxBlock.Module, SH = GreyboxBlock.StoreyHeight;
        private static double Base => BrGround.Base;

        // ---- The ground.
        /// <summary>The excavation pit's floor rectangle at the rim (its faces lean in from it at <see cref="PitFace"/>).</summary>
        public static readonly Rect2 Pit = new Rect2(298.05, 488.55, 369.3, 556.05); // full-depth faces mid height-field cell; the east face, which the ramp shortens, on a vertex
        public const double PitDepth = 9.0, PitFace = 70, RampSlope = 15;
        /// <summary>The ramp into the pit runs down its east side from the north rim, east of this.</summary>
        public const double RampX0 = 358.8; // on a height-field vertex: a ramp-side face of any depth keeps to the two slope bands
        /// <summary>The ground round the pit is level at <see cref="BrGround.Base"/> out to this, easing to the roll by <see cref="PitFlatEase"/>.</summary>
        public const double PitFlat = 14, PitFlatEase = 42;
        /// <summary>Hills: Spoil Hill over the workers' camp and the batching plant's aggregate heaps (centre, radius, height).</summary>
        public static readonly (double x, double z, double r, double h)[] Hills =
        {
            (125, 800, 38, 13), (120, 500, 24, 9), (160, 494, 16, 5.5), (110, 570, 18, 6.5),
        };

        // ---- What the tests check (filled by Build).
        public readonly List<WestKit.SealedRoom> Sealed = new List<WestKit.SealedRoom>();
        public readonly List<WestKit.Lookout> Lookouts = new List<WestKit.Lookout>();
        public readonly List<WestKit.Span> Spans = new List<WestKit.Span>();
        /// <summary>Where the POIs are walked to in the tests (feet).</summary>
        public readonly List<(string Name, Vec3d At)> Goals = new List<(string, Vec3d)>();

        public override double Ground(double x, double z)
        {
            double h = BrGround.Shared(x, z);
            foreach (var k in Hills) h += GroundShapes.Hill(x, z, k.x, k.z, k.r, k.h);
            double d = GroundShapes.ToRect(x, z, Pit.X0, Pit.Z0, Pit.X1, Pit.Z1);
            if (d < PitFlatEase)
            {
                double w = 1 - GroundShapes.Smoothstep((d - PitFlat) / (PitFlatEase - PitFlat));
                h += (Base - h) * w;
            }
            return h - PitDig(x, z);
        }

        /// <summary>How deep the pit is dug at a point (0 outside): 70 degree faces down to the floor, a 15 degree ramp down the east side.</summary>
        public static double PitDig(double x, double z)
        {
            double inset = Math.Min(Math.Min(x - Pit.X0, Pit.X1 - x), Math.Min(z - Pit.Z0, Pit.Z1 - z));
            if (inset <= 0) return 0;
            double face = Math.Tan(PitFace * Math.PI / 180);
            double dig = Math.Min(PitDepth, inset * face);
            double ramp = Math.Min(PitDepth, (Pit.Z1 - z) * Math.Tan(RampSlope * Math.PI / 180)) + Math.Max(0, RampX0 - x) * face;
            return Math.Min(dig, ramp);
        }

        public override void Build(BrBuild b)
        {
            Sealed.Clear(); Lookouts.Clear(); Spans.Clear(); Goals.Clear();
            b.Nature.Groves = 5; b.Nature.LoneTrees = 30; b.Nature.RockGroups = 18; b.Nature.Bushes = 60;
            Home(b);
            Frames(b);
            Dig(b);
            Flats(b);
            PlantHire(b);
            BatchingPlant(b);
            Camp(b);
            FlankCover(b);
            Outskirts(b);
            ScatterCover(b);
            // Loot out on every scaffold bridge: worth the exposure.
            foreach (WestKit.Span sp in Spans)
            {
                double cx = (sp.Deck.Min.X + sp.Deck.Max.X) / 2, cz = (sp.Deck.Min.Z + sp.Deck.Max.Z) / 2;
                b.Chest(new Vec3d(cx, sp.Deck.Max.Y, cz), sp.AlongX ? 0 : Math.PI / 2);
            }
            b.Style = BrStyle.Brick; b.Variant = 0;
        }

        private static void Crates(BrBuild b, double y, (double x, double z, double sx, double sz, int rows)[] stacks)
        {
            foreach (var s in stacks) WestKit.Crate(b, s.x, y, s.z, s.sx, s.sz, s.rows);
        }

        /// <summary>Stacks on their own small footings (open ground that isn't level).</summary>
        private static void FootedCrates(BrBuild b, (double x, double z, double sx, double sz, int rows)[] stacks)
        {
            foreach (var s in stacks)
                if (b.TryFoot(s.x, s.z, s.x + s.sx, s.z + s.sz, 1.0, out double sy)) WestKit.Crate(b, s.x, sy, s.z, s.sx, s.sz, s.rows);
        }

        // ---------------------------------------------------------------- the home POI

        /// <summary>
        /// The Site Compound round the anchor: a two-storey site office east of the anchor (across every pad's view past
        /// it: its back wall is blank), four containers north and south (two sealed), stacked materials and two chests in
        /// the yard. Exactly ten chests.
        /// </summary>
        private void Home(BrBuild b)
        {
            Vec3d a = b.Anchor;
            double y = Base;
            // Site office: 3 x 5 modules, two storeys, stairs up its east lane, a sealed safe room behind them.
            var o = new GreyboxBlock.Building(a.X + 8.0, a.Z - 15.9, 3, 5, 2);
            b.Foot(o, 2.2, y);
            b.Stairwell(o, 2, true, +1);
            for (int s = 0; s < 2; s++)
            {
                o.AlongZ[s, 2, 0] = WallKind.Door; o.AlongZ[s, 2, 1] = WallKind.Wall; o.AlongZ[s, 2, 2] = WallKind.Door;
            }
            o.AlongX[0, 3, 2] = WallKind.Wall; o.AlongZ[0, 2, 3] = WallKind.Wall; o.AlongZ[0, 2, 4] = WallKind.Wall; // the safe room
            WallKind[] west0 = { WallKind.Window, WallKind.Wall, WallKind.WideDoor, WallKind.Wall, WallKind.Window };
            WallKind[] west1 = { WallKind.Window, WallKind.Wall, WallKind.Window, WallKind.Wall, WallKind.Window };
            for (int j = 0; j < 5; j++) { o.AlongZ[0, 0, j] = west0[j]; o.AlongZ[1, 0, j] = west1[j]; }
            o.AlongZ[1, 3, 0] = WallKind.Window; o.AlongZ[1, 3, 4] = WallKind.Window;  // the back: blank behind the anchor
            o.AlongX[0, 0, 0] = WallKind.ShutDoor; o.AlongX[0, 0, 1] = WallKind.Window;
            o.AlongX[1, 0, 0] = WallKind.Window; o.AlongX[1, 0, 1] = WallKind.Window;
            o.AlongX[0, 5, 1] = WallKind.ShutDoor;
            o.AlongX[1, 5, 0] = WallKind.Window; o.AlongX[1, 5, 1] = WallKind.Window;
            b.Style = BrStyle.Painted; b.Variant = 1;
            b.Raise(o, "Site office");
            BrKit.Parapet(b, o);
            b.Chest(o, 0, 0, 0, 0);
            b.Chest(o, 2, 4, 0, Math.PI);
            b.Chest(o, 0, 4, 1, Math.PI / 2);
            b.Chest(o, 1, 2, 2, -Math.PI / 2);
            Sealed.Add(new WestKit.SealedRoom { Building = o, Storey = 0, Cells = new[] { (2, 3), (2, 4) }, Name = "Site office safe room" });
            Lookouts.Add(new WestKit.Lookout { At = new Vec3d(o.Cx(0) + Col + 3, o.Top, o.Cz(1) + Col + 3), Name = "Site office roof" });

            // Containers: two north of the yard, two south (one of each pair sealed).
            (double dx, double dz, bool door, int colour)[] boxes = { (-17, 20, false, 2), (-2, 22, true, 3), (-17, -26.6, true, 4), (-2, -28.6, false, 5) };
            foreach (var c in boxes)
            {
                double x = a.X + c.dx, z = a.Z + c.dz;
                b.Foot(x, z, x + 2 * M + Col, z + M + Col, 2.2, y);
                WestKit.Container(b, Sealed, x, y, z, true, c.door, c.colour);
            }
            // The yard: stacked materials and two chests behind them.
            Crates(b, y, new (double, double, double, double, int)[]
            {
                (a.X - 7.5, a.Z + 5.5, 1.5, 3.0, 1), (a.X - 7.5, a.Z - 8.5, 1.5, 3.0, 1), (a.X - 1.0, a.Z + 10.0, 3.0, 1.5, 1),
                (a.X + 2.0, a.Z + 10.0, 1.5, 1.5, 2), (a.X - 1.0, a.Z - 11.5, 3.0, 1.5, 1), (a.X + 2.0, a.Z - 11.5, 1.5, 1.5, 2),
                (a.X - 22, a.Z + 2.5, 1.5, 3.0, 1), (a.X - 22, a.Z - 5.5, 1.5, 3.0, 1), (a.X - 30, a.Z + 9.0, 3.0, 1.5, 1),
                (a.X - 30, a.Z - 10.5, 3.0, 1.5, 1),
            });
            b.Chest(new Vec3d(a.X - 3, y, a.Z + 4), Math.PI);
            b.Chest(new Vec3d(a.X - 3, y, a.Z - 4), 0);
            b.Poi(HomePoiName, a, o.Bounds);
            Goals.Add((HomePoiName, new Vec3d(a.X - 3, y, a.Z)));
            b.Open(new Rect2(a.X - 40, a.Z - 40, a.X + 40, a.Z + 40));
        }

        // ---------------------------------------------------------------- The Frames (north)

        /// <summary>
        /// The Frames: one graded site inside 4.5 m hoarding with four gates (home, the north road, the camp, the east),
        /// five unfinished towers tied by scaffold bridges (Frame One 10 storeys to Frame Two 7; Frame Four 6 to Frame
        /// Three 9 to Frame Five, a 4 x 3, 10 storeys), a canteen, containers, an explosives store, a rebar yard and a
        /// concrete pour in the yard.
        /// </summary>
        private void Frames(BrBuild b)
        {
            double y = b.Foot(184, 714, 406, 824, 3);
            // Hoarding, with the gates.
            WestKit.Hoarding(b, 186, 716, 272, 716, y); WestKit.Hoarding(b, 290, 716, 404, 716, y);
            WestKit.Hoarding(b, 186, 821.7, 246, 821.7, y); WestKit.Hoarding(b, 266, 821.7, 404, 821.7, y);
            WestKit.Hoarding(b, 186, 716.3, 186, 760, y); WestKit.Hoarding(b, 186, 772, 186, 821.7, y);
            WestKit.Hoarding(b, 403.7, 716.3, 403.7, 760, y); WestKit.Hoarding(b, 403.7, 772, 403.7, 821.7, y);

            b.Style = BrStyle.Concrete;
            b.Variant = 0;
            var t1 = WestKit.FrameTower(b, Sealed, Lookouts, 200, y, 790, 3, 3, 10, 2, false, "Frame One");
            b.Variant = 1;
            var t2 = WestKit.FrameTower(b, Sealed, Lookouts, 230, y, 790, 3, 3, 7, 2, true, "Frame Two");
            b.Variant = 2;
            var t3 = WestKit.FrameTower(b, Sealed, Lookouts, 300, y, 784, 3, 3, 9, 2, false, "Frame Three");
            b.Variant = 3;
            var t4 = WestKit.FrameTower(b, Sealed, Lookouts, 300, y, 742, 3, 3, 6, 2, false, "Frame Four");
            b.Variant = 4;
            var t5 = WestKit.FrameTower(b, Sealed, Lookouts, 340, y, 784, 4, 3, 10, 3, true, "Frame Five");

            // Scaffold bridges (never onto a roof: the parapet is in the way).
            double z12 = t1.Cz(1) + Col + 1.5;
            foreach (int s in new[] { 3, 6 }) Spans.Add(WestKit.Bridge(b, t1.Cx(3) + Col, z12, t2.X0, z12 + 3, y + s * SH, true, s == 6));
            double x43 = t4.Cx(1) + Col + 1.5;
            foreach (int s in new[] { 3, 5 }) Spans.Add(WestKit.Bridge(b, x43, t4.Cz(3) + Col, x43 + 3, t3.Z0, y + s * SH, false, s == 5));
            double z35 = t3.Cz(1) + Col + 1.5;
            foreach (int s in new[] { 4, 8 }) Spans.Add(WestKit.Bridge(b, t3.Cx(3) + Col, z35, t5.X0, z35 + 3, y + s * SH, true, s == 4));

            // The yard: canteen, containers, the explosives store (C4 inside), a rebar yard, a concrete pour.
            b.Style = BrStyle.Timber; b.Variant = 2;
            BrKit.Terrace(b, 200, y, 724, 3, true, 1);
            WestKit.Container(b, Sealed, 236, y, 722, true, true, 6);
            WestKit.Container(b, Sealed, 236, y, 733, true, false, 7);
            b.Style = BrStyle.Industrial; b.Variant = 1;
            var store = BrKit.Shed(b, 380, y, 724);
            Sealed.Add(new WestKit.SealedRoom { Building = store, Storey = 0, Cells = new[] { (0, 0) }, Name = "Explosives store" });
            b.Item(Item.C4, 2, new Vec3d(store.X0 + Col + 1.5, y, store.Z0 + Col + 1.5));
            b.Item(Item.C4, 2, new Vec3d(store.X0 + Col + 4.5, y, store.Z0 + Col + 1.5));
            for (int k = 0; k < 4; k++) b.Strip(new Vec3d(330 + (k % 2) * 16, y, 724 + 5 * k), 12, true, 1); // rebar racks
            // The concrete pour: shuttering one row high round a 12 x 9 m slab, open on its west side.
            b.Strip(new Vec3d(258, y, 742), 12, true, 1);
            b.Strip(new Vec3d(258, y, 751), 12, true, 1);
            b.Strip(new Vec3d(269.7, y, 742.3), 8.7, false, 1);
            b.Chest(new Vec3d(266, y, 746.5), -Math.PI / 2);
            Crates(b, y, new (double, double, double, double, int)[]
            {
                (224, 772, 3, 1.5, 1), (227, 772, 1.5, 1.5, 2), (258, 772, 1.5, 3, 1), (262, 800, 3, 1.5, 1),
                (282, 760, 1.5, 3, 1), (282, 790, 3, 1.5, 1), (285, 790, 1.5, 1.5, 2), (330, 760, 3, 1.5, 1),
                (333, 770, 1.5, 3, 1), (378, 760, 3, 1.5, 1), (381, 760, 1.5, 1.5, 2), (390, 812, 1.5, 3, 1),
                (205, 760, 3, 1.5, 1), (196, 748, 1.5, 3, 1), (300, 726, 3, 1.5, 1), (360, 742, 1.5, 3, 1),
            });
            b.Chest(new Vec3d(229.5, y, 776), 0);
            b.Item(Item.Grenade, 2, new Vec3d(261, y, 776));
            b.Item(Item.Launcher, 6, new Vec3d(t3.Cx(2) + Col + 3, y + 8 * SH, t3.Cz(1) + Col + 0.6));
            b.Item(Item.Grenade, 2, new Vec3d(t1.Cx(1) + Col + 3, t1.Top, t1.Cz(1) + Col + 1.5));
            b.Item(Item.Grenade, 2, new Vec3d(t5.Cx(1) + Col + 3, t5.Top, t5.Cz(1) + Col + 1.5));
            b.Style = BrStyle.Concrete; b.Variant = 0;
            b.Poi("The Frames", new Vec3d(280, y, 770), t1.Bounds, t2.Bounds, t3.Bounds, t4.Bounds, t5.Bounds);
            Goals.Add(("The Frames", new Vec3d(276, y, 745)));
            b.Open(new Rect2(184, 714, 406, 824));
            b.Open(new Rect2(244, 824, 268, 858));
        }

        // ---------------------------------------------------------------- The Dig (south east)

        /// <summary>
        /// The Dig: the excavation pit, 9 m deep, its floor about 66 x 61 m. A ramp runs down its east side from the north rim,
        /// a breakable stair flight down its south face. Phase Two's core stands on the floor (five storeys, so its top
        /// storeys rise out of the pit) with a scaffold bridge from its second floor to the west rim. A sealed site locker
        /// and stacked materials on the floor, a one-row safety barrier round the rim (gaps at the ramp, the stair and the
        /// bridge), three cement silos north of it.
        /// </summary>
        private void Dig(BrBuild b)
        {
            double floor = Base - PitDepth, rim = Base;
            Rect2 p = Pit;
            // No footings in or near the pit (a footing's bank would fill or cut its faces): the floor is level already.
            b.Style = BrStyle.Concrete; b.Variant = 5;
            var core = WestKit.FrameTower(b, Sealed, Lookouts, 324.4, floor, 512.4, 3, 3, 5, 1, true, "Phase Two core");
            double bz = core.Cz(1) + Col + 1.5;
            Spans.Add(WestKit.Bridge(b, p.X0 - 1.05, bz, core.X0, bz + 3, floor + 2 * SH, true, true));
            b.Climb(new Aabb(new Vec3d(p.X0 - 3.45, rim, bz), new Vec3d(p.X0 - 1.05, floor + 2 * SH, bz + 3)), Dir.East, false);
            b.Climb(new Aabb(new Vec3d(350, floor, p.Z0), new Vec3d(353, rim, p.Z0 + 2 * PitDepth)), Dir.South, true);
            // The floor: a sealed locker, stacked materials.
            b.Style = BrStyle.Steel; b.Variant = 8;
            var locker = BrKit.Shed(b, 346, floor, 530);
            Sealed.Add(new WestKit.SealedRoom { Building = locker, Storey = 0, Cells = new[] { (0, 0) }, Name = "Pit locker" });
            Crates(b, floor, new (double, double, double, double, int)[]
            {
                (305, 535, 3, 1.5, 1), (318, 540, 1.5, 3, 1), (348, 518, 3, 1.5, 1), (305, 498, 1.5, 1.5, 2), (306.5, 498, 1.5, 3, 1),
                (330, 500, 3, 1.5, 1), (352, 541, 1.5, 3, 1),
            });
            b.Chest(new Vec3d(308, floor, 539), Math.PI);
            b.Item(Item.Grenade, 2, new Vec3d(335, floor, 545));
            // The safety barrier round the rim, a metre out (gaps at the ramp, the stair and the bridge).
            b.Strip(new Vec3d(p.X0, rim, p.Z1 + 1), 357 - p.X0, true, 1);
            b.Strip(new Vec3d(p.X0, rim, p.Z0 - 1.3), 348 - p.X0, true, 1);
            b.Strip(new Vec3d(355, rim, p.Z0 - 1.3), p.X1 - 355, true, 1);
            b.Strip(new Vec3d(p.X0 - 1.3, rim, p.Z0), 518.5 - p.Z0, false, 1);
            b.Strip(new Vec3d(p.X0 - 1.3, rim, 525.5), p.Z1 - 525.5, false, 1);
            b.Strip(new Vec3d(p.X1 + 1, rim, p.Z0), p.Z1 - p.Z0, false, 1);
            // Cement silos north of the pit (far enough that their footings leave its face alone).
            b.Style = BrStyle.Industrial; b.Variant = 2;
            foreach (double sx in new[] { 310.0, 320.0, 330.0 })
            {
                double sy = b.Foot(sx, 576, sx + M + Col, 576 + M + Col, 2.2, rim);
                WestKit.Silo(b, Sealed, sx, sy, 576, 3);
            }
            b.Poi("The Dig", new Vec3d(334, floor, 522), core.Bounds);
            Goals.Add(("The Dig", new Vec3d(308, floor, 545)));
            b.Open(Pit.Grow(16));
        }

        // ---------------------------------------------------------------- Phase One (west of the dig)

        /// <summary>Phase One: two finished apartment blocks (7 and 6 storeys) round a courtyard, a show home, site cabins.</summary>
        private void Flats(BrBuild b)
        {
            b.Style = BrStyle.Plaster; b.Variant = 1;
            double w = 4 * M + Col, d = 3 * M + Col;
            double y1 = b.Foot(200, 488, 200 + w, 488 + d, 3);
            var a1 = WestKit.Apartments(b, Sealed, Lookouts, 200, y1, 488, 7, 0, "Kestrel Court");
            b.Style = BrStyle.Brick; b.Variant = 3;
            double y2 = b.Foot(200, 540, 200 + w, 540 + d, 3);
            var a2 = WestKit.Apartments(b, Sealed, Lookouts, 200, y2, 540, 6, 1, "Heron Court");
            // The courtyard between them.
            double cy = b.Foot(203, 512, 222, 535, 0.5);
            Crates(b, cy, new (double, double, double, double, int)[]
            {
                (205, 516, 3, 1.5, 1), (214, 522, 1.5, 3, 1), (205, 530, 1.5, 1.5, 2), (218, 514, 1.5, 1.5, 1),
            });
            b.Chest(new Vec3d(208.5, cy, 527), 0);
            // The show home and the site cabins.
            b.Style = BrStyle.Painted; b.Variant = 4;
            double hy = b.Foot(232, 512, 232 + 2 * M + Col, 512 + d, 3);
            BrKit.House(b, 232, hy, 512);
            b.Style = BrStyle.Timber; b.Variant = 1;
            // (Kept 17 m clear of the pit: a footing's 28 degree bank reaches that far up a 9 m face.)
            double ty = b.Foot(266, 480, 266 + M + Col, 480 + d, 2.2);
            BrKit.Terrace(b, 266, ty, 480, 3, false, 2);
            double cy1 = b.Foot(266, 510, 266 + M + Col, 510 + 2 * M + Col, 2.2);
            WestKit.Container(b, Sealed, 266, cy1, 510, false, true, 9);
            double cy2 = b.Foot(266, 535, 266 + M + Col, 535 + 2 * M + Col, 2.2);
            WestKit.Container(b, Sealed, 266, cy2, 535, false, false, 10);
            b.Item(Item.Bandage, 2, new Vec3d(269, cy1, 509));
            b.Style = BrStyle.Brick; b.Variant = 0;
            b.Poi("Phase One", new Vec3d(212, y1, 524), a1.Bounds, a2.Bounds);
            Goals.Add(("Phase One", new Vec3d(211, cy, 524)));
            b.Open(new Rect2(195, 484, 300, 565));
            b.Open(new Rect2(248, 447, 264, 607)); // the road from the south port to the compound
        }

        // ---------------------------------------------------------------- Plant Hire (east, on the road to the centre)

        private void PlantHire(BrBuild b)
        {
            b.Style = BrStyle.Industrial; b.Variant = 3;
            double y1 = b.Foot(338, 668, 338 + 4 * M + Col, 668 + 3 * M + Col, 3);
            var w1 = BrKit.Warehouse(b, 338, y1, 668);
            b.Variant = 4;
            double y2 = b.Foot(338, 612, 338 + 4 * M + Col, 612 + 3 * M + Col, 3);
            var w2 = BrKit.Warehouse(b, 338, y2, 612);
            double cy1 = b.Foot(378, 670, 378 + 2 * M + Col, 670 + M + Col, 2.2);
            WestKit.Container(b, Sealed, 378, cy1, 670, true, true, 11);
            double cy2 = b.Foot(378, 625, 378 + 2 * M + Col, 625 + M + Col, 2.2);
            WestKit.Container(b, Sealed, 378, cy2, 625, true, false, 12);
            FootedCrates(b, new (double, double, double, double, int)[]
            {
                (370, 640, 3, 1.5, 1), (373, 640, 1.5, 1.5, 2), (332, 640, 1.5, 3, 1), (370, 664, 3, 1.5, 1), (395, 665, 1.5, 3, 1),
            });
            b.Chest(new Vec3d(386, cy1, 669), 0);
            b.Item(Item.Grenade, 2, new Vec3d(385, cy2, 632.5));
            b.Poi("Plant Hire", new Vec3d(352, y1, 650), w1.Bounds, w2.Bounds);
            Goals.Add(("Plant Hire", new Vec3d(352, y1, 652)));
            b.Open(new Rect2(320, 642, 423.2, 664));
        }

        // ---------------------------------------------------------------- Batching Plant (south west, on the rim)

        private void BatchingPlant(BrBuild b)
        {
            b.Style = BrStyle.Industrial; b.Variant = 5;
            double y = b.Foot(140, 530, 140 + 4 * M + Col, 530 + 3 * M + Col, 3);
            var w = BrKit.Warehouse(b, 140, y, 530);
            b.Variant = 6;
            foreach (double x in new[] { 88.0, 97.6, 107.2 })
            {
                double sy = b.Foot(x, 596, x + M + Col, 596 + M + Col, 2.2);
                WestKit.Silo(b, Sealed, x, sy, 596, 4);
            }
            b.Style = BrStyle.Timber; b.Variant = 3;
            double ty = b.Foot(160, 590, 160 + 3 * M + Col, 590 + M + Col, 2.2);
            BrKit.Terrace(b, 160, ty, 590, 3, true, 2);
            for (int k = 1; k < Hills.Length; k++) b.SpotChest(Hills[k].x, Hills[k].z, 0);
            FootedCrates(b, new (double, double, double, double, int)[] { (130, 560, 3, 1.5, 1), (172, 560, 1.5, 3, 1) });
            b.Item(Item.Grenade, 2, new Vec3d(152, y, 529));
            b.Style = BrStyle.Brick; b.Variant = 0;
            b.Poi("Batching Plant", new Vec3d(130, y, 540), w.Bounds);
            Goals.Add(("Batching Plant", new Vec3d(150, y, 528)));
        }

        // ---------------------------------------------------------------- Workers' Camp (north west, under Spoil Hill)

        private void Camp(BrBuild b)
        {
            b.Style = BrStyle.Timber; b.Variant = 4;
            (double x, double z, int rooms, bool alongX, int sealedRoom)[] rows =
            {
                (100, 702, 4, true, 2), (100, 724, 4, true, 0), (140, 702, 3, true, 1), (140, 724, 3, true, 2),
                (168, 762, 4, false, 1), (168, 800, 3, false, 0), (130, 842, 3, true, 1),
            };
            foreach (var r in rows)
            {
                double x1 = r.x + (r.alongX ? r.rooms : 1) * M + Col, z1 = r.z + (r.alongX ? 1 : r.rooms) * M + Col;
                double y = b.Foot(r.x, r.z, x1, z1, 2.2);
                BrKit.Terrace(b, r.x, y, r.z, r.rooms, r.alongX, r.sealedRoom);
                b.Variant++;
            }
            (double x, double z, bool door, int colour)[] boxes = { (165, 702, true, 13), (165, 724, false, 14), (95, 840, true, 15) };
            foreach (var c in boxes)
            {
                double y = b.Foot(c.x, c.z, c.x + 2 * M + Col, c.z + M + Col, 2.2);
                WestKit.Container(b, Sealed, c.x, y, c.z, true, c.door, c.colour);
            }
            Vec3d top = b.SpotChest(Hills[0].x, Hills[0].z, Math.PI);
            b.Item(Item.Grenade, 2, top + new Vec3d(1.2, 0, 0));
            double cy = b.Foot(126, 714, 136, 720, 1.0);
            WestKit.Crate(b, 126, cy, 714, 3, 1.5, 1);
            WestKit.Crate(b, 132, cy, 716, 1.5, 1.5, 2);
            b.Item(Item.Bandage, 2, new Vec3d(129, cy, 718.5));
            b.Style = BrStyle.Brick; b.Variant = 0;
            b.Poi("Workers' Camp", new Vec3d(130, cy, 716));
            b.Poi("Spoil Hill", top);
            Goals.Add(("Workers' Camp", new Vec3d(129, cy, 718.5)));
            Goals.Add(("Spoil Hill", top + new Vec3d(0, 0, 1.6)));
        }

        // ---------------------------------------------------------------- the outskirts

        /// <summary>
        /// Smaller pieces in the gaps: a walled plant compound and a site manager's house east of the dig (17 m clear of
        /// its rim), a container on each flank of the pads' walk.
        /// </summary>
        private void Outskirts(BrBuild b)
        {
            b.Style = BrStyle.Concrete; b.Variant = 7;
            double cy = b.Foot(390, 500, 412.5, 522.5, 2.2);
            BrKit.Compound(b, 390, cy, 500);
            b.Style = BrStyle.Painted; b.Variant = 6;
            double hy = b.Foot(391, 540, 391 + 2 * M + Col, 540 + 3 * M + Col, 3);
            BrKit.House(b, 391, hy, 540);
            (double x, double z, bool door, int colour)[] boxes = { (195, 612, true, 16), (205, 690, false, 17) };
            foreach (var c in boxes)
            {
                double y = b.Foot(c.x, c.z, c.x + 2 * M + Col, c.z + M + Col, 2.2);
                WestKit.Container(b, Sealed, c.x, y, c.z, true, c.door, c.colour);
            }
            b.Style = BrStyle.Brick; b.Variant = 0;
        }

        // ---------------------------------------------------------------- cover over the open ground

        /// <summary>
        /// Stacked materials and short low walls over the open ground between the POIs, about every 24 m (a fixed hash
        /// jitters each spot), so no open stretch is a long run under fire. Not in the pads' approach, the home plaza,
        /// the graded site or near the pit.
        /// </summary>
        private void ScatterCover(BrBuild b)
        {
            Vec3d a = b.Anchor;
            Rect2 area = b.Area, site = new Rect2(184, 714, 406, 824).Grow(4), pit = Pit.Grow(20);
            for (int i = 0; area.X0 + 8 + i * 24 < area.X1 - 8; i++)
                for (int j = 0; area.Z0 + 8 + j * 24 < area.Z1 - 8; j++)
                {
                    uint h = BrKit.Hash(i, j, 0x5e57u);
                    double x = area.X0 + 8 + i * 24 + (h % 11) - 5, z = area.Z0 + 8 + j * 24 + ((h >> 4) % 11) - 5;
                    var r = new Rect2(x, z, x + 4.5, z + 4.5);
                    if (r.Overlaps(site) || r.Overlaps(pit) || b.InHome(r, 6) || !b.IsClear(r, 4)) continue;
                    double dx = r.CX - a.X, dz = r.CZ - a.Z;
                    if (dx < 0 && Math.Abs(Math.Atan2(dz, -dx)) < 22 * Math.PI / 180) continue; // the pads' approach
                    if (!b.TryFoot(r.X0, r.Z0, r.X1, r.Z1, 1.5, out double y)) continue;
                    switch ((h >> 8) % 3)
                    {
                        case 0:
                            WestKit.Crate(b, x, y, z, 3, 1.5, 1);
                            WestKit.Crate(b, x + 3, y, z, 1.5, 1.5, 2);
                            break;
                        case 1:
                            WestKit.Crate(b, x, y, z, 1.5, 3, 1);
                            WestKit.Crate(b, x + 3, y, z + 1.5, 1.5, 3, 1);
                            break;
                        default:
                            b.Strip(new Vec3d(x, y, z + 3), 4.5, true, 1);
                            WestKit.Crate(b, x + 1.5, y, z, 1.5, 1.5, 1);
                            break;
                    }
                }
        }

        // ---------------------------------------------------------------- cover beside the pads' walk

        /// <summary>
        /// Stacked materials flanking the pads' walk to the compound (outside the approach wedge, so the ground between
        /// pad and anchor stays the same for every pad), mirrored north and south of the anchor's line.
        /// </summary>
        private void FlankCover(BrBuild b)
        {
            Vec3d a = b.Anchor;
            (double dx, double dz)[] spots = { (-58, 28), (-84, 36), (-112, 44), (-136, 50), (-60, 62), (-100, 74) };
            foreach (var p in spots)
                foreach (int sgn in new[] { 1, -1 })
                {
                    double x = a.X + p.dx, z = a.Z + sgn * p.dz - (sgn < 0 ? 1.5 : 0);
                    var r = new Rect2(x, z, x + 4.5, z + 1.5);
                    if (!b.IsClear(r, 2)) continue;
                    if (!b.TryFoot(r.X0, r.Z0, r.X1, r.Z1, 1.0, out double y)) continue;
                    WestKit.Crate(b, x, y, z, 3, 1.5, 1);
                    WestKit.Crate(b, x + 3, y, z, 1.5, 1.5, 2);
                }
        }
    }
}
