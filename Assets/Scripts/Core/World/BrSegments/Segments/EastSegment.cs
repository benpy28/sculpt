namespace Sculpt.Core
{
    /// <summary>
    /// The East segment of the battle royale map (id 5, see <see cref="BrLayout"/>): Dam and reservoir valley. A plateau
    /// with a gorge cut 24 m into it, held by Greywater Dam (five storeys, rooms inside, its crest the bridge over the
    /// gorge); the dry reservoir upstream with the drowned village of Oldmere on its bed; the power house and the tailrace
    /// yard in the canyon below the dam; the staff village of Damside, the switchyard and Surge Hill's tower in the south;
    /// the home POI round the anchor. Plan and levels: <see cref="EastPlan"/>; ground: <see cref="EastGround"/>; content:
    /// <see cref="EastBuild"/>.
    /// </summary>
    public sealed class EastSegment : BrSegment
    {
        public override int Id => 5;

        public override string HomePoiName => "Damside Square";

        public override double Ground(double x, double z) => EastGround.Ground(x, z);

        public override void Build(BrBuild b) => EastBuild.Build(b);
    }
}
