#pragma warning disable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AeroMod;

// Pressure recovery on the lee side (BakeRecovery): the depth-buffered columns, their exits and how attached the flow stays.
public partial class DampedShadowedDragModel
{
    /// <summary>How attached the flow leaving a face is: 1 turning away by up to SepDeg, 0 past SepDeg + SepFadeDeg
    /// (s: sin of the angle past parallel).</summary>
    private static float _attSep = float.NaN, _attFade = float.NaN, _att0, _att1;
    internal static float Attached(float s)
    {
        if (_attSep != SepDeg || _attFade != SepFadeDeg)
        {
            _att0 = MathF.Sin(SepDeg * MathF.PI / 180f); _att1 = MathF.Sin((SepDeg + SepFadeDeg) * MathF.PI / 180f);
            _attSep = SepDeg; _attFade = SepFadeDeg;
        }
        float a0 = _att0, a1 = _att1;
        if (s <= a0) return 1f;
        if (s >= a1) return 0f;
        float t = (s - a0) / (a1 - a0); return 1f - t * t * (3f - 2f * t);
    }
    /// <summary>A rear-facing face's Cp (cosA = n . travel, below 0): base suction ramped in by angle (full from 30 degrees
    /// past parallel), none where the flow stays attached (recovery on).</summary>
    internal static float LeeCp(float cosA, float baseCp, float cdBluff)
    {
        float s = Math.Clamp(-cosA, 0f, 1f);
        float ramp = MathF.Min(1f, s * 2f); ramp = ramp * ramp * (3f - 2f * ramp);
        float suction = baseCp * ramp;
        return PressureRecovery ? suction * (1f - Attached(s)) : suction;
    }

    private static float Jump(TableWork work, int kk, float d0, float max)
    {
        if (work.RearI[kk] < 0) return 0f;
        float dd = MathF.Abs(work.RearD[kk] - d0); return dd > max ? 0f : dd;
    }

    /// <summary>Each face's recovered push along the travel (per unit q), into work.Rec - PressureRecovery's columns.</summary>
    private void BakeRecovery(Vector3 v, TableWork work, ReadOnlySpan<float> VA)
    {
        int total = _px.Count;
        if (work.Rec.Length < total) work.Rec = new float[total];
        var rec = work.Rec.AsSpan(0, total); rec.Clear();
        var seed = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var e1 = Vector3.Normalize(Vector3.Cross(v, seed)); var e2 = Vector3.Cross(v, e1);
        const float Pix = 0.5f, inv = 1f / Pix;
        var PX = CollectionsMarshal.AsSpan(_px); var PY = CollectionsMarshal.AsSpan(_py); var PZ = CollectionsMarshal.AsSpan(_pz);
        var NX = CollectionsMarshal.AsSpan(_nx); var NY = CollectionsMarshal.AsSpan(_ny); var NZ = CollectionsMarshal.AsSpan(_nz);
        var OI = CollectionsMarshal.AsSpan(_origIndex); var AR = CollectionsMarshal.AsSpan(_area); var RAW = CollectionsMarshal.AsSpan(_raw);
        var excl = _excluded != null && _excludedVersion == _lastSurfaceVersion ? _excluded : null;
        int exLen = excl?.Count ?? 0;
        // ONCE PER DIRECTION, per face: where it is across the flow (in columns), how far along it, and what it brings -
        // the lattice passes below only bin these. Windward faces the air reaches and every face turned away (the exits:
        // BakeOcclusion leaves those 'unreached' behind the front one) are listed; the rest take no part.
        if (work.WU.Length < total)
        {
            int cap = total + 64;
            work.WU = new float[cap]; work.WWc = new float[cap]; work.WD = new float[cap]; work.WV = new float[cap]; work.WA = new float[cap]; work.WS = new bool[cap];
            work.LU = new float[cap]; work.LW = new float[cap]; work.LD = new float[cap]; work.LV = new float[cap]; work.LG = new int[cap]; work.LS = new bool[cap];
        }
        float[] WU = work.WU, WWc = work.WWc, WD = work.WD, WV = work.WV, WA = work.WA, LU = work.LU, LW = work.LW, LD = work.LD, LV = work.LV;
        int[] LG = work.LG; bool[] WS = work.WS, LS = work.LS;
        int nWin = 0, nLee = 0;
        long tp0 = System.Diagnostics.Stopwatch.GetTimestamp();
        float umin = float.MaxValue, umax = float.MinValue, wmin = float.MaxValue, wmax = float.MinValue;
        float cdBluff = CdBluff, st = Streamlining;
        bool square = SubsonicSquareLaw;
        for (int gi = 0; gi < total; gi++)
        {
            int oi = OI[gi];
            if (oi < 0 || AR[gi] < 1e-6f || (oi < exLen && excl[oi])) continue;
            float cosA = NX[gi] * v.X + NY[gi] * v.Y + NZ[gi] * v.Z;
            bool windward = cosA > GrazingCos;
            if (windward ? VA[gi] < 1e-6f : cosA >= -GrazingCos) continue;
            float px = PX[gi], py = PY[gi], pz = PZ[gi];
            float u = px * e1.X + py * e1.Y + pz * e1.Z, w = px * e2.X + py * e2.Y + pz * e2.Z;
            if (u < umin) umin = u; if (u > umax) umax = u; if (w < wmin) wmin = w; if (w > wmax) wmax = w;
            var rn = RAW[gi]; float rc = rn.X * v.X + rn.Y * v.Y + rn.Z * v.Z;
            float d = px * v.X + py * v.Y + pz * v.Z;
            if (windward)
            {
                float vai = VA[gi];
                float cp = square ? cdBluff * cosA * cosA : cdBluff * cosA;
                if (st > 0f && !square) cp *= 1f - st * (1f - (0.08f + 0.92f * cosA * cosA));
                // (its push against the travel; its area across the flow in Gi's place - the front faces need no index;
                //  Step: a cell face itself facing the flow, a step of the front)
                WU[nWin] = u; WWc[nWin] = w; WD[nWin] = d; WV[nWin] = cp * vai * cosA; WA[nWin] = vai * cosA; WS[nWin] = rc > GrazingCos; nWin++;
            }
            else
                // (its own area across the flow; Step: a cell face itself facing away, a step of the taper)
                { LU[nLee] = u; LW[nLee] = w; LD[nLee] = d; LV[nLee] = AR[gi] * -cosA; LG[nLee] = gi; LS[nLee] = rc < -GrazingCos; nLee++; }
        }
        if (umin > umax || nWin == 0) return;
        float umin0 = MathF.Floor(umin / Pix) * Pix, wmin0 = MathF.Floor(wmin / Pix) * Pix;
        for (int i = 0; i < nWin; i++) { WU[i] = (WU[i] - umin0) * inv; WWc[i] = (WWc[i] - wmin0) * inv; }
        for (int i = 0; i < nLee; i++) { LU[i] = (LU[i] - umin0) * inv; LW[i] = (LW[i] - wmin0) * inv; }
        int W = (int)((umax - umin0) * inv) + 2, H = (int)((wmax - wmin0) * inv) + 2, n = W * H;
        if (work.Front.Length < n)
        {
            work.Front = new float[n]; work.RearD = new float[n]; work.RearI = new int[n]; work.Run = new ulong[n]; work.Step = new ulong[n];
            work.FrontA = new float[n]; work.ExitA = new float[n]; work.FrontD = new float[n]; work.FStep = new ulong[n];
        }
        var front = work.Front.AsSpan(0, n); var rearD = work.RearD.AsSpan(0, n); var rearI = work.RearI.AsSpan(0, n);
        var frontA = work.FrontA.AsSpan(0, n); var exitA = work.ExitA.AsSpan(0, n);
        var frontD = work.FrontD.AsSpan(0, n); var fstep = work.FStep.AsSpan(0, n);
        var run = work.Run.AsSpan(0, n); var step = work.Step.AsSpan(0, n);
        long tp1 = System.Diagnostics.Stopwatch.GetTimestamp();
        int passes = RecoveryPasses >= 4 ? 4 : RecoveryPasses >= 2 ? 2 : 1;
        float wPass = 1f / passes;
        for (int pass = 0; pass < passes; pass++)
        {
            float oU = passes == 4 ? (pass & 1) * 0.5f : pass * 0.5f, oW = passes == 4 ? (pass >> 1 & 1) * 0.5f : pass * 0.5f;
            front.Clear(); frontA.Clear(); exitA.Clear(); fstep.Clear(); run.Clear(); step.Clear();
            rearD.Fill(float.MaxValue); rearI.Fill(-1); frontD.Fill(float.MinValue);
            for (int i = 0; i < nWin; i++)
            {
                int k = (int)(WU[i] + oU) + W * (int)(WWc[i] + oW);
                front[k] += WV[i]; frontA[k] += WA[i];
                if (WD[i] > frontD[k]) frontD[k] = WD[i];
            }
            for (int i = 0; i < nLee; i++)
            {
                // (the exit: the most downstream face turned away from the flow - a side along it is no exit, it took the
                //  box's edge columns' recovery: a flat back read 15% attached)
                int k = (int)(LU[i] + oU) + W * (int)(LW[i] + oW);
                if (LD[i] < rearD[k]) { rearD[k] = LD[i]; rearI[k] = LG[i]; }
            }
            // the front's steps: which 0.25 m depth bins behind the column's front-most face hold a cell face itself facing
            // the flow - two or more: a stepped (rounded) nose or leading edge, one: a flat face
            for (int i = 0; i < nWin; i++)
            {
                if (!WS[i]) continue;
                int k = (int)(WU[i] + oU) + W * (int)(WWc[i] + oW);
                int fb = (int)((frontD[k] - WD[i]) * 4f);
                if ((uint)fb < 64u) fstep[k] |= 1UL << fb;
            }
            // the rear surface's run up the column from its exit: which 0.25 m depth bins upstream of the exit hold a face
            // facing away (a 64-bit mask - 16 m), its steps, and the exit's own area across the flow
            for (int i = 0; i < nLee; i++)
            {
                int k = (int)(LU[i] + oU) + W * (int)(LW[i] + oW);
                int bin = (int)((LD[i] - rearD[k]) * 4f);
                if ((uint)bin >= 64u) continue;
                if (bin == 0) exitA[k] += LV[i];
                run[k] |= 1UL << bin;
                if (LS[i]) step[k] |= 1UL << bin;
            }
            long tc0 = System.Diagnostics.Stopwatch.GetTimestamp();
            ColumnsRecover(v, work, rec, front, rearD, rearI, frontA, exitA, frontD, fstep, run, step, W, H, wPass, pass);
            System.Threading.Interlocked.Add(ref RecTicksColumns, System.Diagnostics.Stopwatch.GetTimestamp() - tc0);
        }
        long tp2 = System.Diagnostics.Stopwatch.GetTimestamp();
        System.Threading.Interlocked.Add(ref RecTicksPre, tp1 - tp0);
        System.Threading.Interlocked.Add(ref RecTicksPasses, tp2 - tp1);
        System.Threading.Interlocked.Increment(ref RecBakes);
        System.Threading.Interlocked.Add(ref RecPixels, (long)n * passes);
    }
    public static long RecTicksPre, RecTicksPasses, RecTicksColumns, RecBakes, RecPixels;   // (the bench's profile of BakeRecovery)

    /// <summary>Each column's verdict (BakeRecovery): what its exit gives back, added to that face.</summary>
    private void ColumnsRecover(Vector3 v, TableWork work, Span<float> rec, Span<float> front, Span<float> rearD, Span<int> rearI,
        Span<float> frontA, Span<float> exitA, Span<float> frontD, Span<ulong> fstep, Span<ulong> run, Span<ulong> step, int W, int H, float wPass, int pass)
    {
        const float Pix = 0.5f, JumpMax = 4f;
        var NX = CollectionsMarshal.AsSpan(_nx); var NY = CollectionsMarshal.AsSpan(_ny); var NZ = CollectionsMarshal.AsSpan(_nz);
        int n = W * H;
        for (int k = 0; k < n; k++)
        {
            float fk = front[k];
            if (fk <= 0f) continue;
            int gi = rearI[k];
            if (gi < 0) { if (RecDebug != null && pass == 0) RecDebug.Add((k % W, k / W, 0f, 0f, -2f, fk, 0f)); continue; }   // (no exit)
            float sFace = -(NX[gi] * v.X + NY[gi] * v.Y + NZ[gi] * v.Z);
            // THE COLUMN'S TWO LOSSES. Its front: a flat face with sharp edges (one step: a box's) separates at its edges
            // and keeps its stagnation pressure - nothing given back; a stepped or sloping front (a nose, a leading edge)
            // is a rounded one, its pressure answered by the suction round its shoulder. Its exit: what is left open there
            // - a blunt end, or a steep one where the flow lets go - drags BaseLoss x q x its area (the measured base
            // pressure), not the whole front's pressure. (All-or-nothing at the exit lost a clean airliner's wing its
            // leading edges' pressure at every one-cell trailing edge: 35 kN.)
            int iu = k % W, iw = k / W;
            bool rounded = !BluntPartial || System.Numerics.BitOperations.PopCount(fstep[k] & 0xFFFFUL) >= 2;
            if (!rounded)
            {
                float fd = frontD[k];
                for (int nb = 0; nb < 4 && !rounded; nb++)
                {
                    int kk = nb == 0 ? (iu > 0 ? k - 1 : -1) : nb == 1 ? (iu < W - 1 ? k + 1 : -1) : nb == 2 ? (iw > 0 ? k - W : -1) : (iw < H - 1 ? k + W : -1);
                    if (kk < 0 || frontD[kk] == float.MinValue) continue;
                    float dd = MathF.Abs(frontD[kk] - fd);
                    if (dd > 0.2f * Pix && dd < JumpMax) rounded = true;
                }
            }
            // the run up from the exit: a staircase ending - steps at two depths or more - is the wedge it draws, its slope
            // from the run (a wing's one-cell trailing edge after a step is a tapered edge, not a blunt one); a taper steps
            // down - smoothing alone tilts a box's sides near its back edge, a 0.5-1 m 'run' with the back its only step
            ulong mR = run[k]; int len = 0, gap = 0;
            for (int b = 0; b < 64 && gap < 2; b++) { if ((mR >> b & 1UL) != 0) { len = b + 1; gap = 0; } else gap++; }
            ulong inRun = len >= 64 ? ~0UL : (1UL << len) - 1;
            bool stair = System.Numerics.BitOperations.PopCount(step[k] & inRun) >= 2;
            float runM = stair ? len * 0.25f : 0f;
            float sStair = Pix / MathF.Sqrt(Pix * Pix + runM * runM);
            if (sFace > FlatExit)
            {
                float openF = stair ? 1f - Attached(sStair) : 1f;
                float rb = rounded && BluntPartial ? MathF.Max(0f, RecoveryFrac * fk - BaseLoss * exitA[k] * openF) : 0f;
                rec[gi] += wPass * rb;
                if (RecDebug != null && pass == 0) RecDebug.Add((iu, iw, rearD[k], sFace, -1f, fk - rb, 0f));
                continue;
            }
            // The exit surface's slope from the exits' depths (a staircase's own faces face mostly backwards whatever slope
            // they make): per axis across the flow, the steepest one-sided depth change to a neighbouring column's exit - a
            // jump past JumpMax is another part of the ship; or within the column (a wing is one or two columns thick).
            float d0 = rearD[k];
            float gu = MathF.Max(iu > 0 ? Jump(work, k - 1, d0, JumpMax) : 0f, iu < W - 1 ? Jump(work, k + 1, d0, JumpMax) : 0f) / Pix;
            float gw = MathF.Max(iw > 0 ? Jump(work, k - W, d0, JumpMax) : 0f, iw < H - 1 ? Jump(work, k + W, d0, JumpMax) : 0f) / Pix;
            float s = 1f / MathF.Sqrt(1f + gu * gu + gw * gw);   // (sin of the exit surface's angle past parallel to the flow)
            if (ColumnRun && sStair < s) s = sStair;
            float att = Attached(s);
            float rcv = !rounded ? 0f : BluntPartial ? MathF.Max(0f, RecoveryFrac * fk - BaseLoss * exitA[k] * (1f - att)) : RecoveryFrac * att * fk;
            rec[gi] += wPass * rcv;
            if (RecDebug != null && pass == 0) RecDebug.Add((iu, iw, d0, sFace, s, fk, runM));
        }
    }
}
