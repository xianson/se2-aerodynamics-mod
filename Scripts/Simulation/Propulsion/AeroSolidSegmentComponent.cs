#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// A SOLID ROCKET SEGMENT (2026-10-17; the law: SolidRocket.cs). Injected into the segments' SERVER prefabs only
/// (SolidRocketBlocks.SegmentServerPrefabs). THE CONVEYOR VERSION (2026-10-17): the segment's propellant is the GAME's - a tank of
/// "Solid Propellant" (its ResourceContainerComponent, gen_propulsor_defs.py gen_solid: full when built, never refilled, drained in
/// creative too, saved by the game) on an isolated conveyor chain (ports at its size's own scale on its top and bottom faces); the
/// nozzle draws it like a hydrogen thruster draws hydrogen. This component only READS the tank (its charge: Propellant) - the game
/// moves the propellant - and:
///   its MASS: the game does not weigh stored resources, so it is an IDynamicMassProvider (the game's GridMassComputerComponent adds
///           DynamicMass to the block's definition mass - its dry mass) of the tank's charge (1 L = 1 kg), and tells the grid's mass
///           computer when it has moved by MassStep of a full tank (GridMassComputerComponent.OnBlockMassChanged - public, what the
///           game's own CubeBlockComponent does on a dynamic mass signal). (EntityInjector leaves these tanks out of
///           AeroFuelMassComponent: one mass provider.)
///   no BACKDOORS: a tank switched to Charge would starve the nozzle (a shutdown the user ruled out: "once a solid is on it's on"),
///           a segment switched off drops its tank out of the group - the game's terminal shows both (its tank panel and on / off
///           are generic, no definition hides them), so they are PUT BACK: Mode Auto, switched on (written only when they differ).
///   its PLACE (cell box, Up axis, grid) cached every GeoTicks for the nozzles' stack search: a change bumps Version (so a nozzle
///           searches again only when a solid block came, went, moved or turned); and whether it feeds the chain (working: Feeds).
/// </summary>
public partial class AeroSolidSegmentComponent : Component, IInSceneListener, IDynamicMassProvider, SolidRocket.IPiece
{
    /// <summary>Every segment in the server scene (server prefabs only).</summary>
    internal static readonly List<AeroSolidSegmentComponent> All = new();
    /// <summary>Bumped whenever a solid block (segment or nozzle) is added, removed, moved to another grid or turned.</summary>
    internal static int Version;
    internal static void Bump() => System.Threading.Interlocked.Increment(ref Version);

    /// <summary>The share of a full segment the propellant moves by before the grid's mass is recomputed.</summary>
    public const float MassStep = 0.025f;
    public const int GeoTicks = 30;

    public SolidRocketBlocks.Row Row;
    /// <summary>A full segment's propellant (kg; the table's - the tank's MaxCapacity in litres).</summary>
    public float Full { get; private set; }
    /// <summary>On a NOZZLE (2026-10-23): its own grain - its tank of SolidRocketBlocks.NozzleGrain of a segment; the nozzle puts it
    /// first in its stack (AeroSolidNozzleComponent.Rebuild) and FindStack never takes it as a segment.</summary>
    public bool OnNozzle { get; private set; }
    /// <summary>The tank's charge (kg = L) as its own step last read it; -1 until then.</summary>
    float _charge = -1f;
    bool _known, _feeds; LookRetry _look;
    CubeBlockComponent _cb; PowerableBlockComponent _pb; ResourceContainerComponent _rc;
    SolidRocket.Geo _geo;
    /// <summary>The grid the block is in (its CubeBlockComponent.Grid's entity), as last cached.</summary>
    internal Entity GridEntity;
    int _geoIn;
    float _massReported = -1f;
    /// <summary>The put-backs since load (the harness readout): tank mode, switch.</summary>
    public int ModeRestores, SwitchRestores;

    public SolidRocket.Geo Geo => _geo;

    /// <summary>Propellant left (kg): the tank's charge (full until its first read).</summary>
    public float Propellant { get { EnsureRow(); return _charge >= 0f ? _charge : OnNozzle && _rc == null ? 0f : Full; } }   // (a nozzle with no tank - content older than its scripts: no grain, never a phantom one)

    /// <summary>It feeds the chain: working, with its tank (the game drops a non-working block's tank and conveyor link).</summary>
    public bool Feeds => _feeds;

    /// <summary>Its share of a full segment (0..1).</summary>
    public float Fraction => Full > 0f ? Math.Clamp(Propellant / Full, 0f, 1f) : 0f;

    /// <summary>IDynamicMassProvider: the propellant's mass (kg) on top of the block's dry mass (the tank's charge as last read).</summary>
    public float DynamicMass => Propellant;

    /// <summary>Its row and tank (the game may ask its mass before its first step) - tried again every LookRetry.RetryTicks
    /// while they cannot be read (2026-10-17: once, for good - a segment missed then was never in a motor).</summary>
    void EnsureRow()
    {
        if (_look.Done) return;
        var e = Entity;
        if (e == null || !_look.Due()) return;
        _cb = e.TryGet<CubeBlockComponent>(); _pb = e.TryGet<PowerableBlockComponent>(); _rc = e.TryGet<ResourceContainerComponent>();
        Row = SolidRocketBlocks.RowOf(_cb);
        _known = Row != null;
        _look.Done = _known && _cb != null;
        if (_known) { OnNozzle = Row.Nozzle; Full = SolidRocket.FullSegment(Row.Thrust) * (Row.Nozzle ? SolidRocketBlocks.NozzleGrain : 1f); }
    }

    void IInSceneListener.OnAddedToScene()
    {
        lock (All) All.Add(this);
        Bump();
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        lock (All) All.Remove(this);
        _feeds = false;
        Bump();
    }

    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    internal class OnSolidSegment : JobGroup;

    [OnSolidSegment]
    [MustHave(typeof(AeroSolidSegmentComponent))]
    private static void SegmentJob(AeroSolidSegmentComponent s) => s.Tick();

    void Tick()
    {
        if (Entity == null || !GridPhysics.Alive(Data)) return;
        EnsureRow();
        if (!_known || _cb == null) return;
        if (--_geoIn <= 0)
        {
            _geoIn = GeoTicks;
            var bb = _cb.AABB;
            var up = Base6Directions.GetVector(_cb.BlockOrientation.TransformDirection(Base6Directions.Direction.Up));
            var g = SolidRocket.Geo.Of(bb.Min, bb.Max, up, Row.Size);
            var grid = _cb.Grid?.Entity;
            if (!g.Same(in _geo) || !ReferenceEquals(grid, GridEntity)) { _geo = g; GridEntity = grid; Bump(); }
        }
        // the tank (the game's): its charge; no backdoor shutdown - Auto, switched on
        bool feeds = _rc != null && (_pb == null || _pb.Functional);
        if (feeds != _feeds) { _feeds = feeds; Bump(); }   // (a segment broke or was repaired: the stacks are searched again)
        if (_rc != null)
        {
            _charge = MathF.Max(0f, (float)(double)_rc.CurrentChargeValue);
            if (_rc.Mode != ContainerMode.Auto) { _rc.Mode = ContainerMode.Auto; ModeRestores++; }
        }
        if (_pb != null && !_pb.Enabled && !OnNozzle) { _pb.Enabled = true; SwitchRestores++; }   // (a nozzle's switch is its ignition: the nozzle's)
        // the mass: told to the grid's mass computer when it moved by MassStep of a full segment (and when it reaches 0)
        float p = Propellant;
        if (_massReported < 0f || MathF.Abs(p - _massReported) >= MassStep * Full || (p == 0f && _massReported > 0f))
        {
            var massComputer = _cb.Grid?.Entity?.TryGet<GridMassComputerComponent>();
            if (massComputer != null)
            {
                lock (massComputer) massComputer.OnBlockMassChanged(_cb);
                _massReported = p;
            }
        }
    }

    /// <summary>In a nozzle's motor now (a segment that is not is inert).</summary>
    public bool InStack => AeroSolidNozzleComponent.StackHolding(this) != null;
}
