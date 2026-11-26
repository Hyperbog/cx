// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/JumpPhysics/RayCollisionWorld.cs
// PURPOSE: Existing horizontal collision checks
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Use validated engine walkmesh/collision helpers and do not infer
//   arbitrary geometric support from rendered objects.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Numerics;

namespace NWNFP.Physics;

/// <summary>A scene-segment ray hit. The normal is an estimate if the engine does not expose one.</summary>
public readonly record struct RayContact(Vector3 Point, Vector3 Normal);

/// <summary>A narrow interface that the NWNCX SceneRay wrapper can implement.</summary>
public interface IRayScene
{
    bool Cast(Vector3 from, Vector3 to, out RayContact hit);
}

/// <summary>
/// Conservative multi-ray stand-in for a real capsule cast. Samples the centre and four perimeter
/// locations for vertical motion, and the leading rim at multiple heights for horizontal motion.
/// Cannot detect every edge, overhang, animated platform, or sub-ray-sized obstacle. PREVIEW ONLY.
/// </summary>
public sealed class RayCollisionWorld : ICollisionWorld
{
    private readonly IRayScene _scene;
    private const float Epsilon = 0.001f;
    private static readonly Vector2[] Disk =
    {
        Vector2.Zero, Vector2.UnitX, -Vector2.UnitX, Vector2.UnitY, -Vector2.UnitY
    };

    public RayCollisionWorld(IRayScene scene) => _scene = scene ?? throw new ArgumentNullException(nameof(scene));

    public bool ProbeGround(Vector3 feet, float radius, float maxDrop, out GroundHit ground)
    {
        ground = default;
        if (!(radius > 0f) || !(maxDrop > 0f)) return false;
        bool found = false;
        float best = float.NegativeInfinity;
        Vector3 normal = Vector3.UnitZ;
        foreach (Vector2 v in Disk)
        {
            Vector3 xy = new(v.X * radius * 0.7f, v.Y * radius * 0.7f, 0f);
            Vector3 start = feet + xy + Vector3.UnitZ * 0.025f;
            Vector3 end = start - Vector3.UnitZ * (maxDrop + 0.05f);
            if (!_scene.Cast(start, end, out RayContact c) || !Finite(c.Point)) continue;
            Vector3 n = SafeNormal(c.Normal, Vector3.UnitZ);
            if (n.Z < 0.68f) continue;
            float dz = feet.Z - c.Point.Z;
            if (dz < -0.025f || dz > maxDrop + 0.005f || c.Point.Z <= best) continue;
            best = c.Point.Z;
            normal = n;
            found = true;
        }
        if (found) ground = new GroundHit(best, normal);
        return found;
    }

    public bool Sweep(in Capsule body, Vector3 delta, out SweepHit hit)
    {
        hit = default;
        if (!Finite(body.Feet) || !Finite(delta) || !(body.Radius > 0f)
            || !(body.Height > body.Radius * 2f) || delta.LengthSquared() <= 1e-12f)
            return false;
        float earliest = float.PositiveInfinity;
        Vector3 nBest = Vector3.Zero;
        float vertical = delta.Z;
        if (vertical > Epsilon)
        {
            foreach (Vector2 v in Disk)
                Consider(new Vector3(body.Feet.X + v.X * body.Radius * 0.85f,
                    body.Feet.Y + v.Y * body.Radius * 0.85f,
                    body.Feet.Z + body.Height), delta, -Vector3.UnitZ);
        }
        if (vertical < -Epsilon)
        {
            foreach (Vector2 v in Disk)
                Consider(new Vector3(body.Feet.X + v.X * body.Radius * 0.85f,
                    body.Feet.Y + v.Y * body.Radius * 0.85f,
                    body.Feet.Z + 0.015f), delta, Vector3.UnitZ);
        }
        Vector2 lateral = new(delta.X, delta.Y);
        if (lateral.LengthSquared() > Epsilon * Epsilon)
        {
            Vector2 forward = Vector2.Normalize(lateral), right = new(-forward.Y, forward.X);
            Vector2 front = forward * body.Radius;
            for (int level = 0; level < 3; level++)
            {
                float h = level == 0 ? body.Radius : level == 1 ? body.Height * 0.5f : body.Height - body.Radius;
                for (int side = -1; side <= 1; side++)
                {
                    Vector2 p = front + right * (body.Radius * side * 0.7f);
                    Consider(body.Feet + new Vector3(p.X, p.Y, h), delta,
                             new Vector3(-forward.X, -forward.Y, 0f));
                }
            }
        }
        if (float.IsPositiveInfinity(earliest)) return false;
        hit = new SweepHit(earliest, nBest);
        return true;

        void Consider(Vector3 from, Vector3 move, Vector3 fallbackNormal)
        {
            if (!_scene.Cast(from, from + move, out RayContact contact) || !Finite(contact.Point)) return;
            float fraction = Vector3.Dot(contact.Point - from, move) / move.LengthSquared();
            if (!float.IsFinite(fraction) || fraction < -0.001f || fraction > 1.001f) return;
            fraction = Math.Clamp(fraction, 0f, 1f);
            if (fraction >= earliest) return;
            Vector3 normal = SafeNormal(contact.Normal, fallbackNormal);
            // Never allow a normal aligned with travel to push *toward* the obstacle.
            if (Vector3.Dot(normal, move) > -Epsilon) normal = fallbackNormal;
            earliest = fraction;
            nBest = normal;
        }
    }

    private static Vector3 SafeNormal(Vector3 n, Vector3 fallback)
        => Finite(n) && n.LengthSquared() > 0.1f ? Vector3.Normalize(n) : fallback;
    private static bool Finite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
