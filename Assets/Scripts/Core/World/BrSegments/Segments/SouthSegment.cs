namespace Sculpt.Core
{
    /// <summary>
    /// The South segment of the battle royale map (id 1, see <see cref="BrLayout"/>): a market town. The home POI is the
    /// Market Square round the anchor (a butter cross, stalls, the long covered market hall on its north side, the town
    /// hall and its ten-storey clock tower on the east side, a terrace of shops on the west); the town around it is
    /// dense two- and three-storey terraces on narrow streets (party walls, sealed loot rooms upstairs), courtyard inns,
    /// walled gardens, an upper town on a hill with the church, a mill on Mill Hill and allotments on Gallows Knoll.
    /// Building kit: <see cref="SouthKit"/>; the plan: <see cref="SouthPlan"/>.
    /// </summary>
    public sealed class SouthSegment : BrSegment
    {
        public override int Id => 1;

        public override string HomePoiName => "Market Square";

        public override double Ground(double x, double z) => SouthPlan.Ground(x, z);

        public override void Build(BrBuild b) => SouthPlan.Build(b);
    }
}
