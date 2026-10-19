using AeroMod;

/// <summary>Height hold alone: the pilot flies the ship (their keys across the lift axis theirs), the autopilot only the up key.</summary>
sealed class PilotPack : IScenarioPack
{
    public void Register(ScenarioRegistry r) => r.Add("pilot: height hold alone - the pilot's forward key passes through", true, () =>
    {
        var run = new Run("height hold alone", Ships.Vtol());
        run.P.Piloted = true;
        run.Fly(0.5f);
        var s = run.P.SenseNow();
        Sim.Check(run.P.Fd.Engage(ApGoal.Height, in s, out var why), $"height hold alone: engaged ({why})");
        run.Fly(2f); double y0 = run.P.P.Y;
        run.P.PilotKeys = new Vector3(0f, 0f, -0.6f);
        float keyZ = 0f; run.Measure();
        run.Fly(6f, rr => keyZ = rr.P.Move.Z);
        Sim.Note($"height hold alone: the forward key 0.6 written {-keyZ:F2}, the ship at {run.P.HorizontalV.Length():F1} m/s, the height within {Math.Abs(run.P.P.Y - y0):F2} m");
        Sim.Check(MathF.Abs(keyZ + 0.6f) < 1e-3f && run.P.HorizontalV.Length() > 3f && Math.Abs(run.P.P.Y - y0) < 1.0, $"height hold alone: the forward key passes through ({keyZ:F2}), the ship moves ({run.P.HorizontalV.Length():F1} m/s), the height held");
        Program.LedgerPublic(run, false);
    });
}
