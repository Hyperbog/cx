// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/FirstPersonModelProbe.cs
// PURPOSE: Bounded model diagnostic
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Head/body/attachment pivots are evidence for identifying geometry, not
//   proof of exact mesh surface clearance. Logging must remain opt-in and
//   bounded.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>
/// Fix14B stage 1: read-only local first-person part diagnostic.
/// It never changes visibility, pose, equipment, transforms or draw order.
/// Values describe part pivots, NOT the occupied bounds of a skinned mesh.
/// </summary>
internal static class FirstPersonModelProbe
{
    private const int MaxSamples = 8;
    private struct Sample
    {
        internal int Part;
        internal float DistanceSq;
        internal bool Head, Attached;
        internal string Model, Node;
    }

    private static readonly Sample[] _near = new Sample[MaxSamples];
    private static int _count, _all, _head, _attachments, _unmapped;
    private static int _lastAll, _lastHead, _lastAttachments, _lastUnmapped;
    private static int _lastCount;
    private static uint _player;
    private static bool _active;
    private static long _frames, _noModel, _noHead;
    private static string _status = "disabled";
    private static ExtenderLog? _log;
    private static long _lastLogged;
    private static int _logs;

    internal static void Attach(ExtenderLog log) => _log = log;

    internal static bool Active => _active;

    internal static void Begin(uint cameraFollow, bool enabled, uint player, long publication,
                               int headPart, bool firstPerson)
    {
        _active = false;
        _status = enabled ? "not-ready" : "disabled";
        _count = _all = _head = _attachments = _unmapped = 0;
        _mappedHeadPart = headPart;
        if (!enabled) _lastCount = 0;
        if (!enabled || !firstPerson || cameraFollow == 0 || player == 0 || cameraFollow != player)
        {
            if (enabled) _status = "camera-mismatch-or-gate";
            return;
        }
        long now = Stopwatch.GetTimestamp();
        if (publication == 0 || now - publication < 0 || now - publication > Stopwatch.Frequency / 5)
        { _status = "stale-eye"; return; }
        _player = player;
        if (headPart < 0) _noHead++;
        _active = true;
        _status = "sampling";
        _frames++;
    }

    internal static void Observe(DbId owner, int part, bool direct, bool attached, bool mappedHead)
    {
        if (!_active || (!direct && !attached) || !SceneDb.Objects.Valid(owner)
            || (uint)part >= (uint)SceneDb.MaxParts || !SceneDb.Parts.LiveAt(part)) return;
        _all++;
        if (mappedHead) _head++;
        if (attached) _attachments++;
        var model = SceneDb.PartModel[part];
        int node = SceneDb.PartNode[part];
        if (model == null || (uint)node >= (uint)model.Nodes.Length)
        { _unmapped++; _noModel++; return; }
        string nodeName = model.Nodes[node].Name ?? "?";
        string modelName = model.Name ?? "?";

        // Sort part PIVOTS by their proximity to the player's mapped head.
        // A pivot can lie well outside its mesh; this is diagnostic, not a collision test.
        float d2 = float.MaxValue;
        // The player's head part is deliberately not inferred for unknown rigs.
        int head = _mappedHeadPart;
        if ((uint)head < (uint)SceneDb.MaxParts && SceneDb.Parts.LiveAt(head))
        {
            Vector3 a = SceneDb.PartWorld[part].Translation;
            Vector3 b = SceneDb.PartWorld[head].Translation;
            d2 = Vector3.DistanceSquared(a, b);
            if (!float.IsFinite(d2)) d2 = float.MaxValue;
        }
        int index = _count;
        if (index == MaxSamples && d2 >= _near[MaxSamples - 1].DistanceSq) return;
        if (index < MaxSamples) _count++;
        else index = MaxSamples - 1;
        while (index > 0 && d2 < _near[index - 1].DistanceSq)
        { _near[index] = _near[index - 1]; index--; }
        _near[index] = new Sample
        {
            Part = part, DistanceSq = d2, Head = mappedHead,
            Attached = attached, Model = modelName, Node = nodeName
        };
    }

    private static int _mappedHeadPart = -1;

    internal static void End()
    {
        if (!_active) return;
        _lastAll = _all;
        _lastHead = _head;
        _lastAttachments = _attachments;
        _lastUnmapped = _unmapped;
        _lastCount = _count;
        _active = false;
        long now = Stopwatch.GetTimestamp();
        if (_log != null && _logs < 24 && (_lastLogged == 0 || now - _lastLogged >= Stopwatch.Frequency * 8))
        {
            _lastLogged = now;
            _logs++;
            _log.Always(Report());
        }
    }

    internal static string Report()
    {
        string report = $"modelprobe: {_status} frames={_frames} local={_player} " +
                        $"parts={_lastAll} mappedHead={_lastHead} attached={_lastAttachments} " +
                        $"unmapped={_lastUnmapped} unknownModel={_noModel} noHeadPasses={_noHead}";
        for (int i = 0; i < _lastCount; i++)
        {
            Sample s = _near[i];
            string distance = s.DistanceSq == float.MaxValue ? "?" : MathF.Sqrt(s.DistanceSq).ToString("0.00");
            report += $"\n[fpmodel] part={s.Part} model={s.Model} node={s.Node} " +
                      $"head={(s.Head ? 1 : 0)} attachment={(s.Attached ? 1 : 0)} " +
                      $"pivotToHead={distance}m";
        }
        return report;
    }
}
