#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.ResourceDistribution.Resources;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;

namespace AeroMod;

/// <summary>
/// THE SHIP'S STATE for the combat slots (2026-10-19; ApSense.HasShipStatus / FuelShare / IntegrityShare): every ScanSeconds while
/// the grid flies Engage, its blocks walked once (HierarchyComponent.Children - the thruster cache's walk): the blocks' integrity
/// (CubeBlockComponent.HealthIntegrity, 0..1 each) summed - over the sum when the mode was engaged (destroyed blocks are gone from
/// the walk: counted as lost) -, and the tanks' fill (ResourceContainerComponent.CurrentChargeValue / MaxChargeValue, every
/// container of a resource with mass - a battery's charge has none). On the grid's server step; no allocation.
/// </summary>
internal sealed class ApShipScan
{
    public const float ScanSeconds = 2f;
    long _next; double _integrityStart = -1; bool _engaged;
    public bool Known; public float Fuel = float.NaN, Integrity = 1f;

    /// <summary>The engage is over (a new one takes the integrity it then has as its whole).</summary>
    public void Reset() { _engaged = false; _integrityStart = -1; Known = false; }

    public void Update(Entity grid, bool engaged)
    {
        if (!engaged) { if (_engaged) Reset(); return; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_engaged && now < _next) return;
        _engaged = true; _next = now + (long)(ScanSeconds * System.Diagnostics.Stopwatch.Frequency);
        var h = grid?.TryGet<HierarchyComponent>();
        if (h?.Children == null) return;
        double integ = 0, charge = 0, cap = 0;
        foreach (var e in h.Children)
        {
            if (e == null) continue;
            var cb = e.TryGet<CubeBlockComponent>();
            if (cb != null) integ += cb.HealthIntegrity;
            var rc = e.TryGet<ResourceContainerComponent>();
            if (rc != null)
            {
                var rt = rc.ResourceType;
                if (rt == null || (double)rt.MassPerUnit <= 0) continue;   // (fuel has mass - a battery's charge has none)
                charge += (double)rc.CurrentChargeValue; cap += (double)rc.MaxChargeValue;
            }
        }
        if (_integrityStart < 0 || integ > _integrityStart) _integrityStart = integ;   // (welded up past it: the new whole)
        Integrity = _integrityStart > 0 ? (float)(integ / _integrityStart) : 1f;
        Fuel = cap > 0 ? (float)(charge / cap) : float.NaN;
        Known = true;
    }
}
