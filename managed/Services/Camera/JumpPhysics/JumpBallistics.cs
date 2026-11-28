using System;

namespace CX.Services.Camera;

/// <summary>One elapsed-time contract for the existing local jump motor.
/// A long suspension is not simulated as a single giant movement step.
/// Ordinary slow frames, including 125 ms, retain their entire duration.</summary>
internal static class JumpBallistics
{
    internal const float MaxAcceptedFrame = 0.25f;
    internal const float MaxHorizontalStep = 0.04f;

    internal static bool AcceptFrame(float dt) =>
        float.IsFinite(dt) && dt > 0f && dt <= MaxAcceptedFrame;

    internal static float LaunchElapsed(float dt, float inputAge) =>
        Math.Clamp(inputAge, 0f, dt);

    internal static float Height(float t, float impulse, float gravity) =>
        t * (impulse - 0.5f * gravity * t);

    internal static float Velocity(float t, float impulse, float gravity) =>
        impulse - gravity * t;

    internal static int HorizontalSlices(float dt) =>
        Math.Max(1, (int)MathF.Ceiling(dt / MaxHorizontalStep));
}
