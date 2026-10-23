#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE UPSET MARGIN's arithmetic (Loac.cs EvaluatePitchMargin; engine-free so AeroBench damping / pitchmargin run the same
/// numbers): how far the ship may be knocked about one axis before its actuators can no longer turn it back.
/// </summary>
public static class UpsetMath
{
    /// <summary>The largest upset (rad) the actuators can still turn back. slope: the air's torque's change with the ship
    /// turned about the axis (N m / rad, along it; positive: it turns the ship further - unstable); need0: the air's torque
    /// along it now, the surfaces' trim in (the actuators give -need0); havePos / haveNeg: what they can give toward + / -
    /// (N m, magnitudes). Turned +theta the air asks need0 + slope theta, given toward -; turned -theta, need0 - slope theta,
    /// given toward +. Stable (slope under 1): the air turns it back by itself - +inf. 0: it cannot hold even where it flies.</summary>
    public static float Margin(float slope, float need0, float havePos, float haveNeg)
    {
        if (slope < 1f) return float.PositiveInfinity;
        float up = (haveNeg - need0) / slope, down = (havePos + need0) / slope;
        return MathF.Max(0f, MathF.Min(up, down));
    }

    /// <summary>What can be given along the local unit axis a, toward +a, from per-axis reaches toward + (pos) and - (neg),
    /// magnitudes: each axis's share by the axis's part on it (a cockpit's axes are always the grid's).</summary>
    public static float Toward(Vector3 a, Vector3 pos, Vector3 neg)
        => (a.X >= 0f ? pos.X : neg.X) * MathF.Abs(a.X) + (a.Y >= 0f ? pos.Y : neg.Y) * MathF.Abs(a.Y) + (a.Z >= 0f ? pos.Z : neg.Z) * MathF.Abs(a.Z);
}
