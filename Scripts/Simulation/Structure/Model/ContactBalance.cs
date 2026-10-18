#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// THE CONTACT LOADS' MOMENTUM BALANCE - pure, no game (2026-10-14; Structure/Server/StructureContacts runs exactly this, the
/// offline StructureTests 'balance under edits' check it on synthetic ticks). Between two reads (each before the physics
/// step) the grid's velocity changed by: the last read's extra force and gravity, the step's contacts (what we want), the
/// physics' own speed clamp, this tick's thrust and air (put in before the read) - and whatever ANOTHER MOD wrote into the
/// velocity (the Orbital mod, after the step: a frame anchor drained into the rails every tick, warp's rescale and
/// steering, the N^2 differential gravity, a spinning planet's fictitious forces; OrbitalApi.TryGetGridEdits gives their
/// running sum). So the support - the contacts' impulse over the step:
///     sup = m (v - v0 - written) - thrJ - airJ - (g0 m + x0) dt
/// and, AT THE PHYSICS' SPEED CAP (the engine clamps |v| there: VelocityLimitProvider.LinearVelocityLimit), the part along
/// the velocity is the clamp's, not a contact's - dropped.
/// g0 is the gravity the body GOT (AppliedGravity: after the dampeners' hold, x the engine's GravityMultiplier - 2026-10-18:
/// the planet's GravitySum read a parked ship's support at half its real weight at the vanilla multiplier 2, and a dampened
/// hover - the hold cancels the gravity, no thrust - or a free fall as a phantom support of a whole weight).
/// </summary>
public static class ContactBalance
{
    /// <summary>The gravity the physics applies (m/s2): the body's gravity after the game's hold (CustomGravity - GravitySum
    /// less the dampeners' hold, ThrustComponent.ComputeThrust) x the session's GravityMultiplier (PhysicsHack.GravityScale).</summary>
    public static Vector3 AppliedGravity(Vector3 heldGravity, float multiplier) => heldGravity * multiplier;

    /// <summary>The share of the cap at which the step's velocity counts as clamped (the engine clamps to it exactly).</summary>
    public static float CapShare = 0.999f;

    /// <summary>The velocity just after the last physics step: the read less what came after the step (the other mod's
    /// writes, this tick's thrust and air).</summary>
    public static Vector3 AfterStep(Vector3 vel, Vector3 written, Vector3 thrJ, Vector3 airJ, float m)
        => m > 0f ? vel - written - (thrJ + airJ) / m : vel - written;

    /// <summary>The step's velocity was clamped by the physics' speed cap (cap &lt;= 0: unknown, never).</summary>
    public static bool AtCap(Vector3 vAfterStep, float cap) => cap > 0f && vAfterStep.LengthSquared() >= cap * cap * CapShare * CapShare;

    /// <summary>The support over the last step (world, N s): the velocity's change less every known impulse and the other
    /// mod's writes; its part along the velocity dropped when the step ran at the speed cap.</summary>
    public static Vector3 Support(float m, Vector3 vel, Vector3 v0, Vector3 written, Vector3 thrJ, Vector3 airJ, Vector3 g0, Vector3 x0, float dt, float cap)
    {
        var sup = m * (vel - v0 - written) - thrJ - airJ - (g0 * m + x0) * dt;
        var vs = AfterStep(vel, written, thrJ, airJ, m);
        if (AtCap(vs, cap))
        {
            var d = Vector3.Normalize(vs);
            sup -= d * Vector3.Dot(sup, d);
        }
        return sup;
    }

    /// <summary>Where the grid should be now from where it was (the jump test): the physics moved it at the step's velocity
    /// (not the read's - the other mod's writes and this tick's thrust came after the move).</summary>
    public static Vector3D ExpectedMove(Vector3 vel, Vector3 written, Vector3 thrJ, Vector3 airJ, float m, float dt)
        => (Vector3D)(AfterStep(vel, written, thrJ, airJ, m) * dt);
}
