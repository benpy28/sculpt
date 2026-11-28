using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// First byte of every packet. Kept as one flat list so later traffic (grid deltas, sculpt commands,
    /// thrown-block entities, weapons) slots in as new message types without touching the movement path.
    /// </summary>
    public enum MsgType : byte
    {
        Welcome = 1,   // server -> client, reliable: who you are and where you spawn
        Inputs = 2,    // client -> server, unreliable: newest N input commands
        Snapshot = 3,  // server -> client, unreliable: authoritative state for one server tick
        World = 4,     // server -> client, reliable: piece ops and sculpt events of one tick, or the late-join state
        // Sculpt confirms ride inside Inputs (same tick, same redundancy). Thrown pieces fly deterministically from
        // their Thrown event, so they need no entity stream.
        Combat = 7,    // server -> client, reliable: shots, hits and kills of one tick
        Tiles = 8,     // server -> client, reliable: wall tile changes of one tick, or every changed tile for a late joiner
        Items = 9,     // server -> client, reliable: the whole list of items on the ground, whenever it changes (and on join)
        Match = 10,    // server -> client, reliable: match phase, timer and scores, whenever they change (and on join)
        Reveal = 11,   // server -> client, reliable: an enemy shown through walls by the anti-hiding ping (only to those two players, only when it starts or is cancelled)
        Hello = 12,    // client -> server, reliable, first packet after connecting: version, and play or watch (Replay/SpecProtocol)
        SpecFrame = 13, // server -> spectator, reliable: one tick's spectator packets bundled (also what a replay file stores)
        SpecSnap = 14, // inside spectator frames only: every player, delta-coded (Replay/SpecProtocol)
        Prefs = 15,    // client -> server, reliable, after the welcome and whenever they change: slot auto-sort and the battle royale locker gun
    }

    /// <summary>A player's own choices the server needs (protocol 35): where pickups go, and the locker gun.</summary>
    public struct PrefsMsg
    {
        public SlotPrefs Slots;
        /// <summary>Battle royale locker gun (None = the server's default).</summary>
        public Item Locker;
    }

    public struct WelcomeMsg
    {
        public byte ProtocolVersion;
        public int PlayerId;
        public int Team;
        public int ServerTick;
        public Vec3d Spawn;
        public double SpawnYaw;
        /// <summary>Spectators only: how far behind the live match they watch, in ticks (0 for players).</summary>
        public int SpectatorDelayTicks;

        /// <summary>Player id 0 is never a player: a welcome with it makes the client a spectator.</summary>
        public bool IsSpectator => PlayerId == 0;
    }

    public struct InputsMsg
    {
        /// <summary>Newest server snapshot tick the client has received and kept: the server codes the next snapshots against it.</summary>
        public int AckSnapshotTick;
        /// <summary>Oldest first, consecutive ticks.</summary>
        public List<InputCmd> Cmds;
    }

    /// <summary>What every client sees of another player. Floats are plenty for drawing.</summary>
    public struct PlayerSnap
    {
        public int Id;
        public int Team;
        public Vec3d Position;
        public Vec3d Velocity;
        public float Yaw;     // radians
        public float Pitch;   // degrees
        public MoveMode Mode;
        public bool Crouched;
        public bool Dead;
        /// <summary>Item in hand, and whether they are in sculpt mode etc. (a visible tell).</summary>
        public Item Held;
        public HandMode Hand;
    }

    public struct SnapshotMsg
    {
        public int ServerTick;
        public bool HasSelf;
        /// <summary>True if the server had the client's real command for this tick (false = it repeated the last one).</summary>
        public bool SelfInputWasReal;
        /// <summary>Worst lead (ticks early) of this client's commands over the feedback window. sbyte.MinValue = none arrived.</summary>
        public int SelfLead;
        public MotorState Self;
        /// <summary>The receiver's sculpt bar at this tick (basis points).</summary>
        public int SelfBar;
        /// <summary>
        /// Latency overlay: how long the server held the client's command for this tick, from its arrival to this
        /// snapshot being sent (waiting for the tick, simulating, sending), in tenths of a millisecond.
        /// <see cref="Protocol.NoHold"/> when the server has no clock or had no real command for the tick.
        /// </summary>
        public int SelfHoldTenthMs;
        public List<PlayerSnap> Others;
    }

    public static class Protocol
    {
        public const byte Version = 36; // 36 countdown freeze and BR fall-out deaths in the predicted step, door toggle cooldown, and the Match message carries the match length (Playing ticks); 35 inventory rework (any item in any slot, duplicate guns, bandages and first-aid kits, slot swaps in the command, the Prefs message, held item in 4 bits); 34 battle royale first playable (bag and ground item rarity, chests, the BR match and circle in the Match message, item deltas); 33 anti-cheat groundwork (interest management: snapshots only carry enemies you could see or hear soon; dev respawn refused unless allowed); 32 delta snapshots (coded against the newest snapshot the client acked; other players quantized like the spectator stream); 31 balance pass 2 (SMG head 1.5x, no crouch accuracy bonus: rules differ between builds); 30 replays and spectators (Hello, spectator frames, welcome gains the spectator delay); 29 integrate2 round 3 (balance 28 + ping 25 + doors + guns + home screen); 28 balance pass; 25 anti-hiding ping; 24 the second friend-demo merge (120 Hz + walls by cell + movement/feel + duel map v2); 23 feel pass; 20 friend demo merge; 19 C4/launcher; 18 blast holes; 16 latency
        public const int NoLead = sbyte.MinValue;
        public const int NoHold = ushort.MaxValue;
        public const int MaxCmdsPerPacket = 32;
        public const int MaxPlayersPerSnapshot = 64;

        public static MsgType PeekType(byte[] data, int offset, int count)
        {
            if (data == null || count < 1) throw new NetFormatException("Empty packet.");
            return (MsgType)data[offset];
        }

        // ---------------------------------------------------------------- Welcome

        public static void Write(ByteWriter w, in WelcomeMsg m)
        {
            w.U8((byte)MsgType.Welcome);
            w.U8(m.ProtocolVersion);
            w.I32(m.PlayerId);
            w.I32(m.Team);
            w.I32(m.ServerTick);
            w.Vec(m.Spawn);
            w.F64(m.SpawnYaw);
            w.I32(m.SpectatorDelayTicks);
        }

        public static void Write(ByteWriter w, in PrefsMsg m)
        {
            w.U8((byte)MsgType.Prefs);
            for (int k = 0; k < Items.Slots; k++) w.U8((byte)m.Slots[k]);
            w.U8((byte)m.Locker);
        }

        public static PrefsMsg ReadPrefs(ref ByteReader r)
        {
            Expect(ref r, MsgType.Prefs);
            var m = new PrefsMsg();
            for (int k = 0; k < Items.Slots; k++)
            {
                byte v = r.U8();
                if (v > (byte)SlotPrefs.Last) throw new NetFormatException("Bad slot preference.");
                m.Slots[k] = (SlotPref)v;
            }
            byte l = r.U8();
            if (l != 0 && !Items.IsGun((Item)l)) throw new NetFormatException("Bad locker gun.");
            m.Locker = (Item)l;
            return m;
        }

        public static WelcomeMsg ReadWelcome(ref ByteReader r)
        {
            Expect(ref r, MsgType.Welcome);
            var m = new WelcomeMsg { ProtocolVersion = r.U8() };
            // Another build's welcome may be laid out differently: the version alone tells the client what is wrong.
            if (m.ProtocolVersion != Version) return m;
            m.PlayerId = r.I32();
            m.Team = r.I32();
            m.ServerTick = r.I32();
            m.Spawn = r.Vec();
            m.SpawnYaw = r.F64();
            m.SpectatorDelayTicks = r.I32();
            return m;
        }

        // ---------------------------------------------------------------- Inputs

        public static void Write(ByteWriter w, in InputsMsg m)
        {
            int n = m.Cmds.Count;
            if (n < 1 || n > MaxCmdsPerPacket) throw new System.ArgumentException("Bad command count.");
            w.U8((byte)MsgType.Inputs);
            w.I32(m.AckSnapshotTick);
            w.U8((byte)n);
            w.I32(m.Cmds[n - 1].Tick);
            for (int i = 0; i < n; i++) m.Cmds[i].Write(w);
        }

        /// <summary>Reads into the given list (cleared first). Commands come out oldest first with consecutive ticks.</summary>
        public static InputsMsg ReadInputs(ref ByteReader r, List<InputCmd> into)
        {
            Expect(ref r, MsgType.Inputs);
            var m = new InputsMsg { AckSnapshotTick = r.I32(), Cmds = into };
            into.Clear();
            int n = r.U8();
            if (n < 1 || n > MaxCmdsPerPacket) throw new NetFormatException("Bad command count.");
            int newest = r.I32();
            if (newest < 0 || newest > int.MaxValue - 1) throw new NetFormatException("Bad tick.");
            for (int i = 0; i < n; i++)
                into.Add(InputCmd.Read(ref r, newest - (n - 1) + i));
            return m;
        }

        // ---------------------------------------------------------------- Snapshot

        /// <summary>Writes a full snapshot (no delta base). The server uses <see cref="SnapshotCodec"/> per client.</summary>
        public static void Write(ByteWriter w, in SnapshotMsg m) => new SnapshotCodec().Write(w, m, null, -1);

        /// <summary>Reads a full snapshot. A delta (which needs the receiver's baselines) throws.</summary>
        public static SnapshotMsg ReadSnapshot(ref ByteReader r, List<PlayerSnap> othersInto)
        {
            if (!new SnapshotCodec().TryRead(ref r, othersInto, null, out SnapshotMsg m)) throw new NetFormatException("Delta snapshot without baselines.");
            return m;
        }

        // ---------------------------------------------------------------- MotorState (exact, every field)

        public static void WriteMotor(ByteWriter w, in MotorState s)
        {
            w.Vec(s.Position);
            w.Vec(s.Velocity);
            w.U8((byte)s.Mode);
            byte f = 0;
            if (s.Crouched) f |= 1;
            if (s.Sprinting) f |= 2;
            if (s.Grounded) f |= 4;
            if (s.LandSlideUsed) f |= 8;
            if (s.Braking) f |= 16;
            if (s.Carving) f |= 32;
            w.U8(f);
            w.Vec(s.GroundNormal);
            w.I32(s.Tick);
            w.I32(s.LastGroundedTick);
            w.I32(s.JumpBufferedTick);
            w.I32(s.LastSlideBoostTick);
            w.I32(s.LandTick);
            w.Vec(s.LandVelocity);
            w.F64(s.AirPeakY);
            w.I32(s.UphillTicks);
            w.I32(s.ActionTick);
            w.F64(s.ActionStartY);
            w.F64(s.ActionTargetY);
            w.Vec(s.ActionForward);
            w.Vec(s.ActionKeepVelocity);
            w.I32(s.ShootReadyTick);
            w.I32(s.WallHitTick);
            w.F64(s.WallHitSpeed);
            w.Vec(s.WallHitNormal);
            w.I32(s.WallKicks);
            w.I32(s.LastWallKickTick);
            w.Vec(s.LastWallKickNormal);
            w.I32(s.LastSlideJumpTick);
            w.U8((byte)s.Hand.Mode);
            w.U8((byte)((s.Hand.Slot & 7) | ((s.Hand.PrevSlot & 7) << 3) | (s.Hand.HeldReady ? 0x40 : 0) | (s.Hand.ExitQueued ? 0x80 : 0)));
            Bag bag = s.Hand.Bag;
            for (int k = 0; k < Items.Slots; k++) { w.U8((byte)((byte)bag[k] | ((byte)bag.RarityOf(k) << 4))); w.U8(bag.Count(k)); }
            w.I32(s.Hand.ReadyTick);
            w.I32(s.Hand.SculptLockUntil);
            w.I32(s.Hand.UseUntil);
            w.I32(s.Hand.UseStart);
            WeaponState g = s.Weapon;
            w.I64(g.NextPump); w.I64(g.NextSmg); w.I64(g.NextAr); w.I64(g.NextPistol);
            w.I32(g.LastShotTick); w.I32(g.Streak); w.I32(g.ShotCount);
            w.F64(g.Bloom); w.F64(g.RecoilPitch); w.F64(g.RecoilYaw);
            w.Bool(g.TriggerWasHeld); w.I32(g.BufferedPressTick);
            w.U8((byte)((byte)g.BloomGun | ((byte)g.ReloadGun << 2) | (g.Reloading ? 0x10 : 0)));
            w.U8(g.UsedPump); w.U8(g.UsedSmg); w.U8(g.UsedAr); w.U8(g.UsedPistol);
            w.I32(g.ReloadTick);
            w.I32(s.Damage); w.Bool(s.Dead); w.I32(s.RespawnTick);
            WriteFlight(w, s.Nade0);
            WriteFlight(w, s.Nade1);
            WriteFlight(w, s.Nade2);
            WriteFlight(w, s.Nade3);
            w.U8(s.NadeSeq);
            w.I32(s.LauncherReadyTick);
            w.I32(s.PunchTick);
            w.I32(s.PunchBufferTick);
            w.I32(s.ChargeTick);
        }

        private static void WriteFlight(ByteWriter w, in GrenadeFlight f)
        {
            w.Bool(f.Active);
            if (!f.Active) return;
            w.U8((byte)((byte)f.Kind | (f.Stuck ? 0x10 : 0) | (f.Landed ? 0x20 : 0)));
            w.Vec(f.Position);
            w.Vec(f.Velocity);
            w.I32(f.ThrowTick);
            w.U8(f.Seq);
            w.F64(f.Travel);
            if (!f.Landed) return;
            w.I32(f.StuckTick);
            w.Vec(f.Normal);
        }

        private static GrenadeFlight ReadFlight(ref ByteReader r)
        {
            var f = new GrenadeFlight { Active = r.Bool() };
            if (!f.Active) return f;
            byte kf = r.U8();
            if ((kf & 0x0F) > (byte)ThrowKind.Launcher || (kf & 0xC0) != 0) throw new NetFormatException("Bad flight.");
            f.Kind = (ThrowKind)(kf & 0x0F);
            f.Stuck = (kf & 0x10) != 0;
            f.Landed = (kf & 0x20) != 0;
            f.Position = r.Vec();
            f.Velocity = r.Vec();
            f.ThrowTick = r.I32();
            f.Seq = r.U8();
            f.Travel = r.F64();
            if (!f.Landed) return f;
            f.StuckTick = r.I32();
            f.Normal = r.Vec();
            return f;
        }

        public static MotorState ReadMotor(ref ByteReader r)
        {
            var s = new MotorState();
            s.Position = r.Vec();
            s.Velocity = r.Vec();
            s.Mode = ReadMode(ref r);
            byte f = r.U8();
            s.Crouched = (f & 1) != 0;
            s.Sprinting = (f & 2) != 0;
            s.Grounded = (f & 4) != 0;
            s.LandSlideUsed = (f & 8) != 0;
            s.Braking = (f & 16) != 0;
            s.Carving = (f & 32) != 0;
            s.GroundNormal = r.Vec();
            s.Tick = r.I32();
            s.LastGroundedTick = r.I32();
            s.JumpBufferedTick = r.I32();
            s.LastSlideBoostTick = r.I32();
            s.LandTick = r.I32();
            s.LandVelocity = r.Vec();
            s.AirPeakY = r.F64();
            s.UphillTicks = r.I32();
            s.ActionTick = r.I32();
            s.ActionStartY = r.F64();
            s.ActionTargetY = r.F64();
            s.ActionForward = r.Vec();
            s.ActionKeepVelocity = r.Vec();
            s.ShootReadyTick = r.I32();
            s.WallHitTick = r.I32();
            s.WallHitSpeed = r.F64();
            s.WallHitNormal = r.Vec();
            s.WallKicks = r.I32();
            s.LastWallKickTick = r.I32();
            s.LastWallKickNormal = r.Vec();
            s.LastSlideJumpTick = r.I32();
            byte hm = r.U8();
            if (hm > (byte)HandMode.Disarmed) throw new NetFormatException("Bad hand mode.");
            s.Hand.Mode = (HandMode)hm;
            byte hw = r.U8();
            if ((hw & 7) >= Items.Slots || ((hw >> 3) & 7) >= Items.Slots) throw new NetFormatException("Bad slot.");
            s.Hand.Slot = (byte)(hw & 7);
            s.Hand.PrevSlot = (byte)((hw >> 3) & 7);
            s.Hand.HeldReady = (hw & 0x40) != 0;
            s.Hand.ExitQueued = (hw & 0x80) != 0;
            for (int k = 0; k < Items.Slots; k++)
            {
                byte it = r.U8();
                if ((it & 0x0F) > (byte)Items.Last || (it & 0x0F) == (byte)Item.Chest || (it >> 4) > (byte)Rarities.Last) throw new NetFormatException("Bad item.");
                s.Hand.Bag[k] = (Item)(it & 0x0F);
                s.Hand.Bag.SetRarity(k, (Rarity)(it >> 4));
                s.Hand.Bag.SetCount(k, r.U8());
            }
            s.Hand.ReadyTick = r.I32();
            s.Hand.SculptLockUntil = r.I32();
            s.Hand.UseUntil = r.I32();
            s.Hand.UseStart = r.I32();
            var g = new WeaponState
            {
                NextPump = r.I64(), NextSmg = r.I64(), NextAr = r.I64(), NextPistol = r.I64(),
                LastShotTick = r.I32(), Streak = r.I32(), ShotCount = r.I32(),
                Bloom = r.F64(), RecoilPitch = r.F64(), RecoilYaw = r.F64(),
                TriggerWasHeld = r.Bool(), BufferedPressTick = r.I32(),
            };
            byte gw = r.U8();
            g.BloomGun = (Weapon)(gw & 3);
            g.ReloadGun = (Weapon)((gw >> 2) & 3);
            g.Reloading = (gw & 0x10) != 0;
            g.UsedPump = r.U8(); g.UsedSmg = r.U8(); g.UsedAr = r.U8(); g.UsedPistol = r.U8();
            g.ReloadTick = r.I32();
            s.Weapon = g;
            s.Damage = r.I32();
            s.Dead = r.Bool();
            s.RespawnTick = r.I32();
            s.Nade0 = ReadFlight(ref r);
            s.Nade1 = ReadFlight(ref r);
            s.Nade2 = ReadFlight(ref r);
            s.Nade3 = ReadFlight(ref r);
            s.NadeSeq = r.U8();
            s.LauncherReadyTick = r.I32();
            s.PunchTick = r.I32();
            s.PunchBufferTick = r.I32();
            s.ChargeTick = r.I32();
            return s;
        }

        /// <summary>Bitwise equality of every field. Prediction is reconciled whenever this is false.</summary>
        public static bool SameMotor(in MotorState a, in MotorState b)
        {
            return a.Position.Equals(b.Position) && a.Velocity.Equals(b.Velocity) && a.Mode == b.Mode &&
                   a.Crouched == b.Crouched && a.Sprinting == b.Sprinting && a.Grounded == b.Grounded &&
                   a.LandSlideUsed == b.LandSlideUsed && a.GroundNormal.Equals(b.GroundNormal) && a.Tick == b.Tick &&
                   a.LastGroundedTick == b.LastGroundedTick && a.JumpBufferedTick == b.JumpBufferedTick &&
                   a.LastSlideBoostTick == b.LastSlideBoostTick && a.LandTick == b.LandTick &&
                   a.LandVelocity.Equals(b.LandVelocity) && a.AirPeakY.Equals(b.AirPeakY) && a.UphillTicks == b.UphillTicks && a.ActionTick == b.ActionTick &&
                   a.ActionStartY.Equals(b.ActionStartY) && a.ActionTargetY.Equals(b.ActionTargetY) &&
                   a.ActionForward.Equals(b.ActionForward) && a.ActionKeepVelocity.Equals(b.ActionKeepVelocity) &&
                   a.ShootReadyTick == b.ShootReadyTick && a.Braking == b.Braking && a.Carving == b.Carving &&
                   a.WallHitTick == b.WallHitTick && a.WallHitSpeed.Equals(b.WallHitSpeed) && a.WallHitNormal.Equals(b.WallHitNormal) &&
                   a.WallKicks == b.WallKicks && a.LastWallKickTick == b.LastWallKickTick && a.LastWallKickNormal.Equals(b.LastWallKickNormal) && a.LastSlideJumpTick == b.LastSlideJumpTick &&
                   a.Hand.Equals(b.Hand) && a.Weapon.Equals(b.Weapon) &&
                   a.Damage == b.Damage && a.Dead == b.Dead && a.RespawnTick == b.RespawnTick &&
                   a.Nade0.Equals(b.Nade0) && a.Nade1.Equals(b.Nade1) && a.Nade2.Equals(b.Nade2) && a.Nade3.Equals(b.Nade3) &&
                   a.NadeSeq == b.NadeSeq && a.LauncherReadyTick == b.LauncherReadyTick &&
                   a.PunchTick == b.PunchTick && a.PunchBufferTick == b.PunchBufferTick && a.ChargeTick == b.ChargeTick;
        }

        private static MoveMode ReadMode(ref ByteReader r)
        {
            byte b = r.U8();
            if (b > (byte)MoveMode.Mantle) throw new NetFormatException("Bad move mode.");
            return (MoveMode)b;
        }

        private static void Expect(ref ByteReader r, MsgType t)
        {
            if (r.U8() != (byte)t) throw new NetFormatException("Unexpected message type.");
        }
    }
}
