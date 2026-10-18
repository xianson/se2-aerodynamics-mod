#pragma warning disable
using System;
using System.Collections.Generic;

namespace AeroMod;

/// <summary>The exhaust table's lookup (ExhaustBlocks.g.cs, generated with the effects by gen_propulsor_defs.py gen_exhaust): a block
/// definition GUID's exhaust row (null: no exhaust of ours). Game-free.</summary>
public static partial class ExhaustBlocks
{
    static Dictionary<Guid, Row> _byBlock;

    /// <summary>A block definition's exhaust (null: not a ramjet or a solid rocket nozzle).</summary>
    public static Row RowOf(Guid block)
    {
        var m = _byBlock;
        if (m == null)
        {
            m = new Dictionary<Guid, Row>();
            foreach (var r in Rows) m[r.Block] = r;
            _byBlock = m;
        }
        return m.TryGetValue(block, out var row) ? row : null;
    }
}
