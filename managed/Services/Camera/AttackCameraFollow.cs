// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/AttackCameraFollow.cs
// PURPOSE: Attack motion following
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   The eye follows player attack motion while respecting near-world
//   clearance. Full torso/head must not pass through the eye; limbs and
//   equipment should stay visible.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Diagnostics;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Services.Camera;

/// <summary>
/// Fix11: visual-only, local-player first-person attack camera. The body's
/// animated head/torso motion influences the eye, but never drives the
/// creature, jump motor, camera yaw/pitch, targeting or animation itself.
/// </summary>
internal static class AttackCameraFollow
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Roll;
    internal static readonly Knob<float> Strength, Travel, Response;

    private const float MaxSide = 0.12f, MaxVertical = 0.11f;
    private const float RollCap = 0.014f; // 0.80 degrees, additive to Fix9 roll
    private const float SpinRate = 3.5f; // rad/s; reject fast root yaw turns
    private static IntPtr _player, _root;
    private static Vector3 _neutralTorso, _neutralHead, _neutralUp;
    private static Vector3 _offset, _rootForward;
    private static float _roll, _scale = 1f;
    private static bool _neutralReady, _headReady, _rootForwardReady;
    private static long _lastTick;
    private static long _active, _sampleMiss, _spinIgnored, _rigResets;

    static AttackCameraFollow()
    {
        G = KnobGroup.Declare("attackfollow", "Controls", "Camera", order: 518,
                              title: "First-person attack tracking");
        Enabled = G.Toggle("enabled", "Track attack animation with the camera", true,
            help: "Adds measured head and torso translation during melee attacks; never changes character movement.");
        Strength = G.Slider("strength", "Attack tracking strength", 0f, 100f, 5f, 60f, "%",
            help: "Blend of the actual animated translation transferred to the eye. Start at 60%. ");
        Travel = G.Slider("travel", "Maximum forward tracking", 0f, 0.5f, 0.025f, 0.28f, "m",
            Tier.Advanced, help: "Camera displacement ceiling for lunges; independent of the world position.");
        Response = G.Slider("response", "Tracking response", 2f, 30f, 1f, 16f, "/s",
            Tier.Advanced, help: "Higher follows animation more closely; lower smooths rapid changes.");
        Roll = G.Toggle("roll", "Subtle attack camera roll", true,
            help: "Allows at most an extra 0.8 degrees of roll. Never follows full character spins.");
    }

    private static bool Finite(Vector3 x) =>
        float.IsFinite(x.X) && float.IsFinite(x.Y) && float.IsFinite(x.Z);
    private static bool Finite(Quaternion x) =>
        float.IsFinite(x.X) && float.IsFinite(x.Y) && float.IsFinite(x.Z) && float.IsFinite(x.W);
    private static bool Melee(int animation) => animation is >= 0xc9 and <= 0xcd;

    private static void Reset(bool changed = false)
    {
        if (changed) _rigResets++;
        _player = _root = IntPtr.Zero;
        _neutralTorso = _neutralHead = _neutralUp = _offset = Vector3.Zero;
        _rootForward = Vector3.Zero;
        _roll = 0f;
        _scale = 1f;
        _neutralReady = _headReady = _rootForwardReady = false;
        _lastTick = 0;
    }

    /// <summary>Only called from Fix10's first-person eye path after Fix9's motion.
    /// This is intentionally an overlay: it never edits the original Fix9 response.</summary>
    internal static void Apply(IntPtr player, float dt, ref Vector3 eye, ref Quaternion rotation)
    {
        if (!Enabled.V || player == IntPtr.Zero || !Finite(eye) || !Finite(rotation))
        { Reset(); return; }

        long now = Stopwatch.GetTimestamp();
        if (_lastTick != 0 && now - _lastTick > Stopwatch.Frequency / 4) Reset();
        if (_player != player) { Reset(); _player = player; }
        _lastTick = now;
        dt = Math.Clamp(dt, 0f, 0.1f);

        int anim = new CNWCObject(player).Animation;
        bool attack = Melee(anim);
        bool sample = PoseService.TryCameraRig(player, out var rig)
                      && rig.Root != IntPtr.Zero
                      && Finite(rig.TorsoLocal) && Finite(rig.TorsoUp)
                      && Finite(rig.RootRotation)
                      && rig.RootRotation.LengthSquared() > 0.5f;
        Vector3 target = Vector3.Zero;
        float targetRoll = 0f;

        if (!sample) _sampleMiss++;
        else
        {
            if (_root != rig.Root)
            {
                bool hadRoot = _root != IntPtr.Zero;
                Reset(changed: hadRoot);
                _player = player;
                _root = rig.Root;
                _lastTick = now;
            }
            Vector3 rootForward = Vector3.Transform(Vector3.UnitY, rig.RootRotation);
            bool spinning = false;
            if (_rootForwardReady && dt > 0.0001f)
            {
                float dot = Math.Clamp(Vector3.Dot(rootForward, _rootForward), -1f, 1f);
                spinning = MathF.Acos(dot) / dt > SpinRate;
                if (spinning) _spinIgnored++;
            }
            _rootForward = rootForward;
            _rootForwardReady = true;

            // No guessed universal bone rest pose. Take a reference while this
            // exact character is idle; slowly adapt to stance or gear changes.
            if (!attack)
            {
                float t = 1f - MathF.Exp(-2.0f * dt);
                if (!_neutralReady)
                {
                    _neutralTorso = rig.TorsoLocal;
                    _neutralUp = rig.TorsoUp;
                    _neutralReady = true;
                    _headReady = false;
                    float size = rig.HasHead && Finite(rig.HeadLocal)
                        ? rig.HeadLocal.Length() : rig.TorsoLocal.Length();
                    _scale = Math.Clamp(1.2f / MathF.Max(size, 0.6f), 0.65f, 1.4f);
                }
                else
                {
                    // Large changes outside attacks mean a new rig/appearance.
                    if (Vector3.Distance(rig.TorsoLocal, _neutralTorso) > 0.9f)
                        _neutralReady = _headReady = false;
                    else
                    {
                        _neutralTorso = Vector3.Lerp(_neutralTorso, rig.TorsoLocal, t);
                        _neutralUp = Vector3.Lerp(_neutralUp, rig.TorsoUp, t);
                    }
                }
                if (rig.HasHead && Finite(rig.HeadLocal))
                {
                    if (!_headReady) { _neutralHead = rig.HeadLocal; _headReady = true; }
                    else if (Vector3.Distance(rig.HeadLocal, _neutralHead) > 0.9f) _headReady = false;
                    else _neutralHead = Vector3.Lerp(_neutralHead, rig.HeadLocal, t);
                }
                else _headReady = false;
            }
            else if (_neutralReady && !spinning)
            {
                Vector3 torso = rig.TorsoLocal - _neutralTorso;
                Vector3 movement = torso;
                if (_headReady && rig.HasHead && Finite(rig.HeadLocal))
                    movement = torso * 0.35f + (rig.HeadLocal - _neutralHead) * 0.65f;
                movement = Vector3.Transform(movement, rig.RootRotation) * _scale
                         * (Strength.V / 100f);

                Vector3 viewForward = Vector3.Transform(-Vector3.UnitZ, rotation);
                viewForward.Z = 0f;
                if (viewForward.LengthSquared() > 0.0001f)
                    viewForward = Vector3.Normalize(viewForward);
                else viewForward = rootForward;
                Vector3 right = new(viewForward.Y, -viewForward.X, 0f);
                float forward = Math.Clamp(Vector3.Dot(movement, viewForward),
                                           -Travel.V * 0.35f, Travel.V);
                float side = Math.Clamp(Vector3.Dot(movement, right), -MaxSide, MaxSide);
                float vertical = Math.Clamp(movement.Z, -MaxVertical, MaxVertical);
                target = viewForward * forward + right * side + Vector3.UnitZ * vertical;
                // Never inherit animation yaw/pitch or spin. Torso lean only,
                // with the roll quaternion applied in camera-local coordinates.
                if (Roll.V)
                    targetRoll = Math.Clamp((rig.TorsoUp.X - _neutralUp.X)
                              * Strength.V / 100f * 0.1f, -RollCap, RollCap);
            }
        }

        float response = attack ? Response.V : MathF.Min(Response.V, 9f);
        float alpha = 1f - MathF.Exp(-response * dt);
        _offset = Vector3.Lerp(_offset, target, alpha);
        _roll += (targetRoll - _roll) * alpha;
        if (!Finite(_offset) || !float.IsFinite(_roll)) { Reset(); return; }
        if (attack) _active++;
        eye += _offset;
        if (Roll.V && MathF.Abs(_roll) > 0.000001f)
            rotation = Quaternion.Normalize(rotation *
                    Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _roll));
    }

    internal static string Diagnostics() =>
        $"attackfollow: enabled={Enabled.V} strength={Strength.V:0}% " +
        $"forwardLimit={Travel.V:0.000}m roll={Roll.V} attacksFrames={_active} " +
        $"missingRig={_sampleMiss} spinsIgnored={_spinIgnored} " +
        $"modelResets={_rigResets} offset={_offset.Length():0.000}m";
}
