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
        private const double Bs = VoxelGrid.BlockSize;

        public static double HeightOf(in MotorState s, MovementConfig c) => s.Crouched ? c.CrouchHeight : c.StandHeight;
        public static double EyeOf(in MotorState s, MovementConfig c) => s.Crouched ? c.CrouchEye : c.StandEye;

        public static Aabb Box(Vec3d feet, double halfWidth, double height) => new Aabb(
            new Vec3d(feet.X - halfWidth, feet.Y, feet.Z - halfWidth),
            new Vec3d(feet.X + halfWidth, feet.Y + height, feet.Z + halfWidth));

        private static bool Blocked(VoxelGrid g, MovementConfig c, Vec3d feet, double height) =>
            VoxelGeometry.BoxOverlapsSolid(g, Box(feet, c.HalfWidth, height));

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

        public static void Step(VoxelGrid g, MovementConfig c, ref MotorState s, in MoveInput input, List<MoveEvent> events = null)
        {
            s.Tick++;
            if (input.JumpPressed) s.JumpBufferedTick = s.Tick;
            bool wasSprinting = s.Sprinting;

            Depenetrate(g, c, ref s);

            if (s.Mode == MoveMode.Vault) { StepVault(g, c, ref s, events); FinishTick(c, ref s, wasSprinting); return; }
            if (s.Mode == MoveMode.Mantle) { StepMantle(g, c, ref s, events); FinishTick(c, ref s, wasSprinting); return; }

            Vec3d wish = WishDir(input);
            bool grounded = ProbeGround(g, c, s.Position, HeightOf(s, c), out Vec3d normal);
            s.GroundNormal = grounded ? normal : new Vec3d(0, 1, 0);

            // ---- Mode transitions from contact ----
            if (s.Mode == MoveMode.Air && grounded && s.Velocity.Y <= 0)
                Land(c, ref s, input, events);
            else if ((s.Mode == MoveMode.Ground || s.Mode == MoveMode.Slide) && !grounded)
            {
                s.Mode = MoveMode.Air;
                s.AirPeakY = s.Position.Y;
            }
            s.Grounded = grounded && s.Mode != MoveMode.Air;
            if (s.Grounded) s.LastGroundedTick = s.Tick;

            // Late slide-land: crouch pressed within the window after touchdown restores the landing speed.
            if (s.Mode == MoveMode.Ground && input.CrouchPressed && !s.LandSlideUsed &&
                s.Tick - s.LandTick <= c.SlideLandWindowTicks && s.LandVelocity.HorizontalLength >= c.SlideLandMinSpeed)
            {
                s.Velocity = new Vec3d(s.LandVelocity.X, s.Velocity.Y, s.LandVelocity.Z);
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

        private static void StepGround(VoxelGrid g, MovementConfig c, ref MotorState s, in MoveInput input, Vec3d wish, bool jumpBuffered, List<MoveEvent> events)
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
            Vec3d vh = Horizontal(s.Velocity);
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

        private static void StartSlide(VoxelGrid g, MovementConfig c, ref MotorState s, bool allowBoost, List<MoveEvent> events)
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

        private static void StepSlide(VoxelGrid g, MovementConfig c, ref MotorState s, in MoveInput input, Vec3d wish, bool jumpBuffered, List<MoveEvent> events)
        {
            double dt = c.Dt;
            Vec3d vh = Horizontal(s.Velocity);

            if (jumpBuffered)
            {
                SetCrouched(g, c, ref s, false);
                Jump(c, ref s, vh, events);
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

        private static void StepAir(VoxelGrid g, MovementConfig c, ref MotorState s, in MoveInput input, Vec3d wish, bool jumpBuffered, List<MoveEvent> events)
        {
            double dt = c.Dt;
            Vec3d vh = Horizontal(s.Velocity);

            if (jumpBuffered)
            {
                if (TryMantle(g, c, ref s, input.Yaw, true, events)) return;
                if (s.Tick - s.LastGroundedTick <= c.CoyoteTicks)
                {
                    Jump(c, ref s, vh, events);
                    return;
                }
            }

            if (wish.LengthSquared > 1e-9)
            {
                Vec3d target = wish.Normalized * Math.Max(vh.Length, c.RunSpeed) * wish.Length;
                vh = MoveTowards(vh, target, c.GroundAccel * c.AirAccelFraction * dt);
            }
            s.Velocity = new Vec3d(vh.X, s.Velocity.Y, vh.Z); // gravity is applied exactly in Integrate
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
                if (sp > c.SlideMaxSpeed) s.Velocity = s.Velocity * (c.SlideMaxSpeed / sp);
                Emit(events, MoveEventType.SlideStarted, s.Tick, 0);
                Emit(events, MoveEventType.SlideLandSuccess, s.Tick);
            }
        }

        // ---- Vault ----

        private static bool TryVault(VoxelGrid g, MovementConfig c, ref MotorState s, List<MoveEvent> events)
        {
            Vec3d vh = Horizontal(s.Velocity);
            double speed = vh.Length;
            if (speed < c.VaultMinSpeed) return false;
            Vec3d dir = vh / speed;
            double h = HeightOf(s, c);
            Vec3d pos = s.Position;

            if (!Blocked(g, c, pos + dir * (speed * c.Dt + 0.02), h)) return false; // nothing ahead

            double top = Math.Ceiling((pos.Y + 0.3) / Bs) * Bs;
            if (top - pos.Y > Bs + 0.05) return false; // only 1-level obstacles
            double targetY = top + 0.01;
            if (!LedgeClear(g, c, pos, dir, targetY, h)) return false;

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

        private static void StepVault(VoxelGrid g, MovementConfig c, ref MotorState s, List<MoveEvent> events)
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

        private static bool TryMantle(VoxelGrid g, MovementConfig c, ref MotorState s, double yaw, bool airborne, List<MoveEvent> events)
        {
            Vec3d dir = Forward(yaw);
            double h = HeightOf(s, c);
            Vec3d pos = s.Position;
            if (!Blocked(g, c, pos + dir * 0.1, h)) return false; // must be touching a wall in front

            double minH = airborne ? c.AirMantleMinHeight : c.JumpApex - 0.05;
            double first = Math.Ceiling((pos.Y + minH) / Bs) * Bs;
            for (double top = first; top <= pos.Y + c.MantleMaxHeight + 1e-9; top += Bs)
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
                s.Velocity = Vec3d.Zero;
                s.Sprinting = false;
                s.JumpBufferedTick = int.MinValue / 2;
                s.ShootReadyTick = Math.Max(s.ShootReadyTick, s.Tick + c.MantleRiseTicks + c.MantleForwardTicks);
                Emit(events, MoveEventType.MantleStarted, s.Tick);
                return true;
            }
            return false;
        }

        private static void StepMantle(VoxelGrid g, MovementConfig c, ref MotorState s, List<MoveEvent> events)
        {
            s.ActionTick++;
            double h = HeightOf(s, c);
            s.Velocity = Vec3d.Zero;
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
                s.Velocity = s.ActionForward * (c.RunSpeed * c.MantleExitSpeedFraction);
                Emit(events, MoveEventType.MantleFinished, s.Tick);
            }
        }

        /// <summary>Room to stand at targetY both here and a short step forward, with something to stand on ahead.</summary>
        private static bool LedgeClear(VoxelGrid g, MovementConfig c, Vec3d pos, Vec3d dir, double targetY, double h)
        {
            Vec3d here = new Vec3d(pos.X, targetY, pos.Z);
            Vec3d ahead = here + dir * c.MantleForwardDistance;
            if (Blocked(g, c, here, h)) return false;
            if (Blocked(g, c, ahead, h)) return false;
            return Blocked(g, c, ahead - new Vec3d(0, 0.06, 0), h);
        }

        // ---- Integration and collision ----

        private static void Integrate(VoxelGrid g, MovementConfig c, ref MotorState s)
        {
            double h = HeightOf(s, c);
            Vec3d d = s.Velocity * c.Dt;
            bool groundMode = s.Mode == MoveMode.Ground || s.Mode == MoveMode.Slide;

            if (groundMode)
            {
                // Move along the ground: horizontal with step-up (climbs ramp slopes, never full blocks), then stick down.
                double startY = s.Position.Y;
                MoveHorizontalWithStep(g, c, ref s, 0, d.X, h);
                MoveHorizontalWithStep(g, c, ref s, 2, d.Z, h);
                double snap = c.MaxStepUp + Horizontal(d).Length + (s.Position.Y - startY);
                if (ProbeDown(g, c, s.Position, h, snap, out double drop))
                    s.Position = new Vec3d(s.Position.X, s.Position.Y - drop, s.Position.Z);
                // If no ground within snap distance we leave it: next tick goes airborne with the current
                // velocity (including the slope's vertical part), which is the ramp launch.
            }
            else
            {
                // Exact constant-gravity step: the apex matches the configured jump height.
                double vy0 = s.Velocity.Y;
                double vy1 = Math.Max(-c.MaxFallSpeed, vy0 - c.Gravity * c.Dt);
                d = new Vec3d(d.X, (vy0 + vy1) * 0.5 * c.Dt, d.Z);
                s.Velocity = new Vec3d(s.Velocity.X, vy1, s.Velocity.Z);
                MoveAxis(g, c, ref s.Position, 0, d.X, h, out bool bx);
                if (bx) s.Velocity = new Vec3d(0, s.Velocity.Y, s.Velocity.Z);
                MoveAxis(g, c, ref s.Position, 2, d.Z, h, out bool bz);
                if (bz) s.Velocity = new Vec3d(s.Velocity.X, s.Velocity.Y, 0);
                MoveAxis(g, c, ref s.Position, 1, d.Y, h, out bool by);
                if (by) s.Velocity = new Vec3d(s.Velocity.X, 0, s.Velocity.Z);
            }
        }

        private static void MoveHorizontalWithStep(VoxelGrid g, MovementConfig c, ref MotorState s, int axis, double delta, double h)
        {
            if (delta == 0) return;
            Vec3d start = s.Position;
            double moved = MoveAxis(g, c, ref s.Position, axis, delta, h, out bool blocked);
            if (!blocked) return;

            // Try stepping up a little (ramp slopes) and moving the rest of the way.
            Vec3d alt = start;
            MoveAxis(g, c, ref alt, 1, c.MaxStepUp, h, out _);
            double altMoved = MoveAxis(g, c, ref alt, axis, delta, h, out bool altBlocked);
            if (Math.Abs(altMoved) > Math.Abs(moved) + 1e-6)
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
        public static double MoveAxis(VoxelGrid g, MovementConfig c, ref Vec3d pos, int axis, double delta, double h, out bool blocked)
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
        private static bool ProbeDown(VoxelGrid g, MovementConfig c, Vec3d pos, double h, double maxDrop, out double drop)
        {
            Vec3d p = pos;
            MoveAxis(g, c, ref p, 1, -maxDrop, h, out bool hit);
            drop = pos.Y - p.Y;
            return hit;
        }

        /// <summary>Is there ground just under the feet, and what is its normal?</summary>
        public static bool ProbeGround(VoxelGrid g, MovementConfig c, Vec3d pos, double h, out Vec3d normal)
        {
            normal = new Vec3d(0, 1, 0);
            Aabb probe = Box(pos - new Vec3d(0, c.GroundProbe, 0), c.HalfWidth, h);
            if (!VoxelGeometry.BoxOverlapsSolid(g, probe)) return false;

            // Supporting block: the highest surface under the footprint.
            Aabb foot = Box(pos, c.HalfWidth, h);
            int y = VoxelGrid.CellAt(new Vec3d(pos.X, pos.Y - c.GroundProbe, pos.Z)).Y;
            Int3 lo = VoxelGrid.CellAt(foot.Min + new Vec3d(Aabb.Epsilon, 0, Aabb.Epsilon));
            Int3 hi = VoxelGrid.CellAt(foot.Max - new Vec3d(Aabb.Epsilon, 0, Aabb.Epsilon));
            double best = double.NegativeInfinity;
            bool bestRamp = false;
            Dir bestLow = Dir.East;
            for (int z = lo.Z; z <= hi.Z; z++)
                for (int x = lo.X; x <= hi.X; x++)
                {
                    var cell = new Int3(x, y, z);
                    Block b = g.Get(cell);
                    if (b.IsEmpty) continue;
                    double top = SurfaceUnder(b, cell, foot);
                    bool better = top > best + 1e-6 || (Math.Abs(top - best) <= 1e-6 && bestRamp && !b.IsRamp);
                    if (better)
                    {
                        best = top;
                        bestRamp = b.IsRamp;
                        bestLow = b.RampLow;
                    }
                }
            if (bestRamp)
            {
                Int3 d = bestLow.ToInt3();
                normal = new Vec3d(d.X, 1, d.Z).Normalized;
            }
            return true;
        }

        private static double SurfaceUnder(Block b, Int3 cell, Aabb box)
        {
            Aabb c = VoxelGrid.CellBounds(cell);
            if (!b.IsRamp) return c.Max.Y;
            switch (b.RampLow)
            {
                case Dir.East: return VoxelGeometry.SurfaceHeight(b, cell, box.Min.X, c.Center.Z);
                case Dir.West: return VoxelGeometry.SurfaceHeight(b, cell, box.Max.X, c.Center.Z);
                case Dir.North: return VoxelGeometry.SurfaceHeight(b, cell, c.Center.X, box.Min.Z);
                default: return VoxelGeometry.SurfaceHeight(b, cell, c.Center.X, box.Max.Z);
            }
        }

        private static void SetCrouched(VoxelGrid g, MovementConfig c, ref MotorState s, bool want)
        {
            if (want == s.Crouched) return;
            if (!want && Blocked(g, c, s.Position, c.StandHeight)) return; // no headroom: stay crouched
            s.Crouched = want;
        }

        /// <summary>Safety net: if the box starts inside geometry (e.g. a late net correction), lift it out.</summary>
        private static void Depenetrate(VoxelGrid g, MovementConfig c, ref MotorState s)
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
