// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/FirstPersonVisibility.cs
// PURPOSE: Local player render visibility policy
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   C3C adaptive eye clearance avoids head/torso occlusion without
//   indiscriminately hiding arms, feet, weapons or armor. Keep local-player
//   identity and render-pass gates.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Framework;
using CX.Services.Anim;

namespace CX.Services.Camera;

/// <summary>
/// Fix10: local-player-only, DB world-COLOUR draw filtering. Never touches engine
/// visibility flags, model pose, equipment, selection, collision or shadow records.
/// </summary>
internal static class FirstPersonVisibility
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<int> Mode;
    internal static readonly Knob<bool> Probe;
    private static uint _body;
    private static int _head = -1;
    private static long _published;
    private static int _activeMode;
    private static int _seen, _hidden, _attached, _headHidden, _unknown;
    private static int _lastSeen, _lastHidden, _lastAttached, _lastHeadHidden, _lastUnknown;
    private static long _passes, _noHead;
    private static string _modelName = "-", _status = "not armed";
    // Camera solve publishes a stable versioned correction. The Alloy world
    // colour pass latches it once; no draw-thread creature or Gob dereferences.
    private static int _shiftSequence;
    private static int _shiftCoreValidated;
    private static uint _shiftBody;
    private static float _shiftX, _shiftY, _shiftZ;
    private static long _shiftStamp;
    private static Vector3 _colorShift;
    private static bool _colorShiftActive;
    private static long _renderShiftPasses, _renderShiftParts, _rejectedShifts;

    static FirstPersonVisibility()
    {
        G = KnobGroup.Declare("fpvisibility", "Controls", "Camera", order: 517,
                              title: "First-person visibility test");
        Mode = G.Slider("mode", "Local-character visibility", 0, 2, 1, 0, "", Tier.ChatOnly,
            help: "0 normal; 1 exclude mapped head subtree and attached headgear; 2 hide the local character and mapped attachments (diagnostic only). Alloy renderer required.");
        Probe = G.Toggle("probe", "Capture first-person model-part diagnostics", false,
            tier: Tier.ChatOnly,
            help: "Read-only pivot/attachment samples in cx.log every 8s, up to 24 samples per run. No render changes.");
    }

    /// <summary>Called ONLY from the active Fix9 first-person camera solve.</summary>
    internal static void Publish(IntPtr creature)
    {
        uint body = 0;
        int head = -1;
        string name = "-";
        if (creature != IntPtr.Zero && PoseRig.Db)
        {
            IntPtr gob = PoseService.GobOf(creature);
            if (gob != IntPtr.Zero)
            {
                DbId obj = Facade.ObjectId(gob);
                if (SceneDb.Objects.Valid(obj))
                {
                    body = obj.Value;
                    name = SceneDb.ObjSeamModelName[obj.Index] ?? "-";
                    PoseService.TryFirstPersonHead(creature, out head);
                }
            }
        }
        // Publish the identity last. The scene's own handles have generation bits,
        // so a recycled object ID cannot accidentally match an old player.
        _modelName = name;
        Volatile.Write(ref _head, head);
        Volatile.Write(ref _body, body);
        Volatile.Write(ref _published, Stopwatch.GetTimestamp());
    }

    /// <summary>Camera-thread publication after the FINAL wall constraint.
    /// Coordinates are a VISUAL offset only, not gameplay movement.</summary>
    internal static void PublishEyeCollision(IntPtr creature, Vector3 delta, bool validatedCore = false)
    {
        if (creature == IntPtr.Zero || !float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || !float.IsFinite(delta.Z))
        { delta = Vector3.Zero; validatedCore = false; }
        uint body = Volatile.Read(ref _body);
        System.Threading.Interlocked.Increment(ref _shiftSequence);  // odd: writing
        Volatile.Write(ref _shiftBody, body);
        Volatile.Write(ref _shiftCoreValidated, validatedCore ? 1 : 0);
        Volatile.Write(ref _shiftX, delta.X);
        Volatile.Write(ref _shiftY, delta.Y);
        Volatile.Write(ref _shiftZ, delta.Z);
        Volatile.Write(ref _shiftStamp, Stopwatch.GetTimestamp());
        System.Threading.Interlocked.Increment(ref _shiftSequence);  // even: stable
    }

    // The run-root generation-checked handle groups the body with its
    // attachment models (helm, cloak, weapon); do not touch other actors.
    internal static bool LocalRenderPart(DbId owner)
    {
        uint body = Volatile.Read(ref _body);
        if (body == 0 || !SceneDb.Objects.Valid(owner)) return false;
        // Keep forcing local records through the full stage after the camera
        // exits first-person; otherwise settled caching can retain the final
        // wall-offset matrix on an otherwise idle character indefinitely.
        return owner.Value == body || SceneDb.ObjRunRoot[owner.Index].Value == body;
    }

    internal static Vector3 LocalEyeShift(DbId owner)
    {
        if (!_colorShiftActive || !LocalRenderPart(owner)) return Vector3.Zero;
        Interlocked.Increment(ref _renderShiftParts);
        return _colorShift;
    }

    /// <summary>Call only around the main DB world color pass, never shadow/GUI.</summary>
    internal static void BeginWorldColor(uint cameraFollow)
    {
        _activeMode = 0;
        _colorShiftActive = false;
        _colorShift = Vector3.Zero;
        // Acquire a complete camera-frame correction; reject a raced writer.
        if (HeadBoneCamera.EyeLock.V && FirstPersonWorldGuard.FirstPersonActive
            && cameraFollow != 0 && cameraFollow == Volatile.Read(ref _body))
        {
            int a = Volatile.Read(ref _shiftSequence);
            if ((a & 1) == 0)
            {
                uint who = Volatile.Read(ref _shiftBody);
                float x = Volatile.Read(ref _shiftX), y = Volatile.Read(ref _shiftY), z = Volatile.Read(ref _shiftZ);
                int coreValidated = Volatile.Read(ref _shiftCoreValidated);
                long stamp = Volatile.Read(ref _shiftStamp);
                if (a == Volatile.Read(ref _shiftSequence) && who == cameraFollow
                    && stamp != 0 && Stopwatch.GetTimestamp() - stamp < Stopwatch.Frequency / 5)
                {
                    Vector3 d = new(x, y, z);
                    float len = d.Length();
                    if (float.IsFinite(len) && len > 0f)
                    {
                        float max = HeadBoneCamera.VisualShiftMax.V;
                        // Preserve the original wall-only cap. Only the same-frame,
                        // solver-validated core pose may request a larger visual shift.
                        if (coreValidated == 0 && len > max)
                        { d *= max / len; _rejectedShifts++; }
                        _colorShift = d;
                        _colorShiftActive = true;
                        _renderShiftPasses++;
                    }
                }
            }
        }
        _seen = _hidden = _attached = _headHidden = _unknown = 0;
        _status = "inactive";
        FirstPersonModelProbe.Begin(cameraFollow, Probe.V, Volatile.Read(ref _body),
            Volatile.Read(ref _published), Volatile.Read(ref _head),
            CameraService.Mode == 2 && !CameraService.GateUp && !FreeCameraService.Active &&
            CameraModes.Driving.FirstPerson.V);
        int requested = Mode.V;
        if (requested is < 1 or > 2) { _status = "normal mode"; return; }
        if (cameraFollow == 0) { _status = "no camera target"; return; }
        if (CameraService.Mode != 2 || CameraService.GateUp || FreeCameraService.Active
            || !CameraModes.Driving.FirstPerson.V) { _status = "non-first-person gate"; return; }
        long tick = Volatile.Read(ref _published);
        if (tick == 0 || Stopwatch.GetTimestamp() - tick > Stopwatch.Frequency / 5)
        { _status = "no recent first-person pose"; return; }
        if (cameraFollow != Volatile.Read(ref _body)) { _status = "camera target mismatch"; return; }
        _activeMode = requested;
        _status = "active";
        _passes++;
        if (_head < 0) _noHead++;
    }

    /// <summary>Invoked after DB scene/LOD validity, before issuing a color draw.</summary>
    internal static bool Exclude(DbId owner, int part)
    {
        if (!SceneDb.Objects.Valid(owner) || (_activeMode == 0 && !FirstPersonModelProbe.Active)) return false;
        uint local = Volatile.Read(ref _body);
        bool direct = owner.Value == local;
        bool attached = false, headAttachment = false;
        if (!direct) attached = AttachedToPlayer(owner, local, out headAttachment);
        if (!direct && !attached) return false;
        bool headPart = direct ? HeadBranch(part) : headAttachment;
        FirstPersonModelProbe.Observe(owner, part, direct, attached, headPart);
        if (_activeMode == 0) return false; // Diagnostic only: mode zero NEVER hides anything.
        _seen++;
        if (attached) _attached++;
        if (_activeMode == 1 && !headPart && attached) _unknown++;
        bool hide = _activeMode == 2 || headPart;
        if (hide)
        {
            _hidden++;
            if (headPart) _headHidden++;
        }
        return hide;
    }

    /// <summary>Part tree walk: only a mapped head and its descendants count.
    /// Never substitute neck, torso or guessed height for an unsupported head.</summary>
    private static bool HeadBranch(int part)
    {
        int head = Volatile.Read(ref _head);
        if (head < 0) return false;
        for (int i = 0; i < 48 && (uint)part < SceneDb.MaxParts && SceneDb.Parts.LiveAt(part); i++)
        {
            if (part == head) return true;
            part = SceneDb.PartParent[part];
            if (part < 0) return false;
        }
        return false;
    }

    /// <summary>Only explicit database attachments. If a helmet is attached by an
    /// untracked engine link, fail open and record it as a compatibility gap.</summary>
    private static bool AttachedToPlayer(DbId objectId, uint player, out bool head)
    {
        head = false;
        DbId child = objectId;
        for (int hops = 0; hops < 12 && SceneDb.Objects.Valid(child); hops++)
        {
            DbId parent = SceneDb.ObjAttachTo[child.Index];
            if (!SceneDb.Objects.Valid(parent)) return false;
            if (parent.Value == player)
            {
                head = HeadBranch(SceneDb.ObjAttachPart[child.Index]);
                return true;
            }
            child = parent;
        }
        return false;
    }

    internal static void EndWorldColor()
    {
        _lastSeen = _seen; _lastHidden = _hidden; _lastAttached = _attached;
        _lastHeadHidden = _headHidden; _lastUnknown = _unknown;
        FirstPersonModelProbe.End();
        _activeMode = 0;
        _colorShiftActive = false;
        _colorShift = Vector3.Zero;
    }

    internal static string Diagnostics() =>
        $"fpvisibility: mode={Mode.V} status={_status} pass={_passes} " +
        $"model={_modelName} player={Volatile.Read(ref _body)} " +
        $"headPart={Volatile.Read(ref _head)} seen={_lastSeen} hidden={_lastHidden} " +
        $"headHidden={_lastHeadHidden} attachments={_lastAttached} " +
        $"nonHeadAttachments={_lastUnknown} missingHeadPasses={_noHead} " +
        $"renderRig={(_colorShiftActive ? 1 : 0)} offset={_colorShift.Length():0.000}m parts={_renderShiftParts} passes={_renderShiftPasses} capped={_rejectedShifts} " +
        "(Alloy world color only; local visual model follows wall-constrained eye)" + "\n" + FirstPersonModelProbe.Report();
}
