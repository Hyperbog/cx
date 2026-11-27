using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>Read-only jump framing samples from the world camera solve and Alloy's matching frame.
/// Both call sites run on the game thread. The native renderer provides the solve only.</summary>
internal static class JumpFramingTrace
{
    private const int LineLimit = 96;
    private const int AirIntervalMs = 100, SettledDelayMs = 150, MaxSampleGapMs = 250;

    private readonly record struct Solve(long Tick, long Frame, IntPtr Camera, DbId DbCamera, IntPtr Player,
        bool Airborne, bool Tracked, Vector3 Actor, Vector3 Eye, Vector3 Look, float Requested, float Before, float After);

    private readonly record struct Projection(string? Source, float Fov, float Aspect, int Width, int Height,
        float ScaleX, float ScaleY, Vector3 EyeDifference);

    private readonly record struct Sample(Solve Solve, Projection Projection);

    private static Solve _pending;
    private static ExtenderLog? _pendingLog;
    private static Sample _previous;
    private static int _lines, _jump;
    private static long _nextAirSample, _settledAt;

    internal static bool Enabled => ExtenderLog.Enabled && _lines < LineLimit;

    internal static void Solved(ExtenderLog log, IntPtr camera, DbId dbCamera, IntPtr player, bool airborne,
                                bool tracked, Vector3 actor, Vector3 eye, Quaternion rotation,
                                float requested, float before, float after)
    {
        long frame = dbCamera.Value != 0 ? RenderSnapshot.FrameSeq : -1;
        var solve = new Solve(Environment.TickCount64, frame, camera, dbCamera, player, airborne, tracked,
            actor, eye, -CameraService.Forward(rotation), requested, before, after);
        if (frame < 0)
            Observe(log, new Sample(solve, new Projection("native-unavailable", 0f, 0f, 0, 0, 0f, 0f, default)));
        else
        {
            _pending = solve;
            _pendingLog = log;
        }
    }

    internal static void Published(RenderSnapshot.Frame frame)
    {
        var log = _pendingLog;
        _pendingLog = null;
        if (log == null || !Enabled || _pending.Frame != frame.Index) return;

        int world = -1;
        for (int i = 0; i < frame.ViewCount; i++)
        {
            ref var view = ref frame.Views[i];
            if (!view.World) continue;
            if (view.Camera == _pending.DbCamera) { world = i; break; }
            if (world < 0) world = i;
        }
        Projection projection = default;
        if (world >= 0)
        {
            ref var view = ref frame.Views[world];
            bool sameCamera = view.Camera == _pending.DbCamera;
            string source = sameCamera ? "matched" : "other-camera";
            projection = new Projection(source, view.Fovy, view.Aspect, view.W, view.H,
                view.Proj.M11, view.Proj.M22, sameCamera ? view.CamPos - _pending.Eye : new Vector3(float.NaN));
        }
        Observe(log, new Sample(_pending, projection));
    }

    private static void Observe(ExtenderLog log, in Sample sample)
    {
        Solve current = sample.Solve, previous = _previous.Solve;
        bool continuous = current.Camera == previous.Camera && current.Player == previous.Player &&
            current.Tick - previous.Tick is >= 0 and <= MaxSampleGapMs &&
            (current.Frame < 0 ? previous.Frame < 0 : current.Frame == previous.Frame + 1);
        if (!continuous) _settledAt = 0;
        if (current.Airborne && (!continuous || !previous.Airborne))
        {
            _jump++;
            _settledAt = 0;
            if (continuous) Write(log, "ground", _previous, default, false);
            Write(log, continuous ? "takeoff" : "first-air", sample, previous, continuous);
            _nextAirSample = current.Tick + AirIntervalMs;
        }
        else if (current.Airborne && current.Tick >= _nextAirSample)
        {
            Write(log, "air", sample, previous, continuous);
            _nextAirSample = current.Tick + AirIntervalMs;
        }
        else if (!current.Airborne && continuous && previous.Airborne)
        {
            Write(log, "last-air", _previous, default, false);
            Write(log, "landing", sample, previous, true);
            _settledAt = current.Tick + SettledDelayMs;
        }
        else if (_settledAt != 0 && current.Tick >= _settledAt)
        {
            Write(log, "settled", sample, previous, continuous);
            _settledAt = 0;
        }
        _previous = sample;
    }

    private static void Write(ExtenderLog log, string phase, in Sample sample, in Solve previous, bool havePrevious)
    {
        if (!Enabled) return;
        Solve s = sample.Solve;
        Projection p = sample.Projection;
        Vector3 offset = s.Eye - s.Actor;
        Vector3 actorStep = havePrevious ? s.Actor - previous.Actor : new Vector3(float.NaN);
        Vector3 offsetStep = havePrevious ? offset - (previous.Eye - previous.Actor) : new Vector3(float.NaN);
        string view = p.Source is null or "native-unavailable"
            ? p.Source ?? "missing"
            : FormattableString.Invariant($"{p.Source} drawFov={p.Fov:F3} viewport={p.Width}x{p.Height} aspect={p.Aspect:F5} scale={p.ScaleX:F5},{p.ScaleY:F5} drawEyeDelta={p.EyeDifference.Length():F4}");
        log.Info(FormattableString.Invariant($"[efpjump] jump={_jump} phase={phase} frame={s.Frame} t_ms={s.Tick} tracked={s.Tracked} fov={s.Requested:F3}/{s.Before:F3}/{s.After:F3} look={s.Look.X:F4},{s.Look.Y:F4},{s.Look.Z:F4} eyeOffset={offset.X:F4},{offset.Y:F4},{offset.Z:F4} actorStep={actorStep.X:F4},{actorStep.Y:F4},{actorStep.Z:F4} offsetStep={offsetStep.X:F4},{offsetStep.Y:F4},{offsetStep.Z:F4} view={view}"));
        _lines++;
    }
}
