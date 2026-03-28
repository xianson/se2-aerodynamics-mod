#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Pre-computed aerodynamic conditions at a specific block position.
/// Factory method uses AeroContext.VelocityAtPoint() for rotational effects.
/// </summary>
public readonly struct LocalAeroConditions
{
    /// <summary>Local velocity at this point (body-frame, m/s).</summary>
    public readonly Vector3 Velocity;

    /// <summary>Speed magnitude (m/s).</summary>
    public readonly float Speed;

    /// <summary>Unit flow direction (velocity / speed). Zero if stationary.</summary>
    public readonly Vector3 FlowDirection;

    /// <summary>Dynamic pressure q = ½ρv² (Pa).</summary>
    public readonly float DynamicPressure;

    /// <summary>Mach number.</summary>
    public readonly float Mach;

    /// <summary>Atmospheric properties.</summary>
    public readonly AtmosphereState Atmosphere;

    /// <summary>Grid-local position (meters).</summary>
    public readonly Vector3 Position;

    /// <summary>Height above ground in meters (-1 if unknown/no ground).</summary>
    public readonly float GroundHeight;

    public LocalAeroConditions(Vector3 velocity, float speed, Vector3 flowDirection,
        float dynamicPressure, float mach, AtmosphereState atmosphere, Vector3 position,
        float groundHeight = -1f)
    {
        Velocity = velocity;
        Speed = speed;
        FlowDirection = flowDirection;
        DynamicPressure = dynamicPressure;
        Mach = mach;
        Atmosphere = atmosphere;
        Position = position;
        GroundHeight = groundHeight;
    }

    /// <summary>
    /// Build local conditions at a grid-local position from a global AeroContext.
    /// </summary>
    public static LocalAeroConditions FromContext(in AeroContext ctx, Vector3 position)
    {
        Vector3 vel = ctx.VelocityAtPoint(position);
        float speed = vel.Length();
        Vector3 flowDir = speed > 0.01f ? vel / speed : Vector3.Zero;
        float q = (float)(0.5 * ctx.Atmosphere.Density * speed * speed);
        float mach = ctx.Atmosphere.SpeedOfSound > 0
            ? speed / (float)ctx.Atmosphere.SpeedOfSound
            : 0f;

        return new LocalAeroConditions(vel, speed, flowDir, q, mach, ctx.Atmosphere, position,
            ctx.GroundHeight);
    }
}
