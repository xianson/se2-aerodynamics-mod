#pragma warning disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.Definitions;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Library.Utils;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Constraints;
using Keen.VRage.Physics.Constraints.Data;

namespace AeroMod;

/// <summary>
/// A RETRACTABLE GEAR (server; on our gear blocks - the vanilla suspension, WheelCornerModuleComponent, with this added;
/// AeroGearVariants). The wheel is ALWAYS the vanilla wheel - built by the vanilla block as it loads, its tyre, suspension,
/// brakes, parking brake and steering untouched - and it retracts PHYSICALLY: up its own suspension axis into the well,
/// by moving the live wheel constraint's suspension limits (IWheelConstraint.Min/MaxSuspensionLimit, both together: the
/// wheel held at that height, a rate-limited slide). Down, the vanilla limits are put back exactly. Once it is deeper
/// than the variant's CollisionOffAt its collision goes (the "Ragdolls" entity layer: it meets no grid, voxel, character
/// or floating object - the physics session re-filters the body when the layer changes) and comes back before it leaves
/// the well on the way down. (2026-10-13: a wheel cannot be SPAWNED from a mod's job - the vanilla build hook too: its
/// body finds its mass data missing - so the wheel is never removed; the user chose this: "3, and turn collision off".)
/// The sequence (doors, the leg, the weight gate) is GearSequence; the client animates the strut and doors from the
/// progress and travel published here (AeroGearAnimComponent).
/// </summary>
public partial class AeroGearComponent : Component, IInSceneListener, IGearState
{
    public readonly GearSequence Seq = new(true);   // (a gear is built down: the vanilla block builds its wheel with it)
    public AeroGearVariant Variant;

    public static bool Enabled = true;
    /// <summary>Harness: 1 every gear down, 2 every gear up, 3 toggle (aeroset AeroGearComponent.DevCommand n, then 0).</summary>
    public static int DevCommand;
    /// <summary>Harness (2026-10-13, finding the limits' sense): how the held wheel's limits are written - 2 Min = travel,
    /// Max = travel (the right one, default); 1 -travel / travel; 3 -travel / -travel; 4 travel / -travel.</summary>
    public static int LimitMode = 2;
    int _devSeen;
    internal static readonly List<AeroGearComponent> All = new();
    public string Last = "";

    WheelCornerModuleComponent _cm; bool _server, _looked, _faulted;
    long _tick;
    Vector3I _key; bool _hasKey;
    // the live wheel constraint and what vanilla made it with (put back when down)
    Entity _wheelSeen; IWheelConstraint _wc; float _vanillaMin, _vanillaMax;
    /// <summary>How far up its axis the wheel is held now (m; 0: down, the vanilla limits).</summary>
    float _travel;
    bool _collisionOff; EntityLayerData _wheelLayer;
    // where the wheel was (block frame) at a known suspension distance - the strut is drawn where the wheel IS
    Vector3 _wheel0; float _s0; bool _wheelRef; float _measured = float.NaN;
    // the suspension's spring, off while the wheel is held up (its settings as they were when it left, put back down)
    bool _springOff; float _springStrength, _springDamping;
    string _step = "";

    public float Extension => Seq.Fold();
    public float DoorsOpen => Seq.Doors();
    public void Command(bool down) => Seq.Command(down);
    public void Toggle() => Seq.Toggle();

    void IInSceneListener.OnAddedToScene()
    {
        try { _server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { _server = true; }
        lock (All) All.Add(this);
    }
    void IInSceneListener.OnBeforeRemovedFromScene() { lock (All) All.Remove(this); }

    // (before the physics step - the constraint's limits are written here, never while the solver runs)
    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    private class OnGear : JobGroup;

    [OnGear]
    [MustHave(typeof(AeroGearComponent))]
    private static void GearJob(AeroGearComponent g)
    {
        if (!g._server || !Enabled || g._faulted) return;
        try { g.Tick(); }
        catch (Exception e)
        {
            // (once: a gear that throws stops - an exception every tick costs the frame dearly)
            g._faulted = true; g.Last = "fault in " + g._step + ": " + e.Message;
            Log.Default?.Info($"[AERO-GEAR] fault in {g._step} (gear stopped): {e.GetType().Name}: {e.Message} @ {string.Join(" < ", (e.StackTrace ?? "").Split((char)10).Take(6).Select(l => l.Trim()))}");
        }
    }

    void Tick()
    {
        _step = "setup";
        if (!_looked) { _looked = true; _cm = Entity?.TryGet<WheelCornerModuleComponent>(); Variant ??= AeroGearVariants.Of(Entity); }
        if (_cm == null) return;
        if (!_hasKey && Entity.TryGet<CubeBlockComponent>() is CubeBlockComponent cb) { var bb = cb.AABB; _key = bb.Min + bb.Max; _hasKey = true; }
        if (DevCommand != _devSeen) { _devSeen = DevCommand; if (DevCommand == 1) Command(true); else if (DevCommand == 2) Command(false); else if (DevCommand == 3) Toggle(); }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _tick == 0 ? 0f : (float)Math.Min(0.1, (now - _tick) / (double)System.Diagnostics.Stopwatch.Frequency);
        _tick = now;
        if (Variant != null) Seq.Duration = Variant.Duration;

        _step = "constraint";
        var wheel = _cm.WheelEntity;
        if (!ReferenceEquals(wheel, _wheelSeen)) { _wheelSeen = wheel; _wc = null; _collisionOff = false; _travel = 0f; _wheelRef = false; _springOff = false; }
        if (wheel != null && _wc == null) Resolve();

        // the sequence: the wheel always exists, so it never asks for one
        _step = "sequence";
        // (no weight gate - 2026-10-13, the user: "if I click retract then you retract", on the ground too)
        Seq.Step(dt, Seq.WheelWanted, () => true, () => false);

        // the wheel's height: up its axis as the leg is stowed, at most RetractSpeed (no jolt at a load that starts up)
        _step = "travel";
        float retract = Variant?.RetractTravel ?? 2.5f;
        float target = (1f - GearSequence.Fold(Seq.Progress)) * retract;
        float step = (Variant?.RetractSpeed ?? 2f) * dt;
        _travel = Math.Clamp(target, _travel - step, _travel + step);
        if (_wc != null)
        {
            if (_travel <= 1e-3f)
            {
                _travel = 0f;
                if (_wc.MinSuspensionLimit != _vanillaMin || _wc.MaxSuspensionLimit != _vanillaMax) { _wc.MinSuspensionLimit = _vanillaMin; _wc.MaxSuspensionLimit = _vanillaMax; }
                if (_springOff) { _wc.SuspensionStrengthFactor = _springStrength; _wc.SuspensionDampingFactor = _springDamping; _springOff = false; }
            }
            else
            {
                // the spring off first (2026-10-13: on a flying ship the wheel stopped about halfway up - the suspension's
                // spring, pulling it back to its rest the harder the further it is held, beat the limits; IWheelConstraint:
                // "zero means no soft suspension force ... the suspension only reacts at the hard limits")
                if (_shockOn) { _wc.SuspensionStrengthFactor = _shockS; _wc.SuspensionDampingFactor = _shockD; _shockOn = false; }
                if (!_springOff) { _springStrength = _wc.SuspensionStrengthFactor; _springDamping = _wc.SuspensionDampingFactor; _wc.SuspensionStrengthFactor = 0f; _springOff = true; }
                // (held at that height: Min = Max = +travel. Measured in game 2026-10-13 (LimitMode, all four writings,
                //  the 1 m gear, the game's own CurrentSuspensionDistance after a retract): travel / travel -> +2.15 (up, in
                //  the fork) on a static grid AND on a parked ship; -travel / +travel and -travel / -travel -> -2.15 (hanging
                //  below - and stuck ~1 m down even after the vanilla limits came back); travel / -travel -> +0.11)
                switch (LimitMode)
                {
                    case 1: _wc.MinSuspensionLimit = -_travel; _wc.MaxSuspensionLimit = _travel; break;
                    case 3: _wc.MinSuspensionLimit = -_travel; _wc.MaxSuspensionLimit = -_travel; break;
                    case 4: _wc.MinSuspensionLimit = _travel; _wc.MaxSuspensionLimit = -_travel; break;
                    default: _wc.MinSuspensionLimit = _travel; _wc.MaxSuspensionLimit = _travel; break;   // (2: the right one)
                }
            }
        }

        // collision: off deep in the well, back before the wheel leaves it
        _step = "collision";
        if (wheel != null)
        {
            float offAt = Variant?.CollisionOffAt ?? 0.6f * retract;
            if (!_collisionOff && _travel >= offAt) SetCollision(wheel, false);
            else if (_collisionOff && _travel < offAt) SetCollision(wheel, true);
        }

        // the stiffness: the terminal's slider x StiffnessScale (2026-10-16, the user: "increase the landing gear stiffness by
        // a lot ... not the defaults, just the max") - the vanilla slider tops out at 1; down and not held by the airshock or
        // the retraction, the constraint gets the scaled value
        _step = "stiffness";
        if (_wc != null && _travel <= 0f && !_springOff && !_shockOn && !ShockOwned)
        {
            float want = Math.Max(0f, _cm.SuspensionStiffness) * StiffnessScale;
            if (_wc.SuspensionStrengthFactor != want) _wc.SuspensionStrengthFactor = want;
        }

        _step = "publish";
        Publish();
        _step = "";
    }

    // ── the engine ──

    static FieldInfo _constraintField; static bool _fieldLooked;

    /// <summary>The live wheel constraint (WheelCornerModuleComponent._constraint, private - REFLECTION_AND_WHITELIST
    /// §3.9) and the limits vanilla gave it.</summary>
    void Resolve()
    {
        if (!_fieldLooked)
        {
            _fieldLooked = true;
            _constraintField = typeof(WheelCornerModuleComponent).GetField("_constraint", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_constraintField == null) Log.Default?.Info("[AERO-GEAR] WheelCornerModuleComponent._constraint not found: gears cannot retract");
        }
        if (_constraintField?.GetValue(_cm) is IPhysicsConstraint c && c.TryGet<IWheelConstraint>(out var wc))
        {
            _wc = wc; _vanillaMin = wc.MinSuspensionLimit; _vanillaMax = wc.MaxSuspensionLimit;
            Last = $"wheel constraint: limits {_vanillaMin:F2}..{_vanillaMax:F2} m";
            // (once per wheel: where it is in the block's frame - the model's dummies are checked against it)
            var w = _cm.WheelEntity;
            if (w != null)
            {
                var rel = WorldTransform.GetRelativeTransform(w.Data.GetWorldTransform(), Entity.Data.GetWorldTransform());
                _wheel0 = (Vector3)rel.Position; _s0 = _cm.CurrentSuspensionDistance; _wheelRef = true;
                Log.Default?.Info($"[AERO-GEAR] {_key} wheel in block frame (suspension at {_s0:F3} m): pos ({rel.Position.X:F3}, {rel.Position.Y:F3}, {rel.Position.Z:F3}) rot ({rel.Orientation.X:F3}, {rel.Orientation.Y:F3}, {rel.Orientation.Z:F3}, {rel.Orientation.W:F3}); limits {_vanillaMin:F2}..{_vanillaMax:F2}");
            }
        }
    }

    static EntityLayerData? _noCollision; static bool _layerLooked;

    /// <summary>Off: the wheel onto the "Ragdolls" layer (it meets nothing a stowed wheel could); on: its own layer back.</summary>
    void SetCollision(Entity wheel, bool on)
    {
        if (!_layerLooked)
        {
            _layerLooked = true;
            if (Keen.VRage.Library.Definitions.DefinitionManager.Instance.TryGetConfiguration<EntityLayerConfiguration>(out var cfg) && cfg != null)
                _noCollision = cfg.GetEntityLayerData(StringId.Get("Ragdolls"));
            if (_noCollision == null) Log.Default?.Info("[AERO-GEAR] no 'Ragdolls' entity layer: a stowed wheel keeps its collision");
        }
        if (_noCollision == null) return;
        if (!on)
        {
            if (!wheel.Data.TryGet<EntityLayerData>(out _wheelLayer)) return;
            wheel.Data.Set(_noCollision.Value); _collisionOff = true;
            Log.Default?.Info($"[AERO-GEAR] {_key} wheel stowed: collision off");
        }
        else
        {
            wheel.Data.Set(_wheelLayer); _collisionOff = false;
            Log.Default?.Info($"[AERO-GEAR] {_key} wheel leaving the well: collision on");
        }
    }

    // ── AIRSHOCK (2026-10-15, the user: SE1's suspension setting that anticipated ground impacts - "right now it's
    //    impossible to land"): the gear down and the grid coming down, the wheel's suspension goes stiff and fully damped
    //    just before it touches (within ArmSeconds of contact at the sink rate now, or ArmHeight above the ground) so the
    //    hit is taken over its whole travel without bottoming or bouncing; the player's damping / stiffness come back once it
    //    has settled on the wheel (in contact and barely sinking for SettleSeconds) or HoldSeconds after the touch. ──
    public bool AirshockEnabled = true;   // (saved with the block since 2026-10-15: AeroBlockSave)
    public static float ArmSeconds = 0.5f, ArmHeight = 0.3f, MinSink = 0.5f, SettleSink = 0.3f, SettleSeconds = 0.5f, HoldSeconds = 3f;
    public static float ShockDamping = 1f;
    /// <summary>The terminal's stiffness slider (0..1) x this is what the suspension gets: 1 vanilla, 3 a slider at the top is
    /// three times the vanilla maximum. The airshock goes to this (the slider's top) before a touchdown.</summary>
    public static float StiffnessScale = 3f;
    static float ShockStrength => StiffnessScale;
    bool _shockOn; float _shockS, _shockD; double _shockTouch = -1, _shockCalm = -1;
    public string ShockState = "";

    /// <summary>The wheel carries the ship: in contact (the game's own) or its suspension compressed.</summary>
    bool WeightOnWheel()
        => _travel <= 1e-3f && (Data.Has<WheelOnGroundCollectorComponent.ProxyWheelContactData>() || _cm.CurrentSuspensionDistance > 0.02f);

    // ── to the clients ──

    void Publish()
    {
        if (!_hasKey) return;
        var grid = Entity.GetTopLevelParent();
        var aero = grid?.TryGet<AeroGridComponent>();
        if (aero == null) return;
        // the strut's slide: retracting / up - the commanded travel (the limits hold the wheel there); down - where the wheel
        // IS on its spring, the game's own reading (2026-10-13, the user: "the arm should go where the wheel is"). (Not the
        // wheel's world position less the grid's: in flight the two are a physics step apart and at speed that is tenths of a
        // metre along the slide - the stowed leg stopped short of flat. Kept as a diagnostic: _measured, in Summary.)
        float compression = _travel > 0f ? _travel : Math.Max(0f, _cm.CurrentSuspensionDistance);
        var w = _cm.WheelEntity; var layout = Variant?.Layout;
        if (_wheelRef && w != null && layout != null && layout.RakedSlide)
        {
            var rel = WorldTransform.GetRelativeTransform(w.Data.GetWorldTransform(), Entity.Data.GetWorldTransform());
            _measured = _s0 + Vector3.Dot((Vector3)rel.Position - _wheel0, Vector3.Normalize(layout.RakeAxis));
        }
        ThrustTorque.PublishGear(aero, grid.Data.GetWorldTransform(), _key, Seq.Progress, compression);
    }

    /// <summary>Harness: every gear's state - as the server step built it after the previous read (ServerDevReads,
    /// 2026-10-15: read from the harness's thread it read the server blocks).</summary>
    public static string Summary => ServerDevReads.Summary(ServerDevReads.Gears);
    internal static string BuildSummary()
    {
        {
            var sb = new System.Text.StringBuilder();
            lock (All)
            {
                sb.Append(All.Count).Append(" gears");
                foreach (var g in All)
                    sb.Append(" | ").Append(g.Seq.Down ? "down" : "up").Append($" p {g.Seq.Progress:F2} travel {g._travel:F2} m (wheel: measured {g._measured:F2}, game {(g._cm != null ? g._cm.CurrentSuspensionDistance : float.NaN):F2})")
                      .Append(g._wc != null ? $" limits {g._wc.MinSuspensionLimit:F2}..{g._wc.MaxSuspensionLimit:F2}" : " no constraint")
                      .Append(g._cm?.WheelEntity != null ? " wheel" : " NO WHEEL").Append(g._collisionOff ? " collision off" : "")
                      .Append(g._shockOn ? " AIRSHOCK on" : "").Append(g.ShockState.Length > 0 ? " (shock " + g.ShockState + ")" : "").Append(g.Seq.Held.Length > 0 ? " held: " + g.Seq.Held : "").Append(g.Last.Length > 0 ? " (" + g.Last + ")" : "");
            }
            return sb.ToString();
        }
    }
}
