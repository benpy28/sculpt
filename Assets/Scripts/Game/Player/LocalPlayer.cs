using System.Collections.Generic;
using Sculpt.Core;
using Sculpt.Core.Net;
using Sculpt.Game.Config;
using Sculpt.Game.Input;
using Sculpt.Game.Net;
using Sculpt.Game.Sculpting;
using Sculpt.Game.Settings;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpt.Game.Player
{
    /// <summary>
    /// The local player: samples input every frame and predicts Core movement, hand state and sculpting through the
    /// NetClient at 60 Hz (the server, local or remote, is always authoritative). Mouse look is applied every rendered
    /// frame for minimum latency; the simulation reads the latest yaw on each tick. Sculpt targeting also runs every
    /// frame, and a sculpt press is turned into a confirm with the aim of the frame it was pressed in.
    /// </summary>
    public sealed class LocalPlayer : MonoBehaviour
    {
        public MotorState State => Net.Predicted;
        public MovementConfig Config => _cfg;
        public readonly List<MoveEvent> Events = new List<MoveEvent>();
        public NetClient Net => _session.Client;
        public SculptClient Sculpt => _session.Client.Sculpt;
        public SculptConfig SculptConfig => _session.SculptConfig;

        /// <summary>View angles in degrees. Yaw 0 = north, positive turns toward east.</summary>
        public float Yaw { get; private set; }
        public float Pitch { get; private set; }

        /// <summary>Interpolated feet position for rendering (includes the decaying correction offset).</summary>
        public Vector3 RenderFeet { get; private set; }
        public float RenderSpeed { get; private set; }
        public bool CursorLocked => Cursor.lockState == CursorLockMode.Locked;
        /// <summary>Set by UI that takes the keyboard (e.g. the join box). Movement input is ignored meanwhile.</summary>
        public bool InputBlocked { get; set; }

        // ---- Sculpt (read by the sculpt view and HUD) ----
        public bool InSculptMode => State.Hand.InSculptMode;
        public SculptTarget Target { get; private set; }
        /// <summary>The action a left click would confirm right now, and its full check (drives ghost and cost flash).</summary>
        public SculptAction HoverAction { get; private set; }
        public SculptCheck HoverCheck { get; private set; }
        public bool HasHover { get; private set; }
        public bool ReinforceHeld { get; private set; }

        // ---- Healing (the sculpt key on a map of tiles; read by the tile heal view and HUD) ----
        /// <summary>The wall tile the crosshair picks in sculpt mode (heal or reinforce, by the reinforce key).</summary>
        public TileTarget HealTarget { get; private set; } = TileTarget.None;
        /// <summary>What a click on <see cref="HealTarget"/> would do right now (Ok, or why not).</summary>
        public HealResult HealCheck { get; private set; } = HealResult.NoTile;
        public HealKind HealKindNow => ReinforceHeld ? HealKind.Reinforce : HealKind.Heal;
        /// <summary>Healing replaces block moving while block moves are parked (Breach Settings).</summary>
        public bool Heals => Net.Heals != null && !SculptConfig.BlockMovesEnabled;
        /// <summary>Q heals what you aim at straight away, no sculpt mode (Ben 2026-11-25: "no heal menu").</summary>
        public bool QuickHeal => Heals && _cfg.Hand.QuickHeal;
        /// <summary>Last refused heal press (local check or the server) and when, for the red flash.</summary>
        public float HealRejectTime { get; set; } = -10f;
        public HealResult LastHealReject { get; set; }
        public int LastHealRejectTile { get; set; } = -1;
        private bool _healWant;
        private int _healWantTile, _healWantCell;
        private HealKind _healWantKind;
        private float _healWantTime;

        private NetSession _session;
        private MovementConfig _cfg;
        private MovementSettings _settingsAsset;
        private GameInput _input;
        private ClientSettings _client;
        private bool _jumpLatch, _crouchLatch, _respawnLatch, _toggleLatch, _fireLatch, _aimLatch, _reloadLatch, _punchLatch;
        private bool _sprintOn, _crouchOn, _zoomOn;
        private int _lastSlot = -1;

        // ---- Items (read by the HUD) ----
        /// <summary>Index into <see cref="NetClient.Items"/> of the ground item under the crosshair, or -1.</summary>
        public int PickTarget { get; private set; } = -1;
        /// <summary>What pressing interact on <see cref="PickTarget"/> would do.</summary>
        public PickupPlan PickPlan { get; private set; }
        /// <summary>0..1 progress of a held interact key toward dropping the held item (0 when not holding).</summary>
        public float DropHold { get; private set; }
        public string InteractKeyName => UI.SculptHud.Key(_input.Interact);
        public string AimKeyName => UI.SculptHud.Key(_input.Aim);
        public string FireKeyName => UI.SculptHud.Key(_input.Fire);
        private bool _pickupLatch, _dropLatch, _holdForDrop, _doorLatch, _doorOpen;
        private int _doorTile;
        /// <summary>The door leaf under the crosshair within reach (open or shut), or -1: interact opens or shuts it.</summary>
        public int DoorTarget { get; private set; } = -1;
        /// <summary>Is <see cref="DoorTarget"/> open now (in your view)?</summary>
        public bool DoorTargetOpen => DoorTarget >= 0 && Net.Walls != null && DoorRules.IsOpen(Net.Walls, DoorTarget);
        private int _pickupId;
        private float _holdStart;

        /// <summary>Right mouse zoom (hold or toggle) while a gun is out. The camera scales the FOV by the gun's AimFovScale.</summary>
        public bool Zoomed { get; private set; }
        public GameInput Input => _input;

        /// <summary>Recoil offset of the aim (degrees, x = up, y = right), interpolated for drawing. The camera includes it,
        /// so the crosshair always sits exactly where the next bullet goes (before spread).</summary>
        public Vector2 RenderRecoil { get; private set; }
        /// <summary>Mouse movement this frame in degrees (viewmodel sway).</summary>
        public Vector2 LookDelta { get; private set; }
        // ---- Movement showcase (F6, dev builds): warp to the course and play the tool-assisted run ----
        private CourseInfo _course;
        private ShowcaseRun _showcase;
        private bool _showcaseLatch, _watchLatch, _warping, _warpForRace;
        private CourseRace _race;
        private int _raceOverTicks;
        /// <summary>The course race in progress (F6: 3-2-1, then race the TAS ghost), or the last one for a few seconds
        /// after you finish. Null when none.</summary>
        public CourseRace Race => _race;
        private float _showcaseYaw;
        /// <summary>The showcase run in progress (null when none). Its inputs replace yours until it ends or you move.</summary>
        public ShowcaseRun Showcase => _showcase;
        /// <summary>The last finished showcase, for the dev HUD summary.</summary>
        public ShowcaseRun LastShowcase { get; private set; }

        /// <summary>Gives the player the movement course (breach map only), which enables F6.</summary>
        public void SetCourse(CourseInfo course)
        {
            _course = course;
            // Dev builds: -race starts the F6 race (and -watchtas the F7 run) as soon as you are in.
            if (!GameInput.DevBuild) return;
            string[] args = System.Environment.GetCommandLineArgs();
            _autoRace = System.Array.IndexOf(args, "-race") >= 0;
            _autoWatch = System.Array.IndexOf(args, "-watchtas") >= 0;
        }
        private bool _autoRace, _autoWatch;

        private byte _weaponLatch;
        private bool _confirmLatch;
        private SculptAction _confirmAction;
        private bool _hadJoined;

        public void Init(NetSession session, MovementConfig cfg, MovementSettings settingsAsset, GameInput input, ClientSettings client)
        {
            _session = session;
            _cfg = cfg;
            _settingsAsset = settingsAsset;
            _input = input;
            _client = client;
            if (_settingsAsset != null) _settingsAsset.ApplyTo(_cfg);
            _session.ClientChanged += () => _hadJoined = false;
            LockCursor(true);
        }

        public static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void FaceSpawn()
        {
            Yaw = (float)(Net.SpawnYaw * Mathf.Rad2Deg);
            Pitch = 0;
        }

        /// <summary>Eye position the simulation (and the server) uses, and the look direction.</summary>
        public Vec3d SimEye => State.Position + new Vec3d(0, PlayerMovement.EyeOf(State, _cfg), 0);
        public Vec3d LookDir => SculptAim.LookDir(Yaw * Mathf.Deg2Rad, Pitch);

        /// <summary>Called once per rendered frame before any ticks.</summary>
        public void SampleFrame()
        {
            if (Net.Joined && !_hadJoined)
            {
                _hadJoined = true;
                FaceSpawn();
                RenderFeet = Net.Predicted.Position.ToUnity();
            }

            // Esc opens the settings menu (SettingsMenu frees the cursor and blocks input while it is open).
            var mouse = Mouse.current;
            bool clickToLock = !CursorLocked && !InputBlocked && mouse != null && mouse.leftButton.wasPressedThisFrame;
            if (clickToLock) LockCursor(true);

            if (_showcase != null)
            {
                // The showcase steers per 60 Hz tick and sometimes turns sharply (wall kicks). The camera follows its
                // heading smoothly every frame instead, and the mouse doesn't fight it (that was the F6 jitter).
                float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime / 0.09f);
                Yaw = Mathf.Repeat(Mathf.LerpAngle(Yaw, _showcaseYaw, k) + 180f, 360f) - 180f;
                Pitch = Mathf.Lerp(Pitch, 12f, k);
                LookDelta = Vector2.zero;
            }
            else if (CursorLocked)
            {
                Vector2 d = _input.Look.ReadValue<Vector2>();
                float k = _client.DegreesPerCount;
                Yaw = Mathf.Repeat(Yaw + d.x * k + 180f, 360f) - 180f;
                Pitch = Mathf.Clamp(Pitch - d.y * k, -89f, 89f);
                LookDelta = new Vector2(d.x * k, d.y * k);
            }
            else LookDelta = Vector2.zero;

            // Watching: the mouse look drives the spectator camera; nothing else of the local player runs.
            if (Net.IsSpectator) { PickTarget = -1; DoorTarget = -1; return; }

            UpdateSculptAim();
            UpdateItems();

            if (InputBlocked || !CursorLocked) return;
            if (_input.Jump.WasPressedThisFrame()) _jumpLatch = true;
            if (_input.Crouch.WasPressedThisFrame()) _crouchLatch = true;
            if (GameInput.DevBuild && _input.DevRespawn.WasPressedThisFrame()) _respawnLatch = true;
            if (GameInput.DevBuild && _course != null && _input.DevShowcase.WasPressedThisFrame()) _showcaseLatch = true;
            if (GameInput.DevBuild && _course != null && _input.DevShowcaseWatch.WasPressedThisFrame()) _watchLatch = true;
            if (_input.SculptToggle.WasPressedThisFrame())
            {
                if (QuickHeal) PressHeal(); // Q heals (Alt + Q reinforces); there is no heal mode
                else _toggleLatch = true;
            }
            for (int i = 0; i < Items.Slots; i++) if (_input.Hotbar[i].WasPressedThisFrame()) _weaponLatch = (byte)(i + 1);
            if (_input.LastWeapon.WasPressedThisFrame()) _weaponLatch = (byte)(State.Hand.PrevSlot + 1);
            if (!InSculptMode && !clickToLock && _input.Fire.WasPressedThisFrame()) _fireLatch = true;
            if (!InSculptMode && _input.Reload.WasPressedThisFrame()) _reloadLatch = true;
            if (!InSculptMode && !clickToLock && _input.Aim.WasPressedThisFrame()) _aimLatch = true; // C4 detonator
            if (_input.Punch.WasPressedThisFrame()) _punchLatch = true;
            if (_client.SprintToggle && _input.Sprint.WasPressedThisFrame()) _sprintOn = !_sprintOn;
            if (_client.CrouchToggle && _input.Crouch.WasPressedThisFrame()) _crouchOn = !_crouchOn;
            // Toggle mode: pressing sprint while toggled into a crouch stands you up and sprints.
            if (_client.SprintToggle && _client.CrouchToggle && _input.Sprint.WasPressedThisFrame() && _crouchOn && State.Mode != MoveMode.Slide) _crouchOn = false;
            if (_client.ZoomToggle && !InSculptMode && _input.Aim.WasPressedThisFrame()) _zoomOn = !_zoomOn;

            if (!InSculptMode || clickToLock) return;
            if (Heals)
            {
                if (_input.SculptPush.WasPressedThisFrame()) PressHeal();
                return;
            }
            // The direction press IS the confirm, with this frame's aim. The newest press in a frame wins.
            SculptTarget t = Target;
            if (!t.Valid) return;
            bool reinforce = ReinforceHeld;
            if (_input.SculptPush.WasPressedThisFrame()) Confirm(HasHover ? HoverAction : SculptAction.Move(t.Sub, t.PushDir, t.Point, reinforce));
            if (_input.SculptPull.WasPressedThisFrame()) Confirm(BestMove(t, true, out _));
            if (_input.SculptRaise.WasPressedThisFrame()) Confirm(SculptAction.Raise(t.Sub, t.Point, reinforce));
            if (_input.SculptSink.WasPressedThisFrame()) Confirm(SculptAction.Sink(t.Sub, t.Point, reinforce));
            if (_input.SculptThrow.WasPressedThisFrame()) Confirm(SculptAction.Throw(t.Sub, t.Point, LookDir));
        }

        /// <summary>
        /// Interact key (E): pressed while aiming at an item picks it up (the server decides, the prompt shows what will
        /// happen); held for <see cref="ItemConfig.DropHoldTicks"/> while not aiming at one drops the held item.
        /// </summary>
        private void UpdateItems()
        {
            PickTarget = -1;
            DoorTarget = -1;
            if (!Net.Joined || State.Dead) { _holdForDrop = false; DropHold = 0; return; }
            int i = ItemRules.FindTarget(Net.Items, Net.Grid, SimEye, LookDir, _cfg.Items);
            if (i >= 0)
            {
                PickTarget = i;
                PickPlan = ItemRules.Plan(State, Net.Items[i], Net.PlayerId, Net.Tick, _cfg.Items);
            }
            else if (Net.Walls != null) DoorTarget = DoorRules.FindTarget(Net.Walls, Net.Grid, SimEye, LookDir, Net.Walls.Config.DoorReach);
            bool active = CursorLocked && !InputBlocked;
            if (active && _input.Interact.WasPressedThisFrame())
            {
                if (PickTarget >= 0)
                {
                    if (PickPlan.Allowed) { _pickupLatch = true; _pickupId = Net.Items[PickTarget].Id; }
                    _holdForDrop = false;
                }
                else if (DoorTarget >= 0)
                {
                    // Doors: one press opens a shut door or shuts an open one (yours shows at once; the server decides).
                    _doorLatch = true;
                    _doorTile = DoorTarget;
                    _doorOpen = !DoorTargetOpen;
                    _holdForDrop = false;
                }
                else { _holdForDrop = true; _holdStart = Time.unscaledTime; }
            }
            if (_holdForDrop && (!active || !_input.Interact.IsPressed() || State.Hand.HeldItem == Item.None || State.Hand.InSculptMode))
                _holdForDrop = false;
            DropHold = 0;
            if (!_holdForDrop) return;
            float need = _cfg.Items.DropHoldTicks / (float)SimTime.TicksPerSecond;
            DropHold = Mathf.Clamp01((Time.unscaledTime - _holdStart) / Mathf.Max(0.01f, need));
            if (DropHold < 1f) return;
            _dropLatch = true;
            _holdForDrop = false;
            DropHold = 0;
        }

        /// <summary>
        /// A heal press with this frame's aim. Checked here with the server's rules first: a press that can't work goes
        /// red at once and costs nothing. Pressed while still entering sculpt mode, it waits (up to 0.25 s) until sculpt
        /// mode is ready instead of being refused.
        /// </summary>
        private void PressHeal()
        {
            TileTarget t = HealTarget;
            if (!t.Valid) { RejectHeal(HealResult.NoTile, -1); return; }
            HealKind kind = HealKindNow;
            if (Net.Heals.TryGetJob(Net.PlayerId, out HealJob cur) && cur.Tile == t.Tile && cur.Kind == kind) return; // already on it
            if (HealCheck != HealResult.Ok && HealCheck != HealResult.NotInSculptMode) { RejectHeal(HealCheck, t.Tile); return; }
            _healWant = true;
            _healWantTile = t.Tile;
            _healWantCell = t.Cell;
            _healWantKind = kind;
            _healWantTime = Time.unscaledTime;
        }

        private void RejectHeal(HealResult r, int tile)
        {
            HealRejectTime = Time.time;
            LastHealReject = r;
            LastHealRejectTile = tile;
            Sculpting.SculptAudio.Play(Sculpting.SculptAudio.Reject, Vector3.zero, 0.6f, 1f, false);
        }

        private void Confirm(SculptAction a)
        {
            _confirmLatch = true;
            _confirmAction = a;
        }

        private void UpdateSculptAim()
        {
            HasHover = false;
            ReinforceHeld = CursorLocked && !InputBlocked && _input.SculptReinforce.IsPressed();
            UpdateHealAim();
            if (!Net.Joined || Sculpt == null || Sculpt.LocalBody == null || !InSculptMode)
            {
                Target = default;
                return;
            }
            Target = SculptAim.Find(Net.Grid, Sculpt.Grid, SimEye, LookDir, SculptConfig, Pitch);
            if (!Target.Valid) return;
            Sculpt.SetLocalBody(State);
            HoverAction = BestMove(Target, false, out SculptCheck check);
            HoverCheck = check;
            HasHover = true;
        }

        private void UpdateHealAim()
        {
            HealTarget = TileTarget.None;
            HealCheck = HealResult.NoTile;
            if (!Net.Joined || !Heals || (!QuickHeal && !InSculptMode) || State.Dead) { _healWant = false; return; }
            HealTarget = TileAim.Find(Net.Grid, Net.Walls, SimEye, LookDir, Net.Walls.Config.HealRange, HealKindNow);
            if (!HealTarget.Valid) return;
            HealCheck = Net.Heals.Check(Net.Grid, Net.PlayerId, SimEye, _cfg.Hand.CanHeal(State.Hand), HealTarget.Tile, HealTarget.Cell, HealKindNow, Net.DisplayBar);
        }

        /// <summary>
        /// The push (or pull) to send. On a top face the triangle under the crosshair picks the edge; if the piece can't
        /// go that way (rock or a player behind it) the next-nearest edge is used, then the remaining ones, so a click
        /// moves the piece the most sensible free way instead of going red (Ben: rejections only for true dead ends).
        /// Side faces push straight into the face (pull straight out): no guessing there.
        /// </summary>
        private SculptAction BestMove(in SculptTarget t, bool pull, out SculptCheck check)
        {
            Dir first = pull ? t.PushDir.Opposite() : t.PushDir;
            SculptAction a = SculptAction.Move(t.Sub, first, t.Point, ReinforceHeld);
            check = Sculpt.Evaluate(a, Net.Tick + 1);
            if (!t.TopFace || !Blocked(check.Result)) return a;
            Dir side = pull ? t.SideDir.Opposite() : t.SideDir;
            Dir[] order = { side, side.Opposite(), first.Opposite() };
            foreach (Dir d in order)
            {
                SculptAction b = SculptAction.Move(t.Sub, d, t.Point, ReinforceHeld);
                SculptCheck c = Sculpt.Evaluate(b, Net.Tick + 1);
                if (c.IsValid) { check = c; return b; }
            }
            return a; // nothing free: show why the edge you aimed at is blocked
        }

        private static bool Blocked(SculptResult r) => r == SculptResult.DestinationBlocked || r == SculptResult.PlayerBlocked;

        /// <summary>One 60 Hz prediction tick.</summary>
        public void Tick()
        {
#if UNITY_EDITOR
            if (_settingsAsset != null) _settingsAsset.ApplyTo(_cfg); // live tuning in Play mode
#endif
            bool active = CursorLocked && !InputBlocked;
            Vector2 move = active ? _input.Move.ReadValue<Vector2>() : Vector2.zero;
            bool sculpting = State.Hand.InSculptMode;
            HandState h0 = State.Hand;
            if (h0.Slot != _lastSlot) { _lastSlot = h0.Slot; _zoomOn = false; }
            if (sculpting) _zoomOn = false;
            if (move.sqrMagnitude < 0.01f) _sprintOn = false; // a toggled sprint ends when you stop
            // ...and when you fire or aim (Ben: toggles end on their own, so you rarely press again to turn them off).
            if (_client.SprintToggle && active && !sculpting && (_input.Fire.IsPressed() || _input.Aim.IsPressed())) _sprintOn = false;
            bool sprintHeld = active && (_client.SprintToggle ? _sprintOn : _input.Sprint.IsPressed());
            bool crouchHeld = active && (_client.CrouchToggle ? _crouchOn : _input.Crouch.IsPressed());
            bool aimHeld = active && !sculpting && (_client.ZoomToggle ? _zoomOn : _input.Aim.IsPressed());
            Zoomed = aimHeld && h0.Mode == HandMode.Weapon;
            var mi = new MoveInput
            {
                MoveX = move.x,
                MoveZ = move.y,
                Yaw = Yaw * Mathf.Deg2Rad,
                JumpPressed = _jumpLatch && active,
                JumpHeld = active && _input.Jump.IsPressed(),
                CrouchPressed = _crouchLatch && active,
                CrouchHeld = crouchHeld,
                SprintHeld = sprintHeld,
                // In sculpt mode the mouse buttons sculpt; they never stop your sprint.
                WantsToShoot = active && !sculpting && (_input.Fire.IsPressed() || aimHeld),
                FireHeld = active && !sculpting && _input.Fire.IsPressed(),
                FirePressed = active && !sculpting && _fireLatch,
                AimPressed = active && !sculpting && _aimLatch,
            };
            _fireLatch = false;
            _aimLatch = false;
            var hand = new HandInput
            {
                SculptToggle = _toggleLatch && active, WeaponKey = active ? _weaponLatch : (byte)0, Reload = _reloadLatch && active,
                Pickup = _pickupLatch && active, PickId = _pickupId, Drop = _dropLatch && active, Punch = _punchLatch && active,
                Door = _doorLatch && active, DoorTile = _doorTile, DoorOpen = _doorOpen,
            };
            if (_healWant && (!active || (!QuickHeal && !InSculptMode) || Time.unscaledTime - _healWantTime > 0.25f)) _healWant = false;
            if (_healWant && _cfg.Hand.CanHeal(h0))
            {
                _healWant = false;
                hand.Heal = true;
                hand.HealTile = _healWantTile;
                hand.HealCell = _healWantCell;
                hand.HealReinforce = _healWantKind == HealKind.Reinforce;
            }
            _reloadLatch = false;
            _punchLatch = false;
            _pickupLatch = false;
            _dropLatch = false;
            _doorLatch = false;
            bool respawn = _respawnLatch;
            bool confirm = _confirmLatch && active;
            SculptAction action = _confirmAction;
            _jumpLatch = false;
            _crouchLatch = false;
            _respawnLatch = false;
            _toggleLatch = false;
            _weaponLatch = 0;
            _confirmLatch = false;

            if ((_autoRace || _autoWatch) && Net.Joined && !State.Dead)
            {
                _showcaseLatch = _autoRace;
                _watchLatch = _autoWatch && !_autoRace;
                _autoRace = _autoWatch = false;
            }
            // F6: warp to the course start (a real command the server applies), count down 3-2-1 and race the TAS ghost.
            // F7: warp there and let the TAS drive you. Either key again stops what is running; a move key stops F7.
            if (_showcaseLatch || _watchLatch)
            {
                bool race = _showcaseLatch;
                _showcaseLatch = _watchLatch = false;
                if (_showcase != null || _race != null) { StopShowcase(); StopRace(); }
                else
                {
                    Net.WarpNext = true;
                    _warping = true;
                    _warpForRace = race;
                }
            }
            if (_race != null && !_race.Started)
            {
                // The countdown: look around, but stay on the start line.
                mi = new MoveInput { Yaw = mi.Yaw };
                hand = default;
            }
            if (_showcase != null && active && move.sqrMagnitude > 0.01f) StopShowcase();
            if (_showcase != null)
            {
                mi = _showcase.Next(State);
                _showcaseYaw = (float)(mi.Yaw * Mathf.Rad2Deg); // the camera eases toward it per frame (SampleFrame)
                if (_showcase.Finished) StopShowcase();
            }

            MoveMode modeBefore = State.Mode;
            int before = Events.Count;
            Net.PredictTick(mi, Pitch, respawn, hand, confirm, action, Events);
            // Toggle crouch: one press is a whole slide. When the slide runs out on the ground you stand back up instead
            // of being left crouch-walking (jumping out of a slide keeps it on, so the landing slides again).
            if (_client.CrouchToggle && _crouchOn && modeBefore == MoveMode.Slide && State.Mode == MoveMode.Ground) _crouchOn = false;
            if (_race != null)
            {
                _race.Step(State);
                if (_race.PlayerFinished && ++_raceOverTicks == 1)
                    Debug.Log($"[SCULPT] Course race: you {_race.PlayerFinishTicks / (float)SimTime.TicksPerSecond:0.00} s, TAS {_race.GhostFinishTicks / (float)SimTime.TicksPerSecond:0.00} s.");
                if (_raceOverTicks > SimTime.TicksPerSecond * 8) StopRace(); // the result stays up for 8 s
            }
            for (int i = before; i < Events.Count; i++)
            {
                if (Events[i].Type != MoveEventType.Respawned) continue;
                _crouchOn = _sprintOn = false; // toggles never carry over a respawn
                if (_warping && _course != null)
                {
                    _warping = false;
                    Yaw = _showcaseYaw = (float)(_course.StartYaw * Mathf.Rad2Deg);
                    Pitch = _warpForRace ? 0f : 12f;
                    if (_warpForRace)
                    {
                        // Record the TAS ghost from the same start through the same movement code (~25 s of ticks, a
                        // few milliseconds, once).
                        _race = CourseRace.Begin(Net.Grid, _course, _cfg);
                        _raceOverTicks = 0;
                        Debug.Log($"[SCULPT] Course race: 3-2-1 (TAS {_race.GhostFinishTicks / (float)SimTime.TicksPerSecond:0.00} s).");
                    }
                    else
                    {
                        _showcase = new ShowcaseRun(_course, _cfg);
                        Debug.Log("[SCULPT] Movement showcase: started.");
                    }
                }
                else
                {
                    if (_race != null) StopRace(); // died or respawned mid-race
                    FaceSpawn();
                }
            }
        }

        /// <summary>Turn to face the way your spawn faces (a new match moved you back there).</summary>
        public void FaceSpawnNow() => FaceSpawn();

        /// <summary>Turns the view to look level at a point (battle royale: the map's middle when a match is dealt).</summary>
        public void FaceTowards(Vec3d point)
        {
            Vec3d d = point - State.Position;
            if (d.X * d.X + d.Z * d.Z < 1e-6) return;
            Yaw = (float)(System.Math.Atan2(d.X, d.Z) * Mathf.Rad2Deg);
            Pitch = 0;
        }

        private void StopRace()
        {
            _race = null;
            _raceOverTicks = 0;
        }

        private void StopShowcase()
        {
            ShowcaseRun r = _showcase;
            _showcase = null;
            LastShowcase = r;
            if (r == null) return;
            string how = r.Current == ShowcaseRun.Phase.Done ? "finished" : r.Current == ShowcaseRun.Phase.Failed ? "FAILED: " + r.FailReason : "stopped in " + r.Current;
            Debug.Log($"[SCULPT] Movement showcase {how} after {r.Ticks / (float)SimTime.TicksPerSecond:0.00} s, top speed {r.TopSpeed:0.0} m/s.");
        }

        /// <summary>Interpolates between the last two predicted ticks. alpha in [0, 1].</summary>
        /// <summary>The alpha of the last Render call (where the drawn player sits between the last two ticks).</summary>
        public float RenderAlpha { get; private set; }

        public void Render(float alpha)
        {
            RenderAlpha = alpha;
            MotorState prev = Net.PreviousPredicted, cur = Net.Predicted;
            // A warp or respawn jumps metres in one tick: draw the new spot, never a one-frame streak across the map.
            Vector3 from = prev.Position.ToUnity(), to = cur.Position.ToUnity();
            if ((to - from).sqrMagnitude > 16f) from = to;
            RenderFeet = Vector3.Lerp(from, to, alpha) + Net.CorrectionOffset.ToUnity();
            RenderSpeed = Mathf.Lerp((float)prev.HorizontalSpeed, (float)cur.HorizontalSpeed, alpha);
            // Recoil kicks are instant (no lerp from the previous tick's value when it rose), recovery is smooth.
            float pp = (float)prev.Weapon.RecoilPitch, cp = (float)cur.Weapon.RecoilPitch;
            float py = (float)prev.Weapon.RecoilYaw, cy = (float)cur.Weapon.RecoilYaw;
            RenderRecoil = cp > pp ? new Vector2(cp, cy) : new Vector2(Mathf.Lerp(pp, cp, alpha), Mathf.Lerp(py, cy, alpha));
        }

        public float TargetEyeHeight => (float)PlayerMovement.EyeOf(State, _cfg);
    }
}
