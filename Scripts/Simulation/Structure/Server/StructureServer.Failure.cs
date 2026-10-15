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
    /// torn-edge smoke and tear joint (off for a failed cut's members: once for the whole cut, at its centre - CutDone).
    /// ledger: its health ledger written (off for a crack's staged release - StepCracks runs outside the evaluation's turn;
    /// the ledger's part was done at the commit); split: off for a CRUSHED member (FractureMath.Crushed: it goes whole).</summary>
    void FailBlock(GridDamageReceiverComponent recv, Keen.VRage.Core.WorldTransform gwt, int bi, CubeBlockComponent b, (Vector3 at, Vector3 dir, float util) where, List<Vector3> torn, bool fx = true, bool ledger = true, bool split = true)
    {
        if (ledger) _stHealth.TakeAll(bi);
        if (SplitBlocks && split)
        {
            int killed = 0;
            try { killed = SplitFace(b, where.at, where.dir); }
            catch (Exception e) { SplitLast = "split failed: " + (e.InnerException ?? e).Message; Log.Default?.Info($"[AERO-STRUCT] {SplitLast}"); }
            if (killed > 0)
            {
                // (TEARING: the failed FACE, not the block's middle - the block stays; TORN-EDGE SMOKE out of the face)
                if (fx) { if (torn != null) torn.Add(where.at); TearSmoke.Publish(Entity, where.at, where.dir); }
                bool alive = !Gone(b);
                if (ledger) _stHealth.Set(bi, alive ? Math.Clamp(b.HealthIntegrity, 0f, 1f) : 0f);
                bool broken = false;
                try { broken = alive && _isBroken != null && _isBroken.Invoke(Breakable(b), null) is true; } catch { }
                SplitDone++; SplitPieces += killed; StructureLoads.Breaks++;
                SplitLast = $"{Entity?.DebugName}: {BlockName(b)} split at its failing face ({killed} pieces, utilisation {where.util:F2}){(alive ? $", health {b.HealthIntegrity:F2}" : ", gone")}{(broken ? ", broken (a critical piece)" : "")}";
                Log.Default?.Info($"[AERO-STRUCT] {SplitLast}");
                return;
            }
        }
        // (a split that faulted or took the block under its threshold: the game is removing it - never destroyed twice)
        if (Gone(b)) { _goneBlocks.Add(b); if (ledger) _stHealth.Set(bi, 0f); return; }
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
    sealed class CutVerify
    {
        public List<CubeBlockComponent> Members; public int Stamp, Ticks, Axis; public Vector3 At; public float Plane; public Vector3I Lo, Hi;
        /// <summary>(JAGGED, 2026-10-15) the member blocks the jagged line left standing on the ship, the way out's sign along
        /// the axis, and the stage: 0 - the jagged surface (stubs spared); 1 - the flat plane's rule, stubs and all.
        /// A set (from _stubSets): Bridges / BeyondStub test it per cell - a List.Contains there was cells x stubs, ~8 M
        /// compares a split stamp for a 100-stub cut on a 60 x 60 section, on the simulation thread (2026-10-15 review).</summary>
        public HashSet<CubeBlockComponent> Stubs; public int Side, Stage;
    }
    readonly List<CutVerify> _cutVerify = new();
    /// <summary>Spare stub sets (a verify's, back when it is dropped: DropVerify).</summary>
    readonly Stack<HashSet<CubeBlockComponent>> _stubSets = new();
    void DropVerify(CutVerify v) { if (v.Stubs != null) { v.Stubs.Clear(); _stubSets.Push(v.Stubs); v.Stubs = null; } }
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
            if (stamp != v.Stamp)
            {
                // (a split: THIS cut separated only if no two blocks of this grid still meet face to face across its plane - any
                //  split of the grid dropped every watched cut, one still bridged elsewhere never forced apart, 2026-10-15. A
                //  block straddling the plane alone does not count here: a member split at its face stays whole in the octree)
                v.Stamp = stamp;
                if (_octree == null || Bridges(v, null) == 0) { _cutVerify.RemoveAt(i); DropVerify(v); continue; }
            }
            if (--v.Ticks > 0) continue;
            _cutVerify.RemoveAt(i);
            recv ??= Entity?.TryGet<GridDamageReceiverComponent>();
            if (recv == null || _octree == null) { DropVerify(v); continue; }
            // the bridges: the members split rather than destroyed, and every block still TOUCHING ACROSS the cut's plane in
            // its cross-section (cells k | k+1 along its axis - whatever the structure counted as members, these still join
            // the two sides: the second cut of a test failed 29 of 29 members and still did not split, 2026-10-15)
            var bridge = new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance);
            foreach (var b in v.Members) if (!Gone(b)) bridge.Add(b);
            // (JAGGED, 2026-10-15: first the jagged surface - its stubs spared on the plane, and what still holds a stub from
            //  beyond it taken; only if that does not part it either, the plane's rule with the stubs - stage 1)
            bool spare = Spare(v);
            Bridges(v, bridge);
            int n = 0;
            foreach (var b in bridge)
            {
                if (Gone(b)) continue;
                try { recv.DealDamage(b, float.MaxValue); _goneBlocks.Add(b); n++; }
                catch (Exception e) { _goneBlocks.Add(b); Log.Default?.Info($"[AERO-STRUCT] bridge skipped: {e.Message}"); }
            }
            CutsChecked++;
            if (n > 0) { CutsForced++; Log.Default?.Info($"[AERO-STRUCT] {Entity?.DebugName}: the cut at {v.At} did not separate - {n} block(s) still bridging it, destroyed"); }
            // (the jagged pass done: watched once more, on the plane's rule, should it still not split)
            if (spare) { v.Stage = 1; v.Ticks = CutVerifyTicks; v.Stamp = stamp; _cutVerify.Add(v); }
            else DropVerify(v);
        }
    }
    public static int CutsChecked;
    /// <summary>(JAGGED) a watched cut's first stage spares the jagged line's stubs (stage 1: the plane's rule, stubs and all).</summary>
    static bool Spare(CutVerify v) => v.Stage == 0 && v.Stubs != null && v.Stubs.Count > 0;

    /// <summary>(JAGGED) the blocks holding a stub from beyond the jagged line: across its outer face (the way out), each cell's
    /// neighbour that is not gone and not a stub - what the jagged step should have broken (into: added; null: only counted).
    /// Returns how many cells hold it.</summary>
    int BeyondStub(CubeBlockComponent s, CutVerify v, HashSet<CubeBlockComponent> into)
    {
        if (Gone(s)) return 0;
        int ax = v.Axis, u = (ax + 1) % 3, w = (ax + 2) % 3;
        var bb = s.AABB;
        int kf = (v.Side > 0 ? ShipStructure.Ci(bb.Max, ax) : ShipStructure.Ci(bb.Min, ax)) + v.Side;
        int a0 = ShipStructure.Ci(bb.Min, u), a1 = ShipStructure.Ci(bb.Max, u), c0 = ShipStructure.Ci(bb.Min, w), c1 = ShipStructure.Ci(bb.Max, w);
        if ((long)(a1 - a0 + 1) * (c1 - c0 + 1) > 4096) return 0;
        int held = 0;
        for (int a = a0; a <= a1; a++) for (int c = c0; c <= c1; c++)
        {
            var o = _octree.TryGetCubeBlock(Cell(ax, kf, u, a, c));
            if (o != null && !ReferenceEquals(o, s) && !Gone(o) && !v.Stubs.Contains(o)) { held++; into?.Add(o); }
        }
        return held;
    }

    /// <summary>The blocks of this grid still touching across a watched cut's plane in its cross-section (into: added; null:
    /// only counted). Returns the pairs of two blocks face to face found (a block straddling the plane is added, not counted).
    /// (JAGGED, stage 0: pairs with a stub of the jagged line are not bridges - a stub stays on the ship by design - but what
    /// holds a stub from beyond the line is: BeyondStub.)</summary>
    int Bridges(CutVerify v, HashSet<CubeBlockComponent> into)
    {
        int ax = v.Axis, k0 = (int)MathF.Round(v.Plane / TearMath.Cell - 0.5f);
        int u = (ax + 1) % 3, w = (ax + 2) % 3;
        int uLo = ShipStructure.Ci(v.Lo, u), uHi = ShipStructure.Ci(v.Hi, u), wLo = ShipStructure.Ci(v.Lo, w), wHi = ShipStructure.Ci(v.Hi, w);
        if ((long)(uHi - uLo + 1) * (wHi - wLo + 1) > 40000) return 0;
        bool spare = Spare(v);
        int pairs = 0;
        for (int a = uLo; a <= uHi; a++) for (int c = wLo; c <= wHi; c++)
        {
            var c0 = Cell(ax, k0, u, a, c); var c1 = Cell(ax, k0 + 1, u, a, c);
            var b0 = _octree.TryGetCubeBlock(c0); var b1 = _octree.TryGetCubeBlock(c1);
            if (b0 == null || b1 == null || Gone(b0) || Gone(b1)) continue;
            if (spare && (v.Stubs.Contains(b0) || v.Stubs.Contains(b1))) continue;
            if (ReferenceEquals(b0, b1)) { into?.Add(b0); continue; }   // (one block across the plane)
            pairs++;
            if (into != null) { into.Add(b0); into.Add(b1); }   // (two blocks face to face across it)
        }
        if (spare) foreach (var sb in v.Stubs) pairs += BeyondStub(sb, v, into);
        return pairs;
    }

    /// <summary>The cell k along axis ax, a along u, c along the third (no array a cell: it made one per cell, 2026-10-15).</summary>
    static Vector3I Cell(int ax, int k, int u, int a, int c)
        => new Vector3I(ax == 0 ? k : u == 0 ? a : c, ax == 1 ? k : u == 1 ? a : c, ax == 2 ? k : u == 2 ? a : c);

    void CutDone(StructureEval.CutFail cut, ref int failed, List<Vector3> torn, int version0)
    {
        int n = failed; failed = 0;
        // (THE CRACK, 2026-10-15: its members queued, not failed - it runs from here, StartCrack)
        if (_crackOpen != null && ReferenceEquals(_crackOpen.Cut, cut)) { var c = _crackOpen; _crackOpen = null; _cutSurvivors.Clear(); StartCrack(c, version0); return; }
        if (n == 0) { _cutSurvivors.Clear(); return; }   // (every member already gone: an old cut seen again)
        // (every failed cut watched - it must separate; CheckCutSeparated): its members' cross-section (cells, on the two axes
        //  across the cut, one cell wider all round) and plane, and the members that were split rather than destroyed
        {
            var v = MakeVerify(cut);
            if (v != null) { v.Members = new List<CubeBlockComponent>(_cutSurvivors); v.Stamp = System.Threading.Volatile.Read(ref _splitStamp); _cutVerify.Add(v); }
            _cutSurvivors.Clear();
        }
        if (torn != null) torn.Add(cut.At);
        TearSmoke.Publish(Entity, cut.At, cut.Dir);
        _stBreakHold = StructureLoads.BreakHoldMax; _stHoldVersion = version0;
        StructureLoads.LastCut = $"{Entity?.DebugName}: the cut at {cut.At} (axis {cut.Axis}) failed whole - {n} of {cut.Count} members (worst utilisation {cut.Util:F2})";
        Log.Default?.Info($"[AERO-STRUCT] {StructureLoads.LastCut}");
    }

    /// <summary>A failed cut's verify (CheckCutSeparated) - its members' cross-section (cells, on the two axes across the cut,
    /// one cell wider all round), plane, the way out and the jagged line's stubs - while the block indices are the
    /// evaluation's (ApplyStructureDamage); null: no member left to measure. Members and Stamp: the caller's.</summary>
    CutVerify MakeVerify(StructureEval.CutFail cut)
    {
        WatchSplits();
        var lo = new Vector3I(int.MaxValue); var hi = new Vector3I(int.MinValue);
        for (int k = cut.First; k < cut.First + cut.Count && k < _stDamage.Count; k++)
        {
            int bi = _stDamage[k].block;
            if (bi < 0 || bi >= _stBlocks.Count || _stBlocks[bi] == null) continue;
            var bb = _stBlocks[bi].AABB; lo = Vector3I.Min(lo, bb.Min); hi = Vector3I.Max(hi, bb.Max);
        }
        if (lo.X > hi.X) return null;
        HashSet<CubeBlockComponent> stubs = null;
        if (cut.Stubs != null)
            foreach (int bi in cut.Stubs) if (bi >= 0 && bi < _stBlocks.Count && _stBlocks[bi] != null)
                (stubs ??= _stubSets.Count > 0 ? _stubSets.Pop() : new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance)).Add(_stBlocks[bi]);
        float side = cut.Axis == 0 ? cut.Dir.X : cut.Axis == 1 ? cut.Dir.Y : cut.Dir.Z;
        return new CutVerify { Ticks = CutVerifyTicks, At = cut.At, Axis = cut.Axis, Plane = cut.Plane, Lo = lo - Vector3I.One, Hi = hi + Vector3I.One,
            Stubs = stubs, Side = side > 0f ? 1 : -1 };
    }

    // ── THE CRACK RUNS ACROSS THE SECTION (2026-10-15, the user: "our uniform break is maybe a little too linear?" -
    //    Structure/Model/FractureMath). A failed cut's members let go in their planned order (StructureEval.CutFail.Release:
    //    the most loaded torn fibre first, the hinge - the pushed side - last) over FractureMath.CrackSeconds, a few a tick
    //    (StepCracks, every tick), not all at once. The cut is COMMITTED: the structure's damage is held while any crack runs
    //    (TickStructureCore), so the members left - the hinge, carrying everything now - are never re-solved into a zipper
    //    along the span; it ends whole. A hinge member (compression: FractureMath.Hinge) crumples at the commit (Crumple: a
    //    dent past wear's) and lets go last; a crushed one goes whole (no split). Smoke puffs along it (CrackSmokes), the
    //    creaks and the shake follow each release (StructureLoads.Breaks: StructureFeel). When the last has gone: the cut's
    //    verify (its stubs spared first) and the hold - what CutDone does at once; the tear's springs (StructureTear.Record:
    //    every member's place and its order) are recorded at its start. ──
    /// <summary>Diagnostics (harness aeroget): cracks started, finished, members released along them, hinges crumpled; the
    /// last finished crack, and the running ones.</summary>
    public static int CracksStarted, CracksDone, CrackReleases, Crumples, CracksRunning;
    public static string CrackLast = "-";
    /// <summary>Torn-edge smoke puffs along a crack: at its first release, then evenly to its last (the hinge).</summary>
    public static int CrackSmokes = 3;
    /// <summary>A hinge member's crumple (m: its armour's bones pushed back toward the cut and bulged out from the section's
    /// middle - a crushed wall buckles outward), and the most a bone takes from dents and crumples together (m).</summary>
    public static float CrumpleDepth = 0.15f, CrumpleMax = 0.25f;

    struct CrackPart { public CubeBlockComponent B; public int Bi, Entry; public (Vector3 at, Vector3 dir, float util) Where; public float At; public byte Mode; }
    sealed class Crack
    {
        public StructureEval.CutFail Cut; public CutVerify Verify;
        public readonly List<CrackPart> Parts = new();
        public float Clock; public int Next, Smoked, Version0, Torn, Hinges, Crushed;
        public readonly List<CubeBlockComponent> Survivors = new();
    }
    readonly List<Crack> _cracks = new();
    /// <summary>(ApplyStructureDamage) the cut whose members are being queued now.</summary>
    Crack _crackOpen;

    /// <summary>(ApplyStructureDamage) a failed cut's member queued for its crack instead of failed now: true. Its ledger's
    /// part done now (the release runs outside the evaluation's turn); a hinge crumpled now.</summary>
    bool Cracking(StructureEval.CutFail cut, int di, int bi, CubeBlockComponent b, (Vector3 at, Vector3 dir, float util) where)
    {
        if (!FractureMath.Crack || cut.Release == null || cut.Count < 2) return false;
        int k = di - cut.First;
        if (k < 0 || k >= cut.Release.Length) return false;
        if (_crackOpen == null || !ReferenceEquals(_crackOpen.Cut, cut)) _crackOpen = new Crack { Cut = cut };
        byte mode = cut.Mode != null && k < cut.Mode.Length ? cut.Mode[k] : FractureMath.Torn;
        _stHealth.TakeAll(bi);
        if (mode == FractureMath.Hinge)
        {
            // (its own block crumpled - a jagged step's stub stays on the ship, folded)
            int own = cut.Own != null && k < cut.Own.Length ? cut.Own[k] : -1;
            var ob = own >= 0 && own < _stBlocks.Count ? _stBlocks[own] : b;
            if (ob != null && !Gone(ob)) Crumple(ob, where.at, cut.Dir, cut.At);
            _crackOpen.Hinges++;
        }
        else if (mode == FractureMath.Crushed) _crackOpen.Crushed++;
        else if (mode == FractureMath.Torn) _crackOpen.Torn++;
        _crackOpen.Parts.Add(new CrackPart { B = b, Bi = bi, Entry = k, Where = where, At = cut.Release[k], Mode = mode });
        return true;
    }

    /// <summary>(CutDone) a queued crack started: its verify measured now, its parts in release order, the tear's springs
    /// recorded now (every member's place and its order - kept through the crack: the split may come before its last
    /// member, if that one no longer bridged), the first released now.</summary>
    void StartCrack(Crack c, int version0)
    {
        c.Verify = MakeVerify(c.Cut);
        c.Version0 = version0;
        c.Parts.Sort((p, q) => { int r = p.At.CompareTo(q.At); return r != 0 ? r : p.Entry.CompareTo(q.Entry); });
        if (StructureTear.Enabled && c.Parts.Count > 0)
        {
            var pts = new List<Vector3>(c.Parts.Count); var ord = new List<float>(c.Parts.Count);
            foreach (var p in c.Parts) { pts.Add(p.Where.at); ord.Add(p.At); }
            StructureTear.Record(Entity, pts, ord, (int)MathF.Ceiling(c.Parts[c.Parts.Count - 1].At * 60f) + 1);
        }
        _cracks.Add(c); CracksStarted++; CracksRunning = _cracks.Count;
        if (StepCrack(c, 0f)) { _cracks.Remove(c); CracksRunning = _cracks.Count; }
    }

    /// <summary>Every tick (simulation thread, TickStructure): the running cracks' parts due let go.</summary>
    void StepCracks(float dt)
    {
        if (_cracks.Count == 0) return;
        for (int i = _cracks.Count - 1; i >= 0; i--) if (StepCrack(_cracks[i], dt)) _cracks.RemoveAt(i);
        CracksRunning = _cracks.Count;
    }

    /// <summary>A crack's parts due by its clock released (FailBlock through the Gone guards: split - a crushed one whole -
    /// or destroyed); true when its last has gone (FinishCrack done).</summary>
    bool StepCrack(Crack c, float dt)
    {
        c.Clock += dt;
        if (c.Next < c.Parts.Count && c.Parts[c.Next].At <= c.Clock + 1e-4f)
        {
            var recv = Entity?.TryGet<GridDamageReceiverComponent>();
            if (recv == null) { CrackLast = $"{Entity?.DebugName}: crack dropped (no damage receiver)"; return true; }
            var gwt = Entity.Data.GetWorldTransform();
            c.Version0 = StructBlockVersion;   // (the blocks' version before this step's failures: the hold's, FinishCrack)
            int n = c.Parts.Count, s = Math.Max(1, CrackSmokes);
            while (c.Next < n && c.Parts[c.Next].At <= c.Clock + 1e-4f)
            {
                int j = c.Next++;
                var p = c.Parts[j];
                if (!Gone(p.B))
                {
                    try { FailBlock(recv, gwt, p.Bi, p.B, p.Where, null, false, ledger: false, split: p.Mode != FractureMath.Crushed); }
                    catch (Exception fe) { _goneBlocks.Add(p.B); Log.Default?.Info($"[AERO-STRUCT] fail skipped: {fe.Message}"); }
                    if (!Gone(p.B)) c.Survivors.Add(p.B);
                }
                CrackReleases++;
                // (smoke along the crack: at its first release, then evenly to the hinge)
                if (c.Smoked < s && (long)j * Math.Max(s - 1, 1) >= (long)c.Smoked * (n - 1)) { c.Smoked++; TearSmoke.Publish(Entity, p.Where.at, c.Cut.Dir); }
            }
        }
        if (c.Next < c.Parts.Count) return false;
        FinishCrack(c);
        return true;
    }

    /// <summary>A crack's last member gone: the cut's verify (from now - a crumb split off mid-crack is not its separation)
    /// and the hold (NO ZIPPER: _stBreakHold, from the blocks' version before the last step).</summary>
    void FinishCrack(Crack c)
    {
        var cut = c.Cut;
        if (c.Verify != null) { c.Verify.Members = new List<CubeBlockComponent>(c.Survivors); c.Verify.Stamp = System.Threading.Volatile.Read(ref _splitStamp); _cutVerify.Add(c.Verify); }
        _stBreakHold = StructureLoads.BreakHoldMax; _stHoldVersion = c.Version0;
        CracksDone++;
        StructureLoads.LastCut = CrackLast = $"{Entity?.DebugName}: the cut at {cut.At} (axis {cut.Axis}) failed whole - {c.Parts.Count} of {cut.Count} members cracked across it over {c.Clock:F2} s ({c.Torn} torn, {c.Hinges} hinge, {c.Crushed} crushed; {cut.Steps} off the plane, {cut.Stubs?.Length ?? 0} stubs; worst utilisation {cut.Util:F2})";
        Log.Default?.Info($"[AERO-STRUCT] {CrackLast}");
    }
}
