using System;
using System.Numerics;
using CX.Alloy;

namespace CX.Services.Camera;

/// <summary>Off-device consumer parity for full, unscaled viewports and a roll-free camera.
/// The renderer matrix is the reference; use the same Projector and ShadowMath as live callers.</summary>
internal static class ProjectionConsumerCheck
{
    // Bit 1 = aim pixels, bit 2 = sphere visibility, bit 4 = receiver planes.
    internal static int Failures()
    {
        int failures = 0;
        (int w, int h, float verticalDegrees)[] cases =
        {
            (5120, 1440, 2.819092f), // 10 horizontal at 32:9
            (5120, 1440, 3.19f),
            (5120, 1440, 3.2f),
            (5120, 1440, 3.21f),
            (2560, 1440, 58.7155f), // 90 horizontal at 16:9
            (1440, 2560, 121.2845f) // 90 horizontal in portrait
        };
        Vector2[] offsets = { Vector2.Zero, new(0.5f, 0f), new(-0.5f, 0f),
                              new(0f, 0.5f), new(0f, -0.5f), new(0.7f, -0.7f) };
        Span<CullPlane> planes = stackalloc CullPlane[6];
        foreach (var (w, h, degrees) in cases)
        {
            float aspect = (float)w / h;
            Matrix4x4 matrix = AlloyCamera.Perspective(degrees, aspect, 0.1f, 100f);
            Projector aim = Projector.FromView(Vector3.Zero, Quaternion.Identity,
                                               w / 2, h / 2, degrees, aspect, 0.1f);
            const float depth = 10f;
            float edgeX = depth / matrix.M11, edgeY = depth / matrix.M22;
            foreach (Vector2 offset in offsets)
            {
                Vector3 point = new(offset.X * edgeX, offset.Y * edgeY, -depth);
                Vector4 clip = Vector4.Transform(new Vector4(point, 1f), matrix);
                float x = (1f + clip.X / clip.W) * w * 0.5f;
                float y = (1f - clip.Y / clip.W) * h * 0.5f;
                if (!aim.Screen(point, out float sx, out float sy, out _) ||
                    !(MathF.Abs(sx - x) <= 1f && MathF.Abs(sy - y) <= 1f)) failures |= 1;
            }
            // Identity rotation looks along -Z. ShadowMath's straight-down fallback uses +X right.
            ShadowMath.ReceiverPlanes(Vector3.Zero, -Vector3.UnitZ, degrees, aspect,
                                      0.1f, 100f, 0f, planes);
            for (int axis = 0; axis < 2; axis++)
            for (int side = -1; side <= 1; side += 2)
            for (int outside = 0; outside < 2; outside++)
            {
                float edge = axis == 0 ? edgeX : edgeY;
                float coord = side * (edge + (outside == 0 ? -0.01f : 0.01f));
                Vector3 point = new(axis == 0 ? coord : 0f, axis == 1 ? coord : 0f, -depth);
                const float radius = 0.001f;
                bool expected = outside == 0;
                if (ShadowMath.SphereInCameraFrustum(point, radius, Vector3.Zero, -Vector3.UnitZ,
                    degrees, aspect, 0.1f, 100f) != expected) failures |= 2;
                if (ShadowMath.ShadowReaches(planes, point, radius, point, radius) != expected) failures |= 4;
            }
        }
        return failures;
    }
}
