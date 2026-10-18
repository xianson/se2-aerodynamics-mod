#pragma warning disable
using System;
using Keen.VRage.Library.Mathematics;

namespace AeroMod;

/// <summary>
/// A REVERSIBLE THRUSTER (2026-10-17, the user: "just a thruster that goes forward and back and that is used in the rotation
/// controller properly"; "make this a component ... that other blocks can inherit") - the game-free law (Tests/ThrustTests,
/// RotorTests); the game part is ReversibleThrustComponent, on any thruster block.
/// TRANSLATION - the game's own thrust, both ways. The game books each thruster's maximum on ONE of the grid's six sides, by
///   its ThrustData.Direction, and re-books it whenever that changes (GridMovementCollectorComponent.OnThrusterChanged). The
///   component turns that direction round to the side the ship's controls ask for (Demand: the game's thrust law on that axis
///   - the pilot's input, else the dampeners' gravity hold and inertia dampening; Step: at once for input, else after
///   ConfirmSeconds beyond FlipShare of its maximum and MinHoldSeconds since the last turn - contact jitter never turns it).
///   The game then gives the thrust - keys, dampeners, hover, POWER (its draw is booked on the side it is on) - all vanilla.
/// ROTATION - an ordinary actuator of ThrustTorque's attitude law, either way: ThrustAllocator.Solve lets its force run from
///   -max to +max along its definition's direction (the game's own share of it, signed, is the starting point - GameShare by
///   its game side), in every branch that answers torque (a rate pilot's too, when the grid has reversible thrusters or main
///   rotors). Rotation comes first on it: its force's departure from the game's is weighed RevForceWeight of a one-way
///   thruster's (ThrustAllocator), and what the other thrusters cannot take back of the net force is applied as its real
///   thrust change. The game's thrust and the law's never add up on it: the game applies its share at the centre of mass, the
///   law applies only the difference (the torque, and that net force change).
/// Its push's side (the solved force's sign, Side: hysteresis) is what a block draws (a rotor's blade pitch).
/// </summary>
public static class ReversibleThrust
{
    /// <summary>Off: every reversible thruster pushes its definition's way only (a plain thruster), in the game and the law.</summary>
    public static bool Enabled = true;
    /// <summary>It turns round when the controls ask the other way for more than FlipShare of its maximum (at least
    /// MinFlipForce N) for ConfirmSeconds, and not within MinHoldSeconds of the last turn; the pilot's input at once.</summary>
    public static float FlipShare = 0.02f, MinFlipForce = 100f, ConfirmSeconds = 0.1f, MinHoldSeconds = 0.25f;
    /// <summary>The pilot's input along the axis that counts as input (the game's: any non-zero).</summary>
    public const float InputDeadband = 1e-3f;
    /// <summary>The game's inertia dampening below its curve speed: m x |v| x 60 (it stops the drift in one step).</summary>
    public const float DampRate = 60f;
    /// <summary>Its push's side turns only when |F| exceeds this share of its maximum (what is drawn: no flicker through 0).</summary>
    public static float SideShare = 0.02f;

    /// <summary>Its game side and the hysteresis' clocks (one per block, server).</summary>
    public struct State { public int Sign; public float Against, SinceFlip; public int Flips; }

    /// <summary>The force (N, + along its definition's direction) the game's thrust law asks of that axis now
    /// (ThrustComponent.ComputeThrust on the one axis): the pilot's input (-1..1) x the maximum, the gravity hold against it
    /// shrunk by the input (as the game); without input, with dampeners: the gravity hold -m g + the inertia dampening
    /// -m v x DampRate (the game's low-speed law - above its curve speed the size differs, never the sign). Velocity, gravity
    /// along the axis (m/s, m/s^2), mass (kg), maxNow (N).</summary>
    public static float Demand(float input, bool dampeners, float vAlong, float gAlong, float mass, float maxNow)
    {
        float hold = dampeners && mass > 0f ? -mass * gAlong : 0f;
        if (MathF.Abs(input) > InputDeadband)
        {
            if (hold != 0f && MathF.Sign(input) == -MathF.Sign(hold)) hold *= 1f - MathF.Min(MathF.Abs(input), 1f);
            return hold + input * MathF.Max(maxNow, 1f);
        }
        if (!dampeners || mass <= 0f) return 0f;
        return hold - mass * vAlong * DampRate;
    }

    /// <summary>One step of its game side (s.Sign +1: its definition's direction, -1: the opposite): demand (N, Demand), its
    /// maximum now (N), whether the pilot's input asks (then it turns at once). True: it turned this step.</summary>
    public static bool Step(ref State s, float demand, float maxNow, bool piloted, float dt)
    {
        if (s.Sign == 0) s.Sign = 1;
        s.SinceFlip += dt;
        if (!Enabled) { s.Against = 0f; if (s.Sign == 1) return false; s.Sign = 1; s.Flips++; s.SinceFlip = 0f; return true; }
        if (demand * s.Sign >= 0f) { s.Against = 0f; return false; }
        if (piloted) { Turn(ref s); return true; }
        if (MathF.Abs(demand) <= MathF.Max(FlipShare * maxNow, MinFlipForce)) { s.Against = 0f; return false; }
        s.Against += dt;
        if (s.Against + 1e-5f < ConfirmSeconds || s.SinceFlip + 1e-5f < MinHoldSeconds) return false;
        Turn(ref s); return true;
    }
    static void Turn(ref State s) { s.Sign = -s.Sign; s.Against = 0f; s.SinceFlip = 0f; s.Flips++; }

    /// <summary>Its push's side after a force f (N, signed) with this maximum (N): f's sign once |f| is past SideShare of it.</summary>
    public static int Side(int side, float f, float max)
    {
        if (side == 0) side = 1;
        return MathF.Abs(f) > SideShare * MathF.Max(max, 1f) ? (f < 0f ? -1 : 1) : side;
    }

    /// <summary>The game's direction for a side: its definition's (natural) one or the opposite.</summary>
    public static Base6Directions.Direction Direction(Base6Directions.Direction natural, int sign) => sign < 0 ? natural.GetOppositeDirection() : natural;
}
