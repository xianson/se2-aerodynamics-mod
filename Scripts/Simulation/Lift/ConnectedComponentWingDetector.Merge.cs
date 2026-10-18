#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

// Coplanar wings merged into one (span extents, gaps).
public partial class ConnectedComponentWingDetector
{
    // ─── Merge coplanar wings ────────────────────────────────────

    private void ComputeSpanExtents(LiftingSurface wing, out float minS, out float maxS)
    {
        minS = float.MaxValue;
        maxS = float.MinValue;
        foreach (var c in wing.Cells)
        {
            float s = (c.X + CellOffset) * CellSize * wing.SpanAxis.X
                    + (c.Y + CellOffset) * CellSize * wing.SpanAxis.Y
                    + (c.Z + CellOffset) * CellSize * wing.SpanAxis.Z;
            minS = MathF.Min(minS, s);
            maxS = MathF.Max(maxS, s);
        }
    }

    private List<LiftingSurface> MergeCoplanarWings(List<LiftingSurface> wings, float blockSize)
    {
        float maxGapM = 12f * CellSize;

        // Pre-compute span extents for each wing
        var spanMin = new List<float>(wings.Count);
        var spanMax = new List<float>(wings.Count);
        for (int k0 = 0; k0 < wings.Count; k0++) { spanMin.Add(0f); spanMax.Add(0f); }
        for (int k = 0; k < wings.Count; k++)
        {
            ComputeSpanExtents(wings[k], out float sMin, out float sMax);
            spanMin[k] = sMin;
            spanMax[k] = sMax;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < wings.Count && !changed; i++)
            {
                for (int j = i + 1; j < wings.Count && !changed; j++)
                {
                    var a = wings[i];
                    var b = wings[j];

                    float nDot = Vector3.Dot(a.Normal, b.Normal);
                    if (nDot < 0.99f) continue;

                    // Project both wings onto a's span axis for consistent comparison
                    float aMinS = spanMin[i], aMaxS = spanMax[i];
                    // Re-project b onto a's span axis
                    float bMinS2 = float.MaxValue, bMaxS2 = float.MinValue;
                    foreach (var c in b.Cells)
                    {
                        float s = (c.X + CellOffset) * CellSize * a.SpanAxis.X
                                + (c.Y + CellOffset) * CellSize * a.SpanAxis.Y
                                + (c.Z + CellOffset) * CellSize * a.SpanAxis.Z;
                        bMinS2 = MathF.Min(bMinS2, s);
                        bMaxS2 = MathF.Max(bMaxS2, s);
                    }

                    float gap = MathF.Max(aMinS, bMinS2) - MathF.Min(aMaxS, bMaxS2);
                    float maxSpan = MathF.Max(aMaxS - aMinS, bMaxS2 - bMinS2);
                    float gapLimit = MathF.Max(maxGapM, maxSpan * 0.20f);
                    if (gap > gapLimit)
                    {
                        if (AeroLog.Detail && a.PlanformArea > 100f && b.PlanformArea > 100f)
                            Log.Default?.Info($"[AERO-MERGE] W{i}+W{j} REJECT gap={gap:F2}>{gapLimit:F2} aS=[{aMinS:F1},{aMaxS:F1}] bS=[{bMinS2:F1},{bMaxS2:F1}] spanAx=({a.SpanAxis.X:F3},{a.SpanAxis.Y:F3},{a.SpanAxis.Z:F3})");
                        continue;
                    }

                    float normalSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.Normal));
                    if (normalSep > CellSize * 6f)
                    {
                        if (AeroLog.Detail && a.PlanformArea > 100f && b.PlanformArea > 100f)
                            Log.Default?.Info($"[AERO-MERGE] W{i}+W{j} REJECT normalSep={normalSep:F2}>{CellSize * 6f:F2}");
                        continue;
                    }

                    float chordSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.ChordAxis));
                    float maxChord = MathF.Max(a.MeanChord, b.MeanChord);
                    if (chordSep > maxChord * 2f)
                    {
                        if (AeroLog.Detail && a.PlanformArea > 100f && b.PlanformArea > 100f)
                            Log.Default?.Info($"[AERO-MERGE] W{i}+W{j} REJECT chordSep={chordSep:F2}>{maxChord * 2f:F2}");
                        continue;
                    }

                    if (AeroLog.Detail) Log.Default?.Info(
                        $"[AERO-MERGE] W{i}+W{j} MERGING " +
                        $"nDot={nDot:F4} gap={gap:F2} gapLim={gapLimit:F2} " +
                        $"normSep={normalSep:F2} chordSep={chordSep:F2}/{maxChord * 2f:F2} " +
                        $"aS=[{aMinS:F1},{aMaxS:F1}] bS=[{bMinS2:F1},{bMaxS2:F1}] " +
                        $"aSpan=({a.SpanAxis.X:F3},{a.SpanAxis.Y:F3},{a.SpanAxis.Z:F3})");

                    var allCells = new List<Vector3I>(a.Cells.Count + b.Cells.Count);
                    allCells.AddRange(a.Cells);
                    allCells.AddRange(b.Cells);

                    float totalArea = a.PlanformArea + b.PlanformArea;
                    float fullSpan = MathF.Max(aMaxS, bMaxS2) - MathF.Min(aMinS, bMinS2) + CellSize;

                    var centroid = (a.Centroid * a.PlanformArea + b.Centroid * b.PlanformArea) / totalArea;
                    // (orientation-free, as each piece: the quarter chord's offset and both edges' sweeps, area-weighted -
                    //  the leading edge is the flow's, per tick; 2026-10-18, fix a-wing-edge)
                    // (a piece whose chord runs along a's span - one found the other way round - has its edges along another
                    //  axis: its mean sweep for both of a's edges, or a mirrored ship's wing differed - the Pelican's main
                    //  wing 10.0/18.0 deg, mirrored 10.4/17.6, not swapped; and no quarter-chord offset along a's chord, as
                    //  with the area-weighted aero centres before - whose offset went sideways, along its own chord)
                    bool bSame = MathF.Abs(Vector3.Dot(a.ChordAxis, b.ChordAxis)) > 0.5f;
                    float acOffset = (a.AcOffset * a.PlanformArea + (bSame ? b.AcOffset : 0f) * b.PlanformArea) / totalArea;
                    float tc = (a.ThicknessRatio * a.PlanformArea + b.ThicknessRatio * b.PlanformArea) / totalArea;
                    float bLo = bSame ? b.SweepLo : b.SweepAngle, bHi = bSame ? b.SweepHi : b.SweepAngle;
                    float sweepLo = (a.SweepLo * a.PlanformArea + bLo * b.PlanformArea) / totalArea;
                    float sweepHi = (a.SweepHi * a.PlanformArea + bHi * b.PlanformArea) / totalArea;

                    var merged = new LiftingSurface(
                        a.Normal, a.SpanAxis, a.ChordAxis,
                        totalArea, fullSpan, tc, sweepLo, sweepHi,
                        centroid, acOffset, a.FaceCount + b.FaceCount,
                        allCells);

                    wings[i] = merged;
                    // Update span extents for merged wing
                    spanMin[i] = MathF.Min(aMinS, bMinS2);
                    spanMax[i] = MathF.Max(aMaxS, bMaxS2);

                    // (the span lists shift with the wings - 2026-10-15: they took the LAST wing's span into j's slot while
                    //  RemoveAt shifted the wings down, so from four coplanar wings on, a wing was judged by another's span:
                    //  far-apart wings merged, the merged span wrong)
                    wings.RemoveAt(j); spanMin.RemoveAt(j); spanMax.RemoveAt(j);
                    changed = true;
                }
            }
        }

        return wings;
    }
}
