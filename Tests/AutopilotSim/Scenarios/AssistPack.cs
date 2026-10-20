using AeroMod;

/// <summary>
/// THE FLIGHT ASSISTS (2026-10-19, Lift/FlightAssists.cs) through the plant:
///  - OFFSET THRUST (Assists.OffsetThrust): only the uncommanded part of the thrusters' offset torque is scaled - an offset the
///    attitude law cancels is untouched (the flight the same), one past the thrusters' reach is taken off at 0; every tick the
///    torque applied is never further from the law's ask than the real one, never past it.
/// </summary>
sealed class AssistPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("assist: offset thrust - a cancelled offset is untouched", true, CancelledOffset);
        r.Add("assist: offset thrust - past the thrusters' reach it is taken off", true, OffsetPastReach);
    }

    /// <summary>One run of an unpiloted ship (dampeners on: the damp branch) at an offset-thrust scale: the most tilt, the most
    /// removed (N m), the offset (N m), and the per-tick check's failures (further from the ask, or past it).</summary>
    static (float tilt, float removed, float offset, int bad, Quaternion end) Fly(SimShip ship, float scale, string tag)
    {
        float keep = FlightAssists.OffsetThrustScale;
        FlightAssists.OffsetThrustScale = scale;
        try
        {
            var run = new Run($"{tag} at {scale:0.0}", ship);
            float removed = 0f, offset = 0f; int bad = 0;
            run.Each(rr =>
            {
                var p = rr.P;
                removed = MathF.Max(removed, p.LastAssistRemoved.Length()); offset = MathF.Max(offset, p.LastOffset.Length());
                Vector3 before = p.LastThrApplied - p.LastThrAsked, after = before - p.LastAssistRemoved;
                for (int k = 0; k < 3; k++)
                {
                    float b = k == 0 ? before.X : k == 1 ? before.Y : before.Z, a = k == 0 ? after.X : k == 1 ? after.Y : after.Z;
                    if (MathF.Abs(a) > MathF.Abs(b) + 1e-2f || a * b < -1e-3f) bad++;
                }
            });
            run.Measure();
            run.Fly(16f);
            float tilt = run.MaxTilt;
            Program.LedgerPublic(run, true);
            return (tilt, removed, offset, bad, run.P.Q);
        }
        finally { FlightAssists.OffsetThrustScale = keep; }
    }

    static void CancelledOffset()
    {
        // (the centre of mass 0.8 m / 0.6 m off: the lift thrusters can re-share for it - the law cancels it with them)
        Func<SimShip> ship = () => Ships.Vtol(2.6f, comOff: new Vector3(0.8f, 0f, 0.6f), name: "VTOL CoM off (in reach)");
        var real = Fly(ship(), 1f, "offset in reach");
        var none = Fly(ship(), 0f, "offset in reach");
        float diff = ApMath.Angle(real.end, none.end) / ApMath.Deg;
        Sim.Note($"offset thrust in reach: offset {real.offset / 1e3f:F1} kN m; realistic tilt {real.tilt:F3} deg, at 0 {none.tilt:F3} deg, attitude {diff:F4} deg apart after 16 s; the most taken off {none.removed:F1} N m");
        // (what is taken off is only the solve's residual - the damp law rests inside its rest rate - a thousandth of the offset)
        Sim.Check(none.removed <= 1e-3f * MathF.Max(none.offset, 1f) && diff < 0.25f && real.tilt < 0.25f,
            $"offset thrust in reach: the cancelled offset is the law's - at most {none.removed:F1} N m taken off (0.1 % of {none.offset:F0}), the flight within {diff:F3} deg");
        Sim.Check(none.bad == 0, $"offset thrust in reach: the applied torque never further from the ask nor past it ({none.bad} axis-ticks)");
    }

    static void OffsetPastReach()
    {
        // (one lift thruster at 35 %, barely the thrust to hover, tiny gyros: the others cannot balance it - the ship rolls)
        Func<SimShip> ship = () => Ships.Vtol(2.2f, weak: 0.35f, gyro: 1e3f, name: "VTOL weak lifter (past reach)");
        var real = Fly(ship(), 1f, "offset past reach");
        var half = Fly(ship(), 0.5f, "offset past reach");
        var none = Fly(ship(), 0f, "offset past reach");
        Sim.Note($"offset thrust past reach: tilt realistic {real.tilt:F1} deg, at 0.5 {half.tilt:F1} deg, at 0 {none.tilt:F1} deg; the most taken off {none.removed / 1e3f:F1} kN m of {none.offset / 1e3f:F1}");
        Sim.Check(real.tilt > 3f && none.removed > 0f && none.tilt < 0.5f * real.tilt && half.tilt <= real.tilt + 0.1f,
            $"offset thrust past reach: realistic it departs ({real.tilt:F1} deg), at 0 the uncancelled offset is taken off ({none.tilt:F1} deg; {none.removed / 1e3f:F1} kN m)");
        Sim.Check(none.bad == 0 && half.bad == 0, $"offset thrust past reach: the applied torque never further from the ask nor past it ({none.bad} + {half.bad} axis-ticks)");
    }
}
