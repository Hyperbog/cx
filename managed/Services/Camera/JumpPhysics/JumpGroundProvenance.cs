using System;
using System.Numerics;

namespace CX.Services.Camera;

// A walk callback does not guarantee a new native terrain projection.
// Preserve the old ground sample when the guard refused or the walk chain
// returned the previously written airborne Z. Do not infer NPC support.
internal static class JumpGroundProvenance
{
    internal const float HeightTolerance = 0.001f;
    internal static bool Missing(Vector3? before, Vector3 after, float expectedZ, bool refused)
        => refused || !before.HasValue ||
           (MathF.Abs(before.Value.Z - expectedZ) <= HeightTolerance &&
            MathF.Abs(after.Z - before.Value.Z) <= HeightTolerance);

    // Conservative support reuse: within 1 cm of last accepted ground XY.
    internal static bool SameSupport(Vector2 actual, Vector2 last)
        => Vector2.DistanceSquared(actual, last) <= 0.0001f;
}
