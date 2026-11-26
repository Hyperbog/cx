// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/AnimationCameraMotion.cs
// PURPOSE: Camera response to animation
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Subtle first-person attack and movement camera offsets are visual.
//   Preserve authored animation, locomotion speed and server authority.
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
using CX.Services.Anim;

namespace CX.Services.Camera;

/// <summary>Visual-only response to selected melee attacks; never alters world movement or controls.</summary>
internal static class AnimationCameraMotion
{
    private const float MaxForward = 0.065f, MaxSide = 0.026f, MaxVertical = 0.032f;
    private const float MaxRoll = 0.022f;                 // ~1.26 degrees
    private const float MaxModelScale = 1.5f, MinModelScale = 0.65f;
    private static IntPtr _player, _root;
    private static Vector3 _neutralTorso, _neutralHead, _neutralUp;
    private static bool _neutralValid, _neutralHeadValid;
    private static Vector3 _offset, _previousEye;
    private static Quaternion _lastRootRotation = Quaternion.Identity;
    private static float _roll, _headPush, _rigScale = 1f;
    private static long _lastFrame;
    private static int _attacks, _invalidRigs, _modelResets, _headGuards, _previousAnim = -1;
    private static bool _havePreviousEye;

    private static bool Finite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
    private static bool SelectedAttack(int anim) => anim is >= 0xc9 and <= 0xcd;

    private static void Reset(bool modelChange = false)
    {
        if (modelChange) _modelResets++;
        _player = _root = IntPtr.Zero;
        _neutralValid = _neutralHeadValid = _havePreviousEye = false;
        _offset = _neutralTorso = _neutralHead = _neutralUp = Vector3.Zero;
        _lastRootRotation = Quaternion.Identity;
        _roll = _headPush = 0f;
        _rigScale = 1f;
        _lastFrame = 0;
        _previousAnim = -1;
    }

    internal static void Apply(bool firstPerson, IntPtr player, float dt,
                               ref Vector3 cameraPosition, ref Quaternion cameraRotation)
    {
        if (!AnimationCameraSettings.Enabled.V || !firstPerson || player == IntPtr.Zero
            || !Finite(cameraPosition) || !Finite(cameraRotation))
        { Reset(); return; }

        long now = Stopwatch.GetTimestamp();
        if (_lastFrame != 0 && now - _lastFrame > Stopwatch.Frequency / 4) Reset();
        if (_player != player) { Reset(); _player = player; }
        _lastFrame = now;
        dt = Math.Clamp(dt, 0f, 0.1f);
        // World travel or teleport can invalidate a previously sampled animated rig.
        if (_havePreviousEye && Vector3.Distance(cameraPosition, _previousEye) > 2f)
        { Reset(modelChange: true); _player = player; _lastFrame = now; }
        _previousEye = cameraPosition;
        _havePreviousEye = true;

        int animation = new CNWCObject(player).Animation;
        bool attack = SelectedAttack(animation);
        if (attack && animation != _previousAnim) _attacks++;
        _previousAnim = animation;

        Vector3 target = Vector3.Zero;
        float targetRoll = 0f, targetPush = 0f;
        bool sampled = PoseService.TryCameraRig(player, out var rig);
        if (sampled && rig.Root != IntPtr.Zero && Finite(rig.TorsoLocal)
            && Finite(rig.TorsoUp) && Finite(rig.HeadLocal) && Finite(rig.HeadWorld)
            && Finite(rig.RootRotation) && rig.RootRotation.LengthSquared() > 0.5f)
        {
            if (_root != rig.Root) { Reset(modelChange: _root != IntPtr.Zero); _player = player; _root = rig.Root; _lastFrame = now; }
            _lastRootRotation = rig.RootRotation;
            if (!_neutralValid && !attack)
            {
                _neutralTorso = rig.TorsoLocal;
                _neutralUp = rig.TorsoUp;
                _neutralValid = true;
                if (rig.HasHead) { _neutralHead = rig.HeadLocal; _neutralHeadValid = true; }
                float measurement = rig.HasHead ? rig.HeadLocal.Length() : rig.TorsoLocal.Length();
                float typical = rig.HasHead ? 1.2f : 0.65f;
                _rigScale = Math.Clamp(typical / MathF.Max(measurement, 0.35f),
                                       MinModelScale, MaxModelScale);
            }
            if (!attack)
            {
                // Adapt gradually to equipment, idle pose, or a different rig stance.
                float alpha = Math.Clamp(dt * 1.5f, 0f, 1f);
                if (_neutralValid)
                {
                    _neutralTorso = Vector3.Lerp(_neutralTorso, rig.TorsoLocal, alpha);
                    _neutralUp = Vector3.Lerp(_neutralUp, rig.TorsoUp, alpha);
                    if (rig.HasHead)
                    {
                        if (!_neutralHeadValid) { _neutralHead = rig.HeadLocal; _neutralHeadValid = true; }
                        else _neutralHead = Vector3.Lerp(_neutralHead, rig.HeadLocal, alpha);
                    }
                    else _neutralHeadValid = false;
                }
            }
            else if (_neutralValid)
            {
                float strength = AnimationCameraSettings.Strength.V / 100f;
                Vector3 delta = (rig.TorsoLocal - _neutralTorso) * _rigScale * strength;
                float lean = MathF.Max(0f, rig.TorsoUp.Y - _neutralUp.Y);
                target = new Vector3(Math.Clamp(delta.X, -MaxSide, MaxSide),
                    Math.Clamp(delta.Y + 0.08f * lean * strength, -MaxForward * 0.5f, MaxForward),
                    Math.Clamp(delta.Z, -MaxVertical, MaxVertical));
                if (AnimationCameraSettings.Roll.V)
                    targetRoll = Math.Clamp((rig.TorsoUp.X - _neutralUp.X) * strength,
                                            -MaxRoll, MaxRoll);
                // A conservative lunge guard: ONLY when the animated head advances
                // relative to its own model root. No guessed head size or hardcoded race.
                if (AnimationCameraSettings.HeadGuard.V && rig.HasHead && _neutralHeadValid)
                {
                    Vector3 viewForward = Vector3.Transform(-Vector3.UnitZ, cameraRotation);
                    Vector3 headAdvance = Vector3.Transform(rig.HeadLocal - _neutralHead, rig.RootRotation);
                    float towardView = Vector3.Dot(headAdvance, viewForward);
                    if (towardView > 0.015f)
                    {
                        targetPush = Math.Clamp((towardView - 0.015f) * 0.4f, 0f,
                                                AnimationCameraSettings.GuardLimit.V);
                        if (targetPush > 0f) _headGuards++;
                    }
                }
            }
        }
        else _invalidRigs++;

        float alphaMotion = 1f - MathF.Exp(-(attack ? 13f : 8f) * dt);
        _offset = Vector3.Lerp(_offset, target, alphaMotion);
        _roll += (targetRoll - _roll) * alphaMotion;
        _headPush += (targetPush - _headPush) * alphaMotion;
        if (!Finite(_offset) || !float.IsFinite(_roll) || !float.IsFinite(_headPush))
        { Reset(); return; }

        // Apply only to the rendered eye. The creature and its camera dock remain untouched.
        Vector3 look = Vector3.Transform(-Vector3.UnitZ, cameraRotation);
        cameraPosition += Vector3.Transform(_offset, _lastRootRotation) + look * _headPush;
        if (AnimationCameraSettings.Roll.V && MathF.Abs(_roll) > 1e-6f)
            cameraRotation = Quaternion.Normalize(cameraRotation *
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, _roll));
    }

    internal static string Diagnostics() =>
        $"adaptive camera: enabled={AnimationCameraSettings.Enabled.V} strength={AnimationCameraSettings.Strength.V:0}% " +
        $"roll={AnimationCameraSettings.Roll.V} headguard={AnimationCameraSettings.HeadGuard.V} " +
        $"attacks={_attacks} invalidRig={_invalidRigs} modelResets={_modelResets} " +
        $"guardFrames={_headGuards} offset={_offset.Length():0.000}m";
}
