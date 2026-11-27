using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>Keep the displayed actor-relative eye continuous through flight and rig reacquisition.
/// Carry in yaw axes (never pitch/roll); the current actor position and mouse orientation stay authoritative.</summary>
internal struct JumpEyeContinuity
{
    private const long LandingBlendMs = 160, MaxGapMs = 120, RigWaitMs = 250;
    private const float MaxActorStepSquared = 2.5f * 2.5f;
    private (IntPtr Camera, IntPtr Behavior, IntPtr Player, IntPtr Area) _context;
    private long _lastSample, _landedAt, _blendAt;
    private bool _airborne, _recovering, _blending, _blendTracked;
    private Vector3 _lastActor, _lastLocal, _blendFrom;

    internal bool Recovering => _recovering;

    internal Vector3 Apply(IntPtr camera, IntPtr behavior, IntPtr player, IntPtr area,
                           bool airborne, bool tracked, Vector3 actor, Vector3 eye, float yaw, long now)
    {
        if (camera == IntPtr.Zero || behavior == IntPtr.Zero || player == IntPtr.Zero || area == IntPtr.Zero ||
            !Finite(actor) || !Finite(eye) || !float.IsFinite(yaw))
        {
            this = default;
            return eye;
        }
        var context = (camera, behavior, player, area);
        bool continuous = _context == context && now - _lastSample is >= 0 and <= MaxGapMs &&
            Vector3.DistanceSquared(_lastActor, actor) <= MaxActorStepSquared;
        if (!continuous) this = default;

        Quaternion heading = CameraService.Pose(yaw, 0f);
        Vector3 local = Vector3.Transform(eye - actor, Quaternion.Conjugate(heading));
        if (!Finite(local)) { this = default; return eye; }
        bool adjusted = false;
        if (airborne)
        {
            // Carry the last visible offset, not a second jump trajectory. Turn with mouse yaw,
            // including vertical look and yaw wrap; do not inherit pitch or animated roll.
            if (continuous) { local = _lastLocal; adjusted = true; }
            _recovering = _blending = false;
        }
        else
        {
            if (_airborne)
            {
                _recovering = true;
                _landedAt = now;
                _blending = false;
            }
            if (_recovering)
            {
                if (!tracked && now - _landedAt < RigWaitMs)
                {
                    // A failed rig solve is not a valid landing target. Hold briefly.
                    local = _lastLocal;
                    adjusted = true;
                    _blending = false;
                }
                else
                {
                    // After a short wait, ease toward the calibrated fallback if necessary.
                    // A newly valid rig (or tracking loss) starts from the last displayed eye.
                    if (!_blending || _blendTracked != tracked)
                    {
                        _blendAt = now;
                        _blendFrom = _lastLocal;
                        _blendTracked = tracked;
                        _blending = true;
                    }
                    float t = Math.Clamp((now - _blendAt) / (float)LandingBlendMs, 0f, 1f);
                    if (tracked && t >= 1f)
                    {
                        _recovering = _blending = false;
                    }
                    else
                    {
                        float weight = t * t * (3f - 2f * t);
                        local = Vector3.Lerp(_blendFrom, local, weight);
                        adjusted = true;
                    }
                }
            }
        }

        Vector3 result = adjusted ? actor + Vector3.Transform(local, heading) : eye;
        if (!Finite(result)) { this = default; return eye; }
        _context = context;
        _lastActor = actor;
        _lastLocal = local;
        _lastSample = now;
        _airborne = airborne;
        return result;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    internal static void SelfCheck(Checker check)
    {
        static bool Near(Vector3 a, Vector3 b) => Vector3.DistanceSquared(a, b) < 1e-8f;
        var s = new JumpEyeContinuity();
        IntPtr cam = (IntPtr)1, behavior = (IntPtr)2, player = (IntPtr)3, area = (IntPtr)4;
        Vector3 root = Vector3.Zero, ground = new(0.16f, 0.1f, 1.76f), fallback = new(0f, 0f, 1.65f);
        Vector3 rise = new(0.1f, 0f, 0.5f), rig = new(-0.12f, 0.1f, 1.56f);
        Vector3 Step(bool air, bool tracked, Vector3 actor, Vector3 eye, float yaw, long now) =>
            s.Apply(cam, behavior, player, area, air, tracked, actor, eye, yaw, now);
        check.Expect(Near(Step(false, true, root, ground, 0, 0), ground) &&
            Near(Step(true, false, rise, rise + fallback, 0, 16), rise + ground),
            "jump takeoff preserves the visible eye relative to native actor travel");
        check.Expect(Near(Step(true, false, rise, rise + fallback, 90, 32),
                rise + new Vector3(-ground.Y, ground.X, ground.Z)) &&
            Near(Step(true, false, rise, rise + fallback, 180, 48),
                rise + new Vector3(-ground.X, -ground.Y, ground.Z)),
            "airborne yaw turns the eye offset, without pitch or roll");
        check.Expect(Near(Step(false, false, root, fallback, 0, 64), ground) && s.Recovering &&
            Near(Step(false, false, root, fallback, 0, 96), ground) &&
            Near(Step(false, true, root, rig, 0, 112), ground),
            "landing waits for a valid rig and begins from the displayed offset");
        check.Expect(Near(Step(false, true, root, rig, 0, 192), (ground + rig) * 0.5f) &&
            Near(Step(false, true, root, rig, 0, 272), rig) && !s.Recovering,
            "landing returns to the live rig in 160 ms after reacquisition");
        // Long rig loss: use fallback after a bounded hold, but reacquire smoothly.
        s = default;
        Step(false, true, root, ground, 0, 0);
        Step(true, false, rise, rise + fallback, 0, 16);
        Step(false, false, root, fallback, 0, 32);
        Step(false, false, root, fallback, 0, 132);
        Step(false, false, root, fallback, 0, 232);
        check.Expect(Near(Step(false, false, root, fallback, 0, 282), ground) &&
            Near(Step(false, false, root, fallback, 0, 362), (ground + fallback) * 0.5f) &&
            Near(Step(false, false, root, fallback, 0, 442), fallback) && s.Recovering,
            "prolonged rig loss blends to the fallback rather than retaining a stale offset");
        check.Expect(Near(Step(false, true, root, rig, 0, 458), fallback) &&
            Near(Step(false, true, root, rig, 0, 538), (fallback + rig) * 0.5f) &&
            Near(Step(false, true, root, rig, 0, 618), rig) && !s.Recovering,
            "later rig reacquisition has its own continuous blend");
        // Mid-recovery loss and re-jump use the most recent displayed offset.
        Step(true, false, rise, rise + fallback, 0, 634);
        Step(false, true, root, ground, 0, 650);
        Vector3 halfway = Step(false, true, root, ground, 0, 730);
        check.Expect(Near(Step(false, false, root, fallback, 0, 746), halfway) &&
            Near(Step(true, false, rise, rise + fallback, 0, 762), rise + halfway),
            "rig loss and a repeated jump do not inherit a stale landing target");
        check.Expect(Near(s.Apply((IntPtr)9, behavior, player, area, true, false, rise, rise + fallback, 0, 778), rise + fallback) &&
            !s.Recovering, "changed world camera discards carry");
        check.Expect(Near(Step(false, true, root, rig, 0, 1000), rig) &&
            Near(Step(true, false, rise, rise + fallback, 0, 1200), rise + fallback),
            "a gap discards carry");
        check.Expect(Near(s.Apply(cam, behavior, player, (IntPtr)9, true, false, rise, rise + fallback, 0, 1216), rise + fallback),
            "changed area discards carry");
        s = default;
        Step(false, true, root, ground, 0, 0);
        Vector3 moved = new(3f, 0f, 0f);
        check.Expect(Near(Step(true, false, moved, moved + fallback, 0, 16), moved + fallback),
            "teleport discards carry");
        check.Expect(Near(Step(true, false, moved, moved + fallback, float.NaN, 32), moved + fallback) && !s.Recovering,
            "invalid heading discards carry");
    }
}
