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
    /// <summary>A PAD-FOOT gear (its wheel a vanilla landing-gear mesh, held level: AeroGearComponent.PadFoot.cs); null: a wheeled gear.</summary>
    public PadFootSpec Foot;
    /// <summary>Its wheel's rotation about the axle is locked to the ship (pad feet only).</summary>
    public bool LocksOrientation => Foot != null;
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
        new() { Name = "Wheeled Landing Gear 2.5 m", Block = new("ae200001-ae00-9e03-0001-000000000001"), CornerModule = new("a240c373-8a4f-422b-83c2-caf6d609b121"), WheelRadius = 1.25f, Duration = 4f,
                RetractTravel = 5.3707f, RetractSpeed = 3f, CollisionOffAt = OffAt, Layout = Gear(1f) },
        // (2026-10-20: the PAD-FOOT gears - the same housings, a vanilla landing-gear model as the foot: one per model, on the housing it
        //  fits; tools/meshgen/gen_padgear_defs.py, its padgear_spec.json - GearTests checks these against it)
        new() { Name = "Retractable Gear (Plate) 0.5 m", Block = new("ae200001-ae00-9e21-0001-000000000001"), CornerModule = new("ae200001-ae00-9e31-0068-000000000001"), WheelRadius = 0.25200f, Duration = 2.5f,
                RetractTravel = 5.3707f * 0.2f, RetractSpeed = 3f * 0.2f, CollisionOffAt = OffAt * 0.2f, Layout = Gear(0.2f),
                Foot = new PadFootSpec { Model = "Magneticplate50", Scale = 1.00000f, BoxCenter = new Vector3(0.00000f, -0.12600f, 0.00000f), BoxHalf = new Vector3(0.25100f, 0.12600f, 0.25100f), Height = 0.25200f,
                                         WheelMass = 150f, RatedLoad = 1500f, Stroke = 0.12500f } },
        new() { Name = "Retractable Gear (Pad) 1 m", Block = new("ae200001-ae00-9e22-0001-000000000001"), CornerModule = new("ae200001-ae00-9e32-0068-000000000001"), WheelRadius = 1.00200f, Duration = 3f,
                RetractTravel = 5.3707f * 0.4f, RetractSpeed = 3f * 0.4f, CollisionOffAt = OffAt * 0.4f, Layout = Gear(0.4f),
                Foot = new PadFootSpec { Model = "LandingGear100", Scale = 1.00000f, BoxCenter = new Vector3(0.00000f, -0.50100f, 0.00000f), BoxHalf = new Vector3(0.25100f, 0.50100f, 0.50100f), Height = 1.00200f,
                                         WheelMass = 250f, RatedLoad = 5000f, Stroke = 0.25000f } },
        new() { Name = "Retractable Gear (Pad) 1.5 m", Block = new("ae200001-ae00-9e23-0001-000000000001"), CornerModule = new("ae200001-ae00-9e33-0068-000000000001"), WheelRadius = 1.50120f, Duration = 3.5f,
                RetractTravel = 5.3707f * 0.6f, RetractSpeed = 3f * 0.6f, CollisionOffAt = OffAt * 0.6f, Layout = Gear(0.6f),
                Foot = new PadFootSpec { Model = "LandingGear500", Scale = 0.30000f, BoxCenter = new Vector3(0.00000f, -0.75060f, 0.00000f), BoxHalf = new Vector3(0.37890f, 0.75060f, 0.75090f), Height = 1.50120f,
                                         WheelMass = 600f, RatedLoad = 20000f, Stroke = 0.40000f } },
        new() { Name = "Retractable Gear (Plate) 2.5 m", Block = new("ae200001-ae00-9e24-0001-000000000001"), CornerModule = new("ae200001-ae00-9e34-0068-000000000001"), WheelRadius = 0.50600f, Duration = 4f,
                RetractTravel = 5.3707f * 1.0f, RetractSpeed = 3f * 1.0f, CollisionOffAt = OffAt * 1.0f, Layout = Gear(1.0f),
                Foot = new PadFootSpec { Model = "MagneticPlate250", Scale = 1.00000f, BoxCenter = new Vector3(0.00000f, -0.25300f, 0.00000f), BoxHalf = new Vector3(1.25100f, 0.25300f, 1.25100f), Height = 0.50600f,
                                         WheelMass = 1200f, RatedLoad = 60000f, Stroke = 0.60000f } },
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
