#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
    /// <summary>Diagnostics: failed cuts' members split at their failing face that still held on across it (destroyed whole).</summary>
    public static int SplitsHeld;

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
    /// the ledger's part was done at the commit); split: off for a CRUSHED member (FractureMath.Crushed: it goes whole);
    /// sever: a failed cut's member - its split must cut it loose across its failing face (MountedAcross), else it goes whole.</summary>
    void FailBlock(GridDamageReceiverComponent recv, Keen.VRage.Core.WorldTransform gwt, int bi, CubeBlockComponent b, (Vector3 at, Vector3 dir, float util) where, List<Vector3> torn, bool fx = true, bool ledger = true, bool split = true, bool sever = false)
    {
        if (ledger) _stHealth.TakeAll(bi);
        if (SplitBlocks && split)
        {
            int killed = 0;
            try { killed = SplitFace(b, where.at, where.dir); }
            catch (Exception e) { SplitLast = "split failed: " + (e.InnerException ?? e).Message; Log.Default?.Info($"[AERO-STRUCT] {SplitLast}"); }
            // (A CUT'S MEMBER LETS GO, 2026-10-15, the third integration run - CutsForced +2 on the SSTO pull: two
            //  ControlSurface_Small members "split at its failing face (3 pieces)" stayed on, the game never split the wing off,
            //  and the watch destroyed them and the 3 armour blocks mounted to the first's face 0.5 s later. The game disables
            //  only the mount groups of DEAD pieces (BreakableBlockComponent.IsMountPointEnabled) and a block holds on by any
            //  enabled one (CubeBlockComponent.CanConnectBlock) - a few pieces at the joint leave the rest of that face's
            //  groups mounted. Still mounted across its failing face: destroyed whole now, as armour is)
            if (killed > 0 && sever && CutWatch.Sever && !Gone(b) && MountedAcross(b, where.dir))
            {
                SplitsHeld++; SplitPieces += killed;
                Log.Default?.Info($"[AERO-STRUCT] {Entity?.DebugName}: {BlockName(b)} split at its failing face ({killed} pieces) but still mounted across it - destroyed whole");
                killed = 0;
            }
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
    // (its split count, ticks and stage: CutWatch - the baseline the cut's failure, a crack's START, 2026-10-15)
    sealed class CutVerify : CutWatch
    {
        public List<CubeBlockComponent> Members; public int Axis; public Vector3 At; public float Plane; public Vector3I Lo, Hi;
        /// <summary>(JAGGED, 2026-10-15) the member blocks the jagged line left standing on the ship and the way out's sign
        /// along the axis (the stage, CutWatch.Stage: 0 - the jagged surface, stubs spared; 1 - the plane's rule, stubs and all).
        /// A set (from _stubSets): Bridges / BeyondStub test it per cell - a List.Contains there was cells x stubs, ~8 M
        /// compares a split stamp for a 100-stub cut on a 60 x 60 section, on the simulation thread (2026-10-15 review).</summary>
        public HashSet<CubeBlockComponent> Stubs; public int Side;
        /// <summary>(2026-10-17) blocks just beyond the broken line and just behind it - both sides' still in this grid:
        /// joined (CutJoined, CutWatch.Joined); null or empty: unknown.</summary>
        public List<CubeBlockComponent> Far, Near;
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
            if (v.SplitSince(stamp))
            {
                // (a split: THIS cut separated only if no two blocks of this grid still meet face to face across its plane - any
                //  split of the grid dropped every watched cut, one still bridged elsewhere never forced apart, 2026-10-15. A
                //  block straddling the plane alone does not count here: a member split at its face stays whole in the octree).
                //  JUDGED ON THE JAGGED SURFACE IN EITHER STAGE (2026-10-15, the third integration run: stage 0 forced the cut,
                //  the wing split off 23 ms later, and stage 1 judged that split on the plane's rule - the jagged line's stubs
                //  and their mates, on the ship by design, "4 block(s) still bridging it" - destroyed 0.5 s after it had parted;
                //  CutWatch.SpareOnSplit). THE SEEDS FIRST (2026-10-17): both sides still in this grid - joined, whatever the
                //  box's bridges say (CutWatch.Credit)
                int j = _octree == null ? -1 : CutJoined(v);
                if (_octree == null || CutWatch.Credit(j < 0 ? Bridges(v, null, split: true) : 0, j)) { _cutVerify.RemoveAt(i); DropVerify(v); continue; }
            }
            if (!v.Expired()) continue;
            _cutVerify.RemoveAt(i);
            recv ??= Entity?.TryGet<GridDamageReceiverComponent>();
            if (recv == null || _octree == null) { DropVerify(v); continue; }
            int joined = CutJoined(v);
            if (v.GiveUp(joined))
            {
                CutsGivenUp++; CutsChecked++;
                Log.Default?.Info($"[AERO-STRUCT] {Entity?.DebugName}: the cut at {v.At} still holds after {v.Rounds} forcing rounds - given up");
                DropVerify(v); continue;
            }
            // (seeds known: forced only while joined, any stage; unknown - stage 1's plane's rule only on a cut that never split
            //  or still bridges on the jagged surface: CutWatch.Force)
            if (!v.Force(joined < 0 && v.Stage > 0 ? Bridges(v, null, split: true) : 0, joined)) { CutsChecked++; DropVerify(v); continue; }
            // the bridges: the members split rather than destroyed, and every block still TOUCHING ACROSS the cut's plane in
            // its cross-section (cells k | k+1 along its axis - whatever the structure counted as members, these still join
            // the two sides: the second cut of a test failed 29 of 29 members and still did not split, 2026-10-15)
            var bridge = new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance);
            foreach (var b in v.Members) { if (!Gone(b)) bridge.Add(b); else Resolve(b, bridge); }
            // (JAGGED, 2026-10-15: first the jagged surface - its stubs spared on the plane, and what still holds a stub from
            //  beyond it taken; only if that does not part it either, the plane's rule with the stubs - stage 1. Seeds known:
            //  the jagged surface until the last round - the holders below find the rest)
            Bridges(v, bridge, split: joined >= 0 && !v.Escalate);
            int n = Destroy(recv, bridge), h = 0;
            // (STILL JOINED, 2026-10-17: what holds the far side anywhere - the octree walked from the far seeds, the far end of
            //  every joint into the ship's side destroyed; the seeds moved where the walk went, for the next look)
            if (joined > 0)
            {
                bridge.Clear();
                _holders ??= new OctreeHolders(this);
                _holders.V = v; _holders.Touch = v.Escalate;
                h = _holders.Find(v.Far, bridge, HoldersMaxBlocks);
                CutHolderBlocks += h;
                Reseed(v, _holders, bridge);
                n += Destroy(recv, bridge);
                _holders.V = null;
            }
            CutsChecked++;
            if (n > 0) { CutsForced++; Log.Default?.Info($"[AERO-STRUCT] {Entity?.DebugName}: the cut at {v.At} did not separate - {n} block(s) still bridging it, destroyed{(h > 0 ? $" ({h} holding its far side, found off the cut's section)" : "")}"); }
            else if (joined > 0) Log.Default?.Info($"[AERO-STRUCT] {Entity?.DebugName}: the cut at {v.At} still holds but nothing found to break (round {v.Rounds + 1}) - watched again");
            // (the jagged pass done: watched once more, on the plane's rule, should it still not split; seeds known: until the
            //  seeds say apart, or given up loudly - CutWatch.Again)
            if (v.Again(stamp, CutVerifyTicks, joined)) _cutVerify.Add(v);
            else DropVerify(v);
        }
    }
    /// <summary>Diagnostics: cuts still joined after CutWatch.MaxRounds forcings (logged); blocks destroyed as holders found
    /// by the octree walk; stale members (left with a piece) looked up again by their cells.</summary>
    public static int CutsGivenUp, CutHolderBlocks, CutStale;
    /// <summary>The most far-side blocks one holders' walk looks at.</summary>
    public static int HoldersMaxBlocks = 4096;
    OctreeHolders _holders;

    /// <summary>A watched cut's blocks destroyed - only what is still this grid's (Gone: a block that left with a piece was
    /// "Data not found" to the game, 2026-10-17). Returns how many.</summary>
    int Destroy(GridDamageReceiverComponent recv, HashSet<CubeBlockComponent> set)
    {
        int n = 0;
        foreach (var b in set)
        {
            if (Gone(b)) continue;
            try { recv.DealDamage(b, float.MaxValue); _goneBlocks.Add(b); n++; }
            catch (Exception e) { _goneBlocks.Add(b); Log.Default?.Info($"[AERO-STRUCT] bridge skipped: {e.Message}"); }
        }
        return n;
    }

    /// <summary>A member no longer in this grid's octree but not destroyed (left with a piece - a stale reference): what is in
    /// its cells in this grid now, if anything (into: added) - the bridge looked up again, never the stale one damaged.</summary>
    void Resolve(CubeBlockComponent b, HashSet<CubeBlockComponent> into)
    {
        if (b == null || _octree == null || _goneBlocks.Contains(b) || b.Entity == null || !PhysicsHack.Alive(b.Entity.Data)) return;
        var bb = b.AABB;
        if ((long)(bb.Max.X - bb.Min.X + 1) * (bb.Max.Y - bb.Min.Y + 1) * (bb.Max.Z - bb.Min.Z + 1) > 4096) return;
        CutStale++;
        for (int x = bb.Min.X; x <= bb.Max.X; x++) for (int y = bb.Min.Y; y <= bb.Max.Y; y++) for (int z = bb.Min.Z; z <= bb.Max.Z; z++)
        {
            var o = _octree.TryGetCubeBlock(new Vector3I(x, y, z));
            if (o != null && !ReferenceEquals(o, b) && !Gone(o)) into.Add(o);
        }
    }

    /// <summary>(2026-10-17) the seeds' verdict (CutWatch.Joined): a far seed and a near seed both still this grid's - the game
    /// keeps only joined blocks in one grid; -1 no seeds.</summary>
    int CutJoined(CutVerify v)
    {
        if (v.Far == null || v.Near == null || v.Far.Count == 0 || v.Near.Count == 0) return -1;
        bool far = false, near = false;
        foreach (var b in v.Far) if (!Gone(b)) { far = true; break; }
        foreach (var b in v.Near) if (!Gone(b)) { near = true; break; }
        return CutWatch.Joined(true, far, near);
    }

    /// <summary>A watched cut's side of a block: beyond its plane (the block's centre on the way out), and not a stub.</summary>
    static bool CutLeaves(CutVerify v, CubeBlockComponent b)
    {
        var bb = b.AABB;
        float c = (ShipStructure.Ci(bb.Min, v.Axis) + ShipStructure.Ci(bb.Max, v.Axis)) * 0.5f * TearMath.Cell;
        return (c - v.Plane) * v.Side > 0f && (v.Stubs == null || !v.Stubs.Contains(b));
    }

    /// <summary>(MakeVerify) the seeds: each broken block's neighbours straight out of it (far: leaving) and straight back
    /// (near: staying), not broken, not stubs - at most SeedMax a side.</summary>
    void Seed(CutVerify v, HashSet<CubeBlockComponent> broken)
    {
        if (_octree == null) return;
        var far = new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance); var near = new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance);
        int ax = v.Axis, u = (ax + 1) % 3, w = (ax + 2) % 3;
        foreach (var e in broken)
        {
            var bb = e.AABB;
            int u0 = ShipStructure.Ci(bb.Min, u), u1 = ShipStructure.Ci(bb.Max, u), w0 = ShipStructure.Ci(bb.Min, w), w1 = ShipStructure.Ci(bb.Max, w);
            if ((long)(u1 - u0 + 1) * (w1 - w0 + 1) > 1024) continue;
            int kOut = v.Side > 0 ? ShipStructure.Ci(bb.Max, ax) + 1 : ShipStructure.Ci(bb.Min, ax) - 1;
            int kIn = v.Side > 0 ? ShipStructure.Ci(bb.Min, ax) - 1 : ShipStructure.Ci(bb.Max, ax) + 1;
            for (int a = u0; a <= u1; a++) for (int c = w0; c <= w1; c++)
                for (int sd = 0; sd < 2; sd++)
                {
                    var set = sd == 0 ? far : near;
                    if (set.Count >= SeedMax) continue;
                    var o = _octree.TryGetCubeBlock(Cell(ax, sd == 0 ? kOut : kIn, u, a, c));
                    if (o == null || broken.Contains(o) || (v.Stubs != null && v.Stubs.Contains(o)) || Gone(o) || CutLeaves(v, o) != (sd == 0)) continue;
                    set.Add(o);
                }
        }
        v.Far = new List<CubeBlockComponent>(far); v.Near = new List<CubeBlockComponent>(near);
    }
    /// <summary>The most seeds a side.</summary>
    public static int SeedMax = 12;

    /// <summary>(a forcing round, still joined) the seeds moved where the holders' walk went: far - blocks it reached that
    /// are not being destroyed now; near - what the holders held on to. Kept as they were where it found none.</summary>
    void Reseed(CutVerify v, OctreeHolders hw, HashSet<CubeBlockComponent> holders)
    {
        List<CubeBlockComponent> far = null, near = null;
        foreach (var b in hw.Reached) { if (holders.Contains(b) || Gone(b)) continue; (far ??= new()).Add(b); if (far.Count >= SeedMax) break; }
        foreach (var b in hw.NearHit) { if (Gone(b)) continue; (near ??= new()).Add(b); if (near.Count >= SeedMax) break; }
        if (far != null) v.Far = far;
        if (near != null) v.Near = near;
    }

    /// <summary>CutHolders on this grid's octree: a block's joined neighbours - across each of its cells' faces, live, mounted
    /// as the game joins them (Mated) or, the last round, touching.</summary>
    sealed class OctreeHolders : CutHolders<CubeBlockComponent>
    {
        readonly AeroGridComponent _g;
        public CutVerify V; public bool Touch;
        /// <summary>The ship's-side blocks the walk's holders held on to (since the last Clear: Reseed reads them).</summary>
        public readonly HashSet<CubeBlockComponent> NearHit = new(ReferenceEqualityComparer.Instance);
        readonly HashSet<CubeBlockComponent> _added = new(ReferenceEqualityComparer.Instance);
        public OctreeHolders(AeroGridComponent g) { _g = g; }
        protected override void Started() => NearHit.Clear();
        protected override bool Leaves(CubeBlockComponent b) => CutLeaves(V, b);
        protected override void Joined(CubeBlockComponent b, List<CubeBlockComponent> into)
        {
            var oct = _g._octree;
            if (oct == null || _g.Gone(b)) return;
            var bb = b.AABB;
            if ((long)(bb.Max.X - bb.Min.X + 1) * (bb.Max.Y - bb.Min.Y + 1) * (bb.Max.Z - bb.Min.Z + 1) > 4096) return;
            _added.Clear();
            for (int x = bb.Min.X; x <= bb.Max.X; x++) for (int y = bb.Min.Y; y <= bb.Max.Y; y++) for (int z = bb.Min.Z; z <= bb.Max.Z; z++)
            {
                var c = new Vector3I(x, y, z);
                if (!ReferenceEquals(oct.TryGetCubeBlock(c), b)) continue;
                for (int f = 0; f < 6; f++)
                {
                    int fa = f >> 1, sg = (f & 1) == 0 ? 1 : -1;
                    var nc = c; if (fa == 0) nc.X += sg; else if (fa == 1) nc.Y += sg; else nc.Z += sg;
                    var o = oct.TryGetCubeBlock(nc);
                    if (o == null || ReferenceEquals(o, b) || _added.Contains(o) || _g.Gone(o)) continue;
                    if (!Touch && !Mated(b, o, fa, sg)) continue;
                    _added.Add(o); into.Add(o);
                    if (!CutLeaves(V, o)) NearHit.Add(o);
                }
            }
        }
    }
    public static int CutsChecked;
    /// <summary>(JAGGED) a watched cut's rule spares the jagged line's stubs: its stage's (stage 0; stage 1 - the plane's
    /// rule, stubs and all - for forcing), or a split's (either stage: CutWatch.SpareOnSplit).</summary>
    static bool Spare(CutVerify v, bool split) => (split ? v.SpareOnSplit : v.Spare) && v.Stubs != null && v.Stubs.Count > 0;

    /// <summary>(JAGGED) the blocks holding a stub from beyond the plane: across any of its faces (its outer face - the way out
    /// - and its sides: 2026-10-15), each neighbour cell beyond the plane whose block is not gone, not a stub and mounted to it
    /// (Mated) - what the jagged step should have broken (into: added; null: only counted). Returns how many cells hold it.</summary>
    int BeyondStub(CubeBlockComponent s, CutVerify v, int k0, HashSet<CubeBlockComponent> into)
    {
        if (Gone(s)) return 0;
        int ax = v.Axis;
        var bb = s.AABB;
        if ((long)(bb.Max.X - bb.Min.X + 1) * (bb.Max.Y - bb.Min.Y + 1) * (bb.Max.Z - bb.Min.Z + 1) > 4096) return 0;
        int held = 0;
        for (int x = bb.Min.X; x <= bb.Max.X; x++) for (int y = bb.Min.Y; y <= bb.Max.Y; y++) for (int z = bb.Min.Z; z <= bb.Max.Z; z++)
        {
            var c = new Vector3I(x, y, z);
            if (!ReferenceEquals(_octree.TryGetCubeBlock(c), s)) continue;
            for (int f = 0; f < 6; f++)
            {
                int fa = f >> 1, sg = (f & 1) == 0 ? 1 : -1;
                var nc = c; if (fa == 0) nc.X += sg; else if (fa == 1) nc.Y += sg; else nc.Z += sg;
                int kn = ShipStructure.Ci(nc, ax);
                if (v.Side > 0 ? kn <= k0 : kn > k0) continue;   // (on the ship's side of the plane)
                var o = _octree.TryGetCubeBlock(nc);
                if (o == null || ReferenceEquals(o, s) || Gone(o) || v.Stubs.Contains(o) || !Mated(s, o, fa, sg)) continue;
                held++; into?.Add(o);
            }
        }
        return held;
    }

    /// <summary>The blocks of this grid still joined across a watched cut's plane in its cross-section (into: added; null:
    /// only counted). Returns the pairs of two blocks face to face AND MOUNTED found (Mated - an armour block against a tank's
    /// side touches it and holds nothing: the game never joins them, so they never kept a cut together; 2026-10-15); a block
    /// straddling the plane is added, not counted. (JAGGED - Spare: pairs with a stub of the jagged line are not bridges - a
    /// stub stays on the ship by design - but what holds a stub from beyond the plane is: BeyondStub.) split: a split's rule.</summary>
    int Bridges(CutVerify v, HashSet<CubeBlockComponent> into, bool split = false)
    {
        int ax = v.Axis, k0 = (int)MathF.Round(v.Plane / TearMath.Cell - 0.5f);
        int u = (ax + 1) % 3, w = (ax + 2) % 3;
        int uLo = ShipStructure.Ci(v.Lo, u), uHi = ShipStructure.Ci(v.Hi, u), wLo = ShipStructure.Ci(v.Lo, w), wHi = ShipStructure.Ci(v.Hi, w);
        if ((long)(uHi - uLo + 1) * (wHi - wLo + 1) > 40000) return 0;
        bool spare = Spare(v, split);
        int pairs = 0;
        for (int a = uLo; a <= uHi; a++) for (int c = wLo; c <= wHi; c++)
        {
            var c0 = Cell(ax, k0, u, a, c); var c1 = Cell(ax, k0 + 1, u, a, c);
            var b0 = _octree.TryGetCubeBlock(c0); var b1 = _octree.TryGetCubeBlock(c1);
            if (b0 == null || b1 == null || Gone(b0) || Gone(b1)) continue;
            if (spare && (v.Stubs.Contains(b0) || v.Stubs.Contains(b1))) continue;
            if (ReferenceEquals(b0, b1)) { into?.Add(b0); continue; }   // (one block across the plane)
            if (!Mated(b0, b1, ax, 1)) continue;   // (touching, not mounted: never joined)
            pairs++;
            if (into != null) { into.Add(b0); into.Add(b1); }   // (two blocks face to face across it)
        }
        if (spare) foreach (var sb in v.Stubs) pairs += BeyondStub(sb, v, k0, into);
        return pairs;
    }

    /// <summary>Two blocks face to face, a's face looking along axis ax (sign sg) at b's, joined AS THE GAME JOINS THEM: each
    /// one's enabled mount points that way reaching the other's box (CubeBlockComponent.CanConnectBlock - the octree's own
    /// test, BlockOctreeComponent.TryConnectNodes; a dead fracture piece's mount groups off, BreakableBlockComponent).</summary>
    static bool Mated(CubeBlockComponent a, CubeBlockComponent b, int ax, int sg)
    {
        var d = Base6Directions.GetDirection(new Vector3I(ax == 0 ? sg : 0, ax == 1 ? sg : 0, ax == 2 ? sg : 0));
        return a.CanConnectBlock(b.AABB, d) && b.CanConnectBlock(a.AABB, Base6Directions.GetOppositeDirection(d));
    }

    /// <summary>A failed cut's member, split at its failing face (FailBlock, sever): still mounted to a block across a face
    /// looking the way its failing joint does (out of it: against dir, the way in), by any of its cells (Mated). No octree:
    /// not known (false - the cut's watch is the net).</summary>
    bool MountedAcross(CubeBlockComponent b, Vector3 dir)
    {
        if (_octree == null) return false;
        float ux = MathF.Abs(dir.X), uy = MathF.Abs(dir.Y), uz = MathF.Abs(dir.Z);
        int ax = ux >= uy && ux >= uz ? 0 : uy >= uz ? 1 : 2;
        int sg = (ax == 0 ? dir.X : ax == 1 ? dir.Y : dir.Z) > 0f ? -1 : 1;
        var bb = b.AABB;
        if ((long)(bb.Max.X - bb.Min.X + 1) * (bb.Max.Y - bb.Min.Y + 1) * (bb.Max.Z - bb.Min.Z + 1) > 4096) return false;
        for (int x = bb.Min.X; x <= bb.Max.X; x++) for (int y = bb.Min.Y; y <= bb.Max.Y; y++) for (int z = bb.Min.Z; z <= bb.Max.Z; z++)
        {
            var c = new Vector3I(x, y, z);
            if (!ReferenceEquals(_octree.TryGetCubeBlock(c), b)) continue;
            var nc = c; if (ax == 0) nc.X += sg; else if (ax == 1) nc.Y += sg; else nc.Z += sg;
            var o = _octree.TryGetCubeBlock(nc);
            if (o == null || ReferenceEquals(o, b) || Gone(o)) continue;
            if (Mated(b, o, ax, sg)) return true;
        }
        return false;
    }

    /// <summary>The cell k along axis ax, a along u, c along the third (no array a cell: it made one per cell, 2026-10-15).</summary>
    static Vector3I Cell(int ax, int k, int u, int a, int c)
        => new Vector3I(ax == 0 ? k : u == 0 ? a : c, ax == 1 ? k : u == 1 ? a : c, ax == 2 ? k : u == 2 ? a : c);

    void CutDone(StructureEval.CutFail cut, ref int failed, List<Vector3> torn, int version0)
    {
        int n = failed; failed = 0;
        int goneEarly = _cutGoneEarly; _cutGoneEarly = 0;
        // (THE CRACK, 2026-10-15: its members queued, not failed - it runs from here, StartCrack)
        if (_crackOpen != null && ReferenceEquals(_crackOpen.Cut, cut)) { var c = _crackOpen; _crackOpen = null; c.GoneBefore = goneEarly; _cutSurvivors.Clear(); StartCrack(c, version0); return; }
        if (n == 0) { _cutSurvivors.Clear(); return; }   // (every member already gone: an old cut seen again)
        // (every failed cut watched - it must separate; CheckCutSeparated): its members' cross-section (cells, on the two axes
        //  across the cut, one cell wider all round) and plane, and the members that were split rather than destroyed
        {
            var v = MakeVerify(cut);
            if (v != null) { v.Members = new List<CubeBlockComponent>(_cutSurvivors); v.Begin(System.Threading.Volatile.Read(ref _splitStamp)); v.Finish(v.Stamp, CutVerifyTicks); _cutVerify.Add(v); }
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
        var v = new CutVerify { Ticks = CutVerifyTicks, At = cut.At, Axis = cut.Axis, Plane = cut.Plane, Lo = lo - Vector3I.One, Hi = hi + Vector3I.One,
            Stubs = stubs, HasStubs = stubs != null && stubs.Count > 0, Side = side > 0f ? 1 : -1 };
        // (the seeds, 2026-10-17: around the cut's broken blocks - CutJoined)
        var broken = new HashSet<CubeBlockComponent>(ReferenceEqualityComparer.Instance);
        for (int k = cut.First; k < cut.First + cut.Count && k < _stDamage.Count; k++)
        {
            int bi = _stDamage[k].block;
            if (bi >= 0 && bi < _stBlocks.Count && _stBlocks[bi] != null) broken.Add(_stBlocks[bi]);
        }
        Seed(v, broken);
        return v;
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
    public static int CracksStarted, CracksDone, CrackReleases, Crumples, CracksRunning, CracksAbandoned;
    /// <summary>Cracks whose grid split before their last member let go (the piece parted mid-crack; their verify credits it).</summary>
    public static int CracksSplitDuring;
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
        /// <summary>Its members already gone when it was applied (split away or destroyed since the evaluation - never
        /// queued: the "N of M members" gap, 2026-10-17).</summary>
        public int GoneBefore;
        /// <summary>The grid's split count at the crack's start (its verify's baseline: CutWatch).</summary>
        public int Stamp0;
        public readonly List<CubeBlockComponent> Survivors = new();
    }
    readonly List<Crack> _cracks = new();
    /// <summary>(ApplyStructureDamage) the cut whose members are being queued now; its members found gone so far.</summary>
    Crack _crackOpen;
    int _cutGoneEarly;

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

    /// <summary>(CutDone) a queued crack started: its verify measured now - its baseline the split count now, before any
    /// member lets go (CutWatch: the split may come before its last member, if that one no longer bridged) - its parts in
    /// release order, the tear's springs recorded now (every member's place and its order - kept through the crack), the
    /// first released now.</summary>
    void StartCrack(Crack c, int version0)
    {
        c.Verify = MakeVerify(c.Cut);
        c.Stamp0 = System.Threading.Volatile.Read(ref _splitStamp);
        c.Verify?.Begin(c.Stamp0);
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
            // (split since the crack started: the parts not yet let go may have left with the piece - InOctree)
            bool split = System.Threading.Volatile.Read(ref _splitStamp) != c.Stamp0;
            while (c.Next < n && c.Parts[c.Next].At <= c.Clock + 1e-4f)
            {
                int j = c.Next++;
                var p = c.Parts[j];
                if (split && !Gone(p.B) && !InOctree(p.B)) _goneBlocks.Add(p.B);
                if (!Gone(p.B))
                {
                    try { FailBlock(recv, gwt, p.Bi, p.B, p.Where, null, false, ledger: false, split: p.Mode != FractureMath.Crushed, sever: true); }
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

    /// <summary>A block still in this grid's octree (any of its cells). A split takes its blocks out of the octree at once
    /// (BlockOctreeComponent.SplitDisconnectedBlocks) but their entities are only marked for close - alive, Grid still this
    /// one, Gone blind to them - and a crack's hinge let go after its piece had split away hit one: 'Killing dead Entity'
    /// (2026-10-15, the integration run). After a split mid-crack (StepCrack), and in every Gone (2026-10-17).</summary>
    bool InOctree(CubeBlockComponent b)
    {
        if (_octree == null) return false;
        var bb = b.AABB;
        if ((long)(bb.Max.X - bb.Min.X + 1) * (bb.Max.Y - bb.Min.Y + 1) * (bb.Max.Z - bb.Min.Z + 1) > 4096) return true;   // (huge: assumed)
        for (int x = bb.Min.X; x <= bb.Max.X; x++) for (int y = bb.Min.Y; y <= bb.Max.Y; y++) for (int z = bb.Min.Z; z <= bb.Max.Z; z++)
            if (ReferenceEquals(_octree.TryGetCubeBlock(new Vector3I(x, y, z)), b)) return true;
        return false;
    }

    /// <summary>(TickStructure) Structure.Loads switched off mid-crack (or the grid without its octree): the running cracks
    /// dropped - the members not yet let go stay (their wear taken), no hold, no verify - and the watched cuts dropped. A
    /// crack frozen there held the structure's damage off until loads came back, then let its parts go, minutes stale; a
    /// watched cut forced its bridges apart as stale (2026-10-15 review). Off is off: nothing more breaks.</summary>
    void AbandonCracks()
    {
        foreach (var c in _cracks)
        {
            CracksAbandoned++;
            CrackLast = $"{Entity?.DebugName}: crack at {c.Cut.At} abandoned (structure loads off) - {c.Next} of {c.Parts.Count} members let go";
            if (c.Verify != null) DropVerify(c.Verify);
        }
        if (_cracks.Count > 0) Log.Default?.Info($"[AERO-STRUCT] {CrackLast}");
        _cracks.Clear(); CracksRunning = 0; _crackOpen = null;
        foreach (var v in _cutVerify) DropVerify(v);
        _cutVerify.Clear();
    }

    /// <summary>A crack's last member gone: the cut's verify (from now - a crumb split off mid-crack is not its separation)
    /// and the hold (NO ZIPPER: _stBreakHold, from the blocks' version before the last step).</summary>
    void FinishCrack(Crack c)
    {
        var cut = c.Cut;
        if (c.Verify != null)
        {
            // (the baseline kept from the crack's start - its split mid-crack is its separation, if nothing bridges: CutWatch)
            c.Verify.Members = new List<CubeBlockComponent>(c.Survivors); c.Verify.Finish(System.Threading.Volatile.Read(ref _splitStamp), CutVerifyTicks); _cutVerify.Add(c.Verify);
            if (c.Verify.SplitDuring) CracksSplitDuring++;
        }
        _stBreakHold = StructureLoads.BreakHoldMax; _stHoldVersion = c.Version0;
        CracksDone++;
        StructureLoads.LastCut = CrackLast = $"{Entity?.DebugName}: the cut at {cut.At} (axis {cut.Axis}) failed whole - {c.Parts.Count} of {cut.Count} members cracked across it over {c.Clock:F2} s ({c.Torn} torn, {c.Hinges} hinge, {c.Crushed} crushed; {cut.Steps} off the plane, {cut.Stubs?.Length ?? 0} stubs; {(c.GoneBefore > 0 ? $"{c.GoneBefore} already gone; " : "")}worst utilisation {cut.Util:F2})";
        Log.Default?.Info($"[AERO-STRUCT] {CrackLast}");
    }
}
