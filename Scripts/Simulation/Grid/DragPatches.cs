#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// Other mods' drag on a grid (AeroApi.SetDrag / SetPlateDrag / ClearDrag, API version 6): keyed slots, each a drag area (Cd x A, m2) at
/// a grid-local point - isotropic, or a flat plate (drag x its area facing the air: |n . flow|). Applied every tick with
/// the grid's own air, as AIRFRAME air (in the result the attitude control answers - not the control surfaces' actuator
/// torque): q at the point (its own velocity, the grid's rotation included) x CdA, against that point's travel, so an
/// off-centre patch turns the ship as it should. Set rarely, read every tick: the slots are copied to a new list on change.
/// </summary>
public partial class AeroGridComponent
{
    internal readonly struct DragPatch
    {
        public readonly string Key; public readonly Vector3 Point, Normal; public readonly float CdA;
        public DragPatch(string key, Vector3 point, float cdA, Vector3 normal) { Key = key; Point = point; CdA = cdA; Normal = normal; }
    }

    /// <summary>Caps: a patch's drag area and how far from the grid's origin it may act (a typo must not fling a ship).</summary>
    public const float MaxPatchCdA = 10000f, MaxPatchReach = 2000f;
    readonly Dictionary<string, DragPatch> _dragSlots = new(StringComparer.Ordinal);
    volatile List<DragPatch> _dragPatches = new();   // (replaced, never changed in place: read without the lock)
    /// <summary>The patches' force and torque last tick (grid-local, about the centre of mass).</summary>
    internal Vector3 LastPatchForce, LastPatchTorque;

    internal bool SetDragPatch(string key, Vector3 point, float cdA, Vector3 normal)
    {
        if (string.IsNullOrEmpty(key) || !PhysicsHack.IsFinite(point) || !float.IsFinite(cdA) || !PhysicsHack.IsFinite(normal)) return false;
        if (point.Length() > MaxPatchReach) return false;
        cdA = Math.Clamp(cdA, 0f, MaxPatchCdA);
        float nl = normal.Length();
        normal = nl > 1e-6f ? normal / nl : Vector3.Zero;
        lock (_dragSlots)
        {
            if (cdA <= 0f) _dragSlots.Remove(key);
            else _dragSlots[key] = new DragPatch(key, point, cdA, normal);
            _dragPatches = new List<DragPatch>(_dragSlots.Values);
        }
        return true;
    }

    internal int ClearDragPatches(string prefix)
    {
        int n = 0;
        lock (_dragSlots)
        {
            var gone = new List<string>();
            foreach (var k in _dragSlots.Keys) if (string.IsNullOrEmpty(prefix) || k.StartsWith(prefix, StringComparison.Ordinal)) gone.Add(k);
            foreach (var k in gone) { _dragSlots.Remove(k); n++; }
            if (n > 0) _dragPatches = new List<DragPatch>(_dragSlots.Values);
        }
        return n;
    }

    internal bool TryGetDragPatch(string key, out Vector3 point, out float cdA, out Vector3 normal)
    {
        lock (_dragSlots)
        {
            if (_dragSlots.TryGetValue(key, out var p)) { point = p.Point; cdA = p.CdA; normal = p.Normal; return true; }
        }
        point = normal = Vector3.Zero; cdA = 0f; return false;
    }

    /// <summary>The patches' force and torque this tick (grid-local; torque about the centre of mass).</summary>
    static (Vector3 force, Vector3 torque) EvaluateDragPatches(List<DragPatch> patches, in AeroContext ctx)
    {
        Vector3 f = Vector3.Zero, t = Vector3.Zero;
        float rho = (float)ctx.Atmosphere.Density;
        if (!(rho > 0f)) return (f, t);
        foreach (var p in patches)
        {
            // (the point's own travel: the grid's rotation included)
            Vector3 force = PatchDrag.Force(ctx.VelocityAtPoint(p.Point), rho, p.CdA, p.Normal);
            f += force;
            t += Vector3.Cross(p.Point - ctx.CenterOfMass, force);
        }
        return (f, t);
    }
}
