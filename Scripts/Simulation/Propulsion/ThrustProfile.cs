#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// Defines how an engine's thrust scales with Mach number.
/// Piecewise curve: (Mach, scale) knots with cosine interpolation
/// for smooth, jitter-free transitions.
/// </summary>
public sealed class ThrustProfile
{
    public string Name { get; }
    public double MinOperatingMach { get; }
    public double MaxOperatingMach { get; }

    private readonly (double mach, double scale)[] _knots;

    public ThrustProfile(string name, (double mach, double scale)[] knots,
                         double minOperatingMach = 0, double maxOperatingMach = double.PositiveInfinity)
    {
        if (knots.Length < 2)
            throw new ArgumentException("Need at least 2 knots", nameof(knots));

        Name = name;
        MinOperatingMach = minOperatingMach;
        MaxOperatingMach = maxOperatingMach;
        _knots = knots;
    }

    /// <summary>
    /// Evaluate thrust scale factor at a given Mach number.
    /// Returns 0.0 outside the operating envelope.
    /// Cosine interpolation between knots for C1 continuity.
    /// </summary>
    public double Evaluate(double mach)
    {
        if (mach < MinOperatingMach || mach > MaxOperatingMach)
            return 0.0;

        if (mach <= _knots[0].mach) return _knots[0].scale;
        if (mach >= _knots[^1].mach) return _knots[^1].scale;

        for (int i = 0; i < _knots.Length - 1; i++)
        {
            var (m0, s0) = _knots[i];
            var (m1, s1) = _knots[i + 1];

            if (mach <= m1)
            {
                double t = (mach - m0) / (m1 - m0);
                double smooth = 0.5 * (1.0 - Math.Cos(Math.PI * t));
                return s0 + (s1 - s0) * smooth;
            }
        }

        return _knots[^1].scale;
    }

    // (2026-10-17: the old Evaluate(mach, velocityHat, intakeDirection) is retired - it lowered the Mach sideways, wrong
    //  physics, and was handed the flame's direction as the intake, so straight flight read as reverse flow. The angle is
    //  its own factor now: InletAngle, on top of Evaluate(mach))

    public override string ToString() => $"ThrustProfile({Name})";
}
