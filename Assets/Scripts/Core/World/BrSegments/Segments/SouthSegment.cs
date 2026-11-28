namespace Sculpt.Core
{
    /// <summary>
    /// The South segment of the battle royale map (id 1, see <see cref="BrLayout"/>). Owned by its own design thread:
    /// replace the placeholder with the real design (ground, home POI, POIs, buildings, cover, props). Contract and rules:
    /// <see cref="BrLayout"/>, <see cref="BrSegment"/>, <see cref="BrBuild"/>; shared buildings in <see cref="BrKit"/>.
    /// </summary>
    public sealed class SouthSegment : BrSegment
    {
        public override int Id => 1;

        public override double Ground(double x, double z) => BrPlaceholder.Ground(Id, x, z);

        public override void Build(BrBuild b) => BrPlaceholder.Build(b);
    }
}
