#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// HONEST DAMPENERS - the law (game-free: Tests/ThrustTests; the engine side is HonestDampeners.cs). 2026-10-18, the user:
/// "make it honest, thats fcking ridiculous".
///
/// WHAT THE GAME DOES (SE2 2.4 and 4.0 - re-checked 2026-10-19, ThrustComponent.ComputeThrust): with dampeners on, the GRAVITY HOLD (-m x GravitySum along each
/// grid axis, shrunk by an opposing input; ComputeGravityDampening) is never applied as thrust - it is clamped to that side's
/// thrust and taken off the grid's CustomGravity IN PLACE (only the dampening and input thrust go into ActiveThrustData and the
/// impulse). The physics then applies CustomGravity x the world's GravityMultiplier (vanilla 2). So one g of booked thrust
/// cancels two g of real gravity: a ship whose thrust is between 1x and 2x its planet weight falls with dampeners off, and with
/// them on is weightless AND climbs with all the thrust above one weight.
///
/// HONEST: the hold is real thrust against the gravity the body really gets (GravitySum x the multiplier), within the
/// thrusters' real maxima (MaxThrustData.Regular), and what holds is not there to climb with: the total (hold + the game's own
/// dampening and input thrust) is clamped to the maxima. The game's channel is kept (the hold stays in CustomGravity, as the
/// game puts it): CustomGravity is written so that the physics' gravity x multiplier + the game's impulse = the full gravity x
/// multiplier + the honest thrust. Nothing is added that the thrusters could not give (a ship short of 2 weights sinks), and
/// nothing is taken that they gave.
///
/// SUPER DAMPENERS (the world option; ThrustComponent.ComputeMaxThrusts): braking thrust x 2..5 (MovementDefinition
/// ThrusterBrakingPowerMultiplierMin..Max by speed / the speed limit) for a grid whose MovementData.SuperDampeners is set - the
/// game's own law runs without it when the grid's flag is cleared (SuperDampeners): no correcting force.
/// </summary>
public static class HonestHold
{
    /// <summary>The world setting Physics.HonestDampeners (on: the honest hold and no super dampeners; off: the game's own).</summary>
    public static bool Enabled = true;

    /// <summary>The game's input deadzone in the gravity hold (4.0 ThrustComponent.ComputeThrust: Vector3.Sign(Movement, 0.001)).</summary>
    public const float InputDeadzone = 0.001f;

    /// <summary>The game's gravity hold (grid-local N): -m g on each axis, x (1 - |input|) where the pilot's input opposes it
    /// (ThrustComponent.ComputeThrust after ComputeGravityDampening; zero gravity, zero hold). SE2 4.0: an input counts only
    /// beyond 0.001 (the game's selector is Vector3.Sign(Movement, 0.001)).</summary>
    public static Vector3 GameHold(Vector3 gravityLocal, float mass, Vector3 movement)
    {
        if (gravityLocal == Vector3.Zero || !(mass > 0f)) return Vector3.Zero;
        Vector3 h = -gravityLocal * mass;
        return new Vector3(Axis(h.X, movement.X), Axis(h.Y, movement.Y), Axis(h.Z, movement.Z));
        static float Axis(float h, float u) { float s = u > InputDeadzone ? 1f : u < -InputDeadzone ? -1f : 0f; return s != 0f && s == -MathF.Sign(h) ? h * (1f - MathF.Abs(u)) : h; }
    }

    /// <summary>The honest thrust (grid-local N, the overrides left out): the hold scaled to the gravity the body gets (x the
    /// multiplier) on top of the game's own dampening and input thrust (ActiveThrustData less the overrides), clamped to the
    /// thrusters' maxima per side (MaxThrustData.Regular: Positive, Negative as magnitudes).</summary>
    public static Vector3 Thrust(Vector3 hold, float multiplier, Vector3 gameThrust, Vector3 maxPos, Vector3 maxNeg)
        => Vector3.Clamp(hold * multiplier + gameThrust, -maxNeg, maxPos);

    /// <summary>The CustomGravity (world m/s2) that makes the physics' step honest: the game already put its thrust in as an
    /// impulse, so gravity x multiplier + (honest - game thrust) / m - the thrust the game did not give, given through the
    /// game's own channel (the physics multiplies it back).</summary>
    public static Vector3 Gravity(Vector3 gravityWorld, Vector3 thrustChangeWorld, float mass, float multiplier)
        => mass > 0f && multiplier > 0f ? gravityWorld + thrustChangeWorld / (mass * multiplier) : gravityWorld;

    /// <summary>The grid's MovementData.SuperDampeners to run with: the world's option, unless honest.</summary>
    public static bool SuperDampeners(bool worldOption, bool honest) => worldOption && !honest;
}
