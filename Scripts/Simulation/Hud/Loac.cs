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
    /// <summary>THE PITCH UPSET MARGIN's (EvaluatePitchMargin): under PitchLimitDeg - now or PitchLookahead s ahead at the Mach
    /// it is gaining - PITCH LIMIT; under PitchLostDeg now, PITCH LOST.</summary>
    public static float PitchLimitDeg = 6f, PitchLostDeg = 2f, PitchLookahead = 4f;
    // diagnostics (harness aeroget)
    public static string Last = "-", LastPitch = "-";
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
    /// <summary>The sim frame SurfacePush / SurfacePull were last measured on (AllocateSurfaces: server, while the aero-aware law
    /// flies and the air is fast enough for a result). They are not cleared when it stops - a slowed or parked ship kept its
    /// last fast-flight reach - so a reader that acts on them asks SurfaceReachFresh (2026-10-17: the rotor limiter).</summary>
    internal int _surfReachFrame = int.MinValue / 2;
    /// <summary>The surfaces' reach was measured within the last few frames (the slowest compute interval, 4, plus the frame
    /// budget's most skips): it describes the air now.</summary>
    internal bool SurfaceReachFresh => _simFrameCount - _surfReachFrame <= 4 + AeroFrameBudget.MaxSkips + 1;

    /// <summary>The surfaces' torque left in each direction from where they are trimmed (the allocator's measure this tick:
    /// each one's torque at its measured inputs less at its trim/flap base, the most either way): per axis, the most they add toward
    /// + (SurfacePush) and toward - (SurfacePull, as a magnitude) - a surface trimmed hard one way has little left that way
    /// and plenty the other. (Checked in game on the Tu-134, 2026-10-13, tools/harness/loacdiag.sh: the airframe torque
    /// predicted at the attitude now matched the applied to the kN m; gliding with idle engines it needs ~2.4 MN m of
    /// nose-up against ~3.2 available - 63% climbing to 85% as it slowed, and past 100% at 218 m/s: real.)</summary>
    void SurfaceTravel(List<(ControlSurface cs, Vector3 a)> list, float scale, Vector3 t0)
    {
        Vector3 push = Vector3.Zero, pull = Vector3.Zero;
        for (int k = 0; k < list.Count; k++)
        {
            // (the most torque change either way at any input it may be given, MEASURED there - AllocateSurfaces' knots, 2026-10-19;
            //  a straight line from a slope about the trim gave reach past the stall the surface did not have, or the wrong way)
            int o = k * ControlSurface.Knots;
            Vector3 hi = Vector3.Zero, lo = Vector3.Zero;
            for (int j = o; j < o + ControlSurface.Knots && j < _knotT.Length; j++) { var t = _knotT[j] * scale; hi = Vector3.Max(hi, t); lo = Vector3.Min(lo, t); }
            push += hi; pull -= lo;
        }
        SurfacePush = push; SurfacePull = pull; SurfaceNeutral = t0 * scale; _surfReachFrame = _simFrameCount;
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
        EvaluatePitchMargin(snap);
        // THE AUTOPILOT's reference governor (EnvelopeGuard, 2026-10-19): the same prediction for the attitude its guidance WANTS
        // (before it is ever commanded) and for holding the attitude now - whatever the HUD warning's setting
        if (snap.HasProbe && speed > StallWarning.MinSpeed && snap.Density > StallWarning.MinDensity) EvaluateApProbe(snap, L, speed);

        static string Kn(Vector3 v) => $"({v.X / 1000f:F0},{v.Y / 1000f:F0},{v.Z / 1000f:F0})kNm";
        // (one axis: what it needs over what it has - none to hold, nothing; none to give, everything)
        static float Axis(float need, float have) => LoacMath.Axis(need, have);
    }

    /// <summary>0 none, 1 PITCH LIMIT ahead only, 2 PITCH LIMIT now, 3 PITCH LOST (held LoacWarning.Hold); the upset margin now
    /// and ahead (deg; +inf: stable in pitch), the Mach ahead it was judged at, the time an upset takes to grow e-fold (s; +inf:
    /// stable). PitchValid: evaluated.</summary>
    internal int PitchState; internal bool PitchValid;
    internal float PitchMarginDeg = float.PositiveInfinity, PitchAheadDeg = float.PositiveInfinity, PitchMachAhead, PitchDivergeS = float.PositiveInfinity;
    int _pitchRaw; float _pitchHeld, _pitchLastMach = -1f, _pitchMachRate;

    /// <summary>
    /// THE PITCH UPSET MARGIN, PREDICTED (2026-10-23, the user: "I don't see forewarning when I'm about to lose pitch control" -
    /// the F-6 at high Mach; AeroBench damping on it: unstable in pitch at every Mach, its slope 9 MN m/rad at Mach 0.8 and
    /// 136 at Mach 3 against 7 MN m of gyros - what it can turn back fell from ~43 deg to ~3 deg, and LOAC, holding the
    /// attitude, only ever saw the trim). About the cockpit's pitch axis, in the air now (its density, speed of sound, the flow
    /// at the angle of attack and sideslip flown): the air's torque with the ship turned +-2 deg (DesignProbe.ForcesAt) - its
    /// slope - against the gyros', thrusters' and surfaces' reach either way less what the trim uses: the largest upset turned
    /// back (UpsetMath.Margin). Again at the Mach PitchLookahead s ahead at the rate it is gaining Mach (the surfaces'
    /// reach and trim with the dynamic pressure there). The e-fold time sqrt(I / slope) from the physics' inertia.
    /// </summary>
    void EvaluatePitchMargin(StructSnap snap)
    {
        int raw = 0;
        var L = snap.L;
        float speed = L.AirVel.Length();
        PitchValid = false;
        // (HasTarget: the aero-aware attitude law flies it - only then is its reach known; the game's own law left it zero, a margin of 0)
        if (LoacWarning.Enabled && snap.Piloted && snap.HasTarget && speed > StallWarning.MinSpeed && snap.Density > StallWarning.MinDensity)
        {
            float mach = speed / MathF.Max(snap.SoS, 1f);
            // (the Mach's rate, smoothed over about a second of evaluations - gaining only: a ship slowing down is not warned ahead)
            if (_pitchLastMach >= 0f && snap.Dt > 1e-3f)
            {
                float r = (mach - _pitchLastMach) / snap.Dt, k = MathF.Min(1f, snap.Dt);
                _pitchMachRate += (r - _pitchMachRate) * k;
            }
            _pitchLastMach = mach;
            var p = Vector3.Normalize(Vector3.Transform(Vector3.UnitX, snap.Cockpit));
            var flow = L.AirVel / speed;
            float now = Margin(mach, 1f, out float slope);
            float machAhead = mach + MathF.Min(0.5f, MathF.Max(0f, _pitchMachRate) * LoacWarning.PitchLookahead);
            float ahead = machAhead > mach + 0.01f ? Margin(machAhead, machAhead * machAhead / (mach * mach), out _) : now;
            if (!float.IsNaN(now) && !float.IsNaN(ahead))
            {
                PitchValid = true;
                PitchMarginDeg = now * (180f / MathF.PI); PitchAheadDeg = ahead * (180f / MathF.PI); PitchMachAhead = machAhead;
                // (I about p: 1 / (p . I^-1 p), the inverse tensor's principal axes turned to the grid's - GyroControl's)
                var pp = Vector3.Transform(p, Quaternion.Inverse(snap.InertiaRot));
                float invI = Vector3.Dot(pp * snap.InvInertia, pp);
                PitchDivergeS = slope > 1f && invI > 0f ? MathF.Sqrt(1f / (invI * slope)) : float.PositiveInfinity;
                float lim = LoacWarning.PitchLimitDeg, lost = LoacWarning.PitchLostDeg;
                raw = PitchMarginDeg < lost ? 3 : PitchMarginDeg < lim ? 2 : PitchAheadDeg < lim ? 1 : 0;
                LoacWarning.LastPitch = $"{Entity?.DebugName}: state {raw}, M{mach:F2} margin {PitchMarginDeg:F1} deg, ahead M{machAhead:F2} {PitchAheadDeg:F1} deg, slope {slope / 1e6f:F2} MN m/rad, e-fold {PitchDivergeS:F2} s, axis {p}";
            }

            // the margin at this Mach (the air's density and speed of sound now), the surfaces' reach and trim x qScale
            float Margin(float m, float qScale, out float k)
            {
                k = 0f;
                const float e = 0.035f;   // (2 deg; the body turned +e sees the flow turned -e)
                if (!DesignProbe.ForcesAt(this, flow, m, snap.Density, snap.SoS, L.AeroCom, out _, out var t0, out _)) return float.NaN;
                if (!DesignProbe.ForcesAt(this, Vector3.Transform(flow, Quaternion.CreateFromAxisAngle(p, -e)), m, snap.Density, snap.SoS, L.AeroCom, out _, out var tp, out _)) return float.NaN;
                if (!DesignProbe.ForcesAt(this, Vector3.Transform(flow, Quaternion.CreateFromAxisAngle(p, e)), m, snap.Density, snap.SoS, L.AeroCom, out _, out var tm, out _)) return float.NaN;
                k = Vector3.Dot(tp - tm, p) / (2f * e);
                float need0 = Vector3.Dot(t0 + snap.SurfNeutral * qScale, p);
                float pos = UpsetMath.Toward(p, snap.Reach, snap.Reach) + UpsetMath.Toward(p, snap.SurfPush, snap.SurfPull) * qScale;
                float neg = UpsetMath.Toward(-p, snap.Reach, snap.Reach) + UpsetMath.Toward(-p, snap.SurfPush, snap.SurfPull) * qScale;
                return UpsetMath.Margin(k, need0, pos, neg);
            }
        }
        else { PitchMarginDeg = PitchAheadDeg = PitchDivergeS = float.PositiveInfinity; _pitchLastMach = -1f; _pitchMachRate = 0f; }
        if (raw != _pitchRaw) { _pitchRaw = raw; _pitchHeld = 0f; }
        else _pitchHeld += snap.Dt;
        if (_pitchHeld >= LoacWarning.Hold || raw == PitchState) PitchState = raw;
    }

    /// <summary>The autopilot's probe (StructSnap.Probe): the worst need / reach on the way from now to it (PathPoints), and at the
    /// attitude now - LoacMath over DesignProbe.ForcesAt, as EvaluateLoac. Published for the flight director (ApLoac*).</summary>
    void EvaluateApProbe(StructSnap snap, StructureEval.Loads L, float speed)
    {
        var airWorld = Vector3.Transform(L.AirVel, snap.Orient);
        float mach = speed / MathF.Max(snap.SoS, 1f);
        float Need(Quaternion q)
        {
            var airLocal = Vector3.Transform(airWorld, Quaternion.Inverse(q));
            if (!DesignProbe.ForcesAt(this, airLocal / speed, mach, snap.Density, snap.SoS, L.AeroCom, out _, out var torque, out _)) return 0f;
            return LoacMath.Worst(torque + snap.SurfNeutral, snap.Reach, snap.SurfPush, snap.SurfPull);
        }
        float here = Need(snap.Orient), worst = 0f;
        int n = Math.Max(1, LoacWarning.PathPoints);
        for (int k = 1; k <= n; k++) worst = MathF.Max(worst, Need(Quaternion.Slerp(snap.Orient, snap.Probe, k / (float)n)));
        ApLoacHere = here; ApLoacProbe = worst; ApLoacAt = snap.Probe;
        System.Threading.Volatile.Write(ref ApLoacStamp, System.Diagnostics.Stopwatch.GetTimestamp());
    }
}
