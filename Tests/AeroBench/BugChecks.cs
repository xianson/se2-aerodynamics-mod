using System.Reflection;
using AeroMod;

// AeroBench gate: regressions for bugs fixed 2026-10-15 (the reorg audit, section 5 - ids A3...), each failing before its fix.
static partial class Program
{
    static int BugChecks(AtmosphereState atmo)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; Console.WriteLine($"   {(ok ? "ok  " : "FAIL")} {what}"); }

        // A3: MergeCoplanarWings kept its span lists in step with the wings (it copied the last wing's span into the
        // removed slot while RemoveAt shifted the wings): four coplanar panels, the first two close, the last two 15 m apart.
        {
            static LiftingSurface Panel(int x0)
            {
                var cells = new List<Vector3I>();
                for (int x = x0; x < x0 + 10; x++) for (int z = 0; z < 4; z++) cells.Add(new Vector3I(x, 0, z));
                var c = new Vector3((x0 + 4.5f) * 0.25f, 0f, 0.375f);
                return new LiftingSurface(Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ, 2.5f, 2.5f, 0.1f, 0f, c, c, 40, cells);
            }
            var det = new ConnectedComponentWingDetector();
            var merge = typeof(ConnectedComponentWingDetector).GetMethod("MergeCoplanarWings", BindingFlags.NonPublic | BindingFlags.Instance);
            var wings = (List<LiftingSurface>)merge.Invoke(det, new object[] { new List<LiftingSurface> { Panel(0), Panel(12), Panel(40), Panel(100) }, 0.5f });
            Check(wings.Count == 3 && MathF.Abs(wings[0].Span - 5.5f) < 1e-3f,
                $"A3 coplanar merge: 4 panels -> {wings.Count} wings (3: the close pair merged, 5.5 m span: {(wings.Count > 0 ? wings[0].Span : 0f):F2})");
        }
        return fails;
    }
}
