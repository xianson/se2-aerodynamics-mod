#pragma warning disable
using AeroMod;

/// <summary>
/// A TOUCHDOWN, time-stepped as THE GAME steps it (1-D, vertical; matched to the in-game per-tick trace of the midair drops,
/// 2026-10-21 - WheelShockDev.DevTrace, WSBRICK at 1 / 3 m/s, the constraint's row on and off, the extra stroke): a SHIP SHARE (the
/// mass one wheel carries) and its WHEEL (the wheel body's mass), the suspension between them with its hard limits, a rigid GROUND
/// under the tyre (push only). Each 60 Hz tick:
///  1. the mod (before the physics step, from the state the last step left): the airshock (the wheel's height and sink); the wheel
///     constraint's factors (vanilla: the sliders; airshock only, armed: the stiffness cap and full damping; oleo: the sliders x
///     Wheels.ConstraintSpring - 0); its lower limit (oleo: -Extra); the oleo's force (WheelOleo.Step - the mass it moves: StrutMass
///     on the ground - the ship's at the mount, its rotation included; the reduced mass in the air) as a pair of impulses at once;
///  2. the physics: SUBSTEPS solver substeps (4 - the game's), each: gravity, then ONE pass of the solver - the constraint's soft
///     row first (Box2D-style soft row, k = factor x tau x m / h^2, c = factor x m / h, m the row's own effective mass - the
///     wheel's against the ship), then the ground contact, then the hard limits - then the positions. (One pass a substep, the row
///     before the contact: that is what reproduced the in-game fight - the row's damping cancelling ~45 % of the strut's push at
///     damping 1; with the row off the deck's contact under the wheel equalled the strut's force, as in game.)
/// Until the first contact the sink is HELD (the in-game drop's approach): the ship and the wheel set to the sink after the mod's
/// impulse, every tick, as the harness's Hold does.
/// Motion is judged by POSITIONS (the velocities after a substepped step carry 3/8 g dt that is not motion): the deepest
/// compression, bottoming (the upper limit's impulse), the rebound (the ship's highest upward speed from its positions), bounces
/// (the tyre off the ground for 3+ ticks), the settle time (the ship and the strut still for 0.5 s), the peak deceleration beyond
/// the weight (from the velocities' change over a tick - exact for the impulses and the gravity alike), in g (9.81).
/// </summary>
sealed class Sim
{
    public enum Kind { Vanilla, Airshock, Oleo }
    public const float Dt = 1f / 60f, SolverTau = 0.6f;
    public float G = 2f * 9.81f;
    public int Substeps = 4;

    public Kind Mode;
    public WheelRating Rating;
    public float ShipMass;            // the share one wheel carries (kg)
    public float StrutMass = float.NaN;   // the ship's mass at the mount (kg, its rotation included; NaN: ShipMass) - a corner wheel's is less
    public int Wheels = 4;            // the wheels on the ground with it (WheelOleo.GroundMass: on 3+ the share moves)
    public float VStarGravity = WheelOleo.SubstepGravity;   // (the replay of the game's earlier build: 1)
    public float Slider = 1f, DampSlider = 1f;   // the terminal's stiffness / damping (vanilla 0..1)
    public float Sink = 3f;           // touchdown speed (m/s)
    public float StartHeight = 0.05f; // the tyre's height at the start (m)
    public float Seconds = 4f;

    // results
    public float TouchSpeed, MaxComp, Rebound, SettleTime = float.NaN, PeakG, MaxRiseN, FinalComp, PeakForceN, MaxSpeed;
    public int BottomSteps, Bounces;
    public bool Bottomed => BottomSteps > 0;
    public bool Finite = true;
    public bool Trace;

    public Sim Run()
    {
        float m = ShipMass, mw = Rating.WheelMass, s = Rating.Stroke, L = 1f, h = Dt / Substeps;
        float mAt = float.IsFinite(StrutMass) && StrutMass > 0f ? StrutMass : m;
        float mred = m * mw / (m + mw);
        float lo = Mode == Kind.Oleo ? -Rating.Extra : 0f;
        // positions: the ship ys, the tyre's bottom yw (the ground at 0); x = L - (ys - yw): 0 at the suspension's own lower limit
        float yw = StartHeight, ys = yw + L - lo;
        float vs = -Sink, vw = -Sink;
        bool contact = false, touched = false;
        var shock = new WheelAirshock();
        float last = 0f, lastF = 0f;
        int off = 0, calm = 0, ticks = (int)(Seconds / Dt);
        double t = 0, t0 = 0;
        for (int i = 0; i < ticks; i++, t += Dt)
        {
            float x = L - (ys - yw), v = vw - vs;   // (the compression and its rate as read: + compressing)
            float ys0 = ys, x0 = x, vTick = vs;
            // 1. the mod
            bool armed = false;
            if (Mode != Kind.Vanilla) { shock.Step(true, contact, -vw, yw, t); armed = shock.Armed; }
            float strength = Slider, damping = DampSlider;
            if (Mode == Kind.Airshock && armed) { strength = GearStiffness.MaxStrength; damping = WheelAirshock.ShockDamping; }
            if (Mode == Kind.Oleo) { float share = WheelShockSettings.ConstraintSpringShare; strength = Slider * share; damping = DampSlider * share; }
            strength = GearStiffness.Cap(strength);
            float f = 0f;
            if (Mode == Kind.Oleo)
            {
                float mass = contact ? WheelOleo.GroundMass(mAt, m * Wheels, Wheels) : WheelOleo.AirMass(mAt, mw);
                float vStar = v + (contact ? VStarGravity * G * Dt : 0f);
                f = WheelOleo.Step(Rating, x, v, vStar, mass, Dt, ref last);
                MaxRiseN = MathF.Max(MaxRiseN, f - lastF); lastF = f; PeakForceN = MathF.Max(PeakForceN, f);
                vs += f * Dt / m; vw -= f * Dt / mw;
            }
            if (!touched) { vs = -Sink; vw = -Sink; vTick = vs; }   // (the approach: held)
            // 2. the physics: substeps, one pass each - the soft row, the ground, the limits
            float k = strength * SolverTau * mred / (h * h), c = damping * mred / h;
            float gam = 0f, beta = 0f, msoft = 0f;
            if (k + c > 0f) { gam = 1f / (h * (c + h * k)); beta = h * k / (c + h * k); msoft = 1f / (1f / mred + gam); }
            float jc = 0f, ju = 0f;
            for (int sub = 0; sub < Substeps; sub++)
            {
                vs -= G * h; vw -= G * h;
                float xk = L - (ys - yw);
                if (k + c > 0f && !(xk <= lo && vw - vs <= 0f))
                {
                    // the soft row: toward the lower limit (it extends); lam < 0 pushes the wheel down and the ship up
                    float lam = -msoft * ((vw - vs) + beta * (xk - lo) / h);
                    vw += lam / mw; vs -= lam / m;
                }
                // the ground (push only)
                float vmin = -yw / h;
                if (vw < vmin) { float d = mw * (vmin - vw); vw += d / mw; jc += d; }
                // the upper limit: x <= s
                float rel = vw - vs, vmax = (s - xk) / h;
                if (rel > vmax) { float d = mred * (rel - vmax); vs += d / m; vw -= d / mw; ju += d; }
                // the lower limit: x >= lo
                rel = vw - vs; float vlo = (lo - xk) / h;
                if (rel < vlo) { float d = mred * (vlo - rel); vw += d / mw; vs -= d / m; }
                ys += vs * h; yw += vw * h;
            }
            contact = jc > 1e-3f;
            if (!touched && contact) { touched = true; TouchSpeed = Sink; t0 = t; }
            float xn = L - (ys - yw);
            if (!float.IsFinite(xn + vs + vw)) { Finite = false; break; }
            float vShip = (ys - ys0) / Dt;   // (the motion: positions)
            MaxSpeed = MathF.Max(MaxSpeed, MathF.Abs(vShip));
            if (touched)
            {
                MaxComp = MathF.Max(MaxComp, xn);
                if (ju > 1e-3f || xn >= 0.98f * s) BottomSteps++;
                if (t > t0) Rebound = MathF.Max(Rebound, vShip);
                PeakG = MathF.Max(PeakG, (vs - vTick) / Dt / 9.81f);   // (the push beyond the weight, in g)
                if (!contact) { off++; if (off == 3) Bounces++; } else off = 0;
                bool still = MathF.Abs(vShip) < 0.05f && MathF.Abs(xn - x0) / Dt < 0.05f && contact;
                calm = still ? calm + 1 : 0;
                if (calm == 30 && float.IsNaN(SettleTime)) SettleTime = (float)(t - t0) - 0.5f;
            }
            FinalComp = xn;
            if (Trace && touched && t - t0 < 1.2) Console.WriteLine($"   t {t - t0:F3} x {xn / s:F3} vship {vShip:F2} F {f / Rating.RatedWeight:F2}W c {contact} ground {jc / Dt / Rating.RatedWeight:F2}W U {ju / Dt / Rating.RatedWeight:F2}W armed {armed}");
        }
        return this;
    }

    public string Line() => $"{Mode,-8} touch {TouchSpeed:F1} m/s  comp {MaxComp / Rating.Stroke * 100f,5:F0} %  {(Bottomed ? $"BOTTOMED {BottomSteps,2} steps" : "no bottom      ")}  rebound {Rebound,5:F2} m/s  bounces {Bounces}  settle {(float.IsNaN(SettleTime) ? "  never" : $"{SettleTime,5:F2} s")}  peak {PeakG,5:F1} g  rest {FinalComp / Rating.Stroke * 100f,4:F0} %";
}
