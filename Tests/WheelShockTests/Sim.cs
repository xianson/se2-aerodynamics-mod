#pragma warning disable
using AeroMod;

/// <summary>
/// A TOUCHDOWN, time-stepped at 60 Hz as the game steps it (1-D, vertical): a SHIP SHARE (the mass one wheel carries) and its WHEEL
/// (the wheel body's mass), the suspension between them with its hard limits (0 = hanging, stroke = bottomed; inelastic - the
/// game's restitution 0), a rigid GROUND under the tyre (inelastic, push only), gravity 2 g. Each step, in the game's order:
///  1. the mod (before the physics step): the airshock's state from the wheel's height and sink; the wheel constraint's factors
///     (vanilla: the sliders; airshock armed: the stiffness cap and full damping; oleo: the constraint's spring at its share); the
///     oleo's force as a pair of impulses (WheelOleo.Step - the mass it moves: the ship share on the ground, the reduced mass in the air);
///  2. the physics: gravity; the wheel constraint's SOFT spring as the solver makes it - factor x tau x the effective mass / dt^2 and
///     its damping factor x the effective mass / dt, the effective mass the constraint's own (ship and wheel reduced - the wheel's);
///     then contact and the limits solved by sequential impulses (8 passes, accumulated, clamped);
///  3. positions.
/// Until the first contact the sink is HELD (the in-game test's DevDrop holds the ship's descent too): the touch is at the asked speed.
/// Results: the touch, the deepest compression, bottoming (the hard limit's impulse), the rebound (the ship's highest upward speed
/// after the touch), bounces (the tyre off the ground for 3+ steps after the touch), the settle time, the peak deceleration.
/// </summary>
sealed class Sim
{
    public enum Kind { Vanilla, Airshock, Oleo }
    public const float G = 2f * 9.81f, Dt = 1f / 60f;

    public Kind Mode;
    public WheelRating Rating;
    public float ShipMass;            // the share one wheel carries (kg)
    public float Slider = 1f, DampSlider = 1f;   // the terminal's stiffness / damping (vanilla 0..1)
    public float Sink = 3f;           // touchdown speed (m/s)
    public float StartHeight = 0.8f;  // the tyre's height at the start (m)
    public float Seconds = 4f;

    // results
    public float TouchSpeed, MaxComp, Rebound, SettleTime = float.NaN, PeakDecelG, MaxRiseN, FinalComp, PeakForceN, MaxSpeed;
    public int BottomSteps, Bounces;
    public bool Bottomed => BottomSteps > 0;
    public bool Finite = true;
    public bool Trace;

    public Sim Run()
    {
        float m = ShipMass, mw = Rating.WheelMass, s = Rating.Stroke, L = 1f, r = 0.5f;
        float mred = m * mw / (m + mw);
        float ys = StartHeight + r + L, yw = StartHeight + r;   // (x = yw - ys + L = 0: hanging)
        float vs = -Sink, vw = -Sink;
        bool contact = false, touched = false;
        var shock = new WheelAirshock();
        float last = 0f, lastF = 0f;
        int off = 0, calm = 0, steps = (int)(Seconds / Dt);
        double t = 0;
        for (int i = 0; i < steps; i++, t += Dt)
        {
            if (!touched) { vs = -Sink; vw = -Sink; }
            float vTick = vs;
            float x = yw - ys + L, v = vw - vs;
            // 1. the mod
            float h = yw - r;
            bool armed = false;
            if (Mode != Kind.Vanilla) { shock.Step(true, contact, -vs, h, t); armed = shock.Armed; }
            float strength = Slider, damping = DampSlider;
            if (Mode == Kind.Airshock && armed) { strength = WheelShockSettings.MaxStrength; damping = WheelAirshock.ShockDamping; }
            if (Mode == Kind.Oleo) { strength = Slider * WheelShockSettings.ConstraintSpringShare; if (armed) damping = WheelAirshock.ShockDamping; }
            strength = WheelShockSettings.CapStrength(strength);
            if (Mode == Kind.Oleo)
            {
                float mass = contact ? WheelOleo.GroundMass(m, m, 1) : WheelOleo.AirMass(m, mw);
                float vStar = v + (contact ? G * Dt : 0f);
                float f = WheelOleo.Step(Rating, x, v, vStar, mass, Dt, ref last);
                MaxRiseN = MathF.Max(MaxRiseN, f - lastF); lastF = f; PeakForceN = MathF.Max(PeakForceN, f);
                vs += f * Dt / m; vw -= f * Dt / mw;
            }
            // 2. the physics
            vs -= G * Dt; vw -= G * Dt;
            {
                float k = strength * WheelShockSettings.SolverTau * mred / (Dt * Dt), c = damping * mred / Dt;
                float p = (k * x + c * v) * Dt;   // (extending: the ship up, the wheel down)
                if (x <= 0f && v <= 0f) p = 0f;   // (on its lower limit the soft spring pushes nothing: the limit row holds it)
                vs += p / m; vw -= p / mw;
            }
            float accC = 0f, accU = 0f, accL = 0f;
            for (int it = 0; it < 8; it++)
            {
                // the ground (push only)
                float vmin = -(yw - r) / Dt;
                if (vw < vmin || accC > 0f)
                {
                    float d = mw * (vmin - vw), nacc = MathF.Max(0f, accC + d);
                    vw += (nacc - accC) / mw; accC = nacc;
                }
                // the upper limit: x <= s
                float rel = vw - vs, vmaxRel = (s - x) / Dt;
                if (rel > vmaxRel || accU > 0f)
                {
                    float d = mred * (rel - vmaxRel), nacc = MathF.Max(0f, accU + d);
                    float di = nacc - accU; vs += di / m; vw -= di / mw; accU = nacc;
                }
                // the lower limit: x >= 0
                rel = vw - vs; float vminRel = -x / Dt;
                if (rel < vminRel || accL > 0f)
                {
                    float d = mred * (vminRel - rel), nacc = MathF.Max(0f, accL + d);
                    float di = nacc - accL; vw += di / mw; vs -= di / m; accL = nacc;
                }
            }
            // 3. positions
            ys += vs * Dt; yw += vw * Dt;
            contact = accC > 1e-3f;
            if (!touched && contact) { touched = true; TouchSpeed = Sink; t0 = t; }
            float xn = yw - ys + L;
            if (!float.IsFinite(xn + vs + vw)) { Finite = false; break; }
            MaxSpeed = MathF.Max(MaxSpeed, MathF.Abs(vs));
            if (touched)
            {
                MaxComp = MathF.Max(MaxComp, xn);
                if (accU > 1e-3f) BottomSteps++;
                Rebound = MathF.Max(Rebound, vs);
                PeakDecelG = MathF.Max(PeakDecelG, (vs - vTick) / Dt / 9.81f + 2f);   // (the push over gravity, in g)
                if (!contact) { off++; if (off == 3) Bounces++; } else off = 0;
                bool still = MathF.Abs(vs) < 0.05f && MathF.Abs(vw - vs) < 0.05f && contact;
                calm = still ? calm + 1 : 0;
                if (calm == 30 && float.IsNaN(SettleTime)) SettleTime = (float)(t - t0) - 0.5f;
            }
            FinalComp = xn;
            if (Trace && touched && t - t0 < 1.2) Console.WriteLine($"   t {t - t0:F3} x {xn / s:F3} vs {vs:F2} vw {vw:F2} F {lastF / Rating.RatedWeight:F2}W c {contact} U {accU / Dt / Rating.RatedWeight:F2}W L {accL:F0} armed {armed}");
        }
        return this;
    }
    double t0;

    public string Line() => $"{Mode,-8} touch {TouchSpeed:F1} m/s  comp {MaxComp / Rating.Stroke * 100f,5:F0} %  {(Bottomed ? $"BOTTOMED {BottomSteps,2} steps" : "no bottom      ")}  rebound {Rebound,5:F2} m/s  bounces {Bounces}  settle {(float.IsNaN(SettleTime) ? "  never" : $"{SettleTime,5:F2} s")}  peak {PeakDecelG,5:F1} g  rest {FinalComp / Rating.Stroke * 100f,4:F0} %";
}
