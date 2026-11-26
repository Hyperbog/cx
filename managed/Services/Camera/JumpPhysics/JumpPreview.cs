// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/JumpPhysics/JumpPreview.cs
// PURPOSE: Jump state preview and visual diagnostics
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Read-only preview of jump state; avoid adding physics ticks or altering
//   authoritative motion.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using System.Threading;

namespace CX.Services.Camera;

/// <summary>
/// Spacebar input shared with JumpPositionService. This file deliberately does not
/// move the camera or renderer; those remain tied to the game's creature position.
/// </summary>
internal static class JumpPreview
{
    private static int _spaceEdge;
    private const uint KeyDown = 0x300, KeyUp = 0x301, Space = 32;

    internal static bool CaptureKey(uint kind, uint sym, byte repeat, bool actionHeld)
    {
        if (sym != Space || !actionHeld || !CameraModes.Driving.JumpPreview.V
            || !CameraModes.Driving.FirstPerson.V || CameraService.Mode != 2)
            return false;
        if (kind == KeyDown)
        {
            if (repeat == 0) Interlocked.Exchange(ref _spaceEdge, 1);
            return true;
        }
        return kind == KeyUp;
    }

    internal static bool Take() => Interlocked.Exchange(ref _spaceEdge, 0) != 0;
    internal static void Reset() => Interlocked.Exchange(ref _spaceEdge, 0);
}
