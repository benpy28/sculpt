using System;
using System.Collections.Generic;

namespace Sculpt.Core
{
    /// <summary>
    /// Everything that can sit in a hotbar slot (Ben 2026-11-21: every item is a hotbar item you pick up or drop with
    /// the interact key; only the punch has its own bind). Guns keep their <see cref="Weapon"/> number + 1.
    /// </summary>
    public enum Item : byte
    {
        None = 0,
        Pump = 1,
        Smg = 2,
        Ar = 3,
        Pistol = 4,
        Grenade = 5,
        /// <summary>Remote C4 (friend demo): a breaching charge you throw, that sticks, and that you set off.</summary>
        C4 = 6,
        /// <summary>Grenade launcher: few rounds, slow and clunky, rounds only arm after a few metres.</summary>
        Launcher = 7,
    }

    public static class Items
    {
        public const int Slots = 5;
        /// <summary>The snapshot carries the held item in 3 bits, so this stays at most 7.</summary>
        public const Item Last = Item.Launcher;

        public static bool IsGun(Item i) => i >= Item.Pump && i <= Item.Pistol;
        /// <summary>The gun of a gun item. Only call with <see cref="IsGun"/> true.</summary>
        public static Weapon GunOf(Item i) => (Weapon)((int)i - 1);
        public static Item Of(Weapon w) => (Item)((int)w + 1);
        /// <summary>
        /// Items that carry a count in their slot: grenades and C4 (how many), the launcher (rounds left). Picking up more
        /// of one you have adds to it up to the carry limit. Guns are one per slot, and one of each gun at most.
        /// </summary>
        public static bool Stacks(Item i) => i == Item.Grenade || i == Item.C4 || i == Item.Launcher;
        /// <summary>Items that leave your hand as a projectile (<see cref="GrenadeRules"/>).</summary>
        public static bool Throws(Item i) => i == Item.Grenade || i == Item.C4 || i == Item.Launcher;
        public static ThrowKind KindOf(Item i) => i == Item.C4 ? ThrowKind.C4 : i == Item.Launcher ? ThrowKind.Launcher : ThrowKind.Grenade;
        public static Item ItemOf(ThrowKind k) => k == ThrowKind.C4 ? Item.C4 : k == ThrowKind.Launcher ? Item.Launcher : Item.Grenade;

        public static string Name(Item i)
        {
            switch (i)
            {
                case Item.Pump: return "Pump";
                case Item.Smg: return "SMG";
                case Item.Ar: return "AR";
                case Item.Pistol: return "Pistol";
                case Item.Grenade: return "Grenade";
                case Item.C4: return "C4";
                case Item.Launcher: return "Launcher";
                default: return "";
            }
        }
    }

    /// <summary>
    /// The five hotbar slots: a plain struct inside <see cref="HandState"/>, so it is predicted, sent and reconciled
    /// with the hand. At most one of each gun and one stack of each stacking item. N0..N4 are the slots' counts
    /// (grenades or C4 in the stack, launcher rounds left; 0 for guns). A C4 slot at 0 is the detonator: it stays
    /// while any of your charges is still out.
    /// </summary>
    public struct Bag
    {
        public Item S0, S1, S2, S3, S4;
        public byte N0, N1, N2, N3, N4;

        /// <summary>The fixed starting loadout: pump, SMG, AR, pistol, two grenades in slot 5.</summary>
        public const int StartGrenades = 2;
        public static Bag Standard => new Bag { S0 = Item.Pump, S1 = Item.Smg, S2 = Item.Ar, S3 = Item.Pistol, S4 = Item.Grenade, N4 = StartGrenades };

        /// <summary>The raw count byte of a slot.</summary>
        public byte Count(int slot)
        {
            switch (slot)
            {
                case 0: return N0;
                case 1: return N1;
                case 2: return N2;
                case 3: return N3;
                case 4: return N4;
                default: return 0;
            }
        }

        public void SetCount(int slot, int count)
        {
            byte v = (byte)Math.Max(0, Math.Min(255, count));
            switch (slot)
            {
                case 0: N0 = v; break;
                case 1: N1 = v; break;
                case 2: N2 = v; break;
                case 3: N3 = v; break;
                case 4: N4 = v; break;
                default: throw new ArgumentOutOfRangeException(nameof(slot));
            }
        }

        /// <summary>Grenades in the grenade slot (0 when there is none). Setting it with no grenade slot does nothing.</summary>
        public byte Grenades
        {
            get => CountOf(Item.Grenade);
            set { int k = SlotOf(Item.Grenade); if (k >= 0) SetCount(k, value); }
        }

        /// <summary>How many of a stacking item you carry (0 if none).</summary>
        public byte CountOf(Item i)
        {
            int k = SlotOf(i);
            return k < 0 ? (byte)0 : Count(k);
        }

        public Item this[int slot]
        {
            get
            {
                switch (slot)
                {
                    case 0: return S0;
                    case 1: return S1;
                    case 2: return S2;
                    case 3: return S3;
                    case 4: return S4;
                    default: return Item.None;
                }
            }
            set
            {
                switch (slot)
                {
                    case 0: S0 = value; break;
                    case 1: S1 = value; break;
                    case 2: S2 = value; break;
                    case 3: S3 = value; break;
                    case 4: S4 = value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(slot));
                }
            }
        }

        /// <summary>Slot holding this item, or -1.</summary>
        public int SlotOf(Item i)
        {
            if (i == Item.None) return -1;
            for (int k = 0; k < Items.Slots; k++) if (this[k] == i) return k;
            return -1;
        }

        public int FirstEmpty()
        {
            for (int k = 0; k < Items.Slots; k++) if (this[k] == Item.None) return k;
            return -1;
        }

        /// <summary>How many of the item are in a slot (guns 1, stacking items their count).</summary>
        public int CountIn(int slot)
        {
            Item i = this[slot];
            if (i == Item.None) return 0;
            return Items.Stacks(i) ? Count(slot) : 1;
        }

        /// <summary>Empties a slot (and its count).</summary>
        public void Clear(int slot)
        {
            this[slot] = Item.None;
            SetCount(slot, 0);
        }

        public bool Equals(in Bag o) => S0 == o.S0 && S1 == o.S1 && S2 == o.S2 && S3 == o.S3 && S4 == o.S4 &&
                                        N0 == o.N0 && N1 == o.N1 && N2 == o.N2 && N3 == o.N3 && N4 == o.N4;
    }

    /// <summary>Item numbers (Breach Settings writes these). Ticks at 60 Hz, distances in metres.</summary>
    public sealed class ItemConfig
    {
        /// <summary>Most grenades one player carries (Ben: carry 2).</summary>
        public int GrenadeCarry = 2;
        /// <summary>Most C4 charges one player carries.</summary>
        public int C4Carry = 2;
        /// <summary>Most launcher rounds one player carries.</summary>
        public int LauncherCarry = 4;
        /// <summary>Pickup reach, eye to the item.</summary>
        public double Reach = 2.5;
        /// <summary>An item counts as aimed at within this angle of the crosshair (the closest one to the crosshair wins).</summary>
        public double AimConeDegrees = 25;
        /// <summary>The server accepts a pickup up to this much further than <see cref="Reach"/> (prediction drift).</summary>
        public double ServerReachSlack = 0.75;
        /// <summary>Hold the interact key this long (not aiming at an item) to drop the held item.</summary>
        public int DropHoldTicks = 18 * SimTime.Per60;
        /// <summary>Whoever dropped an item can't pick it back up for this long (no accidental double press).</summary>
        public int DropperLockTicks = 18 * SimTime.Per60;
        /// <summary>Dropped items land this far in front of you (or at your feet if that spot is blocked).</summary>
        public double DropForward = 0.9;
        /// <summary>Oldest items on the ground disappear past this many.</summary>
        public int MaxGroundItems = 64;
        /// <summary>Throw, flight and blast numbers.</summary>
        public GrenadeConfig Grenade = new GrenadeConfig();
        public GrenadeConfig C4 = GrenadeConfig.DefaultC4();
        public GrenadeConfig Launcher = GrenadeConfig.DefaultLauncher();

        /// <summary>The flight and blast numbers of one kind.</summary>
        public GrenadeConfig Of(ThrowKind k) => k == ThrowKind.C4 ? C4 : k == ThrowKind.Launcher ? Launcher : Grenade;

        /// <summary>Most of a stacking item you can carry (1 for anything else).</summary>
        public int Carry(Item i) => i == Item.Grenade ? GrenadeCarry : i == Item.C4 ? C4Carry : i == Item.Launcher ? LauncherCarry : 1;
    }

    /// <summary>One item lying on the ground. Items don't move: they rest where they were dropped.</summary>
    public struct GroundItem
    {
        public int Id;
        public Item Kind;
        /// <summary>Grenades in the stack (1 for a gun).</summary>
        public int Count;
        /// <summary>Guns: rounds fired from the magazine since it was full, so a dropped gun keeps its ammo.</summary>
        public int AmmoUsed;
        /// <summary>The point it rests on (bottom centre).</summary>
        public Vec3d Position;
        public int DroppedBy;
        public int DroppedTick;

        /// <summary>Where aiming and reach are measured to (a little above the floor).</summary>
        public Vec3d Centre => Position + new Vec3d(0, 0.15, 0);
    }

    /// <summary>
    /// The items on the ground. The server owns them; a client keeps a copy that only the server's Items messages
    /// change. Every change goes through <see cref="Add"/> or <see cref="Remove"/>/<see cref="SetCount"/> and bumps
    /// <see cref="Version"/>, so the server knows to send and the view knows to redraw.
    /// </summary>
    public sealed class GroundItems
    {
        private readonly List<GroundItem> _items = new List<GroundItem>();
        public int NextId = 1;
        /// <summary>Changes with every add, remove or count change.</summary>
        public int Version { get; private set; }

        public int Count => _items.Count;
        public GroundItem this[int index] => _items[index];
        public IReadOnlyList<GroundItem> All => _items;

        public int IndexOf(int id)
        {
            for (int i = 0; i < _items.Count; i++) if (_items[i].Id == id) return i;
            return -1;
        }

        public bool TryGet(int id, out GroundItem item)
        {
            int i = IndexOf(id);
            item = i >= 0 ? _items[i] : default;
            return i >= 0;
        }

        /// <summary>Adds an item with a fresh id (or the given one, for clients copying the server). Returns its id.</summary>
        public int Add(GroundItem item, int maxItems = int.MaxValue)
        {
            if (item.Id == 0) item.Id = NextId++;
            else if (item.Id >= NextId) NextId = item.Id + 1;
            while (_items.Count >= Math.Max(1, maxItems)) _items.RemoveAt(0); // oldest first
            _items.Add(item);
            Version++;
            return item.Id;
        }

        public bool Remove(int id)
        {
            int i = IndexOf(id);
            if (i < 0) return false;
            _items.RemoveAt(i);
            Version++;
            return true;
        }

        public void SetCount(int id, int count)
        {
            int i = IndexOf(id);
            if (i < 0) return;
            if (count <= 0) { Remove(id); return; }
            GroundItem g = _items[i];
            if (g.Count == count) return;
            g.Count = count;
            _items[i] = g;
            Version++;
        }

        /// <summary>Replaces everything (a client copying the server's full list).</summary>
        public void ReplaceAll(IReadOnlyList<GroundItem> items)
        {
            _items.Clear();
            foreach (GroundItem g in items)
            {
                _items.Add(g);
                if (g.Id >= NextId) NextId = g.Id + 1;
            }
            Version++;
        }
    }

    public enum PickupResult : byte
    {
        Ok = 0,
        /// <summary>Picked up into the held slot, which already had something: that went on the ground.</summary>
        Swapped = 1,
        NoItem = 2,
        TooFar = 3,
        /// <summary>A wall is between you and it.</summary>
        Blocked = 4,
        /// <summary>You already have that gun, or a full grenade stack.</summary>
        AlreadyHave = 5,
        /// <summary>Dead, disarmed, or you just dropped it.</summary>
        NotNow = 6,
    }

    /// <summary>What a pickup would do (shown in the prompt, applied by the server).</summary>
    public struct PickupPlan
    {
        public PickupResult Result;
        /// <summary>The slot it goes into.</summary>
        public int Slot;
        /// <summary>How many it takes (grenades: up to the carry limit).</summary>
        public int Take;
        /// <summary>What goes on the ground in exchange (a swap), or None.</summary>
        public Item SwapOut;

        public bool Allowed => Result == PickupResult.Ok || Result == PickupResult.Swapped;
    }

    /// <summary>
    /// Pickup and drop rules. Pure functions of the player state and the ground list, so the client's prompt and the
    /// server's decision use the same code.
    /// - A pickup goes into the matching grenade stack, else the held slot if it is empty, else the first empty slot,
    ///   else it swaps with the held item (the held item lands where the picked one was).
    /// - At most one of each gun. Grenades stack to <see cref="ItemConfig.GrenadeCarry"/>; the rest stay on the ground.
    /// - Dropping puts the whole held slot on the ground in front of you and draws your previous slot.
    /// </summary>
    public static class ItemRules
    {
        /// <summary>
        /// The item the crosshair picks: within reach of the eye, inside the aim cone, closest to the crosshair, with
        /// nothing solid in between. -1 if none.
        /// </summary>
        public static int FindTarget(GroundItems items, ITerrain terrain, Vec3d eye, Vec3d look, ItemConfig cfg)
        {
            if (items == null || items.Count == 0) return -1;
            double lookLen = look.Length;
            if (lookLen < 1e-9) return -1;
            Vec3d f = look * (1.0 / lookLen);
            double cosCone = Math.Cos(cfg.AimConeDegrees * Math.PI / 180);
            int best = -1;
            double bestCos = -2;
            for (int i = 0; i < items.Count; i++)
            {
                GroundItem g = items[i];
                Vec3d to = g.Centre - eye;
                double d = to.Length;
                if (d > cfg.Reach) continue;
                double c = d < 1e-6 ? 1 : to.Dot(f) / d;
                if (c < cosCone || c <= bestCos) continue;
                if (!Visible(terrain, eye, g.Centre)) continue;
                best = i;
                bestCos = c;
            }
            return best;
        }

        public static bool Visible(ITerrain terrain, Vec3d from, Vec3d to)
        {
            if (terrain == null) return true;
            Vec3d d = to - from;
            double len = d.Length;
            if (len < 1e-6) return true;
            TerrainHit h = terrain.Raycast(from, d * (1.0 / len), len - 0.05);
            return !h.Hit;
        }

        /// <summary>What picking up this item would do (no change made).</summary>
        public static PickupPlan Plan(in MotorState s, in GroundItem g, int playerId, int tick, ItemConfig cfg)
        {
            var p = new PickupPlan { Result = PickupResult.Ok, Slot = -1, SwapOut = Item.None };
            if (s.Dead || s.Hand.Mode == HandMode.Disarmed) { p.Result = PickupResult.NotNow; return p; }
            if (g.DroppedBy == playerId && tick - g.DroppedTick < cfg.DropperLockTicks) { p.Result = PickupResult.NotNow; return p; }
            if (g.Kind == Item.None || g.Count <= 0) { p.Result = PickupResult.NoItem; return p; }
            Bag b = s.Hand.Bag;
            if (Items.Stacks(g.Kind))
            {
                int have = b.SlotOf(g.Kind);
                int carry = cfg.Carry(g.Kind);
                if (have >= 0)
                {
                    int room = carry - b.Count(have);
                    if (room <= 0) { p.Result = PickupResult.AlreadyHave; return p; }
                    p.Slot = have;
                    p.Take = Math.Min(room, g.Count);
                    return p;
                }
                p.Take = Math.Min(Math.Max(1, carry), g.Count);
            }
            else
            {
                if (b.SlotOf(g.Kind) >= 0) { p.Result = PickupResult.AlreadyHave; return p; }
                p.Take = 1;
            }
            int held = s.Hand.Slot;
            if (b[held] == Item.None) { p.Slot = held; return p; }
            int empty = b.FirstEmpty();
            if (empty >= 0) { p.Slot = empty; return p; }
            if (b.CountIn(held) == 0) { p.Slot = held; return p; } // an empty C4 detonator is simply replaced
            p.Slot = held;
            p.SwapOut = b[held];
            p.Result = PickupResult.Swapped;
            return p;
        }

        /// <summary>
        /// The server's pickup: checks reach and line of sight from <paramref name="eye"/>, then moves the item into the
        /// bag. A swapped-out item lands where the picked one lay. Returns what happened.
        /// </summary>
        public static PickupResult TryPickup(ref MotorState s, GroundItems items, int itemId, ITerrain terrain, Vec3d eye,
            int playerId, int tick, MovementConfig mc)
        {
            ItemConfig cfg = mc.Items;
            if (!items.TryGet(itemId, out GroundItem g)) return PickupResult.NoItem;
            if ((g.Centre - eye).Length > cfg.Reach + cfg.ServerReachSlack) return PickupResult.TooFar;
            if (!Visible(terrain, eye, g.Centre)) return PickupResult.Blocked;
            PickupPlan p = Plan(s, g, playerId, tick, cfg);
            if (!p.Allowed) return p.Result;

            ref HandState h = ref s.Hand;
            if (p.SwapOut != Item.None)
            {
                var outItem = TakeOut(ref s, p.Slot, g.Position, playerId, tick);
                items.Add(outItem, cfg.MaxGroundItems);
            }

            bool intoHeld = p.Slot == h.Slot;
            if (Items.Stacks(g.Kind))
            {
                int before = h.Bag[p.Slot] == g.Kind ? h.Bag.Count(p.Slot) : 0;
                h.Bag[p.Slot] = g.Kind;
                h.Bag.SetCount(p.Slot, before + p.Take);
            }
            else
            {
                h.Bag[p.Slot] = g.Kind;
                if (Items.IsGun(g.Kind)) s.Weapon.SetUsed(Items.GunOf(g.Kind), g.AmmoUsed);
            }
            if (g.Count - p.Take > 0) items.SetCount(g.Id, g.Count - p.Take);
            else items.Remove(g.Id);

            // A new item in the slot you hold is drawn (a grenade added to a stack you already hold is not).
            if (intoHeld && (p.SwapOut != Item.None || h.Bag.CountIn(p.Slot) == p.Take)) HandRules.Redraw(ref h, tick, mc.Hand);
            return p.Result;
        }

        /// <summary>
        /// Drops the held slot. Only with something in hand and not mid sculpt mode or disarmed. The item lands
        /// <see cref="ItemConfig.DropForward"/> in front of you on whatever is below (at your feet if that is blocked).
        /// Draws your previous slot. Returns false if nothing was dropped.
        /// </summary>
        public static bool TryDrop(ref MotorState s, GroundItems items, ITerrain terrain, double yaw, int playerId, int tick, MovementConfig mc)
        {
            ref HandState h = ref s.Hand;
            if (s.Dead || h.InSculptMode || h.Mode == HandMode.Disarmed || h.Mode == HandMode.SculptExit) return false;
            if (h.HeldItem == Item.None) return false;
            if (h.Bag.CountIn(h.Slot) <= 0) return false; // an empty C4 detonator stays until your charges are gone
            Vec3d at = DropPoint(terrain, s.Position, yaw, mc);
            GroundItem g = TakeOut(ref s, h.Slot, at, playerId, tick);
            items.Add(g, mc.Items.MaxGroundItems);
            HandRules.OnHeldEmptied(ref h, tick, mc.Hand);
            return true;
        }

        /// <summary>Empties a slot into a ground item (with the gun's ammo), cancelling a reload of that gun.</summary>
        private static GroundItem TakeOut(ref MotorState s, int slot, Vec3d at, int playerId, int tick)
        {
            Bag b = s.Hand.Bag;
            Item kind = b[slot];
            var g = new GroundItem { Kind = kind, Count = b.CountIn(slot), Position = at, DroppedBy = playerId, DroppedTick = tick };
            if (Items.IsGun(kind))
            {
                Weapon w = Items.GunOf(kind);
                g.AmmoUsed = s.Weapon.UsedOf(w);
                s.Weapon.SetUsed(w, 0);
                if (s.Weapon.Reloading && s.Weapon.ReloadGun == w) s.Weapon.Reloading = false;
            }
            s.Hand.Bag.Clear(slot);
            return g;
        }

        /// <summary>Where a dropped item comes to rest: in front of the feet, else at them, on the surface below.</summary>
        public static Vec3d DropPoint(ITerrain terrain, Vec3d feet, double yaw, MovementConfig mc)
        {
            Vec3d fwd = new Vec3d(Math.Sin(yaw), 0, Math.Cos(yaw));
            Vec3d chest = feet + new Vec3d(0, 0.6, 0);
            Vec3d spot = chest + fwd * mc.Items.DropForward;
            if (terrain != null)
            {
                TerrainHit wall = terrain.Raycast(chest, fwd, mc.Items.DropForward + mc.HalfWidth);
                if (wall.Hit) spot = chest;
            }
            return Settle(terrain, spot);
        }

        /// <summary>Drops a point straight down onto the first surface below it (at most 60 m).</summary>
        public static Vec3d Settle(ITerrain terrain, Vec3d p)
        {
            if (terrain == null) return p;
            TerrainHit down = terrain.Raycast(p, new Vec3d(0, -1, 0), 60);
            return down.Hit ? down.Point : p;
        }
    }
}
