#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// A HELICOPTER ROTOR'S STATE AND PARTS (2026-10-16, the user: "proper helicopter rotor blade animation"), game-free: the
/// server's governed RPM (AeroPropulsorComponent - the thrust follows it) and every pose the client draws
/// (AeroRotorAnimComponent), tested offline (Tests/RotorTests).
///   RPM        governed: on and powered it spins up (an engine's torque, captured by the governor near 100 %) and HOLDS
///              100 %; off or unpowered it coasts down (the air's drag ~ rpm^2, then friction). Lift is the blades' PITCH:
///              the thrust a rotor can give is its maximum x rpm^2, never more - and NONE below MinThrustRpm (2026-10-16,
///              the user: "there has to be a minimum spin speed while thrusting ... it can't just be rotating slowly"): a
///              rotor that is visibly turning slowly gives no lift at all; lift fades in between MinThrustRpm and FullThrustRpm
///              (as a real rotor, whose clutch / governor engages near operating rpm).
///   collective every blade's pitch (at 75 % radius, leading edge up) from flat pitch at no thrust to MaxPitchDeg at the
///              rotor's whole maximum now: the thrust the game asks of it / that maximum.
///   cyclic     the disc (hub, blades, blur disc) tilts as RotorCyclic tilts the thrust: toward the side the thrust leans
///              to, by RotorCyclic.MaxTiltDeg x the share of its reach it used. Each blade's pitch also varies once a turn
///              (the swashplate): highest 90 deg BEFORE the disc's high side (a blade flaps 90 deg after its pitch).
///   coning     the blades flap up with the thrust (lift against the centrifugal stiffness: ~ thrust / rpm^2), and droop
///              when the rotor is slow and unloaded.
///   blur       at speed the blades fade out (no strobing at 60 fps) and the blur disc fades in; back as it slows. The disc's
///              translucency is its MATERIAL's (AeroRotorBlur, a PBRTransparent material with an alpha texture -
///              gen_propulsor_defs.py), so at full opacity it is a smooth blend; the entity opacity only fades it in and out
///              (the engine dithers an entity below 1 - a stipple, seen only while it changes).
///   reaction   the torque the engine needs to hold the governed rpm against the blades' drag, Q = P / omega (x ReactionScale,
///              the calibration to a real helicopter's tail share): the ship is turned the other way (RotorCyclic applies it;
///              a tail rotor or gyros must answer it - RotorLimiter caps the thrust to what they can hold).
/// Frame: the block's (VRage: +Y the rotor axis). Blade k is spawned at its hinge, turned (k x 120 deg) about +Y, its span
/// +Z, its chord X with the leading edge at -X; the rotor turns CLOCKWISE seen from above (about -Y): leading edge first.
/// </summary>
public static class RotorKinematics
{
    // ── RPM ──
    /// <summary>On: ~95 % of the governed rpm after this long from rest (s).</summary>
    public static float SpinUpSeconds = 6f;
    /// <summary>No thrust below this share of the governed rpm; the rpm^2 law in full from FullThrustRpm.</summary>
    public static float MinThrustRpm = 0.6f, FullThrustRpm = 0.85f;
    /// <summary>Off: the coast-down's drag (1/s at full rpm: d rpm/dt = -rpm^2 / DragTau - Friction).</summary>
    public static float DragTau = 9f, Friction = 0.025f;
    /// <summary>The blade tips' speed at the governed rpm (m/s): the rpm = TipSpeed / tip radius.</summary>
    public static float TipSpeed = 200f;

    // ── blades ──
    /// <summary>Collective (deg, at 75 % radius): no thrust, and the rotor's whole maximum now.</summary>
    public static float FlatPitchDeg = 0.5f, MaxPitchDeg = 13f;
    /// <summary>Coning at the rated thrust and the governed rpm (deg); the most; the droop of a slow unloaded rotor.</summary>
    public static float ConeAtRatedDeg = 4f, MaxConeDeg = 9f, DroopDeg = 1.5f;

    // ── blur ──
    /// <summary>The blades: fully shown up to BladeFadeLo of the governed rpm, gone (BladeMinOpacity) from BladeFadeHi.</summary>
    public static float BladeFadeLo = 0.45f, BladeFadeHi = 0.8f, BladeMinOpacity = 0f;
    /// <summary>The blur disc: from nothing at BlurFadeLo to BlurMaxOpacity at BlurFadeHi.</summary>
    public static float BlurFadeLo = 0.35f, BlurFadeHi = 0.85f, BlurMaxOpacity = 1f;

    // ── reaction torque ──
    /// <summary>Induced power over the ideal (momentum theory), and the blades' profile power coefficient (solidity x profile
    /// drag / 8: profile power = that x rho x A x tip speed^3).</summary>
    public static float InducedFactor = 1.15f, ProfileCoef = 0.06f * 0.01f / 8f;

    /// <summary>The power (W) a rotor of tip radius R (m) needs to give thrust (N) at this rpm (0..1 governed) in air of
    /// density rho (kg/m^3): the induced power (hover momentum theory x InducedFactor) + the blades' profile power.</summary>
    public static float Power(float thrust, float tipRadius, float rpm, float rho)
    {
        if (tipRadius <= 0f || rho <= 0f || rpm <= 0f) return 0f;
        float A = MathF.PI * tipRadius * tipRadius, T = MathF.Max(thrust, 0f), vt = TipSpeed * Math.Clamp(rpm, 0f, 1f);
        return InducedFactor * T * MathF.Sqrt(T / (2f * rho * A)) + ProfileCoef * rho * A * vt * vt * vt;
    }

    /// <summary>
    /// THE REACTION TORQUE'S CALIBRATION (2026-10-17, the user: "calibrated like a real helicopter"). Q = P / omega keeps its
    /// shape (the induced power ~ T^1.5: the torque grows faster than the thrust), scaled by this so that a tail thruster at a
    /// typical arm - the rotor's tip radius behind the mast - needs 10 % of the main rotor's thrust at its RATED thrust, as on a
    /// real helicopter (Q / (T R) = (P / T) / tip speed ~ 0.08-0.10 at a real disc loading of 300-500 N/m^2).
    /// Why it is needed: the game's rotors carry far more weight per disc area than real ones (uniform 5.1 kN/m^2:
    /// HelicopterRotor_150's 900 kN on a 7.5 m disc - gen_propulsor_defs.py), so momentum theory's induced velocity
    /// sqrt(T / 2 rho A) is ~50 m/s instead of ~13 and the raw formula asks ~29 % of the thrust at that arm: no sensible
    /// tail could hold it. 0.344 = 0.10 / 0.29 (the raw Q / (T R) at that disc loading). (2026-10-18: 0.42 at the old 600 kN
    /// rating - the rotors went 1.5x for honest dampeners, and the scale with them so the tail share stays 10 %.)
    /// One constant fits every size: at a uniform disc loading Q / (T R) does not depend on R (and with the thrust ~ rho, nor
    /// on the air's density). Tests/RotorTests pins the 10 % for every rotor in the table.
    /// </summary>
    /// (2026-10-18, the user: "just lower the helicopter torque" - a tip-radius tail needs 3 % of rated thrust, not 10 %, so one
    /// small Reversible Rotor (36 kN) holds the 1.5 m rotor; 0.344 x 0.3.)
    public static float ReactionScale = 0.1032f;

    /// <summary>The reaction torque (N m, about the rotor axis, against its turn: +axis for this clockwise-from-above rotor)
    /// the shaft puts on the ship: Q = ReactionScale x P / omega. None below 2 % rpm (stopped: nothing to react).</summary>
    public static float ReactionTorque(float thrust, float tipRadius, float rpm, float rho)
    {
        if (rpm <= 0.02f || tipRadius <= 0f) return 0f;
        float omega = TipSpeed / tipRadius * Math.Clamp(rpm, 0f, 1f);
        return ReactionScale * Power(thrust, tipRadius, rpm, rho) / omega;
    }

    /// <summary>The inverse of ReactionTorque: the most thrust (N) whose reaction torque is at most q (N m) - closed form
    /// (Q omega / scale - profile power = InducedFactor T^1.5 / sqrt(2 rho A)). 0 when q does not even cover the blades'
    /// profile drag; +infinity when there is no reaction to hold (stopped: under 2 % rpm, no radius, no air, no scale).</summary>
    public static float ThrustForTorque(float q, float tipRadius, float rpm, float rho)
    {
        if (rpm <= 0.02f || !(tipRadius > 0f) || !(rho > 0f) || !(ReactionScale > 0f)) return float.PositiveInfinity;
        float A = MathF.PI * tipRadius * tipRadius, vt = TipSpeed * Math.Clamp(rpm, 0f, 1f), omega = vt / tipRadius;
        float induced = MathF.Max(q, 0f) * omega / ReactionScale - ProfileCoef * rho * A * vt * vt * vt;
        if (!(induced > 0f)) return 0f;
        float t = MathF.Pow(induced * MathF.Sqrt(2f * rho * A) / InducedFactor, 2f / 3f);
        return float.IsFinite(t) ? t : float.PositiveInfinity;
    }

    /// <summary>A turboprop's governed rpm while on and asked for nothing (2026-10-17, the user: "the same 0.75 blade speed /
    /// blur treatment" - the vanilla prop's 75 % on): it rises to 100 % with the thrust asked (PropulsorKinds.TargetRpm).</summary>
    public static float TurbopropIdleRpm = 0.75f;

    /// <summary>One step of the governed rpm (0..1 of the governed rpm) toward target while on (1: a rotor's 100 %; a
    /// turboprop's idle .. 100 % by its load - PropulsorKinds.TargetRpm), coasting down while off.</summary>
    public static float StepRpm(float rpm, bool on, float dt, float target = 1f)
    {
        if (dt <= 0f) return rpm;
        if (on)
        {
            // the engine's torque, the governor taking it near the target: the gap halves every SpinUpSeconds / 4.3 (95 % of
            // the way at SpinUpSeconds), plus a little so it arrives; above it (a turboprop's load dropped) back down alike
            target = Math.Clamp(target, 0f, 1f);
            if (rpm <= target) { rpm += ((target - rpm) * 3f / SpinUpSeconds + 0.01f) * dt; return MathF.Min(rpm, target); }
            rpm -= ((rpm - target) * 3f / SpinUpSeconds + 0.01f) * dt;
            return MathF.Max(rpm, target);
        }
        rpm -= (rpm * rpm / DragTau + Friction) * dt;
        return MathF.Max(rpm, 0f);
    }

    /// <summary>The share of its maximum a rotor at this rpm can give (lift ~ rpm^2 at a pitch it cannot exceed), none while
    /// it turns slowly (below MinThrustRpm), faded in up to FullThrustRpm.</summary>
    public static float ThrustFactor(float rpm) { rpm = Math.Clamp(rpm, 0f, 1f); return rpm * rpm * Smooth(MinThrustRpm, FullThrustRpm, rpm); }

    /// <summary>The collective pitch (rad, at 75 % radius) for this thrust (N) and the rotor's maximum now (N).</summary>
    public static float Collective(float thrust, float maxNow)
    {
        float load = maxNow > 1f ? Math.Clamp(thrust / maxNow, 0f, 1.1f) : 0f;
        return (FlatPitchDeg + (MaxPitchDeg - FlatPitchDeg) * load) * MathF.PI / 180f;
    }

    /// <summary>The coning (rad, up +) for this thrust (N), the rated thrust (N) and rpm: the lift against the centrifugal
    /// stiffness (~ rpm^2), less the droop of a slow rotor.</summary>
    public static float Coning(float thrust, float rated, float rpm)
    {
        float w2 = Math.Clamp(rpm, 0f, 1f); w2 *= w2;
        float lift = rated > 1f ? ConeAtRatedDeg * MathF.Max(thrust, 0f) / (rated * MathF.Max(w2, 0.15f)) : 0f;
        float deg = MathF.Min(lift, MaxConeDeg) - DroopDeg * (1f - w2);
        return deg * MathF.PI / 180f;
    }

    static float Smooth(float lo, float hi, float x) { float t = Math.Clamp((x - lo) / (hi - lo), 0f, 1f); return t * t * (3f - 2f * t); }
    /// <summary>The blades' opacity at this rpm (0..1 of the governed rpm).</summary>
    public static float BladeOpacity(float rpm) => BladeMinOpacity + (1f - BladeMinOpacity) * (1f - Smooth(BladeFadeLo, BladeFadeHi, rpm));
    /// <summary>The blur disc's opacity at this rpm.</summary>
    public static float BlurOpacity(float rpm) => BlurMaxOpacity * Smooth(BlurFadeLo, BlurFadeHi, rpm);

    /// <summary>The disc's tilt: tilt is the way the thrust leans (block frame, in the disc's plane), its length the angle
    /// (rad). The rotor axis (+Y) turned toward it.</summary>
    public static Quaternion Tilt(Vector3 tilt)
    {
        tilt.Y = 0f;
        float a = tilt.Length();
        if (a < 1e-6f) return Quaternion.Identity;
        var axis = Vector3.Cross(Vector3.UnitY, tilt / a);
        return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), a);
    }

    /// <summary>The spin's turn at azimuth psi (rad, clockwise from above: about -Y).</summary>
    public static Quaternion Spin(float psi) => Quaternion.CreateFromAxisAngle(Vector3.UnitY, -psi);

    /// <summary>Where a blade's span points in the (untilted) disc at azimuth psi (unit, block frame), from its rest turn.</summary>
    public static Vector3 SpanDir(Quaternion restRot, float psi) => Vector3.Transform(Vector3.Transform(Vector3.UnitZ, restRot), Spin(psi));

    /// <summary>A blade's pitch (rad, leading edge up) at azimuth: the collective + the cyclic's once-a-turn part - the tilt's
    /// angle, highest where the span points 90 deg before the disc's HIGH side (opposite the lean) in the turn.</summary>
    public static float BladePitch(float collective, Vector3 tilt, Vector3 span)
    {
        tilt.Y = 0f;
        float a = tilt.Length();
        if (a < 1e-6f) return collective;
        // the high side is -lean; 90 deg before it (the turn is about -Y): that direction turned +90 deg about +Y
        Vector3 high = -tilt / a;
        Vector3 most = Vector3.Transform(high, Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));
        return collective + a * Vector3.Dot(span, most);
    }

    /// <summary>A blade's pose (block frame): spawned at rest (its hinge restPos, turned restRot), turned with the disc
    /// (hub: the pivot, disc: the tilt, psi: the spin), flapped up by flap and pitched by pitch about its span (rad, both +
    /// up / leading edge up; the model's own pitch is the 0).</summary>
    public static void BladePose(Vector3 hub, Quaternion disc, float psi, Vector3 restPos, Quaternion restRot, float flap, float pitch,
        out Vector3 pos, out Quaternion rot)
    {
        var frame = disc * Spin(psi);
        pos = hub + Vector3.Transform(restPos - hub, frame);
        // (its own axes: flap about the chord X - the span +Z up toward +Y is -flap; pitch about the span Z - the leading
        //  edge -X up is -pitch)
        var own = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -flap) * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -pitch);
        rot = Quaternion.Normalize(frame * restRot * own);
    }

    /// <summary>The hub's pose: turned by the spin, tilted with the disc, about its own pivot.</summary>
    public static Quaternion HubRot(Quaternion disc, float psi, Quaternion restRot) => Quaternion.Normalize(disc * Spin(psi) * restRot);

    /// <summary>The blur disc's pose: tilted with the disc about the hub, raised as the coning exceeds its model's cone
    /// (refCone rad) over its span (m, hinge to tip: the disc's mean height moves by ~0.6 of the tip's).</summary>
    public static void BlurPose(Vector3 hub, Quaternion disc, Vector3 restPos, Quaternion restRot, float cone, float refCone, float span,
        out Vector3 pos, out Quaternion rot)
    {
        float lift = 0.6f * span * (MathF.Tan(cone) - MathF.Tan(refCone));
        pos = hub + Vector3.Transform(restPos - hub + Vector3.UnitY * lift, disc);
        rot = Quaternion.Normalize(disc * restRot);
    }
}
