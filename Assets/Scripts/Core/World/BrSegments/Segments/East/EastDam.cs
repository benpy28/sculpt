using System;
using static Sculpt.Core.EastPlan;

namespace Sculpt.Core
{
    /// <summary>
    /// Greywater Dam and what stands round it: the dam (five storeys wedged across the gorge, its crest the bridge between
    /// the plateaus), the intake tower in the throat (bridged to the crest), the power house against its downstream face
    /// (its roof a terrace at the bed's level, so the dam's third storey runs straight through from the reservoir to it),
    /// the tailrace yard and the canyon tower up to the north plateau, and the buildings on the plateaus at both ends.
    /// </summary>
    public static class EastDam
    {
        private const double Col = EastBuild.Col, Md = EastBuild.M;

        public static void Build(BrBuild b)
        {
            GreyboxBlock.Building dam = Dam(b);
            Intake(b);
            PowerHouse(b);
            CanyonYard(b);
            Plateaus(b, dam);
            b.Poi("Greywater Dam", new Vec3d((DamX0 + DamX1) / 2, Floor, GorgeMid));
        }

        // ---------------------------------------------------------------- the dam

        /// <summary>
        /// The dam: 4 x 9 modules, five storeys from the canyon floor to the crest. Lanes across it (x): the upstream
        /// gallery, two rows of rooms, the downstream gallery. A stairwell at each end (along z in the south, along x in
        /// the north), both up to the crest. Ground floor: doors out to the canyon at both ends and two archways into the
        /// power house, a sealed turbine vault. Third storey: the way in from the reservoir bed (two shut doors and a wide
        /// one) and out onto the power house's roof. Upstream face blank except a sniper's window or two near the top;
        /// downstream face windows over the canyon.
        /// </summary>
        private static GreyboxBlock.Building Dam(BrBuild b)
        {
            var d = new GreyboxBlock.Building(DamX0, DamZ0, DamMx, DamMz, DamStoreys) { Y0 = Floor };
            b.Stairwell(d, 1, true, +1);          // south: lane 1, cells 0..2, rising north
            b.Stairwell(d, 8, false, +1);         // north: row 8, cells 1..3, rising east
            for (int s = 0; s < DamStoreys; s++)
            {
                // Galleries: x-lines 1 and 3 run the length of the dam, with archways into the rooms.
                for (int j = 0; j < DamMz; j++)
                {
                    if (d.AlongZ[s, 1, j] == WallKind.None) d.AlongZ[s, 1, j] = j % 2 == 1 ? WallKind.Door : WallKind.Wall;
                    if (d.AlongZ[s, 3, j] == WallKind.None) d.AlongZ[s, 3, j] = j % 2 == 0 ? WallKind.Door : WallKind.Wall;
                }
                // Rooms between the galleries: cross walls on z-lines 3 and 6 (lines 1, 2 are the stair's), a split on x-line 2.
                foreach (int line in new[] { 3, 6 }) for (int i = 1; i <= 2; i++) d.AlongX[s, line, i] = WallKind.Wall;
                for (int j = 3; j <= 7; j++) d.AlongZ[s, 2, j] = j == 4 || j == 7 ? WallKind.Door : WallKind.Wall;
                // The south stair's room: open to the upstream gallery at its foot, the downstream gallery at its top.
                d.AlongZ[s, 1, 0] = WallKind.Door;
                d.AlongZ[s, 2, 2] = WallKind.Door;
                // The north stair row: open off both galleries.
                d.AlongX[s, 8, 0] = WallKind.Door;
                d.AlongX[s, 8, 3] = WallKind.Door;
                d.AlongZ[s, 1, 8] = WallKind.Door;  // into the north stair's foot
                d.AlongZ[s, 1, 1] = WallKind.Wall;  // never a door into the south stair's open middle
                d.AlongX[s, 8, 2] = WallKind.Wall;  // nor into the north stair's
            }
            // The stair lane's own side walls along it.
            for (int s = 0; s < DamStoreys; s++) d.AlongX[s, 3, 1] = WallKind.Wall;

            // Ground floor (canyon level).
            d.AlongZ[0, 4, 0] = WallKind.ShutDoor;   // out to the canyon, south end
            d.AlongZ[0, 4, 8] = WallKind.ShutDoor;   // north end
            d.AlongZ[0, 4, 3] = WallKind.Door;       // archways into the power house
            d.AlongZ[0, 4, 5] = WallKind.Door;
            // The turbine vault: cells (1..2, 6..7) sealed.
            d.AlongZ[0, 1, 7] = WallKind.Wall;
            d.AlongZ[0, 3, 6] = WallKind.Wall;
            d.AlongZ[0, 2, 7] = WallKind.Wall;
            d.AlongX[0, 6, 1] = WallKind.Wall; d.AlongX[0, 6, 2] = WallKind.Wall;
            d.AlongX[0, 8, 1] = WallKind.Wall; d.AlongX[0, 8, 2] = WallKind.Wall;
            // Second storey: one archway into the power house's gallery, windows at the ends.
            d.AlongZ[1, 4, 4] = WallKind.Door;
            d.AlongZ[1, 4, 0] = WallKind.Window;
            d.AlongZ[1, 4, 8] = WallKind.Window;
            // Third storey (the bed's level): in from the reservoir, out onto the power house roof.
            d.AlongZ[2, 0, 1] = WallKind.ShutDoor;
            d.AlongZ[2, 0, 4] = WallKind.ShutDoor;   // from the alley behind the intake tower
            d.AlongZ[2, 0, 7] = WallKind.WideDoor;
            d.AlongZ[2, 4, 3] = WallKind.ShutDoor;
            d.AlongZ[2, 4, 5] = WallKind.ShutDoor;
            foreach (int j in new[] { 0, 1, 7, 8 }) d.AlongZ[2, 4, j] = WallKind.Window;
            d.AlongX[2, 0, 2] = WallKind.Window;     // down the spill chute
            // Fourth and fifth storeys: windows over the canyon; two over the reservoir at the top.
            for (int s = 3; s < DamStoreys; s++)
                for (int j = 0; j < DamMz; j++) if (j % 2 == 0 || s == 4) d.AlongZ[s, 4, j] = WallKind.Window;
            d.AlongZ[4, 0, 2] = WallKind.Window;
            d.AlongZ[4, 0, 6] = WallKind.Window;
            d.AlongX[4, 9, 3] = WallKind.Window;

            b.Style = BrStyle.Concrete;
            b.Variant = 1;
            b.Raise(d, "Greywater Dam");

            // The crest: a parapet down both faces (a gap for the intake tower's bridge), the ends open for the road.
            double road0 = d.Cx(1) + 3, road1 = d.Cx(3) + Col - 3;
            EastBuild.Parapet(b, d, (0, road0, road1), (1, road0, road1), (2, IntakeBridgeZ0, IntakeBridgeZ1));
            // Cover on the crest: two short walls across the road's middle, staggered.
            b.Strip(new Vec3d(d.Cx(1) + Col, Crest, d.Cz(3)), 4.5, true, 1);
            b.Strip(new Vec3d(d.Cx(3) - 4.5, Crest, d.Cz(6)), 4.5, true, 1);

            // Fill the cracks between the dam and the rock: the north end, the upstream face below the bed, the chute's side.
            EastBuild.Fill(b, DamX0, Floor, DamZ1, DamX1, Crest, DamZ1 + 1.5);
            EastBuild.Fill(b, DamX0 - 1.5, Floor, DamZ0, DamX0, Bed, DamZ1);
            b.World.AddRamp(new Vec3d(DamX0, Floor, ChuteZ1), new Vec3d(DamX1, Chute(DamX0), DamZ0), Dir.West, BrushKind.Wall);
            // The road over the chute to the south plateau, with low walls along it.
            double bx0 = d.Cx(2) - 3, bx1 = d.Cx(2) + Col + 3;
            EastBuild.Walkway(b, bx0, GorgeZ0 - 1.5, bx1, DamZ0, Crest);
            b.Strip(new Vec3d(bx0 - Col, Crest, GorgeZ0 - 1.5), DamZ0 - GorgeZ0 + 1.5, false, 1);
            b.Strip(new Vec3d(bx1, Crest, GorgeZ0 - 1.5), DamZ0 - GorgeZ0 + 1.5, false, 1);

            // Loot: the vault, a room on every floor, the control room, the crest.
            b.Chest(d, 1, 6, 0, Math.PI / 2);
            b.Chest(d, 2, 7, 0, -Math.PI / 2);
            b.Chest(d, 2, 4, 0, Math.PI);
            b.Chest(d, 1, 4, 1, 0);
            b.Chest(d, 2, 7, 1, Math.PI);
            b.Chest(d, 1, 5, 2, Math.PI / 2);
            b.Chest(d, 2, 3, 3, 0);
            b.Chest(d, 1, 7, 3, Math.PI);
            b.Chest(d, 2, 5, 4, -Math.PI / 2);
            b.Chest(d, 0, 4, 4, Math.PI / 2);
            b.Chest(new Vec3d(d.Cx(2) + Col / 2, Crest, d.Cz(4) + 3), 0);
            return d;
        }

        // ---------------------------------------------------------------- the intake tower

        /// <summary>The intake tower's bridge to the crest runs over these z.</summary>
        public static readonly double IntakeBridgeZ0 = 780.0, IntakeBridgeZ1 = 786.0;
        private const double IntakeX0 = 1094.4, IntakeZ0 = 774.0;

        /// <summary>
        /// The intake tower: 2 x 3 modules standing on the bed in the throat, three storeys up to the crest's height, a
        /// bridge from its roof to the dam's crest. In by a shut door from the bed (west); windows on the north and south.
        /// </summary>
        private static void Intake(BrBuild b)
        {
            var t = new GreyboxBlock.Building(IntakeX0, IntakeZ0, 2, 3, 3) { Y0 = Bed };
            b.Stairwell(t, 0, true, +1);
            for (int s = 0; s < 3; s++)
                for (int j = 0; j < 3; j++) t.AlongZ[s, 1, j] = j == 1 ? WallKind.Wall : WallKind.Door;
            t.AlongZ[0, 2, 1] = WallKind.ShutDoor;   // the east side, toward the dam (a 3 m alley)
            t.AlongX[0, 0, 1] = WallKind.ShutDoor;   // south
            for (int s = 1; s < 3; s++) { t.AlongX[s, 0, 1] = WallKind.Window; t.AlongX[s, 3, 1] = WallKind.Window; t.AlongZ[s, 2, 0] = WallKind.Window; }
            t.AlongZ[2, 0, 1] = WallKind.Window;
            b.Style = BrStyle.Concrete;
            b.Variant = 2;
            b.Raise(t, "Intake tower");
            double x1 = t.Cx(2) + Col;
            EastBuild.Parapet(b, t, (3, IntakeBridgeZ0, IntakeBridgeZ1));
            EastBuild.Walkway(b, x1, IntakeBridgeZ0, DamX0, IntakeBridgeZ1, Crest);
            b.Strip(new Vec3d(x1, Crest, IntakeBridgeZ0 - Col), DamX0 - x1, true, 1);
            b.Strip(new Vec3d(x1, Crest, IntakeBridgeZ1), DamX0 - x1, true, 1);
            b.Chest(t, 1, 2, 0, Math.PI);
            b.Chest(t, 1, 0, 1, 0);
            b.Chest(t, 1, 2, 2, Math.PI);
        }

        // ---------------------------------------------------------------- the power house

        /// <summary>
        /// The power house: 6 x 5 modules, two storeys from the canyon floor up to the bed's level, against the dam's
        /// downstream face. A double-height turbine hall with a gallery round it, a stair in its east lane, a sealed
        /// office; a wide loading door east onto the yard, shut doors north and south; its roof a terrace you reach
        /// straight out of the dam's third storey.
        /// </summary>
        private static void PowerHouse(BrBuild b)
        {
            var p = new GreyboxBlock.Building(PowerX0, PowerZ0, PowerMx, PowerMz, 2) { Y0 = Floor };
            b.Stairwell(p, 5, true, +1);
            for (int i = 1; i <= 4; i++) for (int j = 1; j <= 3; j++) p.Hole[0, i, j] = true; // the hall, open to the roof's underside
            p.AlongZ[0, 0, 1] = WallKind.Door;       // archways from the dam
            p.AlongZ[0, 0, 3] = WallKind.Door;
            p.AlongZ[1, 0, 2] = WallKind.Door;       // the gallery's archway from the dam
            p.AlongZ[0, 6, 3] = WallKind.WideDoor;   // the loading door
            p.AlongZ[0, 6, 0] = WallKind.ShutDoor;
            p.AlongX[0, 0, 2] = WallKind.ShutDoor;   // south
            p.AlongX[0, 5, 3] = WallKind.ShutDoor;   // north
            for (int i = 0; i < PowerMx; i++) { p.AlongX[1, 0, i] = i % 2 == 0 ? WallKind.Window : WallKind.Wall; p.AlongX[1, 5, i] = i % 2 == 1 ? WallKind.Window : WallKind.Wall; }
            p.AlongZ[1, 6, 0] = WallKind.Window;
            p.AlongZ[1, 6, 4] = WallKind.Window;
            // The stair lane (x 5, cells 0..2) walled from the hall, open at its ends; the sealed office in cells (5, 3..4).
            for (int s = 0; s < 2; s++) { p.AlongZ[s, 5, 0] = WallKind.Door; p.AlongZ[s, 5, 1] = WallKind.Wall; p.AlongZ[s, 5, 2] = WallKind.Door; }
            p.AlongX[0, 3, 5] = WallKind.Wall;
            p.AlongZ[0, 5, 3] = WallKind.Wall;
            p.AlongZ[0, 5, 4] = WallKind.Wall;
            p.AlongZ[0, 6, 3] = WallKind.Wall;       // the loading door moves off the office
            p.AlongZ[0, 6, 2] = WallKind.WideDoor;
            // Generator bays: low half-walls in the hall (cover).
            b.Style = BrStyle.Industrial;
            b.Variant = 1;
            b.Raise(p, "Power house");
            EastBuild.Parapet(b, p, (2, p.Z0, p.Cz(PowerMz) + Col));
            for (int k = 0; k < 3; k++) b.Strip(new Vec3d(p.Cx(1 + k) + 2.4, Floor, p.Cz(2) + 0.6), 3, false, 1);
            b.Chest(p, 5, 4, 0, Math.PI);           // the sealed office
            b.Chest(p, 2, 1, 0, 0);
            b.Chest(p, 3, 3, 0, Math.PI);
            b.Chest(p, 0, 4, 1, Math.PI / 2);
            b.Chest(p, 4, 0, 1, 0);
            b.Chest(p, 1, 2, 2, Math.PI / 2);       // on the roof terrace
        }

        // ---------------------------------------------------------------- the canyon

        /// <summary>
        /// The tailrace yard east of the power house, under the rock walls: the canyon tower (a stair tower from the floor
        /// to the north plateau, its roof bridged onto the clifftop), sealed transformer sheds, low walls to hide from
        /// the rim.
        /// </summary>
        private static void CanyonYard(BrBuild b)
        {
            double tx0 = 1196.1, tz0 = GorgeZ1 - 3 * Md - Col;
            var t = BrKit.TowerPlan(b, tx0, Floor, tz0, 2, 3, DamStoreys);
            t.AlongX[0, 3, 1] = WallKind.Wall;       // the north face is against the rock
            for (int s = 1; s < DamStoreys; s++) t.AlongX[s, 3, 0] = t.AlongX[s, 3, 1] = WallKind.Wall;
            b.Style = BrStyle.Concrete;
            b.Variant = 3;
            b.Raise(t, "Canyon tower");
            double x1 = t.Cx(2) + Col;
            EastBuild.Parapet(b, t, (1, tx0 + 2, x1 - 2));
            EastBuild.Fill(b, tx0, Floor, GorgeZ1, x1, Crest, GorgeZ1 + 1.5);
            for (int s = 0; s < DamStoreys; s += 2) b.Chest(t, 1, s % 4 == 0 ? 0 : 2, s, Math.PI);
            b.Chest(t, 1, 0, DamStoreys, Math.PI);

            // Transformer sheds (sealed, a chest each) and walls in the yard.
            BrKit.Shed(b, 1181.1, Floor, 749.4);
            BrKit.Shed(b, 1196.1, Floor, 749.4);
            BrKit.Shed(b, 1211.1, Floor, 749.4, chest: false);
            b.Strip(new Vec3d(1178.4, Floor, 776.4), 9, true, 1);
            b.Strip(new Vec3d(1194.0, Floor, 768.0), 7.5, false, 1);
            b.Strip(new Vec3d(1206.0, Floor, 780.0), 9, true, 2);
            b.Strip(new Vec3d(1213.5, Floor, 763.5), 6, false, 1);
            b.Chest(new Vec3d(1185.0, Floor, 790.0), Math.PI / 2);
            b.Item(Item.Grenade, 2, new Vec3d(1200.0, Floor, 772.0));
            b.Open(new Rect2(DamX1, GorgeZ0, 1222.8, GorgeZ1));
        }

        // ---------------------------------------------------------------- the plateaus

        /// <summary>
        /// On the south plateau: the dam's control house (two storeys), a sealed valve house and the gatehouse at the
        /// crest road's end; walls along the canyon's edge. On the north plateau: the keeper's lodge and the crane
        /// shed where the crest road and the canyon tower come up. All on the plateau's exact level (no footings near the
        /// gorge, whose rock walls a footing's bank would cut back).
        /// </summary>
        private static void Plateaus(BrBuild b, GreyboxBlock.Building dam)
        {
            b.Style = BrStyle.Concrete;
            b.Variant = 4;
            // South plateau (z 715 .. 744.3).
            var ctl = new GreyboxBlock.Building(1095.0, 718.2, 4, 2, 2) { Y0 = Crest };
            b.Stairwell(ctl, 1, false, +1);
            ctl.AlongX[0, 0, 0] = WallKind.ShutDoor;   // south
            ctl.AlongZ[0, 0, 1] = WallKind.ShutDoor;   // west
            ctl.AlongX[0, 2, 0] = WallKind.Window;
            ctl.AlongX[1, 2, 0] = WallKind.Window;
            ctl.AlongX[1, 2, 2] = WallKind.Window;
            ctl.AlongZ[1, 3, 0] = WallKind.Window;
            ctl.AlongZ[1, 0, 0] = WallKind.Window;
            b.Raise(ctl, "Dam control house");
            BrKit.Parapet(b, ctl);
            b.Chest(ctl, 0, 1, 0, 0);
            b.Chest(ctl, 2, 0, 1, Math.PI);

            BrKit.Shed(b, 1140.0, Crest, 724.5);       // the sealed valve house
            var gate = new GreyboxBlock.Building(1155.6, 724.5, 2, 1, 1) { Y0 = Crest };
            gate.AlongX[0, 1, 0] = WallKind.ShutDoor;
            gate.AlongZ[0, 2, 0] = WallKind.Window;
            b.Raise(gate, "Gatehouse");
            b.Chest(gate, 1, 0, 0, Math.PI);
            // Walls along the canyon's south edge (cover from the dam's windows and the far rim).
            b.Strip(new Vec3d(1140.0, Crest, GorgeZ0 - 4.5), 12, true, 1);
            b.Strip(new Vec3d(1170.0, Crest, GorgeZ0 - 3.0), 13.5, true, 1);
            b.Strip(new Vec3d(1196.0, Crest, GorgeZ0 - 4.5), 12, true, 1);

            // North plateau (z 813.3 .. 830.4).
            var lodge = new GreyboxBlock.Building(1146.0, 816.0, 2, 2, 1) { Y0 = Crest };
            lodge.AlongX[0, 0, 0] = WallKind.ShutDoor;
            lodge.AlongZ[0, 0, 1] = WallKind.Window;
            lodge.AlongZ[0, 1, 1] = WallKind.Wall;    // a sealed back room
            lodge.AlongX[0, 1, 1] = WallKind.Wall;
            b.Style = BrStyle.Timber;
            b.Raise(lodge, "Keeper's lodge");
            b.Chest(lodge, 0, 1, 0, Math.PI);
            b.Chest(lodge, 1, 1, 0, Math.PI);

            b.Style = BrStyle.Industrial;
            var crane = new GreyboxBlock.Building(1170.0, 816.0, 3, 2, 1) { Y0 = Crest };
            crane.AlongX[0, 0, 1] = WallKind.WideDoor;
            crane.AlongZ[0, 3, 1] = WallKind.ShutDoor;
            crane.AlongZ[0, 1, 1] = WallKind.Wall;
            crane.AlongZ[0, 1, 0] = WallKind.Wall;
            crane.AlongX[0, 1, 0] = WallKind.Wall;     // a sealed store in its west end
            b.Raise(crane, "Crane shed");
            b.Chest(crane, 0, 0, 0, 0);
            b.Chest(crane, 2, 1, 0, Math.PI);
            b.Strip(new Vec3d(1126.0, Crest, GorgeZ1 + 4.5), 10.5, true, 1);
            b.Strip(new Vec3d(1200.0, Crest, GorgeZ1 + 6.0), 9, true, 1);
            b.Item(Item.C4, 2, new Vec3d(1140.0, Crest, GorgeZ1 + 9.0));
            b.Style = BrStyle.Brick;
            b.Variant = 0;
        }
    }
}
