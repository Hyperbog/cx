// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/FirstPersonWorldGuard.cs
// PURPOSE: Camera vs. world clearance
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Ray/near-world checks constrain the camera at wall contact. These are
//   sampled visibility checks, not a full swept sphere or multiplayer
//   collision simulation.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Services.Camera;

/// <summary>
/// Fix12: First-person-only visual eye collision with world geometry, and a
/// separate keyhole-off policy. Does not move the creature or alter physics.
/// Fix14D-C1: thirteen ray samples and contact-release damping (camera only).
/// Uses existing SceneRay tests, not a full capsule sweep or near-plane hull.
/// </summary>
internal static class FirstPersonWorldGuard
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, KeyholeOff;
    internal static readonly Knob<float> Near, Margin, Fan;
    private static long _frames, _casts, _blocked, _noScene, _ignored, _noHit, _badPose;
    private static float _lastFraction = 1f;
    private static long _contactTicks, _probeSamples, _releaseFrames, _wallContacts;
    private static Vector3 _contactFeet, _contactForward;
    private static float _heldFraction = 1f;
    private static bool _haveContact;
    // Camera-only collision displacement, produced at the end of the camera solve.
    // Fix 14D-C2 can consume it for the local player's colour-pass visual pose,
    // but C1 deliberately does NOT write any world/model, physics or animation state.
    internal static Vector3 LastEyeCorrection { get; private set; }
    internal static bool ContactConstrained { get; private set; }
    private const float ReleasePerSecond = 6f;
    private const float MaxStableMoveSq = 0.25f * 0.25f;
    private const float SameHeadingDot = 0.90f;

    private static void ForgetContact()
    {
        LastEyeCorrection = Vector3.Zero;
        ContactConstrained = false;
        _lastFraction = _heldFraction = 1f;
        _haveContact = false;
        _contactTicks = 0;
    }

    // Approaching an obstruction clamps INSTANTLY. Only a newly opening path
    // eases out, avoiding the stop/release camera snap. A new scene/teleport or
    // major turn cannot reuse the previous camera constraint.
    private static float ContactFraction(float raw, Vector3 feet, Vector3 forward)
    {
        long tick = Stopwatch.GetTimestamp();
        bool same = _haveContact && Vector3.DistanceSquared(feet, _contactFeet) <= MaxStableMoveSq
            && Vector3.Dot(forward, _contactForward) >= SameHeadingDot;
        float dt = same && _contactTicks > 0
            ? Math.Clamp((tick - _contactTicks) / (float)Stopwatch.Frequency, 0f, 0.1f) : 0f;
        if (!same || raw <= _heldFraction) _heldFraction = raw;
        else
        {
            _heldFraction = MathF.Min(raw, _heldFraction + ReleasePerSecond * dt);
            _releaseFrames++;
        }
        _contactTicks = tick;
        _contactFeet = feet;
        _contactForward = forward;
        _haveContact = true;
        return _heldFraction;
    }

    // Thirteen perimeter samples. Unlike a full sphere sweep this can miss
    // sub-sample gaps; its shape and every sample are bounded and allocation-free.
    private static Vector3 RayOffset(int sample, Vector3 right, Vector3 up, float radius)
    {
        const float Diag = 0.7071067811865475f;
        return sample switch
        {
            1 => right * radius,
            2 => -right * radius,
            3 => up * radius,
            4 => -up * radius,
            5 => (right + up) * (radius * Diag),
            6 => (right - up) * (radius * Diag),
            7 => (-right + up) * (radius * Diag),
            8 => (-right - up) * (radius * Diag),
            9 => right * (radius * 0.5f),
            10 => -right * (radius * 0.5f),
            11 => up * (radius * 0.5f),
            12 => -up * (radius * 0.5f),
            _ => Vector3.Zero
        };
    }

    static FirstPersonWorldGuard()
    {
        G = KnobGroup.Declare("fpsurface", "Controls", "Camera", order: 519,
                              title: "First-person surface guard");
        Enabled = G.Toggle("enabled", "Keep first-person eye out of walls", true,
            help: "Restricts the visual eye against world geometry after attack camera tracking; no character movement changes.");
        KeyholeOff = G.Toggle("keyholeoff", "Disable keyhole in first-person", true,
            help: "Closes NWN's roof/wall keyhole only for the active first-person world camera in Alloy mode.");
        Near = G.Slider("near", "Forward clearance", 0.05f, 0.35f, 0.01f, 0.16f, "m", Tier.Advanced,
            help: "Extra space reserved ahead of the visual eye to reduce near-plane wall penetration.");
        Margin = G.Slider("margin", "Wall clearance", 0.01f, 0.20f, 0.01f, 0.06f, "m", Tier.Advanced,
            help: "Keep the collision eye this far before an obstacle intersection.");
        Fan = G.Slider("fan", "Collision ray half-width", 0f, 0.18f, 0.01f, 0.08f, "m", Tier.Advanced,
            help: "13 rays approximate eye clearance; not a real swept sphere or complete near-plane hull.");
    }

    // Same first-person predicate as Fix10's scoped color-only visibility mode.
    internal static bool FirstPersonActive =>
        CameraService.Mode == 2 && !CameraService.GateUp && !FreeCameraService.Active
        && CameraModes.Driving.FirstPerson.V;

    internal static bool SuppressKeyhole => KeyholeOff.V && FirstPersonActive;

    private static bool Finite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool OwnedAttachment(IntPtr gob, IntPtr playerGob)
    {
        if (gob == IntPtr.Zero || playerGob == IntPtr.Zero) return false;
        DbId player = Facade.ObjectId(playerGob);
        DbId current = Facade.ObjectId(gob);
        if (!SceneDb.Objects.Valid(player) || !SceneDb.Objects.Valid(current)) return gob == playerGob;
        for (int n = 0; n < 12 && SceneDb.Objects.Valid(current); n++)
        {
            if (current.Value == player.Value) return true;
            current = SceneDb.ObjAttachTo[current.Index];
        }
        return false;
    }

    /// <summary>After Fix9/Fix11 animation motion, before c.Place(eye,q).
    /// The stable creature centre anchors the sweep; the animated eye never
    /// changes player coordinates. Static surfaces and dynamic placeables block,
    /// while character gobs (and own attachments) are passed through.</summary>
    internal static void Resolve(IntPtr player, ref Vector3 eye, Quaternion rotation)
    {
        LastEyeCorrection = Vector3.Zero;
        ContactConstrained = false;
        if (!Enabled.V || !FirstPersonActive || player == IntPtr.Zero) { ForgetContact(); return; }
        if (!Finite(eye) || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.Y)
            || !float.IsFinite(rotation.Z) || !float.IsFinite(rotation.W))
        { _badPose++; ForgetContact(); return; }
        IntPtr scene = SceneRay.WorldScene();
        if (!SceneRay.Armed || scene == IntPtr.Zero) { _noScene++; ForgetContact(); return; }

        Vector3 feet = new CNWCObject(player).Position;
        if (!Finite(feet)) { _badPose++; ForgetContact(); return; }
        Vector3 fromBase = new(feet.X, feet.Y, eye.Z);
        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        if (!Finite(forward) || forward.LengthSquared() < 0.5f) { _badPose++; ForgetContact(); return; }
        forward = Vector3.Normalize(forward);
        Vector3 toBase = eye + forward * Near.V;
        Vector3 span = toBase - fromBase;
        float length = span.Length();
        if (length < 0.01f || length > 10f) { _badPose++; ForgetContact(); return; }
        Vector3 dir = span / length;
        Vector3 right = Vector3.Cross(forward, Vector3.UnitZ);
        right = right.LengthSquared() < 0.001f ? Vector3.UnitX : Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(right, forward);
        float radius = Fan.V;
        IntPtr playerGob = PoseService.GobOf(player);
        float fraction = 1f;
        _frames++;
        try
        {
            for (int r = 0; r < 13; r++)
            {
                Vector3 offset = RayOffset(r, right, up, radius);
                _probeSamples++;
                Vector3 from = fromBase + offset;
                Vector3 to = toBase + offset;
                // Bounded retry, because a creature in front of the eye should
                // not be treated as a solid wall. No unbounded walk through hits.
                for (int hop = 0; hop < 3; hop++)
                {
                    _casts++;
                    if (!SceneRay.Cast(scene, from, to, playerGob, out Vector3 hit,
                                       out IntPtr gob, statics: true)) { _noHit++; break; }
                    if (!Finite(hit)) { _badPose++; break; }
                    if (SceneRay.IsCreature(gob) || OwnedAttachment(gob, playerGob))
                    {
                        _ignored++;
                        from = hit + dir * SceneRay.Nudge;
                        if (Vector3.Dot(to - from, dir) <= 0f) break;
                        continue;
                    }
                    float distance = Vector3.Dot(hit - (fromBase + offset), dir);
                    fraction = MathF.Min(fraction, Math.Clamp((distance - Margin.V) / length, 0f, 1f));
                    break;
                }
            }
        }
        finally { SceneRay.Release(); }
        // No previous camera constraint is inherited after a large position or
        // direction change. The raw collision fraction always overrides a held
        // fraction when a new wall is closer; we NEVER ease into a collision.
        fraction = ContactFraction(fraction, feet, forward);
        _lastFraction = fraction;
        if (fraction >= 0.999f) return;
        // s=1: unmodified eye. s=0: retreat to behind the creature centre.
        // This is the final eye constraint. The rig cannot independently advance
        // after this solve; Fix14D-C2 will handle the accompanying visual pose.
        Vector3 wantedEye = eye;
        eye = Vector3.Lerp(fromBase - forward * Near.V, eye, fraction);
        LastEyeCorrection = eye - wantedEye;
        ContactConstrained = true;
        _blocked++;
        _wallContacts++;
    }

    internal static string Diagnostics() =>
        $"fpsurface: firstPerson={FirstPersonActive} keyholeOff={SuppressKeyhole} " +
        $"eyeGuard={Enabled.V} frames={_frames} rays={_casts} samples={_probeSamples} blocked={_blocked} " +
        $"ignoredActor={_ignored} noHit={_noHit} noScene={_noScene} " +
        $"badPose={_badPose} lastFraction={_lastFraction:0.000} " +
        $"contact={(ContactConstrained ? 1 : 0)} backoff={LastEyeCorrection.Length():0.000}m " +
        $"releaseFrames={_releaseFrames} contacts={_wallContacts} " +
        "(13-ray bounded hull approximation; world guard last; no visual-pose writes yet)";
}
