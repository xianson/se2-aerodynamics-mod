#pragma warning disable
using System;
namespace AeroMod;

// THE PUBLIC API, VERSION 7: the structure block by block, its failures as a polled event queue, the hull's heat block
// by block and face by face, and another heat model's temperatures in place of aero's (the future thermal mod: aero
// publishes the heat input, the thermal mod owns the temperatures, aero's own bucket is the fallback).
// Published by the structure's background evaluation (Structure/Server/StructureServer.Api.cs) - a few times a second, for
// the grids a player flies (SetFocusGrid) - while someone reads them; copied out under the grid's API lock.
public static partial class AeroApi
{
    // ── the layout and the counters ──

    /// <summary>(version 7) The grid's structure counters. layout: the block order every per-block call uses - it moves
    /// when the structure is rebuilt (blocks added, removed, broken off): read GetStructureBlocks again then, and
    /// SetBlockTemperatures needs the current one. evaluation: bumped by each evaluation published to the API (a few a
    /// second while the grid is flown and you read it). blockCount: the blocks in this layout. externalHeat: the last
    /// evaluation ran on your temperatures (SetBlockTemperatures), not aero's. False: no aero here, or not evaluated since
    /// you first asked (the structure is evaluated only for a grid a player flies - SetFocusGrid - with Structure.Loads on).
    /// Any thread.</summary>
    public static bool TryGetStructureCounters(Keen.VRage.DCS.Components.Entity grid, out int layout, out int evaluation, out int blockCount, out bool externalHeat)
    {
        layout = evaluation = blockCount = 0; externalHeat = false;
        var s = Watch(grid, ApiGridState.RecStruct);
        if (s == null) return false;
        lock (s.Lock)
        {
            layout = s.Layout; evaluation = s.Evaluation; blockCount = s.BlockCount; externalHeat = s.TempsExternal;
            return s.Layout > 0;
        }
    }

    /// <summary>(version 7) The structure's blocks in layout order: each block's entity (null for one with none), its
    /// centre (grid-local m) and mass (kg, as the structure counts it). Arrays may be null; fills min(length, count) of
    /// each. Returns the count (0: no structure yet); layout: the layout they are of. Any thread; no allocation.</summary>
    public static int GetStructureBlocks(Keen.VRage.DCS.Components.Entity grid, Keen.VRage.DCS.Components.Entity[] blocks, Vector3[] centresLocal, float[] massKg, out int layout)
    {
        layout = 0;
        var s = Watch(grid, ApiGridState.RecStruct);
        if (s == null) return 0;
        lock (s.Lock)
        {
            layout = s.Layout;
            int n = s.BlockCount;
            if (blocks != null) Array.Copy(s.Blocks, blocks, Math.Min(n, blocks.Length));
            if (centresLocal != null) Array.Copy(s.Centres, centresLocal, Math.Min(n, centresLocal.Length));
            if (massKg != null) Array.Copy(s.Mass, massKg, Math.Min(n, massKg.Length));
            return n;
        }
    }

    /// <summary>(version 7) Every block's state at the last evaluation, in layout order: utilisation (its worst joint's load
    /// over its strength now - 1 yield; heat and damage in), health (0..1: the game's, less fatigue not yet written to the
    /// block), temperature (K, mass-weighted: what its strength reads), hottest skin (K: what burns it), heat in from the
    /// air (W, all its hull faces; negative: the air cools it). Arrays may be null; fills min(length, count). Returns the
    /// count (0: none published for the current layout yet); layout: the layout they are of. Any thread; no allocation.</summary>
    public static int GetStructureBlockState(Keen.VRage.DCS.Components.Entity grid, float[] utilisation, float[] health, float[] tempK, float[] skinMaxK, float[] heatInW, out int layout)
    {
        layout = 0;
        var s = Watch(grid, ApiGridState.RecStruct);
        if (s == null) return 0;
        lock (s.Lock)
        {
            layout = s.Layout;
            if (s.StateLayout != s.Layout) return 0;
            int n = s.BlockCount;
            if (utilisation != null) Array.Copy(s.Util, utilisation, Math.Min(n, utilisation.Length));
            if (health != null) Array.Copy(s.Health, health, Math.Min(n, health.Length));
            if (tempK != null) Array.Copy(s.TempK, tempK, Math.Min(n, tempK.Length));
            if (skinMaxK != null) Array.Copy(s.SkinK, skinMaxK, Math.Min(n, skinMaxK.Length));
            if (heatInW != null) Array.Copy(s.HeatW, heatInW, Math.Min(n, heatInW.Length));
            return n;
        }
    }

    /// <summary>(version 7) Every block's external load at the last evaluation, in layout order (grid-local): force (N) and
    /// moment about its centre (N m) - the air (hull and wings), thrust, gyros, contacts and a railed entry's braking as the
    /// structure spread them; the ship's own inertia (its acceleration and turning) is not in it. Arrays may be null.
    /// Returns the count (0: none for the current layout yet). Any thread; no allocation.</summary>
    public static int GetStructureBlockLoads(Keen.VRage.DCS.Components.Entity grid, Vector3[] forceLocal, Vector3[] momentLocal, out int layout)
    {
        layout = 0;
        var s = Watch(grid, ApiGridState.RecStruct);
        if (s == null) return 0;
        lock (s.Lock)
        {
            layout = s.Layout;
            if (s.StateLayout != s.Layout) return 0;
            int n = s.BlockCount;
            if (forceLocal != null) Array.Copy(s.LoadF, forceLocal, Math.Min(n, forceLocal.Length));
            if (momentLocal != null) Array.Copy(s.LoadM, momentLocal, Math.Min(n, momentLocal.Length));
            return n;
        }
    }

    // ── the hull's heat faces ──

    /// <summary>(version 7) The hull's heat faces (the faces seen from outside, on the structure's blocks): centre and
    /// outward normal (grid-local), area (m2), the block each is on (layout index; -1 none). They change only with the
    /// layout. Reading this (or GetHeatFaceState) publishes the faces each evaluation for 5 s - big ships have 10^5: keep
    /// your arrays. Arrays may be null; fills min(length, count). Returns the count (0: not published yet - the next
    /// evaluation; or no structure). Any thread; no allocation.</summary>
    public static int GetHeatFaces(Keen.VRage.DCS.Components.Entity grid, Vector3[] centresLocal, Vector3[] normalsLocal, float[] areas, int[] blockIndex, out int layout)
    {
        layout = 0;
        var s = Watch(grid, ApiGridState.RecFaces);
        if (s == null) return 0;
        lock (s.Lock)
        {
            layout = s.Layout;
            if (s.FaceLayout != s.Layout) return 0;
            int n = s.FaceCount;
            if (centresLocal != null) Array.Copy(s.FacePos, centresLocal, Math.Min(n, centresLocal.Length));
            if (normalsLocal != null) Array.Copy(s.FaceNrm, normalsLocal, Math.Min(n, normalsLocal.Length));
            if (areas != null) Array.Copy(s.FaceArea, areas, Math.Min(n, areas.Length));
            if (blockIndex != null) Array.Copy(s.FaceBlock, blockIndex, Math.Min(n, blockIndex.Length));
            return n;
        }
    }

    /// <summary>(version 7) THE AIR'S HEAT ON EACH FACE at the last evaluation, in GetHeatFaces' order - the boundary
    /// condition for a heat model of your own: heat in (W, at aero's own wall temperature), aero's skin temperature (K),
    /// and the film coefficient h (W/m2 K); recoveryK: the flow's recovery temperature T0 (K). At your own wall temperature
    /// Tw a face takes h x area x (T0 - Tw) W. Arrays may be null; fills min(length, count). Returns the count (0: not
    /// published yet). Any thread; no allocation.</summary>
    public static int GetHeatFaceState(Keen.VRage.DCS.Components.Entity grid, float[] heatInW, float[] skinK, float[] filmCoefficient, out float recoveryK, out int layout)
    {
        recoveryK = 0f; layout = 0;
        var s = Watch(grid, ApiGridState.RecFaces);
        if (s == null) return 0;
        lock (s.Lock)
        {
            layout = s.Layout;
            if (s.FaceLayout != s.Layout) return 0;
            int n = s.FaceCount;
            recoveryK = s.RecoveryK;
            if (heatInW != null) Array.Copy(s.FaceHeatW, heatInW, Math.Min(n, heatInW.Length));
            if (skinK != null) Array.Copy(s.FaceSkinK, skinK, Math.Min(n, skinK.Length));
            if (filmCoefficient != null) Array.Copy(s.FaceFilm, filmCoefficient, Math.Min(n, filmCoefficient.Length));
            return n;
        }
    }

    // ── another heat model's temperatures ──

    /// <summary>(version 7) YOUR HEAT MODEL'S TEMPERATURES in place of aero's, for one evaluation: every block's temperature
    /// (K, mass-weighted - its strength softens with it) and hottest skin (K - burns it past its burn temperature, with
    /// Heat.Damage; the heat gauge), in the order of the given layout (TryGetStructureCounters). skinMaxK null: the same as
    /// tempK. Set again each evaluation (a few times a second) - left 2 s, aero's own temperatures come back. Aero's own
    /// hull model keeps running (it is the fallback, and the heat input GetHeatFaceState publishes); the scorch marks and
    /// the max skin gauge (TryGetStructure's maxSkinK) still follow aero's own faces. False: no aero, a layout that is not
    /// the current one (read the blocks again), or arrays shorter than the block count. Any thread; copies the arrays.</summary>
    public static bool SetBlockTemperatures(Keen.VRage.DCS.Components.Entity grid, int layout, float[] tempK, float[] skinMaxK)
    {
        if (tempK == null) return false;
        var s = Watch(grid, ApiGridState.RecStruct);
        if (s == null) return false;
        lock (s.Lock)
        {
            int n = s.BlockCount;
            if (layout != s.Layout || layout <= 0 || tempK.Length < n || (skinMaxK != null && skinMaxK.Length < n)) return false;
            for (int i = 0; i < n; i++) if (!float.IsFinite(tempK[i]) || (skinMaxK != null && !float.IsFinite(skinMaxK[i]))) return false;
            s.ExtTempK = ApiGridState.Fit(s.ExtTempK, n); s.ExtSkinK = ApiGridState.Fit(s.ExtSkinK, n);
            Array.Copy(tempK, s.ExtTempK, n);
            Array.Copy(skinMaxK ?? tempK, s.ExtSkinK, n);
            s.ExtLayout = layout; s.ExtStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            // (kept while they apply, then dropped like a record: AeroGridComponent.ApiRelease, 2026-10-15 review)
            s.Push(ApiGridState.RecExt, s.ExtStamp + (long)(AeroGridComponent.ExternalHeatHold * System.Diagnostics.Stopwatch.Frequency));
            s.Held[ApiGridState.RecExt] = true;
            return true;
        }
    }

    /// <summary>(version 7) Aero's own temperatures again at once (as if SetBlockTemperatures had not been called for 2 s).
    /// False: no aero here. Any thread.</summary>
    public static bool ClearBlockTemperatures(Keen.VRage.DCS.Components.Entity grid)
    {
        var aero = AeroOf(grid);
        var s = aero?.ApiState;
        if (aero == null) return false;
        if (s != null) lock (s.Lock) s.ExtLayout = -1;
        return true;
    }

    // ── the structure's failures: a polled event queue ──

    /// <summary>Event kinds (ReadStructureEvents): a block failing from its load (value: its utilisation), a block burnt
    /// through (value: its hottest skin, K), a whole cut (section) failing together (value: its member count; no block).</summary>
    internal const int EventBlockFailed = 1, EventBlockBurnt = 2, EventCutFailed = 3;

    internal struct StructEvent { public int Kind; public Keen.VRage.DCS.Components.Entity Grid, Block; public Vector3 At, Dir; public float Value; public long Stamp; }
    /// <summary>The newest 512 failures of every grid (an evaluation decides a cut's members together: a big one is many).</summary>
    static readonly SeqRing<StructEvent> _events = new SeqRing<StructEvent>(512);

    /// <summary>Recording runs while someone polls the queue (GetStructureEventSequence / ReadStructureEvents) - from a call
    /// until EventsIdleSeconds after the last one: otherwise nothing is kept - the ring would hold up to 512 destroyed grids
    /// and blocks alive for nobody (2026-10-15 review; and once asked it stayed on for the session, into the next world).
    /// Unkept failures still take their numbers (SeqRing.Skip): a reader back after a pause sees the gap.</summary>
    static readonly ReaderGate _eventsWanted = new ReaderGate();
    /// <summary>A reader silent this long (s) stops the recording, the ring dropped (the next call starts it again).</summary>
    internal const float EventsIdleSeconds = 10f;

    internal static void RecordStructureEvent(int kind, Keen.VRage.DCS.Components.Entity grid, Keen.VRage.DCS.Components.Entity block, Vector3 at, Vector3 dir, float value)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_eventsWanted.Open(now, (long)(EventsIdleSeconds * System.Diagnostics.Stopwatch.Frequency), out bool closed))
        {
            if (closed) _events.Clear();   // (nobody polling any more: its entities let go)
            _events.Skip();
            return;
        }
        _events.Add(new StructEvent { Kind = kind, Grid = grid, Block = block, At = at, Dir = dir, Value = value, Stamp = now });
    }

    /// <summary>A world gone (its last settings carrier: AeroWorldSettingsComponent) or a new session: recording off, the
    /// ring's entities let go - it held the last world's destroyed grids and blocks into the next (2026-10-15 review). The
    /// numbers go on: a reader's cursor stays valid.</summary>
    internal static void ResetStructureEvents() { _eventsWanted.Close(); _events.Clear(); }

    /// <summary>(version 7) The structure event queue's next sequence number (= every failure so far, kept or not, every
    /// grid; it only grows). Read it once to start from now. Any thread.</summary>
    public static long GetStructureEventSequence() { _eventsWanted.Touch(System.Diagnostics.Stopwatch.GetTimestamp()); return _events.Next; }

    /// <summary>(version 7) THE STRUCTURE'S FAILURES since <paramref name="fromSequence"/>, oldest first, across every grid -
    /// as each evaluation decides them (the game removes the blocks on the next simulation tick; one already gone is
    /// skipped there): per event its sequence number, kind (1 a block failed from its load, 2 a block burnt through, 3 a
    /// whole cut failed - its members follow as kind 1), grid, block (null for a cut), point (grid-local m: the failing
    /// joint, the block's middle, the cut's centre), direction (grid-local: the way into what goes; zero for heat) and value
    /// (1: utilisation; 2: hottest skin K; 3: member count). The newest 512 are kept: a first sequence above your
    /// fromSequence is events you missed. Arrays may be null; copies up to the shortest non-null length. Returns the count;
    /// nextSequence: pass it as fromSequence next time. Any thread; no allocation.</summary>
    public static int ReadStructureEvents(long fromSequence, long[] sequences, int[] kinds, Keen.VRage.DCS.Components.Entity[] grids, Keen.VRage.DCS.Components.Entity[] blocks,
        Vector3[] pointsLocal, Vector3[] directionsLocal, float[] values, out long nextSequence)
    {
        _eventsWanted.Touch(System.Diagnostics.Stopwatch.GetTimestamp());
        int max = int.MaxValue;
        if (sequences != null) max = Math.Min(max, sequences.Length); if (kinds != null) max = Math.Min(max, kinds.Length);
        if (grids != null) max = Math.Min(max, grids.Length); if (blocks != null) max = Math.Min(max, blocks.Length);
        if (pointsLocal != null) max = Math.Min(max, pointsLocal.Length); if (directionsLocal != null) max = Math.Min(max, directionsLocal.Length);
        if (values != null) max = Math.Min(max, values.Length);
        if (max == int.MaxValue) { nextSequence = _events.Next; return 0; }   // (no arrays: skip to now)
        int n = 0;
        lock (_events.Lock)
        {
            long seq = Math.Max(fromSequence, _events.First), end = _events.NextUnlocked;
            for (; seq < end && n < max; seq++, n++)
            {
                ref readonly var e = ref _events.At(seq);
                if (sequences != null) sequences[n] = seq;
                if (kinds != null) kinds[n] = e.Kind;
                if (grids != null) grids[n] = e.Grid;
                if (blocks != null) blocks[n] = e.Block;
                if (pointsLocal != null) pointsLocal[n] = e.At;
                if (directionsLocal != null) directionsLocal[n] = e.Dir;
                if (values != null) values[n] = e.Value;
            }
            nextSequence = seq;
        }
        return n;
    }
}
