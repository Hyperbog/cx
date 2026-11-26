// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/JumpPhysics/AirMomentumMotor.cs
// PURPOSE: Airborne horizontal steering
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Maintains native-ground running speed and direction constraints while
//   airborne; no permanent ground sliding physics.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Numerics;

namespace CX.Services.Camera;

/// <summary>
/// Independent airborne horizontal velocity. The native game retains control
/// over walking and orientation; this motor owns X/Y only while airborne.
/// </summary>
internal sealed class AirMomentumMotor
{
    internal const float MaxSpeed = 4.5f;
    internal const float AirAcceleration = 8.5f;
    internal const float AirDrag = 0.45f;
    private const float MaxFrame = 0.04f;
    internal Vector2 Position { get; private set; }
    internal Vector2 Velocity { get; private set; }

    internal void Launch(Vector2 position, Vector2 carry)
    {
        Position = position;
        Velocity = Limit(Finite(carry) ? carry : Vector2.Zero);
    }

    internal Vector2 Step(float dt, Vector2 wish)
    {
        if (!float.IsFinite(dt) || dt <= 0f) return Position;
        dt = MathF.Min(dt, MaxFrame);
        wish = Finite(wish) ? LimitUnit(wish) : Vector2.Zero;
        if (wish.LengthSquared() > 1e-6f)
        {
            Vector2 delta = wish * MaxSpeed - Velocity;
            float length = delta.Length();
            float maxChange = AirAcceleration * dt;
            Velocity += length > maxChange ? delta * (maxChange / length) : delta;
        }
        else
        {
            Velocity *= MathF.Max(0f, 1f - AirDrag * dt);
        }
        Velocity = Limit(Velocity);
        Position += Velocity * dt;
        return Position;
    }

    /// <summary>Resolve the motor against the native walkmesh and stop blocked axes.</summary>
    internal void Resolve(Vector2 accepted)
    {
        if (!Finite(accepted)) return;
        if (MathF.Abs(accepted.X - Position.X) > 0.0001f) Velocity = new(0f, Velocity.Y);
        if (MathF.Abs(accepted.Y - Position.Y) > 0.0001f) Velocity = new(Velocity.X, 0f);
        Position = accepted;
    }

    private static Vector2 LimitUnit(Vector2 v)
    {
        float length = v.Length();
        return length > 1f ? v / length : v;
    }

    private static Vector2 Limit(Vector2 v)
    {
        float length = v.Length();
        return length > MaxSpeed ? v * (MaxSpeed / length) : v;
    }

    private static bool Finite(Vector2 p) => float.IsFinite(p.X) && float.IsFinite(p.Y);
}
