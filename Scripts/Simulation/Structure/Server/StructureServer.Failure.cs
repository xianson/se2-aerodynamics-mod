#pragma warning disable
using System;
using System.Reflection;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
namespace AeroMod;

// FAILURE (StructureServer): a failing block split at its failing face or destroyed whole, and a failed cut watched until
// the grid has split along it.
public partial class AeroGridComponent
{
    /// <summary>SPLIT, NOT DESTROY (Structure.SplitBlocks; 2026-10-14, the user: "instead of just destroying a block, we could
    /// split it?"): a failing block with fracture pieces loses only the pieces at its failing joint's face; it stays, broken,
    /// and the ship parts along that face.</summary>
    public static bool SplitBlocks = true;
    /// <summary>Most fracture pieces one failure kills; how far (m) into the block from the joint, and across its face, the
    /// pieces are looked for; the box (m) each look covers.</summary>
    public static int SplitMaxPieces = 6;
    public static float SplitDepth = 0.2f, SplitSpread = 0.35f, SplitBox = 0.3f;
    /// <summary>Diagnostics: blocks split, destroyed whole, pieces killed; the last.</summary>
    public static int SplitDone, SplitDestroyed, SplitPieces; public static string SplitLast = "-";

    // (BreakableBlockComponent by reflection: its base, BreakableComponent, is in VRage.Game - not a mod reference, CS0012
    //  for any typed use - docs/REFLECTION_AND_WHITELIST.md)
    static FieldInfo _breakableField; static MethodInfo _trySetFracture, _isBroken; static bool _breakableTried;
    static object Breakable(CubeBlockComponent b)
    {
        if (!_breakableTried)
        {
            _breakableTried = true;
            _breakableField = typeof(CubeBlockComponent).GetField("_breakableBlock", BindingFlags.NonPublic | BindingFlags.Instance);
            var t = _breakableField?.FieldType;
            if (t != null)
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    var ps = m.GetParameters();
                    if (m.Name == "TrySetFractureIntegrity" && ps.Length == 3 && ps[1].ParameterType == typeof(float) && ps[2].ParameterType == typeof(float)) _trySetFracture = m;
                    else if (m.Name == "IsBroken" && ps.Length == 0 && m.ReturnType == typeof(bool)) _isBroken = m;
                }
            if (_trySetFracture == null) Log.Default?.Info("[AERO-STRUCT] split blocks off: BreakableBlockComponent.TrySetFractureIntegrity not found");
        }
        return _trySetFracture != null ? _breakableField.GetValue(b) : null;
    }

    /// <summary>The failing joint's face of a breakable block broken: the fracture pieces under it, looked for a little way in
    /// from the joint, at its middle and across it (TrySetFractureIntegrity kills the first live piece in a box, through the
    /// game's own fracturing: debris, the fractured model, the collider changed, the block's mount points on dead pieces cut,
    /// the grid's connectivity re-checked and split - server side, replicated). Returns the pieces killed (0: none there -
    /// armour has none).</summary>
    int SplitFace(CubeBlockComponent b, Vector3 at, Vector3 dir)
    {
        var bb = Breakable(b);
        if (bb == null || dir.LengthSquared() < 0.5f) return 0;
        var bwt = b.Entity.Data.GetWorldTransform(); var gwt = Entity.Data.GetWorldTransform();
        var box = b.AABB;
        // (the block's box, m: cells centred at i x Cell)
        Vector3 lo = new Vector3(box.Min.X, box.Min.Y, box.Min.Z) * TearMath.Cell - new Vector3(TearMath.Cell * 0.5f - 0.02f);
        Vector3 hi = new Vector3(box.Max.X, box.Max.Y, box.Max.Z) * TearMath.Cell + new Vector3(TearMath.Cell * 0.5f - 0.02f);
        dir = Vector3.Normalize(dir);
        // (across the face: two directions square to the way in)
        var u = Vector3.Normalize(Vector3.Cross(dir, MathF.Abs(dir.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        var v = Vector3.Cross(dir, u);
        var c = at + dir * SplitDepth;
        var pts = new[] { c, c + u * SplitSpread, c - u * SplitSpread, c + v * SplitSpread, c - v * SplitSpread };
        var args = new object[3]; int killed = 0;
        foreach (var q in pts)
        {
            if (killed >= SplitMaxPieces) break;
            var p = Vector3.Clamp(q, lo, hi);
            Vector3D world = Keen.VRage.Core.WorldTransform.Transform((Vector3D)p, in gwt);
            args[0] = (Vector3)Keen.VRage.Core.WorldTransform.TransformInv(world, bwt); args[1] = SplitBox; args[2] = 0f;
            // (each call kills one piece; once more for the next one in the same box)
            for (int k = 0; k < 2 && killed < SplitMaxPieces; k++)
            {
                if (Gone(b)) return killed;
                if (!(_trySetFracture.Invoke(bb, args) is bool ok) || !ok) break;
                killed++;
                // (the piece's loss took the block under its destruction threshold: the game is removing it now - never
                //  touched again: a second fracture call on it asserted 'Killing dead Entity' and crashed the server, 2026-10-14)
                if (Gone(b)) { _goneBlocks.Add(b); return killed; }
            }
        }
        return killed;
    }

    /// <summary>A block that failed (health out, or a snap). SplitBlocks: its pieces at the failing face broken (SplitFace) -
    /// it stays, broken, its health what its pieces left (the game removes it itself under its destruction threshold; a
    /// critical piece lost stops it working, and a tank may explode - the game's own rules). Otherwise - no pieces there
    /// (armour), or SplitBlocks off - destroyed whole, fractured along the line from the failing joint into it. fx: its own
    /// torn-edge smoke and tear joint (off for a failed cut's members: once for the whole cut, at its centre - CutDone).</summary>
    void FailBlock(GridDamageReceiverComponent recv, Keen.VRage.Core.WorldTransform gwt, int bi, CubeBlockComponent b, (Vector3 at, Vector3 dir, float util) where, List<Vector3> torn, bool fx = true)
    {
        _stHealth.TakeAll(bi);
        if (SplitBlocks)
        {
            int killed = 0;
            try { killed = SplitFace(b, where.at, where.dir); }
            catch (Exception e) { SplitLast = "split failed: " + (e.InnerException ?? e).Message; Log.Default?.Info($"[AERO-STRUCT] {SplitLast}"); }
            if (killed > 0)
            {
                // (TEARING: the failed FACE, not the block's middle - the block stays; TORN-EDGE SMOKE out of the face)
                if (fx) { if (torn != null) torn.Add(where.at); TearSmoke.Publish(Entity, where.at, where.dir); }
                bool alive = !Gone(b);
                _stHealth.Set(bi, alive ? Math.Clamp(b.HealthIntegrity, 0f, 1f) : 0f);
                bool broken = false;
                try { broken = alive && _isBroken != null && _isBroken.Invoke(Breakable(b), null) is true; } catch { }
                SplitDone++; SplitPieces += killed; StructureLoads.Breaks++;
                SplitLast = $"{Entity?.DebugName}: {BlockName(b)} split at its failing face ({killed} pieces, utilisation {where.util:F2}){(alive ? $", health {b.HealthIntegrity:F2}" : ", gone")}{(broken ? ", broken (a critical piece)" : "")}";
                Log.Default?.Info($"[AERO-STRUCT] {SplitLast}");
                return;
            }
        }
        // (a split that faulted or took the block under its threshold: the game is removing it - never destroyed twice)
        if (Gone(b)) { _goneBlocks.Add(b); _stHealth.Set(bi, 0f); return; }
        var bbx = b.AABB; var gone = TearMath.CellCentre(bbx.Min, bbx.Max);   // (read before it goes)
        // (TORN-EDGE SMOKE, 2026-10-14: the ship's side of the break smokes where the block was, out of the gap - the way into
        //  the departing block; the piece's side when the split follows: StructureTear)
        if (fx) { if (torn != null) torn.Add(gone); TearSmoke.Publish(Entity, gone, where.dir); }
        // (FRACTURED where it failed: the game splits the block along a line from the failing joint into it, in the block's own
        //  frame - as its collisions do - not wherever it picks; 2026-10-14)
        recv.DealDamage(b, float.MaxValue, null, Fracture(b, gwt, where.at, where.dir));
        _goneBlocks.Add(b);
        StructureLoads.Breaks++; SplitDestroyed++;
        Log.Default?.Info($"[AERO-STRUCT] grid {Entity?.DebugName}: {BlockName(b)} broke (utilisation {where.util:F2})");   // (the block's kind: its entity's name was 'Root Entity' for some)
    }

    /// <summary>A failed cut's members all handled (ApplyStructureDamage): if any of them went now, its torn-edge smoke once at
    /// its centre, out of it into what leaves (TearSmoke), the tear's joint there (StructureTear), and the structure's damage
    /// held until the structure is rebuilt without what left (NO ZIPPER: _stBreakHold) - from blocks changed since version0,
    /// the version before the pass (the game may count the removals at once). failed: its members that went (reset).</summary>
    // A FAILED CUT MUST SEPARATE (2026-10-15, the user: "solidly attached but not attached"): a member split rather than
    // destroyed (SplitFace: only the pieces at its failing face) can keep mount points across the cut on its other pieces -
    // the block still bridges it, the game never splits the grid, and the far side hangs on, visibly cut. So a cut whose
    // members survived is watched: no split of this grid (CubeGridComponent.SplitGridSpawnedSignal) within CutVerifyTicks,
    // and its surviving members are destroyed - the bridge gone, the game splits.
    public static int CutVerifyTicks = 30;
    public static int CutsForced;
    readonly List<CubeBlockComponent> _cutSurvivors = new();
    sealed class CutVerify { public List<CubeBlockComponent> Members; public int Stamp, Ticks, Axis; public Vector3 At; public float Plane; public Vector3I Lo, Hi; }
    readonly List<CutVerify> _cutVerify = new();
    int _splitStamp; bool _splitWatched;
    static Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent.SplitGridSpawnedSignal.ExternalApi _onSplitStamp;

    void WatchSplits()
    {
        if (_splitWatched || Entity == null) return;
        _splitWatched = true;
        try { _onSplitStamp ??= OnSplitStamp; Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent.SplitGridSpawnedSignal.Subscribe(Entity, _onSplitStamp); }
        catch (Exception e) { Log.Default?.Info($"[AERO-STRUCT] split watch failed: {e.Message}"); }
    }
    static void OnSplitStamp(Keen.VRage.DCS.Components.Entity original, Keen.VRage.DCS.Components.Entity piece)
    {
        var a = original?.TryGet<AeroGridComponent>();
        if (a != null) System.Threading.Interlocked.Increment(ref a._splitStamp);
    }

    /// <summary>Every tick (simulation thread), while a failed cut is watched: split since - done; else, its time out, its
    /// surviving members destroyed (the Gone guards).</summary>
    void CheckCutSeparated()
    {
        if (_cutVerify.Count == 0) return;
        int stamp = System.Threading.Volatile.Read(ref _splitStamp);
        GridDamageReceiverComponent recv = null;
        for (int i = _cutVerify.Count - 1; i >= 0; i--)
        {
            var v = _cutVerify[i];
            if (stamp != v.Stamp) { _cutVerify.RemoveAt(i); continue; }   // (it split: the cut separated)
            if (--v.Ticks > 0) continue;
            _cutVerify.RemoveAt(i);
            recv ??= Entity?.TryGet<GridDamageReceiverComponent>();
            if (recv == null || _octree == null) continue;
            // the bridges: the members split rather than destroyed, and every block still TOUCHING ACROSS the cut's plane in
            // its cross-section (cells k | k+1 along its axis - whatever the structure counted as members, these still join
            // the two sides: the second cut of a test failed 29 of 29 members and still did not split, 2026-10-15)
            var bridge = new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance);
            foreach (var b in v.Members) if (!Gone(b)) bridge.Add(b);
            int ax = v.Axis, k0 = (int)MathF.Round(v.Plane / TearMath.Cell - 0.5f);
            int u = (ax + 1) % 3, w = (ax + 2) % 3;
            int uLo = ShipStructure.Ci(v.Lo, u), uHi = ShipStructure.Ci(v.Hi, u), wLo = ShipStructure.Ci(v.Lo, w), wHi = ShipStructure.Ci(v.Hi, w);
            if ((long)(uHi - uLo + 1) * (wHi - wLo + 1) <= 40000)
                for (int a = uLo; a <= uHi; a++) for (int c = wLo; c <= wHi; c++)
                {
                    var c0 = Cell(ax, k0, u, a, w, c); var c1 = Cell(ax, k0 + 1, u, a, w, c);
                    var b0 = _octree.TryGetCubeBlock(c0); var b1 = _octree.TryGetCubeBlock(c1);
                    if (b0 == null || b1 == null || Gone(b0) || Gone(b1)) continue;
                    if (ReferenceEquals(b0, b1)) { bridge.Add(b0); continue; }   // (one block across the plane)
                    bridge.Add(b0); bridge.Add(b1);   // (two blocks face to face across it)
                }
            int n = 0;
            foreach (var b in bridge)
            {
                if (Gone(b)) continue;
                try { recv.DealDamage(b, float.MaxValue); _goneBlocks.Add(b); n++; }
                catch (Exception e) { _goneBlocks.Add(b); Log.Default?.Info($"[AERO-STRUCT] bridge skipped: {e.Message}"); }
            }
            CutsChecked++;
            if (n > 0) { CutsForced++; Log.Default?.Info($"[AERO-STRUCT] {Entity?.DebugName}: the cut at {v.At} did not separate - {n} block(s) still bridging it, destroyed"); }
        }
    }
    public static int CutsChecked;
    static Vector3I Cell(int ax, int k, int u, int a, int w, int c)
    {
        var r = new int[3]; r[ax] = k; r[u] = a; r[w] = c;
        return new Vector3I(r[0], r[1], r[2]);
    }

    void CutDone(StructureEval.CutFail cut, ref int failed, List<Vector3> torn, int version0)
    {
        int n = failed; failed = 0;
        if (n == 0) { _cutSurvivors.Clear(); return; }   // (every member already gone: an old cut seen again)
        // (every failed cut watched - it must separate; CheckCutSeparated): its members' cross-section (cells, on the two axes
        //  across the cut, one cell wider all round) and plane, and the members that were split rather than destroyed
        {
            WatchSplits();
            var lo = new Vector3I(int.MaxValue); var hi = new Vector3I(int.MinValue);
            for (int k = cut.First; k < cut.First + cut.Count && k < _stDamage.Count; k++)
            {
                int bi = _stDamage[k].block;
                if (bi < 0 || bi >= _stBlocks.Count || _stBlocks[bi] == null) continue;
                var bb = _stBlocks[bi].AABB; lo = Vector3I.Min(lo, bb.Min); hi = Vector3I.Max(hi, bb.Max);
            }
            if (lo.X <= hi.X)
                _cutVerify.Add(new CutVerify { Members = new List<CubeBlockComponent>(_cutSurvivors), Stamp = System.Threading.Volatile.Read(ref _splitStamp),
                    Ticks = CutVerifyTicks, At = cut.At, Axis = cut.Axis, Plane = cut.Plane, Lo = lo - Vector3I.One, Hi = hi + Vector3I.One });
            _cutSurvivors.Clear();
        }
        if (torn != null) torn.Add(cut.At);
        TearSmoke.Publish(Entity, cut.At, cut.Dir);
        _stBreakHold = StructureLoads.BreakHoldMax; _stHoldVersion = version0;
        StructureLoads.LastCut = $"{Entity?.DebugName}: the cut at {cut.At} (axis {cut.Axis}) failed whole - {n} of {cut.Count} members (worst utilisation {cut.Util:F2})";
        Log.Default?.Info($"[AERO-STRUCT] {StructureLoads.LastCut}");
    }
}
