using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// One of the nine segments of the battle royale map (<see cref="BrLayout"/>). Each lives in its own file under
    /// Segments/ (and may add its own files in a folder named after it); it owns everything inside its square: its ground
    /// (<see cref="Ground"/>), its buildings, walls, chests, items, POIs and props (<see cref="Build"/>). The framework
    /// builds the spawn region (<see cref="BrSpawnKit"/>) first, then each segment in id order, all into one world.
    ///
    /// Rules (tested, see BrSegmentTests): everything built stays inside <see cref="BrLayout.Buildable"/>; the road port
    /// lanes, the pad lanes and the spawn region's plot stay clear; exactly <see cref="BrLayout.HomeChests"/> chests within
    /// <see cref="BrLayout.HomeRadius"/> of the anchor; the budgets in <see cref="BrLayout"/>; the map-wide geometry rules
    /// (no tile in anything solid, stairs with headroom, doorways clear, chests on floors, slopes in two bands).
    /// </summary>
    public abstract class BrSegment
    {
        /// <summary>The segment's id (gx + 3 gz, see <see cref="BrLayout"/>).</summary>
        public abstract int Id { get; }

        public string Name => BrLayout.Names[Id];

        /// <summary>The name of the home POI (the buildings and chests round the anchor).</summary>
        public virtual string HomePoiName => Name + " home";

        /// <summary>
        /// Natural ground (metres, absolute) at a map position inside this segment's square, before the seams, the home
        /// plaza, the approach and the rim are applied (<see cref="BrGround.Natural"/>). Pure and thread-safe. Start from
        /// <see cref="BrGround.Shared"/> so the ground meets the seams gently.
        /// </summary>
        public virtual double Ground(double x, double z) => BrGround.Shared(x, z);

        /// <summary>Builds the segment's content. Use <paramref name="b"/> for everything (it records what belongs to whom).</summary>
        public abstract void Build(BrBuild b);
    }

    /// <summary>The nine segments, by id.</summary>
    public static class BrSegments
    {
        public static readonly BrSegment[] All =
        {
            new SouthWestSegment(), new SouthSegment(), new SouthEastSegment(),
            new WestSegment(), new CentreSegment(), new EastSegment(),
            new NorthWestSegment(), new NorthSegment(), new NorthEastSegment(),
        };
    }

    /// <summary>A wall look a building asks for (for the graphics pass: it paints by this; no effect on play).</summary>
    public enum BrStyle : byte { Brick, Plaster, Concrete, Timber, Stone, Steel, Glass, Painted, Industrial, Ruin }

    /// <summary>A building on the battle royale map, with the segment that built it and its look.</summary>
    public struct BrBuilding
    {
        public Aabb Bounds;
        public int Segment;
        public string Name;
        public BrStyle Style;
        /// <summary>A free number for the graphics pass (a colour or a decoration variant; 0 if unused).</summary>
        public int Variant;
    }

    /// <summary>A prop a segment asks for at a spot (<see cref="BrBuild.Tree"/> and friends).</summary>
    public struct PropRequest
    {
        public BrNature.Shape Shape;
        public double X, Z;
        public int Seed;
        public int Segment;
    }

    /// <summary>How many props the framework scatters over a segment's open ground (on top of the ones it asked for).</summary>
    public sealed class NatureFill
    {
        public int Groves = 4, LoneTrees = 10, RockGroups = 10, Bushes = 16;
    }

    /// <summary>
    /// What a segment builds with. Every helper records what it adds, so tests can check each segment stays in its square,
    /// on its budgets and clear of the lanes. Wraps the greybox builder (<see cref="GreyboxBlock"/>) and the shared kit
    /// (<see cref="BrKit"/>); segments may also call <see cref="GreyboxBlock"/> directly with <see cref="World"/> and
    /// <see cref="Info"/> (the tests read the world, not the records, for geometry).
    /// </summary>
    public sealed class BrBuild
    {
        public readonly int Id;
        public readonly BrushWorld World;
        public readonly GreyboxInfo Info;
        internal readonly Footings Footings;
        /// <summary>The segment's square, and the part it may build in.</summary>
        public readonly Rect2 Bounds, Area;
        /// <summary>The home anchor (map centre for the centre segment).</summary>
        public readonly Vec3d Anchor;
        public string Name => BrLayout.Names[Id];
        /// <summary>The look given to buildings raised from now on (<see cref="Raise"/>).</summary>
        public BrStyle Style = BrStyle.Brick;
        public int Variant;
        public readonly NatureFill Nature = new NatureFill();
        /// <summary>What this segment added to the world (set by the framework round <see cref="BrSegment.Build"/>): tile
        /// ids, brush indices, footings and buildings in [First, End).</summary>
        public int TileFirst, TileEnd, BrushFirst, BrushEnd, FootingFirst, FootingEnd, BuildingFirst, BuildingEnd;

        // ---- Records.
        public readonly List<BrChestSpot> Chests = new List<BrChestSpot>();
        public readonly List<GroundItem> Items = new List<GroundItem>();
        public readonly List<BrPoi> Pois = new List<BrPoi>();
        public readonly List<BrBuilding> Buildings = new List<BrBuilding>();
        /// <summary>Areas the prop scatter stays out of (plazas, roads, yards).</summary>
        public readonly List<Rect2> KeepOpen = new List<Rect2>();
        public readonly List<PropRequest> Props = new List<PropRequest>();
        /// <summary>Areas that must stay clear (the spawn plot, pad lanes, port lanes): <see cref="IsClear"/> checks them.</summary>
        public readonly List<Rect2> Reserved = new List<Rect2>();
        /// <summary>Lanes that must stay clear (the spawn region's pad lanes): from, to, half width.</summary>
        public readonly List<(Vec3d From, Vec3d To, double Half)> Lanes = new List<(Vec3d, Vec3d, double)>();
        /// <summary>Spots the prop scatter keeps off (chests, items, pads): filled by the framework.</summary>
        internal readonly List<Vec3d> KeepClearSpots = new List<Vec3d>();

        internal BrBuild(int id, BrushWorld world, GreyboxInfo info, Footings footings)
        {
            Id = id;
            World = world;
            Info = info;
            Footings = footings;
            Bounds = BrLayout.Bounds(id);
            Area = BrLayout.Buildable(id);
            Anchor = BrLayout.Anchor(id);
        }

        private readonly Dictionary<Type, object> _notes = new Dictionary<Type, object>();

        /// <summary>
        /// A segment's own record of this build (what its tests check: sealed rooms, high spots, towers...), one per build
        /// and type. Keep build-time state here, never in a static or a field of the segment: the server's and the
        /// client's copies of the map are built on two threads at once, and the segment objects are shared.
        /// </summary>
        public T Notes<T>() where T : class, new()
        {
            if (!_notes.TryGetValue(typeof(T), out object o)) _notes[typeof(T)] = o = new T();
            return (T)o;
        }

        /// <summary>Natural ground (no footings) at a map position.</summary>
        public double Natural(double x, double z) => BrGround.Natural(x, z);

        /// <summary>The mean natural ground over a rectangle (3 m samples, edges included).</summary>
        public double MeanNatural(double x0, double z0, double x1, double z1)
        {
            int nx = Math.Max(1, (int)Math.Ceiling((x1 - x0) / 3)), nz = Math.Max(1, (int)Math.Ceiling((z1 - z0) / 3));
            double sum = 0;
            for (int j = 0; j <= nz; j++)
                for (int i = 0; i <= nx; i++) sum += Natural(x0 + (x1 - x0) * i / nx, z0 + (z1 - z0) * j / nz);
            return sum / ((nx + 1) * (nz + 1));
        }

        // ---------------------------------------------------------------- footings

        /// <summary>
        /// Cuts a level footing over the rectangle (plus apron) at the level nearest <paramref name="want"/> (default: the
        /// mean natural ground) that no neighbour forbids, on the 0.3 m lattice, and returns it. Throws if none fits.
        /// </summary>
        public double Foot(double x0, double z0, double x1, double z1, double apron, double want = double.NaN)
        {
            if (!TryFoot(x0, z0, x1, z1, apron, out double l, want)) throw new InvalidOperationException($"{BrLayout.Names[Id]}: no level footing fits at {x0:0.0}, {z0:0.0}.");
            return l;
        }

        /// <summary>As <see cref="Foot(double, double, double, double, double, double)"/>; false (nothing added) if no level fits.</summary>
        public bool TryFoot(double x0, double z0, double x1, double z1, double apron, out double level, double want = double.NaN)
        {
            if (double.IsNaN(want)) want = MeanNatural(x0, z0, x1, z1);
            level = Footings.PickLevel(x0, z0, x1, z1, apron, want);
            if (double.IsNaN(level)) return false;
            Footings.Add(x0, z0, x1, z1, level, apron);
            return true;
        }

        /// <summary>The levels a footing here could take (see <see cref="Footings.Allowed"/>).</summary>
        public bool FootAllowed(double x0, double z0, double x1, double z1, double apron, out double lo, out double hi) =>
            Footings.Allowed(x0, z0, x1, z1, apron, out lo, out hi);

        /// <summary>Puts a building on its own footing (its outer walls plus an apron) and returns its level.</summary>
        public double Foot(GreyboxBlock.Building b, double apron = 3, double want = double.NaN)
        {
            b.Y0 = Foot(b.X0, b.Z0, b.Cx(b.Mx) + GreyboxBlock.Column, b.Cz(b.Mz) + GreyboxBlock.Column, apron, want);
            return b.Y0;
        }

        // ---------------------------------------------------------------- building

        /// <summary>Puts up a building (walls, floors, frame) with the current <see cref="Style"/> and records it.</summary>
        public void Raise(GreyboxBlock.Building b, string name)
        {
            GreyboxBlock.Raise(World, Info, b, name);
            Buildings.Add(new BrBuilding { Bounds = b.Bounds, Segment = Id, Name = name, Style = Style, Variant = Variant });
        }

        /// <summary>A stairwell in a building before it is raised (see <see cref="GreyboxBlock.Stairwell"/>; any number of storeys).</summary>
        public void Stairwell(GreyboxBlock.Building b, int lane, bool alongZ, int dir, bool flip = false) =>
            GreyboxBlock.Stairwell(World, Info, b, lane, alongZ, dir, flip);

        /// <summary>A straight wall of tiles (no footing): <paramref name="rows"/> 1.5 m rows tall from <paramref name="corner"/>.</summary>
        public int[] Strip(Vec3d corner, double length, bool alongX, int rows)
        {
            int[] ids = GreyboxBlock.StripWall(World.Tiles, corner, length, alongX, rows);
            Info.Tiles.AddRange(ids);
            return ids;
        }

        /// <summary>A wall standing on its own little footing; returns its level.</summary>
        public double LowWall(double x, double z, double length, bool alongX, int rows)
        {
            double th = World.Tiles.Config.TileThickness;
            double y = Foot(x, z, alongX ? x + length : x + th, alongX ? z + th : z + length, 1.2);
            Strip(new Vec3d(x, y, z), length, alongX, rows);
            return y;
        }

        /// <summary>A stair flight or ramp as breakable segments (see <see cref="GreyboxBlock.Climb"/>).</summary>
        public void Climb(Aabb box, Dir rise, bool stairs = true) =>
            GreyboxBlock.Climb(World, Info, box, rise, stairs ? TileKind.Stair : TileKind.Ramp);

        // ---------------------------------------------------------------- loot

        /// <summary>A chest standing at <paramref name="at"/> (feet), facing <paramref name="yaw"/> (0 = +z; its loot spills that way).</summary>
        public void Chest(Vec3d at, double yaw) => Chests.Add(new BrChestSpot { Position = at, Yaw = yaw, Region = -1 });

        /// <summary>A chest in the middle of building cell (i, j) on storey <paramref name="storey"/>.</summary>
        public void Chest(GreyboxBlock.Building b, int i, int j, int storey, double yaw)
        {
            const double Col = GreyboxBlock.Column;
            Chest(new Vec3d(b.Cx(i) + Col + 3, b.Y0 + storey * GreyboxBlock.StoreyHeight, b.Cz(j) + Col + 3), yaw);
        }

        /// <summary>A chest on its own small level spot (a hilltop); returns where it stands.</summary>
        public Vec3d SpotChest(double x, double z, double yaw)
        {
            double y = Foot(x - 1.5, z - 1.5, x + 1.5, z + 1.5, 1.0);
            var at = new Vec3d(x, y, z);
            Chest(at, yaw);
            return at;
        }

        /// <summary>An item lying on the map at the start (grenades, a C4 pair, a launcher...).</summary>
        public void Item(Item kind, int count, Vec3d at) => Items.Add(new GroundItem { Kind = kind, Count = count, Position = at });

        /// <summary>Names a POI (shown on the map later; the minimap and bots may use it).</summary>
        public BrPoi Poi(string name, Vec3d centre, params Aabb[] buildings)
        {
            var p = new BrPoi { Name = name, Centre = centre, Segment = Id };
            p.Buildings.AddRange(buildings);
            Pois.Add(p);
            return p;
        }

        // ---------------------------------------------------------------- props

        /// <summary>A tree here (dropped if it doesn't fit: too close to something, on steep ground; the tests list drops).</summary>
        public void Tree(double x, double z, int seed, bool big = false) => Props.Add(new PropRequest { Shape = big ? BrNature.Shape.BigTree : BrNature.Shape.Tree, X = x, Z = z, Seed = seed, Segment = Id });

        public void Rocks(double x, double z, int seed) => Props.Add(new PropRequest { Shape = BrNature.Shape.Rocks, X = x, Z = z, Seed = seed, Segment = Id });

        public void Bush(double x, double z, int seed) => Props.Add(new PropRequest { Shape = BrNature.Shape.Bush, X = x, Z = z, Seed = seed, Segment = Id });

        /// <summary>Keeps the prop scatter out of an area (a plaza, a road, a yard).</summary>
        public void Open(Rect2 r) => KeepOpen.Add(r);

        // ---------------------------------------------------------------- queries

        /// <summary>
        /// True if the rectangle grown by <paramref name="street"/> lies in <see cref="Area"/>, clear of everything solid
        /// built so far, every building's yard, the reserved lanes and plots, and every chest and item.
        /// </summary>
        public bool IsClear(Rect2 r, double street)
        {
            Rect2 g = r.Grow(street);
            if (g.X0 < Area.X0 || g.Z0 < Area.Z0 || g.X1 > Area.X1 || g.Z1 > Area.Z1) return false;
            if (World.BoxOverlapsSolid(new Aabb(new Vec3d(g.X0, BrGround.Lowest - 2, g.Z0), new Vec3d(g.X1, BrMap.NavTop, g.Z1)))) return false;
            foreach (Aabb bd in Info.Buildings) if (g.Overlaps(Rect2.Of(bd))) return false;
            foreach (Rect2 q in Reserved) if (g.Overlaps(q)) return false;
            foreach (var l in Lanes) if (LaneTouches(l.From, l.To, l.Half, g)) return false;
            foreach (BrChestSpot ch in Chests) if (g.Contains(ch.Position.X, ch.Position.Z)) return false;
            foreach (GroundItem it in Items) if (g.Contains(it.Position.X, it.Position.Z)) return false;
            return true;
        }

        /// <summary>True if a lane (a segment with a half width) comes within its half width of the rectangle.</summary>
        public static bool LaneTouches(Vec3d from, Vec3d to, double half, Rect2 r)
        {
            double len = (to - from).HorizontalLength;
            int n = Math.Max(1, (int)Math.Ceiling(len / 0.5));
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n, x = from.X + (to.X - from.X) * t, z = from.Z + (to.Z - from.Z) * t;
                if (GroundShapes.ToRect(x, z, r.X0, r.Z0, r.X1, r.Z1) < half) return true;
            }
            return false;
        }

        /// <summary>True if the rectangle reaches into the home plaza (within <see cref="BrLayout.HomeRadius"/> + <paramref name="grow"/> of the anchor).</summary>
        public bool InHome(Rect2 r, double grow = 0) =>
            Id != BrLayout.Centre && GroundShapes.ToRect(Anchor.X, Anchor.Z, r.X0, r.Z0, r.X1, r.Z1) < BrLayout.HomeRadius + grow;
    }
}
