#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
namespace AeroMod;

// THE STRUCTURE FOR THE PUBLIC API (AeroApi version 7: Api/AeroApi.Structure.cs), from the background evaluation
// (StructureServer.EvaluateStructure, one at a time per grid):
//  - ApiHeatOverride, after the hull's heat step: another heat model's block temperatures (AeroApi.SetBlockTemperatures)
//    in place of aero's own, for the strength, the heat damage and the gauges - aero's bucket the fallback;
//  - ApiPublishStructure, at its end: the failures it decided (the event queue, always), and - while someone reads them -
//    every block's utilisation, health, temperatures, heat in and loads, and the hull's heat faces.
public partial class AeroGridComponent
{
    /// <summary>Another heat model's temperatures, older than this (s), are dropped: aero's own again.</summary>
    internal const float ExternalHeatHold = 2f;

    /// <summary>(background) Another heat model's block temperatures in place of aero's (st.TempK - the strength - and the
    /// hottest skins - the heat damage, the heat gauge), when it set them for this structure's layout within
    /// ExternalHeatHold s. Nothing set: nothing done.</summary>
    void ApiHeatOverride(ShipStructure st, HullHeat hh)
    {
        var s = ApiState;
        if (s == null || s.ExtLayout < 0) return;
        lock (s.Lock)
        {
            int n = st.Count;
            bool fresh = System.Diagnostics.Stopwatch.GetTimestamp() - s.ExtStamp < (long)(ExternalHeatHold * System.Diagnostics.Stopwatch.Frequency);
            if (!fresh || s.ExtLayout != s.Layout || !ReferenceEquals(s.LayoutOf, st) || s.ExtTempK.Length < n) { s.TempsExternal = false; return; }
            Array.Copy(s.ExtTempK, st.TempK, n);
            if (hh != null && hh.BlockCount == n) Array.Copy(s.ExtSkinK, hh.BlockSkinMaxK, n);
            s.TempsExternal = true;
        }
    }

    /// <summary>(background, the evaluation's end) The failures this evaluation decided into the API's event queue; and,
    /// while watched, the blocks' and the faces' state. blocks: the block list this structure was built on.</summary>
    void ApiPublishStructure(ShipStructure st, HullHeat hh, List<CubeBlockComponent> blocks)
    {
        if (_stDamage.Count > 0) ApiRecordFailures(st, hh, blocks);
        var s = ApiState;
        if (s == null) return;
        int n = st.Count;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        // a new structure (a rebuild): a new layout, its block list (rare: allocates)
        if (!ReferenceEquals(s.LayoutOf, st))
        {
            var ents = new Keen.VRage.DCS.Components.Entity[n];
            var cen = new Vector3[n]; var mass = new float[n];
            for (int b = 0; b < n; b++) { ents[b] = blocks != null && b < blocks.Count ? blocks[b]?.Entity : null; cen[b] = st.Center[b]; mass[b] = st.Mass[b]; }
            lock (s.Lock) { s.LayoutOf = st; s.Layout++; s.BlockCount = n; s.Blocks = ents; s.Centres = cen; s.Mass = mass; s.TempsExternal = false; }
        }
        if (now >= s.WatchUntil) return;
        // the blocks: each one's worst joint (a contact on it, or its own section's middle), health, temperatures, heat in, loads
        var u = s.UtilB = ApiGridState.Fit(s.UtilB, n);
        if (st.BlockUtil != null && st.BlockUtil.Length >= n) Array.Copy(st.BlockUtil, u, n); else Array.Clear(u, 0, n);
        if (st.ContactUtil != null)
            for (int k = 0; k < st.ContactCount; k++)
            {
                float cu = st.ContactUtil[k];
                st.ContactBlocks(k, out int a, out int b);
                if (a >= 0 && a < n && cu > u[a]) u[a] = cu;
                if (b >= 0 && b < n && cu > u[b]) u[b] = cu;
            }
        s.HealthB = ApiGridState.Fit(s.HealthB, n); Array.Copy(st.Integrity, s.HealthB, n);
        s.TempKB = ApiGridState.Fit(s.TempKB, n); Array.Copy(st.TempK, s.TempKB, n);
        s.SkinKB = ApiGridState.Fit(s.SkinKB, n);
        var heat = s.HeatWB = ApiGridState.Fit(s.HeatWB, n);
        Array.Clear(heat, 0, n);
        bool hhOk = hh != null && hh.BlockCount == n;
        if (hhOk)
        {
            Array.Copy(hh.BlockSkinMaxK, s.SkinKB, n);
            for (int i = 0; i < hh.FaceCount; i++) { int b = hh.FaceBlock[i]; if (b >= 0 && b < n) heat[b] += hh.FaceHeatW[i]; }
        }
        else Array.Copy(st.TempK, s.SkinKB, n);
        s.LoadFB = ApiGridState.Fit(s.LoadFB, n); Array.Copy(st.ExtForce, s.LoadFB, n);
        s.LoadMB = ApiGridState.Fit(s.LoadMB, n); Array.Copy(st.ExtMoment, s.LoadMB, n);
        // the faces, while watched for them (their geometry once per layout)
        bool faces = hhOk && now < s.FacesUntil;
        int nf = faces ? hh.FaceCount : 0;
        if (faces)
        {
            Vector3[] fp = null, fn = null; float[] fa = null; int[] fb = null;
            if (s.FaceLayout != s.Layout)
            {
                fp = new Vector3[nf]; fn = new Vector3[nf]; fa = new float[nf]; fb = new int[nf];
                for (int i = 0; i < nf; i++) { fp[i] = hh.FacePosition(i); fn[i] = hh.FaceNormal(i); fa[i] = hh.FaceArea(i); fb[i] = hh.FaceBlock[i]; }
            }
            s.FaceHeatWB = ApiGridState.Fit(s.FaceHeatWB, nf); Array.Copy(hh.FaceHeatW, s.FaceHeatWB, nf);
            s.FaceSkinKB = ApiGridState.Fit(s.FaceSkinKB, nf); Array.Copy(hh.FaceT, s.FaceSkinKB, nf);
            var film = s.FaceFilmB = ApiGridState.Fit(s.FaceFilmB, nf);
            for (int i = 0; i < nf; i++) film[i] = hh.FaceFilm(i);
            lock (s.Lock)
            {
                if (fp != null) { s.FacePos = fp; s.FaceNrm = fn; s.FaceArea = fa; s.FaceBlock = fb; s.FaceLayout = s.Layout; }
                s.FaceCount = nf; s.RecoveryK = hh.LastRecoveryK; s.FaceEvaluation = s.Evaluation + 1;
                ApiGridState.Swap(ref s.FaceHeatW, ref s.FaceHeatWB); ApiGridState.Swap(ref s.FaceSkinK, ref s.FaceSkinKB); ApiGridState.Swap(ref s.FaceFilm, ref s.FaceFilmB);
            }
        }
        lock (s.Lock)
        {
            ApiGridState.Swap(ref s.Util, ref s.UtilB); ApiGridState.Swap(ref s.Health, ref s.HealthB); ApiGridState.Swap(ref s.TempK, ref s.TempKB);
            ApiGridState.Swap(ref s.SkinK, ref s.SkinKB); ApiGridState.Swap(ref s.HeatW, ref s.HeatWB);
            ApiGridState.Swap(ref s.LoadF, ref s.LoadFB); ApiGridState.Swap(ref s.LoadM, ref s.LoadMB);
            s.Evaluation++; s.StateLayout = s.Layout;
        }
    }

    /// <summary>The failures the evaluation decided (_stDamage's destroys, _stCuts) into the API's event queue: a cut
    /// failing whole, each block failing from its load, each block burnt through (no direction: StructureEval.Damage's
    /// heat entries).</summary>
    void ApiRecordFailures(ShipStructure st, HullHeat hh, List<CubeBlockComponent> blocks)
    {
        var grid = Entity;
        for (int c = 0; c < _stCuts.Count; c++)
        {
            var cut = _stCuts[c];
            AeroApi.RecordStructureEvent(AeroApi.EventCutFailed, grid, null, cut.At, cut.Dir, cut.Count);
        }
        for (int di = 0; di < _stDamage.Count; di++)
        {
            var (bi, fraction, destroy) = _stDamage[di];
            if (!destroy) continue;
            (Vector3 at, Vector3 dir, float util) w = di < _stWhere.Count ? _stWhere[di] : (Vector3.Zero, Vector3.Zero, 0f);
            var block = blocks != null && bi >= 0 && bi < blocks.Count ? blocks[bi]?.Entity : null;
            bool heat = w.dir.LengthSquared() < 0.5f;
            float value = heat ? (hh != null && bi >= 0 && bi < hh.BlockCount ? hh.BlockSkinMaxK[bi] : 0f) : w.util;
            var at = w.at.LengthSquared() > 0f || bi < 0 || bi >= st.Count ? w.at : st.Center[bi];
            AeroApi.RecordStructureEvent(heat ? AeroApi.EventBlockBurnt : AeroApi.EventBlockFailed, grid, block, at, w.dir, value);
        }
    }
}
