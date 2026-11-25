using System;
using System.Collections.Generic;

namespace Sculpt.Core.Net
{
    /// <summary>
    /// Server combat: hitscan with lag compensation, damage, deaths, and the per-tick combat message.
    /// Shots are fired inside each player's simulation (so the client predicts them on the same tick) and resolved here,
    /// after every player has moved: the shooter fires from their current eye, at targets rewound to what the shooter was
    /// drawing (InputCmd.ViewTick + ViewFrac, at most WeaponsConfig.MaxRewindTicks back), against the current terrain.
    /// Cover sculpted on the server stops bullets from that tick on, even if the shooter hasn't seen it yet.
    /// </summary>
    public sealed partial class NetServer
    {
        /// <summary>Where a dead player's sculpt body waits, so pieces and throws ignore it.</summary>
        public static readonly Vec3d ParkedBody = new Vec3d(0, -10000, 0);

        private readonly List<CombatEvent> _combat = new List<CombatEvent>();
        private readonly List<CombatEvent> _combatTo = new List<CombatEvent>();
        private readonly List<HitTarget> _targets = new List<HitTarget>();
        private readonly List<ShotVictim> _victims = new List<ShotVictim>();
        private readonly List<BulletHit> _pellets = new List<BulletHit>();
        private readonly List<TileChange> _tileScratch = new List<TileChange>();

        /// <summary>Every combat event of the last tick (tests and the host's HUD).</summary>
        public IReadOnlyList<CombatEvent> CombatEvents => _combat;

        private WeaponsConfig Weapons => Movement.Weapons;

        private void ResolveShots()
        {
            foreach (ServerPlayer p in _players)
            {
                for (int k = 0; k < p.Shots.Count; k++)
                {
                    WeaponShot shot = p.Shots[k];
                    shot.ShooterId = p.Id;
                    p.Shots[k] = shot;
                    _combat.Add(new CombatEvent
                    {
                        Type = CombatEventType.Shot, Attacker = p.Id, Source = (DamageSource)shot.Weapon,
                        Origin = shot.Origin, Aim = shot.Aim, Spread = (float)shot.Spread, Seed = shot.Seed,
                    });
                    BuildTargets(p, p.LastCmd.ViewTick, p.LastCmd.ViewFrac);
                    WeaponStats st = Weapons.Of(shot.Weapon);
                    Hitscan.Resolve(Grid, shot, st, Weapons, p.Team, _targets, _victims, Walls != null ? _pellets : null);
                    Walls?.ApplyShot(_pellets, shot.Weapon, p.Id);
                    foreach (ShotVictim v in _victims)
                    {
                        ServerPlayer victim = FindById(v.Id);
                        if (victim == null || victim.State.Dead) continue;
                        int dmg = v.Damage;
                        if (dmg <= 0) continue;
                        bool killed = Hurt(p, victim, dmg);
                        _combat.Add(new CombatEvent
                        {
                            Type = CombatEventType.Hit, Attacker = p.Id, Victim = v.Id, Source = (DamageSource)shot.Weapon,
                            Damage = dmg, Pellets = (byte)v.Pellets, HeadPellets = (byte)v.HeadPellets, Point = v.Point,
                            Killed = killed, HealthAfter = victim.State.Health(Weapons), Origin = shot.Origin,
                        });
                        if (killed) OnKilled(victim, p.Id, (DamageSource)shot.Weapon, v.HeadPellets > 0);
                    }
                }
                p.Shots.Clear();
            }
        }

        /// <summary>
        /// Punch hits from this tick, against players as the puncher saw them (same rewind as shots) and the current
        /// tiles: 30 to the nearest player in reach, else half an unreinforced tile (a quarter of a reinforced one).
        /// </summary>
        private void ResolvePunches()
        {
            PunchConfig pc = Movement.Punch;
            foreach (ServerPlayer p in _players)
            {
                foreach (PunchSwing swing in p.Punches)
                {
                    PunchSwing sw = swing;
                    sw.PlayerId = p.Id;
                    BuildTargets(p, p.LastCmd.ViewTick, p.LastCmd.ViewFrac);
                    BulletHit h = PunchRules.Resolve(Grid, Walls, sw, pc, _targets, p.Team, Weapons.FriendlyFire, out int tile);
                    if (h.Kind == HitKind.Player)
                    {
                        ServerPlayer victim = FindById(_targets[h.Target].Id);
                        if (victim == null || victim.State.Dead || pc.PlayerDamage <= 0) continue;
                        bool killed = Hurt(p, victim, pc.PlayerDamage);
                        _combat.Add(new CombatEvent
                        {
                            Type = CombatEventType.Hit, Attacker = p.Id, Victim = victim.Id, Source = DamageSource.Punch,
                            Damage = pc.PlayerDamage, Pellets = 1, Point = h.Point, Killed = killed,
                            HealthAfter = victim.State.Health(Weapons), Origin = sw.Origin,
                        });
                        if (killed) OnKilled(victim, p.Id, DamageSource.Punch, false);
                    }
                    else if (tile >= 0)
                        Walls.Damage(tile, PunchRules.TileDamage(Walls[tile].State, Walls.Config), h.Point, TileCause.Punch, p.Id, Walls.Config.PunchHitRadius);
                }
                p.Punches.Clear();
            }
        }

        private readonly List<int> _blastTiles = new List<int>();

        /// <summary>
        /// Grenade throws and blasts from this tick. The thrower's own push already happened inside their (predicted)
        /// simulation; here the blast hurts enemies, pushes everyone else (less), and breaks the tiles around it. Players
        /// are checked before the tiles break, so the wall the grenade hit still shields whoever is behind it.
        /// </summary>
        private void ResolveGrenades()
        {
            foreach (ServerPlayer p in _players)
            {
                foreach (GrenadeEvent e in p.Nades)
                {
                    GrenadeConfig gc = Movement.Items.Of(e.Kind);
                    DamageSource src = SourceOf(e.Kind);
                    if (e.Type == GrenadeEventType.Thrown)
                    {
                        _combat.Add(new CombatEvent { Type = CombatEventType.Grenade, Attacker = p.Id, Source = src, Nade = e.Type, NadeSeq = e.Seq, Origin = e.Position, Aim = e.Velocity });
                        continue;
                    }
                    _combat.Add(new CombatEvent { Type = CombatEventType.Grenade, Attacker = p.Id, Source = src, Nade = e.Type, NadeSeq = e.Seq, Point = e.Position });
                    if (e.Type != GrenadeEventType.Exploded) continue; // stuck or fizzled: nothing to resolve
                    foreach (ServerPlayer o in _players)
                    {
                        if (o == p || o.State.Dead) continue;
                        double height = PlayerMovement.HeightOf(o.State, Movement);
                        bool enemy = o.Team != p.Team || Weapons.FriendlyFire;
                        int dmg = enemy ? GrenadeRules.Damage(Grid, e.Position, o.State.Position, height, gc, Movement.HalfWidth) : 0;
                        Vec3d push = GrenadeRules.Push(Grid, e.Position, o.State.Position, height, gc, gc.OtherKnock, Movement.HalfWidth);
                        if (push.LengthSquared > 0) GrenadeRules.Knock(ref o.State, push);
                        if (dmg <= 0) continue;
                        bool killed = Hurt(p, o, dmg);
                        _combat.Add(new CombatEvent
                        {
                            Type = CombatEventType.Hit, Attacker = p.Id, Victim = o.Id, Source = src,
                            Damage = dmg, Pellets = 1, Point = GrenadeRules.Chest(o.State.Position, height), Killed = killed,
                            HealthAfter = o.State.Health(Weapons), Origin = e.Position,
                        });
                        if (killed) OnKilled(o, p.Id, src, false);
                    }
                    if (Walls == null) continue;
                    GrenadeRules.TilesInBlast(Walls, e.Position, gc, _blastTiles);
                    Walls.Blast(e.Position, _blastTiles, Walls.Config.BlastTileDamage(e.Kind), Walls.Config.BlastHoleRadius(e.Kind), TileCause.Grenade, p.Id);
                }
                p.Nades.Clear();
            }
        }

        /// <summary>The damage source (and the wire's kind tag) of a projectile kind.</summary>
        public static DamageSource SourceOf(ThrowKind k) => k == ThrowKind.C4 ? DamageSource.C4 : k == ThrowKind.Launcher ? DamageSource.Launcher : DamageSource.Grenade;

        /// <summary>The projectile kind a grenade event's source names.</summary>
        public static ThrowKind KindOf(DamageSource s) => s == DamageSource.C4 ? ThrowKind.C4 : s == DamageSource.Launcher ? ThrowKind.Launcher : ThrowKind.Grenade;

        /// <summary>Everyone but the shooter, as the shooter saw them at <paramref name="viewTick"/> + frac/256.</summary>
        private void BuildTargets(ServerPlayer shooter, int viewTick, byte viewFrac)
        {
            _targets.Clear();
            double view = viewTick + viewFrac / 256.0;
            view = Math.Max(Tick - Weapons.MaxRewindTicks, Math.Min(Tick, view));
            foreach (ServerPlayer o in _players)
            {
                if (o == shooter) continue;
                if (!TryGetRewound(o, view, out Vec3d feet, out double height, out bool dead))
                {
                    feet = o.State.Position;
                    height = PlayerMovement.HeightOf(o.State, Movement);
                    dead = o.State.Dead;
                }
                if (dead || o.State.Dead) continue;
                _targets.Add(HitTarget.Of(o.Id, o.Team, feet, Movement.HalfWidth, height, Weapons));
            }
        }

        /// <summary>
        /// A player's pose at a fractional past tick, interpolated exactly like the client draws remote players
        /// (linear position, crouch and death from the nearer tick, no interpolation across a teleport).
        /// </summary>
        public bool TryGetRewound(ServerPlayer p, double view, out Vec3d feet, out double height, out bool dead)
        {
            int a = (int)Math.Floor(view);
            double f = view - a;
            int n = p.PosHistory.Length;
            int ia = ((a % n) + n) % n, ib = (((a + 1) % n) + n) % n;
            bool hasA = p.PosHistoryTick[ia] == a, hasB = p.PosHistoryTick[ib] == a + 1;
            if (!hasA && !hasB) { feet = default; height = 0; dead = false; return false; }
            if (!hasB || f <= 0) { feet = p.PosHistory[ia]; height = p.HeightHistory[ia]; dead = p.DeadHistory[ia]; return true; }
            if (!hasA) { feet = p.PosHistory[ib]; height = p.HeightHistory[ib]; dead = p.DeadHistory[ib]; return true; }
            Vec3d pa = p.PosHistory[ia], pb = p.PosHistory[ib];
            int near = f < 0.5 ? ia : ib;
            height = p.HeightHistory[near];
            dead = p.DeadHistory[near];
            feet = (pb - pa).LengthSquared > 16.0 ? p.PosHistory[near] : pa + (pb - pa) * f;
            return true;
        }

        private void ThrowDamage(ServerPlayer victim, int damage, Vec3d point, int thrower)
        {
            if (victim.State.Dead || damage <= 0) return;
            bool killed = Hurt(FindById(thrower), victim, damage);
            _combat.Add(new CombatEvent
            {
                Type = CombatEventType.Hit, Attacker = thrower, Victim = victim.Id, Source = DamageSource.Throw,
                Damage = damage, Pellets = 1, Point = point, Killed = killed, HealthAfter = victim.State.Health(Weapons),
            });
            if (killed) OnKilled(victim, thrower, DamageSource.Throw, false);
        }

        /// <summary>How long after an enemy last hurt you a death with no killer still counts as their kill (8 s).</summary>
        public const int KillCreditTicks = (8 * 60) * SimTime.Per60;

        private void OnKilled(ServerPlayer victim, int killer, DamageSource source, bool headshot)
        {
            victim.Deaths++;
            // A death with no killer (a fall, your own blast) goes to the last enemy who hurt you within KillCreditTicks
            // (Ben's play-test: fell on 10 health after a fight and nobody got the kill). Otherwise it is just a death.
            if ((killer == 0 || killer == victim.Id) && victim.LastHurtBy != 0 && Tick - victim.LastHurtTick <= KillCreditTicks)
                killer = victim.LastHurtBy;
            // In a 1v1 match every death is the other player's kill, however it happened and however long ago they last
            // hurt you (Ben 2026-11-25: "its a 1v1 so credit the other player"). Other modes keep the 8 s rule above.
            if ((killer == 0 || killer == victim.Id) && Match.CountsKills) killer = TheOpponent(victim);
            victim.LastHurtBy = 0;
            ServerPlayer k = killer != 0 && killer != victim.Id ? FindById(killer) : null;
            if (k != null) k.Kills++;
            OnMatchKill(victim, k);
            victim.Body.Position = ParkedBody;
            victim.Body.Kick = Vec3d.Zero;
            _combat.Add(new CombatEvent { Type = CombatEventType.Kill, Attacker = killer, Victim = victim.Id, Source = source, HeadPellets = (byte)(headshot ? 1 : 0) });
        }

        /// <summary>This tick's tile changes to everyone (one reliable message), then forgets them.</summary>
        private void SendTiles()
        {
            List<HealEvent> heals = TakeHealEvents();
            if (Walls == null || (Walls.Changes.Count == 0 && (heals == null || heals.Count == 0))) return;
            for (int start = 0; start == 0 || start < Walls.Changes.Count; start += TileProtocol.MaxChanges)
            {
                _tileScratch.Clear();
                for (int k = start; k < Walls.Changes.Count && k < start + TileProtocol.MaxChanges; k++) _tileScratch.Add(Walls.Changes[k]);
                _w.Clear();
                TileProtocol.Write(_w, new TilesMsg { Tick = Tick, Changes = _tileScratch, Heals = start == 0 ? heals : null });
                byte[] data = _w.ToArray();
                foreach (ServerPlayer to in _players) Send(to.ConnectionId, data, true);
            }
            Walls.Changes.Clear();
        }

        /// <summary>Late join: the current state of every tile that ever changed (the rest are as built).</summary>
        private void SendTileJoinState(ServerPlayer to)
        {
            if (Walls == null) return;
            var all = new List<TileChange>();
            for (int i = 0; i < Walls.Count; i++)
            {
                WallTile t = Walls[i];
                if (t.Changed)
                    all.Add(new TileChange
                    {
                        Tick = Tick, Tile = i, To = t.State, Health = t.Health, Cause = TileCause.Server, Open = t.Open,
                        Patch = t.Patch, Steel = t.Steel, Hp = Walls.HasCells(i) ? Walls.CellHealths(i) : null,
                    });
            }
            List<HealEvent> running = RunningHeals();
            if (all.Count == 0 && running.Count == 0) return;
            var w = new ByteWriter(16 + all.Count * 8 + running.Count * 8);
            TileProtocol.Write(w, new TilesMsg { Tick = Tick, Join = true, Changes = all, Heals = running });
            Send(to.ConnectionId, w.ToArray(), true);
        }

        /// <summary>One reliable message per tick with events. Shots skip their shooter; health is told only to the two involved.</summary>
        private void SendCombat()
        {
            if (_combat.Count == 0) return;
            foreach (ServerPlayer to in _players)
            {
                _combatTo.Clear();
                foreach (CombatEvent e in _combat)
                {
                    if ((e.Type == CombatEventType.Shot || e.Type == CombatEventType.Grenade) && e.Attacker == to.Id) continue;
                    CombatEvent c = e;
                    if (c.Type == CombatEventType.Hit && c.Attacker != to.Id && c.Victim != to.Id) c.HealthAfter = -1;
                    _combatTo.Add(c);
                    if (_combatTo.Count == CombatProtocol.MaxEvents) break;
                }
                if (_combatTo.Count == 0) continue;
                _w.Clear();
                CombatProtocol.Write(_w, new CombatMsg { Tick = Tick, Events = _combatTo });
                Send(to.ConnectionId, _w.ToArray(), true);
            }
        }
    }
}
