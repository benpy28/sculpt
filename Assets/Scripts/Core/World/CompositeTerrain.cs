namespace Sculpt.Core
{
    /// <summary>
    /// Two worlds answered as one: the sub-cell ground (hills, the arena) and the building brushes. Solid if either is
    /// solid; a ray stops at the nearer hit; ground contact takes the flatter surface when both touch.
    /// </summary>
    public sealed class CompositeTerrain : ITerrain
    {
        public readonly ITerrain A;
        public readonly ITerrain B;

        public CompositeTerrain(ITerrain a, ITerrain b)
        {
            A = a;
            B = b;
            // The wall cells look past their tile grid's edge to tell a floor from a doorway: the ground counts.
            if (b is BrushWorld bw) bw.Tiles.Outside = a;
        }

        public bool BoxOverlapsSolid(Aabb box) => A.BoxOverlapsSolid(box) || B.BoxOverlapsSolid(box);

        public bool GroundContact(Aabb box, double probe, out Vec3d normal)
        {
            bool a = A.GroundContact(box, probe, out Vec3d na);
            bool b = B.GroundContact(box, probe, out Vec3d nb);
            if (a && b) normal = nb.Y > na.Y ? nb : na;
            else normal = b ? nb : na;
            return a || b;
        }

        public TerrainHit Raycast(Vec3d origin, Vec3d direction, double maxDistance)
        {
            TerrainHit a = A.Raycast(origin, direction, maxDistance);
            TerrainHit b = B.Raycast(origin, direction, a.Hit ? a.Distance : maxDistance);
            if (!a.Hit) return b;
            if (!b.Hit) return a;
            return b.Distance < a.Distance ? b : a;
        }
    }
}
