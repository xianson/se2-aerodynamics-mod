#pragma warning disable
using System;
using System.Globalization;
using System.Text;
using Keen.Game2.Simulation.WorldObjects.Shared.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Data;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// A grid as the thruster steering sees it, as text for the offline flight tests (Tests/ThrustTests reads
/// Tests/data/*.flight): mass properties, bounds, the target gyros' limits, every thruster as ThrustTorque takes it (its
/// cached position, direction and maximum, and the push direction ThrustTorque uses), the game's thrust this frame, and
/// the hull's air force and torque over travel directions (PredictHullForces, 100 m/s at sea-level density). The Orbital
/// Mod's harness calls it by reflection (flightdump).
/// </summary>
public static class FlightFixture
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    static string V(Vector3 v) => string.Format(CI, "{0:R} {1:R} {2:R}", v.X, v.Y, v.Z);

    public static string Build(Keen.VRage.DCS.Components.Entity grid, string name)
    {
        AeroGridComponent aero = null;
        foreach (var c in grid.Components) if (c is AeroGridComponent a && a.IsServerScene) { aero = a; break; }
        if (aero == null) return null;
        var data = aero.Data;
        var sb = new StringBuilder();
        sb.Append("# flight fixture '").Append(name).Append("' dumped from the game\n");
        if (data.TryGet<RigidBodyMassProperties>(out var m))
        {
            sb.Append("mass ").Append(m.Mass.ToString("R", CI)).Append('\n');
            sb.Append("com ").Append(V(m.CenterOfMass)).Append('\n');
            sb.Append("invInertia ").Append(V(m.InvInertiaTensor)).Append('\n');
            var r = m.InertiaMajorAxisRotation;
            sb.Append("inertiaRotation ").Append(string.Format(CI, "{0:R} {1:R} {2:R} {3:R}", r.X, r.Y, r.Z, r.W)).Append('\n');
        }
        // the gravity it is in (world m/s2: GravityEffectData.GravitySum) - the flight tests hover each ship in its own
        sb.Append("gravity ").Append(V(PhysicsHack.GetGravityDirection(data))).Append('\n');
        if (data.TryGet<BoundingBoxData>(out var bb)) sb.Append("bounds ").Append(V(bb.BoundingBox.Size)).Append('\n');
        float maxTorque = data.TryGet<MaxTorqueData>(out var mt) ? mt.MaxTorque : 0f;
        float coef = 0f, damp = 0f;
        if (data.TryGet<MovementData>(out var md))
        {
            try { var rs = md.Definition.Value.TargetGyroRotation; coef = rs.MaximumTorqueLimiterCoefficient; damp = rs.TorqueDampenerPowerMultiplier; }
            catch { }
        }
        sb.Append("gyro ").Append(string.Format(CI, "{0:R} {1:R} {2:R}", maxTorque, coef, damp)).Append('\n');
        // the game's capacity per push direction (Thrust6Directions: Positive.X = thrusters pushing +X, ...)
        if (data.TryGet<MaxThrustData>(out var mx))
            sb.Append("maxThrust ").Append(V(mx.Regular.Positive)).Append(' ').Append(V(mx.Regular.Negative)).Append('\n');
        if (data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ActiveThrustData>(out var atd))
            sb.Append("gameThrust ").Append(V(atd.ComputedThrustPerFrame * 60f)).Append('\n');
        // thruster: cached position, cached ThrustDirection (the push), max (N), and the push direction ThrustTorque uses
        foreach (var t in aero._thrusterCache)
            sb.Append("thruster ").Append(V(t.GridLocalPosition)).Append(' ').Append(V(t.ThrustDirection)).Append(' ').Append(t.MaxPower.ToString("R", CI))
              .Append(' ').Append(V(t.ThrustDirection)).Append('\n');
        // where the air model thinks the hull is, and the centre of mass its table was built about (both grid-local: they must
        // agree with the mass properties and the thrusters - a table about the wrong point gave the Hercules a 70 m lever)
        sb.Append("aeroState ").Append(aero.LocalNote.Replace(' ', '_')).Append(" table=").Append(aero.UsesTable).Append(" rebuilding=").Append(aero.Rebuilding).Append('\n');
        var table = aero.FxModel?.Table;
        if (table != null) sb.Append("tableCom ").Append(V(table.BuildCom)).Append('\n');
        var centres = new System.Collections.Generic.List<Vector3>(); var areas = new System.Collections.Generic.List<float>();
        if (AeroApi.GetSurface(grid, centres, null, areas, null) > 0)
        {
            Vector3 lo = new Vector3(float.MaxValue), hi = new Vector3(float.MinValue), c = Vector3.Zero; float at = 0f;
            for (int i = 0; i < centres.Count; i++) { lo = Vector3.Min(lo, centres[i]); hi = Vector3.Max(hi, centres[i]); c += centres[i] * areas[i]; at += areas[i]; }
            sb.Append("surface ").Append(V(lo)).Append(' ').Append(V(hi)).Append(' ').Append(V(at > 0f ? c / at : c)).Append('\n');
        }
        // the blocks the hull is built from (occupied-cell boxes, 0.25 m cells, as AeroBench's .boxes: the offline air tests
        // fly the real shape)
        foreach (var block in aero._octree.GetAllCubeBlocks())
        {
            if (block == null) continue;
            foreach (var g in block.GetTransformedOccupiedCellGroups())
                sb.Append("box ").Append(g.Min.X).Append(' ').Append(g.Min.Y).Append(' ').Append(g.Min.Z).Append(' ')
                  .Append(g.Max.X).Append(' ').Append(g.Max.Y).Append(' ').Append(g.Max.Z).Append('\n');
        }
        // (no force table yet - a grid in space has had no air: asked for, so a second dump a few seconds later has it)
        AeroApi.RequestForceTable(grid);
        // the hull's air: 100 m/s, 1.225 kg/m3 (q 6125 Pa), travel directions every 15 deg of yaw and pitch
        for (int pitch = -90; pitch <= 90; pitch += 15)
            for (int yaw = 0; yaw < 360; yaw += 15)
            {
                if ((pitch == -90 || pitch == 90) && yaw > 0) continue;
                float p = pitch * MathF.PI / 180f, y = yaw * MathF.PI / 180f;
                // yaw 0 pitch 0: forward (-Z); yaw about +Y, pitch up toward +Y
                var dir = new Vector3(-MathF.Sin(y) * MathF.Cos(p), MathF.Sin(p), -MathF.Cos(y) * MathF.Cos(p));
                if (AeroApi.PredictHullForces(grid, dir * 100f, 1.225f, 340f, out var f, out var tq, out var area))
                    sb.Append("air ").Append(V(dir)).Append(' ').Append(V(f)).Append(' ').Append(V(tq)).Append(' ').Append(area.ToString("R", CI)).Append('\n');
            }
        return sb.ToString();
    }
}
