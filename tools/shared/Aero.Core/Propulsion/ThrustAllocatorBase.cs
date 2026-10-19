using System.Numerics;

namespace Aero.Core.Propulsion;

/// <summary>
/// Everything a thrust allocator needs to compute throttle assignments.
/// Models take what they need and ignore the rest.
/// </summary>
public readonly struct ThrustContext
{
    /// <summary>Player-requested force in body-frame (N). Zero if no input.</summary>
    public readonly Vector3 InputForce;

    /// <summary>Player-requested torque in body-frame (N-m). Zero if no input.</summary>
    public readonly Vector3 InputTorque;

    /// <summary>Current body-frame linear velocity (m/s).</summary>
    public readonly Vector3 Velocity;

    /// <summary>Current body-frame angular velocity (rad/s).</summary>
    public readonly Vector3 AngularVelocity;

    /// <summary>Whether dampeners are enabled (rate-nulling active).</summary>
    public readonly bool DampenersEnabled;

    public ThrustContext(Vector3 inputForce, Vector3 inputTorque,
        Vector3 velocity, Vector3 angularVelocity,
        bool dampenersEnabled)
    {
        InputForce = inputForce;
        InputTorque = inputTorque;
        Velocity = velocity;
        AngularVelocity = angularVelocity;
        DampenersEnabled = dampenersEnabled;
    }
}

/// <summary>
/// Result of thrust allocation: per-thruster throttles and produced force/torque.
/// </summary>
public readonly struct ThrustResult
{
    /// <summary>Per-thruster throttle levels (0..1), indexed same as thrusters array.</summary>
    public readonly float[] Throttles;

    /// <summary>Total force produced by current allocation (body-frame N).</summary>
    public readonly Vector3 ProducedForce;

    /// <summary>Total torque produced by current allocation about CoM (body-frame N-m).</summary>
    public readonly Vector3 ProducedTorque;

    public ThrustResult(float[] throttles, Vector3 producedForce, Vector3 producedTorque)
    {
        Throttles = throttles;
        ProducedForce = producedForce;
        ProducedTorque = producedTorque;
    }

    public static ThrustResult Empty => new(Array.Empty<float>(), Vector3.Zero, Vector3.Zero);
}

/// <summary>
/// Abstract base for all thrust allocation strategies.
/// Subclasses implement Allocate() with whatever control law they want —
/// tests and game integration treat them uniformly.
/// </summary>
public abstract class ThrustAllocatorBase
{
    /// <summary>Human-readable name (for reports/debug).</summary>
    public abstract string Name { get; }

    /// <summary>Short description of the approach.</summary>
    public abstract string Description { get; }

    /// <summary>
    /// Compute throttle assignments for the given thruster layout and flight state.
    /// </summary>
    /// <param name="thrusters">Thruster definitions (position, direction, max thrust).</param>
    /// <param name="centerOfMass">Center of mass in body-frame.</param>
    /// <param name="ctx">Current flight state and player inputs.</param>
    public abstract ThrustResult Allocate(ThrusterInfo[] thrusters, Vector3 centerOfMass, in ThrustContext ctx);
}
