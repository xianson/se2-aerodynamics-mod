#pragma warning disable
using AeroMod;

/// <summary>
/// EACH COMPONENT'S AIR (AeroComponentRegistry.ResultForce / ResultPoint / ResultMoment, 2026-10-17 - the structure puts it
/// on the component's own block, StructureEval.ComponentLoadsAtBlocks): per component, its force with the hull pressure on
/// its own faces taken off, and its moment about its point - their sum is EvaluateAll's total, force and torque. On a
/// synthetic hull (a box of 0.25 m cells) with control surfaces in its cells, deflected, the air from random directions.
/// </summary>
static partial class Program
{
    static void RegistryTests(Random r)
    {
        var grid = Grid((0, 0, 0, 11, 3, 15));
        var surf = new SmoothSurfaceProvider { CellSize = 0.25f, CellOffset = 0f };
        surf.BeginBuild(grid, 0.5f); while (!surf.AddCellBatch(int.MaxValue)) { } surf.FinalizeBuild();
        var reg = new AeroComponentRegistry();
        // surfaces in the hull's corner and edge cells (their faces are the hull's: the subtraction has faces to take off)
        var cells = new[] { new Vector3I(0, 0, 0), new Vector3I(11, 3, 15), new Vector3I(5, 3, 0), new Vector3I(0, 1, 7), new Vector3I(11, 0, 9) };
        var cs = new List<ControlSurface>();
        foreach (var c in cells)
        {
            var s = new ControlSurface(new Vector3(c.X, c.Y, c.Z) * 0.25f, c, new Vector3(0, 0, 1), new Vector3(1, 0, 0), 0.0625f) { MaxDeflection = 20f };
            cs.Add(s); reg.Add(s);
        }
        double worstF = 0, worstT = 0; int withFaces = 0;
        for (int trial = 0; trial < 200; trial++)
        {
            foreach (var s in cs) s.DeflectionInput = (float)(r.NextDouble() * 2 - 1);
            var dir = Vector3.Normalize(new Vector3((float)r.NextDouble() - 0.5f, (float)r.NextDouble() - 0.5f, (float)r.NextDouble() - 0.5f));
            var com = new Vector3(1.5f, 0.5f, 2f) + dir * 0.3f;
            var ctx = new AeroContext(grid, surf, dir * (40f + 200f * (float)r.NextDouble()), Sea, com, 0.5f, new Vector3(0.1f, -0.2f, 0.05f));
            var (F, T) = reg.EvaluateAll(ctx);
            Check(reg.ResultCount == cs.Count, $"registry #{trial}: a result per component ({reg.ResultCount} of {cs.Count})");
            Vector3 sf = Vector3.Zero, st = Vector3.Zero, own = Vector3.Zero;
            for (int i = 0; i < reg.ResultCount; i++)
            {
                sf += reg.ResultForce[i]; st += Vector3.Cross(reg.ResultPoint[i] - com, reg.ResultForce[i]) + reg.ResultMoment[i];
                Check(reg.ResultAt[i] == cs[i].Position, $"registry #{trial}: component {i}'s block point is its position");
            }
            foreach (var s in cs) own += s.Compute(LocalAeroConditions.FromContext(ctx, s.Position)).Force;
            if ((own - F).Length() > 1e-3f) withFaces++;
            worstF = Math.Max(worstF, (sf - F).Length() / Math.Max(F.Length(), 1f));
            worstT = Math.Max(worstT, (st - T).Length() / Math.Max(T.Length(), 1f));
        }
        Console.WriteLine($"registry: per-component air vs EvaluateAll's total - worst relative difference force {worstF:E1}, torque {worstT:E1}; {withFaces} of 200 with hull faces taken off");
        Check(worstF < 1e-5 && worstT < 1e-5, $"registry: the components' results add up to the total (force {worstF:E1}, torque {worstT:E1})");
        Check(withFaces > 50, $"registry: the faces' subtraction was exercised ({withFaces} of 200)");
        // too slow: no results
        reg.EvaluateAll(new AeroContext(grid, surf, Vector3.Zero, Sea, Vector3.Zero, 0.5f));
        Check(reg.ResultCount == 0, "registry: at rest, no results");
    }
}
