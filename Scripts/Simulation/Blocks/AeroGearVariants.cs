#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>One retractable gear block: the vanilla suspension it copies (CornerModule: its wheel prefab is the one we
/// spawn while folding), the wheel's radius, how long the whole motion takes, and the fold's geometry (null until the
/// block has its own model: the wheel then just appears / goes at the leg-down mark).</summary>
public sealed class AeroGearVariant
{
    public string Name;
    public Guid Block, CornerModule;
    public float WheelRadius, Duration;
    /// <summary>How far the wheel slides up its suspension axis into the well (m), how fast at most (m/s), and how deep it
    /// must be before its collision goes (m).</summary>
    public float RetractTravel, RetractSpeed = 2f, CollisionOffAt;
    public GearLayout Layout;
    /// <summary>A MAGNETIC gear (its foot a spawned pad grid with a vanilla magnetic landing gear block: AeroMagGearComponent);
    /// null: a wheeled gear.</summary>
    public MagPadSpec MagPad;
}

/// <summary>A magnetic gear's pad (AeroMagGearComponent; tools/meshgen/gen_maggear_defs.py writes the matching definitions).</summary>
public sealed class MagPadSpec
{
    /// <summary>The pad block's server prefab (the vanilla LandingGear100's: a 0.5 x 0.75 x 0.75 m magnetic pad, 158 kg).</summary>
    public Guid PadPrefab;
    /// <summary>Where the pad's magnetic face is in its grid's frame (m, along its down; the grid's local bounding box's bottom -
    /// measured from the first pad that comes in and kept from then on: PadFace).</summary>
    public float FaceY;
    /// <summary>The face spawns this far above the hidden wheel's lowest point (m): the wheel holds the ship until the pad joins.</summary>
    public float Clearance = 0.03f;
    /// <summary>The hidden wheel's mass (kg; its WheelColliderDefinition's WheelMass - the definition is what counts) and the pad's: their
    /// sum is the suspension's spring mass (GearStiffness.SpringRate - a light wheel under a heavy ship bottoms out).</summary>
    public float WheelMass, PadMass;
    /// <summary>The axle held this hard (N m) while the pad is on: the wheel cannot spin, so the pad keeps its attitude.</summary>
    public float AxleHoldTorque = 1e9f;
    /// <summary>The weight one gear is sized for (kg): its suspension's sag at the stiffness slider's top (GearTests).</summary>
    public float RatedLoad;
    /// <summary>The block's suspension stroke (m, the vanilla 1.5 m corner module's travel - GearTests' sag check).</summary>
    public float Stroke;
}

/// <summary>
/// The retractable gears (2026-10-13: three sizes, the vanilla 1 / 1.5 / 2.5 m wheels - the user stopped at 2.5 m). The
/// block GUIDs are ours (ae200001-ae00-9e0N-...); CornerModule is the vanilla WheelCornerModule{100,150,250}
/// WheelCornerModuleDefinition (GameData\Vanilla\Content\Blocks\SuspensionWheels). Radii measured from the wheel models
/// (diameter = size / 100 m).
/// </summary>
public static class AeroGearVariants
{
    public static readonly AeroGearVariant[] All =
    {
        // (2026-10-13: the user's kitbash - a forward-folding leg on a raked slide; the 1.5 / 1 m are the 2.5 m scaled
        //  0.6 / 0.4. Numbers: gear_export_prep.py's gear_spec.json)
        // (the 0.5 m: our own half-size wheel on our own corner module - gen_gear_defs.py gen_wheel_050; scale 0.2)
        new() { Name = "Wheeled Landing Gear 0.5 m", Block = new("ae200001-ae00-9e04-0001-000000000001"), CornerModule = new("ae200001-ae00-9e04-0060-000000000001"), WheelRadius = 0.25f, Duration = 2.5f,
                RetractTravel = 5.3707f * 0.2f, RetractSpeed = 3f * 0.2f, CollisionOffAt = OffAt * 0.2f, Layout = Gear(0.2f) },
        new() { Name = "Wheeled Landing Gear 1 m", Block = new("ae200001-ae00-9e01-0001-000000000001"), CornerModule = new("5301f8e8-a1de-4512-aa5a-57970a180110"), WheelRadius = 0.5f, Duration = 3f,
                RetractTravel = 5.3707f * 0.4f, RetractSpeed = 3f * 0.4f, CollisionOffAt = OffAt * 0.4f, Layout = Gear(0.4f) },
        new() { Name = "Wheeled Landing Gear 1.5 m", Block = new("ae200001-ae00-9e02-0001-000000000001"), CornerModule = new("a9c8413a-751c-4d41-9913-e00e320ced95"), WheelRadius = 0.75f, Duration = 3.5f,
                RetractTravel = 5.3707f * 0.6f, RetractSpeed = 3f * 0.6f, CollisionOffAt = OffAt * 0.6f, Layout = Gear(0.6f) },
        // (2026-10-20: the MAGNETIC gear - the 1.5 m gear's housing, leg and slide, a hidden wheel on its suspension and a vanilla
        //  magnetic landing gear block as its foot, AeroMagGearComponent; ids 9e15 the block, 9e16 its hidden wheel)
        new() { Name = "Magnetic Landing Gear 1.5 m", Block = new("ae200001-ae00-9e15-0001-000000000001"), CornerModule = new("ae200001-ae00-9e16-0060-000000000001"), WheelRadius = 0.75f, Duration = 3.5f,
                RetractTravel = 5.3707f * 0.6f, RetractSpeed = 3f * 0.6f, CollisionOffAt = OffAt * 0.6f, Layout = Gear(0.6f),
                MagPad = new MagPadSpec { PadPrefab = new("8574735f-e98b-487e-9662-21435f0ce3ed"), FaceY = MagGearSizes.PadFaceY100, WheelMass = MagGearSizes.Wheel150,
                                          PadMass = MagGearSizes.Pad100, RatedLoad = MagGearSizes.Rated150, Stroke = MagGearSizes.Stroke150 } },
        new() { Name = "Wheeled Landing Gear 2.5 m", Block = new("ae200001-ae00-9e03-0001-000000000001"), CornerModule = new("a240c373-8a4f-422b-83c2-caf6d609b121"), WheelRadius = 1.25f, Duration = 4f,
                RetractTravel = 5.3707f, RetractSpeed = 3f, CollisionOffAt = OffAt, Layout = Gear(1f) },
    };

    /// <summary>
    /// A gear's layout at this scale of the 2.5 m one, in its model's space (the dummies' - Blender (x, z, -y) + 0.125,
    /// gen_gear_defs.py): the leg (LandingGear500 mount, hinge head + piston base) turns 60 deg about X at the pivot; the
    /// rod (S2 + the fork) rides the hub, which slides 5.37 m (x scale) up a line raked 11 deg from vertical toward the
    /// block's Forward (-Z) - the wheel stows level ahead of the pivot.
    /// </summary>
    /// <summary>
    /// How far up its slide (m, the 2.5 m gear; x scale) the wheel's collision goes - and comes back on the way down: as it
    /// is about to enter the block (2026-10-13, the user: "off as soon as it's sufficiently close"), its top 0.1 m short of
    /// the block's bottom face. Hub height up the slide: -5.272 + 0.98136 s (model space), the wheel's radius 1.26, the
    /// bottom face at -1.25: s = (-1.25 - 0.1 - 1.26 + 5.272) / 0.98136.
    /// </summary>
    const float OffAt = 2.713f;


    static GearLayout Gear(float s)
    {
        const float o = 0.125f;
        var pivot = new Vector3(-0.00147f, 0.01068f, 1.58986f) * s + new Vector3(o);
        var hub = new Vector3(-0.00147f, -5.27208f, -1.46014f) * s + new Vector3(o);
        return new GearLayout
        {
            HingePoint = pivot, HingeAxis = Vector3.UnitX, DeployedHub = hub,
            RakedSlide = true, RakeAxis = new Vector3(0f, 0.98136f, -0.19216f), AnimatedParts = true,
            // (no actuator, no doors: kept far away so no spawned child is taken for one)
            ActuatorBase = new Vector3(1e6f), ActuatorLegEnd = new Vector3(-1e6f),
        };
    }

    static readonly Dictionary<Guid, AeroGearVariant> _byBlock = new();
    static AeroGearVariants() { foreach (var v in All) _byBlock[v.Block] = v; }

    /// <summary>The variant of a gear block (null: not one of ours - the gear still works, with defaults).</summary>
    public static AeroGearVariant Of(Entity block)
    {
        var cb = block?.TryGet<CubeBlockComponent>();
        var g = cb != null ? BlockComponentFactory.GetDefinitionGuid(cb) : null;
        return g.HasValue && _byBlock.TryGetValue(g.Value, out var v) ? v : null;
    }
}
