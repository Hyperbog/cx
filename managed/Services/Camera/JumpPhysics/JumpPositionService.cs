// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/JumpPhysics/JumpPositionService.cs
// PURPOSE: J4 world-space ballistic jump and landing
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Vertical arc is anchored to launch elevation, not to the changing ground
//   proxy. Guard each native ground sample, end flight when valid terrain
//   intersects the descending arc, and bound unsupported drops. This cannot
//   turn arbitrary placeables into walkable platforms.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Engine;

namespace CX.Services.Camera;

/// <summary>
/// Phase 3: native CREATURE-POSITION experiment. Called exactly once by the
/// existing world-camera solve, which runs even when the player stands still.
/// Does not adjust eye height or rendered model matrices; it changes actual
/// local CNWCCreature.Position and DesiredPos through the existing engine API.
/// This does not grant server authority or guarantee valid walkmesh traversal.
/// </summary>
internal static unsafe class JumpPositionService
{
    private static bool _airborne, _presentationFaultSaid;
    private static IntPtr _creature, _module;
    private static float _floor, _groundZ, _rise, _velocity, _expectedZ, _airTime;
    private static readonly AirMomentumMotor _air = new();
    private static Vector2 _lastGroundXY, _groundVelocity;
    private static bool _haveGroundSample;
    private static int _airSteerFrames, _airBlockedSteps, _walkAtLaunch;
    // J3: previous accepted native ground proxy, not our airborne write.
    // A slope check catches feedback loops that accumulate in small (<0.45) steps.
    private static Vector2 _lastAcceptedWalkXY;
    private static float _lastAcceptedWalkZ, _nativeWalkStepXY, _nativeWalkStepZ, _nativeWalkStepLimit;
    private static bool _haveAcceptedWalk;
    private static int _slopeGuardAborts;

    private static int _jumps, _lands, _corrections, _aborts, _correctionStreak, _walkReapplies;

    // J1: observer only. Never feeds a sample back into the motor or native writes.
    private static ExtenderLog? _trace;
    private static long _launchAt, _nextWalkSample;
    private static float _nativeWalkZ;
    internal static void ConfigureTrace(ExtenderLog log)
    {
        _trace = log;
        JumpTimingTrace.Configure(log);
    }

    private static void Trace(string reason, Vector3 position)
    {
        JumpTimingTrace.Mark(reason, position, new Vector3(_floor, _groundZ, _expectedZ),
            new Vector3(_rise, _velocity, _airTime));
        if (_trace == null) return;
        double sinceMs = _launchAt == 0 ? 0d
            : (Stopwatch.GetTimestamp() - _launchAt) * 1000d / Stopwatch.Frequency;
        _trace.Info($"JumpSlope-J4: event={reason} t_ms={sinceMs:F1} " +
            $"pos={position.X:F3},{position.Y:F3},{position.Z:F3} " +
            $"takeoff_z={_floor:F3} expected_z={_expectedZ:F3} " +
            $"walk_native_z={_nativeWalkZ:F3} ground_z={_groundZ:F3} " +
            $"native_step_xy={_nativeWalkStepXY:F3} native_step_z={_nativeWalkStepZ:F3} " +
            $"slope_limit={_nativeWalkStepLimit:F3} slope_aborts={_slopeGuardAborts} " +
            $"rise={_rise:F3} flight_z={_floor + _rise:F3} " +
            $"ground_gap={_floor + _rise - _groundZ:F3} air_t={_airTime:F3} " +
            $"vz={_velocity:F3} " +
            $"corrections={_corrections} reapplies={_walkReapplies} aborts={_aborts}");
    }

    // Physically integrated ballistic arc (units in NWN meters/seconds).
    private const float Impulse = 5f;
    private const float Gravity = 13.5f;
    private const float MaximumRise = 1.15f;
    // The engine's native WalkUpdateLocation normally projects back to walkable
    // ground before JumpWalkBridge restores jump height. Refuse a sudden large
    // ground step instead of treating it as a valid platform or teleport.
    private const float MaxGroundStepPerWalk = 0.45f;
    // J3: a terrain sample must move plausibly relative to horizontal walking.
    // Normal slope data in J2 (~0.5 vertical per XY) fits within this bound.
    // 0.075 units allows projection jitter and stair discretization; it is not
    // a general support-surface test and intentionally rejects abrupt ledges.
    private const float MaxGroundSlope = 1.5f;
    private const float GroundStepAllowance = 0.075f;
    // Safety boundary for unsupported ledges: never drive local airborne Z
    // indefinitely when the native walkmesh provides no reachable landing.
    // Crossing this bound yields movement authority back to the engine; it
    // does not claim an arbitrary midair landing or write an invented floor.
    private const float MaxWorldFlightSeconds = 1.35f;
    private const float MaxWorldDrop = 3.5f;
    // Tiny tolerance for projection jitter at takeoff and contact.
    private const float GroundContactSlop = 0.025f;
    private const int MaxCorrectionFrames = 4;

    // Read-only native jump state for visual camera effects. No extra stepping,
    // velocity edits or physics ownership changes; does not consume jump input.
    internal static bool IsAirborne => _airborne;
    internal static bool AirborneFor(IntPtr player) =>
        _airborne && player != IntPtr.Zero && player == _creature;

    internal static void Tick(float dt, bool worldCameraActive)
    {
        JumpTimingTrace.Pump();
        try { Step(dt, worldCameraActive); }
        catch (Exception)
        {
            // Native engine and module state may disappear during an area load;
            // never unwind a managed exception across the camera's native hook.
            _airborne = false;
            JumpPreview.Reset();
            _aborts++;
        }
    }

    private static void Step(float dt, bool worldCameraActive)
    {
        JumpTimingTrace.Mark("raw_dt", x: dt, y: _airborne ? 1 : 0, z: _airTime);
        IntPtr module = CameraService.Module();
        IntPtr player = CameraService.PlayerCreature();
        if (_airborne && (_module != module || _creature != player))
        {
            // Do not restore the old area's elevation into a new area.
            _airborne = false;
            JumpPreview.Reset();
        }
        bool allowed = worldCameraActive && module != IntPtr.Zero && player != IntPtr.Zero
            && CameraModes.Driving.JumpPreview.V && CameraModes.Driving.FirstPerson.V
            && CameraService.Mode == 2 && MouseLookService.ActionHeld
            && !FreeCameraService.Active;
        if (!allowed)
        {
            JumpTimingTrace.Mark("gate_closed");
            if (_airborne && player == _creature && module == _module)
                Land(player, restore: true);
            JumpPreview.Reset();
            return;
        }
        // Do not lose ordinary elapsed time or snap an airborne actor to the
        // floor merely because a frame was slow. Long suspensions freeze the
        // flight clock until a valid update; retain the queued input edge.
        if (!JumpBallistics.AcceptFrame(dt))
        {
            JumpTimingTrace.Mark("invalid_dt", x: dt);
            if (!_airborne)
            {
                _haveGroundSample = false;
                _groundVelocity = Vector2.Zero;
            }
            return;
        }
        Vector3 current = new CNWCObject(player).Position;
        if (!Finite(current)) { _airborne = false; JumpPreview.Reset(); return; }
        bool press = JumpPreview.Take(out float inputAge);
        // The frame interval can include time BEFORE the Space press. Start
        // the new flight only over the portion after the recorded input.
        if (!_airborne && press) dt = JumpBallistics.LaunchElapsed(dt, inputAge);
        JumpTimingTrace.Mark("step", current, new Vector3(_rise, _velocity, _airTime),
            new Vector3(_floor, _groundZ, _expectedZ), x: dt, y: press ? 1 : 0, z: _airborne ? 1 : 0);
        if (!_airborne && !press)
        {
            TrackGroundSpeed(current, dt);
            return;
        }
        if (!_airborne)
        {
            _floor = current.Z;
            _groundZ = current.Z;
            _rise = 0f;
            _airTime = 0f;
            _expectedZ = current.Z;
            _velocity = Impulse;
            Vector2 carry = _groundVelocity;
            if (carry.LengthSquared() < 0.09f && CameraDriveService.AirWish.LengthSquared() > 0.1f)
                carry = CameraDriveService.AirWish * 2.6f;
            _air.Launch(new Vector2(current.X, current.Y), carry);
            _haveGroundSample = false;
            _walkAtLaunch = _walkReapplies;
            _correctionStreak = 0;
            _airborne = true;
            _creature = player; _module = module;
            _jumps++;
            _launchAt = Stopwatch.GetTimestamp();
            _nextWalkSample = 0;
            _nativeWalkZ = current.Z;
            _lastAcceptedWalkXY = new Vector2(current.X, current.Y);
            _lastAcceptedWalkZ = current.Z;
            _nativeWalkStepXY = _nativeWalkStepZ = 0f;
            _nativeWalkStepLimit = GroundStepAllowance;
            _haveAcceptedWalk = true;
            Trace("launch", current);
        }
        else if (MathF.Abs(current.Z - _expectedZ) > 0.12f)
        {
            _corrections++;
            Trace("camera_z_correction", current);
            // A walk update can temporarily flatten Z. The Fix6 post-walk
            // bridge has already demonstrated successful reapplication during
            // this jump; do not mistake that for a hard authority correction.
            if (_walkReapplies > _walkAtLaunch) _correctionStreak = 0;
            else if (++_correctionStreak >= MaxCorrectionFrames)
            {
                _aborts++;
                Trace("camera_authority_abort", current);
                _airborne = false;
                return;
            }
        }
        else _correctionStreak = 0;

        // Ballistic height is anchored to TAKEOFF Z, not the rising/falling
        // ground sample. A downhill jump may continue past the takeoff height.
        if (dt <= 0f) return;
        _airTime += dt;
        float next = JumpBallistics.Height(_airTime, Impulse, Gravity);
        _velocity = JumpBallistics.Velocity(_airTime, Impulse, Gravity);
        if (_airTime > MaxWorldFlightSeconds || next < -MaxWorldDrop)
        {
            // No verified nearby ground contact: stop the local jump edits
            // and let native movement resolve the unsupported drop.
            _aborts++;
            Trace("world_fall_guard_abort", current);
            _airborne = false;
            JumpPreview.Reset();
            return;
        }
        _rise = MathF.Min(next, MaximumRise); // Intentionally allows negative rise.
        float flightZ = _floor + _rise;
        if (flightZ < _groundZ - GroundContactSlop)
        {
            // Validated native walk elevation is the contact surface.
            Trace("world_contact_tick", current);
            Land(player, restore: false);
            return;
        }
        // AirMomentumMotor has its own 40 ms ceiling. Divide the entire
        // interval into bounded collision-tested segments instead of silently
        // losing horizontal time. Keep one native position write per tick.
        Vector2 wish = CameraDriveService.AirWish;
        if (wish.LengthSquared() > 0.01f) _airSteerFrames++;
        int slices = JumpBallistics.HorizontalSlices(dt);
        float subDt = dt / slices;
        for (int i = 0; i < slices; i++)
        {
            Vector2 previous = _air.Position;
            Vector2 proposed = _air.Step(subDt, wish);
            Vector2 accepted = ConstrainHorizontal(player, module, previous, proposed);
            if (Vector2.DistanceSquared(proposed, accepted) > 1e-6f) _airBlockedSteps++;
            _air.Resolve(accepted);
        }
        Vector2 end = _air.Position;
        WriteCreaturePosition(player, new Vector3(end.X, end.Y, flightZ));
    }

    // Sample NWN's real takeoff motion. Anomalous/teleport-sized deltas are
    // deliberately discarded, rather than carried into a launch.
    private static void TrackGroundSpeed(in Vector3 current, float dt)
    {
        Vector2 xy = new(current.X, current.Y);
        if (_haveGroundSample && dt > 0.001f)
        {
            Vector2 sample = (xy - _lastGroundXY) / dt;
            _groundVelocity = sample.LengthSquared() < 36f ? sample : Vector2.Zero;
        }
        else _groundVelocity = Vector2.Zero;
        _lastGroundXY = xy;
        _haveGroundSample = true;
    }

    // Native CNWCArea path clearance, the same API used by StepGuardService.
    // Keep the native door-aware collision path, but anchor each short segment
    // at the latest verified native walk elevation rather than takeoff Z.
    // An absent symbol or missing area FAILS CLOSED, never ghosts through walls.
    private static bool GroundSegmentClear(IntPtr creature, IntPtr module, Vector2 from, Vector2 to)
    {
        if (Vector2.DistanceSquared(from, to) < 1e-10f) return true;
        CNWCArea area = new CNWCModule(module).CurrentArea;
        CPathfindInformation pathfind = new CNWCCreature(creature).Pathfind;
        TestDirectLineFn test = EngineFn.TestDirectLine;
        if (area.IsNull || pathfind.IsNull || test == null) return false;
        Span<float> space = pathfind.SpaceArgs;
        Vec3 a = new() { X = from.X, Y = from.Y, Z = _groundZ };
        Vec3 b = new() { X = to.X, Y = to.Y, Z = _groundZ };
        return test(area.Ptr, creature, VectorArg.Of(&a), VectorArg.Of(&b),
                    space[0], space[1], 0) == 1;
    }

    // Slide along a wall by retrying one horizontal axis at a time.
    private static Vector2 ConstrainHorizontal(IntPtr creature, IntPtr module, Vector2 from, Vector2 target)
    {
        if (GroundSegmentClear(creature, module, from, target)) return target;
        Vector2 x = new(target.X, from.Y);
        if (GroundSegmentClear(creature, module, from, x)) from = x;
        Vector2 y = new(from.X, target.Y);
        if (GroundSegmentClear(creature, module, from, y)) from = y;
        return from;
    }

    private static void WriteCreaturePosition(IntPtr player, Vector3 target)
    {
        // This API is already used by StepGuardService to restore the player's
        // model after rejecting a movement step. Both the native creature and
        // its desired movement point are updated in this local experiment.
        var c = new CNWCCreature(player);
        Vec3 n = new() { X = target.X, Y = target.Y, Z = target.Z };
        if (JumpTimingTrace.Active) ObservePosition("write_before", player, target);
        c.SetPosition(VectorArg.Of(&n));
        ref Vector3 desired = ref c.DesiredPos;
        desired = target;
        _expectedZ = target.Z;
        try { JumpPresentationSync.AfterWrite(player, target); }
        catch (Exception ex)
        {
            // Presentation must never cancel physical flight on a managed fault.
            if (!_presentationFaultSaid)
            {
                _presentationFaultSaid = true;
                _trace?.Always("Jump presentation sync failed: " + ex.Message);
            }
            JumpTimingTrace.Mark("sync_fault");
        }
        if (JumpTimingTrace.Active) ObservePosition("write_after", player, target);
    }

    private static void ObservePosition(string kind, IntPtr player, Vector3 target)
    {
        try { JumpTimingTrace.Mark(kind, new CNWCObject(player).Position, target,
            new Vector3(_rise, _velocity, _airTime)); }
        catch (Exception) { /* Observations never alter the motor. */ }
    }

    internal static void BeforeWalk(IntPtr creature)
    {
        if (!JumpTimingTrace.Active || !AirborneFor(creature) || CameraService.Module() != _module) return;
        ObservePosition("walk_before", creature, new Vector3(_air.Position.X, _air.Position.Y, _expectedZ));
    }

    private static void Land(IntPtr player, bool restore)
    {
        Vector3 current = new CNWCObject(player).Position;
        Trace(restore ? "restore_to_ground" : "land_on_ground", current);
        if (Finite(current) && (!restore || MathF.Abs(current.Z - _expectedZ) <= 0.25f))
            WriteCreaturePosition(player, new Vector3(current.X, current.Y, _groundZ));
        _rise = 0f;
        _velocity = 0f;
        _airborne = false;
        if (!restore) _lands++;
    }

    private static bool Finite(Vector3 p)
        => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    // Run after the engine's native walking step. Preserve its actual X/Y and
    // restore our current airborne height if the walkmesh flattened Z.
    // This method does not integrate gravity or consume input.
    internal static void AfterWalk(IntPtr creature)
    {
        if (!_airborne || creature == IntPtr.Zero || creature != _creature ||
            CameraService.Module() != _module) return;
        Vector3 actual = new CNWCObject(creature).Position;
        if (!Finite(actual)) return;
        JumpTimingTrace.Mark("walk_native", actual, new Vector3(_air.Position.X, _air.Position.Y, _expectedZ),
            new Vector3(_floor, _groundZ, _airTime));
        _nativeWalkZ = actual.Z;
        Vector2 nativeXY = new(actual.X, actual.Y);
        _nativeWalkStepXY = _haveAcceptedWalk
            ? Vector2.Distance(nativeXY, _lastAcceptedWalkXY) : 0f;
        _nativeWalkStepZ = _haveAcceptedWalk ? actual.Z - _lastAcceptedWalkZ : 0f;
        _nativeWalkStepLimit = GroundStepAllowance + MaxGroundSlope * _nativeWalkStepXY;
        long sampledAt = Stopwatch.GetTimestamp();
        if (sampledAt >= _nextWalkSample)
        {
            _nextWalkSample = sampledAt + Math.Max(1L, Stopwatch.Frequency / 12);
            Trace("native_walk_before_bridge", actual);
        }
        // Fail before accepting the suspect Z or writing another airborne
        // position: repeated small vertical steps otherwise evade J2's
        // absolute 0.45-per-walk discontinuity check.
        if (_haveAcceptedWalk && MathF.Abs(_nativeWalkStepZ) > _nativeWalkStepLimit)
        {
            Trace("native_ground_slope_abort", actual);
            _slopeGuardAborts++;
            _airborne = false;
            JumpPreview.Reset();
            _aborts++;
            return;
        }
        Vector2 planarError = new(actual.X - _air.Position.X, actual.Y - _air.Position.Y);
        // A large XY correction or an abrupt change in projected walking Z
        // might be a teleport, server correction, fall, or invalid terrain.
        // Stand down rather than teleporting/climbing onto unverified surfaces.
        if (planarError.LengthSquared() > 16f ||
            MathF.Abs(actual.Z - _groundZ) > MaxGroundStepPerWalk)
        {
            Trace("native_ground_discontinuity_abort", actual);
            _airborne = false;
            JumpPreview.Reset();
            _aborts++;
            return;
        }
        // Native walking supplies the validated contact height. It NEVER
        // modifies the airborne ballistic arc. On uphill terrain, land when
        // the native ground overtakes the world-space flight; on downhill
        // terrain, remain airborne until the falling arc intersects ground.
        _lastAcceptedWalkXY = nativeXY;
        _lastAcceptedWalkZ = actual.Z;
        _haveAcceptedWalk = true;
        _groundZ = actual.Z;
        _expectedZ = _floor + _rise;
        if (_expectedZ < _groundZ - GroundContactSlop)
        {
            Trace("world_contact_native", actual);
            Land(creature, restore: false);
            return;
        }
        Vector3 desiredAir = new(_air.Position.X, _air.Position.Y, _expectedZ);
        if (Vector3.DistanceSquared(actual, desiredAir) < 0.015f * 0.015f) return;
        WriteCreaturePosition(creature, desiredAir);
        _walkReapplies++;
    }

    // If the host repeatedly overwrites Z, this is a client-authority failure,
    // not something that can be fixed by moving the model or camera again.
    internal static string Diagnostics()
        => $"native jump: jumps={_jumps} landed={_lands} corrected={_corrections} "
         + $"rejected={_aborts} rise={_rise:0.000} walkReapplies={_walkReapplies} airInput={_airSteerFrames} blocked={_airBlockedSteps}";
}
