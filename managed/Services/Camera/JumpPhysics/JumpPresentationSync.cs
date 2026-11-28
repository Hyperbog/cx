using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Services.Anim;

namespace CX.Services.Camera;

/// <summary>Reconcile the validated native jump write with Alloy's placement
/// mirror, then compose the existing pose. Never advance an animation clock,
/// write a bone, change the camera, or add displacement to an existing pose.</summary>
internal static class JumpPresentationSync
{
    private const float PositionToleranceSquared = 0.0001f; // 1 cm

    private static bool Finite(Vector3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    private static bool Near(Vector3 a, Vector3 b) =>
        Finite(a) && Finite(b) && Vector3.DistanceSquared(a, b) <= PositionToleranceSquared;

    private static bool Resolve(IntPtr player, out DbId obj, out nint shell, out int root)
    {
        obj = default; shell = 0; root = -1;
        if (!PoseRig.Db || player == IntPtr.Zero) return false;
        IntPtr body = PoseService.GobOf(player);
        if (body == IntPtr.Zero) return false;
        shell = Facade.GobBlock(body);
        if (shell == 0) return false;
        obj = Facade.ObjectId(body);
        if (!SceneDb.Objects.Valid(obj) || Facade.ObjectShell(obj) != shell) return false;
        int i = obj.Index;
        if (!SceneDb.ObjAttachTo[i].IsNull || SceneDb.ObjPartCount[i] <= 0) return false;
        root = SceneDb.ObjPartFirst[i];
        return (uint)root < (uint)SceneDb.MaxParts && SceneDb.Parts.LiveAt(root)
            && SceneDb.PartObj[root].Value == obj.Value;
    }

    private static void Record(string kind, DbId obj, int root, Vector3 actor, Vector3 shellPos)
    {
        if (!JumpTimingTrace.Active) return;
        DbAnim.PoseWorld(root, out Vector3 rootWorld, out _);
        JumpTimingTrace.Mark(kind, actor, shellPos, SceneDb.ObjPos[obj.Index],
            x: SceneDb.ObjWorld[obj.Index].M43, y: rootWorld.Z, z: SceneDb.PartPos[root].Z);
    }

    // Called only after the existing native write, including its landing and
    // walk-bridge writes. The engine's actual result, not the requested height,
    // is authoritative. If the shell hasn't accepted it, leave it alone.
    internal static void AfterWrite(IntPtr player, Vector3 target)
    {
        if (!Resolve(player, out DbId obj, out nint shell, out int root)) return;
        Vector3 actor = new CNWCObject(player).Position;
        Vector3 shellPos = Facade.ShellPos(shell);
        Record("sync_before", obj, root, actor, shellPos);
        if (!Near(actor, target) || !Near(shellPos, actor) || !Finite(SceneDb.ObjPos[obj.Index]))
        {
            JumpTimingTrace.Mark("sync_refuse", actor, shellPos, target);
            return;
        }
        if (SceneDb.ObjPos[obj.Index] != shellPos)
        {
            // The validated native shell already carries this position. Mirror
            // it once, without reissuing a native setter or modifying rotation.
            SceneDb.ObjPos[obj.Index] = shellPos;
            SceneDb.Touch(obj, Dirty.Xform);
            JumpTimingTrace.Mark("sync_mirror", actor, shellPos);
        }
        // Existing CX compose path propagates object placement to its parts
        // and attached children; it does not run the animation queue again.
        _ = DbAnim.ObjectWorld(obj);
        Record("sync_after", obj, root, actor, shellPos);
    }

    // Observer at the subsequent world-camera solve. Deliberately does not
    // compose: distinguish a later overwrite from authored local root motion.
    internal static void ObserveCamera(IntPtr player, Vector3 actor)
    {
        if (!JumpTimingTrace.Active || !Resolve(player, out DbId obj, out nint shell, out int root)) return;
        Record("sync_camera", obj, root, actor, Facade.ShellPos(shell));
    }
}
