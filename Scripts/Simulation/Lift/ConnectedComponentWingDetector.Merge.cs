#pragma warning disable
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
                        if (a.PlanformArea > 100f && b.PlanformArea > 100f)
                            Log.Default?.Info($"[AERO-MERGE] W{i}+W{j} REJECT gap={gap:F2}>{gapLimit:F2} aS=[{aMinS:F1},{aMaxS:F1}] bS=[{bMinS2:F1},{bMaxS2:F1}] spanAx=({a.SpanAxis.X:F3},{a.SpanAxis.Y:F3},{a.SpanAxis.Z:F3})");
                        continue;
                    }

                    float normalSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.Normal));
                    if (normalSep > CellSize * 6f)
                    {
                        if (a.PlanformArea > 100f && b.PlanformArea > 100f)
                            Log.Default?.Info($"[AERO-MERGE] W{i}+W{j} REJECT normalSep={normalSep:F2}>{CellSize * 6f:F2}");
                        continue;
                    }

                    float chordSep = MathF.Abs(Vector3.Dot(a.Centroid - b.Centroid, a.ChordAxis));
                    float maxChord = MathF.Max(a.MeanChord, b.MeanChord);
                    if (chordSep > maxChord * 2f)
                    {
                        if (a.PlanformArea > 100f && b.PlanformArea > 100f)
                            Log.Default?.Info($"[AERO-MERGE] W{i}+W{j} REJECT chordSep={chordSep:F2}>{maxChord * 2f:F2}");
                        continue;
                    }

                    Log.Default?.Info(
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
                    var aeroCenter = (a.AeroCenter * a.PlanformArea + b.AeroCenter * b.PlanformArea) / totalArea;
                    float tc = (a.ThicknessRatio * a.PlanformArea + b.ThicknessRatio * b.PlanformArea) / totalArea;
                    float sweep = (a.SweepAngle * a.PlanformArea + b.SweepAngle * b.PlanformArea) / totalArea;

                    var merged = new LiftingSurface(
                        a.Normal, a.SpanAxis, a.ChordAxis,
                        totalArea, fullSpan, tc, sweep,
                        centroid, aeroCenter, a.FaceCount + b.FaceCount,
                        allCells);

                    wings[i] = merged;
                    // Update span extents for merged wing
                    spanMin[i] = MathF.Min(aMinS, bMinS2);
                    spanMax[i] = MathF.Max(aMaxS, bMaxS2);

                    wings.RemoveAt(j);
                    // Shift span arrays: move last element into j's slot
                    int last = wings.Count; // after RemoveAt, Count is one less
                    if (j < last)
                    {
                        spanMin[j] = spanMin[last];
                        spanMax[j] = spanMax[last];
                    }
                    changed = true;
                }
            }
        }

        return wings;
    }
}
