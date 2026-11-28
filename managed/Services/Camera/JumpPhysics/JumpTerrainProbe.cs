using System;
using System.Numerics;
using CX.Alloy.Db;
using static CX.Services.Camera.JumpGroundAudit;

namespace CX.Services.Camera;

// Shadow observer only. A walkmesh intersection is not landing authority.
internal static class JumpTerrainProbe
{
    private const int CastLimit = 8192;
    private static int _casts;

    internal static bool TrySegment(Vector3 at, float expected, float ground, float floor,
        out Vector3 from, out Vector3 to)
    {
        from = to = default;
        if (!Finite(at) || !float.IsFinite(expected) || !float.IsFinite(ground) || !float.IsFinite(floor))
            return false;
        float hi = MathF.Max(MathF.Max(at.Z, expected), MathF.Max(ground, floor)) + .25f;
        float lo = MathF.Min(MathF.Min(at.Z, expected), MathF.Min(ground, floor)) - 3.5f;
        if (!float.IsFinite(hi) || !float.IsFinite(lo) || hi <= lo || hi - lo > 8f) return false;
        from = new Vector3(at.X, at.Y, hi);
        to = new Vector3(at.X, at.Y, lo);
        return true;
    }

    internal static TerrainObservation Observe(Vector3 at, float expected, float ground, float floor)
    {
        if (!CX.Alloy.Renderer.On) return new("non_alloy");
        if (_casts >= CastLimit) return new("budget");
        if (!TrySegment(at, expected, ground, floor, out var from, out var to)) return new("invalid_segment");
        try
        {
            DbId scene = DbScene.World();
            if (scene.IsNull || !SceneDb.Scenes.Valid(scene) || !SceneDb.SceneIsWorld[scene.Index])
                return new("no_world");
            var q = new HitQuery { Scene = scene, From = from, To = to,
                SurfaceMask = HitQuery.NoTriggers, Objects = false, StaticPlaceables = false,
                WalkMesh = true, Ground = false, BoxOnly = false, SkipCharacters = true };
            ++_casts;
            if (!HitTest.Cast(q, out HitResult r)) return new("miss", Scene: scene.Value);
            string status = "unclassified";
            if (DbScene.World() != scene) status = "scene_changed";
            else if (r.Kind == HitKind.Static && SceneDb.Objects.Valid(r.Obj) &&
                     SceneDb.HitObjStatic[r.Obj.Index] && SceneDb.ObjScene[r.Obj.Index] == scene &&
                     r.Part >= 0 && r.Part < SceneDb.Parts.High && SceneDb.Parts.LiveAt(r.Part) &&
                     r.Part >= SceneDb.ObjPartFirst[r.Obj.Index] &&
                     r.Part - SceneDb.ObjPartFirst[r.Obj.Index] < SceneDb.ObjPartCount[r.Obj.Index] &&
                     r.Face >= 0 && r.Surface >= 0 && r.Surface != HitQuery.TriggerSurface &&
                     Finite(r.Point) && r.Point.Z <= from.Z + .001f && r.Point.Z >= to.Z - .001f &&
                     Vector2.DistanceSquared(new(r.Point.X,r.Point.Y),new(at.X,at.Y)) <= .000001f)
            {
                var model = SceneDb.PartModel[r.Part];
                int node = SceneDb.PartNode[r.Part];
                if (model != null && (uint)node < (uint)model.Nodes.Length &&
                    model.Nodes[node].Aabb is { } aabb && aabb.Tree.Length > 0)
                    status = "walkmesh_hit";
            }
            return new(status, r.Point, r.Normal, r.Surface, r.Part, r.Face, scene.Value);
        }
        catch (Exception) { return new("query_fault"); }
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
