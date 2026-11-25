using Sculpt.Core;
using UnityEngine;

namespace Sculpt.Game.Config
{
    /// <summary>
    /// Tunable movement numbers. Create one with Assets > Create > SCULPT > Movement Settings and assign it to
    /// the Bootstrap object. Edits apply live in Play mode. Defaults match CLAUDE.md.
    /// </summary>
    [CreateAssetMenu(menuName = "SCULPT/Movement Settings", fileName = "MovementSettings")]
    public sealed class MovementSettings : ScriptableObject
    {
        [Header("Speeds (m/s)")]
        public float runSpeed = 7f;
        public float sprintSpeed = 10f;
        public float crouchSpeed = 3.5f;

        [Header("Acceleration (m/s^2)")]
        public float groundAccel = 60f;
        public float groundDecel = 60f;
        public float overspeedDecel = 20f;
        [Range(0, 1)] public float airAccelFraction = 0.15f;

        [Header("Jump and gravity")]
        public float gravity = 30f;
        public float jumpApex = 1.3f;
        public float maxFallSpeed = 40f;
        [Tooltip("Every tick count on this asset is in sixtieths of a second (the unit the design spec uses), whatever the simulation's tick rate.")]
        public int coyoteTicks = 5;
        public int jumpBufferTicks = 6;

        [Header("Slide")]
        public float slideBoostSpeed = 13f;
        public float slideFriction = 4.5f;
        public float slideMaxSpeed = 18f;
        public float slideEndSpeed = 4f;
        public float slideMinStartSpeed = 5f;
        public float slideTurnRate = 1.6f;
        public float slideCarveTurnRate = 4.5f;
        [Range(0, 1)] public float slideCarveLoss = 0.08f;
        public int slideBoostCooldownTicks = 48;
        public int slideLandWindowTicks = 9;
        public float slideLandMinSpeed = 5f;

        [Header("Vault and mantle")]
        public bool autoVault = false;
        public bool holdJumpClimbs = true;
        public int vaultTicks = 8;
        public float vaultMinSpeed = 5f;
        public int mantleRiseTicks = 16;
        public int mantleForwardTicks = 8;
        public float mantleExitSpeedFraction = 0.5f;
        public float vaultMaxHeight = 1.55f;
        public float mantleMaxHeight = 3.05f;
        [Range(0, 1)] public float mantleKeepSpeedFraction = 1f;
        public int mantleCancelFromTick = 8;
        public float mantleCancelMaxRemaining = 0.6f;
        public float mantleCancelHopSpeed = 3f;

        [Header("Momentum: wall kick")]
        public bool wallKicksEnabled = false;
        public float wallKickUpSpeed = 6.5f;
        public float wallKickPush = 2.5f;
        public float wallKickMinSpeed = 6f;
        public int wallKickPerfectTicks = 6;
        [Range(0, 1)] public float wallKickLateKeep = 0.85f;
        public int wallKickMaxPerAir = 3;
        public int wallKickCooldownTicks = 12;

        [Header("Momentum: counter-strafe brake")]
        public float counterBrakeMinSpeed = 7f;
        public float counterBrakeDecel = 120f;
        [Range(-1, 1)] public float counterBrakeMaxDot = 0.25f;

        [Header("Momentum: air and hops")]
        public float airNudgeTurnRate = 0.6f;
        public float airBrakeDecel = 3f;
        public float airSoftCapSpeed = 18f;
        public float airSoftCapDecel = 4f;
        [Range(0, 1)] public float slideHopKeep = 0.95f;
        public int slideHopChainTicks = 60;

        [Header("Other")]
        public int sprintToAimTicks = 9;
        public float fallDamageFreeLevels = 6f;
        public float fallDamagePerLevel = 10f;

        public void ApplyTo(MovementConfig c)
        {
            c.RunSpeed = runSpeed;
            c.SprintSpeed = sprintSpeed;
            c.CrouchSpeed = crouchSpeed;
            c.GroundAccel = groundAccel;
            c.GroundDecel = groundDecel;
            c.OverspeedDecel = overspeedDecel;
            c.AirAccelFraction = airAccelFraction;
            c.Gravity = gravity;
            c.JumpApex = jumpApex;
            c.MaxFallSpeed = maxFallSpeed;
            c.CoyoteTicks = coyoteTicks * SimTime.Per60;
            c.JumpBufferTicks = jumpBufferTicks * SimTime.Per60;
            c.SlideBoostSpeed = slideBoostSpeed;
            c.SlideFriction = slideFriction;
            c.SlideMaxSpeed = slideMaxSpeed;
            c.SlideEndSpeed = slideEndSpeed;
            c.SlideMinStartSpeed = slideMinStartSpeed;
            c.SlideTurnRate = slideTurnRate;
            c.SlideCarveTurnRate = slideCarveTurnRate;
            c.SlideCarveLoss = slideCarveLoss;
            c.SlideBoostCooldownTicks = slideBoostCooldownTicks * SimTime.Per60;
            c.SlideLandWindowTicks = slideLandWindowTicks * SimTime.Per60;
            c.SlideLandMinSpeed = slideLandMinSpeed;
            c.AutoVault = autoVault;
            c.HoldJumpClimbs = holdJumpClimbs;
            c.VaultTicks = vaultTicks * SimTime.Per60;
            c.VaultMinSpeed = vaultMinSpeed;
            c.MantleRiseTicks = mantleRiseTicks * SimTime.Per60;
            c.MantleForwardTicks = mantleForwardTicks * SimTime.Per60;
            c.MantleExitSpeedFraction = mantleExitSpeedFraction;
            c.VaultMaxHeight = vaultMaxHeight;
            c.MantleMaxHeight = mantleMaxHeight;
            c.MantleKeepSpeedFraction = mantleKeepSpeedFraction;
            c.MantleCancelFromTick = mantleCancelFromTick * SimTime.Per60;
            c.MantleCancelMaxRemaining = mantleCancelMaxRemaining;
            c.MantleCancelHopSpeed = mantleCancelHopSpeed;
            c.WallKicksEnabled = wallKicksEnabled;
            c.WallKickUpSpeed = wallKickUpSpeed;
            c.WallKickPush = wallKickPush;
            c.WallKickMinSpeed = wallKickMinSpeed;
            c.WallKickPerfectTicks = wallKickPerfectTicks * SimTime.Per60;
            c.WallKickLateKeep = wallKickLateKeep;
            c.WallKickMaxPerAir = wallKickMaxPerAir;
            c.WallKickCooldownTicks = wallKickCooldownTicks * SimTime.Per60;
            c.CounterBrakeMinSpeed = counterBrakeMinSpeed;
            c.CounterBrakeDecel = counterBrakeDecel;
            c.CounterBrakeMaxDot = counterBrakeMaxDot;
            c.AirNudgeTurnRate = airNudgeTurnRate;
            c.AirBrakeDecel = airBrakeDecel;
            c.AirSoftCapSpeed = airSoftCapSpeed;
            c.AirSoftCapDecel = airSoftCapDecel;
            c.SlideHopKeep = slideHopKeep;
            c.SlideHopChainTicks = slideHopChainTicks * SimTime.Per60;
            c.SprintToAimTicks = sprintToAimTicks * SimTime.Per60;
            c.FallDamageFreeLevels = fallDamageFreeLevels;
            c.FallDamagePerLevel = fallDamagePerLevel;
        }
    }
}
