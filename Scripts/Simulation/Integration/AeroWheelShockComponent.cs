#pragma warning disable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Physics.Constraints;
using Keen.VRage.Physics.Constraints.Data;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// WHEEL SHOCKS ON EVERY WHEEL (server; 2026-10-20, the user: "reflect and give ALL wheels airshock / oleo until Keen does it" - a
/// stopgap for SE2's weak wheel impacts). Injected into every prefab with a vanilla suspension (WheelCornerModuleComponent: the
/// game's wheels and the mod's own gears); it acts only on grids the mod simulates (an AeroGridComponent, aero on), in the server's
/// scene, per Wheels.Shocks (WheelShockSettings.Mode). The vanilla wheel stays the vanilla wheel - its rolling, steering, brakes,
/// parking, power and limits untouched; what this adds:
///  - THE AIRSHOCK (WheelAirshock: armed from the wheel's own sink rate and its tyre's height over the ground - the planet's
///    surface, GamePlanetHeight, and the grid's ground ray, the nearer): the wheel constraint's damping factor to full
///    (ShockDamping) before the touch and, airshock only, its strength to the cap (GearStiffness.MaxStrength, an effective tau
///    of 1.67); the player's values back once settled;
///  - THE OLEO (Airshock + oleo; WheelOleo / WheelRating: rated by the wheel's size, stroke and mass, never the ship): an absolute
///    spring-damper along the suspension axis, from the compression (the corner module's own CalculateSuspensionDistance, fresh)
///    and its rate (the wheel body's velocity against the ship's at the mount - read now, not a step-old difference). Its force is
///    a PAIR OF IMPULSES - the ship +F along the axis at the suspension's mount, the wheel -F at its centre - equal and opposite, a
///    modelled strut (no force from nowhere: on the ground the wheel's contact carries it, as it carries the constraint's). While
///    it carries, the wheel constraint's own soft spring is the player's x WheelShockSettings.ConstraintSpringShare (0.25): low,
///    so the two springs do not fight; the constraint keeps its hard limits (an overload bottoms there) and the wheel's return.
///    Why impulses and not the engine's spring-damper motor (ISpringDamperConstraintMotor, N/m): a motor needs a constraint of its
///    own between the wheel and the ship beside the wheel constraint (a prismatic one would lock the wheel's roll and steering, a
///    six-degree one is a new body pair the game's wheel code does not know), it is linear (no gas stiffening, no asymmetric
///    damping, no rate cap without rewriting it every step anyway) - the impulses are the strut itself, solved implicitly here.
/// Order each tick: this job (after the grids' systems, before the physics step) reads and decides per wheel - parallel, read-only
/// but for its own constraint; the impulses go in ONE job a tick (AeroGridComponent.WheelShockApplyJob: every pending pair, single
/// threaded - a grid's body is shared by its wheels' jobs).
/// The mod's own gears (AeroGearComponent): the same, while the gear is down and not held up (ShockUsable); its per-block airshock
/// switch is honoured; while this owns the spring (ShockOwned) the gear does not write its stiffness. Off: the player's values
/// written back once, nothing else.
/// Reflection (docs/REFLECTION_AND_WHITELIST.md §3.9): WheelCornerModuleComponent._constraint (as the gear) and ._suspension
/// (the suspension's idle frame and axis - its public SuspensionInfo record), each read once per wheel.
/// </summary>
public partial class AeroWheelShockComponent : Component, IInSceneListener
{
    internal static readonly List<AeroWheelShockComponent> All = new();
    /// <summary>Non-zero: impulses or dev work wait for this tick's apply job.</summary>
    internal static int Armed;
    // diagnostics (aeroget AeroWheelShockComponent.X)
    public static int Wheels, Resolved, ArmedNow, Arms, Faults;
    public static string LastFault = "-";

    [After(typeof(Keen.Game2.Simulation.GridUpdateOrder.UpdateSystems))]
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    internal class OnWheelShock : JobGroup;

    [OnWheelShock]
    [MustHave(typeof(AeroWheelShockComponent))]
    private static void ShockJob(AeroWheelShockComponent s)
    {
        if (!s._server || s._dead) return;
        s.Tick();
        if (WheelShockDev.Active) Volatile.Write(ref Armed, 1);   // (the harness's drop and recorder run in the apply job)
    }

    // ── per wheel ──
    bool _server, _dead, _looked;
    WheelCornerModuleComponent _cm;
    AeroGearComponent _gear;
    Entity _wheelSeen;
    IWheelConstraint _wc;
    WheelCornerModuleComponent.SuspensionInfo _susp;
    WheelRating _rating;
    readonly WheelAirshock _shock = new();
    GamePlanetHeight _height;
    /// <summary>This component wrote the constraint's factors (the player's go back when it lets go).</summary>
    bool _owned;
    float _lastF;
    // this tick's result, for the apply job: the impulse (N s, + extends) along the axis at the mount; the wheel; the grid
    internal float PendJ; internal Vector3 PendAxis; internal Vector3D PendAt; internal Entity PendWheel, PendGrid;
    // read by the apply job and the recorder
    internal Entity Grid; internal bool Contact, Grounded; internal int GridGrounded = 1;
    internal float X, V, F;
    /// <summary>The factors last written (the readouts: the constraint itself is never read off the server's step).</summary>
    internal float WrittenS = float.NaN, WrittenD = float.NaN;
    internal WheelRating Rating => _rating;
    internal bool ShockArmed => _shock.Armed;
    internal WheelCornerModuleComponent Module => _cm;

    void IInSceneListener.OnAddedToScene()
    {
        try { _server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { _server = true; }
        if (!_server) return;
        _dead = false;
        lock (All) { All.Add(this); Wheels = All.Count; }
    }
    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        if (!_server) return;
        lock (All) { All.Remove(this); Wheels = All.Count; }
        if (_shock.Armed) Interlocked.Decrement(ref ArmedNow);
        _shock.Reset(); _wc = null; _wheelSeen = null; PendJ = 0f; _dead = true;
    }

    void Tick()
    {
        PendJ = 0f; F = 0f;
        if (!_looked) { _looked = true; _cm = Entity?.TryGet<WheelCornerModuleComponent>(); _gear = Entity?.TryGet<AeroGearComponent>(); }
        if (_cm == null) return;
        var wheel = _cm.WheelEntity;
        if (!ReferenceEquals(wheel, _wheelSeen)) { _wheelSeen = wheel; _wc = null; _lastF = 0f; Disarm(); }
        if (wheel == null || !GridPhysics.Alive(wheel.Data)) { Grid = null; Grounded = false; Contact = false; return; }
        var grid = Entity.GetTopLevelParent();
        Grid = grid;
        var aero = grid?.TryGet<AeroGridComponent>();
        int mode = WheelShockSettings.Mode;
        bool usable = mode != WheelShockSettings.Off && AeroSwitch.Enabled && aero != null && aero.IsServerScene
                      && (_gear == null || _gear.ShockUsable) && GridPhysics.Alive(grid.Data);
        if (_wc == null && usable && !Resolve(wheel)) usable = false;
        if (!usable) { LetGo(); return; }

        // ── the state: compression, its rate, the wheel's sink and height ──
        var gd = grid.Data; var wd = wheel.Data;
        if (!gd.TryGet<RigidBodyData>(out var srb) || !gd.TryGet<RigidBodyMassProperties>(out var smass) || !(smass.InvMass > 0f)
            || !wd.TryGet<RigidBodyData>(out var wrb) || !wd.TryGet<RigidBodyMassProperties>(out var wmass) || !(wmass.InvMass > 0f))
        { LetGo(); return; }
        var gwt = gd.GetWorldTransform();
        var idleW = _susp.IdleTransform * Entity.Data.GetWorldTransform();
        Vector3 axis = idleW.Orientation.GetUp();
        float x = _cm.CalculateSuspensionDistance(wheel);
        Vector3D mount = idleW.Position + (Vector3D)(axis * x);
        Vector3 vShip = srb.GetLinearVelocityAtPoint(in smass, in gwt, in mount);
        float v = Vector3.Dot(wrb.LinearVelocity - vShip, axis);
        if (!float.IsFinite(x + v)) { LetGo(); return; }
        X = x; V = v;
        bool contact = Data.Has<WheelOnGroundCollectorComponent.ProxyWheelContactData>();
        Contact = contact;
        Vector3 g = GridPhysics.GetGravityDirection(gd);
        float gl = g.Length();
        Vector3 up = gl > 0.1f ? -g / gl : axis;
        var wpos = wd.GetWorldTransform().Position;
        float sink = -Vector3.Dot(wrb.LinearVelocity, up);

        // ── the airshock ──
        bool allowShock = _gear == null || _gear.AirshockEnabled;
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        float h = float.MaxValue;
        if (allowShock && !_shock.Armed && !contact && sink > WheelAirshock.MinSink) h = Height(aero, gwt, wpos, up);
        int step = _shock.Step(allowShock, contact, sink, h, now);
        if (step > 0) { Arms++; Interlocked.Increment(ref ArmedNow); }
        else if (step < 0) Interlocked.Decrement(ref ArmedNow);
        if (step != 0 && _gear != null) _gear.ShockState = _shock.State;   // (the gear's readout)
        bool armed = _shock.Armed;

        // ── the constraint's factors (written only when they change) ──
        float baseS = PlayerStrength(), baseD = Math.Max(0f, _cm.SuspensionDamping);
        float wantS, wantD = armed ? WheelAirshock.ShockDamping : baseD;
        if (mode == WheelShockSettings.AirshockOnly) wantS = armed ? GearStiffness.MaxStrength : baseS;
        else wantS = GearStiffness.Cap(baseS * Math.Clamp(WheelShockSettings.ConstraintSpringShare, 0f, 1f));
        Write(wantS, wantD);

        // ── the oleo ──
        if (mode != WheelShockSettings.AirshockOleo) { _lastF = 0f; Grounded = false; return; }
        float dt = AeroPhysics.Dt;
        float mAt = MassAt(in srb, in smass, in gwt, mount, axis);
        Grounded = contact;
        float m = contact ? WheelOleo.GroundMass(mAt, smass.Mass, GridGrounded) : WheelOleo.AirMass(mAt, wmass.Mass);
        float vStar = contact ? v - dt * Vector3.Dot(g, axis) : v;
        float f = WheelOleo.Step(_rating, x, v, vStar, m, dt, ref _lastF);
        F = f;
        if (f > 0f)
        {
            PendJ = f * dt; PendAxis = axis; PendAt = mount; PendWheel = wheel; PendGrid = grid;
            Volatile.Write(ref Armed, 1);
        }
    }

    /// <summary>The player's spring: the terminal's stiffness (a gear's x its StiffnessScale), capped.</summary>
    float PlayerStrength()
    {
        float s = Math.Max(0f, _cm.SuspensionStiffness);
        if (_gear != null) s *= AeroGearComponent.StiffnessScale;
        return GearStiffness.Cap(s);
    }

    void Write(float s, float d)
    {
        if (_wc == null) return;
        if (_wc.SuspensionStrengthFactor != s) _wc.SuspensionStrengthFactor = s;
        if (_wc.SuspensionDampingFactor != d) _wc.SuspensionDampingFactor = d;
        WrittenS = s; WrittenD = d;
        if (!_owned) { _owned = true; if (_gear != null) _gear.ShockOwned = true; }
    }

    /// <summary>Not taking part (off, aero off, a gear stowing, no grid): the player's values back once, the airshock disarmed.</summary>
    void LetGo()
    {
        PendJ = 0f; _lastF = 0f; Grounded = false;
        Disarm();
        if (!_owned) return;
        _owned = false;
        if (_gear != null) { _gear.ShockOwned = false; return; }   // (the gear writes its own: stowing it saves and restores them)
        if (_wc != null) { _wc.SuspensionStrengthFactor = WrittenS = PlayerStrength(); _wc.SuspensionDampingFactor = WrittenD = Math.Max(0f, _cm.SuspensionDamping); }
    }

    void Disarm()
    {
        if (_shock.Armed) Interlocked.Decrement(ref ArmedNow);
        _shock.Reset();
    }

    /// <summary>The ship's effective mass at the mount along the axis (kg): a unit impulse there on a copy of its body.</summary>
    static float MassAt(in RigidBodyData rb, in RigidBodyMassProperties mass, in WorldTransform wt, Vector3D at, Vector3 axis)
    {
        var probe = rb;
        probe.ApplyImpulseAt(in mass, in wt, at, axis);
        float inv = Vector3.Dot(probe.GetLinearVelocityAtPoint(in mass, in wt, in at) - rb.GetLinearVelocityAtPoint(in mass, in wt, in at), axis);
        return inv > 1e-9f && float.IsFinite(inv) ? 1f / inv : mass.Mass;
    }

    /// <summary>The tyre's bottom over the ground (m): the planet's surface (its generated shape) and the grid's ground ray (voxels as
    /// they are, grids under it), the nearer; MaxValue when neither knows.</summary>
    float Height(AeroGridComponent aero, in WorldTransform gwt, Vector3D wpos, Vector3 up)
    {
        float r = _cm.WheelRadius, h = float.MaxValue;
        if (aero.GroundHeight >= 0f)
            h = aero.GroundHeight + Vector3.Dot((Vector3)(wpos - gwt.Position), up) - r;
        _height ??= new GamePlanetHeight();
        if (_height.Surface(wpos, 1, out var c, out double R))
        {
            float hp = (float)((wpos - c).Length() - R) - r;
            if (float.IsFinite(hp) && hp < h) h = hp;
        }
        return h;
    }

    // ── the engine ──
    static FieldInfo _constraintField, _suspensionField; static bool _fieldsLooked;

    /// <summary>The live wheel constraint and the suspension's frame (private fields, §3.9), and the wheel's rating (its radius, the
    /// constraint's travel, the wheel body's mass). Once per wheel; false: not reachable (logged once a session).</summary>
    bool Resolve(Entity wheel)
    {
        if (!_fieldsLooked)
        {
            _fieldsLooked = true;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            _constraintField = typeof(WheelCornerModuleComponent).GetField("_constraint", f);
            _suspensionField = typeof(WheelCornerModuleComponent).GetField("_suspension", f);
            if (_constraintField == null || _suspensionField == null || _suspensionField.FieldType != typeof(WheelCornerModuleComponent.SuspensionInfo))
            {
                LastFault = "WheelCornerModuleComponent._constraint / ._suspension not found: no wheel shocks";
                Log.Default?.Info("[AERO-SHOCK] " + LastFault);
            }
        }
        if (_constraintField == null || _suspensionField == null || _suspensionField.FieldType != typeof(WheelCornerModuleComponent.SuspensionInfo)) return false;
        if (!(_constraintField.GetValue(_cm) is IPhysicsConstraint c) || !c.TryGet<IWheelConstraint>(out var wc)) return false;
        _susp = (WheelCornerModuleComponent.SuspensionInfo)_suspensionField.GetValue(_cm);
        float stroke = _susp.LineOfTravel.Length;
        if (!(stroke > 0.01f)) stroke = wc.MaxSuspensionLimit;
        float wm = wheel.Data.TryGet<RigidBodyMassProperties>(out var wmp) ? wmp.Mass : 0f;
        _rating = WheelRating.Of(_cm.WheelRadius, stroke, wm, WheelShockSettings.OleoRating);
        if (!_rating.Valid) { Faults++; LastFault = "unrated wheel: " + _rating; return false; }
        _wc = wc; _ratedScale = WheelShockSettings.OleoRating;
        Interlocked.Increment(ref Resolved);
        if (Resolved <= 8) Log.Default?.Info($"[AERO-SHOCK] {_rating}; limits {wc.MinSuspensionLimit:F2}..{wc.MaxSuspensionLimit:F2}");
        return true;
    }
    float _ratedScale;

    /// <summary>A changed Wheels.OleoRating re-rates the wheel (the apply job, once a second).</summary>
    internal void Rerate()
    {
        if (_wc == null || _ratedScale == WheelShockSettings.OleoRating) return;
        _rating = WheelRating.Of(_cm.WheelRadius, _rating.Stroke, _rating.WheelMass, WheelShockSettings.OleoRating);
        _ratedScale = WheelShockSettings.OleoRating;
    }

    // ── the apply job (one a tick, single threaded: AeroGridComponent.WheelShockApplyJob) ──
    static int _tick;

    internal static void ApplyAll()
    {
        lock (All)
        {
            int n = All.Count;
            _tick++;
            bool rerate = _tick % 60 == 0;
            for (int i = 0; i < n; i++)
            {
                var s = All[i];
                if (rerate) s.Rerate();
                // the wheels on the ground of its grid (the next tick's shared mass)
                int on = 0;
                if (s.Grid != null) for (int k = 0; k < n; k++) if (All[k].Grounded && ReferenceEquals(All[k].Grid, s.Grid)) on++;
                s.GridGrounded = Math.Max(1, on);
                if (s.PendJ <= 0f) continue;
                float j = s.PendJ; s.PendJ = 0f;
                var grid = s.PendGrid; var wheel = s.PendWheel;
                if (grid == null || wheel == null || !GridPhysics.Alive(grid.Data) || !GridPhysics.Alive(wheel.Data)) continue;
                var gd = grid.Data; var wd = wheel.Data;
                ref RigidBodyData srb = ref gd.TryGetWritePtr<RigidBodyData>();
                if (Unsafe.IsNullRef(in srb) || !gd.TryGet<RigidBodyMassProperties>(out var smass)) continue;
                ref RigidBodyData wrb = ref wd.TryGetWritePtr<RigidBodyData>();
                if (Unsafe.IsNullRef(in wrb) || !wd.TryGet<RigidBodyMassProperties>(out var wmass)) continue;
                var gwt = gd.GetWorldTransform();
                Vector3 imp = s.PendAxis * j;
                srb.ApplyImpulseAt(in smass, in gwt, s.PendAt, imp);   // (the ship: pushed off the wheel at the mount)
                Vector3 back = -imp;
                wrb.ApplyLinearImpulse(in wmass, in back);             // (the wheel: the same, the other way, at its centre)
            }
            WheelShockDev.Step(All);
        }
    }

    /// <summary>Harness: every wheel's state (built when asked; read it again for a fresh one).</summary>
    public static string Summary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"mode {WheelShockSettings.Mode} wheels {Wheels} resolved {Resolved} armed {ArmedNow} arms {Arms} faults {Faults} {LastFault}");
            lock (All)
                foreach (var s in All)
                    if (s._wc != null)
                        sb.Append($" | {s._rating.Diameter:F1} m x {s.X:F3}/{s._rating.Stroke:F2} v {s.V:F2} F {s.F / Math.Max(1f, s._rating.RatedWeight):F2}W {(s.Contact ? "ground" : "air")}{(s._shock.Armed ? " ARMED" : "")} S {s.WrittenS:F2} D {s.WrittenD:F2}");
            return sb.ToString();
        }
    }
}
