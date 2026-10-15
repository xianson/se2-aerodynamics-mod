#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// AN ORGANIC BREAK (2026-10-15, the user: "our uniform break is maybe a little too linear?") - the game-free part
/// (planned in StructureEval.DamageBySection; the game side: Structure/Server/StructureServer.Failure.cs - the crack's staged
/// release - and StructureTear - the springs; offline-tested in Tests/StructureTests 'TensionCompression', 'Jagged' and Tests/TearTests).
/// WHEN a cut fails is unchanged (DamageBySection: whole, on one solve - the user disliked a fracture "creeping along the
/// span" and wanted the weak neck "uniformly crumpling"); this is HOW it lets go:
///  - THE CRACK RUNS ACROSS THE SECTION: its members let go one after another over CrackSeconds - the most loaded torn
///    fibre first, the crack running through the section, the pushed side last: those are the HINGE (Plan). The cut stays
///    committed: the structure's damage is held while it runs (no re-decision, no zipper along the span);
///  - TENSION TEARS, COMPRESSION CRUMPLES (2026-10-15, the user: "add compression vs tension to the break work"): a member
///    on the pushed side of its section (ShipStructure.PieceSide) crumples - dented at once, and the hinge,
///    released last - unless it is CRUSHED (its compression past CrushUtil): then it goes whole, by its utilisation, like a
///    torn one (ModeOf);
///  - A JAGGED LINE: each member breaks at the cut's plane or one block either side (JagCut: the block inside it - the
///    member's own block then leaves with the piece; or the block beyond it - the member's block then stays on the ship, a
///    stub), chosen from its own stress (the most loaded stay on the plane) and a hash of the grid and the member's cell
///    (Step: deterministic). Every step is checked: the jagged surface must part the piece wherever the flat cut did - no
///    contact may join what leaves to what stays unless the flat cut left it too - and a member whose step would leave
///    such a bridge is put back on the plane.
/// </summary>
public static class FractureMath
{
    /// <summary>The crack runs across a failed cut over CrackSeconds (off: every member in the same tick, as before).</summary>
    public static bool Crack = true;
    /// <summary>Seconds from a cut's first member letting go to its last (the hinge).</summary>
    public static float CrackSeconds = 0.3f;
    /// <summary>The fracture line steps a block either side of the cut's plane (off: on the plane, ruler-straight).</summary>
    public static bool Jagged = true;
    /// <summary>The chance a cut's least loaded member steps off its plane (the most loaded: JaggedShare x (1 - JaggedStress)).</summary>
    public static float JaggedShare = 0.4f;
    /// <summary>How much a member's own utilisation (over its cut's worst) keeps it on the plane: 0 - every member alike;
    /// 1 - the worst never steps.</summary>
    public static float JaggedStress = 0.75f;
    /// <summary>A step goes only into a block of the member's own material class and at most this many times its mass (never
    /// through a cockpit, nor the fuselage's big block a wing root sits on).</summary>
    public static float JaggedMassRatio = 1.5f;
    /// <summary>A compression member is CRUSHED - it goes whole, by its utilisation, instead of crumpling as the hinge - at
    /// this compressive utilisation (ShipStructure.PieceCompression, against its strength now).</summary>
    public static float CrushUtil = 2f;
    /// <summary>THE HINGE STAYS: a hinge member (compression, not crushed) of a cut that also tears breaks at the block beyond
    /// it when it can (a +1 step, through the separation check) - its own block, crumpled, stays on the ship as a stub, the
    /// fold visible where the piece swung away (off: the hinge steps like any member).</summary>
    public static bool HingeStays = true;

    /// <summary>How a failed cut's entry lets go (StructureEval.CutFail.Mode): torn (tension or shear governs - the crack's
    /// origin), the hinge (compression governs - crumples, last), crushed (compression past CrushUtil - goes whole), a tie
    /// (trim across the plane - with its nearest member).</summary>
    public const byte Torn = 0, Hinge = 1, Crushed = 2, Tie = 3;

    // diagnostics (harness aeroget AeroMod.FractureMath.X)
    /// <summary>Cuts planned; members stepped off the plane, stubs left standing, steps put back (a bridge), hinges, crushed.</summary>
    public static int Planned, Steps, StubsLeft, Repairs, Hinges, CrushedMembers;
    /// <summary>The separation check's passes in the last JagCut (each rebuilds the sets and walks the changed blocks).</summary>
    public static int LastPasses;
    /// <summary>The separation check's passes before a cut falls back to the flat line (each pass puts back the stepped
    /// members at every bridge it found that no other repair of the pass touched; real cuts need 2-3).</summary>
    public static int MaxRepairPasses = 6;

    /// <summary>A member's mode from its tension and compression utilisations (PieceTension, PieceCompression) alone: the
    /// governing one's.</summary>
    public static byte ModeOf(float ut, float uc) => !(uc > ut) ? Torn : uc >= CrushUtil ? Crushed : Hinge;
    /// <summary>A member's mode by its SIDE of the section (ShipStructure.PieceSide - not its governing utilisation: a thin
    /// wall's buckling makes nearly every member compression-governed, even on the pulled side): the pulled side tears, the
    /// pushed side is the hinge - crushed past CrushUtil; neither (no bending, no axial) by its governing utilisation.</summary>
    public static byte ModeOf(int side, float ut, float uc) => side > 0 ? Torn : side < 0 ? (uc >= CrushUtil ? Crushed : Hinge) : ModeOf(ut, uc);
    /// <summary>A section piece's mode (ModeOf by its side, tension and compression - the last solve).</summary>
    public static byte ModeOf(ShipStructure st, int mi) => ModeOf(st.PieceSide(mi), st.PieceTension(mi), st.PieceCompression(mi));

    // ── the noise ──
    /// <summary>A well-mixed hash of a seed and a cell (murmur3's finaliser over the four words).</summary>
    public static uint Hash(int seed, int x, int y, int z)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u;
            h ^= (uint)x * 0x85EBCA77u; h = (h << 13) | (h >> 19); h *= 0xC2B2AE3Du;
            h ^= (uint)y * 0x27D4EB2Fu; h = (h << 13) | (h >> 19); h *= 0x85EBCA6Bu;
            h ^= (uint)z * 0x165667B1u; h = (h << 13) | (h >> 19); h *= 0xC2B2AE35u;
            h ^= h >> 16; h *= 0x85EBCA6Bu; h ^= h >> 13; h *= 0xC2B2AE35u; h ^= h >> 16;
            return h;
        }
    }
    /// <summary>A hash as 0..1 (its high 24 bits).</summary>
    public static float Unit(uint h) => (h >> 8) * (1f / 16777216f);

    /// <summary>A member's step off the plane (-1 the block inside, 0 on it, +1 the block beyond): it steps with chance
    /// JaggedShare x (1 - JaggedStress x u / uMax) - the most loaded stay on the plane - which way from another bit of the
    /// same hash. Deterministic in (seed, cell).</summary>
    public static int Step(int seed, Vector3I cell, float u, float uMax)
    {
        float share = Math.Clamp(JaggedShare, 0f, 1f);
        if (!(share > 0f)) return 0;
        float rel = uMax > 0f && u > 0f ? Math.Clamp(u / uMax, 0f, 1f) : 0f;
        float p = share * (1f - Math.Clamp(JaggedStress, 0f, 1f) * rel);
        uint h = Hash(seed, cell.X, cell.Y, cell.Z);
        if (!(Unit(h) < p)) return 0;
        return (h & 1u) != 0 ? 1 : -1;
    }

    // ── the crack's order ──
    /// <summary>
    /// The crack's release times (seconds from the first) for a failed cut's n entries: the members by their key - torn and
    /// crushed ones by their utilisation, the most loaded first; the hinge after them, the least pushed first and the most
    /// pushed last (the extreme compression fibre holds longest) - spread evenly over seconds by rank (equal keys together:
    /// the first rank at 0, the last at seconds). A cut with no torn or crushed member (all pushed, worn out) goes by its
    /// utilisation like a torn one. Ties (Tie) go with their nearest member (at). Monotone: a later key never goes sooner.
    /// </summary>
    public static void Plan(byte[] mode, float[] u, float[] uc, Vector3[] at, int n, float seconds, float[] release)
    {
        if (n <= 0) return;
        seconds = seconds > 0f ? seconds : 0f;
        bool tear = false;
        for (int i = 0; i < n; i++) if (mode[i] == Torn || mode[i] == Crushed) { tear = true; break; }
        var key = new float[n]; var idx = new int[n]; int m = 0;
        for (int i = 0; i < n; i++)
        {
            if (mode[i] == Tie) continue;
            float k = mode[i] == Hinge && tear ? -uc[i] : u[i];
            key[i] = float.IsNaN(k) ? float.MinValue : k;
            idx[m++] = i;
        }
        Schedule(key, idx, m, seconds, release);
        for (int i = 0; i < n; i++)
        {
            if (mode[i] != Tie) continue;
            float best = float.MaxValue, t = 0f;
            for (int j = 0; j < m; j++) { float d = (at[idx[j]] - at[i]).LengthSquared(); if (d < best) { best = d; t = release[idx[j]]; } }
            release[i] = t;
        }
    }

    /// <summary>Release times by key, the highest first: idx[0..m) the entries scheduled (reordered here), each at
    /// seconds x its rank among the distinct keys over (distinct keys - 1); one key: all at 0.</summary>
    public static void Schedule(float[] key, int[] idx, int m, float seconds, float[] release)
    {
        if (m <= 0) return;
        Array.Sort(idx, 0, m, Comparer<int>.Create((a, b) => { int c = key[b].CompareTo(key[a]); return c != 0 ? c : a.CompareTo(b); }));
        int distinct = 0;
        for (int i = 0; i < m; i++) if (i == 0 || key[idx[i]] != key[idx[i - 1]]) distinct++;
        int r = -1;
        for (int i = 0; i < m; i++)
        {
            if (i == 0 || key[idx[i]] != key[idx[i - 1]]) r++;
            release[idx[i]] = distinct > 1 ? seconds * r / (distinct - 1) : 0f;
        }
    }

    // ── the jagged line ──
    /// <summary>JagCut's working set, kept by the caller for an evaluation (each block's neighbours and contacts, built once
    /// per structure).</summary>
    public sealed class JagScratch
    {
        internal ShipStructure For;
        internal int[] AdjStart, AdjOther, BcStart, BcList;
        /// <summary>Per block, the first of the stepped members at it (a chain through Next: member x 2 + 0 its own block, + 1
        /// its step's); -1 none - reset after each pass.</summary>
        internal int[] Head;
        internal readonly HashSet<int> D = new(), D0 = new(), Stub = new(), Look = new(), Moved = new();
        internal readonly List<(int a, int b)> Bad = new();
    }

    static void Ensure(ShipStructure st, JagScratch sc)
    {
        if (ReferenceEquals(sc.For, st) && sc.AdjStart != null) return;
        sc.For = st;
        StructureBend.Adjacency(st, out sc.AdjStart, out sc.AdjOther);
        int n = st.Count, nc = st.ContactCount;
        var start = new int[n + 1];
        for (int k = 0; k < nc; k++) { st.ContactBlocks(k, out int a, out int b); start[a + 1]++; start[b + 1]++; }
        for (int i = 0; i < n; i++) start[i + 1] += start[i];
        var list = new int[start[n]]; var fill = new int[n];
        for (int k = 0; k < nc; k++) { st.ContactBlocks(k, out int a, out int b); list[start[a] + fill[a]++] = k; list[start[b] + fill[b]++] = k; }
        sc.BcStart = start; sc.BcList = list;
        sc.Head = new int[n]; Array.Fill(sc.Head, -1);
    }

    /// <summary>A step from member block o into block t is allowed: another block, of o's own class (not trim), at most
    /// JaggedMassRatio x its mass.</summary>
    static bool Eligible(ShipStructure st, int o, int t) =>
        t >= 0 && t != o && st.Mat[t] == st.Mat[o] && st.Mat[t] != StructMaterial.Trim && st.Mass[t] <= JaggedMassRatio * st.Mass[o];

    /// <summary>
    /// THE JAGGED LINE of section g (its pieces from SectionPieceStart, i = piece - start): for each, the block that breaks
    /// (outBlock), the point its fracture starts at (outAt: the face of that block toward what stays - with the way out, the
    /// line into it) and its step (outStep: -1 the block inside the plane, 0 the member's own - PieceBlock, PiecePoint - +1
    /// the block beyond it); outStubs: member blocks left standing on the ship (a +1 step's). Straddling blocks and the
    /// section's ties stay on the plane. SEPARATION: with the jagged choice D (and the flat one D0 - every member's own block
    /// and the ties), and each block's side (beyond the plane: leaves; a stub stays), no contact may join a block that
    /// leaves to one that stays, neither broken, unless the flat cut left that same bridge (another part of the ship across
    /// the same plane) - only contacts at blocks whose side or break changed can differ, and those are checked; a step that
    /// leaves a bridge is put back on the plane (Repairs), until none does (at worst the flat cut). Returns the members
    /// that stepped.
    /// </summary>
    public static int JagCut(ShipStructure st, int g, int seed, JagScratch sc, int[] outBlock, Vector3[] outAt, int[] outStep, List<int> outStubs)
    {
        int m0 = st.SectionPieceStart(g), n = st.SectionPieceCount(g);
        outStubs?.Clear();
        st.SectionPlane(g, out int ax, out float plane);
        int side = ShipStructure.C(st.SectionOut(g), ax) > 0f ? 1 : -1;
        float uMax = st.SectionUtil(g);
        var inner = new int[n]; var outer = new int[n]; var outerAt = new Vector3[n];
        Ensure(st, sc);
        bool tears = false;
        for (int i = 0; i < n && !tears; i++) if (ModeOf(st, m0 + i) != Hinge) tears = true;
        for (int i = 0; i < n; i++)
        {
            int mi = m0 + i, o = st.PieceBlock(mi);
            outBlock[i] = o; outAt[i] = st.PiecePoint(mi); outStep[i] = 0; inner[i] = outer[i] = -1;
            int k = st.PieceContact(mi);
            if (k < 0) continue;   // (a straddling block: cut across its own middle, on the plane)
            st.ContactBlocks(k, out int a, out int b);
            int ib = a == o ? b : a;
            if (Eligible(st, o, ib)) inner[i] = ib;
            // (the block beyond: o's contact on the same axis past its centre on the way out, across from this member)
            var p = outAt[i]; float oc = ShipStructure.C(st.Center[o], ax), best = float.MaxValue;
            for (int e = sc.BcStart[o]; e < sc.BcStart[o + 1]; e++)
            {
                int kk = sc.BcList[e];
                st.ContactPlane(kk, out int kax, out float kpl);
                if (kax != ax || (kpl - oc) * side <= 0f) continue;
                st.ContactBlocks(kk, out int ca, out int cb);
                int j = ca == o ? cb : ca;
                if ((ShipStructure.C(st.Center[j], ax) - oc) * side <= 0f || !Eligible(st, o, j)) continue;
                var q = st.ContactPoint(kk); var d = q - p; float da = ShipStructure.C(d, ax);
                float lat = d.LengthSquared() - da * da;
                if (lat < best) { best = lat; outer[i] = j; outerAt[i] = q; }
            }
            int s = HingeStays && tears && outer[i] >= 0 && ModeOf(st, mi) == Hinge ? 1 : Step(seed, CellOf(p), st.PieceUtil(mi), uMax);
            if (s < 0 && inner[i] < 0 || s > 0 && outer[i] < 0) s = 0;
            outStep[i] = s;
        }
        // the check: a step that leaves a bridge the flat cut did not goes back on the plane, until none does - every bridge
        // a pass finds whose blocks no repair of the same pass moved (one that did is looked at again next pass: that repair
        // may have closed it), at most MaxRepairPasses passes, then the flat line (2026-10-15 review: one bridge a pass, up to
        // n + 1 passes each rebuilding the sets, was O(n^2) a failed cut - 51 passes for a 120-member plate)
        int nt = st.SectionTieCount(g), maxPasses = Math.Max(MaxRepairPasses, 1);
        int[] next = null;
        for (int iter = 0; iter <= maxPasses; iter++)
        {
            sc.D.Clear(); sc.D0.Clear(); sc.Stub.Clear(); sc.Look.Clear();
            for (int t = 0; t < nt; t++) { int tb = st.SectionTieBlock(g, t); sc.D.Add(tb); sc.D0.Add(tb); }
            for (int i = 0; i < n; i++)
            {
                int o = st.PieceBlock(m0 + i);
                sc.D0.Add(o);
                sc.D.Add(outStep[i] < 0 ? inner[i] : outStep[i] > 0 ? outer[i] : o);
            }
            for (int i = 0; i < n; i++) { int o = st.PieceBlock(m0 + i); if (outStep[i] > 0 && !sc.D.Contains(o)) sc.Stub.Add(o); }
            foreach (int b in sc.D) if (!sc.D0.Contains(b)) sc.Look.Add(b);
            foreach (int b in sc.D0) if (!sc.D.Contains(b)) sc.Look.Add(b);
            foreach (int b in sc.Stub) sc.Look.Add(b);
            sc.Bad.Clear();
            foreach (int a in sc.Look)
            {
                for (int e = sc.AdjStart[a]; e < sc.AdjStart[a + 1]; e++)
                {
                    int b = sc.AdjOther[e];
                    bool jag = !sc.D.Contains(a) && !sc.D.Contains(b) && Leaves(st, a, ax, plane, side, sc.Stub) != Leaves(st, b, ax, plane, side, sc.Stub);
                    if (!jag) continue;
                    bool flat = !sc.D0.Contains(a) && !sc.D0.Contains(b) && Leaves(st, a, ax, plane, side, null) != Leaves(st, b, ax, plane, side, null);
                    if (!flat) { sc.Bad.Add((a, b)); break; }   // (a's first bridge, as one bridge a pass took it)
                }
            }
            LastPasses = iter + 1;
            if (sc.Bad.Count == 0) break;
            // (back on the plane: every stepped member whose blocks touch the bridge; none, or out of passes - all of them)
            bool flatten = iter == maxPasses - 1;
            sc.Moved.Clear();
            // (the stepped members by their two blocks, so a bridge finds its members without a walk over all of them)
            next ??= new int[2 * n];
            for (int i = 0; i < n; i++)
            {
                if (outStep[i] == 0) continue;
                int o = st.PieceBlock(m0 + i), t = outStep[i] < 0 ? inner[i] : outer[i];
                next[2 * i] = sc.Head[o]; sc.Head[o] = 2 * i;
                next[2 * i + 1] = sc.Head[t]; sc.Head[t] = 2 * i + 1;
            }
            foreach (var (bad, bad2) in sc.Bad)
            {
                if (flatten) break;
                if (sc.Moved.Contains(bad) || sc.Moved.Contains(bad2)) continue;   // (next pass)
                Repairs++;
                bool any = false;
                for (int pass = 0; pass < 2; pass++)
                    for (int e = sc.Head[pass == 0 ? bad : bad2]; e >= 0; e = next[e])
                    {
                        int i = e >> 1;
                        if (outStep[i] == 0) continue;
                        int o = st.PieceBlock(m0 + i), t = outStep[i] < 0 ? inner[i] : outer[i];
                        outStep[i] = 0; any = true; sc.Moved.Add(o); sc.Moved.Add(t);
                    }
                if (!any) flatten = true;
            }
            for (int i = 0; i < n; i++) { int o = st.PieceBlock(m0 + i); sc.Head[o] = -1; if (inner[i] >= 0) sc.Head[inner[i]] = -1; if (outer[i] >= 0) sc.Head[outer[i]] = -1; }
            if (flatten) { for (int i = 0; i < n; i++) outStep[i] = 0; }
        }
        // the blocks and their faces
        int steps = 0;
        for (int i = 0; i < n; i++)
        {
            int mi = m0 + i;
            if (outStep[i] == 0) continue;
            steps++;
            if (outStep[i] < 0)
            {
                // (the block inside: its fracture from its face away from the plane - the member's block leaves with it)
                int ib = inner[i]; var p = outAt[i];
                float face = side > 0 ? ShipStructure.C(st.Lo[ib], ax) : ShipStructure.C(st.Hi[ib], ax);
                outBlock[i] = ib; outAt[i] = p + ShipStructure.Axis(ax) * (face - ShipStructure.C(p, ax));
            }
            else { outBlock[i] = outer[i]; outAt[i] = outerAt[i]; }
        }
        if (outStubs != null) foreach (int b in sc.Stub) outStubs.Add(b);
        Planned++; Steps += steps; StubsLeft += sc.Stub.Count;
        return steps;
    }

    /// <summary>A block's side: beyond the plane (its centre on the way out) leaves - unless a stub (it stays).</summary>
    static bool Leaves(ShipStructure st, int b, int ax, float plane, int side, HashSet<int> stubs) =>
        (ShipStructure.C(st.Center[b], ax) - plane) * side > 0f && (stubs == null || !stubs.Contains(b));

    /// <summary>A point's cell (the game's 0.25 m cells, centred at i x Cell).</summary>
    public static Vector3I CellOf(Vector3 p) => new Vector3I((int)MathF.Round(p.X / ShipStructure.Cell), (int)MathF.Round(p.Y / ShipStructure.Cell), (int)MathF.Round(p.Z / ShipStructure.Cell));
}
