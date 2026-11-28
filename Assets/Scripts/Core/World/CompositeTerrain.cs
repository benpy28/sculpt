namespace Sculpt.Core
{
    /// <summary>
    /// Two worlds answered as one: the sub-cell ground (hills, the arena) and the building brushes. Solid if either is
    /// solid; a ray stops at the nearer hit; ground contact takes the flatter surface when both touch.
    /// </summary>
    public sealed class CompositeTerrain : ITerrain, ISteepGround
    {
        public readonly ITerrain A;
        public readonly ITerrain B;

        public CompositeTerrain(ITerrain a, ITerrain b)
        {
            A = a;
            B = b;
            _steep = a is ISteepGround || b is ISteepGround;
            // The wall cells look past their tile grid's edge to tell a floor from a doorway: the ground counts.
            if (b is BrushWorld bw) bw.Tiles.Outside = a;
        }

        private readonly bool _steep;

        /// <summary>Rock faces under the box and no walkable ground from either world.</summary>
        public bool SteepUnder(Aabb box, double probe, out Vec3d normal)
        {
            normal = new Vec3d(0, 1, 0);
            if (!_steep) return false;
            Vec3d n = default;
            bool steep = A is ISteepGround ga && ga.SteepUnder(box, probe, out n);
            if (!steep) steep = B is ISteepGround gb && gb.SteepUnder(box, probe, out n);
            if (!steep) return false;
            if (A.GroundContact(box, probe, out _) || B.GroundContact(box, probe, out _)) return false;
            normal = n;
            return true;
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

        /// <summary>
        /// Cast rays into B first, then into A only as far as B's hit. The same answer (the nearer hit, A's on a tie)
        /// when both worlds report their nearest hit within the distance asked, ends included; much cheaper when B
        /// (buildings) is usually hit first and A (ground and props) is costly to walk far. The battle royale map sets it.
        /// </summary>
        public bool RayIntoBFirst;

        public TerrainHit Raycast(Vec3d origin, Vec3d direction, double maxDistance)
        {
            if (RayIntoBFirst)
            {
                TerrainHit bf = B.Raycast(origin, direction, maxDistance);
                TerrainHit af = A.Raycast(origin, direction, bf.Hit ? bf.Distance : maxDistance);
                if (!bf.Hit) return af;
                if (!af.Hit) return bf;
                return bf.Distance < af.Distance ? bf : af;
            }
            TerrainHit a = A.Raycast(origin, direction, maxDistance);
            TerrainHit b = B.Raycast(origin, direction, a.Hit ? a.Distance : maxDistance);
            if (!a.Hit) return b;
            if (!b.Hit) return a;
            return b.Distance < a.Distance ? b : a;
        }
    }
}
