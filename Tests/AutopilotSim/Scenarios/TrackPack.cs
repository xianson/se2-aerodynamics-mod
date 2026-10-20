using AeroMod;

/// <summary>
/// THE OBSERVED-MOTION TRACK (ApMotionTrack) against scripted moving entities (SimTarget): only their positions fed, every tick
/// or every 6th, exact or noisy - the velocity, acceleration, turn rate, energy height and specific power it estimates against
/// the script's truth; its confidence rising as it watches and falling in a gap; a jump starting it again.
/// </summary>
sealed class TrackPack : IScenarioPack
{
    public void Register(ScenarioRegistry r)
    {
        r.Add("track: straight / turning / weaving / accelerating entity from positions only - velocity, acceleration, turn rate", true, Accuracy);
        r.Add("track: sparse (every 6th tick) and noisy (0.5 m) positions - still within bounds", true, SparseNoisy);
        r.Add("track: energy height and specific power vs truth (a climbing, accelerating entity)", true, Energy);
        r.Add("track: confidence and age - rises while watched, falls in a gap, lost; a jump restarts", true, Confidence);
    }

    static readonly Vector3 G = new(0f, -9.81f, 0f);

    /// <summary>Fly the script for 'seconds', the track fed every 'every' ticks (noise sigma m), the errors measured after 'settle' s:
    /// the worst velocity error (m/s), acceleration error (m/s2), turn-rate error (rad/s).</summary>
    static (float ev, float ea, float ew, ApMotionTrack tr) Fly(SimTarget t, float seconds, int every = 1, float noise = 0f, float settle = 2f, int seed = 3)
    {
        var tr = new ApMotionTrack(); var rng = new Random(seed);
        float ev = 0f, ea = 0f, ew = 0f; const float dt = 1f / 60f;
        int steps = (int)(seconds * 60f);
        for (int k = 0; k < steps; k++)
        {
            float time = k * dt;
            t.Step(time, dt);
            bool have = k % every == 0;
            Vector3D z = t.P + (noise > 0f ? (Vector3D)new Vector3(N(rng), N(rng), N(rng)) * noise : Vector3D.Zero);
            tr.Step(have, z, dt, G);
            if (time < settle) continue;
            ev = MathF.Max(ev, (tr.Vel - t.V).Length());
            ea = MathF.Max(ea, (tr.Acc - t.A).Length());
            float v2 = t.V.LengthSquared();
            Vector3 wTrue = v2 > 1f ? Vector3.Cross(t.V, t.A) / v2 : Vector3.Zero;
            ew = MathF.Max(ew, (tr.TurnRate - wTrue).Length());
        }
        return (ev, ea, ew, tr);
        static float N(Random r) { double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble(); return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)); }
    }

    static void Accuracy()
    {
        var p0 = new Vector3D(100, 500, -200);
        var cases = new (string name, SimTarget t, float ev, float ea, float ew)[]
        {
            ("straight 120 m/s", SimTarget.Straight(p0, new Vector3(120f, 0f, 30f)), 0.01f, 0.01f, 0.001f),
            ("turn 0.15 rad/s at 100 m/s", SimTarget.Turn(p0, new Vector3(100f, 0f, 0f), 0.15f), 0.3f, 1.0f, 0.01f),
            ("weave 15 m/s2 / 6 s", SimTarget.Weave(p0, new Vector3(80f, 0f, 0f), 15f, 6f), 0.8f, 5.0f, 0.07f),
            ("accelerate 8 m/s2 (the step itself in)", SimTarget.Accelerate(p0, new Vector3(50f, 0f, 0f), 8f, 3f, 5f), 1.0f, 8.5f, 0.001f),
        };
        foreach (var (name, t, evMax, eaMax, ewMax) in cases)
        {
            var (ev, ea, ew, tr) = Fly(t, 12f);
            Sim.Note($"track {name}: worst after 2 s - velocity {ev:F3} m/s, acceleration {ea:F2} m/s2, turn rate {ew:F4} rad/s; confidence {tr.Confidence:F2}");
            Sim.Check(ev <= evMax && ea <= eaMax && ew <= ewMax, $"track {name}: velocity {ev:F3} (<= {evMax}), acceleration {ea:F2} (<= {eaMax}), turn rate {ew:F4} (<= {ewMax})");
        }
        // (the accelerating case's acceleration step: the worst is at the step itself - settled 1 s after it, within 1 m/s2)
        var acc = SimTarget.Accelerate(p0, new Vector3(50f, 0f, 0f), 8f, 3f, 5f);
        var trA = new ApMotionTrack(); float worst = 0f;
        for (int k = 0; k < 600; k++) { float time = k / 60f; acc.Step(time, 1f / 60f); trA.Step(true, acc.P, 1f / 60f, G); if (time > 4.2f && time < 7.9f) worst = MathF.Max(worst, (trA.Acc - acc.A).Length()); }
        Sim.Check(worst < 1f, $"track accelerate: 1.2 s after the step the acceleration is within 1 m/s2 ({worst:F2})");
    }

    static (float ev, float ea) FlyM(SimTarget t, float mem, float noise)
    {
        var tr = new ApMotionTrack { MemorySeconds = mem }; var rng = new Random(3); float ev = 0f, ea = 0f;
        for (int k = 0; k < 720; k++) { float time = k / 60f; t.Step(time, 1f / 60f); tr.Step(true, t.P + (noise > 0f ? (Vector3D)new Vector3(Nn(rng), Nn(rng), Nn(rng)) * noise : Vector3D.Zero), 1f / 60f, G); if (time > 3f) { ev = MathF.Max(ev, (tr.Vel - t.V).Length()); ea = MathF.Max(ea, (tr.Acc - t.A).Length()); } }
        return (ev, ea);
    }
    static float Nn(Random r) { double u1 = 1.0 - r.NextDouble(), u2 = r.NextDouble(); return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2)); }

    static void SparseNoisy()
    {
        var p0 = new Vector3D(0, 800, 0);
        var (ev, ea, ew, _) = Fly(SimTarget.Turn(p0, new Vector3(100f, 0f, 0f), 0.15f), 12f, every: 6, settle: 3f);
        Sim.Note($"track sparse (10 Hz) turn: velocity {ev:F2} m/s, acceleration {ea:F2} m/s2, turn rate {ew:F4} rad/s");
        Sim.Check(ev < 1.5f && ea < 2.5f && ew < 0.025f, $"track sparse (10 Hz) turn: velocity {ev:F2} (< 1.5), acceleration {ea:F2} (< 2.5), turn {ew:F4} (< 0.025)");
        // (noise is the memory's trade: the default 0.1 s for the game's exact positions; a noisy sensor lengthens it)
        var (n1, a1) = FlyM(SimTarget.Straight(p0, new Vector3(150f, 0f, 0f)), 0.35f, 0.5f);
        var (n2, a2) = FlyM(SimTarget.Straight(p0, new Vector3(150f, 0f, 0f)), 1.2f, 0.5f);
        var (n0, a0) = FlyM(SimTarget.Straight(p0, new Vector3(150f, 0f, 0f)), 0.1f, 0.5f);
        Sim.Note($"track noisy (0.5 m) straight: velocity / acceleration error {n0:F1} / {a0:F1} at the default 0.1 s memory, {n1:F2} / {a1:F2} at 0.35 s, {n2:F2} / {a2:F2} at 1.2 s");
        Sim.Check(n1 < 2.5f && a1 < 3f && n2 < n1 && n1 < n0, $"track noisy (0.5 m): a 0.35 s memory holds the velocity within {n1:F2} m/s (< 2.5) and the acceleration {a1:F2} m/s2 (< 3); longer is quieter ({n2:F2} < {n1:F2} < {n0:F2})");
    }

    static void Energy()
    {
        // climbing at 20 m/s while speeding up along a 100 m/s path
        var t = new SimTarget { Name = "climb", P = new Vector3D(0, 1000, 0), V = new Vector3(100f, 20f, 0f), Script = (time, s) => Vector3.Normalize(s.V) * 3f };
        var tr = new ApMotionTrack(); var refP = new Vector3D(0, 1000, 0);
        float eh = 0f, ps = 0f, ehT = 0f, psT = 0f;
        for (int k = 0; k < 600; k++)
        {
            t.Step(k / 60f, 1f / 60f); tr.Step(true, t.P, 1f / 60f, G);
            eh = tr.EnergyHeight(refP); ps = tr.SpecificPower;
            ehT = (float)(t.P.Y - refP.Y) + t.V.LengthSquared() / (2f * 9.81f);
            psT = Vector3.Dot(t.V, t.A - G) / 9.81f;
        }
        Sim.Note($"track energy: energy height {eh:F1} m (truth {ehT:F1}), specific power {ps:F2} m/s (truth {psT:F2}), load {tr.LoadFactor:F2} g");
        Sim.Check(MathF.Abs(eh - ehT) < 1f && MathF.Abs(ps - psT) < 0.3f, $"track energy: energy height {eh:F1} vs {ehT:F1} m, specific power {ps:F2} vs {psT:F2} m/s");
    }

    static void Confidence()
    {
        var t = SimTarget.Straight(new Vector3D(0, 500, 0), new Vector3(60f, 0f, 0f));
        var tr = new ApMotionTrack();
        float c05 = 0f, c2 = 0f, cGap = 0f; bool lost = false;
        for (int k = 0; k < 60 * 8; k++)
        {
            float time = k / 60f; t.Step(time, 1f / 60f);
            bool have = time < 3f || time > 6.5f;
            tr.Step(have, t.P, 1f / 60f, G);
            if (MathF.Abs(time - 0.25f) < 0.01f) c05 = tr.Confidence;
            if (MathF.Abs(time - 2.5f) < 0.01f) c2 = tr.Confidence;
            if (MathF.Abs(time - 4.5f) < 0.01f) cGap = tr.Confidence;
            if (time > 6.1f && time < 6.4f && !tr.Valid) lost = true;
        }
        Sim.Note($"track confidence: {c05:F2} at 0.25 s, {c2:F2} at 2.5 s, {cGap:F2} 1.5 s into a gap; lost after {tr.LoseSeconds} s: {lost}; again valid after it: {tr.Valid}");
        Sim.Check(c05 < c2 && c2 > 0.95f && cGap < c2 && cGap > 0f && lost && tr.Valid, $"track confidence: rises ({c05:F2} -> {c2:F2}), falls in the gap ({cGap:F2}), lost past {tr.LoseSeconds} s ({lost}), seen again ({tr.Valid})");
        // a coasted gap: the estimate carried along (1 s gap, straight - within a metre)
        var t2 = SimTarget.Straight(new Vector3D(0, 500, 0), new Vector3(60f, 0f, 10f)); var tr2 = new ApMotionTrack(); float coastErr = 0f;
        for (int k = 0; k < 300; k++) { float time = k / 60f; t2.Step(time, 1f / 60f); tr2.Step(time < 3f || time > 4f, t2.P, 1f / 60f, G); if (time > 3.9f && time < 4f) coastErr = (float)(tr2.Pos - t2.P).Length(); }
        Sim.Check(coastErr < 1f && tr2.Age == 0f, $"track coast: 0.9 s into a gap the position is carried within {coastErr:F2} m (< 1)");
        // a jump (another entity behind the same id): the track starts again rather than averaging the two
        var tr3 = new ApMotionTrack(); var p = new Vector3D(0, 0, 0);
        for (int k = 0; k < 120; k++) { p += new Vector3D(1, 0, 0); tr3.Step(true, p, 1f / 60f, G); }
        tr3.Step(true, p + new Vector3D(5000, 0, 0), 1f / 60f, G);
        Sim.Check(tr3.Samples == 0 && tr3.Vel == Vector3.Zero, $"track jump: a 5 km jump restarts the track ({tr3.Samples} samples, velocity {tr3.Vel.Length():F1})");
    }
}
