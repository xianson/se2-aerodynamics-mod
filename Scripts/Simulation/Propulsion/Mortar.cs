#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// THE MORTAR (2026-10-23; game-free - Tests/MortarTests). The user: "something parallel to the srb ... like a mortar? For
/// instantaneous impulse ... just 1 charge, segmented like solids ... all at once ... like a bullet casing ... the casing should kick
/// the original grid back, and the split grid forward. If a split doesn't happen, then no impulse ... empty blocks if they are spent".
///
/// The blocks (gen_propulsor_defs.py gen_mortar; one size each: 0.5 / 1.5 / 2.5 m): CHARGES (cubes) stacked on the BACK (Up) face of a
/// BREECH (a half cube; the 0.5 m one a whole cube, each piece a cell) on a ROUND (a half cube) on its FRONT (Down) face; the payload is built on the round's other face.
///   FIRING (AeroMortarComponent, server): the breech's switch flipped off -> on (only a flip: a breech placed or loaded switched on does
///          nothing) with charges behind it and a round in front: the breech's SEAL - a fracture piece of its own at its front face
///          (mortar_rig.py) - is killed through the game's own fracturing (AeroGridComponent.KillPieceAt, the structure's split path),
///          which cuts the mount points on that face; the game re-checks the grid and SPLITS it there if nothing else joins the two
///          sides. The charges are spent either way (empty blocks: AeroMortarChargeComponent.Spent).
///   THE KICK: only if the split came (the ship grid's split signal, within SplitWaitTicks, a new grid holding a mortar round where
///          the round was): the round's grid is pushed forward and the ship back, each by the stack's impulse (Impulse), at the
///          breech's front face - one momentum, nothing created: no split, no impulse.
///   THE HOLD: the game caps a grid's speed (1,000 m/s), so a light round may take less than its kick (in game 2026-10-23: a 51 kg
///          round at 1,000 m/s took 51 of 96 kN s). The rest is STORED (Bank) - the ship has already taken all of it - and tops the
///          round back up to the speed it reached (TopUp) as drag and gravity slow it, along its travel, until the store is used,
///          the round hits something (Hit) or HoldMaxMs pass (the user, 2026-10-24: "store the kinetic energy and maintain 1km/s for
///          longer" - momentum, not energy: the impulse is the kick both grids share).
/// </summary>
public static class Mortar
{
    /// <summary>Steps the firing breech waits for the game's split (s x 60).</summary>
    public const int SplitWaitTicks = 60;
    /// <summary>Steps the kick may wait for both grids' bodies (a new grid's body comes a step or two after it).</summary>
    public const int KickWaitTicks = 30;

    /// <summary>The stack's impulse (N s): its charges' propellant (kg) x the impulse per kg.</summary>
    public static float Impulse(float propellantKg) => float.IsFinite(propellantKg) && propellantKg > 0f ? propellantKg * MortarBlocks.ImpulsePerKg : 0f;

    /// <summary>A breech's front face along its axis, in grid cells x Cell (m, grid-local): the Down face of a block whose cell box
    /// is min..max on that axis (inclusive) with its Up along +axis (sign 1) or -axis (-1).</summary>
    public static float FrontFace(int min, int max, int sign, float cell) => sign > 0 ? min * cell - cell * 0.5f : max * cell + cell * 0.5f;

    /// <summary>The seal's middle, along the axis (m, grid-local): half its thickness in from the front face, toward the back.</summary>
    public static float SealMiddle(float front, int sign, float seal) => front + sign * seal * 0.5f;

    /// <summary>Where the round's middle is, along the axis (m, grid-local): half its height out from the breech's front face.</summary>
    public static float RoundMiddle(float front, int sign, float roundHeight) => front - sign * roundHeight * 0.5f;

    /// <summary>A shortfall under this share of the impulse is no cap, only the check's read: nothing stored.</summary>
    public const float BankMinShare = 0.02f;
    /// <summary>How long the store may hold the round's speed (ms); what is left then is dropped.</summary>
    public const long HoldMaxMs = 120000;
    /// <summary>A hit: the round lost more than this share of the held speed in one step...</summary>
    public const float HitDropShare = 0.25f;
    /// <summary>... or turned more than this (cos of 10 degrees).</summary>
    public const float HitTurnCos = 0.985f;

    /// <summary>The momentum the round did not take (N s): the impulse less what it took; 0 under BankMinShare of it (or nonsense).</summary>
    public static float Bank(float impulse, float delivered)
    {
        float b = impulse - delivered;
        return float.IsFinite(b) && impulse > 0f && b > impulse * BankMinShare ? MathF.Min(b, impulse) : 0f;
    }

    /// <summary>The top-up (N s): the round back up to the held speed (m/s), at most the store; 0 at or over it.</summary>
    public static float TopUp(float speed, float hold, float kg, float bank)
        => kg > 0f && bank > 0f && float.IsFinite(speed) && speed < hold ? MathF.Min(bank, (hold - speed) * kg) : 0f;

    /// <summary>The round hit something since the last step (velocities, m/s): a sudden loss of speed or a sharp turn - drag and
    /// gravity are gentler.</summary>
    public static bool Hit(Vector3 prev, Vector3 v, float hold)
    {
        float a = prev.Length(), b = v.Length();
        if (a - b > HitDropShare * hold) return true;
        return a > 1f && b > 1f && Vector3.Dot(prev, v) / (a * b) < HitTurnCos;
    }

    /// <summary>The round was moved, not flown, since the last step (world m in dtS s at speed m/s): over 3x the flight and 100 m - a
    /// teleport or a hand-over to another frame (the Orbital Mod splits a grid off its frame at 10 km: in game 2026-10-24 that read
    /// as a hit). Its velocity then belongs to the new frame: no hit, the held speed at most the new one.</summary>
    public static bool Jumped(double moved, float speed, float dtS) => moved > speed * dtS * 3f + 100f;

    /// <summary>The kick: the round's grid's impulse (N s, along the firing direction - the breech's Down) and the ship's (the opposite).</summary>
    public static void Kick(float impulse, Vector3 fireDir, out Vector3 onRound, out Vector3 onShip)
    {
        var d = fireDir.LengthSquared() > 1e-8f ? Vector3.Normalize(fireDir) : Vector3.Zero;
        onRound = d * impulse; onShip = -onRound;
    }
}
