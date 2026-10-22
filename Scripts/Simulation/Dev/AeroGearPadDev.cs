#pragma warning disable
using System;
using System.Threading;
using Keen.VRage.Core;
using Keen.VRage.Physics.Data;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels;

namespace AeroMod;

/// <summary>
/// DEV: the pad-foot gears' in-game probes (tools/ingame/padgear_test.sh). Off by default; the harness sets them through aeroset and
/// MUST release them (PadRecord false, PadHop 0, PadPush 0) before the user plays.
///  - HOP (aeroset AeroGearComponent.PadHop V, m/s): the first pad gear's ship and everything joined to it (its feet:
///    QuickLaunchMove.MoveSet) get V straight up, once - it lands at V: a touchdown at a known sink rate.
///  - PUSH (aeroset AeroGearComponent.PadPush V, m/s): the same ship V along its block's forward, level, once - the feet slide.
///  - RECORDER (aeroset AeroGearComponent.PadRecord true; PadReset n starts a window): per pad gear, from its server step - the foot's
///    largest tilt off its block's up (deg), its largest turn rate against the ship (rad/s, the whole relative rate), the deepest compression
///    and a bottom-out (98 % of the stroke), the ship's sink rate at the first contact after it was airborne and its largest rebound
///    (m/s), the slider lost (count). aeroget AeroGearComponent.PadRecordResult (rebuilt twice a second while recording).
/// </summary>
public partial class AeroGearComponent
{
    public static float PadHop, PadPush;
    public static bool PadRecord;
    public static int PadReset;
    public static string PadRecordResult = "off";
    static int _padResetSeen;
    static double _padResultAt;

    float _rTilt, _rSpin, _rComp, _rTouch = float.NaN, _rRebound; bool _rBottom, _rAir; int _rLost0;

    void PadDevStep(Entity wheel)
    {
        if (Variant?.Foot == null) return;
        var grid = Entity.GetTopLevelParent();
        if (grid == null) return;
        if (PadHop != 0f || PadPush != 0f)
        {
            float up = PadHop, fwd = PadPush; PadHop = 0f; PadPush = 0f;
            Last = Kick(grid, up, fwd); Log.Default?.Info($"[AERO-GEAR] {_key} dev kick: {Last}");
        }
        if (!PadRecord) return;
        if (PadReset != _padResetSeen) { _padResetSeen = PadReset; ResetPadRecorders(); }
        if (FootAttitude(out float tilt, out float spin)) { _rTilt = MathF.Max(_rTilt, tilt); _rSpin = MathF.Max(_rSpin, MathF.Abs(spin)); }
        float comp = _cm != null ? _cm.CurrentSuspensionDistance : 0f;
        _rComp = MathF.Max(_rComp, comp);
        if (comp >= 0.98f * Variant.Foot.Stroke) _rBottom = true;
        bool contact = Data.Has<WheelOnGroundCollectorComponent.ProxyWheelContactData>();
        Vector3 g = GridPhysics.GetGravityDirection(grid.Data);
        if (g.LengthSquared() > 1e-4f && GridPhysics.TryGetVelocity(grid.Data, out Vector3 lin, out _))
        {
            float vUp = -Vector3.Dot(lin, Vector3.Normalize(g));
            if (!contact) _rAir = true;
            else if (_rAir && float.IsNaN(_rTouch)) _rTouch = -vUp;
            if (!float.IsNaN(_rTouch)) _rRebound = MathF.Max(_rRebound, vUp);
        }
        PublishPad();
    }

    static void ResetPadRecorders()
    {
        lock (All) foreach (var g in All) { g._rTilt = g._rSpin = g._rComp = g._rRebound = 0f; g._rTouch = float.NaN; g._rBottom = g._rAir = false; g._rLost0 = g._sliderLife.Lost; }
        _padResultAt = 0;
    }

    static void PublishPad()
    {
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (now < _padResultAt) return;
        _padResultAt = now + 0.5;
        var sb = new System.Text.StringBuilder();
        int n = 0;
        lock (All)
            foreach (var g in All)
            {
                if (g.Variant?.Foot == null) continue;
                n++;
                sb.Append(" | ").Append(g.Variant.Name)
                  .Append($" tilt {g._rTilt:F2} spin {g._rSpin:F3} comp {g._rComp:F3} bottom {(g._rBottom ? 1 : 0)}")
                  .Append($" touch {(float.IsNaN(g._rTouch) ? "-" : g._rTouch.ToString("F2"))} rebound {g._rRebound:F2} lost {g._sliderLife.Lost - g._rLost0} slider {(g.PadHeld ? 1 : 0)} alone {(ReferenceEquals(g._releasedWheel, g._cm?.WheelEntity) ? 1 : 0)}");
            }
        PadRecordResult = $"rec {n} pad gears sliders {PadSliders} released {PadReleased} boxes {PadBoxes}" + sb;
    }

    /// <summary>The ship and everything joined to it: up (against the gravity) and forward (its block's, level) at these speeds.</summary>
    string Kick(Entity grid, float up, float fwd)
    {
        var set = new List<Entity>();
        if (!QuickLaunchMove.MoveSet(grid.GetSession(), grid, set, out string why)) return "refused: " + why;
        Vector3 g = GridPhysics.GetGravityDirection(grid.Data);
        if (g.LengthSquared() < 1e-4f) return "refused: no gravity";
        Vector3 u = -Vector3.Normalize(g);
        Vector3 f = Vector3.Transform(Vector3.Forward, Entity.Data.GetWorldTransform().Orientation);
        f -= u * Vector3.Dot(f, u);
        if (f.LengthSquared() > 1e-6f) f = Vector3.Normalize(f);
        Vector3 dv = u * up + f * fwd;
        int n = 0;
        foreach (var e in set)
        {
            if (!GridPhysics.Alive(e.Data)) continue;
            ref var rb = ref e.Data.TryGetWritePtr<RigidBodyData>();
            if (Unsafe.IsNullRef(in rb)) continue;
            rb.LinearVelocity += dv; n++;
        }
        return $"{n} bodies: +{up:F1} m/s up, +{fwd:F1} m/s forward";
    }
}
