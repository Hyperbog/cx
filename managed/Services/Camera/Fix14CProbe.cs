// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/Fix14CProbe.cs
// PURPOSE: Earlier movement/animation diagnostic
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Bounded read-only observer for stalled motion and gait state. Its
//   historical log signals do not by themselves prove the network or
//   animation is defective.
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

namespace CX.Services.Camera;

/// <summary>
/// Fix 14C: bounded, read-only diagnostic observation of the existing client drive/walk hooks.
/// Does not add hooks, mutate engine state, send messages, move models or alter camera/animation.
/// Samples at most four walk events per second; refusals and head identity changes are noted separately.
/// </summary>
internal static class Fix14CProbe
{
    private static ExtenderLog? _log;
    private static long _lastStep, _lastGuard;
    private static bool _held;
    private static int _walk, _fwd, _back, _left, _right, _strafeL, _strafeR;
    private static int _steps, _samples, _passed, _refused, _headChanges;
    private static IntPtr _headGob;
    private static string _headNode = "";

    internal static void Configure(ExtenderLog log)
    {
        if (_log != null) return;
        _log = log;
        KnobSurface.Note(Report);
        log.Info("Fix14C: diagnostic observer active; engine-facing input, desired and actual positions, animation id, guard refusals, head map");
    }

    /// <summary>Engine-facing key state. This is NOT a raw OS keyboard trace.</summary>
    internal static void Drive(bool held, int walk, int fwd, int back, int left, int right, int strafeL, int strafeR)
    {
        _held = held;
        _walk = walk; _fwd = fwd; _back = back;
        _left = left; _right = right; _strafeL = strafeL; _strafeR = strafeR;
    }

    /// <summary>After original walk step, before StepGuard's possible rollback. Local player only.</summary>
    internal static void Step(IntPtr creature, in Vector3 before, in Vector3 after, int driving)
    {
        _steps++;
        long now = Environment.TickCount64;
        if (_log == null || now - _lastStep < 250) return;
        _lastStep = now;
        _samples++;
        var o = new CNWCObject(creature);
        Vector3 actual = o.Position;
        int anim = o.Animation;
        Vector3 delta = after - before;
        _log.Info(FormattableString.Invariant($"Fix14C: step tick={now} mode={CameraService.Mode} held={(_held ? 1 : 0)} drive={driving} keys={_walk},{_fwd},{_back},{_left},{_right},{_strafeL},{_strafeR} anim={anim} actual={actual.X:F3},{actual.Y:F3},{actual.Z:F3} desired_before={before.X:F3},{before.Y:F3},{before.Z:F3} desired_after={after.X:F3},{after.Y:F3},{after.Z:F3} delta={delta.X:F3},{delta.Y:F3},{delta.Z:F3}"));
    }

    /// <summary>Only called when StepGuard invokes the engine's already-existing TestDirectLine.</summary>
    internal static void Guard(int clear, int driving, in Vector3 before, in Vector3 after)
    {
        if (clear == 1) { _passed++; return; }
        _refused++;
        long now = Environment.TickCount64;
        if (_log == null || now - _lastGuard < 250) return;
        _lastGuard = now;
        _log.Info(FormattableString.Invariant($"Fix14C: guard tick={now} refused=1 result={clear} drive={driving} from={before.X:F3},{before.Y:F3},{before.Z:F3} to={after.X:F3},{after.Y:F3},{after.Z:F3} roll_back=StepGuard"));
    }

    /// <summary>The head rig's mapping in HeadTrack's existing local-player path, not a helmet identifier.</summary>
    internal static void Head(IntPtr gob, string node)
    {
        if (gob == _headGob && string.Equals(node, _headNode, StringComparison.Ordinal)) return;
        _headGob = gob;
        _headNode = node;
        _headChanges++;
        _log?.Info($"Fix14C: head tick={Environment.TickCount64} gob=0x{gob.ToInt64():X} mapped={node} changes={_headChanges}");
    }

    private static string Report() =>
        $"Fix14C diagnostic: steps={_steps} samples={_samples} guard_pass={_passed} guard_refused={_refused} " +
        $"head_map_changes={_headChanges} held={(_held ? 1 : 0)} (read only; see cx.log)";
}
