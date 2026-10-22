#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// A PAD-FOOT gear's foot (2026-10-20, the user: "No lock, custom wheel model that is the landing gear, and constrained"): the
/// suspension's wheel IS the foot - a vanilla landing-gear mesh on a copy of the vanilla wheel prefab (tools/meshgen/gen_padgear_defs.py,
/// which writes the same numbers to padgear_spec.json: Tests/GearTests checks they match). In the wheel's own frame (turned as its
/// block: the foot spawns level), its top centre at the origin (the rod hub), m. Since 2026-10-21 the foot is held by ONE constraint,
/// the SLIDER (PadSlider; AeroGearComponent.PadFoot.cs) - the vanilla wheel constraint is released - and carried by the oleo strut
/// (WheelOleo, rated by RatedLoad: PadFootSpec.Rating).
/// </summary>
public sealed class PadFootSpec
{
    /// <summary>The vanilla landing-gear model it shows and its scale (a placeholder reuse: no kitbash).</summary>
    public string Model; public float Scale = 1f;
    /// <summary>The box the wheel's body gets instead of the corner module's sphere (centre, half extents): the foot's bounds.</summary>
    public Vector3 BoxCenter, BoxHalf;
    /// <summary>The foot's height (m): its sole is this far below the hub.</summary>
    public float Height;
    /// <summary>The foot body's mass (kg; its WheelColliderDefinition's - the definition is what counts), the mass one gear is rated to
    /// carry at the design gravity (kg, 2 g: PadFootSpec's table) and the suspension's stroke (m, the housing's line of travel).</summary>
    public float WheelMass, RatedLoad, Stroke;
    /// <summary>The foot's pivot dummy in its own body space (the corner module attaches the wheel by it: its ModelComponentDefinition's
    /// first dummy, as gen_padgear_defs.py writes it - turned as the housing's wheel rest, so the foot spawns level): the slider's frame A.</summary>
    public Vector3 PivotPosition; public Quaternion PivotOrientation = Quaternion.Identity;

    /// <summary>
    /// THE PAD RATINGS (2026-10-21; the design gravity 2 g, WheelRating.DesignG): each pad is rated as the vanilla wheel of the housing it
    /// rides (the same strut, the same stroke - WheelRating.RatedFor of the housing's wheel size), so the tuned oleo carries it as it
    /// carries that wheel. (Before, 1.5 / 5 / 20 / 60 t on the vanilla suspension's spring: the 33 t rig on three 1.5 m pads rested on its
    /// hull.)
    ///   pad                     housing   stroke (+ extra)    foot     rated load    a 3 m/s stop at the rating (Tests/WheelShockTests)
    ///   Plate 0.5 m             0.5 m     0.125 (+0.050) m    150 kg     3.75 t      over 3 g: 0.175 m is too short for 3 m/s at 2 g
    ///   Pad 1 m                 1 m       0.25  (+0.10)  m    250 kg    15 t
    ///   Pad 1.5 m               1.5 m     0.40  (+0.16)  m    600 kg    33.75 t
    ///   Plate 2.5 m             2.5 m     0.60  (+0.24)  m   1200 kg    93.75 t
    /// (x Wheels.OleoRating; the extra: Wheels.OleoExtension of the stroke - the foot hangs that much below the housing's rest.)
    /// </summary>
    public static readonly float[] HousingWheel = { 0.5f, 1f, 1.5f, 2.5f };

    /// <summary>The foot's strut rating: RatedLoad x the world's Wheels.OleoRating (0.5..3), its stroke, its body's mass, the extra
    /// stroke below the rest (Wheels.OleoExtension x the stroke - the slider's extended end). Diameter: the housing's wheel (readouts).</summary>
    public WheelRating Rating(float ratingScale, float extension)
    {
        float d = 0f;
        for (int i = 0; i < HousingWheel.Length; i++) if (MathF.Abs(WheelRating.RatedFor(HousingWheel[i]) - RatedLoad) < 1f) d = HousingWheel[i];
        return new WheelRating(d, Stroke, WheelMass, RatedLoad * Math.Clamp(ratingScale, 0.5f, 3f), Stroke * Math.Clamp(extension, 0f, 1f));
    }
}

/// <summary>What the foot's slider must do this step (PadSliderLife).</summary>
public enum PadSliderDo { Nothing, Create, Destroy }

/// <summary>
/// THE FOOT'S SLIDER's life (pure; one per pad-foot gear). The slider is the one constraint between the foot (the wheel body) and the
/// ship (PadSlider). The game saves no constraint a mod made and a wheel body takes its constraints with it, so:
///  - a wheel there and no slider: create (the build, a load, a new wheel the corner module made);
///  - the wheel changed (or went): the old slider went with the old body - forget it (never destroyed twice: the engine asserts), then
///    create on the new one;
///  - the engine destroyed it (a body removed under it): forget, create again;
///  - the block going: destroy it only while both bodies live.
/// Retraction keeps it (its limits move): the foot stays level in the well.
/// </summary>
public sealed class PadSliderLife
{
    public bool Has { get; private set; }
    public int Created { get; private set; }
    public int Lost { get; private set; }
    object _wheel;

    /// <summary>One step. wheel: the wheel body now (null: none); dead: the engine destroyed the slider. Create: make it, then
    /// <see cref="Made"/>.</summary>
    public PadSliderDo Step(object wheel, bool dead)
    {
        if (Has && (!ReferenceEquals(wheel, _wheel) || dead)) { Has = false; _wheel = null; Lost++; }
        if (!Has && wheel != null) return PadSliderDo.Create;
        return PadSliderDo.Nothing;
    }

    /// <summary>The slider was made on this wheel (false: the engine refused - tried again next step).</summary>
    public void Made(object wheel, bool ok) { if (!ok) return; Has = true; _wheel = wheel; Created++; }

    /// <summary>The block is going: destroy the slider if both bodies still live.</summary>
    public PadSliderDo Removing(bool bodiesAlive, bool dead)
    {
        bool has = Has; Has = false; _wheel = null;
        return has && bodiesAlive && !dead ? PadSliderDo.Destroy : PadSliderDo.Nothing;
    }
}

/// <summary>
/// THE SLIDER (2026-10-21, the user's option 2: "ONE slider constraint instead of the vanilla wheel constraint + a second 6-DOF lock" -
/// the double constraint let a 150-1200 kg foot tumble once a rig bottomed): a SixDegreesOfFreedomConstraint as a PRISMATIC joint,
/// foot (body A) to ship (body B):
///  - LinearX (along the TWIST axis - each pivot's row 1) LIMITED to the strut's travel, LinearY / LinearZ FIXED, all three angular FIXED;
///  - the frames: A the foot's pivot dummy in the foot's body space (where the corner module attaches the wheel), B the suspension's
///    idle frame in the ship's body space (the block's frame in the grid x the housing's idle transform) - row 1 the frame's Up (the
///    suspension axis, + compressing), row 2 its Right, row 4 its point. The vanilla spawn puts the pivot ON the idle frame (turned as
///    it), so the fixed angular rows hold the foot exactly as it spawned: level;
///  - the compression x (m, + up the strut: the corner module's CalculateSuspensionDistance) is the engine's LinearX distance times
///    Sense (+1: x = (A - B) . axis; measured in game - AeroGearComponent.PadSliderSense);
///  - limits in x: down [-Extra, Stroke] (the oleo carries the foot between them; they are its hard end stops), up the well [t, t]
///    (held at the retraction's travel t: the gear's rate-limited slide).
/// </summary>
public static class PadSlider
{
    /// <summary>A 6-DOF pivot (body space): row 1 the strut axis (frame Up), row 2 frame Right, row 3 their cross, row 4 the point.</summary>
    public static Matrix Frame(Vector3 position, Quaternion orientation)
    {
        Vector3 up = Vector3.Normalize(Vector3.Transform(Vector3.Up, orientation));
        Vector3 right = Vector3.Normalize(Vector3.Transform(Vector3.Right, orientation));
        Vector3 third = Vector3.Cross(up, right);
        return new Matrix(up.X, up.Y, up.Z, 0f, right.X, right.Y, right.Z, 0f, third.X, third.Y, third.Z, 0f, position.X, position.Y, position.Z, 1f);
    }

    /// <summary>The engine's own PRISMATIC (PrismaticConstraintArgs: the sliding axis its pivot's Forward - minus row 3 -, the
    /// perpendicular its Right - row 1 - the point row 4; every rotation and the two other translations fixed by the joint itself):
    /// row 1 frame Right, row 2 row 3 x row 1, row 3 minus the frame's Up (so Forward is the strut, + compressing).</summary>
    public static Matrix PrismaticFrame(Vector3 position, Quaternion orientation)
    {
        Vector3 up = Vector3.Normalize(Vector3.Transform(Vector3.Up, orientation));
        Vector3 right = Vector3.Normalize(Vector3.Transform(Vector3.Right, orientation));
        Vector3 back = -up, second = Vector3.Cross(back, right);
        return new Matrix(right.X, right.Y, right.Z, 0f, second.X, second.Y, second.Z, 0f, back.X, back.Y, back.Z, 0f, position.X, position.Y, position.Z, 1f);
    }

    /// <summary>The axis modes (ConstraintAxisIdentifier order LinearX..AngularZ): 1 Limited, 2 Fixed (ConstraintAxisMode) - LinearX
    /// limited, everything else fixed.</summary>
    public static readonly int[] Modes = { 1, 2, 2, 2, 2, 2 };

    /// <summary>The strut's limits in compression (m): down - [-extra, stroke]; up the well at travel t &gt; 0 - held at t.</summary>
    public static void Limits(float travel, float stroke, float extra, out float min, out float max)
    {
        if (travel > 1e-3f) { min = max = travel; return; }
        min = -Math.Max(0f, extra); max = stroke;
    }

    /// <summary>Compression limits (m) to the engine's LinearX limits for a sense (+1 / -1): a negative sense swaps and negates.</summary>
    public static void ToAxis(float min, float max, int sense, out float lo, out float hi)
    {
        if (sense >= 0) { lo = min; hi = max; } else { lo = -max; hi = -min; }
    }

    /// <summary>The compression (m, + up the strut) of a foot pivot against the mount, both in world space, the axis the mount's Up.</summary>
    public static float Compression(Vector3D footPivot, Vector3D mount, Vector3 axis) => (float)Vector3D.Dot(footPivot - mount, (Vector3D)axis);
}
