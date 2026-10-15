#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// REAL SHAPES AND REAL JOINTS (2026-10-14) - each block as its occupied 0.25 m cells (the game's, from the model's
/// collider; its mass is spread uniformly over them), not its bounding box; two blocks joined through their mated mount
/// points, not every face they touch.
///
/// SHELLS AND LATTICES. A compact block (armour, a tank, a thruster: its cells a solid lump) is a hollow steel shell over
/// its real surface - wall = mass / (steel x the cells' exposed surface), its walls cut by a plane the outline of the cells
/// it cuts (a thin wall: the bounding-box tube for a cube, as before; the real staircase for a slope). A THIN block - a
/// truss's four one-cell walls, a catwalk's slab, a pillar, a handrail: its longest side at least LatticeAspect times its
/// thickness (2 x cells / exposed faces) - is a lattice: its steel smeared through its cells (the density mass / cells),
/// a cut's section the cells it cuts as solid at that density, and its walls buckle as plates of their own thickness
/// over their own width (a 0.25 m deep lattice wall 2.5 m wide does not buckle; a 1.2 mm box wall does, at nothing).
///
/// JOINTS. Without mount data: every face two blocks' cells share (as before), the patch its real rectangles - not their
/// bounding rectangle. With it: a face carries only where block A has a mount face at (cell c, direction d) and block B one
/// at (c + d, -d) - the game's own test for placement and connectivity; touching faces with no mated mounts carry nothing.
/// A contact's section: the weaker of its two blocks' material over the patch (a shell's walls round the patch outline, a
/// lattice's cells over its area).
///
/// Cost: a shape's exposed surface is cached by its cells (per definition and orientation); everything else per grid at
/// a rebuild; the solve is unchanged (one piece per contact and per straddling block, as before).
/// </summary>
public sealed partial class ShipStructure
{
    /// <summary>A block is a lattice (its cells are its material: trusses, catwalks, pillars, rails) when its longest side is
    /// at least this many times its thickness (2 x cells / exposed cell faces): a truss 11, a catwalk slab 12, a pillar 21;
    /// a cube 3, a slope 4.2, a corner 5.</summary>
    public static float LatticeAspect = 8f;
    /// <summary>Off: every block a shell (the lattice rule off, for A/B).</summary>
    public static bool Lattices = true;
    /// <summary>Joints through mated mount faces when the blocks' mount points are given (off: every touching face, as
    /// without them - for A/B).</summary>
    public static bool MountJoints = false;   // (off 2026-10-14: the SSTO's wings mate on 2 of 36 cells and fell off at 2.4 g - pending the user)

    /// <summary>Each block's steel fraction (its mass over its cells' volume as steel) - a lattice's smeared density.</summary>
    public float[] Phi;
    /// <summary>A lattice block's plate thickness (m: 2 x cells / exposed faces); 0 a shell.</summary>
    public float[] Thick;
    /// <summary>Each block's mean squared extent about its centre along x, y, z (m2; its cells uniform): its own inertia per kg
    /// about the grid axes is (gy + gz, gx + gz, gx + gy).</summary>
    public Vector3[] Gyr;
    /// <summary>Whether the structure was built with mount points (its joints the mated mount faces).</summary>
    public bool HasMounts;

    /// <summary>A block's own rotational inertia (diagonal, grid axes).</summary>
    Vector3 OwnInertia(int i) { var g = Gyr[i]; float m = Mass[i]; return new Vector3(m * (g.Y + g.Z), m * (g.X + g.Z), m * (g.X + g.Y)); }

    // ── boxes by block ──
    int[] _bStart, _bIdx;

    // ── the shape cache: a shape's exposed cell faces, by its boxes relative to its lowest cell ──
    sealed class ShapeEntry { public int[] Boxes; public int Faces; }
    static readonly Dictionary<long, List<ShapeEntry>> _shapes = new Dictionary<long, List<ShapeEntry>>();
    static int _shapeCount;
    /// <summary>(tests, benches) shape-cache lookups and misses since start.</summary>
    public static int ShapeLookups, ShapeMisses;

    /// <summary>The exposed cell faces of block b's cells (its boxes are disjoint): every box's faces less twice the faces
    /// its boxes share - cached by shape.</summary>
    int ExposedFaces(int b, IReadOnlyList<Vector3I> boxLo, IReadOnlyList<Vector3I> boxHi, Vector3I o)
    {
        int s = _bStart[b], e = _bStart[b + 1];
        if (e - s == 1) { var d = boxHi[_bIdx[s]] - boxLo[_bIdx[s]] + Vector3I.One; return 2 * (d.X * d.Y + d.Y * d.Z + d.Z * d.X); }
        long h = e - s;
        for (int q = s; q < e; q++)
        {
            var l = boxLo[_bIdx[q]] - o; var u = boxHi[_bIdx[q]] - o;
            h = h * 1000003L + ((l.X * 73 + l.Y) * 73 + l.Z); h = h * 1000003L + ((u.X * 73 + u.Y) * 73 + u.Z);
        }
        lock (_shapes)
        {
            ShapeLookups++;
            if (_shapes.TryGetValue(h, out var list))
                foreach (var en in list)
                {
                    if (en.Boxes.Length != 6 * (e - s)) continue;
                    bool same = true;
                    for (int q = s, j = 0; q < e && same; q++, j += 6)
                    {
                        var l = boxLo[_bIdx[q]] - o; var u = boxHi[_bIdx[q]] - o;
                        same = en.Boxes[j] == l.X && en.Boxes[j + 1] == l.Y && en.Boxes[j + 2] == l.Z && en.Boxes[j + 3] == u.X && en.Boxes[j + 4] == u.Y && en.Boxes[j + 5] == u.Z;
                    }
                    if (same) return en.Faces;
                }
        }
        // a miss: count it
        long faces = 0;
        for (int q = s; q < e; q++)
        {
            var d = boxHi[_bIdx[q]] - boxLo[_bIdx[q]] + Vector3I.One;
            faces += 2L * (d.X * d.Y + d.Y * d.Z + d.Z * d.X);
            for (int r = q + 1; r < e; r++) faces -= 2L * SharedFaces(boxLo[_bIdx[q]], boxHi[_bIdx[q]], boxLo[_bIdx[r]], boxHi[_bIdx[r]]);
        }
        var ent = new ShapeEntry { Boxes = new int[6 * (e - s)], Faces = (int)faces };
        for (int q = s, j = 0; q < e; q++, j += 6)
        {
            var l = boxLo[_bIdx[q]] - o; var u = boxHi[_bIdx[q]] - o;
            ent.Boxes[j] = l.X; ent.Boxes[j + 1] = l.Y; ent.Boxes[j + 2] = l.Z; ent.Boxes[j + 3] = u.X; ent.Boxes[j + 4] = u.Y; ent.Boxes[j + 5] = u.Z;
        }
        lock (_shapes)
        {
            ShapeMisses++;
            if (_shapeCount > 8192) { _shapes.Clear(); _shapeCount = 0; }
            if (!_shapes.TryGetValue(h, out var list)) _shapes[h] = list = new List<ShapeEntry>(1);
            list.Add(ent); _shapeCount++;
        }
        return ent.Faces;
    }

    /// <summary>The cell faces two disjoint boxes share.</summary>
    static int SharedFaces(Vector3I al, Vector3I ah, Vector3I bl, Vector3I bh)
    {
        for (int ax = 0; ax < 3; ax++)
        {
            if (Ci(ah, ax) + 1 != Ci(bl, ax) && Ci(bh, ax) + 1 != Ci(al, ax)) continue;
            int u = (ax + 1) % 3, v = (ax + 2) % 3;
            int du = Math.Min(Ci(ah, u), Ci(bh, u)) - Math.Max(Ci(al, u), Ci(bl, u)) + 1, dv = Math.Min(Ci(ah, v), Ci(bh, v)) - Math.Max(Ci(al, v), Ci(bl, v)) + 1;
            return du > 0 && dv > 0 ? du * dv : 0;
        }
        return 0;
    }

    /// <summary>Per block, from its cells: boxes by block, centre, bounding box, own inertia, wall, lattice thickness, steel
    /// fraction (Build).</summary>
    void BuildShapes(IReadOnlyList<int> boxBlock, IReadOnlyList<Vector3I> boxLo, IReadOnlyList<Vector3I> boxHi)
    {
        int n = Count, nb = boxBlock.Count;
        _bStart = new int[n + 1]; _bIdx = new int[nb];
        for (int k = 0; k < nb; k++) _bStart[boxBlock[k] + 1]++;
        for (int i = 0; i < n; i++) _bStart[i + 1] += _bStart[i];
        var fill = new int[n];
        for (int k = 0; k < nb; k++) { int b = boxBlock[k]; _bIdx[_bStart[b] + fill[b]++] = k; }
        Phi = new float[n]; Thick = new float[n]; Gyr = new Vector3[n];
        const float C3 = Cell * Cell * Cell;
        for (int i = 0; i < n; i++)
        {
            int s = _bStart[i], e = _bStart[i + 1];
            if (s == e) { Wall[i] = 1e-4f; continue; }
            double vol = 0, cx = 0, cy = 0, cz = 0;
            var o = boxLo[_bIdx[s]]; var top = boxHi[_bIdx[s]];
            for (int q = s; q < e; q++)
            {
                var lo = boxLo[_bIdx[q]]; var hi = boxHi[_bIdx[q]];
                o = Vector3I.Min(o, lo); top = Vector3I.Max(top, hi);
                double v = (double)(hi.X - lo.X + 1) * (hi.Y - lo.Y + 1) * (hi.Z - lo.Z + 1);
                vol += v; cx += v * (lo.X + hi.X) * 0.5; cy += v * (lo.Y + hi.Y) * 0.5; cz += v * (lo.Z + hi.Z) * 0.5;
            }
            Lo[i] = new Vector3(o.X - 0.5f, o.Y - 0.5f, o.Z - 0.5f) * Cell; Hi[i] = new Vector3(top.X + 0.5f, top.Y + 0.5f, top.Z + 0.5f) * Cell;
            double mx = cx / vol, my = cy / vol, mz = cz / vol;
            Center[i] = new Vector3((float)mx, (float)my, (float)mz) * Cell;
            // own inertia: each box solid, its own (d^2 - 1)/12... in cells: a run of d cells about its middle has <x^2> = (d^2)/12 (continuous)
            double gx = 0, gy = 0, gz = 0;
            for (int q = s; q < e; q++)
            {
                var lo = boxLo[_bIdx[q]]; var hi = boxHi[_bIdx[q]];
                double dx = hi.X - lo.X + 1, dy = hi.Y - lo.Y + 1, dz = hi.Z - lo.Z + 1, v = dx * dy * dz;
                double ox = (lo.X + hi.X) * 0.5 - mx, oy = (lo.Y + hi.Y) * 0.5 - my, oz = (lo.Z + hi.Z) * 0.5 - mz;
                gx += v * (dx * dx / 12.0 + ox * ox); gy += v * (dy * dy / 12.0 + oy * oy); gz += v * (dz * dz / 12.0 + oz * oz);
            }
            Gyr[i] = new Vector3((float)(gx / vol), (float)(gy / vol), (float)(gz / vol)) * (Cell * Cell);
            int faces = ExposedFaces(i, boxLo, boxHi, o);
            float area = faces * Cell * Cell;
            Wall[i] = StructMaterial.WallOver(Mat[i], Mass[i], area);
            float phi = Mass[i] / (float)(vol * C3 * StructMaterial.SteelDensity);
            float hCells = (float)(2.0 * vol / Math.Max(faces, 1));
            var ext = top - o + Vector3I.One; int lmax = Math.Max(ext.X, Math.Max(ext.Y, ext.Z));
            if (Lattices && lmax >= LatticeAspect * hCells)
            {
                Thick[i] = hCells * Cell;
                // (a fitting's load-bearing steel capped as its shell is: two walls of FunctionalWall through the plate)
                if (StructMaterial.CappedWall(Mat[i])) phi = MathF.Min(phi, 2f * StructMaterial.FunctionalWall / Thick[i]);
            }
            Phi[i] = phi;
        }
    }

    // ── rectangles in a plane: cells (u0..u1, v0..v1 inclusive) ──
    struct Rect { public int U0, U1, V0, V1; }

    /// <summary>A section of rectangles of cells: as a THIN WALL round the outline of their union (t per metre of outline:
    /// what a hollow block's walls are where the plane cuts them) - area, centroid, second moments about the centroid per
    /// unit wall (Iuu: stress varying along v). Shared edges between the rectangles are not walls.</summary>
    static void Outline(List<Rect> r, int s, int e, out float len, out float cu, out float cv, out float iuu, out float ivv)
    {
        if (e - s == 1)
        {
            // (one rectangle: the thin-walled a x b tube - t b^3/6 + a t b^2/2 per unit wall, and the same across)
            var q = r[s]; float a = (q.U1 - q.U0 + 1) * Cell, b = (q.V1 - q.V0 + 1) * Cell;
            len = 2f * (a + b); cu = 0.5f * (q.U0 + q.U1) * Cell; cv = 0.5f * (q.V0 + q.V1) * Cell;
            iuu = b * b * b / 6f + a * b * b / 2f; ivv = a * a * a / 6f + b * a * a / 2f;
            return;
        }
        double L = 0, Su = 0, Sv = 0, Suu = 0, Svv = 0;
        void Seg(bool alongU, double at, double a, double b, double w)
        {
            double l = (b - a) * w; L += l;
            if (alongU) { Su += w * (b * b - a * a) / 2; Sv += at * l; Suu += w * (b * b * b - a * a * a) / 3; Svv += at * at * l; }
            else { Sv += w * (b * b - a * a) / 2; Su += at * l; Svv += w * (b * b * b - a * a * a) / 3; Suu += at * at * l; }
        }
        for (int i = s; i < e; i++)
        {
            var q = r[i];
            double u0 = (q.U0 - 0.5) * Cell, u1 = (q.U1 + 0.5) * Cell, v0 = (q.V0 - 0.5) * Cell, v1 = (q.V1 + 0.5) * Cell;
            Seg(true, v0, u0, u1, 1); Seg(true, v1, u0, u1, 1); Seg(false, u0, v0, v1, 1); Seg(false, u1, v0, v1, 1);
            for (int j = i + 1; j < e; j++)
            {
                var p = r[j];
                // an edge shared with a neighbouring rectangle: inside the union, twice counted - taken off twice
                if (q.U1 + 1 == p.U0 || p.U1 + 1 == q.U0)
                {
                    int a = Math.Max(q.V0, p.V0), b = Math.Min(q.V1, p.V1);
                    if (b >= a) Seg(false, (q.U1 + 1 == p.U0 ? q.U1 + 0.5 : q.U0 - 0.5) * Cell, (a - 0.5) * Cell, (b + 0.5) * Cell, -2);
                }
                if (q.V1 + 1 == p.V0 || p.V1 + 1 == q.V0)
                {
                    int a = Math.Max(q.U0, p.U0), b = Math.Min(q.U1, p.U1);
                    if (b >= a) Seg(true, (q.V1 + 1 == p.V0 ? q.V1 + 0.5 : q.V0 - 0.5) * Cell, (a - 0.5) * Cell, (b + 0.5) * Cell, -2);
                }
            }
        }
        len = (float)L;
        if (L <= 1e-9) { cu = cv = iuu = ivv = 0f; return; }
        double mu = Su / L, mv = Sv / L;
        cu = (float)mu; cv = (float)mv; iuu = (float)Math.Max(Svv - L * mv * mv, 0); ivv = (float)Math.Max(Suu - L * mu * mu, 0);
    }

    /// <summary>The same rectangles as SOLID (a lattice's cells): area, centroid, second moments about it.</summary>
    static void Solid(List<Rect> r, int s, int e, out float area, out float cu, out float cv, out float iuu, out float ivv)
    {
        double A = 0, Su = 0, Sv = 0, Suu = 0, Svv = 0;
        for (int i = s; i < e; i++)
        {
            var q = r[i];
            double a = (q.U1 - q.U0 + 1) * Cell, b = (q.V1 - q.V0 + 1) * Cell, x = 0.5 * (q.U0 + q.U1) * Cell, y = 0.5 * (q.V0 + q.V1) * Cell, ar = a * b;
            A += ar; Su += ar * x; Sv += ar * y; Suu += ar * (a * a / 12 + x * x); Svv += ar * (b * b / 12 + y * y);
        }
        area = (float)A;
        if (A <= 1e-12) { cu = cv = iuu = ivv = 0f; return; }
        double mu = Su / A, mv = Sv / A;
        cu = (float)mu; cv = (float)mv; iuu = (float)Math.Max(Svv - A * mv * mv, 0); ivv = (float)Math.Max(Suu - A * mu * mu, 0);
    }

    /// <summary>Block b's material over these rectangles: a shell's walls round their outline, a lattice's cells over their
    /// area (steel m2, centroid, second moments, and the wall that carries the shear flow).</summary>
    void Material(int b, List<Rect> r, int s, int e, out float A, out float cu, out float cv, out float iuu, out float ivv, out float t)
    {
        if (Thick[b] > 0f) Solid(r, s, e, out A, out cu, out cv, out iuu, out ivv);
        else Outline(r, s, e, out A, out cu, out cv, out iuu, out ivv);
        Scale(b, ref A, ref iuu, ref ivv, out t);
    }

    /// <summary>A section's geometry (Outline for a shell, Solid for a lattice) as block b's steel.</summary>
    void Scale(int b, ref float A, ref float iuu, ref float ivv, out float t)
    {
        float k = Thick[b] > 0f ? Phi[b] : Wall[b];
        A *= k; iuu *= k; ivv *= k; t = Thick[b] > 0f ? Phi[b] * Thick[b] : Wall[b];
    }

    /// <summary>Block b's plate for buckling where it meets the plane between cell layers p and p + 1 on axis ax: its
    /// thickness (a shell's wall, a lattice's own depth) and its width - the widest extent of its cells in the layer next to
    /// the plane (side -1: layer p, +1: p + 1, 0: both - a block straddling it).</summary>
    void Plate(int b, int ax, int p, int side, out float T, out float W)
    {
        int u = (ax + 1) % 3, v = (ax + 2) % 3;
        int u0 = int.MaxValue, u1 = int.MinValue, v0 = int.MaxValue, v1 = int.MinValue;
        for (int q = _bStart[b]; q < _bStart[b + 1]; q++)
        {
            int k = _bIdx[q]; int lo = Ci(_boxLo[k], ax), hi = Ci(_boxHi[k], ax);
            bool hit = side < 0 ? lo <= p && hi >= p : side > 0 ? lo <= p + 1 && hi >= p + 1 : lo <= p + 1 && hi >= p;
            if (!hit) continue;
            u0 = Math.Min(u0, Ci(_boxLo[k], u)); u1 = Math.Max(u1, Ci(_boxHi[k], u)); v0 = Math.Min(v0, Ci(_boxLo[k], v)); v1 = Math.Max(v1, Ci(_boxHi[k], v));
        }
        W = u1 >= u0 ? Math.Max(u1 - u0 + 1, v1 - v0 + 1) * Cell : 0f;
        T = Thick[b] > 0f ? Thick[b] : Wall[b];
    }

    // ── contacts' rectangles (by contact: _rStart) and sections ──
    int[] _rStart;
    List<Rect> _rects;
    /// <summary>Per contact: its section's steel (m2), centroid (m, along u and v), second moments about it (m4), shear wall
    /// (m); each block's plate for buckling (thickness, width: A's, B's).</summary>
    float[] _cA, _cCu, _cCv, _cIuu, _cIvv, _cT, _cTa, _cWa, _cTb, _cWb;
    /// <summary>(tests, benches) the contacts kept by mated mounts, the ones dropped (touching, nothing mated), and the
    /// ones kept whole because a block had no mount data.</summary>
    public int MatedContacts, UnmatedContacts, UnmountedContacts;

    /// <summary>The contacts through mated mount faces only: per touching patch, the lower block's mount faces looking up
    /// (+axis) at layer P against the upper block's looking down (-axis) at P + 1 - their overlap is the joint; none, the
    /// contact goes. A block with no mount data at all keeps its touching faces. (Not modelled: the game's per-mount
    /// enabled state - IsMountPointEnabled, a fractured block's mounts switched off - every definition mount counts.)</summary>
    List<Contact> MateContacts(List<Contact> contacts, IReadOnlyList<(int block, Vector3I dir, Vector3I lo, Vector3I hi)> mounts)
    {
        int n = Count;
        var mStart = new int[n + 1];
        foreach (var m in mounts) if ((uint)m.block < (uint)n) mStart[m.block + 1]++;
        for (int i = 0; i < n; i++) mStart[i + 1] += mStart[i];
        var mIdx = new int[mStart[n]]; var fill = new int[n];
        for (int k = 0; k < mounts.Count; k++) { int b = mounts[k].block; if ((uint)b < (uint)n) mIdx[mStart[b] + fill[b]++] = k; }
        var outC = new List<Contact>(contacts.Count); var outR = new List<Rect>(_rects.Count); var start = new List<int>(contacts.Count + 1) { 0 };
        MatedContacts = UnmatedContacts = UnmountedContacts = 0;
        for (int ci = 0; ci < contacts.Count; ci++)
        {
            var c = contacts[ci];
            int lower = c.SideB > 0 ? c.A : c.B, upper = c.SideB > 0 ? c.B : c.A;
            if (mStart[lower + 1] == mStart[lower] || mStart[upper + 1] == mStart[upper])
            {
                for (int q = _rStart[ci]; q < _rStart[ci + 1]; q++) outR.Add(_rects[q]);
                outC.Add(c); start.Add(outR.Count); UnmountedContacts++;
                continue;
            }
            int ax = c.Axis, u = (ax + 1) % 3, v = (ax + 2) % 3, r0 = outR.Count;
            int bu0 = int.MaxValue, bu1 = int.MinValue, bv0 = int.MaxValue, bv1 = int.MinValue, cells = 0;
            for (int q = mStart[lower]; q < mStart[lower + 1]; q++)
            {
                var ml = mounts[mIdx[q]];
                if (Ci(ml.dir, ax) != 1 || Ci(ml.lo, ax) > c.P || Ci(ml.hi, ax) < c.P) continue;
                for (int w = mStart[upper]; w < mStart[upper + 1]; w++)
                {
                    var mu = mounts[mIdx[w]];
                    if (Ci(mu.dir, ax) != -1 || Ci(mu.lo, ax) > c.P + 1 || Ci(mu.hi, ax) < c.P + 1) continue;
                    int a0 = Math.Max(Ci(ml.lo, u), Ci(mu.lo, u)), a1 = Math.Min(Ci(ml.hi, u), Ci(mu.hi, u));
                    int b0 = Math.Max(Ci(ml.lo, v), Ci(mu.lo, v)), b1 = Math.Min(Ci(ml.hi, v), Ci(mu.hi, v));
                    if (a1 < a0 || b1 < b0) continue;
                    outR.Add(new Rect { U0 = a0, U1 = a1, V0 = b0, V1 = b1 });
                    bu0 = Math.Min(bu0, a0); bu1 = Math.Max(bu1, a1); bv0 = Math.Min(bv0, b0); bv1 = Math.Max(bv1, b1); cells += (a1 - a0 + 1) * (b1 - b0 + 1);
                }
            }
            if (cells == 0) { UnmatedContacts++; continue; }   // (touching, nothing mated: no joint)
            c.U0 = (bu0 - 0.5f) * Cell; c.U1 = (bu1 + 0.5f) * Cell; c.V0 = (bv0 - 0.5f) * Cell; c.V1 = (bv1 + 0.5f) * Cell; c.Area = cells * Cell * Cell;
            outC.Add(c); start.Add(outR.Count); MatedContacts++;
        }
        _rects = outR; _rStart = start.ToArray();
        return outC;
    }

    /// <summary>Each contact's section: the weaker of its two blocks' material over its patch (Material), and both blocks'
    /// plates there (Plate).</summary>
    void ContactSections(List<Contact> contacts)
    {
        int nc = contacts.Count;
        _cA = new float[nc]; _cCu = new float[nc]; _cCv = new float[nc]; _cIuu = new float[nc]; _cIvv = new float[nc]; _cT = new float[nc];
        _cTa = new float[nc]; _cWa = new float[nc]; _cTb = new float[nc]; _cWb = new float[nc];
        for (int k = 0; k < nc; k++)
        {
            var c = contacts[k];
            int s = _rStart[k], e = _rStart[k + 1];
            // (the patch's geometry once - as a wall round its outline, as solid cells - each side's steel from it)
            float oL = 0f, oU = 0f, oV = 0f, oIu = 0f, oIv = 0f, sA = 0f, sU = 0f, sV = 0f, sIu = 0f, sIv = 0f;
            if (Thick[c.A] == 0f || Thick[c.B] == 0f) Outline(_rects, s, e, out oL, out oU, out oV, out oIu, out oIv);
            if (Thick[c.A] > 0f || Thick[c.B] > 0f) Solid(_rects, s, e, out sA, out sU, out sV, out sIu, out sIv);
            bool la = Thick[c.A] > 0f, lb = Thick[c.B] > 0f;
            float aA = la ? sA : oL, uA = la ? sU : oU, vA = la ? sV : oV, iuA = la ? sIu : oIu, ivA = la ? sIv : oIv;
            float aB = lb ? sA : oL, uB = lb ? sU : oU, vB = lb ? sV : oV, iuB = lb ? sIu : oIu, ivB = lb ? sIv : oIv;
            Scale(c.A, ref aA, ref iuA, ref ivA, out float tA);
            Scale(c.B, ref aB, ref iuB, ref ivB, out float tB);
            if (aA <= aB) { _cA[k] = aA; _cCu[k] = uA; _cCv[k] = vA; _cIuu[k] = iuA; _cIvv[k] = ivA; }
            else { _cA[k] = aB; _cCu[k] = uB; _cCv[k] = vB; _cIuu[k] = iuB; _cIvv[k] = ivB; }
            _cT[k] = MathF.Min(tA, tB);
            Plate(c.A, c.Axis, c.P, c.SideB > 0 ? -1 : 1, out _cTa[k], out _cWa[k]);
            Plate(c.B, c.Axis, c.P, c.SideB > 0 ? 1 : -1, out _cTb[k], out _cWb[k]);
        }
    }

    /// <summary>The cells block b has on both sides of the plane between layers p and p + 1 (its own material across it) into
    /// r: boxes through it, and where one of its boxes ends at it and another starts, their overlap.</summary>
    void CutCells(int b, int ax, int p, List<Rect> r)
    {
        int u = (ax + 1) % 3, v = (ax + 2) % 3;
        int s = _bStart[b], e = _bStart[b + 1];
        var ends = _ends; var starts = _starts; ends.Clear(); starts.Clear();
        for (int q = s; q < e; q++)
        {
            int k = _bIdx[q]; int lo = Ci(_boxLo[k], ax), hi = Ci(_boxHi[k], ax);
            if (lo <= p && hi >= p + 1) r.Add(new Rect { U0 = Ci(_boxLo[k], u), U1 = Ci(_boxHi[k], u), V0 = Ci(_boxLo[k], v), V1 = Ci(_boxHi[k], v) });
            else if (hi == p) ends.Add(k);
            else if (lo == p + 1) starts.Add(k);
        }
        // a box ending at the plane: its overlap with each box starting there
        foreach (int k in ends)
            foreach (int k2 in starts)
            {
                int a0 = Math.Max(Ci(_boxLo[k], u), Ci(_boxLo[k2], u)), a1 = Math.Min(Ci(_boxHi[k], u), Ci(_boxHi[k2], u));
                int b0 = Math.Max(Ci(_boxLo[k], v), Ci(_boxLo[k2], v)), b1 = Math.Min(Ci(_boxHi[k], v), Ci(_boxHi[k2], v));
                if (a1 >= a0 && b1 >= b0) r.Add(new Rect { U0 = a0, U1 = a1, V0 = b0, V1 = b1 });
            }
    }
    readonly List<int> _ends = new List<int>(), _starts = new List<int>();
}
