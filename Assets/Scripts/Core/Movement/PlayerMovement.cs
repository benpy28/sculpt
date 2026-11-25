using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Deterministic first-person movement: an axis-aligned box against the voxel grid.
    /// Pure function of (grid, config, state, input), so client prediction and server replay agree.
    /// </summary>
    public static class PlayerMovement
    {
        private const double Bs = SubGrid.TileSize;
        private const double Sub = SubGrid.SubSize;

        public static double HeightOf(in MotorState s, MovementConfig c) => s.Crouched ? c.CrouchHeight : c.StandHeight;
        public static double EyeOf(in MotorState s, MovementConfig c) => s.Crouched ? c.CrouchEye : c.StandEye;

        public static Aabb Box(Vec3d feet, double halfWidth, double height) => new Aabb(
            new Vec3d(feet.X - halfWidth, feet.Y, feet.Z - halfWidth),
            new Vec3d(feet.X + halfWidth, feet.Y + height, feet.Z + halfWidth));

        private static bool Blocked(ITerrain g, MovementConfig c, Vec3d feet, double height) =>
            g.BoxOverlapsSolid(Box(feet, c.HalfWidth, height));

        /// <summary>Copies the motor's box into the shared player body used by sculpting and hit checks.</summary>
        public static void WriteBody(in MotorState s, MovementConfig c, PlayerBody body)
        {
            body.Position = s.Position;
            body.Velocity = s.Velocity;
            body.HalfWidth = c.HalfWidth;
            body.Height = HeightOf(s, c);
            body.EyeHeight = EyeOf(s, c);
        }

        public static Vec3d Forward(double yaw) => new Vec3d(Math.Sin(yaw), 0, Math.Cos(yaw));
        public static Vec3d Right(double yaw) => new Vec3d(Math.Cos(yaw), 0, -Math.Sin(yaw));

        /// <summary>World-space wish direction from input, length 0..1.</summary>
        public static Vec3d WishDir(in MoveInput i)
        {
            Vec3d w = Forward(i.Yaw) * i.MoveZ + Right(i.Yaw) * i.MoveX;
            double l = w.Length;
            return l > 1 ? w / l : w;
        }

        // =====================================================================

        public static void Step(ITerrain g, MovementConfig c, ref MotorState s, in MoveInput input, List<MoveEvent> events = null)
        {
            s.Tick++;
            if (input.JumpPressed) s.JumpBufferedTick = s.Tick;
            bool wasSprinting = s.Sprinting;

            Depenetrate(g, c, ref s);

            if (s.Mode == MoveMode.Vault) { StepVault(g, c, ref s, events); FinishTick(c, ref s, wasSprinting); return; }
            if (s.Mode == MoveMode.Mantle) { StepMantle(g, c, ref s, input, events); FinishTick(c, ref s, wasSprinting); return; }

            Vec3d wish = WishDir(input);
            bool grounded = ProbeGround(g, c, s.Position, HeightOf(s, c), out Vec3d normal);
            s.GroundNormal = grounded ? normal : new Vec3d(0, 1, 0);

            // ---- Mode transitions from contact ----
            // Landing: moving down, or (on an uphill slope) no longer rising faster than the slope under you.
            if (s.Mode == MoveMode.Air && grounded && s.Velocity.Y <= Math.Max(0, WithTangentY(Horizontal(s.Velocity), normal).Y))
                Land(c, ref s, input, events);
            else if ((s.Mode == MoveMode.Ground || s.Mode == MoveMode.Slide) && !grounded)
            {
                s.Mode = MoveMode.Air;
                s.AirPeakY = s.Position.Y;
            }
            s.Grounded = grounded && s.Mode != MoveMode.Air;
            if (s.Grounded)
            {
                s.LastGroundedTick = s.Tick;
                s.WallKicks = 0;
                s.LastWallKickNormal = Vec3d.Zero;
            }

            // Late slide-land: crouch pressed within the window after touchdown restores the landing speed.
            if (s.Mode == MoveMode.Ground && input.CrouchPressed && !s.LandSlideUsed &&
                s.Tick - s.LandTick <= c.SlideLandWindowTicks && s.LandVelocity.HorizontalLength >= c.SlideLandMinSpeed)
            {
                double keep = IsChainedHop(c, s, s.LandTick) ? c.SlideHopKeep : 1.0;
                s.Velocity = new Vec3d(s.LandVelocity.X * keep, s.Velocity.Y, s.LandVelocity.Z * keep);
                StartSlide(g, c, ref s, false, events);
                s.LandSlideUsed = true;
                Emit(events, MoveEventType.SlideLandSuccess, s.Tick);
            }

            bool jumpBuffered = s.Tick - s.JumpBufferedTick <= c.JumpBufferTicks;

            switch (s.Mode)
            {
                case MoveMode.Ground: StepGround(g, c, ref s, input, wish, jumpBuffered, events); break;
                case MoveMode.Slide: StepSlide(g, c, ref s, input, wish, jumpBuffered, events); break;
                case MoveMode.Air: StepAir(g, c, ref s, input, wish, jumpBuffered, events); break;
            }

            if (s.Mode == MoveMode.Vault || s.Mode == MoveMode.Mantle) { FinishTick(c, ref s, wasSprinting); return; }

            Integrate(g, c, ref s);
            FinishTick(c, ref s, wasSprinting);
        }

        private static void FinishTick(MovementConfig c, ref MotorState s, bool wasSprinting)
        {
            if (wasSprinting && !s.Sprinting) s.ShootReadyTick = Math.Max(s.ShootReadyTick, s.Tick + c.SprintToAimTicks);
            if (s.Mode == MoveMode.Air) s.AirPeakY = Math.Max(s.AirPeakY, s.Position.Y);
        }

        // ---- Ground ----

        private static void StepGround(ITerrain g, MovementConfig c, ref MotorState s, in MoveInput input, Vec3d wish, bool jumpBuffered, List<MoveEvent> events)
        {
            // Slide from a sprint.
            if (input.CrouchPressed && s.Sprinting && s.HorizontalSpeed >= c.SlideMinStartSpeed)
            {
                StartSlide(g, c, ref s, true, events);
                StepSlide(g, c, ref s, input, wish, jumpBuffered, events);
                return;
            }

            SetCrouched(g, c, ref s, input.CrouchHeld);
            s.Sprinting = input.SprintHeld && !input.WantsToShoot && !s.Crouched && input.MoveZ > 0.1;

            double target = s.Crouched ? c.CrouchSpeed : (s.Sprinting ? c.SprintSpeed : c.RunSpeed);
            if (s.Hand.HeldItem == Item.Launcher && !s.Crouched) target *= c.Items.Launcher.MoveSpeedScale; // heavy: a little slower on foot
            Vec3d vh = Horizontal(s.Velocity);
            vh = CounterBrake(c, ref s, vh, wish, events);
            vh = GroundAccelerate(c, vh, wish, target);

            if (jumpBuffered)
            {
                if (TryMantle(g, c, ref s, input.Yaw, false, events)) return;
                Jump(c, ref s, vh, events);
                return;
            }

            s.Velocity = WithTangentY(vh, s.GroundNormal);

            if (s.Sprinting && vh.Length >= c.VaultMinSpeed) TryVault(g, c, ref s, events);
        }

        /// <summary>
        /// Pressing back or sideways against your motion at speed brakes hard, down to CounterBrakeMinSpeed. It only
        /// removes speed along the current direction; turning is still done by normal acceleration or slide steering.
        /// </summary>
        private static Vec3d CounterBrake(MovementConfig c, ref MotorState s, Vec3d vh, Vec3d wish, List<MoveEvent> events)
        {
            double speed = vh.Length;
            bool brake = speed > c.CounterBrakeMinSpeed && wish.Length > 0.1 &&
                         wish.Normalized.Dot(vh / speed) <= c.CounterBrakeMaxDot;
            if (!brake)
            {
                s.Braking = false;
                return vh;
            }
            if (!s.Braking) Emit(events, MoveEventType.Braked, s.Tick, 0, speed);
            s.Braking = true;
            double newSpeed = Math.Max(c.CounterBrakeMinSpeed, speed - c.CounterBrakeDecel * c.Dt);
            return vh * (newSpeed / speed);
        }

        private static Vec3d GroundAccelerate(MovementConfig c, Vec3d vh, Vec3d wish, double targetSpeed)
        {
            double dt = c.Dt;
            double speed = vh.Length;
            if (wish.LengthSquared < 1e-9) return MoveTowards(vh, Vec3d.Zero, Math.Max(c.GroundDecel, speed > targetSpeed ? c.OverspeedDecel : 0) * dt);
            double wantSpeed = targetSpeed * wish.Length;
            if (speed > wantSpeed + 1e-6)
            {
                // Bleed excess speed, but keep full steering authority at the current speed.
                double newSpeed = Math.Max(wantSpeed, speed - c.OverspeedDecel * dt);
                Vec3d dirTarget = wish.Normalized * newSpeed;
                Vec3d scaled = vh * (newSpeed / speed);
                return MoveTowards(scaled, dirTarget, c.GroundAccel * dt);
            }
            return MoveTowards(vh, wish.Normalized * wantSpeed, c.GroundAccel * dt);
        }

        private static void Jump(MovementConfig c, ref MotorState s, Vec3d vh, List<MoveEvent> events)
        {
            double tangentY = Math.Max(0, WithTangentY(vh, s.GroundNormal).Y);
            s.Velocity = new Vec3d(vh.X, c.JumpSpeed + tangentY, vh.Z);
            s.Mode = MoveMode.Air;
            s.Grounded = false;
            s.AirPeakY = s.Position.Y;
            s.JumpBufferedTick = int.MinValue / 2;
            s.LastGroundedTick = int.MinValue / 2; // no coyote double jump
            Emit(events, MoveEventType.Jumped, s.Tick);
        }

        // ---- Slide ----

        private static void StartSlide(ITerrain g, MovementConfig c, ref MotorState s, bool allowBoost, List<MoveEvent> events)
        {
            s.Mode = MoveMode.Slide;
            s.Sprinting = false;
            s.Crouched = true;
            Vec3d vh = Horizontal(s.Velocity);
            bool downhill = IsDownhill(vh, s.GroundNormal);
            bool boosted = false;
            if (allowBoost && (downhill || s.Tick - s.LastSlideBoostTick >= c.SlideBoostCooldownTicks))
            {
                double sp = vh.Length;
                if (sp < c.SlideBoostSpeed && sp > 1e-6)
                {
                    vh = vh * (c.SlideBoostSpeed / sp);
                    boosted = true;
                }
                if (!downhill) s.LastSlideBoostTick = s.Tick;
            }
            double speed = vh.Length;
            if (speed > c.SlideMaxSpeed) vh = vh * (c.SlideMaxSpeed / speed);
            s.Velocity = new Vec3d(vh.X, s.Velocity.Y, vh.Z);
            Emit(events, MoveEventType.SlideStarted, s.Tick, boosted ? 1 : 0);
        }

        private static void StepSlide(ITerrain g, MovementConfig c, ref MotorState s, in MoveInput input, Vec3d wish, bool jumpBuffered, List<MoveEvent> events)
        {
            double dt = c.Dt;
            Vec3d vh = Horizontal(s.Velocity);

            if (jumpBuffered)
            {
                SetCrouched(g, c, ref s, false);
                Jump(c, ref s, vh, events);
                s.LastSlideJumpTick = s.Tick;
                Emit(events, MoveEventType.SlideJump, s.Tick);
                return;
            }

            if (!input.CrouchHeld)
            {
                SetCrouched(g, c, ref s, false);
                s.Mode = MoveMode.Ground;
                s.Velocity = WithTangentY(vh, s.GroundNormal);
                return;
            }

            // Gravity along the slope (horizontal part), then friction, then capped speed.
            Vec3d n = s.GroundNormal;
            vh += new Vec3d(n.X, 0, n.Z) * (c.Gravity * n.Y * dt);
            double speed = vh.Length;
            if (speed > 1e-9)
            {
                double newSpeed = Math.Max(0, Math.Min(c.SlideMaxSpeed, speed - c.SlideFriction * dt));
                vh = vh * (newSpeed / speed);
                speed = newSpeed;
            }

            if (speed > 1e-9)
            {
                vh = CounterBrake(c, ref s, vh, wish, events);
                speed = vh.Length;
            }

            // Limited steering toward the wish direction.
            if (speed > 1e-6 && wish.LengthSquared > 1e-9) vh = RotateToward(vh, wish, c.SlideTurnRate * dt);

            bool downhill = IsDownhill(vh, n);
            if (speed < c.SlideEndSpeed && !downhill)
            {
                s.Mode = MoveMode.Ground;
                s.Velocity = WithTangentY(vh, n);
                return;
            }

            s.Velocity = WithTangentY(vh, n);
            if (speed >= c.VaultMinSpeed) TryVault(g, c, ref s, events);
        }

        // ---- Air ----

        private static void StepAir(ITerrain g, MovementConfig c, ref MotorState s, in MoveInput input, Vec3d wish, bool jumpBuffered, List<MoveEvent> events)
        {
            double dt = c.Dt;
            Vec3d vh = Horizontal(s.Velocity);

            s.Braking = false;
            if (jumpBuffered)
            {
                if (TryMantle(g, c, ref s, input.Yaw, true, events)) return;
                if (s.Tick - s.LastGroundedTick <= c.CoyoteTicks)
                {
                    Jump(c, ref s, vh, events);
                    return;
                }
                if (TryWallKick(g, c, ref s, input.Yaw, events)) return;
            }

            double speed = vh.Length;
            if (wish.LengthSquared > 1e-9)
            {
                if (speed <= c.RunSpeed)
                {
                    // Slow: plain limited air control (a standing jump can still be steered onto a ledge).
                    Vec3d target = wish.Normalized * c.RunSpeed * wish.Length;
                    vh = MoveTowards(vh, target, c.GroundAccel * c.AirAccelFraction * dt);
                    if (vh.Length > c.RunSpeed && vh.Length > speed) vh = vh * (Math.Max(speed, c.RunSpeed) / vh.Length);
                }
                else
                {
                    // Fast: only a small heading nudge toward the wish direction, and a light brake against it.
                    // Never adds speed.
                    Vec3d w = wish.Normalized;
                    double along = w.Dot(vh / speed);
                    if (along < -0.05)
                    {
                        double ns = Math.Max(c.RunSpeed, speed - c.AirBrakeDecel * (-along) * wish.Length * dt);
                        vh = vh * (ns / speed);
                    }
                    else vh = RotateToward(vh, w, c.AirNudgeTurnRate * wish.Length * dt);
                }
            }
            speed = vh.Length;
            if (speed > c.AirSoftCapSpeed)
                vh = vh * (Math.Max(c.AirSoftCapSpeed, speed - c.AirSoftCapDecel * dt) / speed);
            s.Velocity = new Vec3d(vh.X, s.Velocity.Y, vh.Z); // gravity is applied exactly in Integrate
        }

        // ---- Wall kick ----

        /// <summary>The outward normal of a wall the box is touching on one side (none: zero).</summary>
        private static Vec3d TouchingWall(ITerrain g, MovementConfig c, in MotorState s, double h)
        {
            Vec3d best = Vec3d.Zero;
            double bestInto = double.NegativeInfinity;
            for (int k = 0; k < 4; k++)
            {
                Vec3d d = k == 0 ? new Vec3d(1, 0, 0) : k == 1 ? new Vec3d(-1, 0, 0) : k == 2 ? new Vec3d(0, 0, 1) : new Vec3d(0, 0, -1);
                if (!Blocked(g, c, s.Position + d * c.WallKickProbe, h)) continue;
                // A wall, not a step: still blocked a step higher.
                if (!Blocked(g, c, s.Position + d * c.WallKickProbe + new Vec3d(0, c.MaxStepUp + 0.01, 0), h)) continue;
                double into = d.Dot(s.Velocity) + (s.WallHitNormal.Equals(-d) ? 100 : 0); // prefer the wall just hit
                if (into > bestInto) { bestInto = into; best = -d; }
            }
            return best;
        }

        private static bool TryWallKick(ITerrain g, MovementConfig c, ref MotorState s, double yaw, List<MoveEvent> events)
        {
            if (s.WallKicks >= c.WallKickMaxPerAir) return false;
            if (s.Tick - s.LastWallKickTick < c.WallKickCooldownTicks) return false;
            double h = HeightOf(s, c);
            Vec3d n = TouchingWall(g, c, s, h);
            if (n.LengthSquared < 0.5) return false;
            if (n.Equals(s.LastWallKickNormal)) return false; // not the same wall twice in a row

            double speed = Horizontal(s.Velocity).Length;
            bool perfect = false;
            if (s.WallHitNormal.Equals(n) && s.Tick - s.WallHitTick <= c.WallKickGraceTicks)
            {
                perfect = s.Tick - s.WallHitTick <= c.WallKickPerfectTicks;
                speed = Math.Max(speed, s.WallHitSpeed * (perfect ? 1.0 : c.WallKickLateKeep));
            }
            speed = Math.Max(speed, c.WallKickMinSpeed);

            Vec3d look = Forward(yaw);
            double into = look.Dot(n);
            if (into < 0) look = look - n * (2 * into); // looking into the wall: mirror off it
            Vec3d dir = (look * speed + n * c.WallKickPush).Normalized;
            Vec3d vh = dir * speed;
            s.Velocity = new Vec3d(vh.X, Math.Max(s.Velocity.Y, c.WallKickUpSpeed), vh.Z);
            s.WallKicks++;
            s.LastWallKickTick = s.Tick;
            s.LastWallKickNormal = n;
            s.WallHitTick = int.MinValue / 2;
            s.JumpBufferedTick = int.MinValue / 2;
            s.AirPeakY = s.Position.Y;
            Emit(events, MoveEventType.WallKick, s.Tick, perfect ? 1 : 0, speed);
            return true;
        }

        private static void Land(MovementConfig c, ref MotorState s, in MoveInput input, List<MoveEvent> events)
        {
            double levels = Math.Max(0, (s.AirPeakY - s.Position.Y) / Bs);
            int damage = (int)Math.Floor(Math.Max(0, levels - c.FallDamageFreeLevels) * c.FallDamagePerLevel);
            Emit(events, MoveEventType.Landed, s.Tick, damage, levels);

            Vec3d vh = Horizontal(s.Velocity);
            s.LandTick = s.Tick;
            s.LandVelocity = vh;
            s.LandSlideUsed = false;
            s.Mode = MoveMode.Ground;
            s.Velocity = WithTangentY(vh, s.GroundNormal);

            if (input.CrouchHeld && vh.Length >= c.SlideLandMinSpeed)
            {
                s.LandSlideUsed = true;
                s.Mode = MoveMode.Slide;
                s.Crouched = true;
                s.Sprinting = false;
                double sp = vh.Length;
                if (IsChainedHop(c, s, s.Tick)) { s.Velocity = s.Velocity * c.SlideHopKeep; sp *= c.SlideHopKeep; }
                if (sp > c.SlideMaxSpeed) s.Velocity = s.Velocity * (c.SlideMaxSpeed / sp);
                Emit(events, MoveEventType.SlideStarted, s.Tick, 0);
                Emit(events, MoveEventType.SlideLandSuccess, s.Tick);
            }
        }

        /// <summary>A slide-land soon after a slide-jump, with no wall kick in between (a kick is its own skill).</summary>
        private static bool IsChainedHop(MovementConfig c, in MotorState s, int landTick) =>
            landTick - s.LastSlideJumpTick <= c.SlideHopChainTicks && s.LastWallKickTick < s.LastSlideJumpTick;

        // ---- Vault ----

        private static bool TryVault(ITerrain g, MovementConfig c, ref MotorState s, List<MoveEvent> events)
        {
            Vec3d vh = Horizontal(s.Velocity);
            double speed = vh.Length;
            if (speed < c.VaultMinSpeed) return false;
            Vec3d dir = vh / speed;
            double h = HeightOf(s, c);
            Vec3d pos = s.Position;

            if (!Blocked(g, c, pos + dir * (speed * c.Dt + 0.02), h)) return false; // nothing ahead
            if (!Blocked(g, c, pos + dir * (speed * c.Dt + 0.02 + c.FilletIgnore) + new Vec3d(0, 2 * c.MaxStepUp, 0), h)) return false; // a slope or step: just walk up it

            // Ledge tops sit on the sub-cell lattice. Try each one above a step-up and up to one level.
            double targetY = double.NaN;
            for (double top = Math.Ceiling((pos.Y + c.MaxStepUp + 1e-6) / Sub) * Sub; top <= pos.Y + c.VaultMaxHeight; top += Sub)
                if (LedgeClear(g, c, pos, dir, top + 0.01, h)) { targetY = top + 0.01; break; }
            if (double.IsNaN(targetY)) return false;

            s.Mode = MoveMode.Vault;
            s.ActionTick = 0;
            s.ActionStartY = pos.Y;
            s.ActionTargetY = targetY;
            s.ActionForward = dir;
            s.ActionKeepVelocity = vh;
            Emit(events, MoveEventType.Vaulted, s.Tick);
            StepVault(g, c, ref s, events);
            return true;
        }

        private static void StepVault(ITerrain g, MovementConfig c, ref MotorState s, List<MoveEvent> events)
        {
            s.ActionTick++;
            double h = HeightOf(s, c);
            double t = Math.Min(1.0, s.ActionTick / (double)c.VaultTicks);
            double wantY = s.ActionStartY + (s.ActionTargetY - s.ActionStartY) * t;
            MoveAxis(g, c, ref s.Position, 1, wantY - s.Position.Y, h, out _);
            Vec3d d = s.ActionKeepVelocity * c.Dt;
            MoveAxis(g, c, ref s.Position, 0, d.X, h, out _);
            MoveAxis(g, c, ref s.Position, 2, d.Z, h, out _);
            s.Velocity = new Vec3d(s.ActionKeepVelocity.X, 0, s.ActionKeepVelocity.Z);
            if (s.ActionTick >= c.VaultTicks)
            {
                s.Mode = MoveMode.Air;
                s.AirPeakY = s.Position.Y;
            }
        }

        // ---- Mantle ----

        private static bool TryMantle(ITerrain g, MovementConfig c, ref MotorState s, double yaw, bool airborne, List<MoveEvent> events)
        {
            Vec3d dir = Forward(yaw);
            double h = HeightOf(s, c);
            Vec3d pos = s.Position;
            if (!Blocked(g, c, pos + dir * 0.1, h)) return false; // must be touching a wall in front

            double minH = airborne ? c.AirMantleMinHeight : c.JumpApex - 0.05;
            double first = Math.Ceiling((pos.Y + minH) / Sub) * Sub;
            for (double top = first; top <= pos.Y + c.MantleMaxHeight + 1e-9; top += Sub)
            {
                double targetY = top + 0.01;
                if (!LedgeClear(g, c, pos, dir, targetY, h)) continue;
                // The climb path straight up must be clear too.
                bool pathClear = true;
                for (double y = pos.Y + 0.3; y < targetY; y += 0.3)
                    if (Blocked(g, c, new Vec3d(pos.X, y, pos.Z), h)) { pathClear = false; break; }
                if (!pathClear) continue;

                s.Mode = MoveMode.Mantle;
                s.ActionTick = 0;
                s.ActionStartY = pos.Y;
                s.ActionTargetY = targetY;
                s.ActionForward = dir;
                // Momentum: remember how fast you hit the ledge (an air hit already stopped you against the wall).
                double hitSpeed = Horizontal(s.Velocity).Length;
                if (s.Tick - s.WallHitTick <= c.WallKickGraceTicks) hitSpeed = Math.Max(hitSpeed, s.WallHitSpeed);
                s.ActionKeepVelocity = dir * hitSpeed;
                s.Velocity = Vec3d.Zero;
                s.Sprinting = false;
                s.JumpBufferedTick = int.MinValue / 2;
                s.ShootReadyTick = Math.Max(s.ShootReadyTick, s.Tick + c.MantleRiseTicks + c.MantleForwardTicks);
                Emit(events, MoveEventType.MantleStarted, s.Tick);
                return true;
            }
            return false;
        }

        private static double MantleExitSpeed(MovementConfig c, in MotorState s) =>
            Math.Max(c.RunSpeed * c.MantleExitSpeedFraction, s.ActionKeepVelocity.Length * c.MantleKeepSpeedFraction);

        private static void StepMantle(ITerrain g, MovementConfig c, ref MotorState s, in MoveInput input, List<MoveEvent> events)
        {
            s.ActionTick++;
            double h = HeightOf(s, c);
            s.Velocity = Vec3d.Zero;

            // Mantle cancel: jump again near the top of the rise to pop over the ledge with the speed you hit it
            // with, instead of the slow climb and the step forward. The last few centimetres are skipped in one tick
            // (at most MantleCancelMaxRemaining, a small pop), then you leave forward with a little hop.
            double remaining = s.ActionTargetY - s.Position.Y;
            if (input.JumpPressed && s.ActionTick >= c.MantleCancelFromTick && s.ActionTick <= c.MantleRiseTicks &&
                remaining <= c.MantleCancelMaxRemaining)
            {
                MoveAxis(g, c, ref s.Position, 1, Math.Max(0, remaining), h, out bool blockedUp);
                if (!blockedUp)
                {
                    Vec3d v = s.ActionForward * MantleExitSpeed(c, s);
                    s.Velocity = new Vec3d(v.X, c.MantleCancelHopSpeed, v.Z);
                    s.Mode = MoveMode.Air;
                    s.AirPeakY = s.Position.Y;
                    s.JumpBufferedTick = int.MinValue / 2;
                    Emit(events, MoveEventType.MantleCancel, s.Tick, 0, v.Length);
                    Integrate(g, c, ref s);
                    return;
                }
            }

            if (s.ActionTick <= c.MantleRiseTicks)
            {
                double t = s.ActionTick / (double)c.MantleRiseTicks;
                double wantY = s.ActionStartY + (s.ActionTargetY - s.ActionStartY) * t;
                MoveAxis(g, c, ref s.Position, 1, wantY - s.Position.Y, h, out bool blocked);
                if (blocked)
                {
                    // Something (a sculpt) closed the path: let go cleanly.
                    s.Mode = MoveMode.Air;
                    s.AirPeakY = s.Position.Y;
                }
                return;
            }

            Vec3d step = s.ActionForward * (c.MantleForwardDistance / c.MantleForwardTicks);
            MoveAxis(g, c, ref s.Position, 0, step.X, h, out _);
            MoveAxis(g, c, ref s.Position, 2, step.Z, h, out _);
            if (s.ActionTick >= c.MantleRiseTicks + c.MantleForwardTicks)
            {
                s.Mode = MoveMode.Air; // lands on the ledge next tick
                s.AirPeakY = s.Position.Y;
                s.Velocity = s.ActionForward * MantleExitSpeed(c, s);
                Emit(events, MoveEventType.MantleFinished, s.Tick);
            }
        }

        /// <summary>Room to stand at targetY both here and a short step forward, with something to stand on ahead.</summary>
        private static bool LedgeClear(ITerrain g, MovementConfig c, Vec3d pos, Vec3d dir, double targetY, double h)
        {
            Vec3d here = new Vec3d(pos.X, targetY, pos.Z);
            Vec3d ahead = here + dir * c.MantleForwardDistance;
            if (Blocked(g, c, here, h)) return false;
            if (Blocked(g, c, ahead, h)) return false;
            return Blocked(g, c, ahead - new Vec3d(0, 0.06, 0), h);
        }

        // ---- Integration and collision ----

        private static void Integrate(ITerrain g, MovementConfig c, ref MotorState s)
        {
            double h = HeightOf(s, c);
            Vec3d d = s.Velocity * c.Dt;
            bool groundMode = s.Mode == MoveMode.Ground || s.Mode == MoveMode.Slide;

            if (groundMode)
            {
                // Move along the ground: horizontal with step-up (climbs ramp slopes, never full blocks), then stick down.
                SlipOffFillet(g, c, ref s, h);
                double startY = s.Position.Y;
                s.UphillTicks = s.Velocity.Y > c.RampLaunchMinRiseSpeed ? Math.Min(s.UphillTicks + 1, 1000) : 0;
                MoveHorizontalWithStep(g, c, ref s, 0, d.X, h);
                MoveHorizontalWithStep(g, c, ref s, 2, d.Z, h);

                // Ramp launch: after a sustained climb, if the ground ahead falls away (the 45 degree chamfer on a
                // ramp's crest, or a plain drop), leave it with the full velocity including the climb's vertical part.
                // Plateaus (flat ground ahead at the same height) and short bumps keep you on the ground.
                if (s.UphillTicks >= c.RampLaunchMinTicks && s.Position.Y - startY < s.Velocity.Y * c.Dt - 0.02)
                {
                    Vec3d dirH = Horizontal(s.Velocity).Normalized;
                    // Look where the front of the feet will be next tick.
                    double half = Math.Max(c.RampLaunchProbeHalf, Horizontal(d).Length * 0.5);
                    Aabb front = Box(s.Position + dirH * (c.HalfWidth + half), half, h);
                    if (!g.GroundContact(front, c.RampLaunchProbeDepth, out _))
                    {
                        s.Mode = MoveMode.Air;
                        s.Grounded = false;
                        s.AirPeakY = s.Position.Y;
                        s.UphillTicks = 0;
                        return;
                    }
                }

                double snap = c.MaxStepUp + Horizontal(d).Length + (s.Position.Y - startY);
                if (ProbeDown(g, c, s.Position, h, snap, out double drop))
                    s.Position = new Vec3d(s.Position.X, s.Position.Y - drop, s.Position.Z);
                // If no ground within snap distance we leave it: next tick goes airborne with the current
                // velocity (including the slope's vertical part), which is the ramp launch.
            }
            else
            {
                s.UphillTicks = 0;
                // Exact constant-gravity step: the apex matches the configured jump height.
                double vy0 = s.Velocity.Y;
                double vy1 = Math.Max(-c.MaxFallSpeed, vy0 - c.Gravity * c.Dt);
                d = new Vec3d(d.X, (vy0 + vy1) * 0.5 * c.Dt, d.Z);
                s.Velocity = new Vec3d(s.Velocity.X, vy1, s.Velocity.Z);
                double hBefore = Horizontal(s.Velocity).Length;
                AirMoveHorizontal(g, c, ref s.Position, 0, d.X, h, out bool bx);
                if (bx)
                {
                    RecordWallHit(ref s, new Vec3d(-Math.Sign(d.X), 0, 0), hBefore);
                    s.Velocity = new Vec3d(0, s.Velocity.Y, s.Velocity.Z);
                }
                AirMoveHorizontal(g, c, ref s.Position, 2, d.Z, h, out bool bz);
                if (bz)
                {
                    RecordWallHit(ref s, new Vec3d(0, 0, -Math.Sign(d.Z)), hBefore);
                    s.Velocity = new Vec3d(s.Velocity.X, s.Velocity.Y, 0);
                }
                MoveAxis(g, c, ref s.Position, 1, d.Y, h, out bool by);
                if (by) s.Velocity = new Vec3d(s.Velocity.X, 0, s.Velocity.Z);
            }
        }

        /// <summary>
        /// A horizontal move in the air. Running into a walkable slope (stairs, a ramp) from the air is not a wall:
        /// the move rides up onto the slope (at most a step) and keeps its speed, so jumping onto, up or along stairs
        /// never stops you dead. Only slopes count; walls and flat ledges still block (vault, mantle and wall kicks
        /// are unchanged).
        /// </summary>
        private static double AirMoveHorizontal(ITerrain g, MovementConfig c, ref Vec3d pos, int axis, double delta, double h, out bool blocked)
        {
            Vec3d start = pos;
            double moved = MoveAxis(g, c, ref pos, axis, delta, h, out blocked);
            if (!blocked) return moved;
            Vec3d alt = start;
            double lift = MoveAxis(g, c, ref alt, 1, c.MaxStepUp, h, out _);
            if (lift < 1e-6) return moved;
            double altMoved = MoveAxis(g, c, ref alt, axis, delta, h, out bool altBlocked);
            if (Math.Abs(altMoved) <= Math.Abs(moved) + 1e-6) return moved;
            MoveAxis(g, c, ref alt, 1, -lift, h, out bool landed);
            if (!landed) return moved; // nothing under the raised path: it went over a ledge, not up a slope
            if (!g.GroundContact(Box(alt, c.HalfWidth, h), c.GroundProbe, out Vec3d n) || n.Y <= 0.5 || n.Y >= 0.9999) return moved;
            pos = alt;
            blocked = altBlocked;
            return altMoved;
        }

        /// <summary>Remembers the speed you hit a wall with in the air (wall kick timing, mantle momentum).</summary>
        private static void RecordWallHit(ref MotorState s, Vec3d normal, double speed)
        {
            // Still against the same wall: keep the first hit (the speed before it stopped you, and when).
            if (s.WallHitNormal.Equals(normal) && s.Tick - s.WallHitTick <= 30 * SimTime.Per60) return;
            s.WallHitTick = s.Tick;
            s.WallHitSpeed = speed;
            s.WallHitNormal = normal;
        }

        /// <summary>
        /// Standing on the 0.15 m fillet at the foot of a wall (a slope that runs straight into the wall, e.g. after
        /// landing against it): step off it back onto the floor, so walls never hold you up off the ground.
        /// </summary>
        private static void SlipOffFillet(ITerrain g, MovementConfig c, ref MotorState s, double h)
        {
            if (s.Velocity.HorizontalLength > c.FilletSlipMaxSpeed) return;
            for (int k = 0; k < 4; k++)
            {
                var d = k == 0 ? new Vec3d(1, 0, 0) : k == 1 ? new Vec3d(-1, 0, 0) : k == 2 ? new Vec3d(0, 0, 1) : new Vec3d(0, 0, -1);
                // Touching a wall (still blocked a step higher) on this side?
                if (!Blocked(g, c, s.Position + d * c.FilletIgnore, h)) continue;
                if (!Blocked(g, c, s.Position + d * c.FilletIgnore + new Vec3d(0, c.MaxStepUp + 0.01, 0), h)) continue;
                Vec3d p = s.Position;
                MoveAxis(g, c, ref p, k < 2 ? 0 : 2, -(k < 2 ? d.X : d.Z) * c.FilletSlipPerTick, h, out _);
                if (!ProbeDown(g, c, p, h, c.FilletIgnore, out double drop) || drop < 1e-4) continue;
                s.Position = new Vec3d(p.X, p.Y - drop, p.Z);
                return;
            }
        }

        private static void MoveHorizontalWithStep(ITerrain g, MovementConfig c, ref MotorState s, int axis, double delta, double h)
        {
            if (delta == 0) return;
            Vec3d start = s.Position;
            double moved = MoveAxis(g, c, ref s.Position, axis, delta, h, out bool blocked);
            if (!blocked) return;

            // Try stepping up a little (ramp slopes) and moving the rest of the way.
            Vec3d alt = start;
            MoveAxis(g, c, ref alt, 1, c.MaxStepUp, h, out _);
            double altMoved = MoveAxis(g, c, ref alt, axis, delta, h, out bool altBlocked);
            // A step that only gains the 0.15 m fillet at the foot of a wall is not taken: pressing into a wall must
            // not lift the player onto its base.
            // So the stepped path must keep going for a little while at the raised height (a slope or a ledge top does;
            // a fillet ends in the wall).
            double gain = Math.Abs(altMoved) - Math.Abs(moved);
            bool keepsGoing = false;
            if (gain > 1e-6)
            {
                Vec3d look = alt;
                double ahead = MoveAxis(g, c, ref look, axis, Math.Sign(delta) * c.FilletIgnore, h, out bool lookBlocked);
                keepsGoing = !lookBlocked || Math.Abs(ahead) + gain > c.FilletIgnore;
            }
            if (keepsGoing)
            {
                MoveAxis(g, c, ref alt, 1, -(alt.Y - start.Y), h, out _);
                s.Position = alt;
                blocked = altBlocked;
            }
            if (blocked)
            {
                if (axis == 0) s.Velocity = new Vec3d(0, s.Velocity.Y, s.Velocity.Z);
                else s.Velocity = new Vec3d(s.Velocity.X, s.Velocity.Y, 0);
            }
        }

        /// <summary>Moves along one axis (0 = X, 1 = Y, 2 = Z) until contact. Returns the distance moved.</summary>
        public static double MoveAxis(ITerrain g, MovementConfig c, ref Vec3d pos, int axis, double delta, double h, out bool blocked)
        {
            blocked = false;
            if (delta == 0) return 0;
            double total = 0;
            int steps = (int)Math.Ceiling(Math.Abs(delta) / 0.3);
            double part = delta / steps;
            for (int i = 0; i < steps; i++)
            {
                Vec3d target = Offset(pos, axis, part);
                if (!Blocked(g, c, target, h))
                {
                    pos = target;
                    total += part;
                    continue;
                }
                // Bisect for the contact point.
                double lo = 0, hi = 1;
                for (int k = 0; k < 18; k++)
                {
                    double mid = (lo + hi) * 0.5;
                    if (Blocked(g, c, Offset(pos, axis, part * mid), h)) hi = mid; else lo = mid;
                }
                pos = Offset(pos, axis, part * lo);
                total += part * lo;
                blocked = true;
                break;
            }
            return total;
        }

        private static Vec3d Offset(Vec3d p, int axis, double d) =>
            axis == 0 ? new Vec3d(p.X + d, p.Y, p.Z) : axis == 1 ? new Vec3d(p.X, p.Y + d, p.Z) : new Vec3d(p.X, p.Y, p.Z + d);

        /// <summary>Finds ground within maxDrop below the feet. drop = distance down to it.</summary>
        private static bool ProbeDown(ITerrain g, MovementConfig c, Vec3d pos, double h, double maxDrop, out double drop)
        {
            Vec3d p = pos;
            MoveAxis(g, c, ref p, 1, -maxDrop, h, out bool hit);
            drop = pos.Y - p.Y;
            return hit;
        }

        /// <summary>Is there ground just under the feet, and what is its normal?</summary>
        public static bool ProbeGround(ITerrain g, MovementConfig c, Vec3d pos, double h, out Vec3d normal)
            => g.GroundContact(Box(pos, c.HalfWidth, h), c.GroundProbe, out normal);

        private static void SetCrouched(ITerrain g, MovementConfig c, ref MotorState s, bool want)
        {
            if (want == s.Crouched) return;
            if (!want && Blocked(g, c, s.Position, c.StandHeight)) return; // no headroom: stay crouched
            s.Crouched = want;
        }

        /// <summary>Safety net: if the box starts inside geometry (e.g. a late net correction), lift it out.</summary>
        private static void Depenetrate(ITerrain g, MovementConfig c, ref MotorState s)
        {
            double h = HeightOf(s, c);
            if (!Blocked(g, c, s.Position, h)) return;
            for (double up = 0.05; up <= 1.3 + 1e-9; up += 0.05)
            {
                var p = new Vec3d(s.Position.X, s.Position.Y + up, s.Position.Z);
                if (!Blocked(g, c, p, h))
                {
                    s.Position = p;
                    return;
                }
            }
        }

        // ---- Small maths helpers ----

        private static Vec3d Horizontal(Vec3d v) => new Vec3d(v.X, 0, v.Z);

        /// <summary>Gives a horizontal velocity the vertical part that keeps it on the ground plane.</summary>
        private static Vec3d WithTangentY(Vec3d vh, Vec3d n)
        {
            double vy = n.Y > 1e-6 ? -(n.X * vh.X + n.Z * vh.Z) / n.Y : 0;
            return new Vec3d(vh.X, vy, vh.Z);
        }

        private static bool IsDownhill(Vec3d vh, Vec3d n) => n.X * vh.X + n.Z * vh.Z > 1e-6;

        private static Vec3d MoveTowards(Vec3d from, Vec3d to, double maxDelta)
        {
            Vec3d d = to - from;
            double l = d.Length;
            if (l <= maxDelta || l < 1e-12) return to;
            return from + d * (maxDelta / l);
        }

        /// <summary>Rotates horizontal vector v toward direction w by at most maxAngle radians, keeping its length.</summary>
        private static Vec3d RotateToward(Vec3d v, Vec3d w, double maxAngle)
        {
            double a = Math.Atan2(v.X, v.Z);
            double b = Math.Atan2(w.X, w.Z);
            double diff = b - a;
            while (diff > Math.PI) diff -= 2 * Math.PI;
            while (diff < -Math.PI) diff += 2 * Math.PI;
            // Don't steer against the slide (pressing back) - that only happens through friction.
            if (Math.Abs(diff) > Math.PI * 0.75) return v;
            double turn = Math.Max(-maxAngle, Math.Min(maxAngle, diff));
            double len = v.HorizontalLength;
            double na = a + turn;
            return new Vec3d(Math.Sin(na) * len, 0, Math.Cos(na) * len);
        }

        private static void Emit(List<MoveEvent> events, MoveEventType t, int tick, int value = 0, double amount = 0) =>
            events?.Add(new MoveEvent { Type = t, Tick = tick, Value = value, Amount = amount });
    }
}
