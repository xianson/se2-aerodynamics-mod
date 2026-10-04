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
        // Only the air-breathing ones (the same definitions OffsetThrustJob gives the turbofan profile): this used to
        // be every ThrusterComponent - hydrogen and ion too - each with BlockInfo.FaceArea, a constant 30 m2 meant
        // for control surfaces: ~1.8 MN of ram drag per thruster at 220 m/s on every ship. The capture area now is a
        // quarter of the thruster's own face (its width: 0.5 / 1.25 / 2.5 / 7.5 m).
        foreach (var (guid, width) in new[]
        {
            (new Guid("8dfddd91-eb3a-4979-8b16-2fdf43061486"), 0.5f),   // Atmo 100
            (new Guid("272859d7-957d-41ba-9f9c-8f84cddb5cb3"), 1.25f),  // Atmo 250
            (new Guid("8a005c16-0a05-444d-ab26-786653199b52"), 2.5f),   // Atmo 500
            (new Guid("bfbbff3e-7396-428a-a12c-3f32ec14c712"), 7.5f),   // Atmo 1000
        })
        {
            float area = 0.25f * width * width;
            factory.RegisterByGuid(guid, info =>
                new AirIntake(
                    info.Position,
                    info.BlockPosition,
                    facingDirection: -info.Forward, // intake faces opposite to thrust direction
                    captureArea: area));
        }
    }
}
