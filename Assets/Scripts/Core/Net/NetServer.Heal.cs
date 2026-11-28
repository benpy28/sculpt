using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// Server healing and the sculpt bar (Ben 2026-11-21): heal and reinforce presses arrive in the player's command,
    /// are checked against the server's tiles and bar, and run on the server's clock. The bar refills by itself and
    /// faster from damage dealt to enemy players. Heal events travel in the Tiles message.
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Heals and reinforces in progress (null on maps without tiles). The server owns them.</summary>
        public TileHealing Healing { get; private set; }
        /// <summary>Heal events of the last tick (tests and the host's effects).</summary>
        public readonly List<HealEvent> HealEvents = new List<HealEvent>();
        /// <summary>The client's eye can be a little behind the server's: allow this much extra reach on a heal press.</summary>
        public double HealReachSlack = 0.75;

        /// <summary>Bars when no sculpt system is attached (tests, maps without one), ticked with <see cref="BarConfig"/>.</summary>
        private readonly Dictionary<int, SculptBar> _ownBars = new Dictionary<int, SculptBar>();
        /// <summary>Refill numbers for <see cref="_ownBars"/> (the breach numbers by default).</summary>
        public readonly SculptConfig BarConfig = NewBarConfig();
        private readonly List<HealerStatus> _healers = new List<HealerStatus>();
        private readonly List<HealEvent> _healSend = new List<HealEvent>();

        private static SculptConfig NewBarConfig()
        {
            var s = new SculptConfig();
            new BreachConfig().ApplyBarTo(s);
            return s;
        }

        private void InitHealing()
        {
            if (Walls != null) Healing = new TileHealing(Walls);
        }

        /// <summary>A player's sculpt bar (the sculpt system's when one is attached).</summary>
        public SculptBar BarOf(int playerId)
        {
            if (Sculpt != null) return Sculpt.BarOf(playerId);
            if (!_ownBars.TryGetValue(playerId, out SculptBar b)) _ownBars[playerId] = b = new SculptBar();
            return b;
        }

        private void TickOwnBars()
        {
            if (Sculpt != null) return; // the sculpt system ticks its own
            foreach (ServerPlayer p in _players) BarOf(p.Id).Tick(Tick, BarConfig);
        }

        private HealerStatus StatusOf(ServerPlayer p) => new HealerStatus
        {
            Id = p.Id,
            Alive = !p.State.Dead,
            InSculptMode = Movement.Hand.KeepsHealing(p.State.Hand),
            Eye = p.State.Position + new Vec3d(0, PlayerMovement.EyeOf(p.State, Movement), 0),
            Box = PlayerMovement.Box(p.State.Position, Movement.HalfWidth, PlayerMovement.HeightOf(p.State, Movement)),
        };

        /// <summary>A heal press, right after the player's movement on the tick it was pressed.</summary>
        private void HandleHeal(ServerPlayer p, in InputCmd cmd)
        {
            if (Healing == null) return;
            HealerStatus s = StatusOf(p);
            // Quick heal: any time you are not disarmed. With block moves on: sculpt mode proper (entering doesn't count).
            s.InSculptMode = Movement.Hand.CanHeal(p.State.Hand);
            Healing.TryBegin(Grid, s, cmd.Hand.HealTile, cmd.Hand.HealCell, cmd.Hand.HealReinforce ? HealKind.Reinforce : HealKind.Heal, BarOf(p.Id), Tick, HealReachSlack);
        }

        private readonly List<Aabb> _doorBodies = new List<Aabb>();
        private readonly List<int> _doorLeaves = new List<int>();

        /// <summary>
        /// A door press, right after the player's movement on the tick it was pressed: in reach (with the same lag
        /// slack as heals), not within DoorToggleSeconds of the door's last press, and when shutting, nobody (alive)
        /// inside a leaf's space. Refusals change nothing (the client
        /// puts its door back when no answer comes).
        /// </summary>
        private void HandleDoor(ServerPlayer p, in InputCmd cmd)
        {
            if (Walls == null || !DoorRules.IsLeaf(Walls, cmd.Hand.DoorTile)) return;
            Vec3d eye = p.State.Position + new Vec3d(0, PlayerMovement.EyeOf(p.State, Movement), 0);
            if (DoorRules.DistanceTo(Walls.BoundsOf(cmd.Hand.DoorTile), eye) > Walls.Config.DoorReach + HealReachSlack) return;
            _doorBodies.Clear();
            foreach (ServerPlayer o in _players)
                if (!o.State.Dead) _doorBodies.Add(PlayerMovement.Box(o.State.Position, Movement.HalfWidth, PlayerMovement.HeightOf(o.State, Movement)));
            if (!DoorRules.CanSet(Walls, cmd.Hand.DoorTile, cmd.Hand.DoorOpen, _doorBodies, _doorLeaves)) return;
            // Toggle cooldown: no open, shoot and shut within a tick (two players on one tick included). Quiet refusal.
            if (DoorRules.Cooling(Walls, cmd.Hand.DoorTile, Tick, _doorLeaves)) return;
            Walls.Tick = Tick;
            DoorRules.Set(Walls, cmd.Hand.DoorTile, cmd.Hand.DoorOpen, TileCause.Door, p.Id, _doorLeaves);
        }

        /// <summary>After everyone moved and fought: finish or cancel heals.</summary>
        private void StepHealing()
        {
            if (Healing == null) return;
            _healers.Clear();
            foreach (ServerPlayer p in _players) _healers.Add(StatusOf(p));
            Healing.Step(Tick, _healers, BarOf);
        }

        /// <summary>
        /// Damage from one player to another. Every hit goes through here so the attacker earns bar for the damage
        /// actually dealt to an enemy (a kill shot only counts the health that was left).
        /// </summary>
        private bool Hurt(ServerPlayer attacker, ServerPlayer victim, int damage)
        {
            if (Match.Protects) return false; // countdown and results: nobody can be hurt
            int before = victim.State.Health(Weapons);
            bool killed = WeaponRules.ApplyDamage(ref victim.State, damage, Tick, Weapons);
            int dealt = Math.Max(0, before - victim.State.Health(Weapons));
            if (attacker != null && attacker != victim && attacker.Team != victim.Team && dealt > 0)
            {
                victim.LastHurtBy = attacker.Id; // a fall soon after still credits them (OnKilled)
                victim.LastHurtTick = Tick;
                NotePingHurt(attacker, victim);
            }
            if (attacker != null && attacker != victim && attacker.Team != victim.Team && dealt > 0 && Walls != null)
                BarOf(attacker.Id).GainForDamage(dealt, Walls.Config.BarPer10Damage);
            return killed;
        }

        /// <summary>This tick's heal events, then clears them (they ride in the Tiles message).</summary>
        private List<HealEvent> TakeHealEvents()
        {
            HealEvents.Clear();
            if (Healing == null) return null;
            HealEvents.AddRange(Healing.Events);
            Healing.Events.Clear();
            return HealEvents;
        }

        /// <summary>Every heal still running, as Began events (late joiners).</summary>
        private List<HealEvent> RunningHeals()
        {
            _healSend.Clear();
            if (Healing == null) return _healSend;
            foreach (HealJob j in Healing.Jobs)
                _healSend.Add(new HealEvent { Type = HealEventType.Began, Player = j.Player, Tile = j.Tile, Cell = j.Cell, Kind = j.Kind, Tick = Tick, EndTick = j.EndTick });
            _healSend.Sort((a, b) => a.Player.CompareTo(b.Player));
            return _healSend;
        }
    }
}
