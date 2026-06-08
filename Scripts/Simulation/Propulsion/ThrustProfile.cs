#pragma warning disable
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

    /// <summary>
    /// Evaluate with intake direction correction.
    /// Forward-facing (cosIntake > 0): effectiveMach = mach * cosIntake.
    /// Reverse flow (cosIntake &lt; 0): thrust penalized — reverse airflow disrupts
    /// compressor/fan. Penalty: scale * (1 - |cosIntake| * reverseFlowPenalty).
    /// </summary>
    public double Evaluate(double mach, Vector3 velocityHat, Vector3 intakeDirection)
    {
        float cosIntake = Vector3.Dot(velocityHat, intakeDirection);
        if (cosIntake >= 0)
        {
            double effectiveMach = mach * cosIntake;
            return Evaluate(effectiveMach);
        }
        else
        {
            // Reverse flow: engine faces away from airflow.
            // Compressor stalls, thrust drops with increasing reverse Mach.
            // At mach=0 reverse is harmless (cosIntake~0), at high mach it kills thrust.
            double reverseMach = mach * (-cosIntake);
            double penalty = Math.Max(0.0, 1.0 - reverseMach * 0.5);
            return Evaluate(0.0) * penalty;
        }
    }

    public override string ToString() => $"ThrustProfile({Name})";
}
