// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/JumpPhysics/JumpMotor.cs
// PURPOSE: Jump motor and trigger gating
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Fixed impulse and gravity with controlled air steering. Combat speed
//   modifiers, collision authority and animation transition must not be
//   silently changed.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Numerics;

namespace NWNFP.Physics;

// Positions are world-space FEET locations; Z is up. All units are NWN world metres.
// The host owns authoritative movement and supplies collision queries. No NWN engine
// pointers, network messages, transforms, or memory writes are performed here.
public readonly record struct Capsule(Vector3 Feet, float Radius, float Height);
public readonly record struct SweepHit(float Fraction, Vector3 Normal);
public readonly record struct GroundHit(float Height, Vector3 Normal);

public interface ICollisionWorld
{
    // Sweeps the whole standing capsule along the delta; earliest contact, fraction 0..1.
    // Normal must point out of the obstacle. A zero-length delta returns false.
    bool Sweep(in Capsule body, Vector3 delta, out SweepHit hit);
    // Finds a supporting surface directly below the feet, within maxDrop. Must exclude
    // the controlled creature and non-solid triggers. Return the surface's world Z.
    bool ProbeGround(Vector3 feet, float radius, float maxDrop, out GroundHit hit);
}

public sealed class JumpSettings
{
    public float WalkSpeed { get; init; } = 2f;
    public float RunSpeed { get; init; } = 4.4f;
    public float GroundAcceleration { get; init; } = 35f;
    public float AirAcceleration { get; init; } = 7f;
    public float GroundDeceleration { get; init; } = 28f;
    public float Gravity { get; init; } = 18f;
    public float JumpHeight { get; init; } = 1.20f;
    public float TerminalSpeed { get; init; } = 30f;
    public float Radius { get; init; } = 0.3f;
    public float Height { get; init; } = 1.65f;
    public float Skin { get; init; } = 0.005f;
    public float GroundProbe { get; init; } = 0.12f;
    public float MaxSlopeDegrees { get; init; } = 46f;
    public float CoyoteSeconds { get; init; } = 0.10f;
    public float JumpBufferSeconds { get; init; } = 0.12f;
    public float FixedStep { get; init; } = 1f / 120f;
}

public sealed class JumpMotor
{
    private readonly JumpSettings _cfg;
    private float _accumulator, _coyote, _buffer;


    public Vector3 Feet { get; private set; }
    public Vector3 Velocity { get; private set; }
    public bool Grounded { get; private set; }
    public bool JumpStartedThisTick { get; private set; }
    public bool LandedThisTick { get; private set; }

    public JumpMotor(JumpSettings? config = null)
    {
        _cfg = config ?? new JumpSettings();
        if (_cfg.FixedStep <= 0 || _cfg.FixedStep > 0.04f || _cfg.Gravity <= 0 ||
            _cfg.Height <= 2 * _cfg.Radius || _cfg.JumpHeight <= 0 ||
            _cfg.Radius <= 0 || _cfg.Skin < 0 || _cfg.GroundProbe <= 0)
            throw new ArgumentOutOfRangeException(nameof(config), "Invalid physical parameters");
    }

    public void Reset(Vector3 feet, ICollisionWorld world)
    {
        Feet = feet;
        Velocity = Vector3.Zero;
        _accumulator = _buffer = _coyote = 0f;
        Grounded = world.ProbeGround(feet, _cfg.Radius, _cfg.GroundProbe,
            out var floor) && IsWalkable(floor.Normal) && feet.Z >= floor.Height - 0.025f
            && feet.Z <= floor.Height + _cfg.GroundProbe;
        if (Grounded)
        {
            Feet = Feet.WithZ(floor.Height);
            _coyote = _cfg.CoyoteSeconds;
        }
    }

    /// <summary>Keep the preview at the game-controlled XY position without resetting the vertical jump.
    /// The caller must Reset if there was an area change or a large authoritative displacement.</summary>
    public void FollowAuthoritative(Vector3 realFeet, float elevationChange)
    {
        Feet = new Vector3(realFeet.X, realFeet.Y, Feet.Z + elevationChange);
    }

    // moveWorld is XY normalized by the caller, typically camera-forward/right projected
    // onto the ground plane. jumpPressed is an EDGE, not "key is held".
    public void Tick(float dt, Vector2 moveWorld, bool running, bool jumpPressed, ICollisionWorld world)
    {
        JumpStartedThisTick = LandedThisTick = false;
        if (!float.IsFinite(dt) || dt <= 0f) return;
        if (jumpPressed) _buffer = _cfg.JumpBufferSeconds;
        if (moveWorld.LengthSquared() > 1f) moveWorld = Vector2.Normalize(moveWorld);
        // Clamp a stalled client frame. Running physical time faster to catch up can
        // tunnel through a wall even with a finite sweep query.
        _accumulator += MathF.Min(dt, 0.10f);
        int steps = 0;
        while (_accumulator >= _cfg.FixedStep && steps++ < 12)
        {
            Step(_cfg.FixedStep, moveWorld, running, world);
            _accumulator -= _cfg.FixedStep;
        }
        if (steps > 12) _accumulator = 0f;
    }

    private void Step(float dt, Vector2 input, bool running, ICollisionWorld world)
    {
        bool wasGrounded = Grounded;
        // Ground detection never snaps a rising character down to the floor.
        if (Velocity.Z <= 0f && world.ProbeGround(Feet, _cfg.Radius, _cfg.GroundProbe, out var floor)
            && IsWalkable(floor.Normal) && Feet.Z >= floor.Height - 0.025f
            && Feet.Z <= floor.Height + _cfg.GroundProbe)
        {
            Grounded = true;
            Feet = Feet.WithZ(floor.Height);
            if (Velocity.Z < 0f) Velocity = Velocity.WithZ(0f);
            if (!wasGrounded) LandedThisTick = true;
        }
        else Grounded = false;

        _coyote = Grounded ? _cfg.CoyoteSeconds : MathF.Max(0f, _coyote - dt);
        if (_buffer > 0f && _coyote > 0f)
        {
            Velocity = Velocity.WithZ(MathF.Sqrt(2f * _cfg.Gravity * _cfg.JumpHeight));
            Grounded = false;
            _coyote = 0f;
            _buffer = 0f;
            JumpStartedThisTick = true;
            LandedThisTick = false;
        }
        else _buffer = MathF.Max(0f, _buffer - dt);

        float speed = running ? _cfg.RunSpeed : _cfg.WalkSpeed;
        Vector2 desired = input * speed;
        Vector2 current = new(Velocity.X, Velocity.Y);
        float acceleration = Grounded ? _cfg.GroundAcceleration : _cfg.AirAcceleration;
        if (input.LengthSquared() < 0.0001f && Grounded) acceleration = _cfg.GroundDeceleration;
        Vector2 change = desired - current;
        float maxChange = acceleration * dt;
        if (change.LengthSquared() > maxChange * maxChange)
            change = Vector2.Normalize(change) * maxChange;
        current += change;
        Velocity = new Vector3(current, Velocity.Z);

        if (!Grounded) Velocity = Velocity.WithZ(MathF.Max(-_cfg.TerminalSpeed, Velocity.Z - _cfg.Gravity * dt));
        Move(Velocity * dt, world);
    }

    private void Move(Vector3 delta, ICollisionWorld world)
    {
        for (int slide = 0; slide < 4 && delta.LengthSquared() > 1e-12f; slide++)
        {
            var body = new Capsule(Feet, _cfg.Radius, _cfg.Height);
            if (!world.Sweep(body, delta, out SweepHit hit)) { Feet += delta; break; }
            if (hit.Fraction < 0f || hit.Fraction > 1f || hit.Normal.LengthSquared() < 0.5f)
                break; // malformed collision result: stop rather than clip into walls
            Vector3 normal = Vector3.Normalize(hit.Normal);
            float distance = delta.Length();
            float advance = MathF.Max(0f, hit.Fraction - (_cfg.Skin / distance));
            Feet += delta * advance;
            if (normal.Z > WalkableNormalZ && Velocity.Z <= 0f)
            {
                Grounded = true;
                if (!LandedThisTick) LandedThisTick = true;
            }
            Vector3 unused = delta * (1f - advance);
            delta = unused - normal * MathF.Min(0f, Vector3.Dot(unused, normal));
            Velocity -= normal * MathF.Min(0f, Vector3.Dot(Velocity, normal));
            // When a sweep starts directly on a surface, skin distance can leave a
            // residual sub-millimetre delta; the iteration bound prevents looping.
        }
    }

    private float WalkableNormalZ => MathF.Cos(_cfg.MaxSlopeDegrees * MathF.PI / 180f);
    private bool IsWalkable(Vector3 normal) => normal.Z >= WalkableNormalZ;
}

// Keep the hot path readable. This is deliberately not a write into engine state.
file static class VectorZ
{
    // C# has no with-expression on Vector3. This helper is used as an extension.
    public static Vector3 WithZ(this Vector3 v, float z) => new(v.X, v.Y, z);
}
