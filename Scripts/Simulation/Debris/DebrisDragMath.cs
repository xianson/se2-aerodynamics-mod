#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// The air on a loose fracture piece (AeroDebrisDrag), engine-free (Tests/DebrisTests runs this file as is): a box (the
/// piece's bounding box) in quadratic drag, F = 0.5 rho Cd A |v| v against the air, and a little drag on its spin.
/// Each step is the EXACT solution of the drag over dt (dv/dt = -k v|v| -> v / (1 + k |v| dt)), not an Euler step: it
/// never takes off more than the speed there is, so it cannot reverse the motion or blow up at any dt, mass or speed
/// (a chip at orbital speed in thick air stops relative to the air at most, it does not bounce back).
/// </summary>
public static class DebrisDragMath
{
    /// <summary>The most the game's own wind push changes a body's velocity in one step (WindGeneratorComponent.ApplyWindForce
    /// caps its impulse at 5 / InvMass).</summary>
    public const float GameWindCapDv = 5f;

    /// <summary>The box's area (m2) seen along a unit direction in its own frame - the game's ComputeProjectedAreaLocal
    /// (WindGeneratorComponent): each face's area times how squarely the flow meets it.</summary>
    public static float ProjectedArea(Vector3 size, Vector3 localDir)
        => MathF.Abs(localDir.X) * size.Y * size.Z + MathF.Abs(localDir.Y) * size.X * size.Z + MathF.Abs(localDir.Z) * size.X * size.Y;

    /// <summary>
    /// The speed (m/s) this mod's drag takes off the piece's motion through the air this step, opposite that motion.
    /// speedRel: |velocity - wind| (m/s); area: ProjectedArea along it; gameWind: the wind speed the GAME pushes the piece
    /// with this step (WindData.Speed when it is > 0 and the air has density; else 0).
    /// HOW IT COMBINES WITH THE GAME'S WIND: the game pushes 0.5 rho A Ws |w - v| along (w - v) (Cd 1, but scaled by the
    /// wind speed Ws, not the relative speed) - in still air nothing, at rest in wind exactly quadratic drag. Together they
    /// are meant to make ONE drag against the moving air: 0.5 rho A max(Cd |v_rel|, Ws) |v_rel|. This returns the total
    /// over the step less what the game's push already did (never below 0: where the wind's term is the larger, the
    /// game's push stands alone and this adds nothing).
    /// </summary>
    public static float DeltaV(float speedRel, float rho, float cd, float area, float invMass, float gameWind, float dt)
    {
        if (!(speedRel > 1e-3f) || !(rho > 0f) || !(area > 0f) || !(invMass > 0f) || !(dt > 0f) || !(cd >= 0f)) return 0f;
        float ws = gameWind > 0f ? gameWind : 0f;
        // x = k |v| dt with k |v| = 0.5 rho A c invMass: the step's exact quadratic solution takes v x / (1 + x)
        float x = 0.5f * rho * area * MathF.Max(cd * speedRel, ws) * invMass * dt;
        float total = speedRel * x / (1f + x);
        float game = ws > 0f ? MathF.Min(0.5f * rho * area * ws * speedRel * invMass * dt, GameWindCapDv) : 0f;
        float dv = total - game;
        return dv > 0f && float.IsFinite(dv) ? dv : 0f;
    }

    /// <summary>
    /// The spin (local rad/s, the box's axes) after a step of air drag on it: about each axis the two pairs of faces
    /// sweeping the air, a plate of span L and width W turning at w meets 0.5 rho Cd (w r)^2 W dr at radius r, a torque of
    /// rho Cd W L^4 w|w| / 64; over a solid box's inertia m (L1^2 + L2^2) / 12, solved exactly over the step like the
    /// linear drag (never past zero, never flips the spin).
    /// </summary>
    public static Vector3 SpinDamped(Vector3 spinLocal, Vector3 size, float mass, float rho, float cd, float dt)
    {
        if (!(mass > 0f) || !(rho > 0f) || !(cd > 0f) || !(dt > 0f)) return spinLocal;
        float sx = size.X, sy = size.Y, sz = size.Z;
        float sx2 = sx * sx, sy2 = sy * sy, sz2 = sz * sz;
        float c = rho * cd * dt * (12f / 64f) / mass;
        return new Vector3(Axis(spinLocal.X, c * sx * (sy2 * sy2 + sz2 * sz2), sy2 + sz2),
                           Axis(spinLocal.Y, c * sy * (sx2 * sx2 + sz2 * sz2), sx2 + sz2),
                           Axis(spinLocal.Z, c * sz * (sx2 * sx2 + sy2 * sy2), sx2 + sy2));
    }

    // w / (1 + (k / I) |w| dt): k dt / I folded into num / den
    static float Axis(float w, float num, float den)
    {
        if (!(den > 1e-12f)) return w;
        float x = num / den * MathF.Abs(w);
        return float.IsFinite(x) ? w / (1f + x) : w;
    }
}
