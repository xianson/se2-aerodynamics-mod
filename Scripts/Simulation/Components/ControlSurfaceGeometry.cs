#pragma warning disable
using System;
using System.Collections.Generic;

namespace AeroMod;

/// <summary>
/// What a control surface sits in, read straight off the grid's cells - no wing detection (the user, 2026-10-13: "the
/// control surface needs to work independently of wing detection"). Per chord edge (the air decides which one leads):
///  - HOST CHORD: how deep the plate-like structure directly ahead of that edge runs, in the surface's own plane - the
///    wing, tailplane, fin, canard or strake it is the trailing edge of. A deflection then turns the whole section (host +
///    surface), as a flap does: thin-airfoil flap effectiveness from its share of the chord. A thick body ahead (a fuselage)
///    is not a host.
///  - SECTION SPAN: the run of control surfaces side by side on one hinge line (a row acts as one surface), plus how far
///    the host continues past the row's ends (an aileron on a long wing has the wing's span, not its own).
///  - END PLATES: a wall across the span at an end of the section (fuselage side, fin root) mirrors it - the effective
///    aspect ratio doubles per plate, as at a wing root.
/// Measured once per grid rebuild (Measure), on 0.25 m cells; the surfaces' own cells are never structure (the accessor
/// leaves self-modelled blocks out).
/// </summary>
public sealed class SurfaceSurroundings
{
    /// <summary>Per chord edge: [0] = the edge on +ChordDirection, [1] = the edge on -ChordDirection (m, m, plates).</summary>
    public readonly float[] HostChord = new float[2], SectionSpan = new float[2];
    public readonly int[] EndPlates = new int[2];
    public string Describe(int e) => $"host chord {HostChord[e]:F2} m, section span {SectionSpan[e]:F2} m, end plates {EndPlates[e]}";
}

public static class ControlSurfaceGeometry
{
    public const float Cell = 0.25f;
    /// <summary>How deep a host is looked for, in surface chords (and never past 40 m).</summary>
    public const float MaxHostChords = 4f;

    /// <summary>Every control surface's surroundings, from the grid's occupied cells (surfaces themselves excluded).</summary>
    public static void Measure(IReadOnlyList<IAeroBlockComponent> components, IGridAccessor grid)
    {
        var surfaces = new List<ControlSurface>();
        foreach (var c in components) if (c is ControlSurface cs && cs.Extents != Vector3.Zero) surfaces.Add(cs);
        if (surfaces.Count == 0) return;
        // rows: same hinge axis and plane, same chord line, touching end to end along the span
        var row = new int[surfaces.Count];
        for (int i = 0; i < row.Length; i++) row[i] = i;
        int Find(int i) { while (row[i] != i) i = row[i] = row[row[i]]; return i; }
        for (int i = 0; i < surfaces.Count; i++)
            for (int j = i + 1; j < surfaces.Count; j++)
            {
                ControlSurface a = surfaces[i], b = surfaces[j];
                if (MathF.Abs(Vector3.Dot(a.HingeAxis, b.HingeAxis)) < 0.99f || MathF.Abs(Vector3.Dot(a.ChordDirection, b.ChordDirection)) < 0.99f) continue;
                Vector3 d = b.Position - a.Position, n = Vector3.Cross(a.HingeAxis, a.ChordDirection);
                if (MathF.Abs(Vector3.Dot(d, n)) > 0.1f || MathF.Abs(Vector3.Dot(d, a.ChordDirection)) > 0.1f) continue;
                float gap = MathF.Abs(Vector3.Dot(d, a.HingeAxis)) - 0.5f * (Span(a) + Span(b));
                if (MathF.Abs(gap) < 0.1f) row[Find(i)] = Find(j);
            }
        for (int i = 0; i < surfaces.Count; i++)
        {
            var cs = surfaces[i];
            // the row's ends along this surface's hinge axis (m, from its centre)
            float lo = -0.5f * Span(cs), hi = 0.5f * Span(cs);
            for (int j = 0; j < surfaces.Count; j++)
            {
                if (Find(j) != Find(i)) continue;
                float o = Vector3.Dot(surfaces[j].Position - cs.Position, cs.HingeAxis), h = 0.5f * Span(surfaces[j]);
                lo = MathF.Min(lo, o - h); hi = MathF.Max(hi, o + h);
            }
            var g = new SurfaceSurroundings();
            for (int e = 0; e < 2; e++) MeasureEdge(cs, grid, e == 0 ? 1 : -1, lo, hi, g, e);
            cs.Surroundings = g;
        }
    }

    static float Span(ControlSurface cs) => MathF.Abs(Vector3.Dot(cs.Extents, cs.HingeAxis));
    static float ChordLen(ControlSurface cs) => MathF.Abs(Vector3.Dot(cs.Extents, cs.ChordDirection));
    static float Thick(ControlSurface cs) => MathF.Abs(Vector3.Dot(cs.Extents, Vector3.Cross(cs.HingeAxis, cs.ChordDirection)));

    static Vector3I ToCell(Vector3 p) => new Vector3I((int)MathF.Round(p.X / Cell), (int)MathF.Round(p.Y / Cell), (int)MathF.Round(p.Z / Cell));

    /// <summary>One chord edge (side +-1 along ChordDirection): the host ahead of it, the section's span and its plates.</summary>
    static void MeasureEdge(ControlSurface cs, IGridAccessor grid, int side, float rowLo, float rowHi, SurfaceSurroundings g, int e)
    {
        Vector3 H = cs.HingeAxis, C = cs.ChordDirection * side, N = Vector3.Cross(cs.HingeAxis, cs.ChordDirection);
        float span = Span(cs), chord = ChordLen(cs), thick = Thick(cs);
        int maxDepth = (int)MathF.Min(MaxHostChords * chord / Cell, 40f / Cell);
        int maxThick = (int)MathF.Ceiling(MathF.Max(2f * thick, 0.5f * chord) / Cell);
        // the first cell centre past the edge, on the surface's mid-plane
        Vector3 edge = cs.Position + C * (0.5f * chord + 0.5f * Cell);
        bool Occ(Vector3 p) => grid.IsCellOccupied(ToCell(p));
        // plate-like at p: the structure's thickness through p (along N) stays within maxThick cells
        bool Plate(Vector3 p)
        {
            int t = 1;
            for (int k = 1; k <= maxThick && Occ(p + N * (k * Cell)); k++) t++;
            for (int k = 1; k <= maxThick && Occ(p - N * (k * Cell)); k++) t++;
            return t <= maxThick;
        }

        // 1. host chord: mean depth over the span columns (every other cell), a column ending where the structure does,
        //    or where it stops being a plate (checked every 4 cells: a fuselage ahead is no host)
        int columns = 0; float depthSum = 0f;
        int nCols = Math.Max(1, (int)MathF.Round(span / Cell));
        for (int u = 0; u < nCols; u += 2)
        {
            Vector3 col = edge + H * ((u + 0.5f) * Cell - 0.5f * span);
            int depth = 0;
            for (int k = 0; k < maxDepth; k++)
            {
                Vector3 p = col + C * (k * Cell);
                if (!Occ(p)) break;
                if (k % 4 == 0 && !Plate(p)) break;
                depth++;
            }
            depthSum += depth; columns++;
        }
        float host = columns > 0 ? depthSum / columns * Cell : 0f;
        g.HostChord[e] = host;

        // 2. section span: the row, plus the host past its ends (walked along the host's first cells ahead of the hinge
        //    line), capped at 60 m each way
        float extLo = 0f, extHi = 0f;
        if (host > 0.5f * Cell)
        {
            int cap = (int)(60f / Cell);
            for (int k = 1; k <= cap; k++) { if (!Occ(edge + H * (rowHi + (k - 0.5f) * Cell))) break; extHi = k * Cell; }
            for (int k = 1; k <= cap; k++) { if (!Occ(edge + H * (rowLo - (k - 0.5f) * Cell))) break; extLo = k * Cell; }
        }
        g.SectionSpan[e] = (rowHi - rowLo) + extLo + extHi;

        // 3. end plates: just past each end of the section, at mid-chord of the section, a wall across N (at least half of
        //    the cells within half a section chord either side occupied)
        float sectionChord = chord + host;
        Vector3 mid = cs.Position + C * (0.5f * chord - 0.5f * sectionChord);   // (the section's mid-chord, on the mid-plane)
        int half = Math.Max(1, (int)MathF.Round(0.5f * sectionChord / Cell));
        int plates = 0;
        foreach (float end in new[] { rowHi + extHi + 0.5f * Cell, rowLo - extLo - 0.5f * Cell })
        {
            Vector3 p = mid + H * end; int hits = 0;
            for (int k = -half; k <= half; k++) if (Occ(p + N * (k * Cell))) hits++;
            if (hits * 2 >= 2 * half + 1) plates++;
        }
        g.EndPlates[e] = plates;
    }

    /// <summary>Thin-airfoil flap effectiveness: the share of a section's angle-of-attack a deflection is worth, for a
    /// flap that is chordShare of the section's chord (1: an all-moving surface).</summary>
    public static float FlapEffectiveness(float chordShare)
    {
        if (chordShare >= 0.999f) return 1f;
        if (chordShare <= 0f) return 0f;
        float th = MathF.Acos(2f * chordShare - 1f);
        return 1f - (th - MathF.Sin(th)) / MathF.PI;
    }
}
