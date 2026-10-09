using System;
using System.Collections.Generic;
using Keen.VRage.Core;

#pragma warning disable
namespace AeroMod;

/// <summary>
/// The ship a grid belongs to, for the effects: every grid joined to it (rotors, hinges, pistons, connectors), found once a
/// second on the server. One ship draws ONE set of wing tip trails: its main grid (the largest frontal area of the group)
/// gathers the wing tips of all of its grids; the others draw none (the user, 2026-10-07: each of the Hercules' five grids
/// laid its own trails).
/// </summary>
public partial class AeroGridComponent
{
    internal AeroGridComponent[] FxGroup = Array.Empty<AeroGridComponent>();
    internal bool FxTipOwner = true;
    private long _fxGroupAt;

    static int HullSize(AeroGridComponent a)
    {
        var ch = a?.FxChunks; if (ch == null) return 0;
        int n = 0; foreach (var l in ch.Occluders) n += l.Count;
        return n;
    }

    /// <summary>Server, from the flow publish: the group again when a second old.</summary>
    internal void RefreshFxGroup()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_fxGroupAt != 0 && now - _fxGroupAt < System.Diagnostics.Stopwatch.Frequency) return;
        _fxGroupAt = now;
        var e = Entity;
        if (e == null) return;
        var list = new List<AeroGridComponent> { this };
        try
        {
            var svc = e.GetSession()?.Get<Keen.Game2.Simulation.GameSystems.Physicss.IFindConnectedEntities>();
            if (svc != null)
                using (var buf = new Keen.VRage.Library.Memory.Buffer<Keen.VRage.DCS.Components.Entity>(Keen.VRage.Library.Memory.Allocator.Pool, "AeroFxGroup"))
                {
                    var args = new Keen.Game2.Simulation.GameSystems.Physicss.IFindConnectedEntities.SearchArguments { IncludeRoot = false, IncludeConstrained = true };
                    svc.FindConnectedEntities(e, (Keen.VRage.Library.Memory.BufferReference<Keen.VRage.DCS.Components.Entity>)buf, args);
                    var en = ((Keen.VRage.Library.Memory.BufferReference<Keen.VRage.DCS.Components.Entity>)buf).GetEnumerator();
                    while (en.MoveNext())
                    {
                        var o = en.Current; if (o == null) continue;
                        foreach (var c in o.Components) if (c is AeroGridComponent a && !list.Contains(a)) { list.Add(a); break; }
                    }
                    en.Dispose();
                }
        }
        catch (Exception) { }   // (once a second, never on a hot path: alone is the safe answer)
        FxGroup = list.ToArray();
        // the main grid: the most hull (its chunk table's occluder points) - the same answer from every grid of the group and
        // stable in flight (frontal area changed with the angle of attack and a pod's tilt: the trails hopped grids); a tie
        // by the oldest hash
        AeroGridComponent best = this;
        foreach (var a in list)
        {
            int sa = HullSize(a), sb = HullSize(best);
            if (sa > sb || (sa == sb && a.GetHashCode() < best.GetHashCode())) best = a;
        }
        // (a margin: the owner moves only to a hull more than 10 % larger - two similar grids losing blocks swapped it)
        AeroGridComponent cur = null; foreach (var a in list) if (a.FxTipOwner) { cur = a; break; }
        if (cur != null && !ReferenceEquals(cur, best) && HullSize(best) <= 1.1f * HullSize(cur)) best = cur;
        FxTipOwner = ReferenceEquals(best, this);
    }
}
