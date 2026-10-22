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

    /// <summary>Harness: per-tick trace of every pad foot for PadTraceTicks ticks after its slider is made (or PadTraceNow bumped) -
    /// "[AERO-PADTRACE]": the compression, its rate, the tilt, the foot's speed against the ship, the ship's vertical speed, the foot's
    /// contacts this step (own grid / other) and their summed impulse.</summary>
    public static int PadTraceTicks = 0, PadTraceNow;
    int _traceLeft, _traceSeen, _traceMade;

    void PadTrace(Entity wheel, Entity grid)
    {
        if (PadTraceTicks <= 0) return;
        if (_traceSeen != PadTraceNow || _traceMade != _sliderLife.Created) { _traceSeen = PadTraceNow; _traceMade = _sliderLife.Created; _traceLeft = PadTraceTicks; }
        if (_traceLeft <= 0 || wheel == null || !GridPhysics.Alive(wheel.Data)) return;
        _traceLeft--;
        float x = _cm.CalculateSuspensionDistance(wheel);
        FootAttitude(out float tilt, out float spin);
        int own = 0, other = 0, feet = 0; float j = 0f; string what = "";
        if (wheel.Data.TryGet<ContactImpulseData>(out var cid))
            foreach (var c in cid.ImpulsesFromLastFrame)
            {
                if (!(c.ImpulseMagnitude > 0f)) continue;
                j += c.ImpulseMagnitude;
                if (c.OtherBody.Equals(grid.DEntity)) { own++; continue; }
                bool foot = false;
                lock (All) foreach (var og in All) { var w = og._cm?.WheelEntity; if (w != null && w.DEntity.Equals(c.OtherBody)) { foot = true; break; } }
                if (foot) feet++; else { other++; if (what.Length == 0) what = c.OtherBody.ToString(); }
            }
        Vector3 vw = Vector3.Zero, vg = Vector3.Zero;
        GridPhysics.TryGetVelocity(wheel.Data, out vw, out _); GridPhysics.TryGetVelocity(grid.Data, out vg, out _);
        Vector3 g = GridPhysics.GetGravityDirection(grid.Data); Vector3 up = g.LengthSquared() > 1e-4f ? -Vector3.Normalize(g) : Vector3.UnitY;
        Log.Default?.Info($"[AERO-PADTRACE] {_key} {Variant.Name} t{PadTraceTicks - _traceLeft} x {x:F3} lim {_sliderLo:F2}..{_sliderHi:F2} tilt {tilt:F1} w {spin:F2} dv {(vw - vg).Length():F2} vUp {Vector3.Dot(vg, up):F2} contacts own {own} feet {feet} other {other} {what} J {j:F0}");
    }

    void PadDevStep(Entity wheel)
    {
        if (Variant?.Foot == null) return;
        var grid = Entity.GetTopLevelParent();
        if (grid == null) return;
        PadTrace(wheel, grid);
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
