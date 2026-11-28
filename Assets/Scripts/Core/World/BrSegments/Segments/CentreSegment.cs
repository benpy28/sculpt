using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// The Centre segment of the battle royale map (id 4): the Old City, the map's middle and usually the late game.
    ///
    /// From the middle out:
    /// <list type="bullet">
    /// <item>The <b>Citadel</b>: a raised old-town terrace 126 m square, 7.5 m above the town (rock faces too tall to
    /// climb), reached by eight 12 degree ramps: one up each main road and one more off each face in a pinwheel. On it the
    /// <b>Old Spire</b>, a 12-storey landmark (the tallest thing on the map, roof with a parapet, two sealed vault floors),
    /// round a small plaza, and twelve dense old-town blocks on narrow streets.</item>
    /// <item>The <b>lower town</b>: four quarters of 24 m blocks on 5 m streets (row courts with party walls and sealed
    /// rooms, town houses with a sealed top room, cloisters, guild halls with sealed storerooms and lofts, warehouse yards,
    /// sheds, ruins, small plazas with planters), each with its own towers: Bell Quarter (north east, twin 7-storey towers,
    /// a roof bridge and a sky bridge), Tanners' Row (north west, a 10-storey watch tower over warehouses and yards),
    /// Scholars' Close (south west, twin 6-storey towers among walled cloisters) and Merchant Steps (south east, a quarter
    /// raised 2.7 m on a climbable ledge, with a skyway that steps up 6, 8 and 10 storeys).</item>
    /// <item>The <b>city wall</b>: 4.5 m (too tall to climb) round the town, a gate on every main road and two breaches a
    /// side; outside it the <b>Outer Gardens</b>: walled allotments, huts and sealed sheds, four graveyards, orchards.</item>
    /// </list>
    /// Everything inside the wall stands on exactly flat ground (town 1.5 m, steps 4.2 m, citadel 9.0 m), so the town needs
    /// no footings (a footing's bank would cut a walkable slope into a rock face); outside the wall the ground rolls into
    /// the shared seams and everything there stands on footings.
    /// </summary>
    public sealed class CentreSegment : BrSegment
    {
        public override int Id => 4;

        public override string HomePoiName => "Old City";

        // ================================================================ Ground

        private const double Col = GreyboxBlock.Column, M = GreyboxBlock.Module, SH = GreyboxBlock.StoreyHeight, WH = GreyboxBlock.WallHeight;

        /// <summary>Levels: the lower town, the Merchant Steps, the citadel top.</summary>
        public const double TownLevel = 1.5, StepLevel = 4.2, CitadelLevel = 9.0;
        /// <summary>The citadel's half side (from the map's centre), on the 1.5 m ground grid.</summary>
        public const double CitadelHalf = 63;
        /// <summary>The Merchant Steps (south east quarter), in (u, v) from the map's centre.</summary>
        public const double StepU0 = 67.5, StepU1 = 154.5, StepV0 = -154.5, StepV1 = -67.5;
        /// <summary>The city wall's inner face, from the map's centre.</summary>
        public const double WallHalf = 158;
        /// <summary>The ground is exactly flat out to here, then eases to the shared ground over <see cref="FlatBlend"/>.</summary>
        public const double FlatTo = 172, FlatBlend = 28;
        public const double FaceDegrees = 65, RampDegrees = 12, StepFaceDegrees = 70;

        private static readonly double FaceK = Math.Tan(FaceDegrees * Math.PI / 180), StepK = Math.Tan(StepFaceDegrees * Math.PI / 180);

        /// <summary>A ramp: descends outward from the edge (<see cref="Edge"/> along the outward normal (nx, nz) from the
        /// map's centre), centred at <see cref="At"/> along the edge (tangent (-nz, nx)), flat <see cref="Half"/> either
        /// side, rock faces beside it, from <see cref="Top"/> down until it meets the ground.</summary>
        public struct Ramp
        {
            public int Nx, Nz;
            public double At, Half, Edge, Top, Slope;
            public string Name;
        }

        /// <summary>The citadel's eight ramps (four up the main roads, four in a pinwheel) and the steps' one.</summary>
        public static readonly Ramp[] Ramps =
        {
            new Ramp { Nx = 1, Nz = 0, At = 0, Half = 6, Edge = CitadelHalf, Top = CitadelLevel, Slope = RampDegrees, Name = "east road ramp" },
            new Ramp { Nx = -1, Nz = 0, At = 0, Half = 6, Edge = CitadelHalf, Top = CitadelLevel, Slope = RampDegrees, Name = "west road ramp" },
            new Ramp { Nx = 0, Nz = 1, At = 0, Half = 6, Edge = CitadelHalf, Top = CitadelLevel, Slope = RampDegrees, Name = "north road ramp" },
            new Ramp { Nx = 0, Nz = -1, At = 0, Half = 6, Edge = CitadelHalf, Top = CitadelLevel, Slope = RampDegrees, Name = "south road ramp" },
            // Pinwheel: east face at v = +24, north face at u = -24, west face at v = -24, south face at u = +24.
            new Ramp { Nx = 1, Nz = 0, At = 24, Half = 3.5, Edge = CitadelHalf, Top = CitadelLevel, Slope = RampDegrees, Name = "Bell Quarter ramp" },
            new Ramp { Nx = 0, Nz = 1, At = 24, Half = 3.5, Edge = CitadelHalf, Top = CitadelLevel, Slope = RampDegrees, Name = "Tanners' ramp" },
            new Ramp { Nx = -1, Nz = 0, At = 24, Half = 3.5, Edge = CitadelHalf, Top = CitadelLevel, Slope = RampDegrees, Name = "Scholars' ramp" },
            new Ramp { Nx = 0, Nz = -1, At = 24, Half = 3.5, Edge = CitadelHalf, Top = CitadelLevel, Slope = RampDegrees, Name = "Merchants' ramp" },
            // Merchant Steps: down its north edge along the street at u = 96.5.
            new Ramp { Nx = 0, Nz = 1, At = -96.5, Half = 2.25, Edge = StepV1, Top = StepLevel, Slope = RampDegrees, Name = "steps ramp" },
        };

        /// <summary>The ramp's height at (u, v) from the map's centre (it falls away as a rock face off its sides and ends).</summary>
        public static double RampAt(Ramp r, double u, double v)
        {
            double along = u * r.Nx + v * r.Nz - r.Edge;            // out from the edge
            double t = -u * r.Nz + v * r.Nx;                         // along the edge
            double across = Math.Abs(t - r.At);
            double k = Math.Tan(r.Slope * Math.PI / 180);
            return r.Top - Math.Max(0, along) * k - Math.Max(0, across - r.Half) * FaceK - Math.Max(0, -along) * FaceK;
        }

        /// <summary>A point on the ramp's middle line <paramref name="along"/> metres out from its edge, (u, v).</summary>
        public static (double u, double v) RampPoint(Ramp r, double along)
        {
            double a = r.Edge + along;
            return (a * r.Nx - r.At * r.Nz, a * r.Nz + r.At * r.Nx);
        }

        public override double Ground(double x, double z)
        {
            double c = BrLayout.MapCentre, u = x - c, v = z - c;
            double s = BrGround.Shared(x, z);
            double m = Math.Max(Math.Abs(u), Math.Abs(v));
            double w = 1 - GroundShapes.Smoothstep((m - FlatTo) / FlatBlend);
            double h = s + (TownLevel - s) * w;
            // Merchant Steps.
            h = Math.Max(h, StepLevel - GroundShapes.ToRect(u, v, StepU0, StepV0, StepU1, StepV1) * StepK);
            // The citadel and the ramps.
            h = Math.Max(h, CitadelLevel - GroundShapes.ToRect(u, v, -CitadelHalf, -CitadelHalf, CitadelHalf, CitadelHalf) * FaceK);
            foreach (Ramp r in Ramps) h = Math.Max(h, RampAt(r, u, v));
            return h;
        }

        // ================================================================ Layout

        /// <summary>Lower-town blocks: <see cref="Slot"/> m squares at these offsets from the centre (5 m streets between).</summary>
        public const double Slot = 24, SlotPitch = 29, SlotStart = 12;
        /// <summary>Citadel blocks: two rows of 24 m squares per quarter starting at these offsets.</summary>
        public static readonly double[] CitadelSlots = { 5, 34 };

        // Per quarter a 5 x 5 plan of the lower town's slots, [row j outward][column i outward]. '.' = the citadel.
        //  R row court   Z row court along z   H town house (3 storeys)   h old house (2)   C cloister
        //  G guild hall  W warehouse yard      S sheds and yard           U ruin            P plaza
        //  D the plaza a pinwheel ramp comes down into   A B E towers (the quarter's cluster, inner first)
        private static readonly string[] BellPlan = { "..DRZ", "..CPh", "RZSGU", "CRPAC", "UCRBP" };
        private static readonly string[] TannersPlan = { "..WZR", "..SWC", "DRPGS", "ZWUAW", "CRPWU" };
        private static readonly string[] ScholarsPlan = { "..DCR", "..hCZ", "CRCPU", "RCAZC", "PUBCS" };
        private static readonly string[] MerchantPlan = { "..RhG", "..RRC", "DCPUA", "RZPCB", "CUSRE" };

        private struct Quarter
        {
            public int Sx, Sz;
            public string Name;
            public string[] Plan;
            /// <summary>The pinwheel ramp through its plaza runs along x (east and west faces).</summary>
            public bool RampAlongX;
            public BrStyle Main, Second;
        }

        private static readonly Quarter[] Quarters =
        {
            new Quarter { Sx = 1, Sz = 1, Name = "Bell Quarter", Plan = BellPlan, RampAlongX = true, Main = BrStyle.Stone, Second = BrStyle.Brick },
            new Quarter { Sx = -1, Sz = 1, Name = "Tanners' Row", Plan = TannersPlan, RampAlongX = false, Main = BrStyle.Timber, Second = BrStyle.Industrial },
            new Quarter { Sx = -1, Sz = -1, Name = "Scholars' Close", Plan = ScholarsPlan, RampAlongX = true, Main = BrStyle.Plaster, Second = BrStyle.Stone },
            new Quarter { Sx = 1, Sz = -1, Name = "Merchant Steps", Plan = MerchantPlan, RampAlongX = false, Main = BrStyle.Painted, Second = BrStyle.Plaster },
        };

        /// <summary>A lower-town slot's square in map coordinates.</summary>
        public static Rect2 SlotRect(int sx, int sz, int i, int j)
        {
            double c = BrLayout.MapCentre, a = SlotStart + SlotPitch * i, b = SlotStart + SlotPitch * j;
            double x0 = sx > 0 ? c + a : c - a - Slot, z0 = sz > 0 ? c + b : c - b - Slot;
            return new Rect2(x0, z0, x0 + Slot, z0 + Slot);
        }

        private static bool OnSteps(int sx, int sz, int i, int j) => sx > 0 && sz < 0 && i >= 2 && j >= 2;

        /// <summary>What the segment promises (filled while building; the tests check it on the built map).</summary>
        public sealed class Promises
        {
            /// <summary>Towers (all with stairs to a walkable roof).</summary>
            public readonly List<GreyboxBlock.Building> Towers = new List<GreyboxBlock.Building>();
            public readonly List<string> TowerNames = new List<string>();
            /// <summary>Sealed rooms: the room's inside box, a chest in it, what it is.</summary>
            public readonly List<(Aabb Room, Vec3d Chest, string What)> Sealed = new List<(Aabb, Vec3d, string)>();
            /// <summary>Rooftop and sky bridges: the walkable space between the rails, from the deck up 2 m.</summary>
            public readonly List<Aabb> Bridges = new List<Aabb>();
            /// <summary>The lower town's POI centres (a street spot in each quarter).</summary>
            public readonly List<(Vec3d At, string What)> Places = new List<(Vec3d, string)>();
            /// <summary>Tiles and chests by part (for the budget report).</summary>
            public readonly Dictionary<string, (int Tiles, int Chests)> Cost = new Dictionary<string, (int, int)>();

            internal void Add(string part, int tiles, int chests)
            {
                Cost.TryGetValue(part, out var c);
                Cost[part] = (c.Tiles + tiles, c.Chests + chests);
            }
        }

        /// <summary>The promises of the last build (the template builds once a run; every build is the same).</summary>
        public static Promises Last { get; private set; }

        private Promises _p;

        public override void Build(BrBuild b)
        {
            _p = new Promises();
            double c = BrLayout.MapCentre;
            b.Nature.Groves = 3; b.Nature.LoneTrees = 30; b.Nature.RockGroups = 14; b.Nature.Bushes = 70;

            void Part(string name, Action a)
            {
                int t0 = b.World.Tiles.Count, c0 = b.Chests.Count;
                a();
                _p.Add(name, b.World.Tiles.Count - t0, b.Chests.Count - c0);
            }
            Part("citadel", () => Citadel(b, c));
            foreach (Quarter q in Quarters) Part(q.Name, () => LowerTown(b, q));
            Part("city wall", () => CityWall(b, c));
            Part("gardens", () => Gardens(b, c));
            Last = _p;
        }

        // ================================================================ The citadel

        private void Citadel(BrBuild b, double c)
        {
            double y = CitadelLevel;
            double half = (4 * M + Col) / 2;
            GreyboxBlock.Building spire = Spire(b, c - half, y, c - half);
            var boxes = new List<Aabb> { spire.Bounds };

            // The spire's plaza: a planter (one-tile wall) in each corner, two grenade stacks.
            b.Style = BrStyle.Stone; b.Variant = 1;
            foreach (int sx in new[] { -1, 1 })
                foreach (int sz in new[] { -1, 1 })
                {
                    b.Strip(new Vec3d(c + sx * 18 - 2.25, y, c + sz * 18), 4.5, true, 1);
                    b.Strip(new Vec3d(c + sx * 18, y, c + sz * 18 - (sz > 0 ? 4.5 : -Col)), 4.2, false, 1);
                }
            b.Item(Item.Grenade, 2, new Vec3d(c + 16, y, c - 2));
            b.Item(Item.Grenade, 2, new Vec3d(c - 16, y, c + 2));

            // Twelve old-town blocks, three per quarter (the inner corner square is the spire's plaza).
            string[] kinds = { "HRC", "GCZ", "RCh", "hCR" };
            BrStyle[] looks = { BrStyle.Stone, BrStyle.Brick, BrStyle.Plaster, BrStyle.Painted };
            int qi = 0;
            foreach (int sz in new[] { 1, -1 })
                foreach (int sx in new[] { 1, -1 })
                {
                    int k = 0;
                    for (int j = 0; j < 2; j++)
                        for (int i = 0; i < 2; i++)
                        {
                            if (i == 0 && j == 0) continue;
                            double a = CitadelSlots[i], bb = CitadelSlots[j];
                            double x0 = sx > 0 ? c + a : c - a - Slot, z0 = sz > 0 ? c + bb : c - bb - Slot;
                            b.Style = looks[(qi + k) % 4]; b.Variant = 10 + qi * 3 + k;
                            boxes.AddRange(Block(b, kinds[qi][k], x0, y, z0, (int)(BrKit.Hash(i, j, 900 + (uint)qi) & 0x7fffffff)));
                            k++;
                        }
                    qi++;
                }
            b.Poi("Old Spire", new Vec3d(c, y, c), spire.Bounds);
            b.Poi("Citadel", new Vec3d(c + 31.5, y, c + 31.5), boxes.ToArray());
            b.Open(new Rect2(c - 22, c - 22, c + 22, c + 22));
        }

        /// <summary>
        /// The Old Spire: 4 x 4 modules, 12 storeys of stone. A switchback stair up its west lane to the roof; ground floor
        /// open north and south; every floor two rooms off the stairs with windows all round, except the vault floors (4 and
        /// 8): there a bare corridor joins the flights and two windowless vaults lie behind solid walls (only a hole gets
        /// you in). A chest on every
        /// other floor, two in each vault, two and a grenade launcher on the roof.
        /// </summary>
        private GreyboxBlock.Building Spire(BrBuild b, double x, double y, double z)
        {
            const int mx = 4, mz = 4, storeys = 12;
            var t = new GreyboxBlock.Building(x, z, mx, mz, storeys) { Y0 = y };
            b.Stairwell(t, 0, true, +1);
            t.AlongX[0, 0, 2] = WallKind.WideDoor;   // south
            t.AlongX[0, mz, 2] = WallKind.WideDoor;  // north
            t.AlongZ[0, mx, 1] = WallKind.ShutDoor;  // east
            t.AlongZ[0, mx, 2] = WallKind.Window;
            t.AlongX[0, 0, 1] = WallKind.Window;
            t.AlongX[0, mz, 3] = WallKind.Window;
            for (int s = 0; s < storeys; s++)
            {
                bool vault = IsVault(s);
                // The stair lane's wall: doors where the stairs arrive and start (cells 2 and 0) and onto cell 3's room.
                // The way from one flight to the next runs through the rooms beside the lane (column 1), so on a vault
                // floor column 1 is a bare corridor and the two vaults beyond it (columns 2 and 3) are walled off.
                for (int j = 0; j < mz; j++) t.AlongZ[s, 1, j] = j == 1 || (vault && j == 3) ? WallKind.Wall : WallKind.Door;
                if (s == 0) continue;
                for (int i = 1; i < mx; i++) t.AlongX[s, 2, i] = vault ? (i == 1 ? WallKind.None : WallKind.Wall) : i == 2 ? WallKind.Door : WallKind.Wall;
                if (vault)
                {
                    for (int j = 0; j < mz; j++) t.AlongZ[s, 2, j] = WallKind.Wall;
                    continue;
                }
                for (int i = 1; i < mx; i++)
                {
                    t.AlongX[s, 0, i] = (i + s) % 2 == 0 ? WallKind.Window : WallKind.Wall;
                    t.AlongX[s, mz, i] = (i + s) % 2 == 1 ? WallKind.Window : WallKind.Wall;
                }
                for (int j = 0; j < mz; j++) t.AlongZ[s, mx, j] = (j + s) % 2 == 0 ? WallKind.Window : WallKind.Wall;
            }
            b.Style = BrStyle.Stone; b.Variant = 0;
            b.Raise(t, "Old Spire");
            ParapetWithGaps(b, t);
            for (int s = 0; s < storeys; s++)
            {
                if (IsVault(s))
                {
                    b.Chest(t, 2, 0, s, 0);
                    b.Chest(t, 3, 3, s, Math.PI);
                    double yy = t.Y0 + s * SH;
                    _p.Sealed.Add((new Aabb(new Vec3d(t.Cx(2) + Col, yy, t.Cz(0) + Col), new Vec3d(t.Cx(mx), yy + WH, t.Cz(2))),
                                   CellCentre(t, 2, 0, s), $"Old Spire vault, floor {s} south"));
                    _p.Sealed.Add((new Aabb(new Vec3d(t.Cx(2) + Col, yy, t.Cz(2) + Col), new Vec3d(t.Cx(mx), yy + WH, t.Cz(mz))),
                                   CellCentre(t, 3, 3, s), $"Old Spire vault, floor {s} north"));
                }
                else if (s == 0) b.Chest(t, 3, 0, s, Math.PI);
                else if (s % 2 == 1) b.Chest(t, s % 4 == 1 ? 2 : 3, s % 4 == 1 ? 3 : 0, s, Math.PI);
            }
            b.Chest(t, 3, 3, storeys, Math.PI);
            b.Chest(t, 2, 0, storeys, 0);
            b.Item(Item.Launcher, 6, CellCentre(t, 3, 1, storeys));
            _p.Towers.Add(t); _p.TowerNames.Add("Old Spire");
            return t;
        }

        public static bool IsVault(int storey) => storey == 4 || storey == 8;

        private static Vec3d CellCentre(GreyboxBlock.Building t, int i, int j, int storey) =>
            new Vec3d(t.Cx(i) + Col + 3, t.Y0 + storey * SH, t.Cz(j) + Col + 3);

        // ================================================================ The lower town

        private void LowerTown(BrBuild b, Quarter q)
        {
            var boxes = new List<Aabb>();
            var towerSlots = new Dictionary<char, Rect2>();
            int qi = Array.IndexOf(Quarters, q);
            for (int j = 0; j < 5; j++)
                for (int i = 0; i < 5; i++)
                {
                    char k = q.Plan[j][i];
                    if (k == '.') continue;
                    Rect2 r = SlotRect(q.Sx, q.Sz, i, j);
                    double y = OnSteps(q.Sx, q.Sz, i, j) ? StepLevel : TownLevel;
                    if (k == 'A' || k == 'B' || k == 'E') { towerSlots[k] = r; continue; }
                    if (k == 'D') { RampPlaza(b, q, r, y); continue; }
                    b.Style = (i + 2 * j) % 3 == 0 ? q.Second : q.Main;
                    b.Variant = 100 + i + 5 * j + 25 * qi;
                    boxes.AddRange(Block(b, k, r.X0, y, r.Z0, (int)(BrKit.Hash(i, j, (uint)(40 + qi)) & 0x7fffffff)));
                }
            boxes.AddRange(Cluster(b, qi, q, towerSlots));
            // The quarter's middle: the street corner between slots 2 and 3 both ways.
            double c = BrLayout.MapCentre, m = SlotStart + 3 * SlotPitch - 2.5;
            var at = new Vec3d(c + q.Sx * m, OnSteps(q.Sx, q.Sz, 3, 3) ? StepLevel : TownLevel, c + q.Sz * m);
            b.Poi(q.Name, at, boxes.ToArray());
            _p.Places.Add((at, q.Name));
        }

        /// <summary>The plaza a pinwheel ramp comes down into: planters along its two sides, clear of the ramp.</summary>
        private static void RampPlaza(BrBuild b, Quarter q, Rect2 r, double y)
        {
            b.Style = BrStyle.Stone; b.Variant = 2;
            if (q.RampAlongX)
            {
                b.Strip(new Vec3d(r.X0 + 4, y, r.Z0 + 1.5), 6, true, 1);
                b.Strip(new Vec3d(r.X1 - 10, y, r.Z1 - 1.8), 6, true, 1);
            }
            else
            {
                b.Strip(new Vec3d(r.X0 + 1.5, y, r.Z0 + 4), 6, false, 1);
                b.Strip(new Vec3d(r.X1 - 1.8, y, r.Z1 - 10), 6, false, 1);
            }
        }

        // ---------------------------------------------------------------- blocks

        /// <summary>One old-town block in the 24 m square at (x, z), level y. Returns the buildings' boxes.</summary>
        private List<Aabb> Block(BrBuild b, char kind, double x, double y, double z, int h)
        {
            int before = b.Buildings.Count, t0 = b.World.Tiles.Count, c0 = b.Chests.Count;
            switch (kind)
            {
                case 'R': // a row court: two rows of three houses facing a courtyard, one sealed room in each
                    BrKit.Terrace(b, x + 2.4, y, z + 0.3, 3, true, h % 3);
                    BrKit.Terrace(b, x + 2.4, y, z + 17.1, 3, true, (h / 3 + 1) % 3);
                    b.Strip(new Vec3d(x + 8.25, y, z + 11.85), 7.5, true, 1);
                    break;
                case 'Z': // the same along z, doors east
                    BrKit.Terrace(b, x + 0.3, y, z + 2.4, 3, false, h % 3);
                    BrKit.Terrace(b, x + 17.1, y, z + 2.4, 3, false, (h / 3 + 1) % 3);
                    b.Strip(new Vec3d(x + 11.85, y, z + 8.25), 7.5, false, 1);
                    break;
                case 'H': TownHouse(b, x + 2.4, y, z + 2.4, 3, h); break;
                case 'h': TownHouse(b, x + 2.4, y, z + 2.4, 2, h); break;
                case 'C': BrKit.Compound(b, x + 0.75, y, z + 0.75); break;
                case 'G': GuildHall(b, x + 2.4, y, z + 2.4, h); break;
                case 'W': WarehouseYard(b, x, y, z, h); break;
                case 'S': ShedYard(b, x, y, z); break;
                case 'U': Ruin(b, x, y, z, h); break;
                case 'P': Plaza(b, x, y, z); break;
                default: throw new ArgumentException($"no block kind '{kind}'");
            }
            _p.Add("block " + kind, b.World.Tiles.Count - t0, b.Chests.Count - c0);
            var list = new List<Aabb>();
            for (int k = before; k < b.Buildings.Count; k++) list.Add(b.Buildings[k].Bounds);
            return list;
        }

        /// <summary>
        /// A town house, 3 x 3 modules, 2 or 3 storeys: stairs up its west lane to a flat roof with a parapet, two doors,
        /// windows on the outside, and on the top floor one corner room sealed off (a chest behind solid walls).
        /// </summary>
        private void TownHouse(BrBuild b, double x, double y, double z, int storeys, int h)
        {
            var t = new GreyboxBlock.Building(x, z, 3, 3, storeys) { Y0 = y };
            b.Stairwell(t, 0, true, +1);
            int top = storeys - 1;
            for (int s = 0; s < storeys; s++)
            {
                for (int j = 0; j < 3; j++) t.AlongZ[s, 1, j] = j == 1 ? WallKind.Wall : WallKind.Door;
                t.AlongZ[s, 2, 0] = WallKind.Wall;
                t.AlongZ[s, 2, 1] = WallKind.Door;
                if (s == 0)
                {
                    t.AlongX[0, 0, 1] = WallKind.ShutDoor;
                    t.AlongX[0, 0, 2] = WallKind.Window;
                    t.AlongZ[0, 3, h % 2 == 0 ? 1 : 0] = WallKind.ShutDoor;
                    t.AlongX[0, 3, 1] = WallKind.Window;
                    continue;
                }
                t.AlongX[s, 0, 1] = WallKind.Window;
                t.AlongX[s, 0, 2] = (s + h) % 2 == 0 ? WallKind.Window : WallKind.Wall;
                t.AlongZ[s, 3, 0] = WallKind.Window;
                t.AlongZ[s, 3, 1] = (s + h) % 2 == 1 ? WallKind.Window : WallKind.Wall;
                t.AlongX[s, 3, 1] = WallKind.Window;
            }
            // The sealed corner room: cell (2, 2) on the top floor.
            t.AlongZ[top, 2, 2] = WallKind.Wall;
            t.AlongX[top, 2, 2] = WallKind.Wall;
            t.AlongX[top, 3, 2] = WallKind.Wall;
            t.AlongZ[top, 3, 2] = WallKind.Wall;
            b.Raise(t, storeys == 3 ? "Town house" : "Old house");
            ParapetWithGaps(b, t);
            b.Chest(t, 2, 0, 0, 0);
            b.Chest(t, 1, 2, 1, Math.PI);
            b.Chest(t, 2, 2, top, Math.PI);
            double yy = y + top * SH;
            _p.Sealed.Add((new Aabb(new Vec3d(t.Cx(2) + Col, yy, t.Cz(2) + Col), new Vec3d(t.Cx(3), yy + WH, t.Cz(3))), CellCentre(t, 2, 2, top), "town house top room"));
        }

        /// <summary>
        /// A guild hall, 3 x 3 modules, two storeys with no stairs: a hall with a wide door, two sealed storerooms along its
        /// back, and a sealed loft above (break up through the ceiling or in through the walls).
        /// </summary>
        private void GuildHall(BrBuild b, double x, double y, double z, int h)
        {
            var t = new GreyboxBlock.Building(x, z, 3, 3, 2) { Y0 = y };
            t.AlongX[0, 0, 1] = WallKind.WideDoor;
            t.AlongZ[0, h % 2 == 0 ? 3 : 0, 0] = WallKind.ShutDoor;
            t.AlongX[0, 2, 0] = WallKind.Door;
            t.AlongX[0, 2, 1] = WallKind.Wall;
            t.AlongX[0, 2, 2] = WallKind.Wall;
            t.AlongZ[0, 1, 2] = WallKind.Wall;
            t.AlongZ[0, 2, 2] = WallKind.Wall;
            b.Raise(t, "Guild hall");
            b.Chest(t, 0, 2, 0, Math.PI);
            b.Chest(t, 1, 2, 0, Math.PI);
            b.Chest(t, 2, 2, 0, Math.PI);
            b.Chest(t, 1, 1, 1, 0);
            _p.Sealed.Add((new Aabb(new Vec3d(t.Cx(1) + Col, y, t.Cz(2) + Col), new Vec3d(t.Cx(2), y + WH, t.Cz(3))), CellCentre(t, 1, 2, 0), "guild hall storeroom"));
            _p.Sealed.Add((new Aabb(new Vec3d(t.Cx(2) + Col, y, t.Cz(2) + Col), new Vec3d(t.Cx(3), y + WH, t.Cz(3))), CellCentre(t, 2, 2, 0), "guild hall storeroom"));
            _p.Sealed.Add((new Aabb(new Vec3d(t.Cx(0) + Col, y + SH, t.Cz(0) + Col), new Vec3d(t.Cx(3), y + SH + WH, t.Cz(3))), CellCentre(t, 1, 1, 1), "guild hall loft"));
        }

        /// <summary>A warehouse with a yard: a long one-storey store (a wide door at its west end, a shut door into the
        /// yard, two sealed bays) and a yard behind walls too tall to climb, one gate.</summary>
        private void WarehouseYard(BrBuild b, double x, double y, double z, int h)
        {
            var t = new GreyboxBlock.Building(x + 2.4, z + 0.3, 3, 2, 1) { Y0 = y };
            t.AlongZ[0, 0, 0] = WallKind.WideDoor;
            t.AlongX[0, 2, 0] = WallKind.ShutDoor;
            t.AlongZ[0, 2, 0] = WallKind.Wall;
            t.AlongZ[0, 2, 1] = WallKind.Wall;
            t.AlongX[0, 1, 2] = WallKind.Wall;
            b.Raise(t, "Warehouse");
            b.Chest(t, 0, 1, 0, Math.PI);
            b.Chest(t, 2, 0, 0, Math.PI);
            b.Chest(t, 2, 1, 0, Math.PI);
            _p.Sealed.Add((new Aabb(new Vec3d(t.Cx(2) + Col, y, t.Cz(0) + Col), new Vec3d(t.Cx(3), y + WH, t.Cz(1))), CellCentre(t, 2, 0, 0), "warehouse bay"));
            _p.Sealed.Add((new Aabb(new Vec3d(t.Cx(2) + Col, y, t.Cz(1) + Col), new Vec3d(t.Cx(3), y + WH, t.Cz(2))), CellCentre(t, 2, 1, 0), "warehouse bay"));
            // The yard: three tiles of wall round the north part, a 4 m gate in its south side.
            double yz = z + 14.6, top = z + Slot - Col;
            b.Strip(new Vec3d(x + Col, y, top), Slot - 2 * Col, true, 3);
            b.Strip(new Vec3d(x + Col, y, yz), top - yz, false, 3);
            b.Strip(new Vec3d(x + Slot - 2 * Col, y, yz), top - yz, false, 3);
            b.Strip(new Vec3d(x + 2 * Col, y, yz), 9, true, 3);
            b.Strip(new Vec3d(x + 13.6, y, yz), Slot - 2 * Col - 13.6, true, 3);
            b.Chest(new Vec3d(x + 6, y, z + 19.5), h % 2 == 0 ? 0 : Math.PI);
        }

        /// <summary>Two sealed sheds and a yard of low walls.</summary>
        private void ShedYard(BrBuild b, double x, double y, double z)
        {
            BrKit.Shed(b, x + 2.4, y, z + 2.4);
            BrKit.Shed(b, x + 15, y, z + 15);
            b.Strip(new Vec3d(x + 13, y, z + 4), 7.5, true, 1);
            b.Strip(new Vec3d(x + 3, y, z + 14), 7.5, false, 2);
            b.Strip(new Vec3d(x + 6, y, z + 21.5), 6, true, 1);
            foreach (double o in new[] { 2.4, 15.0 })
                _p.Sealed.Add((new Aabb(new Vec3d(x + o + Col, y, z + o + Col), new Vec3d(x + o + M, y + WH, z + o + M)), new Vec3d(x + o + Col + 3, y, z + o + Col + 3), "shed"));
        }

        /// <summary>A ruined house: broken walls two and three tiles tall in L shapes, a chest in its corner.</summary>
        private static void Ruin(BrBuild b, double x, double y, double z, int h)
        {
            BrStyle was = b.Style;
            b.Style = BrStyle.Ruin;
            b.Strip(new Vec3d(x + 3, y, z + 3), 12, true, 2);
            b.Strip(new Vec3d(x + 3, y, z + 3 + Col), 9, false, 3);
            b.Strip(new Vec3d(x + 12, y, z + 15), 9, true, 2);
            b.Strip(new Vec3d(x + 20.7, y, z + 9), 6, false, 2);
            b.Strip(new Vec3d(x + 8, y, z + 9), 4.5, true, 1);
            b.Chest(new Vec3d(x + 5.5, y, z + 5.5), h % 2 == 0 ? Math.PI / 2 : 0);
            b.Style = was;
        }

        /// <summary>A small plaza: four planters (one-tile walls) round a middle you can cross, a grenade stack.</summary>
        private static void Plaza(BrBuild b, double x, double y, double z)
        {
            BrStyle was = b.Style;
            b.Style = BrStyle.Stone;
            b.Strip(new Vec3d(x + 3, y, z + 3), 6, true, 1);
            b.Strip(new Vec3d(x + 15, y, z + 20.7), 6, true, 1);
            b.Strip(new Vec3d(x + 3, y, z + 15), 6, false, 1);
            b.Strip(new Vec3d(x + 20.7, y, z + 3), 6, false, 1);
            b.Strip(new Vec3d(x + 10.5, y, z + 11.85), 3, true, 1);
            b.Item(Item.Grenade, 2, new Vec3d(x + 12, y, z + 14));
            b.Style = was;
        }

        // ---------------------------------------------------------------- towers

        /// <summary>
        /// A tower, 3 x 3 modules, stairs up its west lane to a flat roof. Ground floor: a wide door south, a shut door north.
        /// Upper floors: two rooms off the stairs, windows on the south and east only; the floors in
        /// <paramref name="sealedFloors"/> have a sealed strong room (the east room: no door, no window; the west room is a bare corridor between the flights). The caller adds bridge doors
        /// before raising.
        /// </summary>
        private static GreyboxBlock.Building TowerPlan(BrBuild b, double x, double y, double z, int storeys, int[] sealedFloors)
        {
            const int mx = 3, mz = 3;
            var t = new GreyboxBlock.Building(x, z, mx, mz, storeys) { Y0 = y };
            b.Stairwell(t, 0, true, +1);
            t.AlongX[0, 0, 2] = WallKind.WideDoor;
            t.AlongX[0, mz, 1] = WallKind.ShutDoor;
            t.AlongZ[0, mx, 1] = WallKind.Window;
            for (int s = 0; s < storeys; s++)
            {
                bool sealedFloor = Array.IndexOf(sealedFloors, s) >= 0;
                // Doors off the stairs where they arrive and start: the way from one flight to the next runs through the
                // west room, so a sealed floor seals only its east room (the west one is a bare corridor).
                for (int j = 0; j < mz; j++) t.AlongZ[s, 1, j] = j == 1 ? WallKind.Wall : WallKind.Door;
                if (s == 0) continue;
                t.AlongZ[s, 2, 0] = WallKind.Wall;
                t.AlongZ[s, 2, 1] = sealedFloor ? WallKind.Wall : WallKind.Door;
                t.AlongZ[s, 2, 2] = WallKind.Wall;
                if (sealedFloor) continue;
                for (int i = 1; i < mx; i++) t.AlongX[s, 0, i] = (i + s) % 2 == 0 ? WallKind.Window : WallKind.Wall;
                for (int j = 0; j < mz; j++) t.AlongZ[s, mx, j] = (j + s) % 2 == 0 ? WallKind.Window : WallKind.Wall;
            }
            return t;
        }

        private void RaiseTower(BrBuild b, GreyboxBlock.Building t, string name, int[] sealedFloors, params (int side, int seg)[] roofGaps)
        {
            int t0 = b.World.Tiles.Count;
            b.Raise(t, name);
            ParapetWithGaps(b, t, roofGaps);
            for (int s = 0; s < t.Storeys; s++)
            {
                if (Array.IndexOf(sealedFloors, s) >= 0)
                {
                    b.Chest(t, 2, 0, s, 0);
                    b.Chest(t, 2, 2, s, Math.PI);
                    double yy = t.Y0 + s * SH;
                    _p.Sealed.Add((new Aabb(new Vec3d(t.Cx(2) + Col, yy, t.Cz(0) + Col), new Vec3d(t.Cx(3), yy + WH, t.Cz(3))), CellCentre(t, 2, 0, s), $"{name} floor {s} strong room"));
                }
                else if (s % 3 == 0) b.Chest(t, 2, s % 2 == 0 ? 0 : 2, s, Math.PI);
            }
            b.Chest(t, 2, 2, t.Storeys, Math.PI);
            _p.Towers.Add(t); _p.TowerNames.Add(name);
            _p.Add($"tower {t.Storeys}", b.World.Tiles.Count - t0, 0);
        }

        /// <summary>A 3 x 3 tower sits this far in from its slot's sides.</summary>
        private static double Inset => (Slot - (3 * M + Col)) / 2;

        /// <summary>
        /// The quarter's towers: Bell Quarter twins (7 + 7), Tanners' watch tower (10), Scholars' twins (6 + 6), Merchant
        /// Steps' skyway (6, 8, 10). Towers of a group stand a street apart along z, joined by bridges.
        /// </summary>
        private List<Aabb> Cluster(BrBuild b, int qi, Quarter q, Dictionary<char, Rect2> slots)
        {
            var made = new List<Aabb>();
            GreyboxBlock.Building Make(char k, int storeys, int[] sealedFloors)
            {
                Rect2 r = slots[k];
                double y = q.Sx > 0 && q.Sz < 0 ? StepLevel : TownLevel;
                return TowerPlan(b, r.X0 + Inset, y, r.Z0 + Inset, storeys, sealedFloors);
            }
            const int S = 0, N = 1;
            switch (qi)
            {
                case 0: // Bell Quarter (north east): twin 7-storey bell towers, a sky bridge on floor 4 and a roof bridge. A is south.
                {
                    int[] sA = { 3 }, sB = { 5 };
                    GreyboxBlock.Building a = Make('A', 7, sA), bt = Make('B', 7, sB);
                    a.AlongX[4, 3, 1] = WallKind.Door; bt.AlongX[4, 0, 1] = WallKind.Door;
                    b.Style = BrStyle.Stone; b.Variant = 20;
                    RaiseTower(b, a, "Bell tower", sA, (N, 1));
                    b.Variant = 21;
                    RaiseTower(b, bt, "Bell tower", sB, (S, 1));
                    BridgeZ(b, a, bt, 4); BridgeZ(b, a, bt, 7);
                    made.Add(a.Bounds); made.Add(bt.Bounds);
                    b.Item(Item.C4, 2, CellCentre(a, 1, 1, 7));
                    break;
                }
                case 1: // Tanners' Row (north west): the watch tower.
                {
                    int[] sA = { 3, 7 };
                    GreyboxBlock.Building a = Make('A', 10, sA);
                    b.Style = BrStyle.Brick; b.Variant = 22;
                    RaiseTower(b, a, "Watch tower", sA);
                    made.Add(a.Bounds);
                    b.Item(Item.Launcher, 6, CellCentre(a, 1, 1, 10));
                    break;
                }
                case 2: // Scholars' Close (south west): twin library towers, a roof bridge and one on floor 2. A is north.
                {
                    int[] sA = { 4 }, sB = { 3 };
                    GreyboxBlock.Building a = Make('A', 6, sA), bt = Make('B', 6, sB);
                    bt.AlongX[2, 3, 1] = WallKind.Door; a.AlongX[2, 0, 1] = WallKind.Door;
                    b.Style = BrStyle.Plaster; b.Variant = 23;
                    RaiseTower(b, a, "Library tower", sA, (S, 1));
                    b.Variant = 24;
                    RaiseTower(b, bt, "Library tower", sB, (N, 1));
                    BridgeZ(b, bt, a, 2); BridgeZ(b, bt, a, 6);
                    made.Add(a.Bounds); made.Add(bt.Bounds);
                    break;
                }
                default: // Merchant Steps (south east): a skyway that steps up 6 -> 8 -> 10 going south. A is north.
                {
                    int[] sA = { 3 }, sB = { 5 }, sE = { 3, 6 };
                    GreyboxBlock.Building a = Make('A', 6, sA), bt = Make('B', 8, sB), e = Make('E', 10, sE);
                    bt.AlongX[6, 3, 1] = WallKind.Door;              // A's roof -> B's floor 6
                    e.AlongX[8, 3, 1] = WallKind.Door;               // B's roof -> E's floor 8
                    a.AlongX[2, 0, 1] = WallKind.Door; bt.AlongX[2, 3, 1] = WallKind.Door; // floor 2, A to B
                    b.Style = BrStyle.Painted; b.Variant = 25;
                    RaiseTower(b, a, "Counting house", sA, (S, 1));
                    b.Style = BrStyle.Plaster; b.Variant = 26;
                    RaiseTower(b, bt, "Exchange tower", sB, (S, 1));
                    b.Style = BrStyle.Stone; b.Variant = 27;
                    RaiseTower(b, e, "Merchant tower", sE);
                    BridgeZ(b, bt, a, 6); BridgeZ(b, e, bt, 8); BridgeZ(b, bt, a, 2);
                    made.Add(a.Bounds); made.Add(bt.Bounds); made.Add(e.Bounds);
                    b.Item(Item.C4, 2, CellCentre(e, 1, 1, 10));
                    break;
                }
            }
            return made;
        }

        /// <summary>
        /// A bridge from <paramref name="south"/>'s north wall to <paramref name="north"/>'s south wall (they stand a street
        /// apart along z on one level), its deck level with floor <paramref name="storey"/> of the south one (its roof when
        /// that is its storey count), 3 m wide under the middle module, a one-tile rail each side.
        /// </summary>
        private void BridgeZ(BrBuild b, GreyboxBlock.Building south, GreyboxBlock.Building north, int storey)
        {
            double top = south.Y0 + storey * SH;
            double x0 = south.Cx(1) + Col + 1.5, x1 = x0 + 3;
            double z0 = south.Bounds.Max.Z, z1 = north.Z0;
            Deck(b, x0, z0, x1, z1, top);
            BrStyle was = b.Style;
            b.Style = BrStyle.Steel;
            b.Strip(new Vec3d(x0, top, z0), z1 - z0, false, 1);
            b.Strip(new Vec3d(x1 - Col, top, z0), z1 - z0, false, 1);
            b.Style = was;
            _p.Bridges.Add(new Aabb(new Vec3d(x0 + Col, top, z0), new Vec3d(x1 - Col, top + 2, z1)));
        }

        /// <summary>A deck of floor tiles (about 1.5 m on a side) with its top at <paramref name="top"/> (like the greybox
        /// block's rooftop bridges, which aren't open to segments).</summary>
        private static void Deck(BrBuild b, double x0, double z0, double x1, double z1, double top)
        {
            BrushWorld w = b.World;
            double s = w.Tiles.Config.TileSize, sx = x1 - x0, sz = z1 - z0;
            int nx = (int)Math.Ceiling(sx / s - 1e-9), nz = (int)Math.Ceiling(sz / s - 1e-9);
            for (int a = 0; a < nx; a++)
                for (int k = 0; k < nz; k++)
                {
                    var t = new Aabb(new Vec3d(x0 + sx * a / nx, top - Col, z0 + sz * k / nz), new Vec3d(x0 + sx * (a + 1) / nx, top, z0 + sz * (k + 1) / nz));
                    int id = w.Tiles.AddTile(t, Dir.Up, TileKind.Floor);
                    b.Info.Tiles.Add(id);
                    b.Info.Floors.Add(id);
                }
            b.Info.Bridges.Add(new Aabb(new Vec3d(x0, top - Col, z0), new Vec3d(x1, top, z1)));
        }

        /// <summary>
        /// A one-tile parapet round a flat roof, with 3 m gaps (side 0 south, 1 north, 2 west, 3 east; under the middle of
        /// module <c>seg</c>) where bridges arrive.
        /// </summary>
        private static void ParapetWithGaps(BrBuild b, GreyboxBlock.Building t, params (int side, int seg)[] gaps)
        {
            double y = t.Top, x0 = t.X0, z0 = t.Z0, x1 = t.Cx(t.Mx) + Col, z1 = t.Cz(t.Mz) + Col;
            void Run(bool alongX, double fixedC, double from, double to, int side)
            {
                var cuts = new List<(double, double)>();
                foreach (var g in gaps)
                    if (g.side == side)
                    {
                        double a = (alongX ? t.Cx(g.seg) : t.Cz(g.seg)) + Col + 1.5;
                        cuts.Add((a, a + 3));
                    }
                cuts.Sort();
                double at = from;
                foreach (var (a, e) in cuts)
                {
                    if (a - at > 1e-6) b.Strip(alongX ? new Vec3d(at, y, fixedC) : new Vec3d(fixedC, y, at), a - at, alongX, 1);
                    at = e;
                }
                if (to - at > 1e-6) b.Strip(alongX ? new Vec3d(at, y, fixedC) : new Vec3d(fixedC, y, at), to - at, alongX, 1);
            }
            Run(true, z0, x0, x1, 0);
            Run(true, z1 - Col, x0, x1, 1);
            Run(false, x0, z0 + Col, z1 - Col, 2);
            Run(false, x1 - Col, z0 + Col, z1 - Col, 3);
        }

        // ================================================================ The city wall and the gardens

        /// <summary>Gates (12 m, one per main road) and breaches (4.5 m, two a side, on street lines) in the city wall, by
        /// side (0 south, 1 north, 2 west, 3 east) as offsets along it from the map's centre.</summary>
        public static readonly double[][] WallOpenings =
        {
            new[] { 0.0, 67.5, -125.5 }, new[] { 0.0, -67.5, 125.5 }, new[] { 0.0, 125.5, -67.5 }, new[] { 0.0, -125.5, 67.5 },
        };
        public const double GateHalf = 6, BreachHalf = 2.25;

        /// <summary>
        /// The city wall: three tiles (4.5 m) tall, too tall to climb, round the town at <see cref="WallHalf"/>; a gate on
        /// each main road and two breaches a side, so it slows a push into the town without walling it off.
        /// </summary>
        private void CityWall(BrBuild b, double c)
        {
            b.Style = BrStyle.Stone; b.Variant = 3;
            double h = WallHalf;
            for (int side = 0; side < 4; side++)
            {
                bool alongX = side < 2;
                double fixedC = side == 0 || side == 2 ? c - h - Col : c + h;
                double from = alongX ? -h - Col : -h, to = alongX ? h + Col : h;
                var cuts = new List<(double, double)>();
                foreach (double m in WallOpenings[side])
                {
                    double half = m == 0 ? GateHalf : BreachHalf;
                    cuts.Add((m - half, m + half));
                }
                cuts.Sort();
                double at = from;
                foreach (var (a, e) in cuts)
                {
                    WallRun(b, alongX, fixedC, c + at, a - at);
                    at = e;
                }
                WallRun(b, alongX, fixedC, c + at, to - at);
            }
            b.Poi("City wall", new Vec3d(c + 30, TownLevel, c - h - 4));
        }

        private static void WallRun(BrBuild b, bool alongX, double fixedC, double start, double length)
        {
            if (length < 1e-6) return;
            b.Strip(alongX ? new Vec3d(start, TownLevel, fixedC) : new Vec3d(fixedC, TownLevel, start), length, alongX, 3);
        }

        /// <summary>
        /// Outside the wall: a walled graveyard with a sealed tomb in each corner, then allotments along every side
        /// (walled plots with sealed sheds, gardeners' huts, cold frames, open plots the scatter plants as orchards).
        /// </summary>
        private void Gardens(BrBuild b, double c)
        {
            var boxes = new List<Aabb>();
            foreach (int sx in new[] { -1, 1 })
                foreach (int sz in new[] { -1, 1 })
                {
                    double gx = sx > 0 ? c + 172 : c - 172 - 24, gz = sz > 0 ? c + 172 : c - 172 - 24;
                    var r = new Rect2(gx, gz, gx + 24, gz + 24);
                    if (!b.IsClear(r, 2) || !b.TryFoot(r.X0, r.Z0, r.X1, r.Z1, 1.5, out double y)) continue;
                    b.Style = BrStyle.Stone; b.Variant = 40;
                    // The wall: two tiles, a 6 m gate facing the town.
                    bool gateNorth = sz < 0;
                    double gateZ = gateNorth ? gz + 24 - Col : gz;
                    b.Strip(new Vec3d(gx, y, gateNorth ? gz : gz + 24 - Col), 24, true, 2);
                    b.Strip(new Vec3d(gx, y, gateZ), 9, true, 2);
                    b.Strip(new Vec3d(gx + 15, y, gateZ), 9, true, 2);
                    b.Strip(new Vec3d(gx, y, gz + Col), 24 - 2 * Col, false, 2);
                    b.Strip(new Vec3d(gx + 24 - Col, y, gz + Col), 24 - 2 * Col, false, 2);
                    for (int row = 0; row < 3; row++) b.Strip(new Vec3d(gx + 3, y, gz + 5 + row * 5), 6, true, 1);
                    var tomb = new GreyboxBlock.Building(gx + 14, gz + 8.7, 1, 1, 1) { Y0 = y };
                    b.Raise(tomb, "Tomb");
                    b.Chest(tomb, 0, 0, 0, 0);
                    _p.Sealed.Add((new Aabb(new Vec3d(tomb.X0 + Col, y, tomb.Z0 + Col), new Vec3d(tomb.X0 + M, y + WH, tomb.Z0 + M)), CellCentre(tomb, 0, 0, 0), "tomb"));
                    boxes.Add(tomb.Bounds);
                }
            // Plots: 16 m x 12 m, two rows staggered along each side, 172..197 from the centre.
            for (int side = 0; side < 4; side++)
                for (int k = 0; k < 16; k++)
                {
                    double t = -170 + k * 22;            // along the side
                    double n = 172 + (k % 2) * 13;        // out from the centre
                    bool alongX = side < 2;
                    double u, v, w, d;
                    if (alongX) { u = t; v = side == 1 ? n : -n - 12; w = 16; d = 12; }
                    else { v = t; u = side == 3 ? n : -n - 12; w = 12; d = 16; }
                    if (Math.Abs(t + 8) < 16) continue; // keep the road out of the gate clear
                    var r = new Rect2(c + u, c + v, c + u + w, c + v + d);
                    uint hsh = BrKit.Hash(side, k, 77);
                    if (!b.IsClear(r, 2.5)) continue;
                    if (hsh % 4 == 3)
                    {
                        b.Open(new Rect2(r.X0 - 1, r.Z0 - 1, r.X1 + 1, r.Z1 + 1)); // reserved for nothing: an orchard gap
                        continue;
                    }
                    if (!b.TryFoot(r.X0, r.Z0, r.X1, r.Z1, 1.5, out double y)) continue;
                    b.Style = hsh % 3 == 0 ? BrStyle.Timber : BrStyle.Stone; b.Variant = 30 + (int)(hsh % 4);
                    switch (hsh % 4)
                    {
                        case 0: // a walled plot with a sealed shed
                            b.Strip(new Vec3d(r.X0, y, r.Z0), w, true, 1);
                            b.Strip(new Vec3d(r.X0, y, r.Z1 - Col), w * 0.4, true, 1);
                            b.Strip(new Vec3d(r.X0, y, r.Z0 + Col), d - 2 * Col, false, 1);
                            BrKit.Shed(b, r.X1 - 7.2, y, r.Z1 - 7.2);
                            _p.Sealed.Add((new Aabb(new Vec3d(r.X1 - 7.2 + Col, y, r.Z1 - 7.2 + Col), new Vec3d(r.X1 - 7.2 + M, y + WH, r.Z1 - 7.2 + M)),
                                           new Vec3d(r.X1 - 7.2 + Col + 3, y, r.Z1 - 7.2 + Col + 3), "garden shed"));
                            break;
                        case 1: // a gardener's hut (a door) and a plot wall
                        {
                            var hut = new GreyboxBlock.Building(r.X0 + 1, r.Z0 + 1, 1, 1, 1) { Y0 = y };
                            hut.AlongX[0, 0, 0] = WallKind.ShutDoor;
                            b.Raise(hut, "Gardener's hut");
                            b.Chest(hut, 0, 0, 0, 0);
                            b.Strip(alongX ? new Vec3d(r.X0 + 9, y, r.Z0 + 2) : new Vec3d(r.X0 + 9, y, r.Z0 + 9), 6, alongX, 1);
                            break;
                        }
                        default: // cold frames: two rows of low walls
                            b.Strip(new Vec3d(r.X0 + 1, y, r.Z0 + 3), w - 2, true, 1);
                            b.Strip(new Vec3d(r.X0 + 1, y, r.Z0 + 8), w - 2, true, 1);
                            break;
                    }
                }
            b.Poi("Outer Gardens", new Vec3d(c + 190, TownLevel, c + 60), boxes.ToArray());
        }
    }
}
