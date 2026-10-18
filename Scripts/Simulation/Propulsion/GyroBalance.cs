#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Library.Mathematics;

namespace AeroMod;

/// <summary>
/// THE GYRO REBALANCE (2026-10-18, the user: "just do 1, maybe a lil 2. have them both as settings") - game-free
/// (Tests/ThrustTests GyroBalanceChecks); the game part is AeroGridComponent.Gyros.cs. Vanilla SE2 gyros are huge reactionless
/// torque sources (an F-7's 32 small gyros: 7 MN m; one large gyro on a 39 t plane: 44 MN m), so with this mod's offset
/// thrust, air moments and control surfaces they silently held lopsided designs straight and out-torqued the surfaces.
///  1. TORQUE SCALE (Gyros.TorqueScale): every gyro's real maximum - its GyroscopeData.MaxTorque, the number the game sums
///     into the grid's MaxTorqueData (GridMovementCollectorComponent.OnGyroscopeChanged) and every gyro law spends - is
///     its definition's x this. The game's own gyros, weaker; no counter-torque. Everything that reads the gyros' budget
///     reads MaxTorqueData (GyroControl.Reach: the aero-aware law, the allocator, the loss-of-control prediction, the rotor
///     limiter; the design probe; the structure's gyro loads are the torque actually given), so all of it follows.
///     Default 0.25, measured (Tests/AeroBench gyros, 150 m/s sea level, vanilla budget = min(0.01 m^2, 0.25 x MaxTorque)):
///     at 0.25 no measured ship's gyros hold one main engine out (worst: Plane2 0.69 of it, vanilla 2.78) or its own
///     moment up to the lift peak (worst: Red Ship 0.92, vanilla 3.67); they still hold a 5 deg upset at 150 m/s on 5 of 7
///     ships (trim, stabilising) and turn them at 0.7..106 deg/s^2 - so they lead at low speed and the air leads at speed.
///     One scale everywhere (space too): thrusters still steer (ThrustTorque) and a scale that moved with the air would
///     change a ship's handling through every climb.
///  2. SATURATION (Gyros.Saturation, mild): the gyros store the angular momentum they give (per grid, grid-local, signed:
///     a turn and its stop cancel; holding a steady moment fills it). Past FadeFrom of the store (Gyros.SaturationSeconds
///     x the full torque) the torque fades smoothly to Floor; it bleeds back while the demand is low (under LowShare of
///     full: a small trim never fills it). Server only; per grid (AeroGridComponent fields - a new grid starts empty, a
///     grid that loses gyros keeps their share of the store only).
/// </summary>
public static class GyroBalance
{
    /// <summary>Every gyro's maximum torque x this (world setting Gyros.TorqueScale; 1 = vanilla).</summary>
    public static float TorqueScale = 0.25f;
    /// <summary>The gyros store the momentum they give and fade when full (world setting Gyros.Saturation).</summary>
    public static bool Saturation = true;
    /// <summary>The store: this many seconds of the gyros' full torque (world setting Gyros.SaturationSeconds).</summary>
    public static float SaturationSeconds = 15f;

    /// <summary>The store's fill (0..1) the fade starts from, and the share of the torque left when it is full (never 0: at
    /// 0 the game drops the grid's gyro law and the cockpit hides its aim reticle).</summary>
    public const float FadeFrom = 0.5f, Floor = 0.25f;
    /// <summary>The bleed back: this share of the full torque (the store empties in SaturationSeconds / Recover when idle),
    /// full while the torque given is 0, none at LowShare of full and above.</summary>
    public const float Recover = 0.5f, LowShare = 0.25f;
    /// <summary>A gyro's maximum is rewritten when it is off its target by more than this share of its rating (each write
    /// re-sums the grid's MaxTorqueData and replicates it), and exactly when the target is its rating.</summary>
    public const float WriteTolerance = 0.01f;
    /// <summary>The update period (ticks): the store fills over seconds.</summary>
    public const int Period = 6;

    /// <summary>The torque left at a store fill (1 below FadeFrom, smoothly down to Floor at full).</summary>
    public static float Fade(float fill)
    {
        if (!(fill > FadeFrom)) return 1f;
        float t = MathF.Min((fill - FadeFrom) / (1f - FadeFrom), 1f);
        return 1f - (1f - Floor) * t * t * (3f - 2f * t);
    }

    /// <summary>The share of its rating every gyro gives now: the scale (clamped 0.05..1) x the fade; 1 with aero off.</summary>
    public static float Factor(bool on, float fill)
    {
        if (!on) return 1f;
        float s = float.IsFinite(TorqueScale) ? Math.Clamp(TorqueScale, 0.05f, 1f) : 1f;
        return s * (Saturation ? Fade(fill) : 1f);
    }

    /// <summary>One update of a grid's store (N m s, grid-local): tau, the gyros' torque given now (N m, local); full, their
    /// full torque (the scaled budget, N m); dt, the time since the last (s). Returns the fill (0..1). Not finite or no
    /// gyros: the store emptied.</summary>
    public static float Step(ref Vector3 store, Vector3 tau, float full, float seconds, float dt)
    {
        if (!(full > 0f) || !(seconds > 0f) || !(dt > 0f) || !tau.IsValid() || !store.IsValid()) { store = Vector3.Zero; return 0f; }
        float cap = full * seconds;
        float load = tau.Length() / full;
        float bleed = Recover * full * (1f - MathF.Min(load / LowShare, 1f)) * dt;
        store += tau * dt;
        float m = store.Length();
        if (m > 0f)
        {
            float k = MathF.Max(m - bleed, 0f);
            if (k > cap) k = cap;   // (a full store takes no more: what the gyros give past it is their faded torque)
            store *= k / m; m = k;
        }
        return m / cap;
    }

    /// <summary>The grid lost gyros (its rated sum fell from 'was' to 'now'): the store keeps their share only (the momentum
    /// in the wheels that left went with them; a grid cut off starts empty).</summary>
    public static void Lost(ref Vector3 store, float was, float now)
    {
        if (!(was > 0f) || !(now < was)) return;
        store *= MathF.Max(now, 0f) / was;
    }

    /// <summary>One gyro: its maximum now (cur) against its rating x the factor. True: write 'write'.</summary>
    public static bool Target(float rated, float cur, float factor, out float write)
    {
        write = cur;
        if (!(rated > 0f) || !float.IsFinite(factor)) return false;
        float want = factor >= 1f ? rated : rated * factor;
        if (want == rated ? cur == rated : MathF.Abs(cur - want) <= WriteTolerance * rated) return false;
        write = want;
        return true;
    }

    /// <summary>A gyro budget read now (N m: MaxTorqueData, faded) taken back to the unfaded one - the design figures' (a
    /// ship's capacity, not its state this second).</summary>
    public static float Unfaded(float live, float fade) => fade > 1e-3f && fade < 1f ? live / fade : live;
}
