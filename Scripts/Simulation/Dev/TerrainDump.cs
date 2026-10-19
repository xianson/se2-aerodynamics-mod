#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.IO;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Queries;

namespace AeroMod;

/// <summary>
/// TERRAIN DUMP (harness: aeroset TerrainDump.Size 2000; aeroset TerrainDump.Step 10; aeroset TerrainDump.Name canyon;
/// aeroset TerrainDump.Go true - then aeroget TerrainDump.Status): a square of the ground around the SEATED pilot's ship sampled
/// with the physics' ray straight down the gravity, written as a heightfield for Tests/AutopilotSim (SimTerrain.Load:
/// "hf nx nz step originX originZ", then nz rows of nx heights in m, over the ship's ground point; misses written as the last
/// height found - the ray hits only where the game has built voxel colliders, near entities: fly the area low first, and check
/// Misses). Rows run along the local east (the gravity's tangent plane, X), columns along the local north (Z). Run from the
/// server ship's attitude step, Rays per tick (never in normal play: Go false is one read). To %TEMP%\AeroMod\terrain\NAME.hf;
/// copy it to Tests/data/terrain/ for the sim. NOT RUN YET - an in-game step (the game was not launched for this work).
/// </summary>
public static class TerrainDump
{
    public static bool Go; public static float Size = 2000f, Step = 10f, Above = 1500f; public static string Name = "dump"; public static int Rays = 200;
    public static string Status = "-"; public static int Done, Misses;
    static float[] _h; static int _n, _i; static Vector3D _c; static Vector3 _down, _east, _north;
    static readonly GridPhysics.GroundProbe _probe = new GridPhysics.GroundProbe();

    /// <summary>Server, a seated ship's attitude step (AeroGridComponent.AutopilotStep): a slice of the rays.</summary>
    internal static void Sample(AeroGridComponent aero, in Keen.VRage.Core.WorldTransform wt)
    {
        var physics = GridPhysics.Physics;
        if (physics == null) return;
        if (_h == null)
        {
            Vector3 g = GridPhysics.GetGravityDirection(aero.Data);
            if (g.LengthSquared() < 1e-4f) { Status = "no gravity here"; Go = false; return; }
            _down = Vector3.Normalize(g); _east = Vector3.Normalize(ApMath.Flat(MathF.Abs(_down.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ, _down)); _north = Vector3.Cross(_east, _down);
            _n = Math.Clamp((int)(Size / MathF.Max(Step, 0.5f)) + 1, 2, 2001); _h = new float[_n * _n]; _i = 0; Misses = 0; _c = wt.Position;
            Status = $"sampling {_n} x {_n}";
        }
        float half = 0.5f * (_n - 1) * Step, last = 0f;
        for (int k = 0; k < Rays && _i < _h.Length; k++, _i++)
        {
            int ix = _i % _n, iz = _i / _n;
            Vector3D top = _c + (Vector3D)(_east * (ix * Step - half) + _north * (iz * Step - half) - _down * Above);
            _probe.Hits.Clear();
            var args = new RayCastArgs(in top, _down * (2f * Above));
            physics.CastRay(_probe.Hits, in args, CollisionPreset.Closest);
            if (_probe.Hits.Count > 0) { last = Above - (float)(_probe.Hits[0].Fraction * 2f * Above); }
            else { Misses++; if (_i > 0) last = _h[_i - 1]; }
            _h[_i] = last;
        }
        Done = _i;
        if (_i < _h.Length) return;
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "AeroMod", "terrain"); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, Name + ".hf");
            using (var w = new StreamWriter(path))
            {
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                w.WriteLine($"hf {_n} {_n} {Step.ToString("R", inv)} {(-half).ToString("R", inv)} {(-half).ToString("R", inv)}");
                var row = new System.Text.StringBuilder();
                for (int z = 0; z < _n; z++) { row.Clear(); for (int x = 0; x < _n; x++) { if (x > 0) row.Append(' '); row.Append(_h[z * _n + x].ToString("F2", inv)); } w.WriteLine(row.ToString()); }
            }
            Status = $"written {path}: {_n} x {_n}, {Misses} misses";
        }
        catch (Exception e) { Status = "write failed: " + e.Message; }
        _h = null; Go = false;
    }
}
