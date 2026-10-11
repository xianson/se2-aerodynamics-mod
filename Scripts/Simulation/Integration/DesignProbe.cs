#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.VRage.Core;
using Keen.VRage.Library.Mathematics;
namespace AeroMod;

/// <summary>
/// THE DESIGN PROBE (2026-10-10, for the build-mode hints: "something while you're building", the CoP that "loops
/// through", "what about Mach", "a calculated top speed at sea level" - the user): the whole ship's air force for ANY
/// flow direction and Mach, as if flying - the hull from its force table, the wings from a wing model of the probe's own
/// (the live one's scratch and last results are untouched) - and from it:
///  - the centre of pressure: the point on the force's line of action nearest the centre of mass;
///  - the static stability about the two axes across the flow: the ship turned a little, does the air turn it back
///    (positive: restoring, per radian, over q x frontal area x size - so ships compare);
///  - the streamlining along it (ForceTable.ShapeStreamlining) and the Mach wall it gives;
///  - the top speed at sea level along a direction: the forward thrust (sea-level air, each thruster's profile at the
///    Mach) against the drag, the speed where they meet.
/// Pure reads of the grid's current shape; client callers are paired with the server copy (position, orientation and
/// bounds), as the flame code pairs them. Sea level: 1.225 kg/m3, 340.3 m/s.
/// </summary>
internal static class DesignProbe
{
    public const float SeaRho = 1.225f, SeaSos = 340.3f;
    static readonly Dictionary<AeroGridComponent, CompressibleWingModel> _wingModels = new();

    /// <summary>The server aero of a grid: its own when it is the server copy; else the server copy at the same place,
    /// turned the same way, with the same local bounds (a client grid; the published flow when it is flying).</summary>
    internal static AeroGridComponent ServerAero(Keen.VRage.DCS.Components.Entity grid)
    {
        if (grid == null) return null;
        foreach (var c in grid.Components) if (c is AeroGridComponent a && a.IsServerScene) return a;
        if (AeroEntryFx.FlowOf(grid, out var p) && p.Aero != null) return p.Aero;
        if (!PhysicsHack.Alive(grid.Data)) return null;
        var wt = grid.Data.GetWorldTransform();
        bool hasBox = grid.Data.TryGet<BoundingBoxData>(out var bb);
        AeroGridComponent best = null; double bestD = 1.0;
        lock (AeroGridComponent.Live)
            foreach (var a in AeroGridComponent.Live)
            {
                if (!a.IsServerScene || a.Entity == null || !PhysicsHack.Alive(a.Entity.Data)) continue;
                var awt = a.Entity.Data.GetWorldTransform();
                double d = (awt.Position - wt.Position).Length();
                if (d >= bestD) continue;
                if (MathF.Abs(Quaternion.Dot(awt.Orientation, wt.Orientation)) < 0.9998f) continue;
                if (hasBox && a.Entity.Data.TryGet<BoundingBoxData>(out var ab) && (ab.BoundingBox.Min - bb.BoundingBox.Min).LengthSquared() + (ab.BoundingBox.Max - bb.BoundingBox.Max).LengthSquared() > 0.05f) continue;
                best = a; bestD = d;
            }
        return best;
    }

    /// <summary>The ship's air force and torque (about its centre of mass) moving along travelLocal at this Mach, sea-level
    /// air unless given. False: no force table yet (asked for).</summary>
    internal static bool Forces(AeroGridComponent aero, Vector3 travelLocal, float mach, float rho, float sos, out Vector3 force, out Vector3 torque, out Vector3 com, out float frontal)
    {
        force = torque = com = default; frontal = 0f;
        var model = aero?.FxModel; var table = model?.Table;
        if (table == null || model.InnerModel is not DampedShadowedDragModel d) { if (aero != null) aero.EntryWantsTable = true; return false; }
        com = aero.Data.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mp) ? mp.CenterOfMass : table.BuildCom;
        Vector3 v = Vector3.Normalize(travelLocal) * Math.Max(1f, mach * sos);
        var atm = new AtmosphereState { Density = rho, SpeedOfSound = sos, Pressure = 101325, Temperature = 288.15, DynamicViscosity = 1.8e-5 };
        var ctx = new AeroContext(null, null, v, atm, com);
        var r = table.Evaluate(ctx, d.SubsonicLimit, d.SupersonicLimit, d.Streamlining);
        force = r.Force; torque = r.Torque; frontal = r.FrontalArea;
        var wings = model.Wings;
        if (wings != null && wings.Count > 0)
        {
            CompressibleWingModel wm;
            lock (_wingModels) { if (!_wingModels.TryGetValue(aero, out wm)) _wingModels[aero] = wm = new CompressibleWingModel(); }
            List<WingForceResult> wf;
            lock (wm) wf = wm.ComputeWingForces(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(wings), ctx);
            if (wf != null)
                foreach (var w in wf)
                {
                    var fw = w.LiftForce + w.InducedDrag;
                    force += fw; torque += Vector3.Cross(w.ApplicationPoint - com, fw);
                }
        }
        return float.IsFinite(force.X + force.Y + force.Z + torque.X + torque.Y + torque.Z);
    }

    /// <summary>Everything the build hints show for one flow direction (grid-local, the way the ship travels).</summary>
    internal static bool Probe(AeroGridComponent aero, Vector3 travelLocal, float mach, out Vector3 cop, out Vector3 force, out Vector3 torque,
        out float stabA, out float stabB, out float streamlining)
    {
        cop = force = torque = default; stabA = stabB = 0f; streamlining = -1f;
        if (travelLocal.LengthSquared() < 1e-8f) return false;
        var dHat = Vector3.Normalize(travelLocal);
        if (!Forces(aero, dHat, mach, SeaRho, SeaSos, out force, out torque, out var com, out float frontal)) return false;
        float f2 = force.LengthSquared();
        cop = f2 > 1e-6f ? com + Vector3.Cross(force, torque) / f2 : com;
        // static stability about the two axes across the flow: the ship turned by +e about axis a sees the flow turned by -e
        Vector3 a1 = Vector3.Normalize(Vector3.Cross(dHat, MathF.Abs(dHat.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX)), a2 = Vector3.Cross(dHat, a1);
        const float e = 0.035f;   // (2 degrees)
        float size = Math.Max(1f, aero.FxModel?.Table != null ? MathF.Sqrt(Math.Max(1f, frontal)) : 1f);
        float qRef = 0.5f * SeaRho * MathF.Pow(Math.Max(1f, mach * SeaSos), 2) * Math.Max(1f, frontal) * size;
        float Stab(Vector3 a)
        {
            if (!Forces(aero, Vector3.Transform(dHat, Quaternion.CreateFromAxisAngle(a, -e)), mach, SeaRho, SeaSos, out _, out var tp, out _, out _)) return 0f;
            if (!Forces(aero, Vector3.Transform(dHat, Quaternion.CreateFromAxisAngle(a, +e)), mach, SeaRho, SeaSos, out _, out var tm, out _, out _)) return 0f;
            float dTdTheta = Vector3.Dot(tp - tm, a) / (2f * e);   // (the body turned +theta about a: the flow turned -theta)
            return -dTdTheta / qRef;                                // positive: the torque turns it back
        }
        stabA = Stab(a1); stabB = Stab(a2);
        var table = aero.FxModel.Table;
        streamlining = table.StreamliningAlong(dHat, aero.FxModel.InnerModel is DampedShadowedDragModel dm ? dm.Streamlining : 0.4f);
        return true;
    }

    /// <summary>The thrust along a direction at sea level and a Mach (N): each thruster pushing that way, at its live
    /// maximum x its class's sea-level efficiency x its profile at the Mach, by how much it points that way.</summary>
    internal static float ThrustAlong(AeroGridComponent aero, Vector3 dirLocal, float mach, out Vector3 centre)
    {
        centre = default;
        var cache = aero?._thrusterCache; if (cache == null) return 0f;
        var dHat = Vector3.Normalize(dirLocal);
        float sum = 0f; Vector3 moment = Vector3.Zero;
        for (int i = 0; i < cache.Count; i++)
        {
            var t = cache[i];
            float along = Vector3.Dot(t.ThrustDirection, dHat);
            if (along < 0.5f) continue;
            float max = ThrustTorque.LiveMax(t.ThrusterEntity, true); if (max < 0f) max = t.MaxPower;
            float eff = ThrustTorque.SeaLevelEfficiency(t.ThrusterEntity, SeaRho);
            float prof = t.Profile == null ? 1f : (float)t.Profile.Evaluate(mach);
            float f = max * eff * Math.Max(0f, prof) * along;
            sum += f; moment += t.GridLocalPosition * f;
        }
        if (sum > 0f) centre = moment / sum;
        return sum;
    }

    /// <summary>The top speed at sea level travelling along a direction: where the forward thrust (at that Mach) meets the
    /// drag along it. NaN: no table; +inf: thrust beats drag past 3 km/s.</summary>
    internal static float TopSpeed(AeroGridComponent aero, Vector3 dirLocal)
    {
        var dHat = Vector3.Normalize(dirLocal);
        float Net(float v)
        {
            float m = v / SeaSos;
            if (!Forces(aero, dHat, m, SeaRho, SeaSos, out var f, out _, out _, out _)) return float.NaN;
            // (Forces evaluates at speed max(1, m sos): the drag scales from there)
            float drag = -Vector3.Dot(f, dHat);
            return ThrustAlong(aero, dHat, m, out _) - drag;
        }
        float lo = 1f, hi = 3000f, nlo = Net(lo);
        if (float.IsNaN(nlo)) return float.NaN;
        if (nlo <= 0f) return 0f;
        // the first speed where drag catches up: step up, then bisect
        float prev = lo;
        for (float v = 25f; v <= hi; v *= 1.25f)
        {
            float n = Net(v);
            if (float.IsNaN(n)) return float.NaN;
            if (n <= 0f)
            {
                float a = prev, b = v;
                for (int k = 0; k < 24; k++) { float mid = 0.5f * (a + b); if (Net(mid) > 0f) a = mid; else b = mid; }
                return 0.5f * (a + b);
            }
            prev = v;
        }
        return float.PositiveInfinity;
    }

    internal static void Forget(AeroGridComponent aero) { lock (_wingModels) _wingModels.Remove(aero); }
}
