#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Physicss;
using Keen.Game2.Simulation.GameSystems;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// A MORTAR CHARGE (2026-10-23; the law: Mortar.cs). Injected into the charges' SERVER prefabs only (MortarBlocks.ChargeServerPrefabs).
/// A plain block (no conveyor, no tank: gen_mortar) whose one charge is this component's: FULL when built, SPENT once its breech fires
/// (an empty block from then on - rebuild it to reload; the user: "empty blocks if they are spent"), saved with the block
/// ("aero-mortarcharge:1 1" when spent; a full one writes nothing).
///   its MASS: an IDynamicMassProvider - the charge's propellant (kg) on the block's dry mass until it is spent; the grid's mass
///           computer is told when that changes (GridMassComputerComponent.OnBlockMassChanged, as AeroSolidSegmentComponent).
///   its PLACE (cell box, Up axis, grid) cached every GeoTicks for the breeches' stack search (SolidRocket.FindStack: a charge is a
///           stack piece of the solid rockets' kind - same size, same footprint, touching, either way up); Feeds: working.
/// </summary>
public partial class AeroMortarChargeComponent : Component, IInSceneListener, IDynamicMassProvider, SolidRocket.IPiece
{
    /// <summary>Every charge in the server scene.</summary>
    internal static readonly List<AeroMortarChargeComponent> All = new();
    public const int GeoTicks = 30;

    public MortarBlocks.Row Row;
    /// <summary>Fired (its breech went off with it in the stack): an empty block.</summary>
    public bool Spent { get; private set; }
    bool _massDirty = true; LookRetry _look;
    CubeBlockComponent _cb; PowerableBlockComponent _pb;
    SolidRocket.Geo _geo; int _geoIn;
    /// <summary>The grid the block is in, as last cached.</summary>
    internal Entity GridEntity;

    public SolidRocket.Geo Geo => _geo;
    /// <summary>A full charge (kg: the table's).</summary>
    public float Full => Row?.Propellant ?? 0f;
    /// <summary>Its charge (kg): full until spent.</summary>
    public float Propellant => Spent ? 0f : Full;
    /// <summary>It counts in a stack: working (a damaged charge breaks the stack there, as a damaged segment does).</summary>
    public bool Feeds => _pb == null || _pb.Functional;
    public float DynamicMass => Propellant;

    /// <summary>Fired with its breech: spent for good (its mass told to the grid on its next step).</summary>
    internal void Spend() { if (Spent) return; Spent = true; _massDirty = true; }
    /// <summary>Its breech's seal would not break after all: full again (marked spent first - Fire).</summary>
    internal void Unspend() { if (!Spent) return; Spent = false; _massDirty = true; }

    void IInSceneListener.OnAddedToScene() { lock (All) All.Add(this); _look.Reset(); _geoIn = 0; _massDirty = true; }
    void IInSceneListener.OnBeforeRemovedFromScene() { lock (All) All.Remove(this); }

    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    internal class OnMortarCharge : JobGroup;

    [OnMortarCharge]
    [MustHave(typeof(AeroMortarChargeComponent))]
    private static void ChargeJob(AeroMortarChargeComponent c) => c.Tick();

    void Tick()
    {
        AeroMortarComponent.StepShots();   // (a shot in flight moves on even when its breech was rebuilt in the new grid)
        if (Entity == null || !GridPhysics.Alive(Data)) return;
        if (!_look.Done)
        {
            if (!_look.Due()) return;
            _cb = Entity.TryGet<CubeBlockComponent>(); _pb = Entity.TryGet<PowerableBlockComponent>();
            Row = MortarBlocks.RowOf(_cb);
            _look.Done = Row != null && _cb != null;
            if (!_look.Done) return;
        }
        if (--_geoIn <= 0)
        {
            _geoIn = GeoTicks;
            var bb = _cb.AABB;
            var up = Base6Directions.GetVector(_cb.BlockOrientation.TransformDirection(Base6Directions.Direction.Up));
            _geo = SolidRocket.Geo.Of(bb.Min, bb.Max, up, Row.Size);
            GridEntity = _cb.Grid?.Entity;
        }
        if (_massDirty)
        {
            var massComputer = _cb.Grid?.Entity?.TryGet<GridMassComputerComponent>();
            if (massComputer != null) { lock (massComputer) massComputer.OnBlockMassChanged(_cb); _massDirty = false; }
        }
    }

    public const string SaveKey = "aero-mortarcharge:";

    /// <summary>Spent when saved: spent again.</summary>
    [Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, SaveKey);
        if (key == null) return;
        var w = key.Substring(SaveKey.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length >= 2 && w[1] == "1") Spent = true;
    }

    [Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
        => ob.NamedEntities = AeroBlockSave.One(Spent ? SaveKey + "1 1" : null, Entity);
}
