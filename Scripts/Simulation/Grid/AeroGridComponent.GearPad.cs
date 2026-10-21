#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
namespace AeroMod;

/// <summary>
/// A MAGNETIC GEAR'S PAD GRID (2026-10-20; Integration/AeroMagGearComponent.cs): a one-block grid the gear spawned as its foot,
/// joined to the gear's hidden wheel. It is part of its ship, not an aircraft of its own: no aero step, no attitude, no wind undo -
/// its air is the ship's (the pad is under the gear, inside the ship's own air). Known by its name (MagPadNames, read once per grid
/// - a reloaded pad or the client's copy) or told by its gear when it joins. The server's copy reports itself once a second (the
/// orphan sweep: a pad no gear claims is removed).
/// </summary>
public partial class AeroGridComponent
{
    /// <summary>This grid is a magnetic gear's pad (no aero of its own).</summary>
    internal volatile bool GearPad;
    /// <summary>On a SHIP: the bodies of the pads joined under its magnetic gears (they are joined to its wheels, not to it): its own
    /// ground ray, terrain casts and obstacle scan skip them. Replaced whole on a join or a loss (AeroMagGearComponent, rare), read
    /// without a lock.</summary>
    internal volatile DEntity[] PadBodies = System.Array.Empty<DEntity>();

    /// <summary>A pad's body in or out of this ship's list (AeroMagGearComponent, under its lock).</summary>
    internal void SetPadBody(DEntity pad, bool on)
    {
        var a = PadBodies;
        int i = System.Array.IndexOf(a, pad);
        if (on == (i >= 0)) return;
        if (on) { var b = new DEntity[a.Length + 1]; a.CopyTo(b, 0); b[a.Length] = pad; PadBodies = b; }
        else { var b = new DEntity[a.Length - 1]; for (int k = 0, n = 0; k < a.Length; k++) if (k != i) b[n++] = a[k]; PadBodies = b; }
    }
    long _padSeenAt;

    /// <summary>
    /// Added to a scene (a load, a spawn, a re-add): a pad grid - known by its name, read once per add - comes in with NO collision
    /// (the "Ragdolls" layer) on the server, until its gear joins it and gives it its ship's collision group (it then never meets its
    /// own ship) and the ground back (AeroMagGearComponent.TryJoin). A pad saved stowed sits inside its gear's housing, and a fresh
    /// one overlaps the hidden wheel it spawns under: either would be thrown out by the solver the first step.
    /// </summary>
    void PadOnAdded()
    {
        string name = null;
        try { name = Entity?.TryGet<CubeGridComponent>()?.DisplayName.EvaluateText(); } catch { }   // (once per grid added)
        if (!MagPadNames.TryParse(name, out _)) return;
        GearPad = true;
        if (!IsServerScene) return;
        var off = AeroGearComponent.NoCollisionLayer();
        if (off != null) Data.Set(off.Value);
    }

    /// <summary>The grid's steps ask first: true - a pad, skip the step (its own once-a-second report done).</summary>
    internal bool PadStep()
    {
        if (!GearPad) return false;
        if (IsServerScene)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= _padSeenAt) { _padSeenAt = now + System.Diagnostics.Stopwatch.Frequency; AeroMagGearComponent.PadSeen(Entity); }
        }
        return true;
    }
}
