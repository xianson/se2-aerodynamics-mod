#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>The loss-of-attitude-control warning's settings (AeroSettings: Hud.Loac).</summary>
public static class LoacWarning
{
    public static bool Enabled = true;
    /// <summary>The share of the available torque the hold needs at which it warns (CONTROL LIMIT), and at which control
    /// is lost (LOSS OF CONTROL).</summary>
    public static float Limit = 0.9f, Lost = 1f;
    /// <summary>A state must hold this long (s) before it shows or clears (no flicker).</summary>
    public static float Hold = 0.25f;
    /// <summary>Attitudes checked on the way from now to the commanded one (the last is the commanded itself).</summary>
    public static int PathPoints = 3;
    // diagnostics (harness aeroget)
    public static string Last = "-";
}

/// <summary>
/// LOSS OF ATTITUDE CONTROL, PREDICTED (2026-10-13, the user: a LOAC warning "for when a commanded attitude is unable to
/// be reached"; "it needs to be predictive and fast, not 2 sec delay - I lost airspeed and my control surfaces couldn't get
/// me into the attitude I needed"). The commanded attitude (TargetControlData's target: the setpoint may be far off - a
/// slew to it is fine), and PathPoints attitudes on the way there: at each, the air's torque on the whole ship at the
/// current speed (DesignProbe.ForcesAt - the force table: the airflow as it would meet the ship turned so) against what
/// can hold it there - the gyros' reach and the thrusters' (ThrustTorque, as the attitude law used them this tick) and the
/// control surfaces' at full throw at the dynamic pressure now (their torque per unit input, as the surface allocator
/// measured it this tick). Needed over available on the worst axis of the worst attitude: past Limit, CONTROL LIMIT; past
/// Lost, LOSS OF CONTROL - before the ship ever fails to get there. In the structure's background evaluation (the
/// player's grid, a few times a second): PathPoints lookups.
/// </summary>
public partial class AeroGridComponent
{
    /// <summary>0 none, 1 at the limit, 2 lost (held LoacWarning.Hold); the worst needed / available torque on the way to
    /// the commanded attitude.</summary>
    internal int LoacState;
    internal float LoacRatio;
    int _loacRaw; float _loacHeld;

    // what the attitude law and the surface allocator had this tick (simulation thread; snapshotted for the evaluation):
    // the commanded attitude, the gyros' and thrusters' reach (local N m per axis), and the surfaces' reach at full throw
    // and their torque at their trim (local N m)
    internal Quaternion LoacTarget; internal bool LoacHasTarget;
    internal Vector3 LoacReach, SurfacePush, SurfacePull, SurfaceNeutral;

    /// <summary>The surfaces' torque left in each direction from where they are trimmed (the allocator's measure this tick:
    /// each one's torque per unit input, about its trim/flap base; inputs reach +-1): per axis, the most they add toward
    /// + (SurfacePush) and toward - (SurfacePull, as a magnitude) - a surface trimmed hard one way has little left that way
    /// and plenty the other. (Checked in game on the Tu-134, 2026-10-13, tools/harness/loacdiag.sh: the airframe torque
    /// predicted at the attitude now matched the applied to the kN m; gliding with idle engines it needs ~2.4 MN m of
    /// nose-up against ~3.2 available - 63% climbing to 85% as it slowed, and past 100% at 218 m/s: real.)</summary>
    void SurfaceTravel(List<(ControlSurface cs, Vector3 a)> list, float scale, Vector3 t0)
    {
        Vector3 push = Vector3.Zero, pull = Vector3.Zero;
        for (int k = 0; k < list.Count; k++)
        {
            var a = list[k].a * scale; float b = k < _allocBase.Count ? _allocBase[k] : 0f;
            Vector3 up = a * (1f - b), down = a * (-1f - b);   // (the torque change at full throw either way)
            push += Vector3.Max(Vector3.Max(up, down), Vector3.Zero);
            pull -= Vector3.Min(Vector3.Min(up, down), Vector3.Zero);
        }
        SurfacePush = push; SurfacePull = pull; SurfaceNeutral = t0 * scale;
    }

    void EvaluateLoac(StructSnap snap)
    {
        int raw = 0;
        var L = snap.L;
        float speed = L.AirVel.Length();
        if (LoacWarning.Enabled && snap.HasTarget && speed > StallWarning.MinSpeed && snap.Density > StallWarning.MinDensity)
        {

            var airWorld = Vector3.Transform(L.AirVel, snap.Orient);
            float mach = speed / MathF.Max(snap.SoS, 1f), worst = 0f;
            int n = Math.Max(1, LoacWarning.PathPoints);
            for (int k = 1; k <= n; k++)
            {
                var q = Quaternion.Slerp(snap.Orient, snap.Target, k / (float)n);
                var airLocal = Vector3.Transform(airWorld, Quaternion.Inverse(q));
                if (!DesignProbe.ForcesAt(this, airLocal / speed, mach, snap.Density, snap.SoS, L.AeroCom, out _, out var torque, out _)) continue;
                var need = torque + snap.SurfNeutral;   // (to cancel: the air's torque there, the surfaces at their trim with it)
                // (per axis, the actuators must give -need: the gyros' and thrusters' reach, and the surfaces' travel that way)
                worst = MathF.Max(worst, Axis(need.X, snap.Reach.X + (need.X < 0f ? snap.SurfPush.X : snap.SurfPull.X)));
                worst = MathF.Max(worst, Axis(need.Y, snap.Reach.Y + (need.Y < 0f ? snap.SurfPush.Y : snap.SurfPull.Y)));
                worst = MathF.Max(worst, Axis(need.Z, snap.Reach.Z + (need.Z < 0f ? snap.SurfPush.Z : snap.SurfPull.Z)));
            }
            LoacRatio = worst;
            raw = worst > LoacWarning.Lost ? 2 : worst > LoacWarning.Limit ? 1 : 0;
            // (diagnostics: the prediction at the attitude NOW against the torque actually applied this tick - the two must
            //  agree for the prediction elsewhere to mean anything - and the reach by source)
            string now = "-";
            if (DesignProbe.ForcesAt(this, L.AirVel / speed, mach, snap.Density, snap.SoS, L.AeroCom, out _, out var tNow, out _))
                now = $"airframe now predicted {Kn(tNow)} vs applied {Kn(snap.Airframe)}";
            LoacWarning.Last = $"{Entity?.DebugName}: state {raw}, needs {worst * 100f:0}% | {now} | reach gyro+thr {Kn(snap.Reach)} surf +{Kn(snap.SurfPush)} -{Kn(snap.SurfPull)} trim {Kn(snap.SurfNeutral)}";
        }
        if (raw != _loacRaw) { _loacRaw = raw; _loacHeld = 0f; }
        else _loacHeld += snap.Dt;
        if (_loacHeld >= LoacWarning.Hold || raw == LoacState) LoacState = raw;

        static string Kn(Vector3 v) => $"({v.X / 1000f:F0},{v.Y / 1000f:F0},{v.Z / 1000f:F0})kNm";
        // (one axis: what it needs over what it has - none to hold, nothing; none to give, everything)
        static float Axis(float need, float have) => MathF.Abs(need) < 1f ? 0f : have <= 1f ? 10f : MathF.Abs(need) / have;
    }
}
