using System;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>Temporary, observer-only capture of the first eligible Space press.
/// One monotonic clock; no per-sample formatting or I/O during the capture.</summary>
internal static class JumpTimingTrace
{
    private const int Capacity = 8192;
    private static readonly object Gate = new();
    private static readonly Sample[] Samples = new Sample[Capacity];
    private static ExtenderLog? _log;
    private static volatile bool _active;
    private static bool _finished;
    private static long _start;
    private static int _count, _dropped;
    private static Sample _lastModel, _lastRig, _lastSolve;

    private readonly record struct Sample(long Tick, string Kind, Vector3 A, Vector3 B,
        Vector3 C, float X, float Y, float Z, long Frame);

    internal static bool Active => _active;
    internal static void Configure(ExtenderLog log) => _log = log;

    private static void Add(in Sample sample)
    {
        if (_count < Capacity) Samples[_count++] = sample;
        else _dropped++;
    }

    internal static void Key(int previous)
    {
        if (!ExtenderLog.Enabled || _log == null) return;
        lock (Gate)
        {
            if (_finished) return;
            long now = Stopwatch.GetTimestamp();
            if (!_active)
            {
                _start = now;
                // These are the last pre-key observations, not newly read poses.
                AddBaseline(_lastModel, now);
                AddBaseline(_lastRig, now);
                AddBaseline(_lastSolve, now);
                _active = true;
            }
            Add(new Sample(now, "keydown", default, default, default, previous, 0, 0, -1));
        }
    }

    private static void AddBaseline(in Sample sample, long now)
    {
        if (sample.Tick != 0 && now >= sample.Tick && now - sample.Tick <= Stopwatch.Frequency / 10)
            Add(sample);
    }

    internal static void Mark(string kind, Vector3 a = default, Vector3 b = default,
                              Vector3 c = default, float x = 0, float y = 0, float z = 0, long frame = -1)
    {
        if (_finished || !ExtenderLog.Enabled || _log == null) return;
        if (!_active && kind != "model" && kind != "rig" && kind != "solve") return;
        lock (Gate)
        {
            if (_finished) return;
            var sample = new Sample(Stopwatch.GetTimestamp(), kind, a, b, c, x, y, z, frame);
            if (_active) Add(sample);
            else if (kind == "model") _lastModel = sample;
            else if (kind == "rig") _lastRig = sample;
            else if (kind == "solve") _lastSolve = sample;
        }
    }

    // Call outside the native position writer, once per world-camera tick.
    // Stop recording before formatting; failures in the diagnostic cannot reset a jump.
    internal static void Pump()
    {
        if (!_active || Stopwatch.GetTimestamp() - _start < 2 * Stopwatch.Frequency) return;
        lock (Gate)
        {
            if (!_active) return;
            _active = false;
            _finished = true;
        }
        try
        {
            var log = _log;
            if (log == null) return;
            log.Info(FormattableString.Invariant($"[efp-jtime] BEGIN frequency={Stopwatch.Frequency} start={_start} records={_count} dropped={_dropped}"));
            for (int i = 0; i < _count; i++)
            {
                Sample s = Samples[i];
                double ms = (s.Tick - _start) * 1000d / Stopwatch.Frequency;
                log.Info(FormattableString.Invariant($"[efp-jtime] seq={i} tick={s.Tick} ms={ms:F4} event={s.Kind} frame={s.Frame} a={s.A.X:F5},{s.A.Y:F5},{s.A.Z:F5} b={s.B.X:F5},{s.B.Y:F5},{s.B.Z:F5} c={s.C.X:F5},{s.C.Y:F5},{s.C.Z:F5} x={s.X:F6} y={s.Y:F6} z={s.Z:F6}"));
            }
            log.Info(FormattableString.Invariant($"[efp-jtime] END records={_count} dropped={_dropped}"));
        }
        catch (Exception) { /* Temporary diagnostic only. */ }
    }
}
