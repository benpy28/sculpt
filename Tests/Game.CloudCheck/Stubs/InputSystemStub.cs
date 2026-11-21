// Compile-only stub of the Input System API surface used by Sculpt.Game. Not shipped.
using System;
using System.Collections.Generic;
using UnityEngine;
namespace UnityEngine.InputSystem
{
    public enum InputActionType { Value, Button, PassThrough }
    public struct InputBinding { public string path; public string effectivePath; public string overridePath; public string name; public bool isComposite; public bool isPartOfComposite; public string groups; public string action; public InputBinding(string p, string a = null, string g = null, string pr = null, string i = null, string n = null) { path = p; effectivePath = p; overridePath = null; name = n; isComposite = false; isPartOfComposite = false; groups = g; action = a; } }
    public class InputControl { public string path; public string name; public string displayName; }
    public class InputActionMap : IDisposable
    {
        public InputActionMap(string name) { }
        public string name => "";
        public IReadOnlyList<InputAction> actions => null;
        public InputAction AddAction(string name, InputActionType type = InputActionType.Value, string binding = null, string interactions = null, string processors = null, string groups = null, string expectedControlLayout = null) => null;
        public InputAction FindAction(string n, bool throwIfNotFound = false) => null;
        public void Enable() { } public void Disable() { } public void Dispose() { }
        public bool enabled => true;
        public void LoadBindingOverridesFromJson(string json, bool removeExisting = true) { }
        public string SaveBindingOverridesAsJson() => ""; public void RemoveAllBindingOverrides() { }
    }
    public struct CompositeSyntax { public CompositeSyntax With(string part, string path, string groups = null, string processors = null) => this; }
    public struct BindingSyntax { public BindingSyntax WithProcessor(string p) => this; }
    public class InputAction
    {
        public string name => ""; public InputActionMap actionMap => null; public bool enabled => true;
        public IReadOnlyList<InputBinding> bindings => null;
        public IReadOnlyList<InputControl> controls => null;
        public CompositeSyntax AddCompositeBinding(string composite, string interactions = null, string processors = null) => default;
        public BindingSyntax AddBinding(string path, string interactions = null, string processors = null, string groups = null) => default;
        public bool WasPressedThisFrame() => false; public bool WasReleasedThisFrame() => false; public bool IsPressed() => false; public bool WasPerformedThisFrame() => false; public bool triggered => false;
        public TValue ReadValue<TValue>() where TValue : struct => default;
        public void Enable() { } public void Disable() { }
        public void ApplyBindingOverride(int bindingIndex, string path) { }
        public void ApplyBindingOverride(string path, string group = null, string bindingPath = null) { }
        public void RemoveBindingOverride(int bindingIndex) { }
        public void RemoveAllBindingOverrides() { }
        public string GetBindingDisplayString(int bindingIndex, int options = 0) => "";
        public InputActionRebindingExtensions.RebindingOperation PerformInteractiveRebinding(int bindingIndex = -1) => null;
    }
    public static class InputActionRebindingExtensions
    {
        public static void ApplyBindingOverride(this InputAction a, int i, string p) { }
        public static void RemoveBindingOverride(this InputAction a, int i) { }
        public static string GetBindingDisplayString(this InputAction a, int i) => "";
        public sealed class RebindingOperation : IDisposable
        {
            public InputAction action => null; public InputControl selectedControl => null;
            public RebindingOperation WithControlsExcluding(string p) => this;
            public RebindingOperation WithCancelingThrough(string p) => this;
            public RebindingOperation WithMatchingEventsBeingSuppressed(bool v = true) => this;
            public RebindingOperation WithTargetBinding(int i) => this;
            public RebindingOperation WithExpectedControlType(string t) => this;
            public RebindingOperation OnMatchWaitForAnother(float s) => this;
            public RebindingOperation OnComplete(Action<RebindingOperation> a) => this;
            public RebindingOperation OnCancel(Action<RebindingOperation> a) => this;
            public RebindingOperation OnPotentialMatch(Action<RebindingOperation> a) => this;
            public RebindingOperation WithControlsHavingToMatchPath(string p) => this;
            public RebindingOperation Start() => this;
            public void Cancel() { } public void Complete() { } public void Dispose() { }
            public bool started => false; public bool completed => false; public bool canceled => false;
        }
    }
    public static class InputControlPath
    {
        [Flags] public enum HumanReadableStringOptions { None = 0, OmitDevice = 2, UseShortNames = 4 }
        public static string ToHumanReadableString(string path, HumanReadableStringOptions options = HumanReadableStringOptions.None, InputControl control = null) => "";
    }
    public static class InputSystem { }
}
namespace UnityEngine.InputSystem
{
    public partial struct InputBindingExt { }
}
namespace UnityEngine.InputSystem.Controls
{
    public class ButtonControl : InputControl { public bool isPressed => false; public bool wasPressedThisFrame => false; public bool wasReleasedThisFrame => false; public float ReadValue() => 0; }
    public class Vector2Control : InputControl { public Vector2 ReadValue() => default; public AxisControl x => null; public AxisControl y => null; public ButtonControl up => null; public ButtonControl down => null; }
    public class AxisControl : InputControl { public float ReadValue() => 0; }
    public class KeyControl : ButtonControl { }
}
namespace UnityEngine.InputSystem
{
    using UnityEngine.InputSystem.Controls;
    public enum Key { None, Space, Enter, Tab, Backquote, Quote, Semicolon, Comma, Period, Slash, Backslash, LeftBracket, RightBracket, Minus, Equals, A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z, Digit1, Digit2, Digit3, Digit4, Digit5, Digit6, Digit7, Digit8, Digit9, Digit0, LeftShift, RightShift, LeftAlt, RightAlt, LeftCtrl, RightCtrl, Escape, LeftArrow, RightArrow, UpArrow, DownArrow, Backspace, F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12 }
    public class InputDevice : InputControl { }
    public class Keyboard : InputDevice
    {
        public static Keyboard current => null;
        public KeyControl this[Key k] => null;
        public KeyControl escapeKey, enterKey, spaceKey, backspaceKey, tabKey, leftShiftKey, leftCtrlKey, leftAltKey, f1Key, f2Key, f3Key, f4Key, f5Key, f6Key, f7Key, f8Key, f9Key, f10Key, f11Key, f12Key, aKey, dKey, eKey, qKey, rKey, wKey, sKey, digit1Key, digit2Key, digit3Key, digit4Key, digit5Key, numpadEnterKey, backquoteKey;
        public ButtonControl anyKey => null;
        public event Action<char> onTextInput { add { } remove { } }
    }
    public class Mouse : InputDevice
    {
        public static Mouse current => null;
        public Vector2Control delta => null; public Vector2Control scroll => null; public Vector2Control position => null;
        public ButtonControl leftButton => null; public ButtonControl rightButton => null; public ButtonControl middleButton => null; public ButtonControl backButton => null; public ButtonControl forwardButton => null;
    }
}
