#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;

namespace AeroMod;

/// <summary>The mortar blocks' table (MortarBlocks.g.cs, generated with their content by gen_propulsor_defs.py gen_mortar): which
/// prefabs get AeroMortarChargeComponent / AeroMortarComponent (EntityInjector), and what a block is.</summary>
public static partial class MortarBlocks
{
    static Dictionary<Guid, Row> _byBlock;
    static HashSet<Guid> _chargeServer, _breechServer;

    static void Build()
    {
        if (_byBlock != null) return;
        var b = new Dictionary<Guid, Row>(); var cs = new HashSet<Guid>(); var bs = new HashSet<Guid>();
        foreach (var r in Rows)
        {
            b[r.Block] = r;
            if (r.Kind == Kind.Charge) cs.Add(r.Server); else if (r.Kind == Kind.Breech) bs.Add(r.Server);
        }
        _chargeServer = cs; _breechServer = bs; _byBlock = b;
    }

    /// <summary>The charges' server prefabs (AeroMortarChargeComponent: the charge's state is the server's, saved with the block).</summary>
    public static HashSet<Guid> ChargeServerPrefabs { get { Build(); return _chargeServer; } }
    /// <summary>The breeches' server prefabs (AeroMortarComponent: it fires on the server).</summary>
    public static HashSet<Guid> BreechServerPrefabs { get { Build(); return _breechServer; } }

    /// <summary>A block definition's row (null: not a mortar block).</summary>
    public static Row RowOf(Guid block) { Build(); return _byBlock.TryGetValue(block, out var r) ? r : null; }

    /// <summary>A block's row (null: not one).</summary>
    internal static Row RowOf(CubeBlockComponent cb)
    {
        var g = cb != null ? BlockComponentFactory.GetDefinitionGuid(cb) : null;
        return g.HasValue ? RowOf(g.Value) : null;
    }
}
