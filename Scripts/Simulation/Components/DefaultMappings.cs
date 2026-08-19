#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;

namespace AeroMod;

/// <summary>
/// Registers the default block-definition → aero-component mappings.
/// Custom aero blocks are matched by GUID; vanilla blocks by component type.
/// </summary>
public static class DefaultMappings
{
    public static void Register(BlockComponentFactory factory)
    {
        // ── Custom aero blocks (matched by definition GUID) ──

        factory.RegisterByGuid(AeroBlockGuids.ControlSurface_1x1, info =>
        {
            // Hinge runs along the span (block's right axis = cross(forward, up))
            var hingeAxis = Vector3.Cross(info.Forward, info.Up);

            // Canonicalize hinge direction: always pick the positive half-space.
            // This prevents mirrored blocks (e.g. two rudder blocks placed back-to-back
            // on a fin) from having opposite hinge axes and cancelling each other's forces.
            float absX = MathF.Abs(hingeAxis.X);
            float absY = MathF.Abs(hingeAxis.Y);
            float absZ = MathF.Abs(hingeAxis.Z);
            float dominant = absY >= absX && absY >= absZ ? hingeAxis.Y
                           : absX >= absZ ? hingeAxis.X
                           : hingeAxis.Z;
            if (dominant < 0) hingeAxis = -hingeAxis;

            return new ControlSurface(
                info.Position,
                info.BlockPosition,
                hingeAxis: hingeAxis,
                chordDirection: info.Forward,
                area: info.FaceArea);
        });

        factory.RegisterByGuid(AeroBlockGuids.Airbrake_1x1, info =>
            new Airbrake(
                info.Position,
                info.BlockPosition,
                facingDirection: info.Forward, // deployed plate faces forward
                area: info.FaceArea));

        factory.RegisterByGuid(AeroBlockGuids.Scoop_1x1, info =>
            new AtmosphericScoop(
                info.Position,
                info.BlockPosition,
                facingDirection: info.Forward, // scoop opening faces forward
                scoopArea: info.FaceArea));

        // CCW rotor (standard) — reaction torque yaws fuselage CW
        factory.RegisterByGuid(AeroBlockGuids.HelicopterRotor_1x1, info =>
            new HelicopterRotor(
                info.Position,
                info.BlockPosition,
                discAxis: info.Up,              // rotor disc normal = block's up direction
                ratedThrust: 500_000f,          // 500 kN at sea level, full collective
                discRadius: 5f,                 // 5m disc radius
                spinSign: +1f));                // CCW from above

        // CW rotor (counter-rotating) — reaction torque yaws fuselage CCW
        // Pair with a CCW rotor to cancel torque reaction (coaxial, tandem, or quad layout)
        factory.RegisterByGuid(AeroBlockGuids.HelicopterRotorCW_1x1, info =>
            new HelicopterRotor(
                info.Position,
                info.BlockPosition,
                discAxis: info.Up,
                ratedThrust: 500_000f,
                discRadius: 5f,
                spinSign: -1f));                // CW from above

        // ── Vanilla: atmospheric thrusters → AirIntake ──
        factory.RegisterByComponent<ThrusterComponent>((info, thrusterComp) =>
            new AirIntake(
                info.Position,
                info.BlockPosition,
                facingDirection: -info.Forward, // intake faces opposite to thrust direction
                captureArea: info.FaceArea));
    }
}
