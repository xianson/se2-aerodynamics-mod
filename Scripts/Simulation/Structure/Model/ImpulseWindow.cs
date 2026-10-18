#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// ONE-TICK IMPULSES AS STRUCTURE LOADS - pure, no game (2026-10-17; AeroGridComponent.PublishBlockImpulse runs exactly this,
/// Tests/StructureTests 'ImpulseLoads' check it). A rotor strike puts its whole impulse into the body in ONE tick
/// (RigidBodyDataFunctions.TryApplyImpulse), but the structure takes a snapshot of its loads only StructureLoads.Rate times a
/// second, and a published force is in it only while at most two ticks old (PublishBlockLoad): J / dt published for its tick
/// would be caught by about one snapshot in five, and held for longer it would be caught twice or count a hit as a push.
/// So each block's impulses are gathered over the evaluation window and taken by the NEXT snapshot exactly once, the way the
/// hull's collisions are (StructureContacts: the window's mean and its peak step over StructureContactLoads.CrushSeconds):
///   mean   the impulses' sum over the window's length (a blade grinding on: strike after strike - the push it keeps up);
///   peak   the window's largest single-tick impulse delivered over the crush time (a hit: the load while it is stopped).
/// The larger is the load - a hit is a hit however many came before it in the window (never their sum over the crush),
/// and many small ones add up through the mean. The same crush time as a collision of the same impulse: a blade strike on
/// rock and the hull's own crash on that rock load the structure alike. The angular impulses (a broken rotor's momentum
/// dumped into the ship) are held the same way, as a torque. Grid-local throughout; the moment of every impulse is kept
/// about the grid's origin, so impulses at different points (a disc's strikes all round it) keep their true moment.
/// </summary>
public struct ImpulseWindow
{
    /// <summary>The window's sums: linear impulse (N s), its moment about the grid's origin plus the angular impulses (N m s).</summary>
    public Vector3 SumJ, SumM, SumL;
    /// <summary>The current tick's impulse, moment (about the origin) and angular impulse - the tick being added to.</summary>
    public Vector3 TickJ, TickM, TickL;
    /// <summary>The window's largest single-tick impulse (by size), its moment about the origin and its point; and the
    /// largest single-tick angular impulse.</summary>
    public Vector3 PeakJ, PeakM, PeakP, PeakL;
    /// <summary>The tick of the last impulse added (the frame counter it was published at); Any: something was added.</summary>
    public int Frame; public bool Any;

    /// <summary>One impulse at <paramref name="point"/> (N s, grid-local) and an angular impulse (N m s) in tick
    /// <paramref name="frame"/>. Several in one tick add up into that tick's.</summary>
    public void Add(Vector3 j, Vector3 point, Vector3 l, int frame)
    {
        if (!Any || frame != Frame) { TickJ = Vector3.Zero; TickM = Vector3.Zero; TickL = Vector3.Zero; }
        var m = Vector3.Cross(point, j);
        TickJ += j; TickM += m; TickL += l;
        SumJ += j; SumM += m; SumL += l;
        if (TickJ.LengthSquared() >= PeakJ.LengthSquared() && TickJ.LengthSquared() > 0f) { PeakJ = TickJ; PeakM = TickM; PeakP = point; }
        if (TickL.LengthSquared() > PeakL.LengthSquared()) PeakL = TickL;
        Frame = frame; Any = true;
    }

    /// <summary>The window as a load (grid-local): force (N) at point (m) and a torque of its own about that point (N m) -
    /// the larger of the mean over <paramref name="window"/> s and the peak tick over <paramref name="crush"/> s, the force's
    /// and the angular part's each. False: nothing to load.</summary>
    public bool Held(float window, float crush, out Vector3 force, out Vector3 point, out Vector3 torque)
    {
        force = Vector3.Zero; point = PeakP; torque = Vector3.Zero;
        if (!Any) return false;
        float T = MathF.Max(window, 1e-3f), c = MathF.Max(crush, 1e-3f);
        // the force: a hit (the peak tick over the crush) or the push kept up (the window's mean) - each with its own moment
        var hitF = PeakJ / c; var meanF = SumJ / T;
        if (hitF.LengthSquared() >= meanF.LengthSquared()) { force = hitF; torque = (PeakM - Vector3.Cross(point, PeakJ)) / c; }
        else { force = meanF; torque = (SumM - Vector3.Cross(point, SumJ)) / T; }
        // the angular part the same way
        var hitL = PeakL / c; var meanL = SumL / T;
        torque += hitL.LengthSquared() >= meanL.LengthSquared() ? hitL : meanL;
        return force.LengthSquared() > 0f || torque.LengthSquared() > 0f;
    }

    /// <summary>Empty again (a snapshot took it).</summary>
    public void Clear() => this = default;
}
