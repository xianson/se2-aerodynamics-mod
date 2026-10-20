#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>The formation's panel values: the slot right / up / back of the leader in its own frame (m), the most speed over the
/// leader's a gap is closed at (m/s).</summary>
public struct ApFormationParams
{
    public float RightM, UpM, BackM, CatchUpMs;
    public static ApFormationParams Of(in ApPanel p) => new ApFormationParams { RightM = p.FmRightM, UpM = p.FmUpM, BackM = p.FmBackM, CatchUpMs = p.FmCatchUpMs };
    public Vector3 Offset => new Vector3(RightM, UpM, BackM);
}

/// <summary>
/// FORMATION FLYING (engine-free; a built-in behaviour - ApBehaviour.Formation): the leader is the target (any grid the target
/// source picks - a grid by name, the API's, the targeting's; the filter does not apply), its motion ESTIMATED FROM ITS OBSERVED
/// POSITIONS (ApTargetTrack.Motion: velocity, acceleration, turn rate). The leader's frame: forward along its velocity (held
/// through a hover - its last heading), up against the gravity, right across; the SLOT: right / up / back in it, moving with the
/// leader's velocity and turn (v + w x r, its centripetal pull fed forward).
/// HELICOPTER / SIX-AXIS: the slot is the hover's / the thrusters' moving place - matched in position, speed and heading, the gap
/// closed at most CatchUpMs over the leader's speed.
/// AIRPLANE: along the leader's path the speed is the slot's plus a closure planned against the drag's deceleration (behind:
/// faster, at most CatchUpMs over; ahead: slower, never under MinShare of the leader's or the stall's margin); across, L1 steers
/// at the slot's place predicted LeadSeconds ahead along the leader's curved path (it rolls out on the leader's track, not cuts
/// across it); the height the slot's. OVERSHOOT (ahead of the slot by more than OvershootM and still pulling ahead at the least
/// speed): the aim point pushed out to the slot's side by up to a turn radius - a longer path drops it back - until it is within
/// a third of that again.
/// Readouts (status line, API, tests): the slot error (m) split along / across / up the leader's frame, the closure, overshooting.
/// </summary>
public sealed class ApFormation
{
    /// <summary>Airplane: the slot's place predicted this far ahead for the steering (s; the L1 point), the least speed share of the
    /// leader's while dropping back, the closure's share of the drag's deceleration it plans with, the near closure gain (1/s).</summary>
    public const float LeadSeconds = 2.5f, MinShare = 0.6f, DecelShare = 0.6f, NearGain = 0.15f;
    /// <summary>Under this leader speed (m/s) its heading is held, not read from its velocity.</summary>
    public const float HeadingSpeed = 3f;

    /// <summary>The slot error now (m: the slot's place less the ship's), along the leader's forward (+: the slot is ahead - the ship
    /// behind), across (right +) and up; its size; the closure speed asked over the leader's (m/s); overshooting; the slot (world).</summary>
    public float Along, Across, Up, Err, Closure; public bool Overshoot;
    public Vector3D Slot; public Vector3 SlotVel, Fwd = Vector3.UnitX;
    bool _hasFwd; float _side = 1f;

    public void Reset() { _hasFwd = false; Overshoot = false; Along = Across = Up = Err = Closure = 0f; }

    /// <summary>The goal for this tick (c: the ship and the leader's track; p: the slot; vMin: the least airspeed it flies - the
    /// stall's margin; 0 none).</summary>
    public void Plan(in ApBhCtx c, in ApFormationParams p, float vMin, ref ApNavGoal g)
    {
        var m = c.T.Motion;
        bool tr = m.Valid;
        Vector3D lp = tr ? m.Pos : c.T.Pos; Vector3 lv = tr ? m.Vel : c.T.Vel, la = tr ? m.Acc : c.T.Acc, w = tr ? m.TurnRate : Vector3.Zero;
        Vector3 up = c.Up;
        // the leader's frame: forward along its velocity (its last heading through a hover; first: the ship's own)
        float ls = lv.Length();
        if (ls > HeadingSpeed) { Fwd = lv / ls; _hasFwd = true; }
        else if (!_hasFwd)
        {
            Vector3 own = ApMath.Flat(c.Vel, up);
            Fwd = own.LengthSquared() > 1f ? Vector3.Normalize(own) : ApMath.Across(up); _hasFwd = true;
        }
        Vector3 u = ApMath.Flat(up, Fwd);
        u = u.LengthSquared() > 1e-4f ? Vector3.Normalize(u) : up;
        Vector3 right = Vector3.Cross(Fwd, u);
        Vector3 off = right * p.RightM + u * p.UpM - Fwd * p.BackM;
        Slot = lp + (Vector3D)off;
        SlotVel = lv + Vector3.Cross(w, off);
        Vector3 slotAcc = la + Vector3.Cross(w, Vector3.Cross(w, off));
        Vector3 e = (Vector3)(Slot - c.Pos);
        Along = Vector3.Dot(e, Fwd); Across = Vector3.Dot(e, right); Up = Vector3.Dot(e, u); Err = e.Length();
        float hSlot = c.THeight + Vector3.Dot(off, up);
        float vs = SlotVel.Length();
        g = ApNavGoal.None;
        g.Height = hSlot; g.Aim = false;
        if (c.Style != ApStyle.Airplane)
        {
            // a moving place: matched in position, speed and heading (the hover's / the thrusters' own closure, at most CatchUp over)
            g.Kind = ApNavKind.Point; g.Point = Slot; g.PointVel = SlotVel; g.PointAcc = slotAcc;
            g.Face = ApMath.Flat(Fwd, up);
            g.Speed = vs + MathF.Max(p.CatchUpMs, 1f);
            Closure = Vector3.Dot(c.Vel - SlotVel, e) / MathF.Max(Err, 1e-3f);
            Overshoot = false;
            return;
        }
        // ── the airplane: the speed along the leader's path, the steering at the slot ahead on its path ──
        float dec = MathF.Max(0.3f, c.Decel);
        float close;
        if (Along >= 0f) close = MathF.Min(p.CatchUpMs, MathF.Min(MathF.Sqrt(2f * DecelShare * dec * Along), NearGain * Along + 0.02f * Along * Along / MathF.Max(vs, 1f)));
        else
        {
            // (ahead: slower - the thrust's push back up is quick, the stall's floor is not: as the drag lets it, at most CatchUp under)
            float d = -Along;
            close = -MathF.Min(p.CatchUpMs, MathF.Min(MathF.Sqrt(2f * DecelShare * dec * d), NearGain * d + 0.02f * d * d / MathF.Max(vs, 1f)));
        }
        float vLeast = MathF.Max(MinShare * vs, vMin);
        g.Speed = MathF.Max(vs + close, vLeast); Closure = g.Speed - vs;
        // overshoot: ahead past OvershootM at the least speed - the path widened to drop back
        float overM = MathF.Max(40f, 0.25f * vs);
        if (!Overshoot && Along < -overM && g.Speed <= vLeast + 0.5f) { Overshoot = true; _side = p.RightM < 0f ? -1f : 1f; }
        else if (Overshoot && Along > -overM / 3f) Overshoot = false;
        float tau = LeadSeconds;
        Vector3D aim = Slot + (Vector3D)(SlotVel * tau + slotAcc * (0.5f * tau * tau));
        if (Overshoot) aim += (Vector3D)(right * (_side * MathF.Min(-Along, MathF.Max(c.TurnR, 50f))));
        g.Kind = ApNavKind.Point; g.Point = aim; g.PointVel = SlotVel; g.PointAcc = slotAcc;
        g.Face = ApMath.Flat(Fwd, up);
    }
}
