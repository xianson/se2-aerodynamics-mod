#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections.Generic;

namespace AeroMod;

/// <summary>
/// THRUST VECTORING IN GAME (2026-10-20): the grid's gimballed actuators as ThrustVector nozzles in ThrustTorque's attitude law -
/// any ship, generic attitude control only. Two kinds: a LIT solid rocket nozzle with its Thrust Vectoring on (its panel:
/// AeroSolidNozzleComponent.GimbalOn / GimbalDeg) at the game's thrust on it now (ThrustNow), and any thruster in the grid's
/// cache with a gimbal cone (AeroThrustSettingsComponent.GimbalDeg &gt; 0 - saved with it; AeroApi.SetThrusterGimbal) at the
/// force the attitude solve gave it. ThrustTorque collects them after the game's share (Collect), plans the attitude law's
/// braking with their reach (Reach), and after the thrusters and the gyros asks them for what is still missing of the law's need
/// (Solve: ThrustVector.Solve - each within its cone and its rate, ThrustVector.DefaultRateDeg); with no attitude law flying
/// (unpiloted, no target) they go back to straight at their rate. What they give is the thrust's own change (its side force, the
/// little it loses along its axis) and that force's moment - applied by ThrustTorque like every other actuator's, nothing else.
/// Each actuator keeps its deflection (GimbalDeflect) between ticks. Server scene only (the lit nozzles' and the settings' copies
/// that fly); the arrays grown only (no per-tick allocation).
/// </summary>
internal static class ThrustVectorGame
{
    /// <summary>The grid's gimballed actuators now into aero._tvc (count returned): the lit solid nozzles with their gimbal on
    /// (aero._solidSteer, already this tick's), then the cached thrusters with a cone at their force (thrust[i]; a reversible one
    /// pushing backwards along -dir).</summary>
    internal static int Collect(AeroGridComponent aero, Vector3 com, List<ThrusterInfo> thrusters, Vector3[] dirs, float[] thrust, int n, int steerN)
    {
        if (!aero.IsServerScene) { aero._tvcN = 0; return 0; }
        int k = 0;
        var steer = aero._solidSteer;
        for (int j = 0; j < steerN && j < steer.Count; j++) if (steer[j].GimbalOn && steer[j].GimbalDeg > 0f) k++;
        for (int i = 0; i < n && i < thrusters.Count; i++) { var st = thrusters[i].Settings; if (st != null && st.GimbalDeg > 0f) k++; }
        if (k == 0) { aero._tvcN = 0; return 0; }
        if (aero._tvc.Length < k) { int c = Math.Max(k, 4); aero._tvc = new ThrustVector.Nozzle[c]; aero._tvcSolid = new AeroSolidNozzleComponent[c]; aero._tvcThr = new int[c]; }
        ThrustVector.Nozzle[] z = aero._tvc; int m = 0;
        for (int j = 0; j < steerN && j < steer.Count; j++)
        {
            var s = steer[j];
            if (!s.GimbalOn || !(s.GimbalDeg > 0f)) continue;
            z[m] = new ThrustVector.Nozzle { Pos = s.PosLocal - com, Dir = s.PushLocal, Thrust = MathF.Max(0f, s.ThrustNow), MaxRad = Cone(s.GimbalDeg), RateRad = ThrustVector.DefaultRateDeg * ApMath.Deg, Deflect = s.GimbalDeflect };
            aero._tvcSolid[m] = s; aero._tvcThr[m] = -1; m++;
        }
        for (int i = 0; i < n && i < thrusters.Count; i++)
        {
            var st = thrusters[i].Settings;
            if (st == null || !(st.GimbalDeg > 0f)) continue;
            float f = thrust != null && i < thrust.Length ? thrust[i] : 0f;
            z[m] = new ThrustVector.Nozzle { Pos = thrusters[i].GridLocalPosition - com, Dir = f < 0f ? -dirs[i] : dirs[i], Thrust = MathF.Abs(f), MaxRad = Cone(st.GimbalDeg), RateRad = ThrustVector.DefaultRateDeg * ApMath.Deg, Deflect = st.GimbalDeflect };
            aero._tvcSolid[m] = null; aero._tvcThr[m] = i; m++;
        }
        aero._tvcN = m;
        return m;
    }

    static float Cone(float deg) => Math.Clamp(deg, 0f, GimbalLimits.MaxDeg) * ApMath.Deg;

    /// <summary>The thrusters' gimbals at the forces the solve gave them (use[i]; the solid nozzles keep the game's thrust).</summary>
    internal static void Refresh(AeroGridComponent aero, Vector3[] dirs, float[] use)
    {
        ThrustVector.Nozzle[] z = aero._tvc;
        for (int m = 0; m < aero._tvcN; m++)
        {
            int i = aero._tvcThr[m];
            if (i < 0 || use == null || i >= use.Length) continue;
            float f = use[i];
            z[m].Thrust = MathF.Abs(f);
            var d = f < 0f ? -dirs[i] : dirs[i];
            if (Vector3.Dot(d, z[m].Dir) < 0f) { z[m].Dir = d; z[m].Deflect = -z[m].Deflect; }   // (a reversible one turned over: its deflection the same way in space)
        }
    }

    /// <summary>The torque the gimbals can give per local axis (N m): the attitude law's planning.</summary>
    internal static Vector3 Reach(AeroGridComponent aero) => aero._tvcN > 0 ? ThrustVector.Reach(aero._tvc, aero._tvcN) : Vector3.Zero;

    /// <summary>The gimbals asked for 'ask' (local N m; zero: back to straight) this tick: their deflections kept on each actuator,
    /// the force and torque they give (local).</summary>
    internal static void Solve(AeroGridComponent aero, Vector3 ask, float dt, out Vector3 force, out Vector3 torque, out float maxDeg)
    {
        force = torque = Vector3.Zero; maxDeg = 0f;
        int c = aero._tvcN;
        if (c == 0) return;
        if (!float.IsFinite(ask.X) || !float.IsFinite(ask.Y) || !float.IsFinite(ask.Z)) ask = Vector3.Zero;
        ThrustVector.Nozzle[] z = aero._tvc;
        ThrustVector.Solve(ask, z, c, dt, out force, out torque);
        for (int m = 0; m < c; m++)
        {
            var d = z[m].Deflect;
            if (!float.IsFinite(d.X) || !float.IsFinite(d.Y) || !float.IsFinite(d.Z)) { d = Vector3.Zero; force = torque = Vector3.Zero; }
            maxDeg = MathF.Max(maxDeg, d.Length() / ApMath.Deg);
            var s = aero._tvcSolid[m];
            if (s != null) s.GimbalDeflect = d;
            else
            {
                int i = aero._tvcThr[m];
                var th = aero._thrusterCache;
                if (i >= 0 && i < th.Count && th[i].Settings != null) th[i].Settings.GimbalDeflect = d;
            }
        }
    }
}
