#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// WHEEL SHOCKS - the world's settings and the solver's limits (2026-10-20, the user: "reflect and give ALL wheels airshock / oleo
/// until Keen does it"). A STOPGAP for SE2's weak wheel impacts on every wheel suspension of a grid the mod simulates - the vanilla
/// suspensions (WheelCornerModuleComponent) and the mod's own gears alike (AeroWheelShockComponent; the laws: WheelOleo,
/// WheelAirshock - game-free, Tests/WheelShockTests). Why it is needed: the wheel constraint's soft spring is
/// SuspensionStrengthFactor x the solver's tau (0.6) acting on the LIGHT wheel body (k ~ factor x 0.6 x m_wheel / dt^2: a 1 m
/// wheel, 200 kg, makes ~0.43 MN/m at factor 1 and ~12 kN s/m of damping - a 14 t share at the mod's 2 g sags 0.64 m into a
/// 0.25 m stroke), restitution is 0, the hard limit takes the rest in one step, and SE1's wheel contact response modifier (the
/// impact spread over several steps) has no SE2 counterpart a mod could attach.
/// THE GAME'S STEP (measured 2026-10-21, the midair per-tick trace - WheelShockDev.DevTrace; Tests/WheelShockTests/Sim.cs models
/// it): 4 solver substeps a 60 Hz tick, gravity integrated per substep, one solver pass a substep - so the constraint's soft row is
/// k = factor x tau x m / h^2 at h = dt/4 (16 x the per-tick figure), and it is solved BEFORE the wheel's ground contact. Its
/// damping row sees the strut's impulse on the light wheel (~20 m/s a tick, before the ground takes it back) and cancels a share of
/// it: at the armed damping 1 the ship got 55 % of the strut's force (the deck's own contact impulse under each wheel, against the
/// force applied), at the player's 0.4 about 85 % - the strut over-compressed and bottomed (3 m/s), then held its force high and
/// threw the ship back up. So while the oleo carries, the constraint's own spring and damping are OFF (ConstraintSpringShare 0):
/// it keeps only its hard limits. (The velocities read between ticks also carry the substeps: at rest under the strut the ship
/// reads -3/8 g dt - 0.11 m/s at 2 g - though it does not move: judge motion by positions.)
/// </summary>
public static class WheelShockSettings
{
    public const int Off = 0, AirshockOnly = 1, AirshockOleo = 2;
    /// <summary>Wheels.Shocks (world): 0 off (vanilla wheels; the mod's gears with their own stiffness only), 1 the airshock only,
    /// 2 the airshock and the oleo strut (default).</summary>
    public static int Mode = AirshockOleo;
    /// <summary>Wheels.OleoRating (world, 0.5..3): every wheel's rated load (WheelRating) times this.</summary>
    public static float OleoRating = 1f;
    /// <summary>Wheels.CompressionDamping / ReboundDamping (world, advanced): the strut's damping as a share of critical damping
    /// for its rated load at its stiffness where it is - compressing / extending.</summary>
    public static float CompressionDamping = 1f, ReboundDamping = 2f;
    /// <summary>Wheels.ForceRateCap (world, advanced): how fast the strut's force may RISE, in rated loads a second (0: no cap). The
    /// impact spread over steps - SE1's wheel contact response modifier, rebuilt on the strut. A falling force is never held.</summary>
    public static float ForceRateCap = 180f;
    /// <summary>Wheels.ConstraintSpring (world, advanced): while the oleo carries the wheel, the wheel constraint's own soft spring AND
    /// damping are the player's x this - 0 by default: the constraint's damping row cancelled up to 45 % of the strut's push in game
    /// (see the summary); the constraint keeps its hard limits, the strut alone springs and damps.</summary>
    public static float ConstraintSpringShare = 0f;
    /// <summary>Wheels.OleoExtension (world, advanced, 0..1): the strut's extra stroke below the suspension's own travel, a share of it
    /// (0.4: 0.1 m on a 1 m wheel). The wheel hangs that much lower while a touchdown is coming (the airshock armed: the constraint's
    /// lower limit moved down) and while it rests below its normal travel, so the hit is taken over 1.4 x the stroke.</summary>
    public static float Extension = 0.4f;

    // (the stiffness cap - the solver's tau, the highest effective tau, the spring rate a factor gives - is GearStiffness's, one for
    //  every wheel and gear: Gear/GearStiffness.cs)
}

/// <summary>
/// A WHEEL'S RATING - from the wheel itself, never from the ship it carries: the rated static load (the mass it is built to carry
/// at the design gravity) by the wheel's size, its stroke from the suspension (the constraint's travel), its mass from its body.
/// The table (the vanilla wheels; between rows the load goes with the diameter squared - a tyre's footprint and a strut's piston
/// area - and so past the ends):
///   diameter  stroke    wheel mass   rated load (x Wheels.OleoRating)
///    1 m      0.25 m      200 kg       15 t
///    1.5 m    0.40 m      300 kg       33.75 t
///    2.5 m    0.60 m      500 kg       93.75 t
///    5 m     (block's)   1000 kg      375 t
///    7.5 m   (block's)  14000 kg      843.75 t
///   12.5 m   (block's)  25000 kg     2343.75 t
/// Design gravity: 2 g (the mod's worlds: gravity x 2) - a ship at its wheels' rated load sits at StaticFraction of the stroke in it.
/// (Ballpark: a 1 m airliner tyre carries ~10-15 t; the Tu-134 Big - 56 t on four 1 m wheels - is at ~93 % of its rating.)
/// </summary>
public readonly struct WheelRating
{
    public const float DesignG = 2f * 9.81f;
    /// <summary>The table: diameter (m), rated load (kg) - see the summary.</summary>
    public static readonly float[] Diameters = { 1f, 1.5f, 2.5f, 5f, 7.5f, 12.5f };
    public static readonly float[] RatedKg = { 15000f, 33750f, 93750f, 375000f, 843750f, 2343750f };
    /// <summary>The vanilla strokes and wheel masses (m, kg) for the rows that have them - the tests' wheels; the game reads its own.</summary>
    public static readonly float[] Strokes = { 0.25f, 0.40f, 0.60f };
    public static readonly float[] WheelKg = { 200f, 300f, 500f, 1000f, 14000f, 25000f };

    /// <summary>Stroke: the suspension's own travel (m, its constraint's limits 0..Stroke); Extra: the strut's stroke below it (m,
    /// the constraint's lower limit moved to -Extra while the airshock is armed - Wheels.OleoExtension).</summary>
    public readonly float Diameter, Stroke, WheelMass, RatedMass, Extra;
    public WheelRating(float diameter, float stroke, float wheelMass, float ratedMass, float extra = 0f)
    { Diameter = diameter; Stroke = stroke; WheelMass = wheelMass; RatedMass = ratedMass; Extra = extra > 0f && float.IsFinite(extra) ? extra : 0f; }
    /// <summary>The strut's whole stroke (m): the travel and the extra below it.</summary>
    public float StrutStroke => Stroke + Extra;
    /// <summary>The rated load's weight at the design gravity (N).</summary>
    public float RatedWeight => RatedMass * DesignG;
    public bool Valid => Stroke > 0.01f && RatedMass > 0f && WheelMass > 0f && float.IsFinite(Stroke + RatedMass + WheelMass);

    /// <summary>The rated load (kg) of a wheel this wide (m), before Wheels.OleoRating: the table, the diameter squared between rows
    /// and past the ends.</summary>
    public static float RatedFor(float diameter)
    {
        if (!(diameter > 0f) || !float.IsFinite(diameter)) return 0f;
        int n = Diameters.Length;
        if (diameter <= Diameters[0]) return RatedKg[0] * Sq(diameter / Diameters[0]);
        if (diameter >= Diameters[n - 1]) return RatedKg[n - 1] * Sq(diameter / Diameters[n - 1]);
        for (int i = 1; i < n; i++)
            if (diameter <= Diameters[i])
            {
                // (between two rows: each row's load per diameter squared, blended in the diameter squared)
                float a = Sq(Diameters[i - 1]), b = Sq(Diameters[i]), d2 = Sq(diameter);
                float t = (d2 - a) / (b - a);
                return d2 * (RatedKg[i - 1] / a * (1f - t) + RatedKg[i] / b * t);
            }
        return 0f;
    }

    /// <summary>This wheel's rating: its radius (m, the corner module's WheelRadius), its stroke (m, the constraint's travel), its
    /// body's mass (kg), times the world's Wheels.OleoRating (clamped 0.5..3); the strut's extra stroke the world's
    /// Wheels.OleoExtension (0..1 of the travel).</summary>
    public static WheelRating Of(float radius, float stroke, float wheelMass, float ratingScale)
        => Of(radius, stroke, wheelMass, ratingScale, WheelShockSettings.Extension);
    public static WheelRating Of(float radius, float stroke, float wheelMass, float ratingScale, float extension)
    {
        float d = 2f * radius;
        float s = Math.Clamp(ratingScale, 0.5f, 3f);
        return new WheelRating(d, stroke, wheelMass, RatedFor(d) * s, stroke * Math.Clamp(extension, 0f, 1f));
    }
    static float Sq(float x) => x * x;
    public override string ToString() => $"{Diameter:F2} m wheel, stroke {Stroke:F2} m (+{Extra:F2}), {WheelMass:F0} kg, rated {RatedMass / 1000f:F1} t";
}

/// <summary>
/// THE OLEO STRUT - an absolute spring-damper along the suspension axis, between the ship (at the suspension's mount) and the wheel,
/// rated for its wheel (WheelRating). Game-free; the server applies its force as a pair of impulses (AeroWheelShockComponent):
///  - THE STROKE: the suspension's travel and the extra below it (WheelRating.Extra, Wheels.OleoExtension: the wheel hangs lower
///    while a touchdown is coming) - u = (compression + Extra) / (travel + Extra); every function here takes the SUSPENSION's
///    compression (0 at its own lower limit, -Extra at the strut's full extension);
///  - the GAS SPRING: F(u) = K0 ((1+e) / (1+e-u))^n - K0 - nothing at the strut's full extension, stiffening toward the end of the
///    stroke (n 1.3, polytropic; e 0.15, the gas column left at full compression), K0 so that the rated weight at the design gravity
///    sits at StaticFraction (0.7) of the strut's stroke (58 % of the suspension's travel at the default extension 0.4 - the ride
///    height as before); at full compression it pushes ~6 rated weights; at the suspension's own lower limit (the wheel hanging in
///    the air, unarmed) ~0.2 - a preload the limit holds;
///  - the DAMPER: c = 2 zeta sqrt(k(u) m), k the gas spring's stiffness where it is, m the rated load (or the mass it moves when
///    that is less: metered) - near critical: Wheels.CompressionDamping compressing, the stronger Wheels.ReboundDamping extending;
///  - THE STEP (60 Hz, an explicit impulse into a stiff spring - the wheel body weighs 1-2 % of the load it carries): solved
///    implicitly over the step against the mass it moves (backward Euler on the spring-damper's local line, the soft-constraint
///    form F = (F_s + (c + k dt) v*) / (1 + dt (c + k dt) / m), v* the compression rate the step will have before the strut - its
///    gravity included on the ground), so a light wheel in the air or a stiff end of stroke never overshoots, and a ship at rest
///    sits exactly on F_s = its weight;
///  - PUSH ONLY: the rebound damper takes the push down to nothing but never pulls (tension would lift the light wheel off the
///    ground: in the time-stepped sim it chattered between the ground and its limit at +-7 m/s);
///  - THE RATE CAP: the force may RISE by at most Wheels.ForceRateCap rated weights a second - a hit spread over steps (SE1's
///    contact response modifier, rebuilt on the strut); it falls at once (a held force would throw the ship back up).
/// Past the stroke the wheel constraint's hard limit takes the rest: an overload bottoms, honestly. Equal and opposite: the ship
/// takes +F along the axis at the mount, the wheel -F.
/// </summary>
public static class WheelOleo
{
    public const float GasN = 1.3f, GasEnd = 0.15f;
    /// <summary>The rated weight's place in the strut's stroke (0.7; a static for the offline replay of the 0.6 the game ran before).</summary>
    public static float StaticFraction = 0.7f;
    /// <summary>The share of a tick's gravity the coming step's MOTION takes, on top of the velocity read now (the game's 4 solver
    /// substeps: the positions move with the mean of the substeps' velocities, (N+1)/2N of the tick's g dt; the strut's impulse lands
    /// whole at the start). vStar = v + this x g dt on the ground: a ship at rest - read sinking 3/8 g dt though still - sits exactly
    /// where the gas spring carries its weight, whatever mass the strut moves (with g dt it sat deeper the lighter the corner).</summary>
    public const float SubstepGravity = 0.625f;

    /// <summary>The gas spring's shape G(u) = ((1+e)/(1+e-u))^n - 1 (u clamped 0..1).</summary>
    public static float Shape(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return MathF.Pow((1f + GasEnd) / (1f + GasEnd - u), GasN) - 1f;
    }
    /// <summary>dG/du.</summary>
    public static float ShapeSlope(float u)
    {
        u = Math.Clamp(u, 0f, 1f);
        return GasN * MathF.Pow(1f + GasEnd, GasN) * MathF.Pow(1f + GasEnd - u, -GasN - 1f);
    }
    /// <summary>K0 (N): the rated weight at StaticFraction of the stroke.</summary>
    public static float Scale(in WheelRating r) => r.RatedWeight / Shape(StaticFraction);
    /// <summary>The strut's own compression (m, 0 at its full extension) for the suspension's.</summary>
    public static float StrutX(in WheelRating r, float x) => x + r.Extra;
    /// <summary>The gas spring's force (N, extending) at a compression (m, the suspension's).</summary>
    public static float SpringForce(in WheelRating r, float x) { float xs = StrutX(r, x); return xs <= 0f ? 0f : Scale(r) * Shape(xs / r.StrutStroke); }
    /// <summary>Its stiffness (N/m) there.</summary>
    public static float Stiffness(in WheelRating r, float x) => Scale(r) * ShapeSlope(Math.Max(0f, StrutX(r, x)) / r.StrutStroke) / r.StrutStroke;
    /// <summary>The damper's coefficient (N s/m) there, compressing (v &gt; 0) or extending: its share of critical for the rated load -
    /// or for the mass it moves when that is less (a metered damper: a light ship on a big wheel is not stopped in one step; the
    /// rating itself never follows the ship).</summary>
    public static float Damping(in WheelRating r, float x, float v, float movedMass = float.MaxValue)
        => 2f * (v >= 0f ? WheelShockSettings.CompressionDamping : WheelShockSettings.ReboundDamping) * MathF.Sqrt(Stiffness(r, x) * Math.Min(r.RatedMass, movedMass));
    /// <summary>The compression (m, the suspension's) where the gas spring carries a load (kg) at a gravity (m/s^2) - the static sag.</summary>
    public static float SagFor(in WheelRating r, float loadKg, float g)
    {
        float k0 = Scale(r), w = loadKg * g;
        if (!(w > 0f)) return -r.Extra;
        // G(u) = w / K0  ->  u = (1+e) (1 - (1 + w/K0)^(-1/n))
        float u = (1f + GasEnd) * (1f - MathF.Pow(1f + w / k0, -1f / GasN));
        return u * r.StrutStroke - r.Extra;
    }

    /// <summary>
    /// One step's force (N, never negative: the strut pushes the ship along the axis away from the wheel). x the suspension's compression (m), v its rate now (m/s,
    /// + compressing), vStar the rate the step will have before the strut (v plus the step's gravity on a wheel on the ground), m the
    /// mass the strut moves (kg; the ship's at the mount on the ground, the wheel's against the ship in the air), last the force of the
    /// previous step (the rate cap; updated). Never NaN: a bad input gives 0.
    /// </summary>
    public static float Step(in WheelRating r, float x, float v, float vStar, float m, float dt, ref float last)
        => Step(r, x, v, vStar, m, dt, ref last, out _);
    /// <summary>The same; uncapped: the force before the rate cap (diagnostics).</summary>
    public static float Step(in WheelRating r, float x, float v, float vStar, float m, float dt, ref float last, out float uncapped)
    {
        uncapped = 0f;
        if (!r.Valid || !(dt > 0f) || !(m > 0f) || !float.IsFinite(x + v + vStar + m)) { last = 0f; return 0f; }
        if (StrutX(r, x) <= 0f && vStar <= 0f) { last = 0f; return 0f; }   // (fully extended, extending: the wheel hangs - nothing)
        float xs = Math.Max(-r.Extra, x);
        float fs = SpringForce(r, xs), k = Stiffness(r, xs), c = Damping(r, xs, vStar, m);
        float a = c + k * dt;
        float f = (fs + a * vStar) / (1f + dt * a / m);
        // (push only: the rebound damper eats the push down to nothing, never pulls - a strut in tension would yank the light wheel
        //  off the ground, and it chatters between the ground and its limit)
        if (!(f > 0f)) f = 0f;
        uncapped = f;
        // the rate cap: a rise at most ForceRateCap rated weights a second; a fall at once
        float cap = WheelShockSettings.ForceRateCap;
        if (cap > 0f) f = Math.Min(f, Math.Max(last, 0f) + cap * r.RatedWeight * dt);
        last = f;
        return f;
    }

    /// <summary>The ship's effective mass at the strut (kg) against the ground: on three or more wheels the ship's share, M/n (it
    /// settles on them together - its rotation held by the others); on one or two the ship's at the mount (its rotation included),
    /// never more than M/n. (2026-10-21: on four wheels the corner's mass - ~0.64 M/4 for WSBRICK - made the implicit solve sit the
    /// ship deeper than its gas spring's sag: the strut predicted the step's motion with a mass that was not the one that moved.)</summary>
    public static float GroundMass(float shipAtMount, float shipMass, int wheelsOnGround)
    {
        int n = Math.Max(1, wheelsOnGround);
        float share = shipMass / n;
        return Math.Max(1f, n >= 3 ? share : Math.Min(shipAtMount, share));
    }
    /// <summary>In the air: the wheel against the ship (their reduced mass).</summary>
    public static float AirMass(float shipAtMount, float wheelMass) => shipAtMount > 0f && wheelMass > 0f ? shipAtMount * wheelMass / (shipAtMount + wheelMass) : Math.Max(1f, wheelMass);
}
