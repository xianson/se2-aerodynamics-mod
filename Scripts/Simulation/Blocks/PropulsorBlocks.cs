#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>The propulsor blocks' table (PropulsorBlocks.g.cs, generated with their content by gen_propulsor_defs.py): which
/// prefabs get AeroPropulsorComponent, and what a block is (kind, size, rotor radius, hub offset).</summary>
public static partial class PropulsorBlocks
{
    static Dictionary<Guid, Row> _byBlock;
    static HashSet<Guid> _server, _client, _blocks, _rotorClient, _ramjetClient, _strikeServer;

    static void Build()
    {
        if (_byBlock != null) return;
        var b = new Dictionary<Guid, Row>(); var s = new HashSet<Guid>(); var c = new HashSet<Guid>(); var k = new HashSet<Guid>(); var rc = new HashSet<Guid>(); var jc = new HashSet<Guid>(); var ss = new HashSet<Guid>();
        foreach (var r in Rows)
        {
            b[r.Block] = r; s.Add(r.Server); c.Add(r.Client); k.Add(r.Block);
            if (AeroPropulsorComponent.Governed(r.Kind)) rc.Add(r.Client);
            if (AeroPropulsorComponent.IsRamjet(r.Kind)) jc.Add(r.Client);
            if (RotorStrike.Strikes(r.Kind)) ss.Add(r.Server);
        }
        _server = s; _client = c; _blocks = k; _rotorClient = rc; _ramjetClient = jc; _strikeServer = ss; _byBlock = b;
    }

    /// <summary>Server / client prefab GUIDs (EntityInjector) and block definition GUIDs (self-modelled: no hull surface).</summary>
    public static HashSet<Guid> ServerPrefabs { get { Build(); return _server; } }
    public static HashSet<Guid> ClientPrefabs { get { Build(); return _client; } }
    public static HashSet<Guid> Blocks { get { Build(); return _blocks; } }
    /// <summary>The governed propulsors' client prefabs - main and reversible rotors, turboprops (AeroRotorAnimComponent; a turboprop
    /// not split into spinner / blades / blur is left to its vanilla spin).</summary>
    public static HashSet<Guid> RotorClientPrefabs { get { Build(); return _rotorClient; } }
    /// <summary>The ramjets' client prefabs (AeroRamjetSpikeComponent: the inlet spike slid aft with the Mach).</summary>
    public static HashSet<Guid> RamjetClientPrefabs { get { Build(); return _ramjetClient; } }

    /// <summary>The striking propulsors' SERVER prefabs - helicopter rotors, the Reversible Rotor, turboprops (AeroRotorStrikeComponent:
    /// the probes and the blades' saved health; the client copy reads the published state).</summary>
    public static HashSet<Guid> StrikeServerPrefabs { get { Build(); return _strikeServer; } }

    /// <summary>A block's row (null: not a propulsor).</summary>
    internal static Row RowOf(Entity e)
    {
        Build();
        var cb = e?.TryGet<CubeBlockComponent>();
        var g = cb != null ? BlockComponentFactory.GetDefinitionGuid(cb) : null;
        return g.HasValue && _byBlock.TryGetValue(g.Value, out var r) ? r : null;
    }

    /// <summary>Fill a component's kind / size / radius / hub from its block's definition; false: not one of ours.</summary>
    internal static bool Describe(AeroPropulsorComponent p)
    {
        Build();
        var cb = p.Entity?.TryGet<CubeBlockComponent>();
        var g = cb != null ? BlockComponentFactory.GetDefinitionGuid(cb) : null;
        if (!g.HasValue || !_byBlock.TryGetValue(g.Value, out var r)) return false;
        p.Kind = r.Kind; p.Size = r.Size; p.RotorRadius = r.Radius; p.HubOffset = r.Hub;
        return true;
    }
}
