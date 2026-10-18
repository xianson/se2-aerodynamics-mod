#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Queries;

namespace AeroMod;

/// <summary>
/// ROTOR STRIKES on the SERVER (2026-10-17, the user's design; the laws: RotorStrike.cs, game-free): injected into the SERVER prefabs of
/// the striking propulsors (helicopter rotors, the Reversible Rotor, turboprops - PropulsorBlocks.StrikeServerPrefabs) with the blades'
/// saved health (AeroBlockSave: "aero-blades:1 health"). Its own job (OnStrikeProbe: server, every tick, after the grids' systems and
/// BEFORE the physics step - Keen.Game2.Simulation.Physics; the world is not stepping, as for the gear's ground ray; the propulsor's
/// own job runs before Simulation, which the physics step overlaps - no ray there):
///  - REPAIR: worn blades on a block welded back to full integrity are whole again (RotorStrike.Repaired);
///  - PUBLISH: blades that are not whole are published for the client copy (ThrustTorque.PublishBlades - the broken rotor drawn as a
///    stub with no blades and no blur: AeroRotorAnimComponent), and for a moment after a repair;
///  - PROBES: while it turns (RotorStrike.MinRpm) and nothing of its is waiting, RotorStrike.RaysPerTick synchronous rays a tick
///    (IPhysics.CastRay, as the gear's ground probe: in its job, before the physics step, into its own reused native hit list - no
///    allocation) over its disc: tip-circle chords round-robin and one spoke. Every body is reported (CollisionPreset.Bodies) and the
///    closest that is not the rotor itself is taken: its own block (a hit on its grid inside its own cells) and its own subparts'
///    bodies are skipped; the rest of its ship, other grids, terrain and characters count. A hit fast enough (RotorStrike.IsStrike)
///    is QUEUED (RotorStrikeServer) and the probes rest until it is handled and a blade pass has gone by (RotorStrike.GapTicks).
/// The consequences - damage, impulses, effects - are RotorStrikeServer's, once a tick in one job.
/// The thrust and rpm of a broken rotor: AeroPropulsorComponent (no thrust written, rpm 0).
/// </summary>
public partial class AeroRotorStrikeComponent : Component, IInSceneListener
{
    /// <summary>The blades' health (0..1; 0 broken). Saved (AeroBlockSave).</summary>
    public float Health = 1f;
    public bool Broken => RotorStrike.IsBroken(Health);
    /// <summary>A strike of this rotor waits in the queue (RotorStrikeServer); the probes rest until it is handled.</summary>
    internal volatile bool Pending;
    /// <summary>Ticks the probes rest after a strike (a blade pass: RotorStrike.GapTicks).</summary>
    internal int Gap;
    /// <summary>This rotor's counts (the harness's Summary): strikes, repairs, rays cast, probe ticks and their time (Stopwatch ticks).</summary>
    public int Strikes, Repairs; public long Rays, ProbeTicks, ProbeTime;
    /// <summary>The last strike, for the readout.</summary>
    public string Last = "-";

    internal static readonly List<AeroRotorStrikeComponent> All = new();   // (SERVER prefabs only: StrikeServerPrefabs)
    CubeBlockComponent _block; Vector3I _key; Vector3 _centre; bool _init;
    int _chord, _spoke, _selfAt, _pubAt, _pubHold;
    readonly Vector2[] _from = new Vector2[RotorStrike.RaysPerTick], _to = new Vector2[RotorStrike.RaysPerTick];
    // (the rays' hit list: native, grown once and reused - only this rotor's job touches it; released when it leaves the scene)
    Keen.VRage.Library.Memory.Buffer<SweepQueryHit> _hits;
    readonly List<DEntity> _self = new();
    /// <summary>Ticks a repaired rotor's whole blades stay published (the client copy sees the repair).</summary>
    const int PublishHoldTicks = 180;

    [After(typeof(Keen.Game2.Simulation.GridUpdateOrder.UpdateSystems))]
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    internal class OnStrikeProbe : JobGroup;

    [OnStrikeProbe]
    [MustHave(typeof(AeroRotorStrikeComponent))]
    private static void StrikeProbeJob(AeroRotorStrikeComponent c)
    {
        if (c.Entity == null || !PhysicsHack.Alive(c.Data)) return;
        var p = c._prop ??= c.Entity.TryGet<AeroPropulsorComponent>();
        if (p != null && p.RotorRadius > 0f) c.ServerTick(p);   // (its radius: the propulsor has read its row - PropulsorBlocks.Describe)
    }
    AeroPropulsorComponent _prop;

    void IInSceneListener.OnAddedToScene() { lock (All) All.Add(this); }
    void IInSceneListener.OnBeforeRemovedFromScene() { lock (All) All.Remove(this); _hits.Dispose(); _self.Clear(); _init = false; }

    /// <summary>(server, its own job, every tick) repair, publish, probe.</summary>
    internal void ServerTick(AeroPropulsorComponent p)
    {
        if (!_init)
        {
            _block = Entity?.TryGet<CubeBlockComponent>();
            if (_block == null) return;
            var bb = _block.AABB; _key = bb.Min + bb.Max; _centre = CellCoords.BlockCentre(bb.Min, bb.Max);
            _init = true;
        }
        if (Health < 1f)
        {
            float h = RotorStrike.Repaired(Health, _block.HealthIntegrity);
            if (h != Health)
            {
                Health = h; Repairs++; _pubHold = PublishHoldTicks; _pubAt = 0;
                Log.Default?.Info($"[AERO-STRIKE] {p.Kind} {p.Size} welded back to full integrity: blades repaired");
            }
        }
        if (Health < 1f || _pubHold > 0) PublishBlades();
        if (Broken || !RotorStrike.Enabled || Pending) return;
        if (Gap > 0) { Gap--; return; }
        if (!(p.Rpm >= RotorStrike.MinRpm) || !(p.RotorRadius > 0f)) return;
        Probe(p);
    }

    /// <summary>The blades' health for the client copy: every 15 ticks while not whole (the record stays fresh), at once on a change.</summary>
    void PublishBlades()
    {
        if (_pubHold > 0) _pubHold--;
        if (--_pubAt > 0) return;
        _pubAt = 15;
        var top = Entity.GetTopLevelParent();
        var aero = top?.TryGet<AeroGridComponent>();
        if (aero == null || !PhysicsHack.Alive(top.Data)) return;
        ThrustTorque.PublishBlades(aero, top.Data.GetWorldTransform(), _key, Health);
    }
    /// <summary>A change of the blades' health (RotorStrikeServer): published on this rotor's next tick.</summary>
    internal void Changed() { _pubAt = 0; }

    /// <summary>This tick's rays over the disc; the closest hit not on the rotor itself, fast enough, queued.</summary>
    void Probe(AeroPropulsorComponent p)
    {
        var phys = PhysicsHack.Physics;
        var grid = _block.Grid?.Entity;
        if (phys == null || grid == null || !PhysicsHack.Alive(grid.Data)) return;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        if (--_selfAt <= 0) { _selfAt = 60; CollectSelf(); }
        var gwt = grid.Data.GetWorldTransform();
        var q = Data.GetWorldTransform().Orientation;
        var kind = p.Kind;
        RotorStrike.DiscBasis(kind, out var u0, out var w0);
        Vector3 u = Vector3.Transform(u0, q), w = Vector3.Transform(w0, q), spin = Vector3.Transform(RotorStrike.SpinAxis(kind), q);
        Vector3D hub = WorldTransform.Transform((Vector3D)_centre, in gwt) + (Vector3D)(Vector3.Transform(RotorStrike.HubAxis(kind), q) * p.HubOffset);
        float R = p.RotorRadius;
        RotorStrike.TickRays(ref _chord, ref _spoke, R, _from, _to);
        for (int k = 0; k < RotorStrike.RaysPerTick; k++)
        {
            Vector3D a = hub + (Vector3D)(u * _from[k].X + w * _from[k].Y);
            Vector3 ray = u * (_to[k].X - _from[k].X) + w * (_to[k].Y - _from[k].Y);
            _hits.Clear();   // (CastRay appends)
            var args = new RayCastArgs(in a, ray);
            phys.CastRay(_hits, in args, CollisionPreset.Bodies);
            Rays++;
            int best = -1; double bestF = double.MaxValue;
            for (int i = 0; i < _hits.Count; i++)
            {
                var h = _hits[i];
                if (h.Fraction >= bestF || IsSelf(in h, grid, in gwt)) continue;
                best = i; bestF = h.Fraction;
            }
            if (best < 0) continue;
            var hit = _hits[best];
            Vector3 d = (Vector3)(hit.Position - hub);
            float r = RotorStrike.RadiusOf(d, spin), v = RotorStrike.SpeedAt(p.Rpm, r, R);
            if (!RotorStrike.IsStrike(v)) continue;
            var inPlane = d - spin * Vector3.Dot(d, spin);
            // (queued: the probes rest until the strike job has handled it - RotorStrikeServer.Step clears Pending, sets Gap)
            if (RotorStrikeServer.Queue(new RotorStrikeServer.Strike
            {
                Rotor = this, Prop = p, Grid = grid, Body = hit.Body, Pos = hit.Position, Normal = hit.Normal,
                Motion = RotorStrike.BladeMotion(spin, inPlane), Spin = spin, Radius = r, TipRadius = R, Rpm = p.Rpm, Speed = v,
            })) Pending = true;
            break;
        }
        ProbeTicks++; ProbeTime += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>The rotor itself: a body among its own children (its subparts), or its own grid at a cell of its own block (one cell
    /// of slack - the hit is on the surface).</summary>
    bool IsSelf(in SweepQueryHit h, Entity grid, in WorldTransform gwt)
    {
        for (int i = 0; i < _self.Count; i++) if (h.Body == _self[i]) return true;
        if (h.Body != grid.DEntity) return false;
        var local = (Vector3)WorldTransform.TransformInv(h.Position, gwt) - WorldTransform.TransformDirectionInv(h.Normal, gwt) * 0.06f;
        var c = AeroGridComponent.CellOf(local);
        var bb = _block.AABB;
        return RotorStrike.IsOwnCell(c, bb.Min, bb.Max);
    }

    /// <summary>The block's own entity and its subparts (two levels of children) - their bodies are never struck.</summary>
    void CollectSelf()
    {
        _self.Clear();
        _self.Add(Entity.DEntity);
        var kids = Entity.TryGet<HierarchyComponent>()?.Children;
        if (kids == null) return;
        foreach (var k in kids)
        {
            if (k == null) continue;
            _self.Add(k.DEntity);
            var kk = k.TryGet<HierarchyComponent>()?.Children;
            if (kk != null) foreach (var g in kk) if (g != null) _self.Add(g.DEntity);
        }
    }

    /// <summary>Harness (aeroget AeroRotorStrikeComponent.Summary): every striking rotor - its blades, strikes, repairs, rays and the
    /// probe's cost (mean us per probing tick); then the queue's totals (RotorStrikeServer.Status).</summary>
    public static string Summary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            lock (All)
            {
                sb.Append(All.Count).Append(" striking rotors");
                foreach (var c in All)
                {
                    double us = c.ProbeTicks > 0 ? c.ProbeTime * 1e6 / System.Diagnostics.Stopwatch.Frequency / c.ProbeTicks : 0.0;
                    sb.Append($" | blades {c.Health:F2}{(c.Broken ? " BROKEN" : "")} strikes {c.Strikes} repairs {c.Repairs} rays {c.Rays} probe {us:F1} us/tick last {c.Last}");
                }
            }
            sb.Append(" || ").Append(RotorStrikeServer.Status);
            return sb.ToString();
        }
    }
}
