#pragma warning disable
using System;
using System.Collections.Generic;
namespace AeroMod;

/// <summary>
/// ONE EVALUATION'S LOADS AND DAMAGE - pure, no game: the game's background evaluation (Structure/Server/StructureServer)
/// and the offline failure simulation (AeroBench 'struct', 'fail') run exactly this.
///
/// Loads: the wings' forces spread over their cells; the rest of the air (the applied total less the wings) over the
/// hull's windward faces by Newtonian weights (cos^2 x area, the heat model's last pass), its moment about the centre of
/// mass matched by a couple spread the same way; each thruster's force at its block; the gyros' torque shared by the
/// gyro blocks; a railed re-entry's braking (EntryLoads) as a pressure over the same windward faces. Damage
/// (PROGRESSIVE, 2026-10-14 - Structure/Model/StructureFatigue; BY SECTION, 2026-10-15 - DamageBySection): every member of a
/// loaded CUT (the method of sections' cut through one part of the ship: its patches and the blocks straddling it) loses
/// health at StructureFatigue.HealthRate of its own utilisation - fatigue past the endurance limit, yield wear past 1 -
/// times the time, so the whole neck tires together; the CUT fails when a member's health is exhausted, or at once at
/// SnapUtil, and then every member of it goes in the same evaluation (the worst MaxBreaks cuts an evaluation, each whole or
/// not at all); blocks past their burn temperature lose health at their burn rate, and go when it is out.
/// </summary>
public static class StructureEval
{
    /// <summary>YIELD WEAR (2026-10-14, progressive - no break here any more: StructureFatigue): at utilisation BreakUtil a
    /// joint loses WearPerSecond of its health a second, in proportion to the overshoot past 1 (Structure.BreakAt,
    /// Structure.WearRate; 0.45 puts the AeroBench fail sims' first break where the old instant one was: the Tu-134 at
    /// 3.8-3.9 g, Plane2 at 13.2-13.7 g); most blocks broken per evaluation.</summary>
    public static float BreakUtil = 1.5f, WearPerSecond = 0.45f;
    /// <summary>Most CUTS failed per evaluation (2026-10-15: whole cuts, never part of one - BySection; by block: most blocks).</summary>
    public static int MaxBreaks = 2;
    /// <summary>DAMAGE BY SECTION (2026-10-15, the user: "one pattern im not sure i like is that a fracture creeps along the span
    /// instead of uniformly crumpling the weak neck" - DamageBySection). Off (A/B): by block, each from its worst joint - the
    /// most loaded block wore and failed alone, its load moved onto its neighbour, which became the worst: a fracture ZIPPED
    /// along the neck a block or two an evaluation (Plane2's pull ramp: 13 blocks along one row over 3.5 s).</summary>
    public static bool BySection = true;
    /// <summary>Most blocks failed per evaluation in the cuts after the first (the first cut goes whole whatever its size; a
    /// later one that would pass this waits, whole, for the next evaluation).</summary>
    public static int MaxBreakBlocks = 96;
    /// <summary>Thrust loads the structure (at the thrusters, and the ship's acceleration from it). Off:
    /// (2026-10-13, the user: "can we disable thrust overload"): engines never strain or break a ship; the loads balance
    /// as if their thrust were not there (Structure.ThrustLoads).</summary>
    /// <summary>Diagnostics: contact cells that fell in a block's cells, and those that did not.</summary>
    public static int ContactMapped, ContactMissed;
    public static bool ThrustLoads = true;
    /// <summary>The gyros' torque in the loads (off: as if they made none - for A/B).</summary>
    public static bool GyroLoads = true;
    /// <summary>A RAILED RE-ENTRY'S BRAKING loads the structure (2026-10-14, the user: "should a railed re-entry load the
    /// structure with its deceleration (up to ~5 g)?" - yes; Structure.EntryLoads). The Orbital mod brakes the frame through
    /// the band on rails - no physical force, so the ship felt nothing; its braking (Loads.EntryAccel) is put on as the air
    /// would put it: the ship's mass x the deceleration, spread over the windward faces by the same Newtonian weights as the
    /// air's own load (cos^2 x area along the travel - the heat model's pass), with no net moment (the rails hold the
    /// attitude: the couple field takes its moment off). The free-body solve then gives the inertial relief through the
    /// whole ship: the nose and heat shield carry it, the rest is pressed into them. Not a contact (no part of the contact
    /// balance).</summary>
    public static bool EntryLoads = true;
    /// <summary>The gyros' torque applied at the gyro blocks (physical: a torque bolted on there) instead of over the whole
    /// ship by mass (the default: the game's gyros turn the ship from anywhere).</summary>
    public static bool GyroAtBlocks = false;

    /// <summary>The air's couple (its torque less what the face loads give) spread as a rotational FORCE field over the windward
    /// faces (df = w Omega x (p - c), J Omega = the couple: no net force, the moment exact) - not as pure moments on single
    /// blocks by their area, whatever their lever arm (a 1.3 t nose platform got 1.3 MN m; 2026-10-14 review).</summary>
    public static bool CoupleAsForces = true;
    public static Vector3 LastDm, LastMDist;
    public sealed class Loads
    {
        /// <summary>The air's applied force and torque (grid-local; torque about AeroCom), and the air past the ship.</summary>
        public Vector3 AeroF, AeroT, AeroCom, AirVel, GyroT, AngVel;
        public float LiftScale = 1f, CellSize = 0.25f, CellOffset;
        public readonly List<List<Vector3I>> WingCells = new();
        public readonly List<Vector3> WingF = new();
        public readonly List<int> ThrBlock = new(); public readonly List<Vector3> ThrF = new();
        /// <summary>Each thruster's force without the flight controller's steering (the game's own sharing): ThrF less the steering.</summary>
        public readonly List<Vector3> ThrF0 = new();
        public readonly List<int> GyroBlock = new();
        /// <summary>Contact forces (collisions, the ground): grid-local point and force (N) on this grid.</summary>
        public readonly List<Vector3> ContactP = new(), ContactF = new();
        /// <summary>A railed re-entry's braking acceleration (grid-local, m/s2; zero: none) - EntryLoads.</summary>
        public Vector3 EntryAccel;
        public void Clear() { WingCells.Clear(); WingF.Clear(); ThrBlock.Clear(); ThrF.Clear(); ThrF0.Clear(); GyroBlock.Clear(); ContactP.Clear(); ContactF.Clear(); AeroF = AeroT = GyroT = AngVel = EntryAccel = Vector3.Zero; }
    }

    /// <summary>A hull's windward set for one Pass (2026-10-15): the sum of every face's weight (Sum), the loaded faces (weight
    /// over 0 on a block - Faces[0..Count), in face order), their weights' sum (Sum2), weighted centre (Centre) and the
    /// couple's J about it - each summed exactly as Apply summed it, so the loads are bit-identical. Made again when the
    /// hull's PassVersion moves (FaceWind is written in Pass only).</summary>
    internal sealed class WindSet
    {
        public int Version = -1, Count;
        public int[] Faces = Array.Empty<int>();
        public float Sum, Sum2;
        public Vector3 Centre;
        public double Jxx, Jyy, Jzz, Jxy, Jxz, Jyz;
    }

    /// <summary>The hull's windward set for its last Pass (cached on it; one evaluation at a time uses a hull).</summary>
    internal static WindSet WindOf(HullHeat hh)
    {
        var ws = hh.Wind ??= new WindSet();
        if (ws.Version == hh.PassVersion && ws.Faces.Length >= ws.Count) return ws;
        int nAll = hh.FaceCount;
        if (ws.Faces.Length < nAll) ws.Faces = new int[nAll];
        float sw = 0f; for (int i = 0; i < nAll; i++) sw += hh.FaceWind[i];
        int nf = 0; Vector3 cw = Vector3.Zero; float sw2 = 0f;
        for (int i = 0; i < nAll; i++)
        {
            float w = hh.FaceWind[i];
            if (w <= 0f || hh.FaceBlock[i] < 0) continue;
            ws.Faces[nf++] = i; cw += hh.FacePosition(i) * w; sw2 += w;
        }
        cw /= sw2;
        double jxx = 0, jyy = 0, jzz = 0, jxy = 0, jxz = 0, jyz = 0;
        for (int k = 0; k < nf; k++)
        {
            int i = ws.Faces[k]; float w = hh.FaceWind[i];
            var r = hh.FacePosition(i) - cw;
            jxx += w * (r.Y * r.Y + r.Z * r.Z); jyy += w * (r.X * r.X + r.Z * r.Z); jzz += w * (r.X * r.X + r.Y * r.Y);
            jxy -= w * r.X * r.Y; jxz -= w * r.X * r.Z; jyz -= w * r.Y * r.Z;
        }
        ws.Count = nf; ws.Sum = sw; ws.Sum2 = sw2; ws.Centre = cw;
        ws.Jxx = jxx; ws.Jyy = jyy; ws.Jzz = jzz; ws.Jxy = jxy; ws.Jxz = jxz; ws.Jyz = jyz;
        ws.Version = hh.PassVersion;
        return ws;
    }

    /// <summary>A wing's cells' blocks in one structure (st.BlockAt of each cell's centre), kept with the cell list they were
    /// found for.</summary>
    internal sealed class WingBlocks
    {
        public List<Vector3I> Cells; public int Count; public Vector3I First, Last; public float Size, Offset;
        public int[] Blocks = Array.Empty<int>();
    }

    /// <summary>The block of each of a wing's cells (-1: none) - st.BlockAt, found once per structure and wing cell list
    /// (2026-10-15: every Apply looked every wing cell up again - a hash and a bucket scan each, two or three Applies an
    /// evaluation - for lists that change only when the wings are found again). Cached on the structure (one evaluation at a
    /// time uses a structure), by the wing's place in the loads; found again whenever the list there is another one (another
    /// object, count, first or last cell) or the cell geometry moved. The same BlockAt, the same answers.</summary>
    static int[] WingBlocksOf(ShipStructure st, int w, List<Vector3I> cells, float size, float offset)
    {
        var all = st.WingBlockCache;
        while (all.Count <= w) all.Add(new WingBlocks());
        var e = all[w];
        int n = cells.Count;
        if (ReferenceEquals(e.Cells, cells) && e.Count == n && e.Size == size && e.Offset == offset && e.First == cells[0] && e.Last == cells[n - 1])
            return e.Blocks;
        if (e.Blocks.Length < n) e.Blocks = new int[n];
        for (int j = 0; j < n; j++)
        {
            var c = cells[j];
            e.Blocks[j] = st.BlockAt(new Vector3((c.X + offset) * size, (c.Y + offset) * size, (c.Z + offset) * size));
        }
        e.Cells = cells; e.Count = n; e.Size = size; e.Offset = offset; e.First = cells[0]; e.Last = cells[n - 1];
        return e.Blocks;
    }

    /// <summary>The blocks' external loads (st.ExtForce / ExtMoment) for these loads. The heat model's faces give the
    /// windward weights (its Pass must be along the air's travel).</summary>
    public static void Apply(ShipStructure st, HullHeat hh, Loads L)
    {
        int n = st.Count;
        Array.Clear(st.ExtForce, 0, n); Array.Clear(st.ExtMoment, 0, n);
        var com = st.Com;
        Vector3 wingF = Vector3.Zero, wingM = Vector3.Zero;
        for (int w = 0; w < L.WingCells.Count; w++)
        {
            var cells = L.WingCells[w]; if (cells == null || cells.Count == 0) continue;
            var f = L.WingF[w] * (L.LiftScale / cells.Count);
            int[] blk = WingBlocksOf(st, w, cells, L.CellSize, L.CellOffset);
            for (int j = 0; j < cells.Count; j++)
            {
                var c = cells[j];
                var p = new Vector3((c.X + L.CellOffset) * L.CellSize, (c.Y + L.CellOffset) * L.CellSize, (c.Z + L.CellOffset) * L.CellSize);
                int b = blk[j];
                if (b < 0) continue;
                st.ExtForce[b] += f; st.ExtMoment[b] += Vector3.Cross(p - st.Center[b], f);
                wingF += f; wingM += Vector3.Cross(p - com, f);
            }
        }
        var bodyF = L.AeroF - wingF;
        var bodyM = L.AeroT + Vector3.Cross(L.AeroCom - com, L.AeroF) - wingM;
        // (a railed re-entry's braking: the structure's own mass x the deceleration - its free-body solve then decelerates at
        //  exactly that - added to the body force with NO moment: the faces' spread puts one on, the couple below takes it off)
        var entryF = EntryLoads && float.IsFinite(L.EntryAccel.X + L.EntryAccel.Y + L.EntryAccel.Z) ? L.EntryAccel * st.TotalMass : Vector3.Zero;
        bodyF += entryF;
        // (the windward set - the faces' weights' sum, the loaded faces, their centre and the couple's J - is the last Pass's
        //  alone: cached on the hull's heat until the next Pass, WindSet; the loops below run over the loaded faces only.
        //  Six passes over every face per Apply - 383k faces on newred_v2, 5.3 ms, two or three Applies an evaluation - and
        //  the same sums in the same order: bit-identical, 2026-10-15)
        var ws = hh != null ? WindOf(hh) : null;
        float sw = ws != null ? ws.Sum : 0f;
        if (sw > 0f && (L.AirVel.LengthSquared() > 1f || entryF.LengthSquared() > 0f))
        {
            Vector3 mDist = Vector3.Zero;
            var faces = ws.Faces; int nf = ws.Count;
            for (int k = 0; k < nf; k++)
            {
                int i = faces[k];
                float w = hh.FaceWind[i]; int b = hh.FaceBlock[i];
                var f = bodyF * (w / sw); var p = hh.FacePosition(i);
                st.ExtForce[b] += f; st.ExtMoment[b] += Vector3.Cross(p - st.Center[b], f);
                mDist += Vector3.Cross(p - com, f);
            }
            var dm = bodyM - mDist;
            LastDm = dm; LastMDist = mDist;
            if (CoupleAsForces)
            {
                // (scratch) the couple as a rotational force field over the same weights: df_i = w_i Omega x (p_i - c), J Omega = dm
                // (the centre cw and J: WindSet's, the pass's own)
                var cw = ws.Centre;
                var om = ShipStructure.Solve3(ws.Jxx, ws.Jxy, ws.Jxz, ws.Jyy, ws.Jyz, ws.Jzz, dm);
                for (int k = 0; k < nf; k++)
                {
                    int i = faces[k];
                    float w = hh.FaceWind[i]; int b = hh.FaceBlock[i];
                    var p = hh.FacePosition(i); var f = Vector3.Cross(om, p - cw) * w;
                    st.ExtForce[b] += f; st.ExtMoment[b] += Vector3.Cross(p - st.Center[b], f);
                }
            }
            else
            for (int k = 0; k < nf; k++) { int i = faces[k]; float w = hh.FaceWind[i]; int b = hh.FaceBlock[i]; st.ExtMoment[b] += dm * (w / sw); }
        }
        else if (st.Root >= 0) { st.ExtForce[st.Root] += bodyF; st.ExtMoment[st.Root] += bodyM + Vector3.Cross(com - st.Center[st.Root], bodyF); }
        if (ThrustLoads) for (int k = 0; k < L.ThrBlock.Count; k++) { int b = L.ThrBlock[k]; if (b >= 0 && b < n) st.ExtForce[b] += L.ThrF[k]; }
        // contacts (collisions, the ground): at the block in the contact's cell (else none - a point in no block's cells)
        for (int k = 0; k < L.ContactP.Count; k++)
        {
            int b = st.BlockAt(L.ContactP[k]);
            if (b < 0 || b >= n) { System.Threading.Interlocked.Increment(ref ContactMissed); continue; }
            System.Threading.Interlocked.Increment(ref ContactMapped);
            st.ExtForce[b] += L.ContactF[k]; st.ExtMoment[b] += Vector3.Cross(L.ContactP[k] - st.Center[b], L.ContactF[k]);
        }
        if (GyroLoads && L.GyroT.LengthSquared() > 0f)
        {
            if (!GyroAtBlocks)
            {
                // THE GYROS ACT ON THE WHOLE SHIP (2026-10-14, the user's choice): their torque spread over every block as the
                // field that turns the ship rigidly - df_i = m_i alpha x (c_i - com), J alpha = the gyros' torque (J: the blocks'
                // inertia about the centre of mass): no net force, the torque exact. A pure turn then meets its own inertia
                // block for block - no stress from where the gyros sit (in this game one gyro turns any ship from anywhere);
                // what loads the structure is how hard the ship is whipped (the spin's pull, the air), not its gyros' places.
                // (At the gyro blocks, as torque bolted on there - GyroAtBlocks - one 300 MN m gyro tore a 2.8 kt ship apart
                // in an ordinary turn.)
                SpreadOverShip(st, L.GyroT);
            }
            else
            {
                int ng = 0; foreach (int b in L.GyroBlock) if (b >= 0 && b < n) ng++;
                if (ng > 0) { var g = L.GyroT / ng; foreach (int b in L.GyroBlock) if (b >= 0 && b < n) st.ExtMoment[b] += g; }
                else if (st.Root >= 0) st.ExtMoment[st.Root] += L.GyroT;
            }
        }
    }

    /// <summary>A torque spread over every block as the field that turns the ship rigidly: df_i = m_i alpha x (c_i - com),
    /// J alpha = torque (J: the blocks' inertia about the centre of mass) - no net force, the torque exact.</summary>
    public static void SpreadOverShip(ShipStructure st, Vector3 torque)
    {
        int n = st.Count; var c = st.Com;
        double jxx = 0, jyy = 0, jzz = 0, jxy = 0, jxz = 0, jyz = 0;
        for (int b = 0; b < n; b++)
        {
            float m = st.Mass[b]; if (m <= 0f) continue;
            var r = st.Center[b] - c;
            var g = st.Gyr[b];   // (each block its cells, not a point: its own inertia too - a straight boom has none about its axis as points)
            jxx += m * (r.Y * r.Y + r.Z * r.Z + g.Y + g.Z); jyy += m * (r.X * r.X + r.Z * r.Z + g.X + g.Z); jzz += m * (r.X * r.X + r.Y * r.Y + g.X + g.Y);
            jxy -= m * r.X * r.Y; jxz -= m * r.X * r.Z; jyz -= m * r.Y * r.Z;
        }
        var alpha = ShipStructure.Solve3(jxx, jxy, jxz, jyy, jyz, jzz, torque);
        for (int b = 0; b < n; b++)
        {
            float m = st.Mass[b]; if (m <= 0f) continue;
            st.ExtForce[b] += Vector3.Cross(alpha, st.Center[b] - c) * m;
            var g = st.Gyr[b];
            st.ExtMoment[b] += new Vector3(alpha.X * (g.Y + g.Z), alpha.Y * (g.X + g.Z), alpha.Z * (g.X + g.Y)) * m;
        }
    }

    /// <summary>The damage this evaluation (dt seconds of it): (block, fraction of full health, destroy).</summary>
    public static void Damage(ShipStructure st, HullHeat hh, float dt, bool structure, bool heat, float breakUtil, float wearPerSecond, int maxBreaks, List<(int block, float fraction, bool destroy)> outDamage)
        => Damage(st, hh, dt, structure, heat, breakUtil, wearPerSecond, maxBreaks, outDamage, null, null);
    public static void Damage(ShipStructure st, HullHeat hh, float dt, bool structure, bool heat, float breakUtil, float wearPerSecond, int maxBreaks, List<(int block, float fraction, bool destroy)> outDamage, List<(Vector3 at, Vector3 dir, float util)> outWhere)
        => Damage(st, hh, dt, structure, heat, breakUtil, wearPerSecond, maxBreaks, outDamage, outWhere, null);

    /// <summary>A failed cut (2026-10-15): its centre on its plane (grid-local m), the way out of it into what it takes off,
    /// its worst member's utilisation, its plane (axis, coordinate), and its members' entries in the damage list
    /// [First, First + Count) - each a destroy, all in this evaluation.</summary>
    public sealed class CutFail { public Vector3 At, Dir; public float Util, Plane; public int Axis, First, Count; }
    /// <summary>Diagnostics: cuts failed, their members in all, the most in one.</summary>
    public static int CutsFailed, CutMembersFailed, CutMostMembers;

    /// <summary>As Damage, and for each entry where it fails (structure coordinates = the grid's local metres) and which
    /// way: a joint's point and the way into the block that goes (its fracture's line, its dent's direction); a block's own
    /// middle and its load's direction; for heat, its middle and none.
    /// PROGRESSIVE (2026-10-14): every block whose worst joint is over the endurance limit (StructureFatigue.EnduranceUtil;
    /// over 1 with fatigue off) loses StructureFatigue.HealthRate x dt of its health (the entry's fraction); it FAILS (destroy,
    /// fraction: the health it had) when that exhausts it (StructureFatigue.BreakBelow) or at StructureFatigue.SnapUtil - the
    /// worst maxBreaks of those an evaluation, the rest held just short (next time). breakUtil, wearPerSecond: the yield
    /// wear's (wearPerSecond a second at breakUtil). The health it starts from: st.Integrity (the caller's - the ledger's,
    /// the game's). A burning block's health runs out the same way: it goes (no direction - the whole block).
    /// BY SECTION (BySection, on the slices - the default; 2026-10-15): the same per CUT, DamageBySection; outCuts (optional):
    /// each failed cut, its members' entries together.</summary>
    public static void Damage(ShipStructure st, HullHeat hh, float dt, bool structure, bool heat, float breakUtil, float wearPerSecond, int maxBreaks, List<(int block, float fraction, bool destroy)> outDamage, List<(Vector3 at, Vector3 dir, float util)> outWhere, List<CutFail> outCuts)
    {
        outDamage.Clear(); outWhere?.Clear(); outCuts?.Clear();
        int n = st.Count;
        float from = StructureFatigue.Enabled ? MathF.Min(StructureFatigue.EnduranceUtil, 1f) : 1f;
        float snap = StructureFatigue.SnapUtil, floor = StructureFatigue.BreakBelow;
        // (what each block lost to the structure this evaluation: its heat comes off what is left)
        Dictionary<int, float> lost = null;
        if (structure && BySection && st.SectionCount > 0) lost = DamageBySection(st, dt, breakUtil, wearPerSecond, maxBreaks, outDamage, outWhere, outCuts);
        else if (structure)
        {
            // (cap: the joint's other block's strength over this one's now - the joint is the weaker of the two, so this block's
            //  health lost raises the joint's utilisation only once it is the weaker; a block's own overload: none)
            var over = new Dictionary<int, (float u, Vector3 at, Vector3 dir, float cap)>();
            for (int k = 0; k < st.ContactCount; k++)
            {
                float u = st.ContactUtil[k]; if (!(u > from)) continue;
                int b = st.Outboard(k);
                if (over.TryGetValue(b, out var o) && u <= o.u) continue;
                var at = st.ContactPoint(k); var into = st.Center[b] - at;
                st.ContactBlocks(k, out int ca, out int cb);
                float sb = st.BlockStrength(b);
                over[b] = (u, at, into.LengthSquared() > 1e-8f ? Vector3.Normalize(into) : Vector3.UnitY, sb > 0f ? st.BlockStrength(ca == b ? cb : ca) / sb : float.MaxValue);
            }
            if (st.BlockUtil != null)
                for (int b = 0; b < n; b++)
                {
                    float u = st.BlockUtil[b]; if (!(u > from) || (over.TryGetValue(b, out var o) && u <= o.u)) continue;
                    var f = st.ExtForce[b];
                    over[b] = (u, st.Center[b], f.LengthSquared() > 1e-8f ? Vector3.Normalize(f) : Vector3.UnitY, float.MaxValue);
                }
            var list = new List<KeyValuePair<int, (float u, Vector3 at, Vector3 dir, float cap)>>(over);
            list.Sort((a, b) => b.Value.u.CompareTo(a.Value.u));
            int breaks = 0;
            foreach (var kv in list)
            {
                int b = kv.Key; float u = kv.Value.u;
                float have = b < st.Integrity.Length ? st.Integrity[b] : 1f;
                float loss = StructureFatigue.Loss(st.Mat[b], u, have, kv.Value.cap, dt, breakUtil, wearPerSecond, out float uIntact);
                // (the snap on the load against the joint's INTACT strength - a genuine overload; a worn joint's own utilisation
                //  runs high as its health goes, and wears through instead)
                bool fails = uIntact >= snap || have - loss <= floor;
                if (fails && breaks < maxBreaks) { outDamage.Add((b, MathF.Max(have, 0f), true)); breaks++; (lost ??= new())[b] = have; }
                else
                {
                    if (fails) loss = MathF.Max(have - floor - 1e-3f, 0f);   // (past this evaluation's breaks: held just short - next time)
                    if (!(loss > 0f)) continue;
                    outDamage.Add((b, loss, false)); (lost ??= new())[b] = loss;
                }
                outWhere?.Add((kv.Value.at, kv.Value.dir, u));
            }
        }
        if (heat && hh != null)
            for (int b = 0; b < n && b < hh.BlockCount; b++)
            {
                float r = StructMaterial.BurnRate(st.Mat[b], hh.BlockSkinMaxK[b]);
                if (!(r > 0f)) continue;
                float gone = 0f; if (lost != null && lost.TryGetValue(b, out var g)) gone = g;
                float have = (b < st.Integrity.Length ? st.Integrity[b] : 1f) - gone;
                if (have <= floor) continue;   // (already going this evaluation)
                float loss = r * dt;
                outDamage.Add(have - loss <= floor ? (b, have, true) : (b, loss, false));   // (burnt through: the whole block)
                outWhere?.Add((st.Center[b], Vector3.Zero, 0f));
            }
    }

    /// <summary>
    /// DAMAGE BY SECTION (2026-10-15, the user: "one pattern im not sure i like is that a fracture creeps along the span instead
    /// of uniformly crumpling the weak neck"). By block, each block wore from its own worst joint: the most loaded one wore
    /// and failed alone, the solve moved its load onto its neighbour, which became the worst and went next - a fracture
    /// ZIPPED along a neck a block or two an evaluation. A real neck yields across its whole section; so, per CUT (the method
    /// of sections' cut through one part of the ship - ShipStructure.SectionCount):
    ///  - WEAR: a cut over the endurance limit (its worst member's utilisation) tires EVERY member over it, each at its own
    ///    utilisation from the section solve - its share of the cut's load (StructureFatigue.Loss: rate-independent, the
    ///    loop closed inside the step, a patch the weaker of its two blocks) - so the whole neck tires together; as each
    ///    member's health goes its strength goes (BlockStrength), so the cut's capacity falls across the neck. A block in
    ///    several cuts takes the worst of them;
    ///  - FAILURE: when the cut's capacity is exhausted - a member's health would fall to BreakBelow (the first fibre to let
    ///    go: the rest cannot carry what it did - the section solve moves no load between members) - or the cut SNAPS (a
    ///    member at StructureFatigue.SnapUtil of its intact strength), ALL its members fail in this evaluation: each one's
    ///    departing block (a patch's on the lighter side, a straddling block itself) a destroy entry, together (outCuts),
    ///    and its TIES - the trim crossing its plane (no load, so no member, but it held the piece on: the Tu-134's left wing
    ///    went over three cuts before) - so the piece beyond the cut leaves in one go;
    ///  - BUDGET: the worst maxBreaks failing cuts an evaluation, after the first no more than MaxBreakBlocks blocks; a cut
    ///    past it is DEFERRED WHOLE - its members held just short (next time), never part of it failed now;
    ///  - NO ZIPPER: an evaluation's failures are decided on its own solve (no load moved onto neighbours inside it; a failing
    ///    cut wholly inside what an earlier one takes off is not failed too - FarSide - nor worn: it leaves whole), and
    ///    the game holds the structure's damage after a failure until the structure is rebuilt without what left
    ///    (Structure/Server/StructureServer _stBreakHold): what is left is damaged again only if a cut is genuinely overloaded
    ///    on that new solve.
    /// Returns what each block lost (the heat's pass takes it off first).
    /// </summary>
    static Dictionary<int, float> DamageBySection(ShipStructure st, float dt, float breakUtil, float wearPerSecond, int maxBreaks,
        List<(int block, float fraction, bool destroy)> outDamage, List<(Vector3 at, Vector3 dir, float util)> outWhere, List<CutFail> outCuts)
    {
        float from = StructureFatigue.Enabled ? MathF.Min(StructureFatigue.EnduranceUtil, 1f) : 1f;
        float snap = StructureFatigue.SnapUtil, floor = StructureFatigue.BreakBelow;
        int ng = st.SectionCount;
        // each loaded cut: its members' losses at their own utilisation, and whether it fails
        var cuts = new List<(int g, float u, bool fails)>();
        var mLoss = new Dictionary<int, float>();   // (piece -> its block's loss in that cut)
        for (int g = 0; g < ng; g++)
        {
            float ug = st.SectionUtil(g); if (!(ug > from)) continue;
            int m0 = st.SectionPieceStart(g), m1 = m0 + st.SectionPieceCount(g);
            bool fails = false;
            for (int mi = m0; mi < m1; mi++)
            {
                float u = st.PieceUtil(mi); if (!(u > from)) continue;
                int b = st.PieceBlock(mi);
                float have = b < st.Integrity.Length ? st.Integrity[b] : 1f;
                // (cap: a patch's other block's strength over this one's now - the patch is the weaker of the two, so this
                //  block's health lost raises its utilisation only once it is the weaker; a straddling block's own: none)
                float cap = float.MaxValue; int k = st.PieceContact(mi);
                if (k >= 0) { st.ContactBlocks(k, out int ca, out int cb); float sb = st.BlockStrength(b); cap = sb > 0f ? st.BlockStrength(ca == b ? cb : ca) / sb : float.MaxValue; }
                float loss = StructureFatigue.Loss(st.Mat[b], u, have, cap, dt, breakUtil, wearPerSecond, out float uIntact);
                // (the snap on the load against the INTACT strength - a genuine overload; a worn member's own utilisation runs
                //  high as its health goes, and wears through instead)
                if (uIntact >= snap || (loss > 0f && have - loss <= floor)) fails = true;
                mLoss[mi] = loss;
            }
            cuts.Add((g, ug, fails));
        }
        if (cuts.Count == 0) return null;
        cuts.Sort((a, b) => b.u.CompareTo(a.u));
        // the failing cuts, the worst first: each whole, or deferred whole
        var failed = new HashSet<int>(); var held = new HashSet<int>();
        int nCut = 0, nBlocks = 0, nFailing = 0;
        foreach (var c in cuts) if (c.fails) nFailing++;
        Dictionary<int, float> lost = null;
        HashSet<int> leaving = null; int[] adjStart = null, adjOther = null;
        foreach (var (g, ug, fails) in cuts)
        {
            if (!fails) continue;
            int m0 = st.SectionPieceStart(g), m1 = m0 + st.SectionPieceCount(g);
            // (NESTED in a cut failed before it this evaluation - inside the piece that one takes off, or taking that one off
            //  with its own: not failed too - the wing broken once, at its worst cut, not at every cut over the line at once
            //  (the Tu-134's wing at two neighbouring cuts at 2 Hz); its members keep their wear, held just short, and fail on
            //  the next solve only if that one still overloads them)
            HashSet<int> mine = null;
            if (nCut > 0)
            {
                if (adjStart == null) StructureBend.Adjacency(st, out adjStart, out adjOther);
                leaving ??= new HashSet<int>();
                bool nested = true;
                for (int mi = m0; mi < m1 && nested; mi++) if (!leaving.Contains(st.PieceBlock(mi))) nested = false;
                if (!nested)
                {
                    mine = new HashSet<int>(); var seeds = new List<int>();
                    for (int mi = m0; mi < m1; mi++) seeds.Add(st.PieceBlock(mi));
                    st.SectionPlane(g, out int gax, out float gpl);
                    var go = st.SectionOut(g);
                    FarSide(st, gax, gpl, (gax == 0 ? go.X : gax == 1 ? go.Y : go.Z) > 0f ? 1 : -1, seeds, adjStart, adjOther, mine);
                    foreach (int b in failed) if (mine.Contains(b)) { nested = true; break; }
                }
                if (nested) { for (int mi = m0; mi < m1; mi++) held.Add(st.PieceBlock(mi)); continue; }
            }
            int add = 0;
            for (int mi = m0; mi < m1; mi++) if (!failed.Contains(st.PieceBlock(mi))) add++;
            int nt = st.SectionTieCount(g);
            if (add == 0) continue;   // (every member already going with a cut before it)
            bool fits = nCut < maxBreaks && (nCut == 0 || nBlocks + add + nt <= MaxBreakBlocks);
            if (!fits) { for (int mi = m0; mi < m1; mi++) held.Add(st.PieceBlock(mi)); continue; }
            nCut++; nBlocks += add;
            var cf = new CutFail { At = st.SectionCentre(g), Dir = st.SectionOut(g), Util = ug, First = outDamage.Count };
            st.SectionPlane(g, out cf.Axis, out cf.Plane);
            for (int mi = m0; mi < m1; mi++)
            {
                int b = st.PieceBlock(mi);
                if (!failed.Add(b)) continue;   // (in an earlier failed cut too: once)
                float have = b < st.Integrity.Length ? st.Integrity[b] : 1f;
                outDamage.Add((b, MathF.Max(have, 0f), true)); (lost ??= new())[b] = have;
                // (each member split at its own place on the cut, out of it into what leaves)
                outWhere?.Add((st.PiecePoint(mi), cf.Dir, st.PieceUtil(mi)));
            }
            // (its TIES too - the trim across its plane: no load, but it would hold what leaves; ShipStructure.SectionTieCount)
            for (int t = 0; t < nt; t++)
            {
                int b = st.SectionTieBlock(g, t);
                if (!failed.Add(b)) continue;
                float have = b < st.Integrity.Length ? st.Integrity[b] : 1f;
                outDamage.Add((b, MathF.Max(have, 0f), true)); (lost ??= new())[b] = have;
                outWhere?.Add((st.SectionTiePoint(g, t), cf.Dir, 0f));
            }
            cf.Count = outDamage.Count - cf.First;
            outCuts?.Add(cf);
            // (what leaves with it - only while more cuts are failing: its departing blocks and everything joined to them on
            //  its far side, at most LeavingMax blocks)
            if (nCut < nFailing)
            {
                if (adjStart == null) StructureBend.Adjacency(st, out adjStart, out adjOther);
                leaving ??= new HashSet<int>();
                if (mine != null) leaving.UnionWith(mine);
                else
                {
                    var seeds = new List<int>(); for (int i = cf.First; i < cf.First + cf.Count; i++) seeds.Add(outDamage[i].block);
                    FarSide(st, cf.Axis, cf.Plane, (cf.Axis == 0 ? cf.Dir.X : cf.Axis == 1 ? cf.Dir.Y : cf.Dir.Z) > 0f ? 1 : -1, seeds, adjStart, adjOther, leaving);
                }
            }
            CutsFailed++; CutMembersFailed += cf.Count; if (cf.Count > CutMostMembers) CutMostMembers = cf.Count;
        }
        // the wear: each block not failing, the worst of its cuts' losses (a deferred cut's members held just short; what
        // leaves with a failed cut wears no more)
        var best = new Dictionary<int, (float loss, int mi, int g)>();
        foreach (var (g, ug, fails) in cuts)
        {
            int m0 = st.SectionPieceStart(g), m1 = m0 + st.SectionPieceCount(g);
            for (int mi = m0; mi < m1; mi++)
            {
                if (!mLoss.TryGetValue(mi, out float loss) || !(loss > 0f)) continue;
                int b = st.PieceBlock(mi);
                if (failed.Contains(b) || (leaving != null && leaving.Contains(b))) continue;
                if (!best.TryGetValue(b, out var o) || loss > o.loss) best[b] = (loss, mi, g);
            }
        }
        foreach (var kv in best)
        {
            int b = kv.Key; var (loss, mi, g) = kv.Value;
            float have = b < st.Integrity.Length ? st.Integrity[b] : 1f;
            if (held.Contains(b) || have - loss <= floor) loss = MathF.Min(loss, MathF.Max(have - floor - 1e-3f, 0f));   // (deferred: next time, whole)
            if (!(loss > 0f)) continue;
            outDamage.Add((b, loss, false)); (lost ??= new())[b] = loss;
            outWhere?.Add((st.PiecePoint(mi), st.SectionOut(g), st.PieceUtil(mi)));
        }
        return lost;
    }

    /// <summary>Most blocks a failed cut's far side is walked over (DamageBySection: what leaves with it).</summary>
    public static int LeavingMax = 20000;
    /// <summary>A cut's far side into "into": its blocks (seeds: its members' departing blocks) and every block joined to them
    /// through contacts with its centre beyond the plane on side side (the way out), at most LeavingMax.</summary>
    static void FarSide(ShipStructure st, int ax, float plane, int side, List<int> seeds, int[] adjStart, int[] adjOther, HashSet<int> into)
    {
        var q = new Queue<int>(); int seen = 0;
        foreach (int b in seeds) if (into.Add(b)) q.Enqueue(b);
        while (q.Count > 0 && seen++ < LeavingMax)
        {
            int i = q.Dequeue();
            for (int e = adjStart[i]; e < adjStart[i + 1]; e++)
            {
                int j = adjOther[e];
                var c = st.Center[j]; float x = ax == 0 ? c.X : ax == 1 ? c.Y : c.Z;
                if ((x - plane) * side <= 0f || !into.Add(j)) continue;
                q.Enqueue(j);
            }
        }
    }
}
