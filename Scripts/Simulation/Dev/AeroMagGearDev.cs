#pragma warning disable
using System;
using System.Threading;
using Keen.VRage.Core;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// DEV: the magnetic gear's in-game probes (tools/ingame/maggear_test.sh). All off by default; the harness sets them through
/// aeroset and MUST release them (DevRecord false, DevHop 0) before the user plays.
///  - HOP (aeroset AeroMagGearComponent.DevHop V, m/s): the first magnetic gear's ship - and everything joined to it (its wheels,
///    its pads: QuickLaunchMove.MoveSet) - gets V straight up (against the gravity), once; it lands at V: a touchdown at a known
///    sink rate. Refused while its pad is locked or the ship is joined to something static (Last says so).
///  - RECORDER (aeroset AeroMagGearComponent.DevRecord true; reset by DevReset 1): per magnetic gear, from its server step - the
///    pad's drift off its joined pose in the wheel's frame (m, deg: the join holds), its tilt off the block's up against the tilt
///    at the join (deg: the axle hold keeps the pad's attitude), the suspension's deepest compression and whether it bottomed out
///    (the stroke's 98 %), the ship's sink rate at the first pad contact after it was airborne and its highest rebound after it (m/s), self-lock vetoes,
///    times the pad was not joined. aeroget AeroMagGearComponent.DevRecordResult (rebuilt twice a second while recording).
/// </summary>
public partial class AeroMagGearComponent
{
    public static float DevHop;
    public static bool DevRecord;
    public static int DevReset;
    public static string DevRecordResult = "off";
    static int _devResetSeen;
    static double _devResultAt;

    // per gear, while recording
    RelativeTransform _joinRel; bool _hasJoinRel; double _joinTilt;
    float _recDrift, _recDriftDeg, _recTiltDev, _recComp, _recTouchV = float.NaN, _recRebound; bool _recBottom, _recAir; int _recUnjoined;

    void DevStep(double now, Entity grid, Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleComponent cm)
    {
        if (DevHop != 0f) { float v = DevHop; DevHop = 0f; Last = Hop(grid, v); Log.Default?.Info("[AERO-MAGGEAR] dev hop: " + Last); }
        if (!DevRecord) { _hasJoinRel = false; return; }
        if (DevReset != _devResetSeen) { _devResetSeen = DevReset; ResetRecorderAll(); }
        var wheel = cm?.WheelEntity;
        if (!_gear.PadJoined || _pad == null || wheel == null || !GridPhysics.Alive(_pad.Data) || !GridPhysics.Alive(wheel.Data)) { _recUnjoined++; _hasJoinRel = false; Publish(now); return; }
        var pw = _pad.Data.GetWorldTransform(); var ww = wheel.Data.GetWorldTransform(); var bw = Entity.Data.GetWorldTransform();
        var rel = (RelativeTransform)WorldTransform.GetRelativeTransform(pw, ww);
        double tilt = Math.Acos(Math.Clamp(Vector3D.Dot(Vector3D.Transform(Vector3D.Up, pw.Orientation), Vector3D.Transform(Vector3D.Up, bw.Orientation)), -1.0, 1.0)) * 57.2958;
        if (!_hasJoinRel) { _hasJoinRel = true; _joinRel = rel; _joinTilt = tilt; }
        _recDrift = MathF.Max(_recDrift, (rel.Position - _joinRel.Position).Length());
        float dq = MathF.Abs(Quaternion.Dot(Quaternion.Normalize(rel.Orientation), Quaternion.Normalize(_joinRel.Orientation)));
        _recDriftDeg = MathF.Max(_recDriftDeg, 2f * MathF.Acos(MathF.Min(1f, dq)) * 57.2958f);
        _recTiltDev = MathF.Max(_recTiltDev, (float)Math.Abs(tilt - _joinTilt));
        float comp = cm.CurrentSuspensionDistance;
        _recComp = MathF.Max(_recComp, comp);
        float stroke = _variant?.MagPad?.Stroke ?? 0.75f;
        if (comp >= 0.98f * stroke) _recBottom = true;
        // the ship's vertical speed (up +): the sink rate at the first contact, the highest rebound after it
        Vector3 g = GridPhysics.GetGravityDirection(grid.Data);
        if (g.LengthSquared() > 1e-4f && GridPhysics.TryGetVelocity(grid.Data, out Vector3 lin, out _))
        {
            float up = -Vector3.Dot(lin, Vector3.Normalize(g));
            // (the touch: the first contact after the pad was off the ground - a reset on the ground waits for the hop)
            if (!_gear.PadContact) _recAir = true;
            else if (_recAir && float.IsNaN(_recTouchV)) _recTouchV = -up;
            if (!float.IsNaN(_recTouchV)) _recRebound = MathF.Max(_recRebound, up);
        }
        Publish(now);
    }

    void ResetRecorder()
    {
        _hasJoinRel = false; _recDrift = _recDriftDeg = _recTiltDev = _recComp = _recRebound = 0f; _recTouchV = float.NaN;
        _recBottom = _recAir = false; _recUnjoined = 0;
    }

    static void ResetRecorderAll()
    {
        lock (All) foreach (var g in All) g.ResetRecorder();
        Interlocked.Exchange(ref SelfLockVetoes, 0);
        _devResultAt = 0;
    }

    static void Publish(double now)
    {
        if (now < _devResultAt) return;
        _devResultAt = now + 0.5;
        var sb = new System.Text.StringBuilder();
        lock (All)
        {
            sb.Append("rec ").Append(All.Count).Append(" gears selflock ").Append(SelfLockVetoes);
            foreach (var g in All)
                sb.Append(" | ").Append(MagPadNames.Name(g.Life.Uid))
                  .Append($" drift {g._recDrift:F3} m {g._recDriftDeg:F2} deg tilt {g._recTiltDev:F2} deg comp {g._recComp:F3} m bottom {(g._recBottom ? 1 : 0)}")
                  .Append($" touch {(float.IsNaN(g._recTouchV) ? "-" : g._recTouchV.ToString("F2"))} m/s rebound {g._recRebound:F2} m/s unjoined {g._recUnjoined}")
                  .Append(g._magnet != null && g._magnet.IsLocked ? " LOCKED" : "");
        }
        DevRecordResult = sb.ToString();
    }

    /// <summary>The ship and everything joined to it straight up at v (m/s) - it lands at v.</summary>
    string Hop(Entity grid, float v)
    {
        if (_magnet != null && _magnet.IsLocked) return "hop refused: the pad is locked";
        var set = new List<Entity>();
        if (!QuickLaunchMove.MoveSet(grid.GetSession(), grid, set, out string why)) return "hop refused: " + why;
        Vector3 g = GridPhysics.GetGravityDirection(grid.Data);
        if (g.LengthSquared() < 1e-4f) return "hop refused: no gravity";
        var up = -Vector3.Normalize(g) * v;
        int n = 0;
        foreach (var e in set)
        {
            if (!GridPhysics.Alive(e.Data)) continue;
            ref var rb = ref e.Data.TryGetWritePtr<RigidBodyData>();
            if (Unsafe.IsNullRef(in rb)) continue;
            rb.LinearVelocity += up; n++;
        }
        return $"hop: {n} bodies up at {v:F1} m/s";
    }
}
