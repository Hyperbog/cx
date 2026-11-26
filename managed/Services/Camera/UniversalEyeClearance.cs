// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/UniversalEyeClearance.cs
// PURPOSE: C3C adaptive self-occlusion clearance
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Validated anatomical head/torso depth drives local player render-only
//   retreat. Avoid a fixed hard cap that lets unarmed attacks bring torso
//   through camera; preserve hands and weapon visibility.
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
/// Fix14D-C3C: unlimited clearance for VALIDATED animated head/torso samples.
/// No class, race, animation-ID or weapon rule; no limb/weapon clipping filter.
/// Only the local model's world-colour render transform moves. Camera, gameplay
/// pose, walls, collision and server position are left unchanged.
/// The joint-envelope test is an approximation, not a vertex-level guarantee.
/// </summary>
internal static class UniversalEyeClearance
{
    private static readonly KnobGroup G;
    private static readonly Knob<bool> Enabled, Trace;
    private static readonly Knob<float> HeadReach, TorsoReach;
    private static ExtenderLog? _log;
    private static long _frames, _missing, _guarded, _rejected, _logged;
    private static long _lastLogTick, _lastValidTick;
    private static int _lastAnimation = int.MinValue;
    private static IntPtr _lastPlayer, _lastRoot, _validPlayer, _validRoot;
    private static Vector3 _lastHeadRelative, _lastTorsoRelative;
    private static float _lastSpan, _lastNeed, _lastExtra, _lastScale;

    static UniversalEyeClearance()
    {
        G = KnobGroup.Declare("fpclear", "Controls", "Camera", order: 522,
            title: "Adaptive first-person core clearance (Fix14D-C3C)");
        Enabled = G.Toggle("enabled", "Keep animated local model behind eye", true,
            tier: Tier.Advanced,
            help: "Validated head/torso clearance, no fixed animation retreat cap. Visual-only.");
        HeadReach = G.Slider("headreach", "Forward head/helmet envelope", 0.10f, 0.40f,
            0.01f, 0.21f, "m", Tier.Advanced,
            help: "Head clearance envelope, scaled to this character's actual rig.");
        TorsoReach = G.Slider("torsoreach", "Forward upper-body envelope", 0.10f, 0.50f,
            0.01f, 0.23f, "m", Tier.Advanced,
            help: "Torso envelope; hands, legs, feet and weapons remain unrestricted.");
        // Keep old user configs parseable. This setting intentionally has NO
        // influence on C3C's animation displacement (unlike C3A/C3B).
        G.Slider("maxextra", "Legacy core limit (unused by C3C)",
            0.05f, 0.70f, 0.01f, 0.60f, "m", Tier.ChatOnly,
            help: "Compatibility only: ignored by C3C's validated core clearance.");
        Trace = G.Toggle("trace", "Bounded adaptive-clearance diagnostics", true,
            tier: Tier.ChatOnly,
            help: "Read-only animated-core and safety-rejection samples in cx.log (max 240).");
    }

    internal static void Configure(ExtenderLog log)
    {
        _log = log;
        KnobSurface.Note(Diagnostics);
        log.Info("Fix14D-C3C: validated adaptive CORE clearance active; no fixed animation cap; all limbs/weapons visible");
    }

    private static bool Finite(in Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(in Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) &&
        float.IsFinite(q.Z) && float.IsFinite(q.W);

    /// <summary>
    /// Require coherent, locally continuous animated-core samples before asking
    /// the renderer to bypass its separate WALL correction cap. These are
    /// size-relative plausibility tests, NOT clamps on the accepted retreat:
    /// a continuously animated valid pose can request any finite distance.
    /// A lost/changed model starts a fresh sample without inheriting old data.
    /// </summary>
    private static bool Plausible(IntPtr player, IntPtr root, long now,
                                  float span, float torsoRoot,
                                  in Vector3 headRelative,
                                  in Vector3 torsoRelative, bool hasHead)
    {
        if (!(span > 0.001f) || !(torsoRoot > 0.001f) ||
            !float.IsFinite(span) || !float.IsFinite(torsoRoot) ||
            !Finite(torsoRelative) || (hasHead && !Finite(headRelative))) return false;
        float proportion = span / torsoRoot;
        if (!float.IsFinite(proportion) || proportion < 0.04f || proportion > 8f)
            return false; // Broken joint mapping, not a normal pose.

        float torsoDist = torsoRelative.Length();
        float headDist = hasHead ? headRelative.Length() : 0f;
        if (!float.IsFinite(torsoDist) || !float.IsFinite(headDist)) return false;
        bool continuing = _validPlayer == player && _validRoot == root &&
                          _lastValidTick != 0 && now >= _lastValidTick &&
                          now - _lastValidTick <= Stopwatch.Frequency / 2;
        if (!continuing)
        {
            // Initial eye proximity is defined by this rig's own dimensions,
            // not a universal metre limit. Never bootstrap from a remote Gob.
            if (torsoDist > 8f * span || (hasHead && headDist > 8f * span))
                return false;
        }
        else
        {
            float dt = MathF.Min(0.5f, (float)(now - _lastValidTick) /
                                          Stopwatch.Frequency);
            float size = MathF.Max(span, _lastSpan);
            float permittedStep = size * (3f + 24f * dt);
            if (!float.IsFinite(permittedStep) ||
                Vector3.Distance(torsoRelative, _lastTorsoRelative) > permittedStep ||
                (hasHead && Vector3.Distance(headRelative, _lastHeadRelative) > permittedStep) ||
                span > _lastSpan * 3f || _lastSpan > span * 3f)
                return false; // Sudden inconsistent rig/teleport: use wall guard only.
        }
        return true;
    }

    /// <summary>
    /// The camera eye has already passed the world wall guard. Return the local
    /// model-only translation and an explicit trust bit for the renderer's
    /// sequenced publication. The basic wall component retains its C2 cap.
    /// </summary>
    internal static Vector3 Apply(IntPtr player, in Vector3 eye,
                                  in Quaternion rotation, in Vector3 wallCorrection,
                                  out bool validatedCore)
    {
        validatedCore = false;
        if (!Enabled.V || player == IntPtr.Zero || !FirstPersonWorldGuard.FirstPersonActive ||
            HeadBoneCamera.Mode.V != 2 || !HeadBoneCamera.EyeLock.V ||
            !Finite(eye) || !Finite(rotation) || !Finite(wallCorrection))
            return wallCorrection;

        _frames++;
        float maxWall = HeadBoneCamera.VisualShiftMax.V;
        float wallLength = wallCorrection.Length();
        if (!float.IsFinite(wallLength)) return Vector3.Zero;
        Vector3 wall = wallLength > maxWall && wallLength > 1e-5f
            ? wallCorrection * (maxWall / wallLength) : wallCorrection;
        if (!PoseService.TryCameraRig(player, out var rig) || rig.Root == IntPtr.Zero ||
            !Finite(rig.TorsoLocal) || !Finite(rig.RootRotation) ||
            (rig.HasHead && (!Finite(rig.HeadWorld) || !Finite(rig.HeadLocal))))
        { _missing++; return wall; }

        Vector3 forward = Vector3.Transform(-Vector3.UnitZ, rotation);
        forward.Z = 0f;
        if (forward.LengthSquared() < 1e-5f)
        {
            forward = Vector3.Transform(Vector3.UnitY, rig.RootRotation);
            forward.Z = 0f;
        }
        if (!Finite(forward) || forward.LengthSquared() < 1e-5f)
        { _missing++; return wall; }
        forward = Vector3.Normalize(forward);

        // The actual appearance establishes the clearance scale. This scales
        // envelopes, but never imposes a displacement ceiling on valid poses.
        float torsoRoot = rig.TorsoLocal.Length();
        float span = rig.HasHead ? Vector3.Distance(rig.HeadLocal, rig.TorsoLocal)
                                 : torsoRoot * 0.65f;
        float scale = span / 0.55f;
        if (!float.IsFinite(scale) || !(scale > 0f))
        { _missing++; return wall; }

        Vector3 torsoWorld;
        if (rig.HasHead)
            torsoWorld = rig.HeadWorld +
                Vector3.Transform(rig.TorsoLocal - rig.HeadLocal, rig.RootRotation);
        else
        {
            PoseRig.World(rig.Root, out Vector3 rootWorld, out _);
            torsoWorld = rootWorld + Vector3.Transform(rig.TorsoLocal, rig.RootRotation);
        }
        if (!Finite(torsoWorld)) { _missing++; return wall; }
        Vector3 relativeHead = rig.HasHead ? rig.HeadWorld - eye : Vector3.Zero;
        Vector3 relativeTorso = torsoWorld - eye;
        long tick = Stopwatch.GetTimestamp();
        if (!Plausible(player, rig.Root, tick, span, torsoRoot,
                       relativeHead, relativeTorso, rig.HasHead))
        {
            _rejected++;
            if (Trace.V && _log != null && _logged < 240 &&
                tick - _lastLogTick > Stopwatch.Frequency)
            {
                _lastLogTick = tick; _logged++;
                _log.Info(FormattableString.Invariant($"Fix14D-C3C: rejectedPose={_rejected} rig=0x{rig.Root.ToInt64():X} span={span:F3} torsoRoot={torsoRoot:F3} headDistance={relativeHead.Length():F3} torsoDistance={relativeTorso.Length():F3}; wall-only fallback"));
            }
            return wall;
        }

        float headNeed = rig.HasHead ? HeadReach.V * scale +
            Vector3.Dot(relativeHead + wall, forward) : float.NegativeInfinity;
        float torsoNeed = TorsoReach.V * scale +
            Vector3.Dot(relativeTorso + wall, forward);
        float need = MathF.Max(0f, MathF.Max(headNeed, torsoNeed));
        Vector3 shifted = wall - forward * need;
        if (!float.IsFinite(need) || !Finite(shifted))
        { _rejected++; return wall; }
        // Commit history ONLY after the full projection and shift validate.
        _validPlayer = player; _validRoot = rig.Root;
        _lastValidTick = tick; _lastSpan = span;
        _lastHeadRelative = relativeHead;
        _lastTorsoRelative = relativeTorso;

        // Full clearance for every validated pose. fpclear.maxextra and
        // fphead.modelbackmax DO NOT cap core retreat. The render pass receives
        // validatedCore in the SAME sequenced publication as the vector.
        validatedCore = need > 0.0001f;
        if (validatedCore) _guarded++;
        _lastNeed = need; _lastExtra = need; _lastScale = scale;

        if (Trace.V && _log != null && _logged < 240)
        {
            int animation = new CNWCObject(player).Animation; // diagnostics only
            bool changed = _lastPlayer != player || _lastRoot != rig.Root ||
                           animation != _lastAnimation;
            if ((changed || need > 0.16f || torsoNeed > 0.05f) &&
                (changed || tick - _lastLogTick > Stopwatch.Frequency / 3))
            {
                _lastLogTick = tick; _lastAnimation = animation;
                _lastPlayer = player; _lastRoot = rig.Root; _logged++;
                _log.Info(FormattableString.Invariant($"Fix14D-C3C: anim={animation} rig=0x{rig.Root.ToInt64():X} scale={scale:F2} mappedHead={(rig.HasHead ? 1 : 0)} headNeed={headNeed:F3} torsoNeed={torsoNeed:F3} requested={need:F3} extra={need:F3} shortfall=0.000 wall={wallLength:F3} trusted={(validatedCore ? 1 : 0)} rejected={_rejected} eye=({eye.X:F2},{eye.Y:F2},{eye.Z:F2})"));
            }
        }
        return shifted;
    }

    internal static string Diagnostics() =>
        $"fpclear: enabled={Enabled.V} frames={_frames} guarded={_guarded} " +
        $"invalidRig={_missing} rejectedPose={_rejected} logs={_logged}/240 " +
        $"lastNeed={_lastNeed:F3} lastExtra={_lastExtra:F3} scale={_lastScale:F2} " +
        "(no animation cap; only validated animated core; limbs/weapons unrestricted)";
}
