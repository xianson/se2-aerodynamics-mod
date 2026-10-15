#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// SHIP STRUCTURE - the loads in a grid's joints, pure math, no game (Tests/StructureTests, AeroBench 'struct').
///
/// THE GRAPH. Blocks are nodes (their occupied 0.25 m cell boxes - their real shape, ShipStructure.Shape.cs - mass,
/// material); two blocks whose cells share a face are joined, the joint being the shared patch (given the blocks' mount
/// points: only the faces where their mount points mate). A load-path TREE runs from the root (the block nearest the centre of
/// mass) out to every block along the shortest route (Dijkstra on centre distances): each block hangs from one parent.
///
/// THE LOADS (Solve, per evaluation). Each block: its external force and moment (the air on its faces, its thrust) and
/// its inertia in the ship's motion, -m (a + alpha x r + omega x (omega x r)) with r from the centre of mass and a the
/// PROPER acceleration (gravity acts on every block alike and loads no joint). With a and alpha the ship's own (the
/// sum of the external loads over its mass and inertia), everything balances: what reaches the root is ~0 (Residual).
/// One pass from the tips in: a joint carries the force and moment of everything outboard of it.
///
/// THE SECTIONS - cuts (the method of sections). A tree routes each strip of a wing, each block of a fuselage's ring,
/// its own way, but bonded blocks bend as one deep section: two stacked 0.5 m strips are 2.5 times as strong as one,
/// not twice; a load the tree sends through one pinhole is really spread over the whole fuselage ring. So every
/// contact patch in one plane, within CutGap of its neighbours, is one CUT - the ship's section there - and the cut
/// carries the resultant of the tree's joints that cross it (each from its child's side), moved to its centroid: where
/// the cut parts the ship, exactly the loads on one side (equilibrium), however the tree routed them. Each patch is the weaker block's material over it (a shell's walls round the patch's real outline, a lattice's cells
/// over its area: ShipStructure.Shape.cs); their
/// second moments add with the parallel-axis terms. Per patch: the normal stress at its corners (N/A + Mu v/Iuu -
/// Mv u/Ivv), the shear (V over half the area, the torque on the cut's outline by Bredt), von Mises; ContactUtil =
/// that over the weaker block's yield x its StrengthAt(temperature) x Strength (the world's multiplier). 1: yield.
/// The game side tires the outboard block from the endurance limit, wears it past 1 and breaks it when its health is out
/// (Structure/Model/StructureFatigue; 2026-10-14) - per CUT on the slices: every member of an overloaded cut together, and the
/// whole cut fails at once (StructureEval.DamageBySection, 2026-10-15).
///
/// Simplifications: the tree is one path per block (it prefers whole-face contacts: a pinhole is routed round when it
/// can be); redundant paths share only through their cuts; no buckling.
/// </summary>
public sealed partial class ShipStructure
{
    public const float Cell = 0.25f;
    /// <summary>World multiplier on every block's strength (Structure.Strength). 7 (with buckling, the trim left out;
    /// AeroBench 'fail', a pull at 250 m/s): the Tu-134 - an airliner of small blocks - yields at 2.9 g, sheds an engine at
    /// 3.8 g and a wing section at 4.3 g; Plane2 - a light large-block plane - yields at 8.4 g, breaks at 12.8 g. (Rechecked
    /// with the real shapes, 2026-10-14: Tu-134 2.9 / 3.8 g, Plane2 8.8 / 13.2 g - their blocks' shapes are boxes.)</summary>
    public static float Strength = 7f;

    /// <summary>TRIM ON ITS HOST: a trim block's (detailing: not load-bearing) own loads moved onto the load-bearing block it is
    /// mounted on (through other trim, breadth first) before the sections - left on the trim, they crossed the plane through
    /// whatever tiny joint the trim touched (41 detailing cubes' 55 kN through one 0.25 x 0.5 m patch: 33x at 1 g side,
    /// 2026-10-14 review).</summary>
    public static bool TrimToHost = true;
    int[] _host;
    void LumpTrim()
    {
        int n = Count;
        if (_host == null)
        {
            _host = new int[n]; var adj = new List<int>[n];
            for (int i = 0; i < n; i++) { _host[i] = Mat[i] == StructMaterial.Trim ? -1 : i; adj[i] = new List<int>(); }
            foreach (var c in _contacts) { adj[c.A].Add(c.B); adj[c.B].Add(c.A); }
            var q = new Queue<int>();
            for (int i = 0; i < n; i++) if (_host[i] >= 0) q.Enqueue(i);
            while (q.Count > 0) { int i = q.Dequeue(); foreach (int j in adj[i]) if (_host[j] < 0) { _host[j] = _host[i]; q.Enqueue(j); } }
            for (int i = 0; i < n; i++) if (_host[i] < 0) _host[i] = i;
        }
        for (int i = 0; i < n; i++)
        {
            int h = _host[i]; if (h == i) continue;
            _blockF[h] += _blockF[i]; _blockM[h] += _blockM[i] + Vector3.Cross(Center[i] - Center[h], _blockF[i]);
            _blockF[i] = Vector3.Zero; _blockM[i] = Vector3.Zero;
        }
    }
    /// <summary>Patches in one plane within this of each other (m) are one section.</summary>
    public static float CutGap = 2f;
    /// <summary>How the loads are shared: 0 SLICES (ShipStructure.Sections.cs: the method of sections - exact by
    /// equilibrium, no load paths to guess; the default); 1 the load-path tree and its cuts (a tree routes a 3D blob of
    /// blocks through one joint: false hot spots); 2 the elastic network (ShipStructure.Network.cs - right in principle,
    /// but real hulls, full of staircase contacts and near-hinges, converge too slowly).</summary>
    public static int Method = 0;

    // ── blocks ──
    public int Count;
    public float[] Mass, Wall;
    public int[] Mat;
    public Vector3[] Center, Lo, Hi;
    public float TotalMass;
    public Vector3 Com;
    /// <summary>Each block's temperature (K) for its strength (the hull-heat model writes it; 290 cold).</summary>
    public float[] TempK;
    /// <summary>Each block's health left (0..1, the game's HealthIntegrity): a damaged block is that much weaker (at
    /// least MinIntegrity of its strength).</summary>
    public float[] Integrity;
    public static float MinIntegrity = 0.2f;
    /// <summary>Each block's own destruction threshold (its definition's DestructionThreshold; null or 0: the default,
    /// StructureFatigue.BreakBelow) - 2026-10-15: a block whose definition removes it above 0.3 was worn into the game's
    /// removal by a health write (no split, smoke or tear) before the structure failed it.</summary>
    public float[] BreakAt;
    /// <summary>The health (fraction of full) block b fails at: its own threshold, never under BreakBelow.</summary>
    public float FloorOf(int b) => BreakAt != null && b >= 0 && b < BreakAt.Length ? MathF.Max(BreakAt[b], StructureFatigue.BreakBelow) : StructureFatigue.BreakBelow;

    // ── the tree ──
    /// <summary>Parent block (-1: a root).</summary>
    public int[] Parent;
    /// <summary>Blocks root first (each after its parent).</summary>
    public int[] Order;
    public int Root = -1, Components;
    /// <summary>Each block's piece (connected part; a grid is one - a merged blueprint of several, several).</summary>
    public int[] Piece;

    // ── joints: block i to Parent[i] ──
    /// <summary>The patch's normal axis (0 x, 1 y, 2 z).</summary>
    public int[] JAxis;
    /// <summary>+1: the child is on the patch plane's + side.</summary>
    public int[] JSide;
    /// <summary>The patch centre (m).</summary>
    public Vector3[] JPoint;
    /// <summary>The patch outline across its plane (m): along u = axis+1, v = axis+2.</summary>
    public float[] JA, JB, JArea;
    public int[] JCut;

    // ── cuts ──
    public int CutCount;
    float[] _cutA, _cutIuu, _cutIvv, _cutTorsion;
    Vector3[] _cutCentroid;
    int[] _cutAxis;
    List<int>[] _cutContacts;

    // ── contacts: every face two blocks share (the tree's joints among them) ──
    public int ContactCount;
    /// <summary>Each contact's cut, and its utilisation (Solve).</summary>
    public int[] ContactCut;
    public float[] ContactUtil;
    /// <summary>Each block's place in Order (its depth along the tree: the outboard side of a contact is the deeper).</summary>
    public int[] OrderIndex;

    // ── inputs per evaluation ──
    /// <summary>External force on each block (N, grid-local) and moment about its centre (N m).</summary>
    public Vector3[] ExtForce, ExtMoment;

    // ── outputs ──
    public Vector3[] JForce, JMoment;
    public float[] JUtil;
    public float MaxUtil;
    public int MaxJoint = -1, MaxContact = -1;
    public Vector3 ResidualForce, ResidualMoment;
    public Vector3 Accel, AngAccel;

    /// <summary>A vector's component on axis a (0 X, 1 Y, 2 Z).</summary>
    internal static float C(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    internal static Vector3 Axis(int a) => a == 0 ? Vector3.UnitX : a == 1 ? Vector3.UnitY : Vector3.UnitZ;

    /// <summary>The structure of these blocks. boxes: (block index, cell min, cell max) - inclusive 0.25 m cells, cell i
    /// centred at i x 0.25 m (the aero surface's cells), disjoint, a block's cells all its boxes. mass, mat: per block.
    /// mounts (optional): the blocks' mount faces - (block, outward direction, cell min, cell max: the block's own boundary
    /// cells); given, two blocks join only through mated mount faces (ShipStructure.Shape.cs), else through every face
    /// they share.</summary>
    public static ShipStructure Build(IReadOnlyList<int> boxBlock, IReadOnlyList<Vector3I> boxLo, IReadOnlyList<Vector3I> boxHi, IReadOnlyList<float> mass, IReadOnlyList<int> mat,
        IReadOnlyList<(int block, Vector3I dir, Vector3I lo, Vector3I hi)> mounts = null)
    {
        var s = new ShipStructure();
        int n = mass.Count;
        s.Count = n;
        s.Mass = new float[n]; s.Wall = new float[n]; s.Mat = new int[n];
        s.Center = new Vector3[n]; s.Lo = new Vector3[n]; s.Hi = new Vector3[n]; s.TempK = new float[n]; s.Integrity = new float[n];
        for (int i = 0; i < n; i++) { s.TempK[i] = 290f; s.Integrity[i] = 1f; s.Mass[i] = MathF.Max(mass[i], 0.1f); s.Mat[i] = mat[i]; }
        s._boxBlock = boxBlock; s._boxLo = boxLo; s._boxHi = boxHi;
        // each block's real shape: its centre, box, own inertia, wall, lattice (ShipStructure.Shape.cs)
        s.BuildShapes(boxBlock, boxLo, boxHi);
        double M = 0, mx = 0, my = 0, mz = 0;
        for (int i = 0; i < n; i++) { M += s.Mass[i]; mx += s.Mass[i] * s.Center[i].X; my += s.Mass[i] * s.Center[i].Y; mz += s.Mass[i] * s.Center[i].Z; }
        s.TotalMass = (float)M;
        s.Com = M > 0 ? new Vector3((float)(mx / M), (float)(my / M), (float)(mz / M)) : Vector3.Zero;
        s.BuildGraph(boxBlock, boxLo, boxHi, mounts);
        s.ExtForce = new Vector3[n]; s.ExtMoment = new Vector3[n];
        s.JForce = new Vector3[n]; s.JMoment = new Vector3[n]; s.JUtil = new float[n];
        return s;
    }

    // ── contacts: the faces two blocks' boxes share ──
    /// <summary>A contact: blocks A, B (A the lower index) across the plane between cell layers P and P + 1 on Axis (SideB +1:
    /// B on the plane's + side), the patch's bounding outline (m) and its real area (m2, its rectangles: _rStart).</summary>
    struct Contact { public int A, B, Axis, SideB, P; public float Plane, U0, U1, V0, V1, Area; }

    void BuildGraph(IReadOnlyList<int> boxBlock, IReadOnlyList<Vector3I> boxLo, IReadOnlyList<Vector3I> boxHi, IReadOnlyList<(int block, Vector3I dir, Vector3I lo, Vector3I hi)> mounts)
    {
        int n = Count, nb = boxBlock.Count;
        // boxes into 2 m buckets (each box into every bucket it reaches, one cell out): candidate pairs share a bucket.
        // Packed (2026-10-14: a list per bucket and a set of every pair seen were 56 MB per rebuild on a 4409-block ship):
        // each bucket an id in order of first use (the order the pairs are met in), its boxes in index order in one array.
        int nk = 0;
        for (int k = 0; k < nb; k++)
        {
            var lo = boxLo[k]; var hi = boxHi[k];
            nk += (BFd(hi.X + 1) - BFd(lo.X - 1) + 1) * (BFd(hi.Y + 1) - BFd(lo.Y - 1) + 1) * (BFd(hi.Z + 1) - BFd(lo.Z - 1) + 1);
        }
        var bucketId = new Dictionary<long, int>(Math.Max(16, nk / 4));
        var entryId = new int[nk]; var bkCount = new List<int>(Math.Max(16, nk / 4));
        int e0 = 0;
        for (int k = 0; k < nb; k++)
        {
            var lo = boxLo[k]; var hi = boxHi[k];
            for (int x = BFd(lo.X - 1); x <= BFd(hi.X + 1); x++)
                for (int y = BFd(lo.Y - 1); y <= BFd(hi.Y + 1); y++)
                    for (int z = BFd(lo.Z - 1); z <= BFd(hi.Z + 1); z++)
                    {
                        long key = BKey(x, y, z);
                        if (!bucketId.TryGetValue(key, out int id)) { bucketId[key] = id = bkCount.Count; bkCount.Add(0); }
                        bkCount[id]++; entryId[e0++] = id;
                    }
        }
        int nbk = bkCount.Count;
        var bkStart = new int[nbk + 1];
        for (int q = 0; q < nbk; q++) bkStart[q + 1] = bkStart[q] + bkCount[q];
        var bkBox = new int[nk];
        for (int q = 0; q < nbk; q++) bkCount[q] = 0;
        e0 = 0;
        for (int k = 0; k < nb; k++)
        {
            var lo = boxLo[k]; var hi = boxHi[k];
            int cnt = (BFd(hi.X + 1) - BFd(lo.X - 1) + 1) * (BFd(hi.Y + 1) - BFd(lo.Y - 1) + 1) * (BFd(hi.Z + 1) - BFd(lo.Z - 1) + 1);
            for (int q = 0; q < cnt; q++) { int id = entryId[e0++]; bkBox[bkStart[id] + bkCount[id]++] = k; }
        }
        _bucketId = bucketId; _bkStart = bkStart; _bkBox = bkBox;
        // per block pair, axis and plane: the shared patch (its rectangles, area, outline)
        var contacts = new List<Contact>(Math.Max(16, nb / 2));
        var index = new Dictionary<long, int>(Math.Max(16, nb / 2));
        var rects = new List<Rect>(2 * nb); var rectOf = new List<int>(2 * nb);
        for (int bk = 0; bk < nbk; bk++)
            for (int i = bkStart[bk]; i < bkStart[bk + 1]; i++)
                for (int j = i + 1; j < bkStart[bk + 1]; j++)
                {
                    int ka = bkBox[i], kb = bkBox[j];
                    int ba = boxBlock[ka], bb = boxBlock[kb];
                    if (ba == bb) continue;
                    var al = boxLo[ka]; var ah = boxHi[ka]; var bl = boxLo[kb]; var bh = boxHi[kb];
                    // (a pair is met in every bucket both reach: taken in the first of them, as a set of pairs seen did - only
                    //  touching pairs are checked, and none but they make a contact)
                    if (!Touch(al, ah, bl, bh) || FirstCommonBucket(bucketId, al, ah, bl, bh) != bk) continue;
                    for (int ax = 0; ax < 3; ax++)
                    {
                        int u = (ax + 1) % 3, v = (ax + 2) % 3;
                        int aH = Ci(ah, ax), aL = Ci(al, ax), bH = Ci(bh, ax), bL = Ci(bl, ax);
                        int sideB, pc;
                        if (aH + 1 == bL) { sideB = 1; pc = aH; }
                        else if (bH + 1 == aL) { sideB = -1; pc = bH; }
                        else continue;
                        float plane = (pc + 0.5f) * Cell;
                        int u0 = Math.Max(Ci(al, u), Ci(bl, u)), u1 = Math.Min(Ci(ah, u), Ci(bh, u));
                        int v0 = Math.Max(Ci(al, v), Ci(bl, v)), v1 = Math.Min(Ci(ah, v), Ci(bh, v));
                        if (u1 < u0 || v1 < v0) continue;
                        // (a, b) as (lower, higher) block index; the side is b's
                        int A = ba, B = bb; if (A > B) { A = bb; B = ba; sideB = -sideB; }
                        long ck = (((long)A * n + B) * 3 + ax) * 0x400000L + (pc & 0x3FFFFF);
                        float fu0 = (u0 - 0.5f) * Cell, fu1 = (u1 + 0.5f) * Cell, fv0 = (v0 - 0.5f) * Cell, fv1 = (v1 + 0.5f) * Cell;
                        float area = (fu1 - fu0) * (fv1 - fv0);
                        if (index.TryGetValue(ck, out int ci))
                        {
                            var c = contacts[ci];
                            c.U0 = MathF.Min(c.U0, fu0); c.U1 = MathF.Max(c.U1, fu1); c.V0 = MathF.Min(c.V0, fv0); c.V1 = MathF.Max(c.V1, fv1); c.Area += area;
                            contacts[ci] = c;
                        }
                        else
                        {
                            index[ck] = ci = contacts.Count;
                            contacts.Add(new Contact { A = A, B = B, Axis = ax, SideB = sideB, P = pc, Plane = plane, U0 = fu0, U1 = fu1, V0 = fv0, V1 = fv1, Area = area });
                        }
                        rects.Add(new Rect { U0 = u0, U1 = u1, V0 = v0, V1 = v1 }); rectOf.Add(ci);
                    }
                }
        // each contact's rectangles together
        {
            int nc0 = contacts.Count;
            _rStart = new int[nc0 + 1];
            foreach (int c in rectOf) _rStart[c + 1]++;
            for (int c = 0; c < nc0; c++) _rStart[c + 1] += _rStart[c];
            var fillR = new int[nc0];
            _rects = new List<Rect>(rects.Count); for (int k = 0; k < rects.Count; k++) _rects.Add(default);   // (the game bans arrays of mod structs)
            for (int k = 0; k < rects.Count; k++) { int c = rectOf[k]; _rects[_rStart[c] + fillR[c]++] = rects[k]; }
        }
        // the real joints: only the mated mount faces (ShipStructure.Shape.cs)
        HasMounts = MountJoints && mounts != null && mounts.Count > 0;
        if (HasMounts) contacts = MateContacts(contacts, mounts);
        ContactSections(contacts);
        // adjacency: each pair's largest patch
        var best = new Dictionary<long, int>(contacts.Count);
        for (int i = 0; i < contacts.Count; i++)
        {
            long pk = (long)contacts[i].A * n + contacts[i].B;
            if (!best.TryGetValue(pk, out int j) || contacts[j].Area < contacts[i].Area) best[pk] = i;
        }
        var adjStart = new int[n + 1];
        foreach (var ci in best.Values) { adjStart[contacts[ci].A + 1]++; adjStart[contacts[ci].B + 1]++; }
        for (int i = 0; i < n; i++) adjStart[i + 1] += adjStart[i];
        var adj = new int[adjStart[n]]; var adjC = new int[adjStart[n]]; var fill = new int[n];
        foreach (var ci in best.Values)
        {
            int a = contacts[ci].A, b = contacts[ci].B;
            adj[adjStart[a] + fill[a]] = b; adjC[adjStart[a] + fill[a]++] = ci;
            adj[adjStart[b] + fill[b]] = a; adjC[adjStart[b] + fill[b]++] = ci;
        }
        _contacts = contacts;
        BuildTree(adjStart, adj, adjC);
        BuildCuts();
    }

    List<Contact> _contacts;
    internal static int Ci(Vector3I v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
    IReadOnlyList<int> _boxBlock; IReadOnlyList<Vector3I> _boxLo, _boxHi;
    Dictionary<long, int> _bucketId; int[] _bkStart, _bkBox;
    /// <summary>The wings' cells' blocks found by BlockAt (StructureEval.WingBlocksOf: one evaluation at a time uses it).</summary>
    internal readonly List<StructureEval.WingBlocks> WingBlockCache = new();

    /// <summary>Two disjoint boxes share part of a face.</summary>
    static bool Touch(Vector3I al, Vector3I ah, Vector3I bl, Vector3I bh)
    {
        for (int ax = 0; ax < 3; ax++)
        {
            if (Ci(ah, ax) + 1 != Ci(bl, ax) && Ci(bh, ax) + 1 != Ci(al, ax)) continue;
            int u = (ax + 1) % 3, v = (ax + 2) % 3;
            if (Math.Min(Ci(ah, u), Ci(bh, u)) >= Math.Max(Ci(al, u), Ci(bl, u)) && Math.Min(Ci(ah, v), Ci(bh, v)) >= Math.Max(Ci(al, v), Ci(bl, v))) return true;
        }
        return false;
    }

    /// <summary>The lowest id of the buckets two boxes both reach.</summary>
    static int FirstCommonBucket(Dictionary<long, int> ids, Vector3I al, Vector3I ah, Vector3I bl, Vector3I bh)
    {
        int best = int.MaxValue;
        int x1 = Math.Min(BFd(ah.X + 1), BFd(bh.X + 1)), y1 = Math.Min(BFd(ah.Y + 1), BFd(bh.Y + 1)), z1 = Math.Min(BFd(ah.Z + 1), BFd(bh.Z + 1));
        for (int x = Math.Max(BFd(al.X - 1), BFd(bl.X - 1)); x <= x1; x++)
            for (int y = Math.Max(BFd(al.Y - 1), BFd(bl.Y - 1)); y <= y1; y++)
                for (int z = Math.Max(BFd(al.Z - 1), BFd(bl.Z - 1)); z <= z1; z++)
                    if (ids.TryGetValue(BKey(x, y, z), out int id) && id < best) best = id;
        return best;
    }
    const int Bk = 8;
    static long BKey(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);
    static int BFd(int a) => a >= 0 ? a / Bk : -((-a + Bk - 1) / Bk);

    /// <summary>The block whose cells hold this point (m, grid-local), or the nearest within a cell (-1: none). For a
    /// hull face: its centre a little inside, against its own normal.</summary>
    public int BlockAt(Vector3 p)
    {
        var c = new Vector3I((int)MathF.Round(p.X / Cell), (int)MathF.Round(p.Y / Cell), (int)MathF.Round(p.Z / Cell));
        if (_bucketId == null || !_bucketId.TryGetValue(BKey(BFd(c.X), BFd(c.Y), BFd(c.Z)), out int bkt)) return -1;
        int best = -1; float bd = float.MaxValue;
        for (int e = _bkStart[bkt]; e < _bkStart[bkt + 1]; e++)
        {
            int k = _bkBox[e];
            var lo = _boxLo[k]; var hi = _boxHi[k];
            // distance (cells) from the point to the box
            float dx = MathF.Max(0f, MathF.Max(lo.X - 0.5f - p.X / Cell, p.X / Cell - hi.X - 0.5f));
            float dy = MathF.Max(0f, MathF.Max(lo.Y - 0.5f - p.Y / Cell, p.Y / Cell - hi.Y - 0.5f));
            float dz = MathF.Max(0f, MathF.Max(lo.Z - 0.5f - p.Z / Cell, p.Z / Cell - hi.Z - 0.5f));
            float d = dx * dx + dy * dy + dz * dz;
            if (d < bd) { bd = d; best = _boxBlock[k]; if (d == 0f) break; }
        }
        return bd <= 1.01f ? best : -1;
    }

    void BuildTree(int[] adjStart, int[] adj, int[] adjC)
    {
        int n = Count;
        Parent = new int[n]; Order = new int[n]; Piece = new int[n];
        for (int i = 0; i < n; i++) Parent[i] = -2;
        JAxis = new int[n]; JSide = new int[n]; JPoint = new Vector3[n]; JA = new float[n]; JB = new float[n]; JArea = new float[n]; JCut = new int[n];
        _parentContact = new int[n];
        // THE STIFFEST SPANNING TREE (Kruskal): the joints as stiff as possible along it - the network's preconditioner
        // is this tree's exact solve, and the closer the tree is to the whole, the fewer iterations (a tree of short
        // routes through pinhole patches, with stiff whole-face joints closing loops around it, never converged)
        var edges = new List<int>(_contacts.Count);
        var weight = new float[_contacts.Count]; var weighed = new bool[_contacts.Count];
        for (int i = 0; i < n; i++)
            for (int e = adjStart[i]; e < adjStart[i + 1]; e++)
            {
                int ci = adjC[e];
                if (adj[e] < i || weighed[ci]) continue;
                var c = _contacts[ci];
                float L = MathF.Max(MathF.Abs(C(Center[c.B] - Center[c.A], c.Axis)), 0.125f);
                weight[ci] = _cA[ci] / L; weighed[ci] = true;   // (the patch's steel - ContactSections)
                edges.Add(ci);
            }
        edges.Sort((x, y) => weight[y].CompareTo(weight[x]));
        var uf = new int[n];
        for (int i = 0; i < n; i++) uf[i] = i;
        int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
        var tStart = new int[n + 1]; var chosen = new List<int>();
        foreach (int ci in edges)
        {
            int ra = Find(_contacts[ci].A), rb = Find(_contacts[ci].B);
            if (ra == rb) continue;
            uf[ra] = rb; chosen.Add(ci);
            tStart[_contacts[ci].A + 1]++; tStart[_contacts[ci].B + 1]++;
        }
        for (int i = 0; i < n; i++) tStart[i + 1] += tStart[i];
        var tAdj = new int[tStart[n]]; var tC = new int[tStart[n]]; var tFill = new int[n];
        foreach (int ci in chosen)
        {
            int a = _contacts[ci].A, b = _contacts[ci].B;
            tAdj[tStart[a] + tFill[a]] = b; tC[tStart[a] + tFill[a]++] = ci;
            tAdj[tStart[b] + tFill[b]] = a; tC[tStart[b] + tFill[b]++] = ci;
        }
        // roots: per piece, the block nearest the centre of mass; the tree walked out from it (breadth first)
        int ord = 0;
        Components = 0;
        while (ord < n)
        {
            int root = -1; float rd = float.MaxValue;
            for (int i = 0; i < n; i++)
                if (Parent[i] == -2) { float d = (Center[i] - Com).LengthSquared(); if (d < rd) { rd = d; root = i; } }
            if (root < 0) break;
            if (Components == 0) Root = root;
            Components++;
            Parent[root] = -1;
            Piece[root] = Components - 1;
            int head = ord;
            Order[ord++] = root;
            while (head < ord)
            {
                int i = Order[head++];
                for (int e = tStart[i]; e < tStart[i + 1]; e++)
                {
                    int j = tAdj[e];
                    if (Parent[j] != -2) continue;
                    Parent[j] = i; _parentContact[j] = tC[e]; Piece[j] = Piece[i];
                    Order[ord++] = j;
                }
            }
        }
        Array.Resize(ref Order, ord);
        OrderIndex = new int[n];
        for (int k = 0; k < ord; k++) OrderIndex[Order[k]] = k;
        for (int i = 0; i < n; i++)
        {
            if (Parent[i] < 0) { JCut[i] = -1; continue; }
            var c = _contacts[_parentContact[i]];
            int ax = c.Axis;
            JAxis[i] = ax;
            // the child (i) is on the side of the contact's B if i is B
            JSide[i] = i == c.B ? c.SideB : -c.SideB;
            float uc = (c.U0 + c.U1) * 0.5f, vc = (c.V0 + c.V1) * 0.5f;
            JPoint[i] = Axis(ax) * c.Plane + Axis((ax + 1) % 3) * uc + Axis((ax + 2) % 3) * vc;
            JA[i] = c.U1 - c.U0; JB[i] = c.V1 - c.V0; JArea[i] = c.Area;
        }
    }

    int[] _parentContact;

    /// <summary>The cuts: every contact patch, grouped by plane (axis, plane) and by nearness (union-find, gaps up to
    /// CutGap: a fuselage ring with windows is one section, a wing's root and a tailplane's 6 m off are two) - a plane's
    /// section. Where a cut parts the ship in two, its resultant is exact by equilibrium (the sum of all on one side),
    /// whichever joints the tree routed through.</summary>
    void BuildCuts()
    {
        int nc = _contacts.Count;
        ContactCount = nc;
        var groups = new Dictionary<long, List<int>>();
        for (int i = 0; i < nc; i++)
        {
            var c = _contacts[i];
            long key = (long)MathF.Round(c.Plane / Cell * 2f) * 3 + c.Axis;
            if (!groups.TryGetValue(key, out var l)) groups[key] = l = new List<int>();
            l.Add(i);
        }
        var uf = new int[nc];
        for (int i = 0; i < nc; i++) uf[i] = i;
        int Find(int x) { while (uf[x] != x) { uf[x] = uf[uf[x]]; x = uf[x]; } return x; }
        float Eps = CutGap;
        foreach (var l in groups.Values)
        {
            if (l.Count < 2) continue;
            // (sorted along u: a patch only meets the ones whose u-range reaches it)
            l.Sort((a, b) => _contacts[a].U0.CompareTo(_contacts[b].U0));
            for (int i = 0; i < l.Count; i++)
            {
                var a = _contacts[l[i]];
                for (int j = i + 1; j < l.Count; j++)
                {
                    var b = _contacts[l[j]];
                    if (b.U0 > a.U1 + Eps) break;
                    if (b.V0 > a.V1 + Eps || a.V0 > b.V1 + Eps) continue;
                    int ra = Find(l[i]), rb = Find(l[j]);
                    if (ra != rb) uf[ra] = rb;
                }
            }
        }
        var cutOf = new Dictionary<int, int>();
        var members = new List<List<int>>();
        ContactCut = new int[nc];
        for (int i = 0; i < nc; i++)
        {
            int r = Find(i);
            if (!cutOf.TryGetValue(r, out int c)) { cutOf[r] = c = members.Count; members.Add(new List<int>()); }
            ContactCut[i] = c; members[c].Add(i);
        }
        CutCount = members.Count;
        _cutContacts = members.ToArray();
        _cutA = new float[CutCount]; _cutIuu = new float[CutCount]; _cutIvv = new float[CutCount]; _cutTorsion = new float[CutCount];
        _cutCentroid = new Vector3[CutCount]; _cutAxis = new int[CutCount];
        for (int c = 0; c < CutCount; c++)
        {
            var l = members[c];
            int ax = _contacts[l[0]].Axis, u = (ax + 1) % 3, v = (ax + 2) % 3;
            _cutAxis[c] = ax;
            double A = 0, su = 0, sv = 0;
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue, tMin = float.MaxValue;
            foreach (int k in l)
            {
                var ct = _contacts[k];
                float t = ContactWall(k), a = ct.U1 - ct.U0, b = ct.V1 - ct.V0;
                float Ak = 2f * (a + b) * t;
                A += Ak; su += Ak * (ct.U0 + ct.U1) * 0.5f; sv += Ak * (ct.V0 + ct.V1) * 0.5f;
                u0 = MathF.Min(u0, ct.U0); u1 = MathF.Max(u1, ct.U1); v0 = MathF.Min(v0, ct.V0); v1 = MathF.Max(v1, ct.V1);
                tMin = MathF.Min(tMin, t);
            }
            float cu = (float)(su / A), cv = (float)(sv / A);
            _cutCentroid[c] = Axis(ax) * _contacts[l[0]].Plane + Axis(u) * cu + Axis(v) * cv;
            double Iuu = 0, Ivv = 0;
            foreach (int k in l)
            {
                var ct = _contacts[k];
                float t = ContactWall(k), a = ct.U1 - ct.U0, b = ct.V1 - ct.V0;
                float Ak = 2f * (a + b) * t;
                float du = (ct.U0 + ct.U1) * 0.5f - cu, dv = (ct.V0 + ct.V1) * 0.5f - cv;
                // a thin-walled a x b tube: about u (stress across v) t b^3/6 + a t b^2/2; about v, the same with a, b swapped
                Iuu += t * b * b * b / 6f + a * t * b * b / 2f + Ak * dv * dv;
                Ivv += t * a * a * a / 6f + b * t * a * a / 2f + Ak * du * du;
            }
            _cutA[c] = (float)A; _cutIuu[c] = (float)Iuu; _cutIvv[c] = (float)Ivv;
            // torsion: Bredt on the cut's outline (2 x enclosed area x wall)
            _cutTorsion[c] = 2f * (u1 - u0) * (v1 - v0) * tMin;
        }
        ContactUtil = new float[nc];
        for (int i = 0; i < Count; i++) JCut[i] = Parent[i] < 0 ? -1 : ContactCut[_parentContact[i]];
    }

    /// <summary>A contact's wall: the thinner of its two blocks'.</summary>
    float ContactWall(int k) => MathF.Min(Wall[_contacts[k].A], Wall[_contacts[k].B]);


    /// <summary>The ship's own proper acceleration and angular acceleration from the external loads (grid-local, about
    /// the centre of mass; inertia of the blocks as point masses, omega the angular velocity): what makes the loads
    /// balance. Returns them in Accel / AngAccel.</summary>
    public void RigidAccel(Vector3 omega)
    {
        Vector3 F = Vector3.Zero, Mc = Vector3.Zero;
        double ixx = 0, iyy = 0, izz = 0, ixy = 0, ixz = 0, iyz = 0;
        for (int i = 0; i < Count; i++)
        {
            var r = Center[i] - Com;
            F += ExtForce[i];
            Mc += ExtMoment[i] + Vector3.Cross(r, ExtForce[i]);
            float m = Mass[i];
            // (each block its cells, solid, of its mass: its own inertia counted here - Solve leaves it out per block)
            var ib = OwnInertia(i);
            ixx += m * (r.Y * r.Y + r.Z * r.Z) + ib.X; iyy += m * (r.X * r.X + r.Z * r.Z) + ib.Y; izz += m * (r.X * r.X + r.Y * r.Y) + ib.Z;
            ixy -= m * r.X * r.Y; ixz -= m * r.X * r.Z; iyz -= m * r.Y * r.Z;
        }
        Accel = TotalMass > 0 ? F / TotalMass : Vector3.Zero;
        // I alpha = M - omega x (I omega)
        Vector3 Iw = new Vector3((float)(ixx * omega.X + ixy * omega.Y + ixz * omega.Z), (float)(ixy * omega.X + iyy * omega.Y + iyz * omega.Z), (float)(ixz * omega.X + iyz * omega.Y + izz * omega.Z));
        var rhs = Mc - Vector3.Cross(omega, Iw);
        AngAccel = Solve3(ixx, ixy, ixz, iyy, iyz, izz, rhs);
    }

    internal static Vector3 Solve3(double a, double b, double c, double d, double e, double f, Vector3 r)
    {
        // symmetric [[a b c][b d e][c e f]]
        double det = a * (d * f - e * e) - b * (b * f - e * c) + c * (b * e - d * c);
        if (Math.Abs(det) < 1e-12) return Vector3.Zero;
        double i00 = (d * f - e * e) / det, i01 = (c * e - b * f) / det, i02 = (b * e - c * d) / det;
        double i11 = (a * f - c * c) / det, i12 = (b * c - a * e) / det, i22 = (a * d - b * b) / det;
        return new Vector3((float)(i00 * r.X + i01 * r.Y + i02 * r.Z), (float)(i01 * r.X + i11 * r.Y + i12 * r.Z), (float)(i02 * r.X + i12 * r.Y + i22 * r.Z));
    }

    /// <summary>Each piece free on its own loads: its own proper and angular acceleration from them (RigidAccel per
    /// piece), then Solve. omega: the angular velocity (grid-local). One pass over the blocks for all pieces (a ship split
    /// into many pieces - mount joints, breaks - costs no more than one).</summary>
    public void SolveFree(Vector3 omega)
    {
        int np = Math.Max(1, Components), n = Count;
        if (_pM == null || _pM.Length < np)
        {
            _pM = new double[np]; _pSum = new double[np * 9]; _pF = new Vector3[np]; _pMc = new Vector3[np];
            _pa = new Vector3[np]; _pal = new Vector3[np]; _pc = new Vector3[np];
        }
        var pm = _pM; var sum = _pSum; var pF = _pF; var pMc = _pMc;
        Array.Clear(pm, 0, np); Array.Clear(sum, 0, np * 9); Array.Clear(pF, 0, np); Array.Clear(pMc, 0, np);
        // each piece's mass and centre of mass
        for (int i = 0; i < n; i++)
        {
            int q = Piece[i]; double m = Mass[i];
            pm[q] += m; sum[9 * q] += m * Center[i].X; sum[9 * q + 1] += m * Center[i].Y; sum[9 * q + 2] += m * Center[i].Z;
        }
        for (int q = 0; q < np; q++)
        {
            double M = pm[q];
            _pc[q] = M > 0 ? new Vector3((float)(sum[9 * q] / M), (float)(sum[9 * q + 1] / M), (float)(sum[9 * q + 2] / M)) : Vector3.Zero;
            for (int j = 0; j < 9; j++) sum[9 * q + j] = 0;
        }
        // each piece's loads and inertia about its centre of mass (sum: ixx iyy izz ixy ixz iyz)
        for (int i = 0; i < n; i++)
        {
            int q = Piece[i], o = 9 * q;
            var r = Center[i] - _pc[q];
            pF[q] += ExtForce[i];
            pMc[q] += ExtMoment[i] + Vector3.Cross(r, ExtForce[i]);
            float m = Mass[i]; var ib = OwnInertia(i);
            sum[o] += m * (r.Y * r.Y + r.Z * r.Z) + ib.X; sum[o + 1] += m * (r.X * r.X + r.Z * r.Z) + ib.Y; sum[o + 2] += m * (r.X * r.X + r.Y * r.Y) + ib.Z;
            sum[o + 3] -= m * r.X * r.Y; sum[o + 4] -= m * r.X * r.Z; sum[o + 5] -= m * r.Y * r.Z;
        }
        for (int q = 0; q < np; q++)
        {
            int o = 9 * q; double ixx = sum[o], iyy = sum[o + 1], izz = sum[o + 2], ixy = sum[o + 3], ixz = sum[o + 4], iyz = sum[o + 5];
            _pa[q] = pm[q] > 0 ? pF[q] / (float)pm[q] : Vector3.Zero;
            Vector3 Iw = new Vector3((float)(ixx * omega.X + ixy * omega.Y + ixz * omega.Z), (float)(ixy * omega.X + iyy * omega.Y + iyz * omega.Z), (float)(ixz * omega.X + iyz * omega.Y + izz * omega.Z));
            _pal[q] = Solve3(ixx, ixy, ixz, iyy, iyz, izz, pMc[q] - Vector3.Cross(omega, Iw));
        }
        SolveCore(_pa, _pal, _pc, omega);
    }

    double[] _pM, _pSum;
    Vector3[] _pF, _pMc, _pa, _pal, _pc;

    /// <summary>The joints' loads and utilisations for ExtForce / ExtMoment, the ship's proper acceleration accel, its
    /// angular acceleration alpha and angular velocity omega (grid-local) - the whole grid one rigid body.</summary>
    public void Solve(Vector3 accel, Vector3 alpha, Vector3 omega)
    {
        int np = Math.Max(1, Components);
        if (_pa == null || _pa.Length < np) { _pM = null; _pa = new Vector3[np]; _pal = new Vector3[np]; _pc = new Vector3[np]; }
        for (int q = 0; q < np; q++) { _pa[q] = accel; _pal[q] = alpha; _pc[q] = Com; }
        SolveCore(_pa, _pal, _pc, omega);
        Accel = accel; AngAccel = alpha;
    }

    void SolveCore(Vector3[] pa, Vector3[] pal, Vector3[] pcom, Vector3 omega)
    {
        // (the elastic network only when it is the method: its arrays are megabytes on a big ship, unused by the slices)
        bool net = Method == 2;
        if (net && !_netReady) BuildNetwork();
        if (_gA == null) BuildSections();
        var f = net ? _f : null;
        var sF = _accF ??= new Vector3[Count]; var sM = _accM ??= new Vector3[Count];
        Array.Clear(sF, 0, Count); Array.Clear(sM, 0, Count);
        ResidualForce = Vector3.Zero; ResidualMoment = Vector3.Zero;
        // tips in (Order reversed): each block's own load plus what its children pass it, about its centre
        for (int k = Order.Length - 1; k >= 0; k--)
        {
            int i = Order[k];
            int pq = Piece[i];
            var accel = pa[pq]; var alpha = pal[pq];
            var r = Center[i] - pcom[pq];
            var aI = accel + Vector3.Cross(alpha, r) + Vector3.Cross(omega, Vector3.Cross(omega, r));
            var Fs = sF[i] + ExtForce[i] - aI * Mass[i];
            // (the block's own rotational inertia, its cells in grid axes: -(I alpha + omega x I omega))
            var Ib = OwnInertia(i);
            var own = ExtMoment[i] - Ib * alpha - Vector3.Cross(omega, Ib * omega);
            var Ms = sM[i] + own;
            // (the network's load: the block's own, about its centre)
            var fo = ExtForce[i] - aI * Mass[i]; int o = 6 * i;
            if (f != null) { f[o] = fo.X; f[o + 1] = fo.Y; f[o + 2] = fo.Z; f[o + 3] = own.X; f[o + 4] = own.Y; f[o + 5] = own.Z; }
            _blockF[i] = fo; _blockM[i] = own;
            int p = Parent[i];
            if (p < 0) { ResidualForce += Fs; ResidualMoment += Ms + Vector3.Cross(Center[i] - Com, Fs); JForce[i] = Fs; JMoment[i] = Ms; continue; }
            var Mj = Ms + Vector3.Cross(Center[i] - JPoint[i], Fs);   // (about the joint)
            JForce[i] = Fs; JMoment[i] = Mj;
            sF[p] += Fs; sM[p] += Mj + Vector3.Cross(JPoint[i] - Center[p], Fs);
        }
        if (TrimToHost) LumpTrim();
        // (what reaches the roots: the loads' imbalance - ~0 with the ship's own accelerations; then how they share)
        if (Method == 2) { SolveNetwork(); NetworkStresses(); }
        else if (Method == 1) Stresses();
        else SliceStresses();
    }

    Vector3[] _accF, _accM;

    /// <summary>Each cut's resultant - the tree joints crossing it, each counted from its child's side to its parent's
    /// (+ for a child on the + side) - on its section; each contact's utilisation (ContactUtil, and JUtil for the
    /// tree's joints), the worst (MaxUtil, MaxContact, MaxJoint: its outboard block).</summary>
    void Stresses()
    {
        int nCut = CutCount;
        var cF = _cutF ??= new Vector3[nCut]; var cM = _cutM ??= new Vector3[nCut];
        Array.Clear(cF, 0, nCut); Array.Clear(cM, 0, nCut);
        for (int j = 0; j < Count; j++)
        {
            if (Parent[j] < 0) continue;
            int c = JCut[j]; float sg = JSide[j];
            cF[c] += JForce[j] * sg;
            cM[c] += (JMoment[j] + Vector3.Cross(JPoint[j] - _cutCentroid[c], JForce[j])) * sg;
        }
        MaxUtil = 0f; MaxJoint = -1; MaxContact = -1;
        for (int c = 0; c < nCut; c++)
        {
            var l = _cutContacts[c];
            var Fc = cF[c]; var Mc = cM[c];
            if (Fc == Vector3.Zero && Mc == Vector3.Zero) { foreach (int k in l) ContactUtil[k] = 0f; continue; }
            int ax = _cutAxis[c], u = (ax + 1) % 3, v = (ax + 2) % 3;
            var cen = _cutCentroid[c];
            float A = _cutA[c], N = C(Fc, ax), Mu = C(Mc, u), Mv = C(Mc, v);
            float tau = MathF.Sqrt(C(Fc, u) * C(Fc, u) + C(Fc, v) * C(Fc, v)) / (0.5f * A) + MathF.Abs(C(Mc, ax)) / MathF.Max(_cutTorsion[c], 1e-9f);
            float iuu = MathF.Max(_cutIuu[c], 1e-12f), ivv = MathF.Max(_cutIvv[c], 1e-12f);
            foreach (int k in l)
            {
                var ct = _contacts[k];
                float sMax = 0f;
                for (int q = 0; q < 4; q++)
                {
                    float uu = ((q & 1) == 0 ? ct.U0 : ct.U1) - C(cen, u), vv = ((q & 2) == 0 ? ct.V0 : ct.V1) - C(cen, v);
                    float sgm = MathF.Abs(N / A + Mu * vv / iuu - Mv * uu / ivv);
                    if (sgm > sMax) sMax = sgm;
                }
                float vm = MathF.Sqrt(sMax * sMax + 3f * tau * tau);
                float sa = BlockStrength(ct.A);
                float sb = BlockStrength(ct.B);
                float util = vm / (MathF.Min(sa, sb) * Strength);
                ContactUtil[k] = util;
                if (util > MaxUtil) { MaxUtil = util; MaxContact = k; }
            }
        }
        for (int j = 0; j < Count; j++) JUtil[j] = Parent[j] < 0 ? 0f : ContactUtil[_parentContact[j]];
        if (MaxContact >= 0) MaxJoint = Outboard(MaxContact);
    }

    Vector3[] _cutF, _cutM;

    /// <summary>A contact's two blocks.</summary>
    public void ContactBlocks(int k, out int a, out int b) { a = _contacts[k].A; b = _contacts[k].B; }
    /// <summary>A contact's plane: its normal axis (0 x, 1 y, 2 z) and coordinate (m) - the slice a bend hinges on
    /// (StructureBend, 2026-10-14).</summary>
    public void ContactPlane(int k, out int axis, out float plane) { axis = _contacts[k].Axis; plane = _contacts[k].Plane; }
    /// <summary>A block's own net load after the last Solve (N, grid-local; its external load less its inertia in the ship's
    /// motion, trim lumped on its host) and moment about its centre (N m) - what each block adds to a slice (StructureBend).</summary>
    public Vector3 NetForce(int b) => _blockF != null ? _blockF[b] : Vector3.Zero;
    public Vector3 NetMoment(int b) => _blockM != null ? _blockM[b] : Vector3.Zero;
    /// <summary>A contact's centre (m).</summary>
    public Vector3 ContactPoint(int k)
    {
        var c = _contacts[k];
        return Axis(c.Axis) * c.Plane + Axis((c.Axis + 1) % 3) * ((c.U0 + c.U1) * 0.5f) + Axis((c.Axis + 2) % 3) * ((c.V0 + c.V1) * 0.5f);
    }
    /// <summary>The block of a contact that departs when it breaks: the one on its slice's lighter side (the tail, not
    /// the fuselage); before the slices are built, the one farther out along the tree.</summary>
    public int Outboard(int k) => ContactOutboard != null ? ContactOutboard[k] : OrderIndex[_contacts[k].A] > OrderIndex[_contacts[k].B] ? _contacts[k].A : _contacts[k].B;
    public int[] ContactOutboard;

    /// <summary>A block's strength now (Pa): its yield x StrengthAt(its temperature) x its health left.</summary>
    public float BlockStrength(int b) => StructMaterial.Yield[Mat[b]] * StructMaterial.StrengthAt(Mat[b], TempK[b]) * MathF.Max(Integrity[b], MinIntegrity);
    /// <summary>A cut's contacts (for reports and tests).</summary>
    public IReadOnlyList<int> CutContacts(int cut) => _cutContacts[cut];

    sealed class MinHeap
    {
        int[] _i; float[] _k; public int Count;
        public MinHeap(int cap) { _i = new int[Math.Max(4, cap)]; _k = new float[Math.Max(4, cap)]; }
        public void Push(int i, float k)
        {
            if (Count == _i.Length) { Array.Resize(ref _i, Count * 2); Array.Resize(ref _k, Count * 2); }
            int c = Count++;
            while (c > 0) { int p = (c - 1) >> 1; if (_k[p] <= k) break; _i[c] = _i[p]; _k[c] = _k[p]; c = p; }
            _i[c] = i; _k[c] = k;
        }
        public int Pop(out float key)
        {
            int top = _i[0]; key = _k[0];
            int li = _i[--Count]; float lk = _k[Count];
            int c = 0;
            while (true)
            {
                int l = 2 * c + 1; if (l >= Count) break;
                int r = l + 1, m = r < Count && _k[r] < _k[l] ? r : l;
                if (_k[m] >= lk) break;
                _i[c] = _i[m]; _k[c] = _k[m]; c = m;
            }
            _i[c] = li; _k[c] = lk;
            return top;
        }
    }
}
