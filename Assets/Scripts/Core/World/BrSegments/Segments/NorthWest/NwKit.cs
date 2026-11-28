using System;

namespace Sculpt.Core
{
    /// <summary>
    /// A building plan for the North West segment that can be mirrored east-west (<see cref="FX"/>) and north-south
    /// (<see cref="FZ"/>): every wall, hole, stair and chest is given in plan coordinates (stairs always up the plan's
    /// west column, rising plan-north), so one design faces any way. Walls along x: <see cref="WX"/>(storey, z-line,
    /// segment); walls along z: <see cref="WZ"/>(storey, x-line, segment).
    /// </summary>
    internal sealed class NwPlan
    {
        public readonly GreyboxBlock.Building T;
        public readonly bool FX, FZ;

        public NwPlan(double x, double y, double z, int mx, int mz, int storeys, bool fx = false, bool fz = false)
        {
            T = new GreyboxBlock.Building(x, z, mx, mz, storeys) { Y0 = y };
            FX = fx;
            FZ = fz;
        }

        public int Mx => T.Mx;
        public int Mz => T.Mz;
        public int Storeys => T.Storeys;
        public int I(int i) => FX ? T.Mx - 1 - i : i;
        public int J(int j) => FZ ? T.Mz - 1 - j : j;
        private int LI(int line) => FX ? T.Mx - line : line;
        private int LJ(int line) => FZ ? T.Mz - line : line;

        public void WX(int s, int line, int i, WallKind k) => T.AlongX[s, LJ(line), I(i)] = k;
        public void WZ(int s, int line, int j, WallKind k) => T.AlongZ[s, LI(line), J(j)] = k;
        public WallKind GetWX(int s, int line, int i) => T.AlongX[s, LJ(line), I(i)];
        public WallKind GetWZ(int s, int line, int j) => T.AlongZ[s, LI(line), J(j)];
        public void Hole(int s, int i, int j) => T.Hole[s, I(i), J(j)] = true;

        /// <summary>Every outer wall segment of storey <paramref name="s"/> set to <paramref name="k"/> (doors set after this win).</summary>
        public void Outer(int s, WallKind k)
        {
            for (int i = 0; i < Mx; i++) { WX(s, 0, i, k); WX(s, Mz, i, k); }
            for (int j = 0; j < Mz; j++) { WZ(s, 0, j, k); WZ(s, Mx, j, k); }
        }

        /// <summary>
        /// Switchback stairs up the plan's west column over cells 0..2, one flight a storey, up to the roof; the column's
        /// east wall gets doorways where the flights start and arrive and a wall beside the open middle (as the shared
        /// tower). Call once the level is final (the flights are built at it).
        /// </summary>
        public void Stairs(BrBuild b)
        {
            b.Stairwell(T, I(0), true, FZ ? -1 : +1, FX);
            for (int s = 0; s < Storeys; s++)
            {
                WZ(s, 1, 0, WallKind.Door);
                WZ(s, 1, 1, WallKind.Wall);
                WZ(s, 1, 2, WallKind.Door);
            }
        }

        public void Chest(BrBuild b, int i, int j, int storey, double yaw = 0) => b.Chest(T, I(i), J(j), storey, yaw);

        /// <summary>A chest at an offset from the middle of plan cell (i, j) (offsets in plan directions).</summary>
        public void ChestAt(BrBuild b, int i, int j, int storey, double dx, double dz, double yaw = 0)
        {
            const double Col = GreyboxBlock.Column;
            double cx = T.Cx(I(i)) + Col + 3, cz = T.Cz(J(j)) + Col + 3;
            b.Chest(new Vec3d(cx + (FX ? -dx : dx), T.Y0 + storey * GreyboxBlock.StoreyHeight, cz + (FZ ? -dz : dz)), yaw);
        }

        public void Raise(BrBuild b, string name, BrStyle style, int variant)
        {
            b.Style = style;
            b.Variant = variant;
            b.Raise(T, name);
        }
    }

    /// <summary>
    /// The North West segment's own buildings (Farmstead and Radio Mast Hill). Every one rewards breaching: sealed
    /// rooms with the loot, blind long sides, lofts you reach faster through the floor than round by the stairs, silos
    /// whose only door is a hole. Positions are map coordinates; levels absolute (cut the footing first).
    /// </summary>
    internal static class NwKit
    {
        public const double M = GreyboxBlock.Module, Col = GreyboxBlock.Column, Storey = GreyboxBlock.StoreyHeight;

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BrBuild, System.Collections.Generic.List<Vec3d>> Sealed =
            new System.Runtime.CompilerServices.ConditionalWeakTable<BrBuild, System.Collections.Generic.List<Vec3d>>();

        /// <summary>The chests this build put in sealed rooms (no door, no window: the way in is a hole), for the tests.</summary>
        public static System.Collections.Generic.List<Vec3d> SealedChests(BrBuild b) => Sealed.GetOrCreateValue(b);

        /// <summary>Marks the last <paramref name="count"/> chests added as sealed in.</summary>
        public static void MarkSealed(BrBuild b, int count = 1)
        {
            var list = SealedChests(b);
            lock (list)
                for (int k = b.Chests.Count - count; k < b.Chests.Count; k++) list.Add(b.Chests[k].Position);
        }

        /// <summary>The outside size of a building <paramref name="modules"/> modules long.</summary>
        public static double Span(int modules) => modules * M + Col;

        /// <summary>The smallest apron that keeps a building's corners on level ground: the level ground round a footing
        /// has rounded corners, and the ground's 1.5 m cells reach up to 2.12 m past a corner.</summary>
        public const double MinApron = 2.25;

        /// <summary>A level footing under a building of mx x mz modules at (x, z); returns its level.</summary>
        public static double Foot(BrBuild b, double x, double z, int mx, int mz, double apron, double want = double.NaN) =>
            b.Foot(x, z, x + Span(mx), z + Span(mz), Math.Max(apron, MinApron), want);

        // ---------------------------------------------------------------- barns

        /// <summary>
        /// A two-storey barn, <paramref name="mx"/> (3 or 4) x <paramref name="mz"/> (4 or more) modules: a tall nave
        /// open from the floor to the roof slab, a hay loft all round it reached by stairs up the west side (and on to
        /// the flat roof, with a parapet), a wide door in each gable, a side door. The long east side is blind on the
        /// ground floor. A sealed tack room in the south-east corner: break in from the loft above or from outside.
        /// Chests: the tack room, the hall's far end, two loft corners, the roof (in that order, up to <paramref name="chests"/>).
        /// </summary>
        public static NwPlan Barn(BrBuild b, double x, double y, double z, int mx, int mz, string name, BrStyle style, int variant,
                                  int chests = 5, bool fx = false, bool fz = false, bool parapet = true)
        {
            var p = new NwPlan(x, y, z, mx, mz, 2, fx, fz);
            p.Stairs(b);
            int mid = mx / 2;
            for (int i = 1; i < mx - 1; i++)
                for (int j = 1; j < mz - 1; j++) p.Hole(0, i, j);
            // Tack room (sealed) in the plan's south-east corner.
            p.WX(0, 1, mx - 1, WallKind.Wall);
            p.WZ(0, mx - 1, 0, WallKind.Wall);
            p.WX(0, 0, mid, WallKind.WideDoor);
            p.WX(0, mz, mid, WallKind.WideDoor);
            if (mz > 3) p.WZ(0, 0, 3, WallKind.ShutDoor);
            for (int j = 3; j < mz; j++) p.WZ(1, 0, j, j == 3 && mz > 4 ? WallKind.Wall : WallKind.Window);
            for (int j = 1; j < mz; j += 2) p.WZ(1, mx, j, WallKind.Window);
            p.WX(1, 0, mid, WallKind.Window);
            p.WX(1, mz, mid, WallKind.Window);
            p.Raise(b, name, style, variant);
            if (parapet) BrKit.Parapet(b, p.T);
            int n = 0;
            if (n++ < chests) { p.Chest(b, mx - 1, 0, 0); MarkSealed(b); }
            if (n++ < chests) p.Chest(b, mid, mz - 1, 0, Math.PI);
            if (n++ < chests) p.Chest(b, mx - 1, mz - 1, 1, Math.PI);
            if (n++ < chests) p.Chest(b, 0, mz - 1, 1, Math.PI);
            if (n++ < chests) p.Chest(b, mid, mz - 1, 2, Math.PI);
            return p;
        }

        /// <summary>A one-room field barn (two modules, one storey): a wide open front (plan south), a chest at the back.</summary>
        public static NwPlan FieldBarn(BrBuild b, double x, double y, double z, bool alongX, bool openSide, string name = "Field barn", int variant = 0)
        {
            NwPlan p;
            if (alongX)
            {
                p = new NwPlan(x, y, z, 2, 1, 1, false, openSide);
                p.WX(0, 0, 0, WallKind.WideDoor);
                p.WX(0, 1, 1, WallKind.Window);
            }
            else
            {
                p = new NwPlan(x, y, z, 1, 2, 1, openSide, false);
                p.WZ(0, 0, 0, WallKind.WideDoor);
                p.WZ(0, 1, 1, WallKind.Window);
            }
            p.Raise(b, name, BrStyle.Timber, variant);
            if (alongX) p.Chest(b, 1, 0, 0, 0); else p.Chest(b, 0, 1, 0, 0);
            return p;
        }

        /// <summary>A hay rick: a sealed one-module store with a chest, timber. Break in.</summary>
        public static NwPlan Rick(BrBuild b, double x, double y, double z, int variant = 0)
        {
            var p = new NwPlan(x, y, z, 1, 1, 1);
            p.Raise(b, "Hay rick", BrStyle.Timber, variant);
            p.Chest(b, 0, 0, 0);
            MarkSealed(b);
            return p;
        }

        /// <summary>A one-room hut with one shut door: side 0 south, 1 north, 2 west, 3 east. A chest inside.</summary>
        public static NwPlan Hut(BrBuild b, double x, double y, double z, int side, string name, BrStyle style, int variant = 0, bool chest = true)
        {
            var p = new NwPlan(x, y, z, 1, 1, 1);
            if (side == 0) p.WX(0, 0, 0, WallKind.ShutDoor);
            else if (side == 1) p.WX(0, 1, 0, WallKind.ShutDoor);
            else if (side == 2) p.WZ(0, 0, 0, WallKind.ShutDoor);
            else p.WZ(0, 1, 0, WallKind.ShutDoor);
            p.Raise(b, name, style, variant);
            if (chest) p.Chest(b, 0, 0, 0);
            return p;
        }

        /// <summary>A silo bin: one module square, <paramref name="storeys"/> tall, sealed, a floor every storey (so a
        /// drop through a broken floor is one storey, never a fall to the bottom). A chest at the bottom, and one in the
        /// top bin if <paramref name="topChest"/> (reach it by breaking in through the roof).</summary>
        public static NwPlan Silo(BrBuild b, double x, double y, double z, int storeys, bool topChest, int variant)
        {
            var p = new NwPlan(x, y, z, 1, 1, storeys);
            p.Raise(b, "Silo", BrStyle.Industrial, variant);
            p.Chest(b, 0, 0, 0);
            MarkSealed(b);
            if (topChest) { p.Chest(b, 0, 0, storeys - 1); MarkSealed(b); }
            return p;
        }

        /// <summary>
        /// A row of one-room units one module deep (stables, sheds, cottages): along x the front is the plan's south
        /// side (fz: north), along z the front is the plan's west side (fx: east). Each unit has a shut door on the
        /// front except the sealed ones (<paramref name="sealedMask"/>, bit k = unit k), which get a chest each; the
        /// others get a chest if bit k of <paramref name="chestMask"/> is set. Back windows on every other unit.
        /// </summary>
        public static NwPlan Row(BrBuild b, double x, double y, double z, int units, bool alongX, bool flip, int sealedMask, int chestMask,
                                 string name, BrStyle style, int variant = 0, bool backWindows = true)
        {
            NwPlan p = alongX ? new NwPlan(x, y, z, units, 1, 1, false, flip) : new NwPlan(x, y, z, 1, units, 1, flip, false);
            for (int k = 0; k < units; k++)
            {
                bool sealed_ = (sealedMask >> k & 1) != 0;
                if (alongX)
                {
                    if (k > 0) p.WZ(0, k, 0, WallKind.Wall);
                    if (!sealed_) p.WX(0, 0, k, WallKind.ShutDoor);
                    if (backWindows && k % 2 == 0 && !sealed_) p.WX(0, 1, k, WallKind.Window);
                }
                else
                {
                    if (k > 0) p.WX(0, k, 0, WallKind.Wall);
                    if (!sealed_) p.WZ(0, 0, k, WallKind.ShutDoor);
                    if (backWindows && k % 2 == 0 && !sealed_) p.WZ(0, 1, k, WallKind.Window);
                }
            }
            p.Raise(b, name, style, variant);
            for (int k = 0; k < units; k++)
            {
                bool sealed_ = (sealedMask >> k & 1) != 0;
                if (!sealed_ && (chestMask >> k & 1) == 0) continue;
                if (alongX) p.Chest(b, k, 0, 0); else p.Chest(b, 0, k, 0);
                if (sealed_) MarkSealed(b);
            }
            return p;
        }

        // ---------------------------------------------------------------- houses

        /// <summary>
        /// A two-storey farmhouse, 3 x 3 modules, flat roof with a parapet. Stairs up the plan's west column. Ground
        /// floor: kitchen at the front (shut front door, plan south), parlour behind it, a hall to the back door, and a
        /// sealed pantry in the back corner (its loot is the reason to break the wall). Upstairs a bedroom at the front,
        /// an open landing. Chests: pantry, bedroom, parlour, roof (up to <paramref name="chests"/>).
        /// </summary>
        public static NwPlan FarmHouse(BrBuild b, double x, double y, double z, string name, BrStyle style, int variant,
                                       int chests = 3, bool fx = false, bool fz = false)
        {
            var p = new NwPlan(x, y, z, 3, 3, 2, fx, fz);
            p.Stairs(b);
            // Ground floor.
            p.WX(0, 0, 1, WallKind.ShutDoor);
            p.WX(0, 0, 2, WallKind.Window);
            p.WX(0, 1, 1, WallKind.Door);
            p.WX(0, 1, 2, WallKind.Wall);
            p.WX(0, 2, 2, WallKind.Wall);   // the pantry (2, 2)
            p.WZ(0, 2, 2, WallKind.Wall);
            p.WX(0, 3, 1, WallKind.ShutDoor);
            p.WZ(0, 3, 0, WallKind.Window);
            p.WZ(0, 3, 1, WallKind.Window);
            p.WZ(0, 0, 1, WallKind.Window);
            // Upstairs.
            p.WX(1, 1, 1, WallKind.Door);
            p.WX(1, 1, 2, WallKind.Wall);
            p.WX(1, 0, 1, WallKind.Window);
            p.WX(1, 0, 2, WallKind.Window);
            p.WZ(1, 3, 1, WallKind.Window);
            p.WZ(1, 3, 2, WallKind.Window);
            p.WX(1, 3, 2, WallKind.Window);
            p.WX(1, 3, 1, WallKind.Window);
            p.Raise(b, name, style, variant);
            BrKit.Parapet(b, p.T);
            int n = 0;
            if (n++ < chests) { p.Chest(b, 2, 2, 0, Math.PI); MarkSealed(b); }
            if (n++ < chests) p.Chest(b, 2, 0, 1);
            if (n++ < chests) p.Chest(b, 2, 1, 0);
            if (n++ < chests) p.Chest(b, 2, 2, 2, Math.PI);
            return p;
        }

        /// <summary>
        /// A two-storey hall, 3 x 3 modules, stairs up the plan's west column, flat roof with a parapet: a control
        /// room or taproom at the front (plan south, shut door), a sealed strongroom across the back two cells.
        /// Upstairs open, windows all round. Chests: two in the strongroom, the front room, upstairs, the roof.
        /// </summary>
        public static NwPlan Hall(BrBuild b, double x, double y, double z, string name, BrStyle style, int variant, int chests = 5,
                                  bool fx = false, bool fz = false)
        {
            var p = new NwPlan(x, y, z, 3, 3, 2, fx, fz);
            p.Stairs(b);
            p.WZ(0, 1, 2, WallKind.Wall);   // the stair column's ground-floor nook stays shut off from the strongroom
            p.WX(0, 2, 1, WallKind.Wall);   // the strongroom (1..2, 2)
            p.WX(0, 2, 2, WallKind.Wall);
            p.WX(0, 0, 1, WallKind.ShutDoor);
            p.WX(0, 0, 2, WallKind.Window);
            p.WZ(0, 3, 0, WallKind.Window);
            p.WZ(0, 3, 1, WallKind.Door);
            for (int i = 1; i < 3; i++) { p.WX(1, 0, i, WallKind.Window); p.WX(1, 3, i, WallKind.Window); }
            for (int j = 0; j < 3; j++) p.WZ(1, 3, j, WallKind.Window);
            p.Raise(b, name, style, variant);
            BrKit.Parapet(b, p.T);
            int n = 0;
            if (n++ < chests) { p.ChestAt(b, 1, 2, 0, 1.0, 0, Math.PI); MarkSealed(b); }
            if (n++ < chests) { p.ChestAt(b, 2, 2, 0, -1.0, 0, Math.PI); MarkSealed(b); }
            if (n++ < chests) p.Chest(b, 2, 0, 0);
            if (n++ < chests) p.Chest(b, 2, 1, 1);
            if (n++ < chests) p.Chest(b, 2, 2, 2, Math.PI);
            return p;
        }

        /// <summary>
        /// A tall tower 2 x 3 modules, <paramref name="storeys"/> storeys, stairs up the plan's west column to a flat
        /// roof with a parapet; the plan's east column holds one room a floor. <paramref name="walls"/> picks each
        /// upper storey's outer walls (lattice: open bays and windows all round; plain: windows on two faces). Ground
        /// floor: a shut door on the plan's east side and a wide door on the plan's south side. A chest every
        /// <paramref name="chestEvery"/> storeys from storey <paramref name="firstChest"/>, and one on the roof.
        /// </summary>
        public static NwPlan Tower(BrBuild b, double x, double y, double z, int storeys, string name, BrStyle style, int variant,
                                   bool lattice, int chestEvery, int firstChest, bool fx = false, bool fz = false, bool parapet = true,
                                   Action<NwPlan> before = null)
        {
            var p = new NwPlan(x, y, z, 2, 3, storeys, fx, fz);
            p.Stairs(b);
            p.WX(0, 0, 1, WallKind.WideDoor);
            p.WZ(0, 2, 1, WallKind.ShutDoor);
            for (int s = 1; s < storeys; s++)
            {
                if (lattice) p.Outer(s, s % 2 == 0 ? WallKind.None : WallKind.Window);
                else
                {
                    p.WX(s, 0, 1, (s % 2 == 0) ? WallKind.Window : WallKind.Wall);
                    for (int j = 0; j < 3; j++) p.WZ(s, 2, j, (j + s) % 2 == 0 ? WallKind.Window : WallKind.Wall);
                }
            }
            before?.Invoke(p);
            p.Raise(b, name, style, variant);
            if (parapet) BrKit.Parapet(b, p.T);
            for (int s = firstChest; s < storeys; s += Math.Max(1, chestEvery)) p.Chest(b, 1, (s / Math.Max(1, chestEvery)) % 2 == 0 ? 0 : 2, s, Math.PI);
            p.Chest(b, 1, 2, storeys, Math.PI);
            return p;
        }

        /// <summary>
        /// A burnt-out farmhouse, 3 x 2 modules: no roof, half the upper floor fallen in, walls gone or holed by a
        /// fixed hash of <paramref name="seed"/>. Loot among the rubble on the ground floor.
        /// </summary>
        public static NwPlan Ruin(BrBuild b, double x, double y, double z, int seed, string name, int chests = 3)
        {
            var p = new NwPlan(x, y, z, 3, 2, 2);
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 2; j++)
                {
                    p.Hole(1, i, j);
                    if (BrKit.Hash(i, j, (uint)seed) % 3 != 0) p.Hole(0, i, j);
                }
            p.Hole(0, 0, 0);
            p.Hole(0, 2, 1);
            WallKind Pick(int s, int k)
            {
                uint h = BrKit.Hash(k, s, (uint)seed * 31 + 7) % 10;
                if (s == 1) return h < 5 ? WallKind.None : h < 8 ? WallKind.Window : WallKind.Wall;
                return h < 2 ? WallKind.None : h < 6 ? WallKind.Window : WallKind.Wall;
            }
            for (int s = 0; s < 2; s++)
            {
                int k = 0;
                for (int i = 0; i < 3; i++) { p.WX(s, 0, i, Pick(s, k++)); p.WX(s, 2, i, Pick(s, k++)); }
                for (int j = 0; j < 2; j++) { p.WZ(s, 0, j, Pick(s, k++)); p.WZ(s, 3, j, Pick(s, k++)); }
            }
            p.WX(0, 0, 1, WallKind.Door);
            p.WZ(0, 1, 1, WallKind.Wall);   // the one room still shut: break in
            p.WX(0, 1, 0, WallKind.Wall);
            p.WZ(0, 0, 1, WallKind.Wall);
            p.WX(0, 2, 0, WallKind.Wall);
            p.Raise(b, name, BrStyle.Ruin, seed & 3);
            int n = 0;
            if (n++ < chests) { p.Chest(b, 0, 1, 0); MarkSealed(b); }
            if (n++ < chests) p.Chest(b, 1, 0, 0);
            if (n++ < chests) p.Chest(b, 2, 1, 0);
            return p;
        }

        /// <summary>
        /// A bunker, 3 x 2 modules, one storey, concrete: a corridor along the plan's south side with a shut blast door
        /// at its west end, a guard room off it and two sealed vaults. Its flat roof is the way in for the bold: break
        /// the slab and drop into a vault.
        /// </summary>
        public static NwPlan Bunker(BrBuild b, double x, double y, double z, string name, bool fx = false, bool fz = false)
        {
            var p = new NwPlan(x, y, z, 3, 2, 1, fx, fz);
            p.WZ(0, 0, 0, WallKind.ShutDoor);
            p.WX(0, 1, 0, WallKind.Door);
            p.WX(0, 1, 1, WallKind.Wall);
            p.WX(0, 1, 2, WallKind.Wall);
            p.WZ(0, 1, 1, WallKind.Wall);
            p.WZ(0, 2, 1, WallKind.Wall);
            p.WZ(0, 3, 0, WallKind.Window);
            p.Raise(b, name, BrStyle.Concrete, 1);
            p.Chest(b, 0, 1, 0, Math.PI);
            p.Chest(b, 1, 1, 0, Math.PI);
            p.Chest(b, 2, 1, 0, Math.PI);
            MarkSealed(b, 2);
            p.ChestAt(b, 2, 0, 0, 1.5, 0, Math.PI / 2);
            return p;
        }

        /// <summary>A chapel nave, 2 x 4 modules, one tall storey: a shut door in the plan's south gable, windows down both sides, a chest at the altar.</summary>
        public static NwPlan Chapel(BrBuild b, double x, double y, double z, string name, bool fz = false)
        {
            var p = new NwPlan(x, y, z, 2, 4, 1, false, fz);
            p.WX(0, 0, 0, WallKind.ShutDoor);
            p.WX(0, 0, 1, WallKind.Window);
            for (int j = 0; j < 4; j++) { p.WZ(0, 0, j, j == 0 ? WallKind.Wall : WallKind.Window); p.WZ(0, 2, j, j == 3 ? WallKind.Wall : WallKind.Window); }
            p.Raise(b, name, BrStyle.Stone, 2);
            p.Chest(b, 1, 3, 0, Math.PI);
            return p;
        }

        // ---------------------------------------------------------------- walls and nature

        /// <summary>A walled yard: walls <paramref name="rows"/> tiles tall (3 = 4.5 m, too tall to climb) round the
        /// rectangle at level y, with gates (3 m gaps) given as (side, offset from the side's start): side 0 south, 1 north, 2 west, 3 east.</summary>
        public static void Walls(BrBuild b, double x0, double z0, double x1, double z1, double y, int rows, params (int side, double at)[] gates)
        {
            const double th = 0.3, gate = 3.0;
            void Side(int side, Vec3d start, double length, bool alongX)
            {
                double a = 0;
                var cuts = new System.Collections.Generic.List<double>();
                foreach (var g in gates) if (g.side == side) cuts.Add(g.at);
                cuts.Sort();
                foreach (double c in cuts)
                {
                    if (c - a > 0.5) b.Strip(alongX ? start + new Vec3d(a, 0, 0) : start + new Vec3d(0, 0, a), c - a, alongX, rows);
                    a = c + gate;
                }
                if (length - a > 0.5) b.Strip(alongX ? start + new Vec3d(a, 0, 0) : start + new Vec3d(0, 0, a), length - a, alongX, rows);
            }
            Side(0, new Vec3d(x0, y, z0), x1 - x0, true);
            Side(1, new Vec3d(x0, y, z1 - th), x1 - x0, true);
            Side(2, new Vec3d(x0, y, z0 + th), z1 - z0 - 2 * th, false);
            Side(3, new Vec3d(x1 - th, y, z0 + th), z1 - z0 - 2 * th, false);
        }

        /// <summary>A hedgerow: bushes every <paramref name="step"/> metres from (x0, z0) to (x1, z1), leaving out the
        /// stretches listed in <paramref name="gaps"/> (as fractions of the length: from, to).</summary>
        public static void Hedge(BrBuild b, double x0, double z0, double x1, double z1, double step, int seed, params (double from, double to)[] gaps)
        {
            double len = Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0));
            int n = Math.Max(1, (int)Math.Floor(len / step));
            for (int k = 0; k <= n; k++)
            {
                double t = (double)k / n;
                bool skip = false;
                foreach (var g in gaps) if (t >= g.from && t <= g.to) skip = true;
                if (!skip) b.Bush(x0 + (x1 - x0) * t, z0 + (z1 - z0) * t, seed * 977 + k);
            }
        }

        /// <summary>An orchard: <paramref name="cols"/> x <paramref name="rows"/> trees from (x0, z0), every
        /// (<paramref name="dx"/>, <paramref name="dz"/>) metres, alternate rows shifted half a step.</summary>
        public static void Orchard(BrBuild b, double x0, double z0, int cols, int rows, double dx, double dz, int seed, bool stagger = true)
        {
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    double x = x0 + i * dx + (stagger && j % 2 == 1 ? dx / 2 : 0);
                    b.Tree(x, z0 + j * dz, seed * 131 + j * 17 + i);
                }
        }
    }
}
