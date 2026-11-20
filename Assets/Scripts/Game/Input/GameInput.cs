using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.Input
{
    /// <summary>
    /// All input actions, built from code (no .inputactions asset). Every bind is rebindable:
    /// overrides are saved as JSON in PlayerPrefs and loaded on start.
    /// </summary>
    public sealed class GameInput : IDisposable
    {
        private const string OverridesKey = "sculpt.bindOverrides";

        public readonly InputActionMap Player;
        public readonly InputAction Move;
        public readonly InputAction Look;
        public readonly InputAction Jump;
        public readonly InputAction Crouch;
        public readonly InputAction Sprint;
        public readonly InputAction Fire;
        public readonly InputAction Aim;
        public readonly InputAction SculptToggle;
        public readonly InputAction Respawn;
        // Sculpt mode (own actions so they rebind separately from the guns).
        public readonly InputAction SculptPush;
        public readonly InputAction SculptPull;
        public readonly InputAction SculptRaise;
        public readonly InputAction SculptSink;
        public readonly InputAction SculptReinforce;
        public readonly InputAction SculptThrow;
        public readonly InputAction[] Hotbar = new InputAction[5];

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
            Crouch = Player.AddAction("Crouch", InputActionType.Button, "<Keyboard>/leftCtrl");
            Crouch.AddBinding("<Keyboard>/c");
            Sprint = Player.AddAction("Sprint", InputActionType.Button, "<Keyboard>/leftShift");
            Fire = Player.AddAction("Fire", InputActionType.Button, "<Mouse>/leftButton");
            Aim = Player.AddAction("Aim", InputActionType.Button, "<Mouse>/rightButton");
            SculptToggle = Player.AddAction("Sculpt", InputActionType.Button, "<Keyboard>/q");
            Respawn = Player.AddAction("Respawn", InputActionType.Button, "<Keyboard>/r");
            SculptPush = Player.AddAction("Sculpt Push", InputActionType.Button, "<Mouse>/leftButton");
            SculptPull = Player.AddAction("Sculpt Pull", InputActionType.Button, "<Mouse>/rightButton");
            SculptRaise = Player.AddAction("Sculpt Raise", InputActionType.Button, "<Mouse>/scroll/up");
            SculptSink = Player.AddAction("Sculpt Sink", InputActionType.Button, "<Mouse>/scroll/down");
            SculptReinforce = Player.AddAction("Sculpt Reinforce", InputActionType.Button, "<Keyboard>/leftAlt");
            SculptThrow = Player.AddAction("Sculpt Throw", InputActionType.Button, "<Keyboard>/e");
            string[] keys = { "1", "2", "3", "4", "5" };
            for (int i = 0; i < 5; i++) Hotbar[i] = Player.AddAction("Slot " + (i + 1), InputActionType.Button, "<Keyboard>/" + keys[i]);

            string json = PlayerPrefs.GetString(OverridesKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try { Player.LoadBindingOverridesFromJson(json); }
                catch (Exception e) { Debug.LogWarning("[SCULPT] Ignoring bad bind overrides: " + e.Message); }
            }
            Player.Enable();
        }

        public void SaveOverrides()
        {
            PlayerPrefs.SetString(OverridesKey, Player.SaveBindingOverridesAsJson());
            PlayerPrefs.Save();
        }

        public void Dispose()
        {
            Player.Disable();
            Player.Dispose();
        }
    }
}
