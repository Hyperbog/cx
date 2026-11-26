// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/ActionFeedback.cs
// PURPOSE: Damage feedback and reticle hit crescents
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Reticle displays a centered dot and two compact side arcs for 160 ms on a
//   selected-target damage event. The event is not guaranteed to identify
//   which actor caused damage. No new combat packet is sent.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Camera;

/// <summary>
/// Crosshair feedback for the action camera: <see cref="MouseLookService"/> owns it, <see cref="Crosshair"/> draws it.
/// Chaining the one player-&gt;server send funnel latches <c>{kind, target, when}</c> a round trip before the server's
/// echo; the creature's AI state and <c>CGuiInGame</c>'s action-queue panel are the lagging half. The latch is written
/// on the input thread and read on the render thread, so a torn read costs one wrong glyph. Allocates nothing.
/// </summary>
internal static unsafe class ActionFeedback
{
    private static bool _ready;
    private sealed class HitMarkState { internal long Last; }
    private static readonly HitMarkState HitMark = new();
    private const long HitMarkerMs = 160;
    private static SendPlayerToServerMessageFn* _next;

    // ---- the wire frame ----

    /// <summary>Major 0x06 is player input; 0x26 is the shift-click party-group twin of the same payload, same
    /// minor and the same leading object id.</summary>
    private const byte MajorInput = 0x06, MajorGroupInput = 0x26;  // not-an-offset: a message opcode

    /// <summary>The action minors this feature names. <c>CancelAction</c> is the "I changed my mind" signal and
    /// clears the latch instead of setting it.</summary>
    private const byte MinorAttack = 0x02, MinorUseFeat = 0x06, MinorUseSkill = 0x07, MinorDialog = 0x08,  // not-an-offset: a message opcode
                       MinorUseItem = 0x09, MinorUseObject = 0x0b, MinorCastSpell = 0x12,
                       MinorCancelAction = 0x14, MinorTargetSelected = 0x25;

    /// <summary>Bytes <c>CNWMessage::CreateWriteMessage</c> reserves before the first payload byte:
    /// <c>'p'</c>, major, minor and the u32 byte-stream length at <c>buf+3</c>.</summary>
    private const int HeaderBytes = 7;

    /// <summary>CastSpell's composed flags byte (after DWORD spell + 3 BYTEs); bit 0 = an object target follows.</summary>
    private const int CastSpellFlagsOff = 14;

    /// <summary>The one AI-state byte whose meaning two independent call sites pin down.</summary>
    private const byte AiStateAttacking = 2;

    // ---- the latch ----

    private static byte _kind;
    private static uint _target;
    private static long _at;
    private static int _sent;
    private static int _queued;
    private static bool _attacking;
    private static uint _aiTarget;

    // ---- the reticle additions, in px at GUI scale 1 ----

    /// <summary>How long the "order sent" pulse lives, and the radii it travels between.</summary>
    private const long PulseMs = 350;
    private const float PulseFrom = 6f, PulseTo = 22f, PulseTick = 7f;
    /// <summary>Half-size of the optional persistent attack ring.</summary>
    private const float AttackRing = 17f;
    /// <summary>The attack red, the same verb colour the reticle takes from the attack cursor.</summary>
    private const float AttackR = 1f, AttackG = 0.28f, AttackB = 0.22f;

    /// <summary>Arm the feedback: gate the three classes it reads and chain the send funnel. A refusal leaves
    /// the action camera and its crosshair untouched -- one fewer thing drawn, nothing broken.</summary>
    internal static void Init(ExtenderLog log, HookService hooks)
    {
        if (!EngineLayout.Require(log, "MouseLook.feedback", "CGuiInGame", "CPanelActionQueue", "CNWCCreature")) return;
        if (!SymbolMap.Require(log, "MouseLook.feedback", SymbolMap.SendPlayerToServerMessage)) return;

        _next = hooks.ChainSendPlayerToServerMessage(&OnSend, 50);
        _ready = _next != null;
        log.Info(_ready ? "MouseLook.feedback: chained CNWCMessage::SendPlayerToServerMessage; crosshair answers the action queue"
                        : "MouseLook.feedback: standing down (SendPlayerToServerMessage chain refused)");
    }

    /// <summary>Every player-&gt;server message. Cold -- one call per player action, no per-frame caller -- and it
    /// forwards unconditionally: this reads the frame the engine already built and changes nothing.</summary>
    [UnmanagedCallersOnly]
    private static int OnSend(IntPtr self, byte major, byte minor, byte* buf, uint len)
    {
        if (buf != null && (major == MajorInput || major == MajorGroupInput)) Latch(minor, buf, len);
        // D2: pass through the existing native send hook exactly once, unchanged.
        long d2Begin = major == MajorInput ? Stopwatch.GetTimestamp() : 0;
        int result = (*_next)(self, major, minor, buf, len);
        if (d2Begin != 0)
            CameraDriveService.D2ObserveSend(major, minor, len, result, d2Begin, Stopwatch.GetTimestamp());
        return result;
    }

    private static void Latch(byte minor, byte* buf, uint len)
    {
        if (minor == MinorCancelAction) { _at = 0; _kind = 0; _target = 0; return; }
        if (len < HeaderBytes) return;
        // GetWriteMessage stamps the BYTE-stream total here; the bit stream is memcpy'd past it, so bytes <= len.
        uint bytes = MemoryMarshal.Read<uint>(new ReadOnlySpan<byte>(buf, (int)len).Slice(3));
        if (bytes < HeaderBytes || bytes > len) return;
        int off = TargetOffset(minor, buf, bytes);
        if (off < 0) return;
        uint oid = off > 0 && off + 4 <= bytes ? MemoryMarshal.Read<uint>(new ReadOnlySpan<byte>(buf, (int)len).Slice(off)) : TypingSignal.ObjectInvalid;
        _target = oid == TypingSignal.ObjectInvalid ? 0u : oid;
        _kind = minor;
        _at = Environment.TickCount64;
        _sent++;
    }

    /// <summary>Where this minor's target object id sits in the frame, past <see cref="HeaderBytes"/>: 0 when the
    /// action carries none, -1 for a minor this feature does not name. Byte-stream writers advance the cursor in
    /// call order and the <c>BOOL</c>s do not, so a row is whatever precedes the OBJECTID: skill BYTE+BYTE, feat
    /// WORD+WORD, item OBJECTID+BYTE (+a sub-item BYTE unless 0xff), cast DWORD+3 BYTEs then a flag-gated object.</summary>
    private static int TargetOffset(byte minor, byte* buf, uint bytes)
    {
        switch (minor)
        {
            case MinorAttack:
            case MinorDialog:
            case MinorUseObject:
            case MinorTargetSelected:
                return 7;
            case MinorUseSkill: return 9;
            case MinorUseFeat: return 11;
            case MinorUseItem:
                // The sub-item BYTE is the one bit of payload length that moves the target, so the size decides.
                return (bytes - HeaderBytes) switch { 9 or 21 => 12, 10 or 22 => 13, _ => 0 };
            case MinorCastSpell:
                return bytes > CastSpellFlagsOff && (buf[CastSpellFlagsOff] & 1) != 0 ? CastSpellFlagsOff + 1 : 0;
            default: return -1;
        }
    }

    // The game reported damage on a current target, not a client-side guess based on a swing.
    // The server damage event does not contain an attacker here: a party member may have dealt it.
    internal static void NoteDamage(uint victim)
    {
        if (!CameraModes.Driving.HitMarker.V || victim == 0) return;
        long now = Environment.TickCount64;
        bool attackingVictim = _attacking && _aiTarget == victim;
        bool recentTargetedAction = _at != 0 && now - _at >= 0 && now - _at < 5000
            && _target == victim && (_kind == MinorAttack || _kind == MinorCastSpell ||
                _kind == MinorUseFeat || _kind == MinorUseItem);
        if (attackingVictim || recentTargetedAction)
            System.Threading.Interlocked.Exchange(ref HitMark.Last, now);
    }

    // ---- the per-frame read + draw ----

    /// <summary>Optional order pulse, attack ring and short damage crescents inside the reticle batch.
    /// Pending action count is still tracked for reports, but no square is drawn below the dot.
    /// Native targeting and combat behavior are unchanged.</summary>
    internal static void Draw(VertexBatch b, float cx, float cy, float s, float t)
    {
        if (!_ready || (!MouseLook.Feedback.V && !CameraModes.Driving.HitMarker.V)) return;
        int queued = Pending();
        IntPtr cre = CameraService.PlayerCreature();
        var creature = new CNWCCreature(cre);
        bool attacking = cre != IntPtr.Zero && creature.AiState == AiStateAttacking;
        _queued = queued;
        _attacking = attacking;
        _aiTarget = attacking ? creature.AiTarget : 0u;

        long age = Environment.TickCount64 - _at;
        if (MouseLook.Feedback.V && _at != 0 && (ulong)age < PulseMs)
        {
            float p = age / (float)PulseMs;
            float d = (PulseFrom + (PulseTo - PulseFrom) * p) * s, half = PulseTick * s * 0.5f, al = (1f - p) * 0.85f;
            b.Rect(cx - half, cy - d - t, cx + half, cy - d + t, 1f, 1f, 1f, al);
            b.Rect(cx - half, cy + d - t, cx + half, cy + d + t, 1f, 1f, 1f, al);
            b.Rect(cx - d - t, cy - half, cx - d + t, cy + half, 1f, 1f, 1f, al);
            b.Rect(cx + d - t, cy - half, cx + d + t, cy + half, 1f, 1f, 1f, al);
        }
        if (MouseLook.Feedback.V && attacking && !CameraModes.Driving.CleanReticle.V)
            Ring(b, cx, cy, AttackRing * s, t, AttackR, AttackG, AttackB, 0.55f);
        if (CameraModes.Driving.HitMarker.V)
        {
            long hitAt = System.Threading.Volatile.Read(ref HitMark.Last);
            long ms = Environment.TickCount64 - hitAt;
            if (hitAt != 0 && ms >= 0 && ms < HitMarkerMs)
            {
                // Fix14D-R1: only the left/right shoulders of a small circle flash around the dot.
                // Keep the top and bottom entirely open. The hit timestamp is target-damage,
                // not proven local-player damage (the inbound event does not name an attacker).
                float fade = 1f - ms / (float)HitMarkerMs;
                HitCrescents(b, cx, cy, s, fade * fade * 0.95f);
            }
        }
    }

    /// <summary>Brief, symmetric side arcs, shaped like ( dot ). Center and vertical gaps stay clear.
    /// Uses solid atlas rectangles, matching Crosshair's existing rounded-by-sampling ring technique.
    /// Nothing allocates or changes the native picking/attack state.</summary>
    private static void HitCrescents(VertexBatch b, float cx, float cy, float scale, float alpha)
    {
        const int steps = 9;
        float radius = 9f * scale, half = 1.05f * scale, shadow = 0.5f * scale;
        for (int side = 0; side != 2; side++)
        {
            for (int i = 0; i < steps; i++)
            {
                // +/-55 degrees at the right, then rotate 180 degrees for the left.
                // No samples near +/-90 degrees: no top/bottom arc.
                float degrees = -55f + i * (110f / (steps - 1)) + side * 180f;
                float angle = degrees * (MathF.PI / 180f);
                float x = cx + MathF.Cos(angle) * radius;
                float y = cy + MathF.Sin(angle) * radius;
                b.Rect(x - half - shadow, y - half - shadow, x + half + shadow,
                       y + half + shadow, 0f, 0f, 0f, alpha * 0.45f);
                b.Rect(x - half, y - half, x + half, y + half, AttackR, AttackG, AttackB, alpha);
            }
        }
    }

    /// <summary>Actions the client still believes are outstanding: the "Current" slot plus the queued list.
    /// <c>AddAction</c> fills the slot first, so the count alone reads 0 for the common one-action case.</summary>
    private static int Pending()
    {
        IntPtr gui = GameGate.InGameGui;
        if (gui == IntPtr.Zero) return 0;
        CPanelActionQueue panel = new CGuiInGame(gui).ActionQueuePanel;
        if (panel.IsNull) return 0;
        int n = panel.QueuedCount;
        return (panel.Current != IntPtr.Zero ? 1 : 0) + (n > 0 ? n : 0);
    }

    /// <summary>A square outline of half-size <paramref name="k"/> from four thin rects.</summary>
    private static void Ring(VertexBatch b, float cx, float cy, float k, float t, float r, float g, float bl, float a)
    {
        b.Rect(cx - k, cy - k - t, cx + k, cy - k + t, r, g, bl, a);
        b.Rect(cx - k, cy + k - t, cx + k, cy + k + t, r, g, bl, a);
        b.Rect(cx - k - t, cy - k, cx - k + t, cy + k, r, g, bl, a);
        b.Rect(cx + k - t, cy - k, cx + k + t, cy + k, r, g, bl, a);
    }

    private static string Name(byte minor) => minor switch
    {
        MinorAttack => "attack",
        MinorUseFeat => "feat",
        MinorUseSkill => "skill",
        MinorDialog => "talk",
        MinorUseItem => "item",
        MinorUseObject => "use",
        MinorCastSpell => "cast",
        MinorTargetSelected => "target",
        _ => "-",
    };

    /// <summary>The mode status fragment. The queue and combat halves are the last drawn frame's sample --
    /// the panel walk belongs on the render thread, not on whichever thread typed the command.</summary>
    internal static string Report() => !_ready ? "feedback off (stood down)"
        : FormattableString.Invariant($"feedback {(MouseLook.Feedback.V ? "on" : "off")}, {_sent} order(s), last {Name(_kind)} on 0x{_target:x} {(_at == 0 ? "(none)" : $"{Environment.TickCount64 - _at}ms ago")}; {_queued} pending, {(_attacking ? $"attacking 0x{_aiTarget:x}" : "not attacking")}");
}
