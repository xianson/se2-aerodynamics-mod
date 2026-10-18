#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// A REVERSIBLE THRUSTER (ReversibleThrust: the law) - an entity component any thruster block can carry (injected by its
/// prefab GUID: ReversibleThrustComponent.Prefabs - the propulsor table's reversible rows, gen_propulsor_defs.py REVERSIBLE,
/// plus Extra for any other block). It owns its GAME SIDE (+1 its definition's direction, -1 the opposite): every step the
/// server decides it from the ship's controls along its axis (ReversibleThrust.Demand / Step: the pilot's input,
/// ControlData.Movement; the dampeners, DampeningData; the grid's velocity, gravity and mass) and the client copy takes the
/// server's (ThrustTorque.ReversibleState); either writes it as the block's ThrustData.Direction - the game books its maximum
/// there (GridMovementCollectorComponent.OnThrusterChanged, server) and ThrustTorque reads it (LiveMax). And its PUSH SIDE:
/// the sign of the attitude law's force on it (ThrustTorque sets it, server; the client copy takes the server's) - what a
/// block draws (a rotor's blade pitch). Its natural direction is the one the game set (ThrusterComponent.UpdateThrustData,
/// from the block's turn; never written again by the game, not saved): read before the first turn.
/// The attitude law knows a thruster is reversible by this component being on it (OffsetThrustJob's thruster list:
/// ThrusterInfo.Rev, per list).
/// </summary>
public partial class ReversibleThrustComponent : Component, IInSceneListener
{
    /// <summary>Any other block's prefab GUIDs (server and client) to carry it, besides the propulsor table's reversible rows.</summary>
    public static readonly System.Collections.Generic.HashSet<Guid> Extra = new();
    static System.Collections.Generic.HashSet<Guid> _prefabs;
    /// <summary>The prefabs it is injected into (EntityInjector).</summary>
    public static System.Collections.Generic.HashSet<Guid> Prefabs
    {
        get
        {
            if (_prefabs != null) return _prefabs;
            var s = new System.Collections.Generic.HashSet<Guid>(Extra);
            foreach (var r in PropulsorBlocks.Rows) if (r.Reversible) { s.Add(r.Server); s.Add(r.Client); }
            return _prefabs = s;
        }
    }

    /// <summary>Its game side and hysteresis (server: decided; client: the server's), the force its axis was asked for last
    /// step (N, server), its push's side (the law's force's sign; server: ThrustTorque, client: the server's).</summary>
    public ReversibleThrust.State State; public float Demand; public int PushSide = 1;
    /// <summary>Its game side now (+1 / -1).</summary>
    public int Side => State.Sign < 0 ? -1 : 1;
    Base6Directions.Direction _natural; bool _hasNatural, _server;
    Vector3I _key; ThrustTorque.Published _match; long _recheck;

    void IInSceneListener.OnAddedToScene()
    {
        try { _server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { _server = true; }
        State.Sign = 1;
    }
    void IInSceneListener.OnBeforeRemovedFromScene() { }

    // (after the propulsors' maximum writes - both write the block's ThrustData; in turn, never over each other)
    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    [After(typeof(AeroPropulsorComponent.OnPropulsor))]
    [After(typeof(AeroThrustSettingsComponent.OnJetCurve))]   // (and the vanilla atmospherics' Mach curve: a reversible vanilla one)
    private class OnReversible : JobGroup;

    [OnReversible]
    [MustHave(typeof(ReversibleThrustComponent))]
    private static void ReversibleJob(ReversibleThrustComponent c) => c.Step();

    void Step()
    {
        if (Entity == null || !GridPhysics.Alive(Data) || !Data.TryGet<ThrustData>(out var td)) return;
        if (!_hasNatural)
        {
            var cb = Entity.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>();
            if (cb == null) return;
            _natural = td.Direction; _hasNatural = true;
            var bb = cb.AABB; _key = bb.Min + bb.Max;
        }
        var grid = Entity.GetTopLevelParent();
        if (grid == null) return;
        if (_server)
        {
            var gd = grid.Data;
            Vector3 axis = Base6Directions.GetVector(_natural);
            float input = gd.TryGet<ControlData>(out var cd) ? Vector3.Dot(cd.Movement, axis) : 0f;
            bool damp = gd.Has<DampeningData>();
            var gwt = gd.GetWorldTransform();
            Vector3 v = gd.TryGet<Keen.VRage.Physics.Data.RigidBodyData>(out var rb) ? WorldTransform.TransformDirectionInv(rb.LinearVelocity, gwt) : Vector3.Zero;
            // (honest dampeners: the hold is against the gravity the body gets, x the engine's multiplier - HonestDampeners)
            Vector3 g = WorldTransform.TransformDirectionInv(GridPhysics.GetGravityDirection(gd), gwt) * (HonestHold.Enabled && AeroSwitch.Enabled ? GridPhysics.GravityScale : 1f);
            float mass = gd.TryGet<Keen.VRage.Physics.Data.RigidBodyMassProperties>(out var mp) ? mp.Mass : 0f;
            float max = MathF.Max(td.MaxThrustPower, 0f);
            Demand = ReversibleThrust.Demand(input, damp, Vector3.Dot(v, axis), Vector3.Dot(g, axis), mass, max);
            ReversibleThrust.Step(ref State, Demand, max, MathF.Abs(input) > ReversibleThrust.InputDeadband, AeroPhysics.Dt);
        }
        else if (ThrustTorque.ReversibleState(grid, _key, ref _match, ref _recheck, System.Diagnostics.Stopwatch.GetTimestamp(), out int gameSide, out int pushSide))
        {
            State.Sign = gameSide; PushSide = pushSide;
        }
        var want = ReversibleThrust.Direction(_natural, State.Sign);
        if (td.Direction != want) { td.Direction = want; Data.Set(td); }
    }
}
