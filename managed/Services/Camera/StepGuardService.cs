// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/StepGuardService.cs
// PURPOSE: Native movement/position observer
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   D5 records pre/post WalkUpdateLocation position changes while leaving the
//   StepGuard correction decision untouched. A native drive flag by itself
//   does not prove displacement.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>
/// Client step guard: chains <c>CNWCCreature::WalkUpdateLocation</c>, the per-frame walk step, for the player
/// creature alone -- the desired position is snapshotted, the engine steps it, and the step is re-run through the
/// engine's own door-aware swept test, <c>CNWCArea::TestDirectLine</c> with closed doors solid. A refused step puts
/// the desired position and the model back, so no packet claims the far side of a closed door. Hot, no allocation.
/// </summary>
public sealed unsafe class StepGuardService : IExtenderService
{
    private static ExtenderLog? _log;
    private static WalkUpdateLocationFn* _next;
    private static TestDirectLineFn _testLine;
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);

    /// <summary>The update handler snaps rather than lerps from 4 m; a desired-position jump past it is not a step.</summary>
    private const float MaxStep = 4f;
    /// <summary>TestDirectLine's bCheckCreatures: doors are always solid to it; creatures in the way are not a wall.</summary>
    private const int NoCreatures = 0;

    private static bool _live, _probeOk;
    private static int _steps, _walkRefusals, _driveRefusals;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        Fix14CProbe.Configure(log);
        CameraKnobs.Declare();
        SelfCheck(log);
        if (!EngineLayout.Require(log, "StepGuard", "CClientExoApp", "CClientExoAppInternal", "CNWCObject", "CNWCCreature", "CNWCModule", "CPathfindInformation")) return;
        if (!SymbolMap.Require(log, "StepGuard", SymbolMap.WalkUpdateLocation, SymbolMap.TestDirectLine, SymbolMap.AppManager, SymbolMap.GetModule)) return;
        _testLine = EngineFn.TestDirectLine;
        // chained, not installed: the pose layer's keyboard weight sets the step's rate just outside it (priority 60)
        _next = hooks.ChainWalkUpdateLocation(&OnWalkUpdateLocation, 50);
        if (_next == null) { log.Info("StepGuard: standing down (WalkUpdateLocation chain refused)"); return; }
        KnobSurface.Note(Report);
        log.Info("StepGuard: chained CNWCCreature::WalkUpdateLocation");
    }

    [UnmanagedCallersOnly]
    private static int OnWalkUpdateLocation(IntPtr cre)
    {
        IntPtr mgr = *(IntPtr*)_appMgr;
        IntPtr app = mgr == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)mgr;
        CClientExoAppInternal intern = app == IntPtr.Zero ? default : new CClientExoApp(app).Internal;
        var creature = new CNWCCreature(cre);
        if (intern.IsNull || new CNWCObject(cre).Id != intern.PlayerCreatureId) return (*_next)(cre);
        if (!_live)
        {
            _live = true;
            _probeOk = EngineLayout.Probe("CNWCCreature", cre, _log);
            _log?.Info(_probeOk ? "StepGuard: first player step -- hook is LIVE" : "StepGuard: layout probe failed -- engine walk untouched");
        }
        IntPtr module = CameraService.Module();
        CNWCArea area = module == IntPtr.Zero ? default : new CNWCModule(module).CurrentArea;
        CPathfindInformation pathfind = creature.Pathfind;
        if (!_probeOk || area.IsNull || pathfind.IsNull) return (*_next)(cre);

        ref Vector3 desired = ref creature.DesiredPos;
        Vector3 before = desired;
        // Fix14D-D5 OBSERVER BEGIN: pre-native position capture; no engine writes.
        long d5Start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool d5Watch = CameraDriveService.D4TraceWindow(d5Start, out int d5ReleaseId, out long d5ReleaseAt);
        Vector3 d5PlayerBefore = d5Watch ? new CNWCObject(cre).Position : default;
        int d5DrivingBefore = d5Watch ? creature.Driving : 0;
        // Fix14D-D5 OBSERVER END.
        int r = (*_next)(cre);
        // Fix14D-D5 OBSERVER BEGIN: sample the existing original call.
        if (d5Watch)
            D5Trace("native", d5Start, d5ReleaseAt, d5ReleaseId, d5PlayerBefore,
                new CNWCObject(cre).Position, before, desired, d5DrivingBefore, creature.Driving, r);
        // Fix14D-D5 OBSERVER END.
        Vector3 after = desired;
        Fix14CProbe.Step(cre, before, after, creature.Driving);
        if (!IsStep(before, after) || new CNWCModule(module).CurrentArea.Ptr != area.Ptr) return r;
        Span<float> space = pathfind.SpaceArgs;
        // TestDirectLine takes both Vectors BY VALUE: the copies are what VectorArg points Win64 at.
        Vec3 from = Vec(before), to = Vec(after);
        int clear = _testLine(area.Ptr, cre, VectorArg.Of(&from), VectorArg.Of(&to), space[0], space[1], NoCreatures);
        Fix14CProbe.Guard(clear, creature.Driving, before, after);
        if (clear == 1) { _steps++; return r; }
        desired = before;
        creature.SetPosition(VectorArg.Of(&from));
        // Fix14D-D5 OBSERVER BEGIN: sample the existing StepGuard correction.
        if (d5Watch)
            D5Trace("guard", d5Start, d5ReleaseAt, d5ReleaseId, d5PlayerBefore,
                new CNWCObject(cre).Position, before, desired, d5DrivingBefore, creature.Driving, r);
        // Fix14D-D5 OBSERVER END.
        if (creature.Driving != 0) _driveRefusals++; else _walkRefusals++;
        return r;
    }

    /// <summary>The engine's own <c>Vector</c> shape, which the by-value call arguments need.</summary>
    private static Vec3 Vec(in Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    // Fix14D-D5 OBSERVER BEGIN: bounded logging in D4's existing post-release window.
    private const int D5Limit = 1000;
    private static int _d5Count;
    private static void D5Trace(string phase, long begin, long releasedAt, int id,
        Vector3 p0, Vector3 p1, Vector3 d0, Vector3 d1, int driving0, int driving1, int result)
    {
        if (_log == null || _d5Count >= D5Limit) return;
        float dx = p1.X - p0.X, dy = p1.Y - p0.Y;
        float dwx = d1.X - d0.X, dwy = d1.Y - d0.Y;
        int count = ++_d5Count;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double tickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        // Keep each interpolation a FormattableString before concatenating the log text.
        // Concatenating first produces string, which FormattableString.Invariant cannot accept.
        _log.Info(
            FormattableString.Invariant($"Fix14D-D5: phase={phase} release_id={id} t={begin} rel_ms={(begin - releasedAt) * tickMs:F2} ") +
            FormattableString.Invariant($"elapsed_ms={(now - begin) * tickMs:F3} native_result={result} drive={driving0},{driving1} ") +
            FormattableString.Invariant($"pos_pre={p0.X:F5},{p0.Y:F5},{p0.Z:F5} pos_post={p1.X:F5},{p1.Y:F5},{p1.Z:F5} ") +
            FormattableString.Invariant($"delta_xy={MathF.Sqrt(dx*dx+dy*dy):F5} desired_pre={d0.X:F5},{d0.Y:F5} ") +
            FormattableString.Invariant($"desired_post={d1.X:F5},{d1.Y:F5} desired_delta_xy={MathF.Sqrt(dwx*dwx+dwy*dwy):F5} ") +
            FormattableString.Invariant($"thread={Environment.CurrentManagedThreadId} count={count}/{D5Limit}"));
    }
    // Fix14D-D5 OBSERVER END.

    /// <summary>A walk step moved the desired point and by less than <see cref="MaxStep"/> in the plane; anything
    /// else (unchanged, or a server rewrite) is not a step to test.</summary>
    private static bool IsStep(in Vector3 a, in Vector3 b)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y, d2 = dx * dx + dy * dy;
        return d2 > 0f && d2 < MaxStep * MaxStep;
    }

    /// <summary>The /knobs note: the refusal ledger split by the driving flag, which answers
    /// whether refusals ever fire on a drive segment or only on waypoint steps.</summary>
    internal static string Report()
        => FormattableString.Invariant(
            $"step guard: {(_live ? _probeOk ? "live" : "probe failed" : "no player step yet")} | {_steps} step(s) passed, {_walkRefusals} walk + {_driveRefusals} drive step(s) refused");

    /// <summary>The step classifier off-device: stationary and teleport-sized moves pass untested, a walk step is tested.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "StepGuard.SelfCheck");
        static Vector3 V(float x, float y, float z) => new(x, y, z);
        check.Expect(!IsStep(V(1, 2, 3), V(1, 2, 3)), "an unchanged desired point is not a step");
        check.Expect(!IsStep(V(1, 2, 3), V(1, 2, 9)), "a height-only change is not a step");
        check.Expect(IsStep(V(1, 2, 3), V(1.05f, 2, 3)), "a centimetre move is a step");
        check.Expect(IsStep(V(0, 0, 0), V(2.8f, 2.8f, 0)), "a 3.96 m move is still a step");
        check.Expect(!IsStep(V(0, 0, 0), V(4, 0, 0)) && !IsStep(V(0, 0, 0), V(50, 50, 0)), "4 m and beyond is a rewrite, not a step");
        log.Info(check.Ok ? "StepGuard.SelfCheck: passed" : "StepGuard.SelfCheck: FAILED");
    }
}
