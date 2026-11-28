using System;
using System.Numerics;
using System.Diagnostics;

namespace CX.Services.Camera;

// Observer only: bounded per-flight history, emitted only on selected failures.
internal sealed class JumpGroundAudit
{
    internal const int Capacity = 256;
    internal const int DumpLimit = 6;
    private readonly Sample[] _samples = new Sample[Capacity];
    private int _next, _count, _dumps;
    private long _seen;
    private struct Sample
    {
        internal long Number, Tick;
        internal Vector3? Before;
        internal Vector3 After;
        internal Vector2 Air, Support;
        internal float Expected, Ground, Floor, Time, Velocity, StepXY, StepZ, Limit;
        internal bool Refused, Missing, PriorMissing;
    }
    internal void Reset() { _next = _count = 0; _seen = 0; }
    internal void Record(Vector3? before, Vector3 after, Vector2 air, Vector2 support,
        float expected, float ground, float floor, float time, float velocity,
        float stepXY, float stepZ, float limit, bool refused, bool missing, bool priorMissing)
    {
        if (_dumps >= DumpLimit) return;
        _samples[_next] = new Sample { Number = ++_seen, Tick = Stopwatch.GetTimestamp(),
            Before = before, After = after, Air = air, Support = support,
            Expected = expected, Ground = ground, Floor = floor, Time = time,
            Velocity = velocity, StepXY = stepXY, StepZ = stepZ, Limit = limit,
            Refused = refused, Missing = missing, PriorMissing = priorMissing };
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
    }
    private static string V(Vector3 v) => FormattableString.Invariant($"{v.X:F6},{v.Y:F6},{v.Z:F6}");
    private static string V(Vector2 v) => FormattableString.Invariant($"{v.X:F6},{v.Y:F6}");
    internal void Dump(string reason, Action<string> emit)
    {
        if (_dumps >= DumpLimit) return;
        int dump = ++_dumps;
        emit($"[efp-ground] begin dump={dump} reason={reason} seen={_seen} retained={_count} discarded={_seen - _count} freq={Stopwatch.Frequency}");
        for (int i = 0; i < _count; i++)
        {
            Sample s = _samples[(_next - _count + Capacity + i) % Capacity];
            emit(FormattableString.Invariant($"[efp-ground] sample dump={dump} seq={s.Number} tick={s.Tick} air_t={s.Time:F6} vz={s.Velocity:F6} ") +
                $"pre={(s.Before.HasValue ? V(s.Before.Value) : "none")} post={V(s.After)} air_xy={V(s.Air)} support_xy={V(s.Support)} " +
                FormattableString.Invariant($"expected={s.Expected:F6} ground={s.Ground:F6} floor={s.Floor:F6} step_xy={s.StepXY:F6} step_z={s.StepZ:F6} limit={s.Limit:F6} ") +
                $"refused={(s.Refused ? 1 : 0)} missing={(s.Missing ? 1 : 0)} prior_missing={(s.PriorMissing ? 1 : 0)}");
        }
        emit($"[efp-ground] end dump={dump}");
    }
}
