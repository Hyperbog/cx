// BEGIN FIX14D-J4 PUBLICATION NOTES -- comments only; original source follows unchanged.
// MODULE: managed/Services/Camera/AnimationCameraSettings.cs
// PURPOSE: Animation camera tuning
// SOURCE: validated local NWNCX checkpoint with Fix14D J4 extensions.
// DEPENDENCY: must compile as part of the matching full NWNCX project;
//             source overlay alone is not an independent executable.
//
// DESIGN / SAFETY INVARIANTS:
//   Controls opt-in camera responses without changing game actor animation or
//   input. Settings must retain conservative defaults.
//
// Provenance: base commit b4b43a54ed68, local mod
// checkpoint 2470f0d27be0, subsequent C3C/D1-D5/R3/J4 overlays.
// This header is explanatory only. The code from the source checkout
// appears byte-for-byte below it after newline/BOM normalization.
// END FIX14D-J4 PUBLICATION NOTES

using CX.Framework;

namespace CX.Services.Camera;

/// <summary>First-person animation response. All values adjustable at runtime via /knobs.</summary>
internal static class AnimationCameraSettings
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Roll, HeadGuard;
    internal static readonly Knob<float> Strength, GuardLimit;

    static AnimationCameraSettings()
    {
        G = KnobGroup.Declare("animcam", "Controls", "Camera", order: 516,
                              title: "First-person animation camera");
        Enabled = G.Toggle("enabled", "Animation camera", true,
                           help: "Switch off all experimental first-person animation motion without rebuilding.");
        Strength = G.Slider("strength", "Motion strength", 0f, 100f, 5f, 25f, "%",
                            help: "Amount of normalized torso animation transferred to the camera.");
        Roll = G.Toggle("roll", "Subtle camera roll", true,
                        help: "Roll is capped to 1.26 degrees; spinning animations never rotate the view.");
        HeadGuard = G.Toggle("headguard", "Adaptive head clearance", true,
                             help: "Small forward motion during a lunge when the rig's head comes forward.");
        GuardLimit = G.Slider("clearance", "Maximum forward clearance", 0f, 0.08f, 0.005f, 0.05f, "m",
                              Tier.Advanced, help: "Visual-only: does not hide helmet meshes or bypass world walls.");
    }
}
