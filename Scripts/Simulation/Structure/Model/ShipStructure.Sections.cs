#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// SLICES - the method of sections, the way a hull girder is checked (ShipStructure.Solve, the default).
///
/// Every plane where blocks meet (a contact patch's plane, on each axis) slices the ship. What crosses it is its
/// SECTION: the contact patches in it and the blocks that straddle it (their walls, cut). By equilibrium the load across
/// the slice is exactly the sum of everything on one side - every block's own load (air, thrust, its inertia in the
/// ship's motion) - whatever the blocks' connections: no load paths to guess, a ship missing a block still holds.
///
/// One slice can cut several members: a wing's root and the tailplane's, a pylon and the fuselage under it. The section
/// splits into MEMBERS (material within CutGap of each other), and each block on the far side loads the member nearest
/// it across the plane (its load flows straight over): the wing's blocks the wing root, the pod's the pylon.
///
/// Each member: the resultant of its blocks (force, and moment about its centroid) on its section - every piece of
/// material its real section (ShipStructure.Shape.cs: a patch - the weaker block's material over its rectangles; a
/// straddling block - the cells it has on both sides; a shell's walls round their outline, a lattice's cells solid at its
/// density), second moments by the parallel axis - normal stress at each piece's corners N/A + Mu v/Iuu - Mv u/Ivv, shear V/(A/2) + torque/(2 x enclosed area x
/// wall), von Mises; over the weaker block's yield x StrengthAt(temperature) x Strength. ContactUtil (patches) and
/// BlockUtil (a straddling block across its own middle): 1 is yield.
/// </summary>
public sealed partial class ShipStructure
{
    // planes
    int _planeCount;
    int[] _plAxis, _plSplit, _plGroupStart, _plGroupCount;
    float[] _plCoord;
    // members (groups) of each plane
    float[] _gA, _gIuu, _gIvv, _gTor, _gCu, _gCv, _gU0, _gU1, _gV0, _gV1;
    Vector3[] _gCen;   // (SliceStresses' scratch: a plane's sections' centres)
    int[] _gMemStart, _gMemCount;
    // their pieces of material: outline, what it is (>= 0 a contact, < 0 -(block + 1) straddling), and its plates for
    // buckling (thickness, width: a contact's two blocks', a straddling block's own twice)
    float[] _mU0, _mU1, _mV0, _mV1;
    int[] _mWhat;
    float[] _mTa, _mWa, _mTb, _mWb;
    // blocks sorted along each axis by centre
    int[][] _byAxis;
    float[][] _sortedCoord;

    /// <summary>A straddling block's utilisation across its own middle (Solve).</summary>
    public float[] BlockUtil;
    // (DAMAGE BY SECTION, 2026-10-15: each plane's lighter side, each contact's section, each piece's own utilisation and each
    //  section's worst - what StructureEval.Damage wears and fails a whole cut by)
    bool[] _plPlusLight;
    int[] _gPlane, _contactSection;
    float[] _mUtil, _gUtil;
    // (TENSION VS COMPRESSION, 2026-10-15, the user: "add compression vs tension to the break work": each piece's normal
    //  stress kept by its sign - its worst pulled corner against the yield, its worst pushed corner against what its plates
    //  carry buckled - besides the combined utilisation; PieceTension / PieceCompression)
    float[] _mUt, _mUc;
    /// <summary>(the fold's pivot, 2026-10-15) each piece's normal stress at its four corners (Pa; 4 x piece + corner: bit 0 u, bit 1 v).</summary>
    float[] _mS4;
    // (each section's TIES: trim crossing its plane - detailing carries no load, so it is no member, but it still joins the
    //  two sides: a cut that failed without them left the piece hanging on its detailing, and the fracture went on to the
    //  next cut out - the Tu-134's left wing over three cuts, 2026-10-15. Each the trim block, and its place on the plane)
    int[] _gTieStart, _gTieCount, _tieBlock;
    Vector3[] _tiePoint;
    /// <summary>The worst piece of material: a contact (MaxContact) or a block (MaxBlock), -1 the other.</summary>
    public int MaxBlock = -1;
    public int SliceCount => _planeCount;

    void BuildSections()
    {
        int n = Count, nc = _contacts.Count;
        _byAxis = new int[3][]; _sortedCoord = new float[3][];
        for (int ax = 0; ax < 3; ax++)
        {
            var idx = new int[n]; for (int i = 0; i < n; i++) idx[i] = i;
            int a = ax;
            Array.Sort(idx, (p, q) => C(Center[p], a).CompareTo(C(Center[q], a)));
            _byAxis[ax] = idx;
            var co = new float[n]; for (int i = 0; i < n; i++) co[i] = C(Center[idx[i]], ax);
            _sortedCoord[ax] = co;
        }
        // the planes: every contact's (axis, coordinate)
        var planeOf = new Dictionary<long, int>();
        var plAxis = new List<int>(); var plCoord = new List<float>(); var plContacts = new List<List<int>>();
        for (int k = 0; k < nc; k++)
        {
            var c = _contacts[k];
            long key = (long)MathF.Round(c.Plane / Cell * 2f) * 3 + c.Axis;
            if (!planeOf.TryGetValue(key, out int pi)) { planeOf[key] = pi = plAxis.Count; plAxis.Add(c.Axis); plCoord.Add(c.Plane); plContacts.Add(new List<int>()); }
            plContacts[pi].Add(k);
        }
        _planeCount = plAxis.Count;
        // each contact's departing block: its plane's lighter side (by the blocks' masses, centre order)
        ContactOutboard = new int[nc];
        var preMass = new double[3][];
        for (int ax = 0; ax < 3; ax++) { var pmass = preMass[ax] = new double[n + 1]; for (int k = 0; k < n; k++) pmass[k + 1] = pmass[k] + Mass[_byAxis[ax][k]]; }
        _plAxis = plAxis.ToArray(); _plCoord = plCoord.ToArray();
        _plSplit = new int[_planeCount]; _plGroupStart = new int[_planeCount]; _plGroupCount = new int[_planeCount]; _plPlusLight = new bool[_planeCount];
        var gPlane = new List<int>();
        var gTs = new List<int>(); var gTc = new List<int>(); var tieBlock = new List<int>(); var tiePoint = new List<Vector3>();
        var tieU = new List<float>(); var tieV = new List<float>(); var tieB = new List<int>();
        var gA = new List<float>(); var gIuu = new List<float>(); var gIvv = new List<float>(); var gTor = new List<float>();
        var gCu = new List<float>(); var gCv = new List<float>(); var gU0 = new List<float>(); var gU1 = new List<float>(); var gV0 = new List<float>(); var gV1 = new List<float>();
        var gMs = new List<int>(); var gMc = new List<int>();
        var mU0 = new List<float>(); var mU1 = new List<float>(); var mV0 = new List<float>(); var mV1 = new List<float>(); var mWhat = new List<int>();
        var mTa = new List<float>(); var mWa = new List<float>(); var mTb = new List<float>(); var mWb = new List<float>();
        var cut = new List<Rect>();
        // (each plane's pieces: reused plane to plane)
        var pu0 = new List<float>(); var pu1 = new List<float>(); var pv0 = new List<float>(); var pv1 = new List<float>(); var pw = new List<int>(); var pt = new List<float>();
        var pA = new List<float>(); var pcu = new List<float>(); var pcv = new List<float>(); var piu = new List<float>(); var piv = new List<float>();
        var pTa = new List<float>(); var pWa = new List<float>(); var pTb = new List<float>(); var pWb = new List<float>();
        const float Eps = 1e-3f;
        _plAssign = new int[_planeCount][];
        // (CONNECT, per plane, without per-plane arrays: the summed side's blocks stamped with the plane, their union-find
        //  reset only where touched, its unions along each block's own contacts - not over every block and contact)
        var farStamp = new int[n]; var bu = new int[n]; var farList = new List<int>(); var strad = new List<int>();
        var compPiece = new Dictionary<int, int>(); _compPiece = compPiece;
        var bcStart = new int[n + 1];
        foreach (var c in _contacts) { bcStart[c.A + 1]++; bcStart[c.B + 1]++; }
        for (int i = 0; i < n; i++) bcStart[i + 1] += bcStart[i];
        var bcOther = new int[bcStart[n]]; var bcFill = new int[n];
        foreach (var c in _contacts) { bcOther[bcStart[c.A] + bcFill[c.A]++] = c.B; bcOther[bcStart[c.B] + bcFill[c.B]++] = c.A; }
        for (int pl = 0; pl < _planeCount; pl++)
        {
            int ax = _plAxis[pl], u = (ax + 1) % 3, v = (ax + 2) % 3;
            float x = _plCoord[pl];
            // the split: blocks with centre beyond the plane
            _plSplit[pl] = LowerBound(_sortedCoord[ax], x);
            double plus = preMass[ax][n] - preMass[ax][_plSplit[pl]];
            bool plusLight = plus <= 0.5 * preMass[ax][n];
            _plPlusLight[pl] = plusLight;
            foreach (int k in plContacts[pl])
            {
                var c = _contacts[k];
                int plusBlock = c.SideB > 0 ? c.B : c.A, minusBlock = c.SideB > 0 ? c.A : c.B;
                ContactOutboard[k] = plusLight ? plusBlock : minusBlock;
            }
            // the material crossing: the plane's patches, and blocks straddling it - each its real section (steel, centroid,
            // second moments, shear wall: ShipStructure.Shape.cs) and its outline
            int pli = (int)MathF.Round(x / Cell - 0.5f);
            pu0.Clear(); pu1.Clear(); pv0.Clear(); pv1.Clear(); pw.Clear(); pt.Clear(); pA.Clear(); pcu.Clear(); pcv.Clear(); piu.Clear(); piv.Clear();
            pTa.Clear(); pWa.Clear(); pTb.Clear(); pWb.Clear();
            tieU.Clear(); tieV.Clear(); tieB.Clear();
            foreach (int k in plContacts[pl])
            {
                var c = _contacts[k];
                if (Mat[c.A] == StructMaterial.Trim || Mat[c.B] == StructMaterial.Trim)
                {
                    // (a tie: its trim block - the departing one if both are)
                    int tb = Mat[c.A] != StructMaterial.Trim ? c.B : Mat[c.B] != StructMaterial.Trim ? c.A : ContactOutboard[k];
                    tieU.Add((c.U0 + c.U1) * 0.5f); tieV.Add((c.V0 + c.V1) * 0.5f); tieB.Add(tb);
                    continue;
                }
                pu0.Add(c.U0); pu1.Add(c.U1); pv0.Add(c.V0); pv1.Add(c.V1); pw.Add(k); pt.Add(_cT[k]);
                pA.Add(_cA[k]); pcu.Add(_cCu[k]); pcv.Add(_cCv[k]); piu.Add(_cIuu[k]); piv.Add(_cIvv[k]);
                pTa.Add(_cTa[k]); pWa.Add(_cWa[k]); pTb.Add(_cTb[k]); pWb.Add(_cWb[k]);
            }
            strad.Clear();
            for (int i = 0; i < n; i++)
                if (C(Lo[i], ax) < x - Eps && C(Hi[i], ax) > x + Eps && StraddleMark(strad, i) && TieMark(i, u, v, tieU, tieV, tieB))
                {
                    // (its own cells across the plane - none: its parts meet elsewhere, nothing of it crosses here)
                    cut.Clear(); CutCells(i, ax, pli, cut);
                    if (cut.Count == 0) continue;
                    int r0 = int.MaxValue, r1 = int.MinValue, s0 = int.MaxValue, s1 = int.MinValue;
                    foreach (var r in cut) { r0 = Math.Min(r0, r.U0); r1 = Math.Max(r1, r.U1); s0 = Math.Min(s0, r.V0); s1 = Math.Max(s1, r.V1); }
                    Material(i, cut, 0, cut.Count, out float a, out float cu0, out float cv0, out float iu, out float iv, out float t);
                    // (its plate: a lattice's depth or a shell's wall, over the cut's own width)
                    float T = Thick[i] > 0f ? Thick[i] : Wall[i], W = Math.Max(r1 - r0 + 1, s1 - s0 + 1) * Cell;
                    pu0.Add((r0 - 0.5f) * Cell); pu1.Add((r1 + 0.5f) * Cell); pv0.Add((s0 - 0.5f) * Cell); pv1.Add((s1 + 0.5f) * Cell); pw.Add(-(i + 1)); pt.Add(t);
                    pA.Add(a); pcu.Add(cu0); pcv.Add(cv0); piu.Add(iu); piv.Add(iv); pTa.Add(T); pWa.Add(W); pTb.Add(T); pWb.Add(W);
                }
            // members: within CutGap of each other (union-find over the pieces, sorted along u)
            int m = pw.Count;
            var ord = new int[m]; for (int i = 0; i < m; i++) ord[i] = i;
            Array.Sort(ord, (p, q) => pu0[p].CompareTo(pu0[q]));
            var uf = new int[m]; for (int i = 0; i < m; i++) uf[i] = i;
            int Find(int a) { while (uf[a] != a) { uf[a] = uf[uf[a]]; a = uf[a]; } return a; }
            float gap = CutGap;
            for (int i = 0; i < m; i++)
                for (int j = i + 1; j < m; j++)
                {
                    int a = ord[i], b = ord[j];
                    if (pu0[b] > pu1[a] + gap) break;
                    if (pv0[b] > pv1[a] + gap || pv0[a] > pv1[b] + gap) continue;
                    int ra = Find(a), rb = Find(b); if (ra != rb) uf[ra] = rb;
                }
            // (scratch) CONNECT: members joined through the summed (smaller) side's own blocks bend as one section
            int split0 = _plSplit[pl]; bool upper0 = n - split0 <= split0;
            int stamp = pl + 1;
            int BF(int a) { while (bu[a] != a) { bu[a] = bu[bu[a]]; a = bu[a]; } return a; }
            if (ConnectMembers)
            {
                farList.Clear();
                for (int k = upper0 ? split0 : 0; k < (upper0 ? n : split0); k++) farList.Add(_byAxis[ax][k]);
                if (ConnectStraddlers) farList.AddRange(strad);
                foreach (int i in farList) { farStamp[i] = stamp; bu[i] = i; }
                // (the far side's components: each far block's contacts to far blocks)
                foreach (int i in farList)
                    for (int e = bcStart[i]; e < bcStart[i + 1]; e++)
                    {
                        int j = bcOther[e];
                        if (j > i && farStamp[j] == stamp) { int ra = BF(i), rb = BF(j); if (ra != rb) bu[ra] = rb; }
                    }
                // each piece's far-side block(s): union the pieces that reach one component
                compPiece.Clear();
                void Join(int i, int b)
                {
                    if (farStamp[b] != stamp) return;
                    int cp = BF(b);
                    if (compPiece.TryGetValue(cp, out int o)) { int ra = Find(i), rb = Find(o); if (ra != rb) uf[ra] = rb; } else compPiece[cp] = i;
                }
                for (int i = 0; i < m; i++)
                {
                    int w = pw[i];
                    if (w >= 0) { Join(i, _contacts[w].A); Join(i, _contacts[w].B); } else Join(i, -w - 1);
                }
            }
            var groupOf = new Dictionary<int, List<int>>();
            for (int i = 0; i < m; i++) { int r = Find(i); if (!groupOf.TryGetValue(r, out var l)) groupOf[r] = l = new List<int>(); l.Add(i); }
            var pieceGroup = new int[m];
            { int gi = gA.Count; foreach (var l in groupOf.Values) { foreach (int i in l) pieceGroup[i] = gi; gi++; } }
            if (ConnectMembers && groupOf.Count > 1)
            {
                // (kept only where the plane has several members - one member takes the whole side, SliceStresses)
                int from0 = upper0 ? split0 : 0, to0 = upper0 ? n : split0;
                var asg = new int[to0 - from0];
                for (int k = from0; k < to0; k++) { int b = _byAxis[ax][k]; asg[k - from0] = compPiece.TryGetValue(BF(b), out int pc) ? pieceGroup[pc] : -1; }
                _plAssign[pl] = asg;
            }
            _plGroupStart[pl] = gA.Count; _plGroupCount[pl] = groupOf.Count;
            foreach (var l in groupOf.Values)
            {
                double A = 0, su = 0, sv = 0; float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue, tMin = float.MaxValue;
                foreach (int i in l)
                {
                    float Ai = pA[i];
                    A += Ai; su += Ai * pcu[i]; sv += Ai * pcv[i];
                    u0 = MathF.Min(u0, pu0[i]); u1 = MathF.Max(u1, pu1[i]); v0 = MathF.Min(v0, pv0[i]); v1 = MathF.Max(v1, pv1[i]); tMin = MathF.Min(tMin, pt[i]);
                }
                A = Math.Max(A, 1e-12);
                float cu = (float)(su / A), cv = (float)(sv / A);
                double Iuu = 0, Ivv = 0;
                foreach (int i in l)
                {
                    // (each piece's own second moments about its centroid, plus the parallel axis)
                    float du = pcu[i] - cu, dv = pcv[i] - cv;
                    Iuu += piu[i] + pA[i] * dv * dv;
                    Ivv += piv[i] + pA[i] * du * du;
                }
                gA.Add((float)A); gIuu.Add((float)Iuu); gIvv.Add((float)Ivv); gTor.Add(2f * (u1 - u0) * (v1 - v0) * tMin);
                gCu.Add(cu); gCv.Add(cv); gU0.Add(u0); gU1.Add(u1); gV0.Add(v0); gV1.Add(v1);
                gMs.Add(mWhat.Count); gMc.Add(l.Count); gPlane.Add(pl); gTs.Add(0); gTc.Add(0);
                foreach (int i in l) { mU0.Add(pu0[i]); mU1.Add(pu1[i]); mV0.Add(pv0[i]); mV1.Add(pv1[i]); mWhat.Add(pw[i]); mTa.Add(pTa[i]); mWa.Add(pWa[i]); mTb.Add(pTb[i]); mWb.Add(pWb[i]); }
            }
            // the plane's ties, each to its nearest section (by outline): it goes when that one fails
            if (groupOf.Count > 0 && tieB.Count > 0)
            {
                int g0 = _plGroupStart[pl], gn = _plGroupCount[pl];
                var near = new int[tieB.Count];
                for (int t = 0; t < tieB.Count; t++)
                {
                    float best = float.MaxValue; near[t] = g0;
                    for (int q = g0; q < g0 + gn; q++)
                    {
                        float du = MathF.Max(0f, MathF.Max(gU0[q] - tieU[t], tieU[t] - gU1[q])), dv = MathF.Max(0f, MathF.Max(gV0[q] - tieV[t], tieV[t] - gV1[q]));
                        if (du * du + dv * dv < best) { best = du * du + dv * dv; near[t] = q; }
                    }
                }
                for (int q = g0; q < g0 + gn; q++)
                {
                    gTs[q] = tieBlock.Count;
                    for (int t = 0; t < tieB.Count; t++)
                        if (near[t] == q) { tieBlock.Add(tieB[t]); tiePoint.Add(Axis(ax) * x + Axis(u) * tieU[t] + Axis(v) * tieV[t]); }
                    gTc[q] = tieBlock.Count - gTs[q];
                }
            }
        }
        _gA = gA.ToArray(); _gIuu = gIuu.ToArray(); _gIvv = gIvv.ToArray(); _gTor = gTor.ToArray(); _gCu = gCu.ToArray(); _gCv = gCv.ToArray();
        _gU0 = gU0.ToArray(); _gU1 = gU1.ToArray(); _gV0 = gV0.ToArray(); _gV1 = gV1.ToArray(); _gMemStart = gMs.ToArray(); _gMemCount = gMc.ToArray();
        _mU0 = mU0.ToArray(); _mU1 = mU1.ToArray(); _mV0 = mV0.ToArray(); _mV1 = mV1.ToArray(); _mWhat = mWhat.ToArray();
        _mTa = mTa.ToArray(); _mWa = mWa.ToArray(); _mTb = mTb.ToArray(); _mWb = mWb.ToArray();
        _gPlane = gPlane.ToArray(); _mUtil = new float[_mWhat.Length]; _gUtil = new float[_gA.Length];
        _mUt = new float[_mWhat.Length]; _mUc = new float[_mWhat.Length]; _mS4 = new float[4 * _mWhat.Length];
        _gTieStart = gTs.ToArray(); _gTieCount = gTc.ToArray(); _tieBlock = tieBlock.ToArray(); _tiePoint = tiePoint.ToArray();
        _contactSection = new int[nc]; Array.Fill(_contactSection, -1);
        for (int g = 0; g < _gA.Length; g++) for (int mi = _gMemStart[g]; mi < _gMemStart[g] + _gMemCount[g]; mi++) if (_mWhat[mi] >= 0) _contactSection[_mWhat[mi]] = g;
        BlockUtil = new float[n];
        _blockF = new Vector3[n]; _blockM = new Vector3[n];
        _gF = new Vector3[_gA.Length]; _gM = new Vector3[_gA.Length];
    }

    /// <summary>BUCKLING (2026-10-13: a large light-armour block is a 2.5 m box of 3.6 mm walls - by yield alone a wing one
    /// such block deep took 29 g; a thin plate that size buckles at a sliver of the yield). A wall panel of thickness t and
    /// width b - the block's face, or FramePitch where frames stiffen it - buckles in compression at
    /// sigma_cr = k pi^2 E / (12 (1 - nu^2)) (t / b)^2 (k 4: edges held by the next walls), and carries on past it on its
    /// stiff edges (the effective width: von Karman / Winter) up to sqrt(sigma_cr sigma_y) - never more than the yield.
    /// The square-cube law big blocks lacked: the same 3 mm over a five-times-wider panel holds a fifth as much.</summary>
    internal static float Buckled(float yield, float t, float b)
    {
        if (FramePitch > 0f) b = MathF.Min(b, FramePitch);
        if (!(b > 1e-4f) || !(t > 0f)) return yield;
        float cr = BucklingK * (float)E * (t / b) * (t / b);
        return MathF.Min(yield, MathF.Sqrt(cr * yield));
    }
    /// <summary>k pi^2 / (12 (1 - nu^2)) for a plate held on all four edges (k 4, nu 0.3).</summary>
    public static float BucklingK = 3.615f;
    /// <summary>Internal frames every this many metres stiffen the walls (0: none - a block's face buckles whole).</summary>
    public static float FramePitch = 2.5f;


    // (2026-10-14, an independent review on a real 2.8 kt ship dumped from the game - newred_v2 - where every joint read 40-230x
    //  its strength per g; set B, calibration kept: Tu-134 2.9 / 3.8 g, Plane2 8.8 / 13.2 g)
    /// <summary>Each block's walls buckle at their own thickness over their own face; a contact takes the weaker of its two
    /// (the larger neighbour's face made a 0.25 m pillar buckle as a 2.5 m panel).</summary>
    public static bool OwnWall = true;
    /// <summary>(off: the smaller face for the panel - it broke the calibration, Plane2 surviving 28 g)</summary>
    public static bool PanelSmaller = false;
    /// <summary>Blocks straddling a plane count as part of the summed side's components (else 1738 t fell back to the
    /// nearest-member rule on newred_v2; with them, 0.7 t).</summary>
    public static bool ConnectStraddlers = true;
    /// <summary>CONNECTED SECTIONS: per plane, the summed side's blocks in connected components; every member a component
    /// touches bends as one section, and each block loads the section of its own component - not the nearest member (a nose
    /// hull's 34.5 t loaded a two-contact catwalk edge: 182x at 1 g). The nearest member only where no component reaches.</summary>
    public static bool ConnectMembers = true;
    int[][] _plAssign; Dictionary<int, int> _compPiece;
    /// <summary>(BuildSections) a block straddling the plane, noted.</summary>
    static bool StraddleMark(List<int> l, int i) { l.Add(i); return true; }
    /// <summary>(BuildSections) a straddling block: trim is a TIE (its middle across the plane), not a member - false.</summary>
    bool TieMark(int i, int u, int v, List<float> tu, List<float> tv, List<int> tb)
    {
        if (Mat[i] != StructMaterial.Trim) return true;
        tu.Add(C(Center[i], u)); tv.Add(C(Center[i], v)); tb.Add(i);
        return false;
    }
    static int LowerBound(float[] a, float x) { int lo = 0, hi = a.Length; while (lo < hi) { int mid = (lo + hi) >> 1; if (a[mid] <= x) lo = mid + 1; else hi = mid; } return lo; }

    Vector3[] _blockF, _blockM, _gF, _gM;
    Vector3[][] _preF, _preM, _sortC, _sortF, _sortM;

    /// <summary>Each slice's members' resultants from the blocks' own loads (_blockF, _blockM about their centres), and
    /// every piece of material's utilisation.</summary>
    void SliceStresses()
    {
        if (_gA == null) BuildSections();
        Array.Clear(ContactUtil, 0, ContactUtil.Length); Array.Clear(BlockUtil, 0, BlockUtil.Length);
        Array.Clear(_mUtil, 0, _mUtil.Length); Array.Clear(_gUtil, 0, _gUtil.Length);
        Array.Clear(_mUt, 0, _mUt.Length); Array.Clear(_mUc, 0, _mUc.Length); Array.Clear(_mS4, 0, _mS4.Length);
        Array.Clear(_gF, 0, _gF.Length); Array.Clear(_gM, 0, _gM.Length);
        MaxUtil = 0f; MaxContact = -1; MaxBlock = -1; MaxJoint = -1;
        // running totals along each axis (blocks in centre order): force, and moment about the grid origin - a
        // one-member slice's resultant is a difference of two, no pass over its blocks
        int nb = Count;
        _preF ??= new Vector3[3][]; _preM ??= new Vector3[3][];
        // (and each block's centre, load and moment in that order too - the many-section planes below walk them in it, not
        //  scattered through the block arrays: the same values, 2026-10-15)
        _sortC ??= new Vector3[3][]; _sortF ??= new Vector3[3][]; _sortM ??= new Vector3[3][];
        for (int ax = 0; ax < 3; ax++)
        {
            var pf = _preF[ax] ??= new Vector3[nb + 1]; var pm = _preM[ax] ??= new Vector3[nb + 1];
            var sc = _sortC[ax] ??= new Vector3[nb]; var sf = _sortF[ax] ??= new Vector3[nb]; var sm = _sortM[ax] ??= new Vector3[nb];
            var order = _byAxis[ax];
            for (int k = 0; k < nb; k++)
            {
                int i = order[k];
                pf[k + 1] = pf[k] + _blockF[i];
                pm[k + 1] = pm[k] + _blockM[i] + Vector3.Cross(Center[i], _blockF[i]);
                sc[k] = Center[i]; sf[k] = _blockF[i]; sm[k] = _blockM[i];
            }
        }
        for (int pl = 0; pl < _planeCount; pl++)
        {
            int ax = _plAxis[pl], u = (ax + 1) % 3, v = (ax + 2) % 3;
            int g0 = _plGroupStart[pl], gn = _plGroupCount[pl];
            if (gn == 0) continue;
            float x = _plCoord[pl];
            // the smaller side; the other side's load is its opposite (the whole is balanced)
            int split = _plSplit[pl], n = Count;
            bool upper = n - split <= split;
            int from = upper ? split : 0, to = upper ? n : split;
            float sg = upper ? 1f : -1f;
            var order = _byAxis[ax];
            if (gn == 1)
            {
                var cen1 = Axis(ax) * x + Axis(u) * _gCu[g0] + Axis(v) * _gCv[g0];
                var F1 = _preF[ax][to] - _preF[ax][from];
                _gF[g0] = F1 * sg;
                _gM[g0] = (_preM[ax][to] - _preM[ax][from] - Vector3.Cross(cen1, F1)) * sg;
            }
            else
            {
                // (each section's centre once per plane, not once per block - the same sum, so the same bits; the plane's
                //  assignment read once: the hot loop of the solve, 2026-10-15)
                if (_gCen == null || _gCen.Length < _gA.Length) _gCen = new Vector3[_gA.Length];
                var cens = _gCen;
                for (int q = g0; q < g0 + gn; q++) cens[q] = Axis(ax) * x + Axis(u) * _gCu[q] + Axis(v) * _gCv[q];
                var assign = _plAssign[pl];
                var sc = _sortC[ax]; var sf = _sortF[ax]; var sm = _sortM[ax];
                for (int k = from; k < to; k++)
                {
                    int g = g0;
                    int asg = assign != null ? assign[k - from] : -1;
                    if (asg >= 0) g = asg;
                    else if (gn > 1)
                    {
                        float bu = C(sc[k], u), bv = C(sc[k], v);
                        float best = float.MaxValue;
                        for (int q = g0; q < g0 + gn; q++)
                        {
                            float du = MathF.Max(0f, MathF.Max(_gU0[q] - bu, bu - _gU1[q])), dv = MathF.Max(0f, MathF.Max(_gV0[q] - bv, bv - _gV1[q]));
                            float d = du * du + dv * dv;
                            if (d < best) { best = d; g = q; }
                        }
                    }
                    var bf = sf[k];
                    _gF[g] += bf * sg;
                    _gM[g] += (sm[k] + Vector3.Cross(sc[k] - cens[g], bf)) * sg;
                }
            }
            for (int g = g0; g < g0 + gn; g++)
            {
                var Fc = _gF[g]; var Mc = _gM[g];
                if (Fc == Vector3.Zero && Mc == Vector3.Zero) continue;
                float A = _gA[g], N = C(Fc, ax), Mu = C(Mc, u), Mv = C(Mc, v);
                float tau = MathF.Sqrt(C(Fc, u) * C(Fc, u) + C(Fc, v) * C(Fc, v)) / (0.5f * A) + MathF.Abs(C(Mc, ax)) / MathF.Max(_gTor[g], 1e-9f);
                float iuu = MathF.Max(_gIuu[g], 1e-12f), ivv = MathF.Max(_gIvv[g], 1e-12f);
                for (int mi = _gMemStart[g]; mi < _gMemStart[g] + _gMemCount[g]; mi++)
                {
                    float sTen = 0f, sCom = 0f;   // (the worst tension and compression at its corners)
                    for (int q = 0; q < 4; q++)
                    {
                        float uu = ((q & 1) == 0 ? _mU0[mi] : _mU1[mi]) - _gCu[g], vv = ((q & 2) == 0 ? _mV0[mi] : _mV1[mi]) - _gCv[g];
                        float s = N / A + Mu * vv / iuu - Mv * uu / ivv;
                        _mS4[4 * mi + q] = s;
                        if (s > sTen) sTen = s; else if (-s > sCom) sCom = -s;
                    }
                    int what = _mWhat[mi];
                    float strength, comp;
                    // tension against the yield; compression against what the plates carry buckled (Buckled) - each block's
                    // own: a shell's wall over its face there, a lattice's own depth over its width
                    if (what >= 0)
                    {
                        var c = _contacts[what];
                        float sa = BlockStrength(c.A), sb = BlockStrength(c.B);
                        strength = MathF.Min(sa, sb);
                        comp = OwnWall ? MathF.Min(Buckled(sa, _mTa[mi], _mWa[mi]), Buckled(sb, _mTb[mi], _mWb[mi]))
                            : Buckled(strength, MathF.Min(_mTa[mi], _mTb[mi]), PanelSmaller ? MathF.Min(_mWa[mi], _mWb[mi]) : MathF.Max(_mWa[mi], _mWb[mi]));
                    }
                    else { strength = BlockStrength(-what - 1); comp = Buckled(strength, _mTa[mi], _mWa[mi]); }
                    float ut = sTen / strength, uc = sCom / comp, us = tau / strength;
                    float u2 = MathF.Max(ut, uc);
                    float util = MathF.Sqrt(u2 * u2 + 3f * us * us) / Strength;
                    _mUtil[mi] = util; if (util > _gUtil[g]) _gUtil[g] = util;
                    _mUt[mi] = ut / Strength; _mUc[mi] = uc / Strength;
                    if (what >= 0) { if (util > ContactUtil[what]) ContactUtil[what] = util; }
                    else { int b = -what - 1; if (util > BlockUtil[b]) BlockUtil[b] = util; }
                    if (util > MaxUtil) { MaxUtil = util; if (what >= 0) { MaxContact = what; MaxBlock = -1; } else { MaxBlock = -what - 1; MaxContact = -1; } }
                }
                _gF[g] = Vector3.Zero; _gM[g] = Vector3.Zero;
            }
        }
        for (int j = 0; j < Count; j++) JUtil[j] = Parent[j] < 0 ? 0f : ContactUtil[_parentContact[j]];
        MaxJoint = MaxContact >= 0 ? Outboard(MaxContact) : MaxBlock;
    }

    // ── SECTIONS for the damage (2026-10-15, the user: "one pattern im not sure i like is that a fracture creeps along the span
    //    instead of uniformly crumpling the weak neck"): a section is one member of a slice - a cut through one connected part
    //    of the ship (Sections: within CutGap, or joined through the summed side's own blocks); its PIECES (the cut's members:
    //    the contact patches in it and the blocks straddling it) each have their own utilisation from the last solve. The
    //    slices only (Method 0); none otherwise (StructureEval.Damage then by block, as before) ──
    /// <summary>Sections (0 before the slices are built, or another Method).</summary>
    public int SectionCount => Method == 0 && _gA != null ? _gA.Length : 0;
    /// <summary>A section's worst piece's utilisation (the last solve; 0 unloaded).</summary>
    public float SectionUtil(int g) => _gUtil[g];
    /// <summary>A section's pieces: _mWhat-indexed [start, start + count).</summary>
    public int SectionPieceStart(int g) => _gMemStart[g];
    public int SectionPieceCount(int g) => _gMemCount[g];
    /// <summary>A piece's utilisation (the last solve).</summary>
    public float PieceUtil(int mi) => _mUtil[mi];
    /// <summary>TENSION VS COMPRESSION (2026-10-15): a piece's normal stress by its sign, as utilisations (the last solve; shear
    /// left out - PieceUtil has it): its worst pulled corner against the yield, its worst pushed corner against what its
    /// plates carry buckled. A member in bending has both (one face pulled, the other pushed).</summary>
    public float PieceTension(int mi) => _mUt != null ? _mUt[mi] : 0f;
    public float PieceCompression(int mi) => _mUc != null ? _mUc[mi] : 0f;
    /// <summary>The piece's governing normal stress is compression (its compression utilisation - against its buckled plates
    /// - over its tension's): light armour's thin walls make most members compression-governed, even those mostly pulled.</summary>
    public bool PieceCompressive(int mi) => PieceCompression(mi) > PieceTension(mi);
    /// <summary>The piece's mean normal stress over its outline (Pa; the last solve): + pulled, - pushed - which side of its
    /// section's neutral axis it is on.</summary>
    public float PieceMidStress(int mi) => _mS4 != null ? 0.25f * (_mS4[4 * mi] + _mS4[4 * mi + 1] + _mS4[4 * mi + 2] + _mS4[4 * mi + 3]) : 0f;
    /// <summary>The piece's SIDE of its section: +1 the pulled side, -1 the pushed side, 0 neither (PieceMidStress's sign) -
    /// a failing cut tears on the pulled side and hinges on the pushed one (FractureMath.ModeOf).</summary>
    public int PieceSide(int mi) { float s = PieceMidStress(mi); return s > 0f ? 1 : s < 0f ? -1 : 0; }
    /// <summary>A piece's contact (-1: a straddling block - PieceBlock).</summary>
    public int PieceContact(int mi) => _mWhat[mi] >= 0 ? _mWhat[mi] : -1;
    /// <summary>The block a piece's failure takes off the ship: a patch's block on the slice's lighter side (Outboard), a
    /// straddling block itself (cut across its own middle).</summary>
    public int PieceBlock(int mi) => _mWhat[mi] >= 0 ? Outboard(_mWhat[mi]) : -_mWhat[mi] - 1;
    /// <summary>A piece's middle on its plane (m, grid-local).</summary>
    public Vector3 PiecePoint(int mi)
    {
        int pl = _gPlane[GroupOfPiece(mi)], ax = _plAxis[pl];
        return Axis(ax) * _plCoord[pl] + Axis((ax + 1) % 3) * ((_mU0[mi] + _mU1[mi]) * 0.5f) + Axis((ax + 2) % 3) * ((_mV0[mi] + _mV1[mi]) * 0.5f);
    }
    /// <summary>A piece's PUSHED EDGE on its plane (the last solve): the middle of its corners at its lowest normal stress
    /// (within a tenth of its corners' range) - a member's extreme compression fibre (StructureBend: the fold's pivot). No
    /// stress across it: its middle.</summary>
    public Vector3 PiecePushedEdge(int mi)
    {
        if (_mS4 == null) return PiecePoint(mi);
        float lo = float.MaxValue, hi = float.MinValue;
        for (int q = 0; q < 4; q++) { float v = _mS4[4 * mi + q]; lo = MathF.Min(lo, v); hi = MathF.Max(hi, v); }
        if (!(hi > lo)) return PiecePoint(mi);
        int pl = _gPlane[GroupOfPiece(mi)], ax = _plAxis[pl], u = (ax + 1) % 3, w = (ax + 2) % 3;
        float su = 0f, sv = 0f; int n = 0;
        for (int q = 0; q < 4; q++)
        {
            if (_mS4[4 * mi + q] > lo + 0.1f * (hi - lo)) continue;
            su += (q & 1) == 0 ? _mU0[mi] : _mU1[mi]; sv += (q & 2) == 0 ? _mV0[mi] : _mV1[mi]; n++;
        }
        return Axis(ax) * _plCoord[pl] + Axis(u) * (su / n) + Axis(w) * (sv / n);
    }
    int GroupOfPiece(int mi)
    {
        int lo = 0, hi = _gMemStart.Length - 1;   // (sections' pieces are consecutive, in order)
        while (lo < hi) { int mid = (lo + hi + 1) >> 1; if (_gMemStart[mid] <= mi) lo = mid; else hi = mid - 1; }
        return lo;
    }
    /// <summary>A section's plane: its normal axis and coordinate (m).</summary>
    public void SectionPlane(int g, out int axis, out float plane) { int pl = _gPlane[g]; axis = _plAxis[pl]; plane = _plCoord[pl]; }
    /// <summary>A section's centre on its plane (its material's centroid, m).</summary>
    public Vector3 SectionCentre(int g) { int pl = _gPlane[g], ax = _plAxis[pl]; return Axis(ax) * _plCoord[pl] + Axis((ax + 1) % 3) * _gCu[g] + Axis((ax + 2) % 3) * _gCv[g]; }
    /// <summary>The way out of a section into what it takes off (its plane's lighter side; unit, along its axis).</summary>
    public Vector3 SectionOut(int g) { int pl = _gPlane[g]; return Axis(_plAxis[pl]) * (_plPlusLight[pl] ? 1f : -1f); }
    /// <summary>A section's TIES (trim crossing its plane nearest it - no member, but joining the two sides): how many, each
    /// one's trim block and its place on the plane. They go with the section when it fails.</summary>
    public int SectionTieCount(int g) => _gTieCount[g];
    public int SectionTieBlock(int g, int t) => _tieBlock[_gTieStart[g] + t];
    public Vector3 SectionTiePoint(int g, int t) => _tiePoint[_gTieStart[g] + t];
    /// <summary>THE ORGANIC BREAK's noise seed (FractureMath.Step: the grid's id - the game sets it each evaluation; 0 offline).</summary>
    public int Seed;
    /// <summary>A contact's section (-1: none - trim, or not the slices).</summary>
    public int ContactSection(int k) => SectionCount > 0 && k >= 0 && k < _contactSection.Length ? _contactSection[k] : -1;
}
