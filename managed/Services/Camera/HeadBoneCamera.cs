// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/HeadBoneCamera.cs
// PURPOSE: Animated head-eye sampling
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Samples the correct rig and equipment-dependent head location. Never
//   treat a helmet replacement as a reason to modify the player world
//   position.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Services.Camera;

/// <summary>
/// Fix13: visual eye positioning from the LOCAL creature's animated head joint.
/// Never writes creature world XYZ, animation state, or collision. An independently
/// adjustable first-person facing lock can write creature horizontal orientation.
/// This is not an eye-socket rig: a head joint can be inside the head mesh.
/// </summary>
internal static class HeadBoneCamera
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<int> Mode;
    internal static readonly Knob<bool> AirLock;
    internal static readonly Knob<bool> Face;
    internal static readonly Knob<bool> MovementTether, EyeLock;
    internal static readonly Knob<float> EyeLockMaxError, VisualShiftMax;
    internal static readonly Knob<float> MovementClearance;
    internal static readonly Knob<float> Strength, Forward, Up, Response, MaxShift;

    private static IntPtr _player, _root;
    private static Vector3 _neutralHead, _offset;
    private static bool _neutral, _havePrevious;
    private static Vector3 _lastEye;
    private static int _lastMode = -1;
    private static long _samples, _missing, _clamped, _resets, _direct, _relative, _facingWrites;

    static HeadBoneCamera()
    {
        G = KnobGroup.Declare("fphead", "Controls", "Camera", order: 520,
                              title: "First-person head-bone camera (experimental)");
        Face = G.Toggle("face", "Mouse-look turns the character", true,
                        tier: Tier.Advanced,
                        help: "In first person the creature faces the camera's horizontal direction while yaw and pitch remain under mouse control. Client-side experimental facing.");
        Mode = G.Slider("mode", "Head-bone tracking (0 off, 1 relative, 2 direct)",
                        0, 2, 1, 2, tier: Tier.Advanced,
                        help: "0 uses the existing Fix12 eye. 1 adds head movement. 2 tracks head joint location, capped and smoothed.");
        AirLock = G.Toggle("airlock", "Preserve native jump camera while airborne", true,
                           tier: Tier.Advanced,
                           help: "While the actual character is airborne, suspend head-bone camera translation. Keeps native jump and air momentum as the sole position source; character facing remains locked.");
        Strength = G.Slider("strength", "Head tracking blend", 0f, 100f, 5f, 100f, "%",
                            Tier.Advanced, help: "How much head-joint motion changes the eye position.");
        Forward = G.Slider("forward", "Eye forward from head joint", -0.05f, 0.30f, 0.01f, 0.13f, "m",
                           Tier.Advanced, help: "Fixed camera-relative horizontal offset from the mapped head joint (direct mode).");
        Up = G.Slider("up", "Eye above head joint", -0.15f, 0.25f, 0.01f, 0.03f, "m",
                      Tier.Advanced, help: "Vertical correction from the mapped head joint (direct mode).");
        MovementTether = G.Toggle("movetether", "Keep walking model behind the eye", true,
            tier: Tier.Advanced,
            help: "In first-person locomotion, keep the final visual eye ahead of the animated head and torso AFTER the world surface guard. No movement or animation writes.");
        MovementClearance = G.Slider("moveahead", "Movement eye clearance", 0.13f, 0.35f, 0.01f, 0.22f, "m",
            tier: Tier.Advanced,
            help: "Forward clearance from the animated head pivot when walking or running. Increase slightly for helmets that extend past the head joint.");
        EyeLock = G.Toggle("eyelock", "Anchor eye to animated head each frame", true,
            Tier.Advanced, help: "In direct head mode, follow the actual head joint each frame, including jumps; fail open if the rig is implausibly far away.");
        EyeLockMaxError = G.Slider("eyelockmax", "Maximum eye-anchor discrepancy", 0.30f, 1.50f, 0.05f, 1.10f, "m",
            Tier.Advanced, help: "Reject an implausibly distant head joint on a model swap, teleport or stale pose. Not an eye height.");
        VisualShiftMax = G.Slider("modelbackmax", "Maximum wall-contact visual model shift", 0.10f, 1.20f, 0.05f, 0.70f, "m",
            Tier.Advanced, help: "Only the local character's rendered color-pass geometry is moved with a collision-displaced eye; never affects gameplay coordinates.");
        Response = G.Slider("response", "Head tracking response", 2f, 60f, 1f, 30f, "/s",
                            Tier.Advanced, help: "Smoothing rate; high values follow animated lunges faster.");
        MaxShift = G.Slider("maxshift", "Maximum head-camera correction", 0.10f, 0.80f, 0.05f, 0.45f, "m",
                            Tier.Advanced, help: "Cap measured from the existing eye to prevent large rig errors.");
    }

    private static bool Finite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    private static void Clear(bool changed)
    {
        if (changed) _resets++;
        _player = _root = IntPtr.Zero;
        _neutralHead = _offset = _lastEye = default;
        _neutral = _havePrevious = false;
        _lastMode = -1;
    }

    /// <summary>
    /// Runs BEFORE existing Fix9/11 visual motion and Fix12 wall guard. Camera
    /// drives creature facing in the horizontal plane, but NEVER inherits head rotations.
    /// </summary>
    internal static void Apply(IntPtr player, float dt, ref Vector3 eye, Quaternion rotation)
    {
        if (player == IntPtr.Zero || !Finite(eye) || !Finite(rotation))
        { Clear(false); return; }
        TurnCharacter(player, rotation);
        int mode = Mode.V;
        if (mode == 0) { Clear(false); return; }
        // The head's WORLD position already includes the native creature jump.
        // Adding a second visual correction to the docked eye causes an apparent
        // double-jump and extra forward speed. The camera follows the native body
        // unchanged during flight; the facing lock above still runs.
        if (mode == 1 && AirLock.V && JumpPositionService.AirborneFor(player))
        {
            _offset = Vector3.Zero;
            _neutral = _havePrevious = false;
            _root = IntPtr.Zero; // Re-calibrate once on landing or model changes.
            return;
        }
        if (mode != _lastMode || player != _player)
        { Clear(_player != IntPtr.Zero); _player = player; _lastMode = mode; }

        dt = Math.Clamp(dt, 0f, 0.1f);
        // A teleport, body swap, or pause cannot inherit a stale visual offset.
        if (_havePrevious && Vector3.Distance(eye, _lastEye) > 2.5f)
        { Clear(true); _player = player; _lastMode = mode; }
        _lastEye = eye; _havePrevious = true;
        if (!PoseService.TryCameraRig(player, out var rig) || !rig.HasHead || rig.Root == IntPtr.Zero
            || !Finite(rig.HeadWorld) || !Finite(rig.HeadLocal) || !Finite(rig.RootRotation))
        { _missing++; _offset = Vector3.Zero; return; }
        if (_root != rig.Root)
        {
            if (_root != IntPtr.Zero) _resets++;
            _root = rig.Root; _neutral = false; _offset = Vector3.Zero;
        }
        _samples++;
        Vector3 correction;
        if (mode == 1)
        {
            // Root-relative head motion: world travel is already handled by
            // the original dock, so never add absolute world motion twice.
            int anim = new CNWCObject(player).Animation;
            bool melee = anim is >= 0xc9 and <= 0xcd;
            if (!_neutral)
            { _neutralHead = rig.HeadLocal; _neutral = true; }
            if (!melee)
            {
                float idleBlend = 1f - MathF.Exp(-2f * dt);
                _neutralHead = Vector3.Lerp(_neutralHead, rig.HeadLocal, idleBlend);
            }
            correction = Vector3.Transform(rig.HeadLocal - _neutralHead, rig.RootRotation);
            _relative++;
        }
        else
        {
            // Bone world location is already animated, including native jump
            // and creature locomotion. Offset toward the view, not helmet geometry.
            Vector3 forward = Vector3.Transform(-Vector3.UnitZ, rotation);
            forward.Z = 0f;
            if (forward.LengthSquared() < 0.001f)
                forward = Vector3.Transform(Vector3.UnitY, rig.RootRotation);
            forward.Z = 0f;
            if (forward.LengthSquared() < 0.001f) { _missing++; return; }
            forward = Vector3.Normalize(forward);
            Vector3 desiredEye = rig.HeadWorld + forward * Forward.V + Vector3.UnitZ * Up.V;
            correction = desiredEye - eye;
            _direct++;
            // No time-lag during jumps or fast attacks: match this frame's
            // animated head position. Abnormal rigs fail open to the original
            // dock; never teleport the eye to a corrupt or stale joint.
            if (EyeLock.V)
            {
                if (Finite(desiredEye) && correction.LengthSquared() <= EyeLockMaxError.V * EyeLockMaxError.V)
                { eye = desiredEye; _offset = Vector3.Zero; return; }
                _missing++; _offset = Vector3.Zero; return;
            }
        }
        if (!Finite(correction)) { _missing++; _offset = Vector3.Zero; return; }
        float distance = correction.Length();
        if (distance > MaxShift.V)
        { correction *= MaxShift.V / distance; _clamped++; }
        float blend = 1f - MathF.Exp(-Response.V * dt);
        _offset = Vector3.Lerp(_offset, correction, blend);
        if (!Finite(_offset)) { _offset = Vector3.Zero; _missing++; return; }
        eye += _offset * (Strength.V / 100f);
    }

    /// <summary>
    /// First-person-only call from the gated camera branch. Matches the drive system's
    /// existing facing-vector writes, but allows stationary mouse turns too.
    /// No head rotations, pitch, camera yaw rewrites, or movement packets are synthesized.
    /// </summary>
    private static void TurnCharacter(IntPtr player, Quaternion rotation)
    {
        if (!Face.V) return;
        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        forward.Z = 0f;
        if (!Finite(forward) || forward.LengthSquared() < 0.001f) return;
        forward = Vector3.Normalize(forward);
        var obj = new CNWCObject(player);
        // Avoid repeated writes when the movement hook already placed the
        // creature on the camera heading; fight neither server or animation unnecessarily.
        Vector3 was = obj.Orientation;
        if (Finite(was) && Vector3.DistanceSquared(was, forward) < 0.000004f) return;
        obj.Orientation = forward;
        var creature = new CNWCCreature(player);
        creature.FacingTarget = forward;
        creature.FacingTargetB = forward;
        _facingWrites++;
    }

    private static long _movementTethers, _movementMisses, _movementSkips, _movementReleaseFrames;
    private static float _lastMovementAdvance;
    private static IntPtr _tetherPlayer, _tetherRoot;
    private static Vector3 _tetherCarry;

    private static void ResetMovementTether()
    {
        _tetherCarry = Vector3.Zero;
        _tetherPlayer = _tetherRoot = IntPtr.Zero;
        _lastMovementAdvance = 0f;
    }

    // The world guard MUST have the final say on the rendered eye.
    // This calculates a model-relative GOAL before the surface guard runs.
    // A walking-to-idle transition fades the extra forward offset rather than
    // dropping it in one frame (which caused the movement-release camera jolt).
    // Never touch combat, creature coordinates, collision checks or animations.
    internal static void KeepMovementModelBehindEye(IntPtr player, float dt, ref Vector3 eye, Quaternion rotation)
    {
        _lastMovementAdvance = 0f;
        if (!MovementTether.V || player == IntPtr.Zero || !Finite(eye) || !Finite(rotation)
            || !FirstPersonWorldGuard.FirstPersonActive
            || (AirLock.V && JumpPositionService.AirborneFor(player)))
        { ResetMovementTether(); return; }
        if (_tetherPlayer != player)
        { ResetMovementTether(); _tetherPlayer = player; }

        int animation = new CNWCObject(player).Animation;
        // Preserve the tested Fix14B attack camera without even a tail offset.
        if (animation is >= 0xc9 and <= 0xcd) { ResetMovementTether(); return; }
        bool locomotion = animation is (2 or 3 or 4 or 0x4e or 0x4f or 0x54 or 0x55 or 0x56 or 0x57);
        if (!locomotion)
        {
            // At a stop, do not jump back by the entire tether offset.
            // This is solely a camera target; WorldGuard still clamps it.
            float decay = MathF.Exp(-10f * Math.Clamp(dt, 0f, 0.1f));
            _tetherCarry *= decay;
            if (_tetherCarry.LengthSquared() < 0.000001f) _tetherCarry = Vector3.Zero;
            eye += _tetherCarry;
            _lastMovementAdvance = _tetherCarry.Length();
            _movementReleaseFrames++;
            return;
        }
        if (!PoseService.TryCameraRig(player, out var rig) || !rig.HasHead
            || rig.Root == IntPtr.Zero || !Finite(rig.HeadWorld) || !Finite(rig.HeadLocal)
            || !Finite(rig.TorsoLocal) || !Finite(rig.RootRotation)
            || rig.RootRotation.LengthSquared() < 0.5f)
        { _movementMisses++; ResetMovementTether(); return; }
        if (Vector3.DistanceSquared(eye, rig.HeadWorld) > 6.25f)
        { _movementSkips++; ResetMovementTether(); return; }
        if (_tetherRoot != rig.Root) { _tetherRoot = rig.Root; _tetherCarry = Vector3.Zero; }

        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        forward.Z = 0f;
        if (forward.LengthSquared() < 0.0001f)
        {
            forward = Vector3.Transform(Vector3.UnitY, rig.RootRotation);
            forward.Z = 0f;
        }
        if (!Finite(forward) || forward.LengthSquared() < 0.0001f)
        { _movementMisses++; ResetMovementTether(); return; }
        forward = Vector3.Normalize(forward);
        Vector3 torsoWorld = rig.HeadWorld
            + Vector3.Transform(rig.TorsoLocal - rig.HeadLocal, rig.RootRotation);
        float headAdvance = Vector3.Dot(rig.HeadWorld - eye, forward) + MovementClearance.V;
        float torsoAdvance = Vector3.Dot(torsoWorld - eye, forward) + MovementClearance.V * 0.75f;
        float advance = MathF.Max(0f, MathF.Max(headAdvance, torsoAdvance));
        if (!float.IsFinite(advance)) { _movementMisses++; ResetMovementTether(); return; }
        _tetherCarry = forward * advance;
        eye += _tetherCarry;
        _lastMovementAdvance = advance;
        if (advance > 0f) _movementTethers++;
    }

    private static long _postAttackGuards;
    /// <summary>After Fix14B attack motion: retain the attack's vertical and
    /// lateral camera animation, but prevent the head pivot overtaking the eye
    /// along the horizontal viewing direction. World collision runs afterward;
    /// its retreat is paired with a render-only model translation.</summary>
    internal static void MaintainHeadClearance(IntPtr player, ref Vector3 eye, Quaternion rotation)
    {
        if (!EyeLock.V || Mode.V != 2 || !FirstPersonWorldGuard.FirstPersonActive || player == IntPtr.Zero
            || !Finite(eye) || !Finite(rotation)) return;
        if (!PoseService.TryCameraRig(player, out var rig) || !rig.HasHead || !Finite(rig.HeadWorld)) return;
        if (Vector3.DistanceSquared(eye, rig.HeadWorld) > 1.75f * 1.75f) return;
        Vector3 f = Vector3.Transform(-Vector3.UnitZ, rotation);
        f.Z = 0f;
        if (!Finite(f) || f.LengthSquared() < 0.01f) return;
        f = Vector3.Normalize(f);
        float shortfall = Forward.V - Vector3.Dot(eye - rig.HeadWorld, f);
        if (shortfall > 0f)
        {
            eye += f * MathF.Min(shortfall, 0.35f);
            _postAttackGuards++;
        }
    }

    internal static string Diagnostics() =>
        $"fphead: mode={Mode.V} face={(Face.V ? 1 : 0)} airlock={(AirLock.V ? 1 : 0)} air={(JumpPositionService.IsAirborne ? 1 : 0)} facingWrites={_facingWrites} strength={Strength.V:0}% headSamples={_samples} " +
        $"direct={_direct} relative={_relative} missingRig={_missing} " +
        $"clamps={_clamped} modelResets={_resets} shift={_offset.Length():0.000}m " +
        "(position-only; eyeLock=" + (EyeLock.V ? "on" : "off") + $" maxError={EyeLockMaxError.V:0.00}m modelBackMax={VisualShiftMax.V:0.00}m postAnim={_postAttackGuards}; moving tether=" + (MovementTether.V ? "on" : "off") + $" clearance={MovementClearance.V:0.00}m tethers={_movementTethers} missing={_movementMisses} stale={_movementSkips} lastAdvance={_lastMovementAdvance:0.000}m easeFrames={_movementReleaseFrames} (world guard last))";
}
