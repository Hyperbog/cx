// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/JumpPhysics/JumpWalkBridgeService.cs
// PURPOSE: Jump-to-native-walk bridge
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   The hook calls the original native walking chain first, then reapplies
//   the already-integrated airborne Z. It does not advance gravity again and
//   returns the unmodified walk result.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>Reapply airborne Z after NWN completes a walking update; never step gravity here.</summary>
public sealed unsafe class JumpWalkBridgeService : IExtenderService
{
    private static WalkUpdateLocationFn* _next;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        JumpPositionService.ConfigureTrace(log);
        if (!EngineLayout.Require(log, "JumpWalkBridge", "CNWCObject", "CNWCCreature")) return;
        if (!SymbolMap.Require(log, "JumpWalkBridge", SymbolMap.WalkUpdateLocation)) return;
        // Priority 70 wraps StepGuard (50) and existing pose weight (60).
        _next = hooks.ChainWalkUpdateLocation(&OnWalkUpdateLocation, 70);
        if (_next == null) log.Info("JumpWalkBridge: hook refused, airborne walking unchanged");
        else log.Info("JumpWalkBridge: post-walk airborne Z bridge armed");
    }

    [UnmanagedCallersOnly]
    private static int OnWalkUpdateLocation(IntPtr creature)
    {
        int result = (*_next)(creature);
        try { JumpPositionService.AfterWalk(creature); }
        catch (Exception) { /* area changes can invalidate a creature mid-frame */ }
        return result;
    }
}
