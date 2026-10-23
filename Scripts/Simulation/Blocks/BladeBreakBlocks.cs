#pragma warning disable
using System;
using System.Collections.Generic;

namespace AeroMod;

/// <summary>The rotors' blade-break table (BladeBreakBlocks.g.cs, generated with the blade-break content): per block (by its
/// PropulsorBlocks name) where a blade breaks and the two pieces that fly off - RotorStrike's per-blade breaks (PerBlade kinds).
/// Hinge, StubR, SplitR: m from the spin axis (the blade's hinge; the stub's end; where the tip chunk B parts from chunk A).
/// A chunk: its model (ModelAsset id - spawned as client debris: BladeDebris), mass (kg), centre of mass (blade frame, VRage, m
/// from the blade's dummy = its hinge) and half extents (m).</summary>
public static partial class BladeBreakBlocks
{
    public sealed class Chunk
    {
        public readonly Guid Model; public readonly float Mass; public readonly Vector3 Com, HalfExtents;
        public Chunk(Guid model, float mass, Vector3 com, Vector3 halfExtents) { Model = model; Mass = mass; Com = com; HalfExtents = halfExtents; }
        /// <summary>Its centre's radius from the axis (m): the hinge plus its offset along the blade.</summary>
        public float ComRadius(float hinge) => hinge + Com.Z;
    }
    public sealed class Row
    {
        public readonly string Name; public readonly float Hinge, StubR, SplitR; public readonly Chunk A, B;
        public Row(string name, float hinge, float stubR, float splitR, Chunk a, Chunk b) { Name = name; Hinge = hinge; StubR = stubR; SplitR = splitR; A = a; B = b; }
    }

    static Dictionary<string, Row> _byName;
    /// <summary>A block's row by its PropulsorBlocks name (null: its blades do not break one by one).</summary>
    public static Row Of(string blockName)
    {
        var d = _byName;
        if (d == null)
        {
            d = new Dictionary<string, Row>();
            foreach (var r in Rows) d[r.Name] = r;
            _byName = d;
        }
        return blockName != null && d.TryGetValue(blockName, out var row) ? row : null;
    }
}
