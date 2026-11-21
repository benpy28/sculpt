namespace Sculpt.Core
{
    /// <summary>
    /// What movement, bullets and sculpt aiming need from the world. Implemented by the sub-cell grid and by
    /// "what if" overlays. Everything answers from the one chamfered surface (<see cref="TerrainShape"/>).
    /// </summary>
    public interface ITerrain
    {
        /// <summary>Does the box overlap solid terrain (touching is not overlapping)?</summary>
        bool BoxOverlapsSolid(Aabb box);

        /// <summary>
        /// Is there ground under the box within <paramref name="probe"/> metres? <paramref name="normal"/> is the surface
        /// normal it rests on (the flattest one if several touch), straight up if none qualifies as ground.
        /// </summary>
        bool GroundContact(Aabb box, double probe, out Vec3d normal);

        TerrainHit Raycast(Vec3d origin, Vec3d direction, double maxDistance);
    }

    /// <summary>Result of a ray cast against the terrain surface.</summary>
    public struct TerrainHit
    {
        public bool Hit;
        public Vec3d Point;
        public double Distance;
        /// <summary>Unit surface normal. Chamfers give diagonal normals.</summary>
        public Vec3d Normal;
        /// <summary>The solid sub-cell the hit belongs to (nearest occupied sub-cell of the hit dual cell).</summary>
        public Int3 Sub;
        /// <summary>Lattice vertex (dual cell) where the ray met the surface.</summary>
        public Int3 Vertex;
        /// <summary>Id of <see cref="Sub"/>.</summary>
        public ushort Id;
        /// <summary>Brush index + 1 when a <see cref="BrushWorld"/> brush was hit, 0 for sub-cell terrain.</summary>
        public int Brush;
    }
}
