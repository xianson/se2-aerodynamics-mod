#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// PERMANENT BENDS (2026-10-14, the user: "an overloaded wing stays bent: tip a bit droopy or twisted after a hard pull - you
/// see you pushed it too far without breaking it") - the game-free part (the game side: Structure/Server/StructureServer.cs
/// ApplyBends; offline-tested in Tests/StructureTests 'Bends').
///
/// A joint past its yield (utilisation over 1, not broken) takes a PERMANENT SET: the structure outboard of it - the slice's
/// far side, the lighter one (ShipStructure.Outboard: away from the ship's main mass), the blocks connected to the failing
/// block without crossing its plane - turns a little about the joint, as a plastic hinge does. The turn is about the far
/// side's own load's moment at the joint (its net loads after the solve: lift less its inertia, a pod's weight in a pull),
/// so it bends the way the load pushed - up after a hard pull on a light wing, down for a heavy pod, twisted for a torsion
/// overload - and every point on the far side moves Rot x (p - joint): zero at the joint, growing outward (a droop, not a
/// crumpled block). The game moves its deformation BONES that way (a 1 m lattice the armour mesh is skinned to).
///  - the set follows the PEAK: a joint keeps the highest utilisation it was bent at (by its place, across rebuilds) and
///    bends again only past it, by PerUtil x the new overshoot - a harder pull bends it further, the same pull twice does
///    not; a worn joint (its health lost) reaches a higher utilisation under the same load, so wear adds up too;
///  - ONE HINGE PER BRANCH: only the worst yielded joint of a branch bends (the joints inside its far side, or holding it,
///    are nested: their peaks taken) - so a wing of 0.5 m blocks sets as much as one of 2.5 m blocks;
///  - capped: at most MaxJoints bends an evaluation, a far side of at most MaxBlocks blocks (a bigger one is the ship's
///    body, not a tip: no bend), and the game side caps every bone's total at Max (m);
///  - BY CUT (2026-10-15, with StructureEval.DamageBySection - the whole neck wears together): on the slices the hinge is the
///    worn CUT - at its centre on its plane, at its worst member's utilisation, its far side from that member's departing
///    block - one per cut, keyed by the cut's centre (its peak does not wander from member to member as they wear); a cut
///    whose worst member is a straddling block bends too (its plane is the cut's). Not the slices: each worn block's worst
///    contact, as before (straddling blocks there have no plane: not bent).
/// FOLD TOWARD THE PUSHED SIDE (2026-10-15, HingeToCompression): a cut's bend turns about its most pushed member's pushed
/// edge, not its centre - the pulled side opens, nothing is pushed into the ship.
/// Defaults: PerUtil 0.05 rad - at BreakAt 1.5 (just short of breaking) a 10 m wing's tip sets 25 cm, at 1.2 ~10 cm; Max 0.3 m.
/// </summary>
public static class StructureBend
{
    public static bool Enabled = true;
    /// <summary>The permanent turn (rad) at a joint per unit of utilisation over its last peak (1 at first: the yield).</summary>
    public static float PerUtil = 0.05f;
    /// <summary>The most one bend turns its far side (rad; 0.07 = 4 deg) - a set, not a fold.</summary>
    public static float MaxStepRad = 0.07f;
    /// <summary>Most any point (a bone) is moved by bends in total (m).</summary>
    public static float Max = 0.3f;
    /// <summary>Most blocks a bend moves; a bigger far side is the ship's body, not a tip - not bent.</summary>
    public static int MaxBlocks = 300;
    /// <summary>Most joints bent per evaluation (the worst first).</summary>
    public static int MaxJoints = 2;
    /// <summary>A joint bends again only this far past its last peak (no dribble of tiny bends every evaluation).</summary>
    public static float MinStep = 0.02f;
    /// <summary>Over this distance (m) from the joint's plane a point takes up its full share (bones on the plane stay put).</summary>
    public static float Ramp = 1f;
    /// <summary>FOLD TOWARD THE PUSHED SIDE (2026-10-15, the user: "add compression vs tension to the break work"): a cut's bend
    /// turns about its most compressed member (ShipStructure.PieceCompression) instead of its centre - a plastic hinge forms
    /// on the crushed face, the pulled face opens and the far side swings away from it (the torn side) - by this share of the
    /// way from the centre (0: about the centre, as before; 1: about the most pushed member's pushed edge - the extreme compression fibre, ShipStructure.PiecePushedEdge).
    /// On the slices only.</summary>
    public static float HingeToCompression = 1f;

    // diagnostics (harness aeroget)
    public static int Planned, TooBig, NoMoment, Nested;

    /// <summary>One bend: the joint (grid-local m), its plane (axis, coordinate, the far side's sign), the turn (rad, about
    /// the joint: its direction the axis), the far side's blocks.</summary>
    public sealed class Bend
    {
        public Vector3 Joint, Rot; public int Axis, Side; public float Plane, Util;
        public readonly List<int> Blocks = new();
    }

    /// <summary>A joint's key across rebuilds: its patch's centre (1/8 m) and axis.</summary>
    public static long Key(Vector3 p, int axis) =>
        ((long)MathF.Round(p.X * 8) & 0xFFFFF) << 40 | ((long)MathF.Round(p.Y * 8) & 0xFFFFF) << 20 | ((long)MathF.Round(p.Z * 8) & 0xFFFFF) | (long)axis << 61;

    /// <summary>The bends this evaluation's wear calls for (after StructureEval.Damage and a solve on the same loads):
    /// for each worn joint (not destroyed, over 1) whose utilisation passed its last peak (peaks: by Key, the caller's,
    /// kept across evaluations), the far side and its turn. Returns how many.</summary>
    public static int Plan(ShipStructure st, List<(int block, float fraction, bool destroy)> damage, List<(Vector3 at, Vector3 dir, float util)> where,
        Dictionary<long, float> peaks, List<Bend> outBends)
    {
        outBends.Clear();
        if (!Enabled || st == null || damage == null || where == null || PerUtil <= 0f || Max <= 0f) return 0;
        // the worn joints over 1 (the damage list: the structure's entries first, worst first)
        HashSet<int> worn = null;
        for (int i = 0; i < damage.Count && i < where.Count; i++)
        {
            var d = damage[i]; if (d.destroy || !(where[i].util > 1f) || where[i].dir.LengthSquared() < 0.5f) continue;
            (worn ??= new HashSet<int>()).Add(d.block);
        }
        if (worn == null) return 0;
        // the hinges: (point - the key's, axis, plane, utilisation, departing block, the turn's pivot)
        var order = new List<(Vector3 J, int ax, float plane, float u, int ob, Vector3 H)>();
        if (st.SectionCount > 0)
        {
            // BY CUT (2026-10-15): each cut over 1 with a worn member - at its centre, its worst member's utilisation and block
            for (int g = 0; g < st.SectionCount; g++)
            {
                float ug = st.SectionUtil(g); if (!(ug > 1f)) continue;
                int m0 = st.SectionPieceStart(g), m1 = m0 + st.SectionPieceCount(g), wm = -1, cm = -1; bool any = false;
                for (int mi = m0; mi < m1; mi++)
                {
                    if (worn.Contains(st.PieceBlock(mi))) any = true;
                    if (wm < 0 || st.PieceUtil(mi) > st.PieceUtil(wm)) wm = mi;
                    if (st.PieceCompression(mi) > 0f && (cm < 0 || st.PieceCompression(mi) > st.PieceCompression(cm))) cm = mi;   // (the most pushed)
                }
                if (!any || wm < 0) continue;
                st.SectionPlane(g, out int ax, out float plane);
                // (the pivot: toward the most pushed member - the fold on the crushed side; the key stays the centre's, so the
                //  peak does not wander as the members wear)
                var centre = st.SectionCentre(g);
                var pivot = cm >= 0 ? centre + (st.PiecePushedEdge(cm) - centre) * Math.Clamp(HingeToCompression, 0f, 1f) : centre;
                order.Add((centre, ax, plane, ug, st.PieceBlock(wm), pivot));
            }
        }
        else
        {
            // each worn block's worst contact (the joint it departs at)
            var best = new Dictionary<int, int>();
            for (int k = 0; k < st.ContactCount; k++)
            {
                float u = st.ContactUtil[k]; if (!(u > 1f)) continue;
                int b = st.Outboard(k); if (!worn.Contains(b)) continue;
                if (!best.TryGetValue(b, out int o) || u > st.ContactUtil[o]) best[b] = k;
            }
            foreach (int k in best.Values) { st.ContactPlane(k, out int ax, out float plane); order.Add((st.ContactPoint(k), ax, plane, st.ContactUtil[k], st.Outboard(k), st.ContactPoint(k))); }
        }
        if (order.Count == 0) return 0;
        order.Sort((p, q) => q.u.CompareTo(p.u));
        int[] adjStart = null, adjOther = null;
        // ONE HINGE PER BRANCH: a wing yields over several joints near its root (its utilisation falls slowly along the span);
        // bending each would set a small-block wing (a joint every 0.5 m) several times more than a large-block one (one
        // every 2.5 m). The worst joint of a branch bends; a joint inside its far side - or holding it in its own - is nested:
        // its peak taken, not bent (it bends later only if it becomes its branch's worst past that peak).
        HashSet<int> chosenFar = null; List<int> chosenOb = null;
        foreach (var (J, ax, plane, u, ob, H) in order)
        {
            long key = Key(J, ax);
            float peak = peaks != null && peaks.TryGetValue(key, out float pk) ? pk : 1f;
            if (u < peak + MinStep) continue;
            // (past the snap the joint breaks, it does not bend: no bend planned at all - a crash's 23x utilisation set the
            //  far side 63 degrees, 2026-10-15; and every bend's own angle capped at MaxStepRad)
            if (u >= StructureFatigue.SnapUtil) continue;
            bool nested = chosenFar != null && chosenFar.Contains(ob);
            if (!nested && outBends.Count >= MaxJoints) continue;   // (left for the next evaluation: its peak not taken)
            if (peaks != null) peaks[key] = u;   // (this peak taken, bent or not: a too-big far side is not looked at again for it)
            if (nested) { Nested++; continue; }
            int side = ShipStructure.C(st.Center[ob], ax) >= plane ? 1 : -1;
            if (adjStart == null) Adjacency(st, out adjStart, out adjOther);
            var b = new Bend { Joint = H, Axis = ax, Plane = plane, Side = side, Util = u };
            if (!FarSide(st, ob, ax, plane, side, adjStart, adjOther, MaxBlocks, b.Blocks)) { TooBig++; continue; }
            if (chosenOb != null) { bool holds = false; foreach (int o in chosenOb) if (b.Blocks.Contains(o)) { holds = true; break; } if (holds) { Nested++; continue; } }
            // the far side's load's moment at the joint - the cut's centre (net loads: its inertia counted); the turn about the pivot
            Vector3 M = Vector3.Zero;
            foreach (int i in b.Blocks) M += st.NetMoment(i) + Vector3.Cross(st.Center[i] - J, st.NetForce(i));
            float m = M.Length();
            if (!(m > 1e-3f)) { NoMoment++; continue; }
            b.Rot = M * (MathF.Min(PerUtil * (u - peak), MaxStepRad) / m);
            outBends.Add(b); Planned++;
            (chosenOb ??= new List<int>()).Add(ob);
            chosenFar ??= new HashSet<int>(); foreach (int i in b.Blocks) chosenFar.Add(i);
        }
        return outBends.Count;
    }

    /// <summary>The blocks reachable from ob through contacts without crossing the plane (centres on the far side); false
    /// when more than max.</summary>
    public static bool FarSide(ShipStructure st, int ob, int ax, float plane, int side, int[] adjStart, int[] adjOther, int max, List<int> outBlocks)
    {
        outBlocks.Clear();
        var seen = new HashSet<int> { ob };
        var q = new Queue<int>(); q.Enqueue(ob);
        while (q.Count > 0)
        {
            int i = q.Dequeue();
            outBlocks.Add(i);
            if (outBlocks.Count > max) return false;
            for (int e = adjStart[i]; e < adjStart[i + 1]; e++)
            {
                int j = adjOther[e];
                if ((ShipStructure.C(st.Center[j], ax) - plane) * side <= 0f || !seen.Add(j)) continue;
                q.Enqueue(j);
            }
        }
        return true;
    }

    /// <summary>Each block's neighbours through contacts (CSR).</summary>
    public static void Adjacency(ShipStructure st, out int[] start, out int[] other)
    {
        int n = st.Count, nc = st.ContactCount;
        start = new int[n + 1];
        for (int k = 0; k < nc; k++) { st.ContactBlocks(k, out int a, out int b); start[a + 1]++; start[b + 1]++; }
        for (int i = 0; i < n; i++) start[i + 1] += start[i];
        other = new int[start[n]]; var fill = new int[n];
        for (int k = 0; k < nc; k++) { st.ContactBlocks(k, out int a, out int b); other[start[a] + fill[a]++] = b; other[start[b] + fill[b]++] = a; }
    }

    /// <summary>How far a point (grid-local m) moves in a bend: Rot x (p - joint), taken up over Ramp from the joint's
    /// plane (nothing on it or behind it).</summary>
    public static Vector3 Displacement(Bend b, Vector3 p)
    {
        float h = (ShipStructure.C(p, b.Axis) - b.Plane) * b.Side;
        if (!(h > 0f)) return Vector3.Zero;
        float s = Ramp > 1e-4f ? MathF.Min(1f, h / Ramp) : 1f;
        return Vector3.Cross(b.Rot, p - b.Joint) * s;
    }

    /// <summary>A point's running total with a new displacement, its length capped at Max.</summary>
    public static Vector3 Capped(Vector3 total, Vector3 add)
    {
        var t = total + add; float l = t.Length();
        return l > Max && l > 0f ? t * (Max / l) : t;
    }
}
