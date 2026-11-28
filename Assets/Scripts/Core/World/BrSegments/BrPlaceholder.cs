using System;

namespace Sculpt.Core
{
    /// <summary>
    /// Stand-in content for a segment nobody has designed yet (framework-owned): two gentle hills, the placeholder home
    /// POI (<see cref="BrKit.HomeSquare"/>), villages over the rest. Outer segments get the same layout turned like their
    /// spawn region, so the stand-in map stays fair. A segment thread deletes its calls to this.
    /// </summary>
    public static class BrPlaceholder
    {
        // Hills in the template's frame (u, v from the map's centre): edge template east, corner template north east.
        private static readonly (double u, double v, double r, double h)[] EdgeHills = { (282, 0, 40, 8), (470, 150, 35, 7) };
        private static readonly (double u, double v, double r, double h)[] CornerHills = { (263, 385, 40, 8), (445, 425, 35, 7) };

        public static double Ground(int id, double x, double z)
        {
            double h = BrGround.Shared(x, z), c = BrLayout.MapCentre;
            if (id == BrLayout.Centre) return h + GroundShapes.Hill(x, z, c, c, 120, 6);
            int[] m = BrLayout.KitSymmetry(id);
            foreach (var k in BrLayout.IsCorner(id) ? CornerHills : EdgeHills)
                h += GroundShapes.Hill(x, z, c + m[0] * k.u + m[1] * k.v, c + m[2] * k.u + m[3] * k.v, k.r, k.h);
            return h;
        }

        public static void Build(BrBuild b)
        {
            if (b.Id == BrLayout.Centre)
            {
                // A few towers round the middle (exercises the tower kit: ten storeys, a twelve-storey landmark).
                double c = BrLayout.MapCentre;
                (double dx, double dz, int storeys)[] towers = { (-60, -60, 10), (40, -60, 8), (-60, 40, 8), (40, 40, 12) };
                foreach (var t in towers)
                {
                    double side = 3 * GreyboxBlock.Module + GreyboxBlock.Column;
                    double y = b.Foot(c + t.dx, c + t.dz, c + t.dx + side, c + t.dz + side, 3); // the level first: stairs are built at it
                    var plan = BrKit.TowerPlan(b, c + t.dx, y, c + t.dz, 3, 3, t.storeys);
                    b.Style = t.storeys == 12 ? BrStyle.Stone : BrStyle.Concrete;
                    b.Raise(plan, t.storeys == 12 ? "Landmark tower" : "Tower");
                    BrKit.Parapet(b, plan);
                    for (int s = 0; s < t.storeys; s += 2) b.Chest(plan, 2, (s / 2) % 2 == 0 ? 0 : 2, s, Math.PI);
                    b.Chest(plan, 2, 2, t.storeys, Math.PI);
                }
                b.Style = BrStyle.Brick;
                b.Poi("Towers", new Vec3d(c, BrGround.Base, c));
                b.Open(new Rect2(c - 20, c - 20, c + 20, c + 20));
            }
            else BrKit.HomeSquare(b);
            BrKit.Villages(b, b.Area, stopAtChests: b.Id == BrLayout.Centre ? BrLayout.MaxChestsCentre - 10 : BrLayout.MaxChests - 10);
        }
    }
}
