using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.Input
{
    /// <summary>
    /// All input actions, built from code (no .inputactions asset), matching the keybind table in CLAUDE.md. Every bind
    /// is rebindable: overrides are saved as JSON in PlayerPrefs and loaded on start. Dev keys live in their own "Dev"
    /// map, enabled only in dev builds (the Editor or a Development Build), never shipped.
    /// </summary>
    public sealed class GameInput : IDisposable
    {
        private const string OverridesKey = "sculpt.bindOverrides.v2";
        private const string DevOverridesKey = "sculpt.devBindOverrides";

        /// <summary>Which mode an action is used in, for the duplicate-bind warning (gun and sculpt binds may share keys).</summary>
        public enum Context { Always, Gun, Sculpt, Dev, Watch }

        public readonly InputActionMap Player;
        public readonly InputActionMap Dev;
        public readonly InputAction Move;
        public readonly InputAction Look;
        public readonly InputAction Jump;
        public readonly InputAction Crouch;
        public readonly InputAction Sprint;
        public readonly InputAction Fire;
        public readonly InputAction Aim;
        public readonly InputAction Reload;
        public readonly InputAction LastWeapon;
        /// <summary>Interact (E): pick up the item you aim at; hold to drop the held item.</summary>
        public readonly InputAction Interact;
        /// <summary>Punch (F): its own bind, not a hotbar item (Ben 2026-11-21).</summary>
        public readonly InputAction Punch;
        /// <summary>Inventory screen (I): drag slots to sort them by hand (Ben 2026-11-26).</summary>
        public readonly InputAction Inventory;
        public readonly InputAction SculptToggle;
        // Sculpt mode (own actions so they rebind separately from the guns).
        public readonly InputAction SculptPush;
        public readonly InputAction SculptPull;
        public readonly InputAction SculptRaise;
        public readonly InputAction SculptSink;
        public readonly InputAction SculptReinforce;
        public readonly InputAction SculptThrow;
        public readonly InputAction[] Hotbar = new InputAction[5];
        // Watching (spectating live or a replay). The free camera flies with Move, Jump (up), Crouch (down), Sprint (fast).
        public readonly InputAction WatchNext;
        public readonly InputAction WatchPrev;
        public readonly InputAction WatchCamera;
        public readonly InputAction WatchPause;
        public readonly InputAction WatchSlower;
        public readonly InputAction WatchFaster;
        public readonly InputAction WatchBack;
        public readonly InputAction WatchForward;
        public readonly InputAction WatchPrevKill;
        public readonly InputAction WatchNextKill;
        public readonly InputAction WatchHud;

        // Dev map.
        public readonly InputAction DevRespawn;
        public readonly InputAction DevHud;
        public readonly InputAction DevShowcase;
        public readonly InputAction DevShowcaseWatch;
        public readonly InputAction DevPerf;
        public readonly InputAction DevSimPing;
        public readonly InputAction DevSimLoss;
        public readonly InputAction DevSimJitter;
        public readonly InputAction DevHost;
        public readonly InputAction DevJoin;

        public static bool DevBuild => Application.isEditor || Debug.isDebugBuild;

        public GameInput()
        {
            Player = new InputActionMap("Player");

            Move = Player.AddAction("Move", InputActionType.Value);
            Move.AddCompositeBinding("2DVector")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");

            Look = Player.AddAction("Look", InputActionType.Value, "<Mouse>/delta");
            Jump = Player.AddAction("Jump", InputActionType.Button, "<Keyboard>/space");
            Sprint = Player.AddAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
            Crouch = Player.AddAction("Crouch", InputActionType.Button, "<Keyboard>/leftCtrl");
            Crouch.AddBinding("<Keyboard>/c");
            Fire = Player.AddAction("Fire", InputActionType.Button, "<Mouse>/leftButton");
            Aim = Player.AddAction("Aim", InputActionType.Button, "<Mouse>/rightButton");
            Reload = Player.AddAction("Reload", InputActionType.Button, "<Keyboard>/r");
            string[] keys = { "1", "2", "3", "4", "5" };
            string[] names = { "Slot 1", "Slot 2", "Slot 3", "Slot 4", "Slot 5" };
            for (int i = 0; i < 5; i++) Hotbar[i] = Player.AddAction($"{names[i]} ({i + 1})", InputActionType.Button, "<Keyboard>/" + keys[i]);
            LastWeapon = Player.AddAction("Last Weapon", InputActionType.Button);
            LastWeapon.AddBinding(""); // unbound by default; the empty slot is there so it can be bound in the menu
            Interact = Player.AddAction("Interact (pick up, hold to drop)", InputActionType.Button, "<Keyboard>/e");
            Punch = Player.AddAction("Punch", InputActionType.Button, "<Keyboard>/f");
            Inventory = Player.AddAction("Inventory (sort slots)", InputActionType.Button, "<Keyboard>/i");
            SculptToggle = Player.AddAction("Sculpt Mode", InputActionType.Button, "<Keyboard>/q");
            SculptPush = Player.AddAction("Sculpt Push", InputActionType.Button, "<Mouse>/leftButton");
            SculptPull = Player.AddAction("Sculpt Pull", InputActionType.Button, "<Mouse>/rightButton");
            SculptRaise = Player.AddAction("Sculpt Raise", InputActionType.Button, "<Mouse>/scroll/up");
            SculptSink = Player.AddAction("Sculpt Sink", InputActionType.Button, "<Mouse>/scroll/down");
            // Throw is part of the parked block moving: unbound by default so E is free for Interact (bind it in the
            // menu if Block Moves Enabled is turned back on).
            SculptThrow = Player.AddAction("Sculpt Throw", InputActionType.Button);
            SculptThrow.AddBinding("");
            // Mouse 4 first: Left Alt + Shift is the Windows layout switch, and you can sculpt while sprinting.
            SculptReinforce = Player.AddAction("Sculpt Reinforce (hold)", InputActionType.Button, "<Mouse>/backButton");
            SculptReinforce.AddBinding("<Keyboard>/leftAlt");

            WatchNext = Player.AddAction("Watch: next player", InputActionType.Button, "<Mouse>/leftButton");
            WatchPrev = Player.AddAction("Watch: previous player", InputActionType.Button, "<Mouse>/rightButton");
            WatchCamera = Player.AddAction("Watch: change camera", InputActionType.Button, "<Keyboard>/v");
            WatchPause = Player.AddAction("Replay: pause", InputActionType.Button, "<Keyboard>/p");
            WatchSlower = Player.AddAction("Replay: slower", InputActionType.Button, "<Keyboard>/minus");
            WatchSlower.AddBinding("<Keyboard>/leftBracket");
            WatchFaster = Player.AddAction("Replay: faster", InputActionType.Button, "<Keyboard>/equals");
            WatchFaster.AddBinding("<Keyboard>/rightBracket");
            WatchBack = Player.AddAction("Replay: back 5 s", InputActionType.Button, "<Keyboard>/leftArrow");
            WatchForward = Player.AddAction("Replay: forward 5 s", InputActionType.Button, "<Keyboard>/rightArrow");
            WatchPrevKill = Player.AddAction("Replay: previous kill", InputActionType.Button, "<Keyboard>/comma");
            WatchNextKill = Player.AddAction("Replay: next kill", InputActionType.Button, "<Keyboard>/period");
            WatchHud = Player.AddAction("Watch: hide HUD", InputActionType.Button, "<Keyboard>/h");

            Dev = new InputActionMap("Dev");
            DevRespawn = Dev.AddAction("Respawn", InputActionType.Button, "<Keyboard>/f1");
            DevHud = Dev.AddAction("Dev HUD", InputActionType.Button, "<Keyboard>/f2");
            DevPerf = Dev.AddAction("Latency Panel", InputActionType.Button, "<Keyboard>/f3");
            DevShowcase = Dev.AddAction("Race the TAS", InputActionType.Button, "<Keyboard>/f6");
            DevShowcaseWatch = Dev.AddAction("Watch the TAS", InputActionType.Button, "<Keyboard>/f7");
            DevSimPing = Dev.AddAction("Sim Ping", InputActionType.Button, "<Keyboard>/f8");
            DevSimLoss = Dev.AddAction("Sim Loss", InputActionType.Button, "<Keyboard>/f9");
            DevSimJitter = Dev.AddAction("Sim Jitter", InputActionType.Button, "<Keyboard>/f10");
            DevHost = Dev.AddAction("Host / Offline", InputActionType.Button, "<Keyboard>/f11");
            DevJoin = Dev.AddAction("Join Box", InputActionType.Button, "<Keyboard>/f12");

            Load(Player, OverridesKey);
            Load(Dev, DevOverridesKey);
            Player.Enable();
            if (DevBuild) Dev.Enable();
        }

        public Context ContextOf(InputAction a)
        {
            if (a.actionMap == Dev) return Context.Dev;
            if (a == Fire || a == Aim || a == Reload || a == LastWeapon || Array.IndexOf(Hotbar, a) >= 0) return Context.Gun;
            if (a == SculptPush || a == SculptPull || a == SculptRaise || a == SculptSink || a == SculptThrow || a == SculptReinforce) return Context.Sculpt;
            if (a == WatchNext || a == WatchPrev || a == WatchCamera || a == WatchPause || a == WatchSlower || a == WatchFaster ||
                a == WatchBack || a == WatchForward || a == WatchPrevKill || a == WatchNextKill || a == WatchHud) return Context.Watch;
            return Context.Always;
        }

        /// <summary>Two contexts clash if both can be live at once (Always clashes with everything but Dev only with Dev and Always).
        /// Watch keys are only live while spectating, so they share keys with the guns and sculpt mode freely.</summary>
        public static bool Clashes(Context a, Context b)
        {
            if (a == b) return true;
            return a == Context.Always || b == Context.Always;
        }

        private static void Load(InputActionMap map, string key)
        {
            string json = PlayerPrefs.GetString(key, "");
            if (string.IsNullOrEmpty(json)) return;
            try { ImportOverrides(map, json); }
            catch (Exception e) { Debug.LogWarning("[SCULPT] Ignoring bad bind overrides: " + e.Message); }
        }

        public void SaveOverrides()
        {
            PlayerPrefs.SetString(OverridesKey, ExportOverrides(Player));
            PlayerPrefs.SetString(DevOverridesKey, ExportOverrides(Dev));
            PlayerPrefs.Save();
        }

        // The Input System's override JSON names each binding by its id. Actions built from code get new random binding
        // ids every run, so saved overrides (and settings codes from another PC) never found their binding again and
        // every bind went back to default on restart. Saved JSON names a binding by action name and index instead
        // (Core BindIds); ids are swapped in and out here. Same field names as the Input System's own JSON.
        [Serializable] private sealed class OverrideJson { public string action, id, path, interactions, processors; }
        [Serializable] private sealed class OverrideListJson { public System.Collections.Generic.List<OverrideJson> bindings = new System.Collections.Generic.List<OverrideJson>(); }

        /// <summary>The map's bind overrides as JSON that loads again in another run or on another PC.</summary>
        public static string ExportOverrides(InputActionMap map)
        {
            string json = map.SaveBindingOverridesAsJson();
            if (string.IsNullOrEmpty(json)) return "";
            OverrideListJson list = JsonUtility.FromJson<OverrideListJson>(json);
            if (list == null || list.bindings == null) return json;
            foreach (OverrideJson e in list.bindings)
                foreach (InputAction a in map.actions)
                {
                    int i = IndexOfId(a, e.id);
                    if (i >= 0) { e.id = Sculpt.Core.Settings.BindIds.Key(a.name, i); break; }
                }
            return JsonUtility.ToJson(list);
        }

        /// <summary>Replaces the map's bind overrides with the ones in <paramref name="json"/> (from <see cref="ExportOverrides"/>;
        /// old id-named entries still load within the run that wrote them). Throws on JSON that isn't overrides.</summary>
        public static void ImportOverrides(InputActionMap map, string json)
        {
            OverrideListJson list = JsonUtility.FromJson<OverrideListJson>(json);
            if (list == null || list.bindings == null) throw new FormatException("not bind overrides");
            var keep = new System.Collections.Generic.List<OverrideJson>();
            foreach (OverrideJson e in list.bindings)
            {
                if (e == null) continue;
                if (Sculpt.Core.Settings.BindIds.TryParse(e.id, out string name, out int index))
                {
                    InputAction a = null;
                    foreach (InputAction x in map.actions) if (x.name == name) { a = x; break; }
                    if (a == null || index >= a.bindings.Count || a.bindings[index].isComposite) continue; // a bind this build doesn't have
                    e.id = a.bindings[index].id.ToString();
                }
                keep.Add(e);
            }
            list.bindings = keep;
            map.RemoveAllBindingOverrides();
            if (keep.Count > 0) map.LoadBindingOverridesFromJson(JsonUtility.ToJson(list));
        }

        private static int IndexOfId(InputAction a, string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < a.bindings.Count; i++)
                if (string.Equals(a.bindings[i].id.ToString(), id, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public void Dispose()
        {
            Player.Disable();
            Player.Dispose();
            Dev.Disable();
            Dev.Dispose();
        }
    }
}
