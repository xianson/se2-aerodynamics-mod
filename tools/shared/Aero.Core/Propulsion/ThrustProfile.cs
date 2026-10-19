namespace Aero.Core.Propulsion;

/// <summary>
/// Defines how an engine's thrust scales with Mach number.
/// Each profile is a piecewise curve: an array of (Mach, scale) knots
/// with cosine interpolation between them for smooth, jitter-free transitions.
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
            throw new System.ArgumentException("Need at least 2 knots", nameof(knots));

        Name = name;
        MinOperatingMach = minOperatingMach;
        MaxOperatingMach = maxOperatingMach;
        _knots = knots;
    }

    /// <summary>
    /// Evaluate thrust scale factor at a given Mach number.
    /// Returns 0.0 outside the operating envelope.
    /// Uses cosine interpolation between knots for C1 continuity.
    /// </summary>
    public double Evaluate(double mach)
    {
        if (mach < MinOperatingMach || mach > MaxOperatingMach)
            return 0.0;

        // Clamp to knot range
        if (mach <= _knots[0].mach) return _knots[0].scale;
        if (mach >= _knots[^1].mach) return _knots[^1].scale;

        // Find bracketing knots
        for (int i = 0; i < _knots.Length - 1; i++)
        {
            var (m0, s0) = _knots[i];
            var (m1, s1) = _knots[i + 1];

            if (mach <= m1)
            {
                double t = (mach - m0) / (m1 - m0);
                // Cosine interpolation: smooth without overshoot
                double smooth = 0.5 * (1.0 - System.Math.Cos(System.Math.PI * t));
                return s0 + (s1 - s0) * smooth;
            }
        }

        return _knots[^1].scale;
    }

    public override string ToString() => $"ThrustProfile({Name})";
}
