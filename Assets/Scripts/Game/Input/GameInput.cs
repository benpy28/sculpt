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
        public enum Context { Always, Gun, Sculpt, Dev }

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
        public readonly InputAction SculptToggle;
        // Sculpt mode (own actions so they rebind separately from the guns).
        public readonly InputAction SculptPush;
        public readonly InputAction SculptPull;
        public readonly InputAction SculptRaise;
        public readonly InputAction SculptSink;
        public readonly InputAction SculptReinforce;
        public readonly InputAction SculptThrow;
        public readonly InputAction[] Hotbar = new InputAction[5];

        // Dev map.
        public readonly InputAction DevRespawn;
        public readonly InputAction DevHud;
        public readonly InputAction DevShowcase;
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

            Dev = new InputActionMap("Dev");
            DevRespawn = Dev.AddAction("Respawn", InputActionType.Button, "<Keyboard>/f1");
            DevHud = Dev.AddAction("Dev HUD", InputActionType.Button, "<Keyboard>/f2");
            DevPerf = Dev.AddAction("Latency Panel", InputActionType.Button, "<Keyboard>/f3");
            DevShowcase = Dev.AddAction("Movement Showcase", InputActionType.Button, "<Keyboard>/f6");
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
            return Context.Always;
        }

        /// <summary>Two contexts clash if both can be live at once (Always clashes with everything but Dev only with Dev and Always).</summary>
        public static bool Clashes(Context a, Context b)
        {
            if (a == b) return true;
            return a == Context.Always || b == Context.Always;
        }

        private static void Load(InputActionMap map, string key)
        {
            string json = PlayerPrefs.GetString(key, "");
            if (string.IsNullOrEmpty(json)) return;
            try { map.LoadBindingOverridesFromJson(json); }
            catch (Exception e) { Debug.LogWarning("[SCULPT] Ignoring bad bind overrides: " + e.Message); }
        }

        public void SaveOverrides()
        {
            PlayerPrefs.SetString(OverridesKey, Player.SaveBindingOverridesAsJson());
            PlayerPrefs.SetString(DevOverridesKey, Dev.SaveBindingOverridesAsJson());
            PlayerPrefs.Save();
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
