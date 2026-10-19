#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>Which height a height hold keeps: over the ground under the ship (AGL), or along the gravity from where it was
/// engaged (a level altitude, steady over rough ground).</summary>
public enum ApHeightRef : byte { Ground, Inertial }

/// <summary>The Autopilot block's mode choice (its panel). Later modes append (the saved number is this).</summary>
public enum ApMode : byte { Off = 0, HoverHold = 1, HeightHold = 2 }

/// <summary>
/// THE AUTOPILOT BLOCK'S PANEL (engine-free): what the player sets on the block - the mode, the height and its reference, the hover
/// speed, the protection's limits, what it does unattended - its limits (ApLimits.Of) and goals (ApModes), saved with the block
/// (AeroBlockSaveForm.Autopilot). Engine-free and dependency-light: Tests/BlockSaveTests compiles it with ApConfig.cs.
/// </summary>
public struct ApPanel
{
    public ApMode Mode;
    /// <summary>The height held (m): over the ground, or over the ground where it was engaged (HeightRef). Engaging catches the
    /// height the ship has (written back here); a new number flies it there.</summary>
    public float HeightM;
    public ApHeightRef HeightRef;
    public float HoverSpeed;
    public bool Protection;
    public float GLimit, AoaMarginDeg, FloorM;
    public bool LookAhead;
    /// <summary>Keep the mode with nobody seated (within the world's Autopilot.Unattended).</summary>
    public bool Unattended;

    public const float HeightMax = 20000f, HoverSpeedMin = 1f, HoverSpeedMax = 50f, GLimitMin = 1.5f, GLimitMax = 15f,
        AoaMarginMin = 0f, AoaMarginMax = 15f, FloorMin = 0f, FloorMax = 500f;

    /// <summary>A new block's panel: off, the static defaults (Autopilot.*).</summary>
    public static ApPanel Defaults => new ApPanel
    {
        Mode = ApMode.Off, HeightM = 0f, HeightRef = Autopilot.HoldAboveGround ? ApHeightRef.Ground : ApHeightRef.Inertial,
        HoverSpeed = Autopilot.HoverSpeed, Protection = Autopilot.Protection, GLimit = Autopilot.GLimit, AoaMarginDeg = Autopilot.AoaMarginDeg,
        FloorM = Autopilot.FloorM, LookAhead = Autopilot.LookAhead, Unattended = true,
    };

    /// <summary>Every number in its range, every choice known (a hand-edited or older save).</summary>
    public ApPanel Clamped()
    {
        var p = this;
        if (p.Mode > ApMode.HeightHold) p.Mode = ApMode.Off;
        if (p.HeightRef > ApHeightRef.Inertial) p.HeightRef = ApHeightRef.Ground;
        p.HeightM = float.IsFinite(p.HeightM) ? Math.Clamp(p.HeightM, 0f, HeightMax) : 0f;
        p.HoverSpeed = Fin(p.HoverSpeed, Autopilot.HoverSpeed, HoverSpeedMin, HoverSpeedMax);
        p.GLimit = Fin(p.GLimit, Autopilot.GLimit, GLimitMin, GLimitMax);
        p.AoaMarginDeg = Fin(p.AoaMarginDeg, Autopilot.AoaMarginDeg, AoaMarginMin, AoaMarginMax);
        p.FloorM = Fin(p.FloorM, Autopilot.FloorM, FloorMin, FloorMax);
        return p;
    }
    static float Fin(float v, float def, float lo, float hi) => float.IsFinite(v) ? Math.Clamp(v, lo, hi) : def;
}

/// <summary>
/// The limits and choices one autopilot flies with (ApSense.Lim): the controlling block's panel within the world's caps. A struct
/// of values - no allocation, read on the simulation thread each tick.
/// </summary>
public struct ApLimits
{
    public bool Enabled, Unattended, Protection, LookAhead, HoldAboveGround;
    public float MaxLeanDeg, HoverSpeed, ClimbRate, GLimit, AoaMarginDeg, FloorM;

    /// <summary>The static defaults (Autopilot.*) - the offline sim's, and a block with no saved panel yet.</summary>
    public static ApLimits Defaults => new ApLimits
    {
        Enabled = Autopilot.Enabled, Unattended = Autopilot.Unattended, Protection = Autopilot.Protection, LookAhead = Autopilot.LookAhead,
        HoldAboveGround = Autopilot.HoldAboveGround, MaxLeanDeg = Autopilot.MaxLeanDeg, HoverSpeed = Autopilot.HoverSpeed,
        ClimbRate = Autopilot.ClimbRate, GLimit = Autopilot.GLimit, AoaMarginDeg = Autopilot.AoaMarginDeg, FloorM = Autopilot.FloorM,
    };

    /// <summary>A block's panel within the world's caps: the world's switch and its unattended permission, its lean and climb caps;
    /// the panel's numbers clamped to their ranges (ApPanel).</summary>
    public static ApLimits Of(in ApPanel panel)
    {
        var p = panel.Clamped();   // (a NaN or an out-of-range number from a hand-edited save never reaches the core)
        return new ApLimits
        {
            Enabled = Autopilot.Enabled, Unattended = Autopilot.Unattended && p.Unattended, Protection = p.Protection, LookAhead = p.LookAhead,
            HoldAboveGround = p.HeightRef == ApHeightRef.Ground, MaxLeanDeg = Autopilot.MaxLeanDeg, ClimbRate = Autopilot.ClimbRate,
            HoverSpeed = Math.Clamp(p.HoverSpeed, ApPanel.HoverSpeedMin, ApPanel.HoverSpeedMax),
            GLimit = Math.Clamp(p.GLimit, ApPanel.GLimitMin, ApPanel.GLimitMax),
            AoaMarginDeg = Math.Clamp(p.AoaMarginDeg, ApPanel.AoaMarginMin, ApPanel.AoaMarginMax),
            FloorM = Math.Clamp(p.FloorM, ApPanel.FloorMin, ApPanel.FloorMax),
        };
    }
}
