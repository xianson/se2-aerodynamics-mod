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
    public static float CompressionDamping = 0.8f, ReboundDamping = 2f;
    /// <summary>Wheels.ForceRateCap (world, advanced): how fast the strut's force may RISE, in rated loads a second (0: no cap). The
    /// impact spread over steps - SE1's wheel contact response modifier, rebuilt on the strut. A falling force is never held.</summary>
    public static float ForceRateCap = 180f;
    /// <summary>Wheels.ConstraintSpring (world, advanced): while the oleo carries the wheel, the wheel constraint's own soft spring is
    /// the player's x this - low, so the two do not fight (the constraint keeps the hard limits and the wheel's return in the air).</summary>
    public static float ConstraintSpringShare = 0.25f;

    /// <summary>The solver's tau (SolverSettings.Tau; the wheel constraint's strength factor scales it).</summary>
    public const float SolverTau = 0.6f;
    /// <summary>The highest effective tau (factor x SolverTau) the mod writes: past ~1.5-2 the suspension pumps energy in.</summary>
    public const float MaxEffectiveTau = 1.67f;
    /// <summary>The highest strength factor written (2.78).</summary>
    public const float MaxStrength = MaxEffectiveTau / SolverTau;
    /// <summary>A strength factor capped (never negative, never past MaxStrength).</summary>
    public static float CapStrength(float strength) => strength > 0f ? Math.Min(strength, MaxStrength) : 0f;
    /// <summary>The wheel constraint's own spring rate (N/m) at a strength factor: factor x tau x the wheel's mass / dt^2.</summary>
    public static float ConstraintSpringRate(float strength, float wheelMass, float dt) => strength * SolverTau * wheelMass / (dt * dt);
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

    public readonly float Diameter, Stroke, WheelMass, RatedMass;
    public WheelRating(float diameter, float stroke, float wheelMass, float ratedMass)
    { Diameter = diameter; Stroke = stroke; WheelMass = wheelMass; RatedMass = ratedMass; }
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
    /// body's mass (kg), times the world's Wheels.OleoRating (clamped 0.5..3).</summary>
    public static WheelRating Of(float radius, float stroke, float wheelMass, float ratingScale)
    {
        float d = 2f * radius;
        float s = Math.Clamp(ratingScale, 0.5f, 3f);
        return new WheelRating(d, stroke, wheelMass, RatedFor(d) * s);
    }
    static float Sq(float x) => x * x;
    public override string ToString() => $"{Diameter:F2} m wheel, stroke {Stroke:F2} m, {WheelMass:F0} kg, rated {RatedMass / 1000f:F1} t";
}

/// <summary>
/// THE OLEO STRUT - an absolute spring-damper along the suspension axis, between the ship (at the suspension's mount) and the wheel,
/// rated for its wheel (WheelRating). Game-free; the server applies its force as a pair of impulses (AeroWheelShockComponent):
///  - the GAS SPRING: F(u) = K0 ((1+e) / (1+e-u))^n - K0, u = compression / stroke - nothing at full extension (no preload: the wheel
///    hangs on its limit in the air), stiffening toward the end of the stroke (n 1.3, polytropic; e 0.15, the gas column left at full
///    compression), K0 so that the rated weight at the design gravity sits at StaticFraction (0.6) of the stroke; at full compression
///    it pushes ~8 rated weights;
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
    public const float GasN = 1.3f, GasEnd = 0.15f, StaticFraction = 0.6f;

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
    /// <summary>The gas spring's force (N, extending) at a compression (m).</summary>
    public static float SpringForce(in WheelRating r, float x) => x <= 0f ? 0f : Scale(r) * Shape(x / r.Stroke);
    /// <summary>Its stiffness (N/m) there.</summary>
    public static float Stiffness(in WheelRating r, float x) => Scale(r) * ShapeSlope(Math.Max(0f, x) / r.Stroke) / r.Stroke;
    /// <summary>The damper's coefficient (N s/m) there, compressing (v &gt; 0) or extending: its share of critical for the rated load -
    /// or for the mass it moves when that is less (a metered damper: a light ship on a big wheel is not stopped in one step; the
    /// rating itself never follows the ship).</summary>
    public static float Damping(in WheelRating r, float x, float v, float movedMass = float.MaxValue)
        => 2f * (v >= 0f ? WheelShockSettings.CompressionDamping : WheelShockSettings.ReboundDamping) * MathF.Sqrt(Stiffness(r, x) * Math.Min(r.RatedMass, movedMass));
    /// <summary>The compression (m) where the gas spring carries a load (kg) at a gravity (m/s^2) - the static sag.</summary>
    public static float SagFor(in WheelRating r, float loadKg, float g)
    {
        float k0 = Scale(r), w = loadKg * g;
        if (!(w > 0f)) return 0f;
        // G(u) = w / K0  ->  u = (1+e) (1 - (1 + w/K0)^(-1/n))
        float u = (1f + GasEnd) * (1f - MathF.Pow(1f + w / k0, -1f / GasN));
        return u * r.Stroke;
    }

    /// <summary>
    /// One step's force (N, never negative: the strut pushes the ship along the axis away from the wheel: the ship pushed along the axis away from the wheel). x the compression (m), v its rate now (m/s,
    /// + compressing), vStar the rate the step will have before the strut (v plus the step's gravity on a wheel on the ground), m the
    /// mass the strut moves (kg; the ship's at the mount on the ground, the wheel's against the ship in the air), last the force of the
    /// previous step (the rate cap; updated). Never NaN: a bad input gives 0.
    /// </summary>
    public static float Step(in WheelRating r, float x, float v, float vStar, float m, float dt, ref float last)
    {
        if (!r.Valid || !(dt > 0f) || !(m > 0f) || !float.IsFinite(x + v + vStar + m)) { last = 0f; return 0f; }
        if (x <= 0f && vStar <= 0f) { last = 0f; return 0f; }   // (on its limit, extending: the wheel hangs - nothing)
        float xs = Math.Max(0f, x);
        float fs = SpringForce(r, xs), k = Stiffness(r, xs), c = Damping(r, xs, vStar, m);
        float a = c + k * dt;
        float f = (fs + a * vStar) / (1f + dt * a / m);
        // (push only: the rebound damper eats the push down to nothing, never pulls - a strut in tension would yank the light wheel
        //  off the ground, and it chatters between the ground and its limit)
        if (!(f > 0f)) f = 0f;
        // the rate cap: a rise at most ForceRateCap rated weights a second; a fall at once
        float cap = WheelShockSettings.ForceRateCap;
        if (cap > 0f) f = Math.Min(f, Math.Max(last, 0f) + cap * r.RatedWeight * dt);
        last = f;
        return f;
    }

    /// <summary>The ship's effective mass at the strut (kg) against the ground: the ship's at the mount (its rotation included), shared
    /// with the other wheels on the ground (a ship settling on n wheels moves M/n on each), never more than either.</summary>
    public static float GroundMass(float shipAtMount, float shipMass, int wheelsOnGround)
        => Math.Max(1f, Math.Min(shipAtMount, shipMass / Math.Max(1, wheelsOnGround)));
    /// <summary>In the air: the wheel against the ship (their reduced mass).</summary>
    public static float AirMass(float shipAtMount, float wheelMass) => shipAtMount > 0f && wheelMass > 0f ? shipAtMount * wheelMass / (shipAtMount + wheelMass) : Math.Max(1f, wheelMass);
}
