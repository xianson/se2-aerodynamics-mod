#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// THE MORTAR (2026-10-23; game-free - Tests/MortarTests). The user: "something parallel to the srb ... like a mortar? For
/// instantaneous impulse ... just 1 charge, segmented like solids ... all at once ... like a bullet casing ... the casing should kick
/// the original grid back, and the split grid forward. If a split doesn't happen, then no impulse ... empty blocks if they are spent".
///
/// The blocks (gen_propulsor_defs.py gen_mortar; one size each: 0.5 / 1.5 / 2.5 m): CHARGES (cubes) stacked on the BACK (Up) face of a
/// BREECH (a half cube) on a ROUND (a half cube) on its FRONT (Down) face; the payload is built on the round's other face.
///   FIRING (AeroMortarComponent, server): the breech's switch flipped off -> on (only a flip: a breech placed or loaded switched on does
///          nothing) with charges behind it and a round in front: the breech's SEAL - a fracture piece of its own at its front face
///          (mortar_rig.py) - is killed through the game's own fracturing (AeroGridComponent.KillPieceAt, the structure's split path),
///          which cuts the mount points on that face; the game re-checks the grid and SPLITS it there if nothing else joins the two
///          sides. The charges are spent either way (empty blocks: AeroMortarChargeComponent.Spent).
///   THE KICK: only if the split came (the ship grid's split signal, within SplitWaitTicks, a new grid holding a mortar round where
///          the round was): the round's grid is pushed forward and the ship back, each by the stack's impulse (Impulse), at the
///          breech's front face - one momentum, nothing created: no split, no impulse.
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

    /// <summary>The kick: the round's grid's impulse (N s, along the firing direction - the breech's Down) and the ship's (the opposite).</summary>
    public static void Kick(float impulse, Vector3 fireDir, out Vector3 onRound, out Vector3 onShip)
    {
        var d = fireDir.LengthSquared() > 1e-8f ? Vector3.Normalize(fireDir) : Vector3.Zero;
        onRound = d * impulse; onShip = -onRound;
    }
}
