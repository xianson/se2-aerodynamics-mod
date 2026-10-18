#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>The solid rocket blocks' table (SolidRocketBlocks.g.cs, generated with their content by gen_propulsor_defs.py): which
/// prefabs get AeroSolidSegmentComponent / AeroSolidNozzleComponent (EntityInjector), and what a block is. The nozzles are left
/// out of the mod's thruster set (OffsetThrustJob.RebuildThrusterCache: IsNozzle) - no controller ever uses them.</summary>
public static partial class SolidRocketBlocks
{
    static Dictionary<Guid, Row> _byBlock;
    static HashSet<Guid> _segServer, _nozServer, _nozClient;

    static void Build()
    {
        if (_byBlock != null) return;
        var b = new Dictionary<Guid, Row>(); var ss = new HashSet<Guid>(); var ns = new HashSet<Guid>(); var nc = new HashSet<Guid>();
        foreach (var r in Rows)
        {
            b[r.Block] = r;
            if (r.Nozzle) { ns.Add(r.Server); nc.Add(r.Client); } else ss.Add(r.Server);
        }
        _segServer = ss; _nozServer = ns; _nozClient = nc; _byBlock = b;
    }

    /// <summary>The segments' server prefabs (their tank - the game's ResourceContainerComponent, server-only - is read there).</summary>
    public static HashSet<Guid> SegmentServerPrefabs { get { Build(); return _segServer; } }
    /// <summary>The nozzles' server and client prefabs (the client copy only follows the game's switch and override).</summary>
    public static HashSet<Guid> NozzleServerPrefabs { get { Build(); return _nozServer; } }
    public static HashSet<Guid> NozzleClientPrefabs { get { Build(); return _nozClient; } }

    /// <summary>A block definition's row (null: not a solid rocket block).</summary>
    public static Row RowOf(Guid block) { Build(); return _byBlock.TryGetValue(block, out var r) ? r : null; }

    /// <summary>A block's row (null: not one).</summary>
    internal static Row RowOf(CubeBlockComponent cb)
    {
        var g = cb != null ? BlockComponentFactory.GetDefinitionGuid(cb) : null;
        return g.HasValue ? RowOf(g.Value) : null;
    }

    /// <summary>A solid rocket NOZZLE (out of the mod's thruster set).</summary>
    internal static bool IsNozzle(CubeBlockComponent cb) => RowOf(cb) is { Nozzle: true };
}
