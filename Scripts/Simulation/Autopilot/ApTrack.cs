#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// AN OBSERVED-MOTION TRACK of any entity (engine-free, allocation-free): its position, velocity, acceleration, turn rate and
/// specific energy estimated ONLY from where it was seen over time - never from a velocity the game or a sensor hands over (a
/// GPS point moved by hand, a grid seen by another mod's radar, a formation leader: all the same). A fading-memory polynomial
/// filter of the second degree (Morrison's alpha-beta-gamma: critically damped, a constant acceleration tracked without lag),
/// its memory MemorySeconds whatever the sample rate (the discount per sample exp(-dt / memory)); the first samples with a
/// shorter memory, so a new track settles in a few ticks. A gap: the state coasted along its own estimate, the age counted;
/// a jump past JumpM (a new entity behind the same id, a teleport) starts it again.
/// Used by the formation behaviour (ApFormation), the combat slots (ApHookCtx.Track) and anything that wants a target's motion.
/// </summary>
public sealed class ApMotionTrack
{
    /// <summary>The filter's memory (s): longer - quieter, slower to follow a change of manoeuvre (the lag of the acceleration is
    /// about 2.5 x memory x the jerk). 0.1: the game's positions are exact (a 15 m/s2 weave followed within 4 m/s2); a noisy sensor
    /// wants 0.3..1 (0.5 m of noise: 1.6 m/s at 0.35 s).</summary>
    public float MemorySeconds = 0.1f;
    /// <summary>A sample this far (m) from where the track expected it starts the track again.</summary>
    public float JumpM = 500f;
    /// <summary>A track not seen for this long (s) is dropped (Valid false).</summary>
    public float LoseSeconds = 3f;

    /// <summary>Seen at least once and not lost.</summary>
    public bool Valid { get; private set; }
    /// <summary>The estimate NOW (world): extrapolated over the time since the last sample.</summary>
    public Vector3D Pos; public Vector3 Vel, Acc;
    /// <summary>The path's turn rate (world rad/s, about the axis v x a): the acceleration across the velocity over the speed.</summary>
    public Vector3 TurnRate;
    /// <summary>The samples taken, the time it has been watched (s), the time since the last sample (s).</summary>
    public int Samples; public float Watched, Age;
    /// <summary>The gravity it was last updated with (world m/s2; zero: none - no energy).</summary>
    public Vector3 Gravity;

    Vector3D _p; Vector3 _v, _a;   // (the state at the last sample)

    public void Reset() { Valid = false; Samples = 0; Watched = Age = 0f; _v = _a = Vel = Acc = TurnRate = Vector3.Zero; }

    /// <summary>The speed (m/s) and the turn rate's size (rad/s).</summary>
    public float Speed => Vel.Length();
    public float TurnRateMag => TurnRate.Length();

    /// <summary>How far the estimate can be trusted (0..1): watched for three memories (at least 0.5 s), and fresh (falls to 0 at LoseSeconds).</summary>
    public float Confidence => !Valid ? 0f : Math.Clamp(Watched / MathF.Max(3f * MemorySeconds, 0.5f), 0f, 1f) * Math.Clamp(1f - Age / MathF.Max(LoseSeconds, 1e-3f), 0f, 1f);

    /// <summary>One tick: seen (z, world) or not (have false - the estimate coasts, the age grows). dt: the tick (s); gravity: the
    /// gravity there (world m/s2, for the energy).</summary>
    public void Step(bool have, Vector3D z, float dt, Vector3 gravity)
    {
        if (!(dt > 0f)) dt = 1f / 60f;
        Gravity = gravity;
        if (!have)
        {
            if (!Valid) return;
            Age += dt; Watched += dt;
            if (Age > LoseSeconds) { Reset(); return; }
            Extrapolate();
            return;
        }
        float gap = Age + dt;   // (the time since the last sample)
        if (Valid)
        {
            // predict to the sample's time
            Vector3D pp = _p + (Vector3D)(_v * gap + _a * (0.5f * gap * gap));
            Vector3 vp = _v + _a * gap;
            Vector3 e = (Vector3)(z - pp);
            if (e.Length() > JumpM) { Reset(); Start(z); return; }
            Samples++;
            // the gains: the fading memory's (discount th per sample), or - while the track is young - the EXPANDING memory's
            // (Morrison: the least-squares fit of every sample so far, self-starting), whichever weighs the new sample more
            float th = MathF.Exp(-gap / MathF.Max(MemorySeconds, 1e-3f)), om = 1f - th;
            float alpha = 1f - th * th * th, beta = 1.5f * om * om * (1f + th), gamma = 0.5f * om * om * om;
            float n = Samples, d = (n + 1f) * (n + 2f) * (n + 3f);
            float aE = 3f * (3f * n * n + 3f * n + 2f) / d;
            if (aE > alpha) { alpha = aE; beta = 18f * (2f * n + 1f) / d; gamma = 30f / d; }
            _p = pp + (Vector3D)(e * alpha);
            _v = vp + e * (beta / gap);
            _a += e * (2f * gamma / (gap * gap));
            Watched += dt; Age = 0f;
        }
        else Start(z);
        Extrapolate();
    }

    void Start(Vector3D z) { _p = z; _v = _a = Vector3.Zero; Samples = 0; Watched = 0f; Age = 0f; Valid = true; }

    void Extrapolate()
    {
        float t = Age;
        Pos = _p + (Vector3D)(_v * t + _a * (0.5f * t * t)); Vel = _v + _a * t; Acc = _a;
        float v2 = Vel.LengthSquared();
        TurnRate = v2 > 1f ? Vector3.Cross(Vel, Acc) / v2 : Vector3.Zero;
    }

    /// <summary>The ENERGY HEIGHT (m: the height over refPos along the gravity's up plus v^2 / 2g) - compare two of them (own minus
    /// the target's, from the same refPos) for who has the energy. No gravity: the kinetic part over 9.81.</summary>
    public float EnergyHeight(Vector3D refPos)
    {
        float g = Gravity.Length();
        Vector3 up = g > 1e-3f ? -Gravity / g : Vector3.Zero;
        float gk = g > 1e-3f ? g : 9.81f;
        return (float)Vector3D.Dot(Pos - refPos, (Vector3D)up) + Vel.LengthSquared() / (2f * gk);
    }

    /// <summary>SPECIFIC EXCESS POWER as observed (m/s: the energy height's rate) - the specific force's push along the path,
    /// v . (a - gravity) / g. Positive: it is gaining energy (climbing or speeding up on its thrust).</summary>
    public float SpecificPower
    {
        get { float g = Gravity.Length(); return g > 1e-3f ? Vector3.Dot(Vel, Acc - Gravity) / g : 0f; }
    }

    /// <summary>The load it pulls (g: the specific force across the path over the gravity) - 1 in level flight, 0 no gravity.</summary>
    public float LoadFactor
    {
        get
        {
            float g = Gravity.Length(), v = Vel.Length();
            if (g < 1e-3f) return 0f;
            Vector3 f = Acc - Gravity;
            if (v > 1f) f = ApMath.Flat(f, Vel / v);
            return f.Length() / g;
        }
    }

    /// <summary>Where it will be in t seconds (constant acceleration - the turn not bent in; ApAim.Lead for an intercept).</summary>
    public Vector3D Predict(float t) => Pos + (Vector3D)(Vel * t + Acc * (0.5f * t * t));
}
